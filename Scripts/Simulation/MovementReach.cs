using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

/// <summary>Religious affinity, diplomatic recognition and an actual local following are different things.</summary>
public static class MovementReach
{
    private sealed class ReachCache
    {
        public ProvinceTopology Map;
        public int Revision = -1;
        public readonly Dictionary<int, (float Latitude, float Longitude)> Centres = new();
        /// <summary>Which region each province was in when Centres was worked out.</summary>
        public ushort[] RegionOfProvince = Array.Empty<ushort>();
    }

    private static readonly ConditionalWeakTable<Campaign, ReachCache> ReachCaches = new();
    [ThreadStatic] private static Dictionary<int, (double Latitude, double X, double Y, int Count)> _centreSums;

    private static (float Latitude, float Longitude) Centre(Campaign campaign, ScenarioState region)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return Movements.CentreOf(campaign, region);
        var cache = ReachCaches.GetOrCreateValue(campaign);
        // A region's centre is its provinces averaged, so it moves only when a province changes
        // region. The ownership revision also moves when a region changes country - several
        // times a week late in a campaign - and each time this walked all 34,000 provinces
        // with a sine and a cosine apiece, 3 ms, to arrive at the same centres. The province
        // list is compared first; the walk is for a map that has really been redrawn.
        if (ReferenceEquals(cache.Map, map) && cache.Revision != campaign.OwnershipRevision &&
            map.ProvinceToState.AsSpan().SequenceEqual(cache.RegionOfProvince))
            cache.Revision = campaign.OwnershipRevision;
        if (!ReferenceEquals(cache.Map, map) || cache.Revision != campaign.OwnershipRevision)
        {
            cache.Map = map; cache.Revision = campaign.OwnershipRevision; cache.Centres.Clear();
            cache.RegionOfProvince = (ushort[])map.ProvinceToState.Clone();
            var sums = _centreSums ??= new Dictionary<int, (double Latitude, double X, double Y, int Count)>();
            sums.Clear();
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                var id = map.ProvinceToState[province];
                var (latitude, longitude) = map.CenterOf(province);
                var old = sums.GetValueOrDefault(id);
                sums[id] = (old.Latitude + latitude, old.X + Math.Cos(longitude * Math.PI / 180),
                    old.Y + Math.Sin(longitude * Math.PI / 180), old.Count + 1);
            }
            foreach (var (id, sum) in sums)
                cache.Centres[id] = ((float)(sum.Latitude / sum.Count), (float)(Math.Atan2(sum.Y, sum.X) * 180 / Math.PI));
        }
        return cache.Centres.GetValueOrDefault(region.MapIndex);
    }

    public static bool CanOperate(Campaign campaign, Nation nation, ScenarioState region, MovementDefinition definition,
        Nation owner = null) => CanOperate(campaign, nation, region, definition, owner, null);

    /// <summary>
    /// The same question from a caller that asks it of many regions in one breath and changes
    /// no war while it does: <paramref name="atWarWith"/> is its own memory of which owners the
    /// movement is at war with, filled here as the owners come up. "At war" walks every live
    /// war and both sides of each - a microsecond late in a campaign - and the week's support
    /// drift asked it once per region where once per owner answers: 130 ms of mov:support over
    /// fourteen late days became 80.
    /// </summary>
    internal static bool CanOperate(Campaign campaign, Nation nation, ScenarioState region, MovementDefinition definition,
        Nation owner, Dictionary<int, bool> atWarWith)
    {
        if (campaign is null || nation?.Movement is not { } movement || region is null || definition is null) return false;
        owner ??= MovementRules.OwnerOf(campaign, region);
        // A movement that already governs ground can organise anywhere on its own soil;
        // checking its distance from a homeland it has already won is a question with no
        // possible answer but no.
        if (owner is not null && owner.Id == nation.Id) return true;
        if (MovementDefinition.Listed(definition.Homeland, region.Id) || region.MapIndex == movement.HeadquartersStateMapIndex)
            return true;

        var humanitarian = definition.Has("humanitarian");
        // Permission can open a distant region. A hostile host closes geographic access for relief too.
        var permitted = owner is not null && (movement.InvitedBy.Contains(owner.Id) ||
            movement.SponsoredBy.ContainsKey(owner.Id) || definition.Sponsors.ContainsKey(owner.Tag) ||
            (humanitarian && movement.RecognisedBy.Contains(owner.Id)));
        if (owner is not null && (permitted || humanitarian))
        {
            var hostile = movement.DesignatedBy.Contains(owner.Id);
            if (!hostile)
            {
                if (atWarWith is null) hostile = CampaignDiplomacy.AtWar(campaign, nation.Id, owner.Id);
                else if (!atWarWith.TryGetValue(owner.Id, out hostile))
                    atWarWith[owner.Id] = hostile = CampaignDiplomacy.AtWar(campaign, nation.Id, owner.Id);
            }
            if (hostile && humanitarian) return false;
            if (!hostile && permitted) return true;
        }

        var reach = definition.Has("diaspora_lobby") ? definition.DiasporaReachKm : definition.OutreachKm;
        var point = Centre(campaign, region);
        if (Distance(movement.HeadquartersLatitude, movement.HeadquartersLongitude, point.Latitude, point.Longitude) <= reach)
            return true;
        // The homeland's place on the map does not move when a region's people do.
        // Resolving each homeland id, for every region asked, was the walk.
        var homes = HomelandCentresOf(campaign, definition);
        for (var i = 0; i < homes.Count; i++)
        {
            var home = homes.Points[i];
            if (Distance(home.Latitude, home.Longitude, point.Latitude, point.Longitude) <= reach)
                return true;
        }
        return false;
    }

    sealed class HomelandCentres
    {
        public bool Built;
        public int Generation = -1;
        public int Count;
        public (float Latitude, float Longitude)[] Points = Array.Empty<(float, float)>();
    }

    // A changed trait can replace the runtime definition. Its old geography should not keep it alive.
    static readonly ConditionalWeakTable<Campaign, ConditionalWeakTable<MovementDefinition, HomelandCentres>> HomelandCentreCaches = new();

    static HomelandCentres HomelandCentresOf(Campaign campaign, MovementDefinition definition)
    {
        var cached = HomelandCentreCaches.GetOrCreateValue(campaign).GetOrCreateValue(definition);
        var generation = Movements.RegionListGeneration(campaign);
        if (cached.Built && cached.Generation == generation) return cached;
        var homeland = definition.Homeland;
        var count = homeland?.Count ?? 0;
        if (cached.Points.Length < count) cached.Points = new (float, float)[count];
        var written = 0;
        for (var i = 0; i < count; i++)
        {
            if (Movements.StateById(campaign, homeland[i]) is not { } home) continue;
            cached.Points[written++] = Centre(campaign, home);
        }
        cached.Count = written;
        cached.Built = true;
        // StateById may have indexed the list and moved the generation. Read it after.
        cached.Generation = Movements.RegionListGeneration(campaign);
        return cached;
    }

    public static float SupportCeiling(Campaign campaign, Nation nation, ScenarioState region,
        MovementDefinition definition, long own, long faithful, Nation owner = null) =>
        SupportCeiling(campaign, nation, region, definition, own, faithful, owner, null);

    /// <summary>The ceiling for a caller that keeps the at-war memory <see cref="CanOperate(Campaign, Nation, ScenarioState, MovementDefinition, Nation, Dictionary{int, bool})"/> describes.</summary>
    internal static float SupportCeiling(Campaign campaign, Nation nation, ScenarioState region,
        MovementDefinition definition, long own, long faithful, Nation owner, Dictionary<int, bool> atWarWith)
    {
        if (campaign is null || nation?.Movement is not { } movement || region is null || definition is null ||
            (own <= 0 && faithful <= 0)) return 0f;
        // A defeated regional insurgency is not a worldwide movement of its culture.
        // Diaspora offices may be hosted abroad, but do not create a territorial following there.
        // A campaign for one region's charter is a campaign in that region: the Silesian
        // autonomists had a following among the Poles of Grodno and Kaliningrad, and the
        // Texan nationalists would have had one in California.
        if ((movement.Id?.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal) == true ||
             definition.Has("civic_independence")) && !IsHomeland(campaign, definition, region)) return 0f;
        if (!CanOperate(campaign, nation, region, definition, owner, atWarWith)) return 0f;
        // The shared faith supplies a small sympathetic audience, not a territorial mandate.
        return own > 0 ? MovementRules.MaxSupport : .04f;
    }

    /// <summary>Permission to lobby, host an office or provide aid is never permission to found a state.</summary>
    public static bool CanClaimTerritory(Campaign c, Nation nation, ScenarioState region)
    {
        var definition = Movements.DefinitionOf(nation);
        if (c?.ProvinceMap is null || nation?.Movement is not { Dissolved: false } movement ||
            region is null || region.Unclaimed || region.Population <= 0 || definition is null ||
            !(definition.CanStageUprising || MovementMethods.WillRise(c, nation, definition))) return false;
        if ((definition.Has("humanitarian") || definition.Has("diaspora_lobby")) &&
            !definition.Has("territorial_ambition")) return false;
        // Return to a remembered homeland, including a partition of it. A refuge abroad
        // never becomes a new homeland merely because the government has an office there.
        if (movement.Exile)
            return TerritorialClaims.TitleOf(c, nation, region) == TerritorialTitle.Homeland;
        if (IsHomeland(c, definition, region)) return true;
        if (definition.Homeland.Count == 0 && !definition.Has("territorial_ambition")) return false;
        var own = MovementRules.PeopleOf(region, definition, out _);
        if (own / (double)region.Population < c.Defines.MovementTerritorialResidentShare) return false;
        // Expansion of a territorial movement requires an actual common frontier; neither
        // worldwide minority presence, sponsoring nor headquarters relocation is an anchor.
        return c.HasOwnedLand(nation) && TerritorialCoherence.TouchesOwnedGround(c, nation, region.MapIndex);
    }

    internal static bool IsHomeland(Campaign c, MovementDefinition definition, ScenarioState region)
    {
        if (MovementDefinition.Listed(definition.Homeland, region.Id)) return true;
        var origin = TerritorialClaims.Origin(region);
        foreach (var id in definition.Homeland)
            if (Movements.StateById(c, id) is { } home && TerritorialClaims.Origin(home) == origin) return true;
        return false;
    }

    /// <summary>The region card shows residents, not the share of a tiny minority that likes a movement.</summary>
    public static float ResidentSupport(Campaign campaign, Nation nation, ScenarioState region)
    {
        var definition = Movements.DefinitionOf(nation);
        if (definition is null || nation.Movement is null || region.Population <= 0) return 0f;
        var recordedSupport = nation.Movement.Support.GetValueOrDefault(region.MapIndex);
        if (recordedSupport <= 0f) return 0f;
        // A region card can open between slices of the simulation's people index.
        // Count only this region so the preview cannot restart its suspended build.
        var own = MovementRules.PeopleOf(region, definition, out var faithful);
        var following = MathF.Min(recordedSupport,
            SupportCeiling(campaign, nation, region, definition, own, faithful));
        return M.Clamp01(following * (own + faithful * campaign.Defines.MovementFaithShare) / region.Population);
    }

    public static float Distance(float latitude, float longitude, float otherLatitude, float otherLongitude)
    {
        const float radians = MathF.PI / 180f;
        var a = latitude * radians; var b = otherLatitude * radians;
        var cosine = MathF.Sin(a) * MathF.Sin(b) + MathF.Cos(a) * MathF.Cos(b) * MathF.Cos((longitude - otherLongitude) * radians);
        return 6371f * MathF.Acos(Math.Clamp(cosine, -1f, 1f));
    }
}
