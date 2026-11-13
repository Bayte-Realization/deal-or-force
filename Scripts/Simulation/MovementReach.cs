using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Religious affinity, diplomatic recognition and an actual local following are different things.</summary>
public static class MovementReach
{
    private sealed class ReachCache
    {
        public ProvinceTopology Map;
        public int Revision = -1;
        public readonly Dictionary<int, (float Latitude, float Longitude)> Centres = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, ReachCache> ReachCaches = new();
    [ThreadStatic] private static Dictionary<int, (double Latitude, double X, double Y, int Count)> _centreSums;

    private static (float Latitude, float Longitude) Centre(Campaign campaign, ScenarioState region)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return Movements.CentreOf(campaign, region);
        var cache = ReachCaches.GetOrCreateValue(campaign);
        if (!ReferenceEquals(cache.Map, map) || cache.Revision != campaign.OwnershipRevision)
        {
            cache.Map = map; cache.Revision = campaign.OwnershipRevision; cache.Centres.Clear();
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
        Nation owner = null)
    {
        if (campaign is null || nation?.Movement is not { } movement || region is null || definition is null) return false;
        owner ??= MovementRules.OwnerOf(campaign, region);
        var hostile = owner is not null && (movement.DesignatedBy.Contains(owner.Id) ||
            CampaignDiplomacy.AtWar(campaign, nation.Id, owner.Id));
        // A movement that already governs ground can organise anywhere on its own soil;
        // checking its distance from a homeland it has already won is a question with no
        // possible answer but no.
        if (owner is not null && owner.Id == nation.Id) return true;
        if (MovementDefinition.Listed(definition.Homeland, region.Id) || region.MapIndex == movement.HeadquartersStateMapIndex)
            return true;
        // A humanitarian mission needs permission, not simply believers of the same faith.
        var invited = owner is not null && !hostile && (movement.InvitedBy.Contains(owner.Id) ||
            movement.SponsoredBy.ContainsKey(owner.Id) || definition.Sponsors.ContainsKey(owner.Tag));
        if (invited) return true;
        if (definition.Has("humanitarian") && owner is not null && !hostile && movement.RecognisedBy.Contains(owner.Id)) return true;
        var reach = definition.Has("diaspora_lobby") ? definition.DiasporaReachKm : definition.OutreachKm;
        var point = Centre(campaign, region);
        var open = !definition.Has("humanitarian") || !hostile;
        if (Distance(movement.HeadquartersLatitude, movement.HeadquartersLongitude, point.Latitude, point.Longitude) <= reach)
            return open;
        // The homeland's place on the map does not move when a region's people do.
        // Resolving each homeland id, for every region asked, was the walk.
        var homes = HomelandCentresOf(campaign, definition);
        for (var i = 0; i < homes.Count; i++)
        {
            var home = homes.Points[i];
            if (Distance(home.Latitude, home.Longitude, point.Latitude, point.Longitude) <= reach)
                return open;
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

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, Dictionary<MovementDefinition, HomelandCentres>> HomelandCentreCaches = new();

    static HomelandCentres HomelandCentresOf(Campaign campaign, MovementDefinition definition)
    {
        var table = HomelandCentreCaches.GetValue(campaign, static _ => new Dictionary<MovementDefinition, HomelandCentres>());
        if (!table.TryGetValue(definition, out var cached))
            table[definition] = cached = new HomelandCentres();
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
        MovementDefinition definition, long own, long faithful, Nation owner = null)
    {
        if (!CanOperate(campaign, nation, region, definition, owner)) return 0f;
        // A defeated regional insurgency is not a worldwide movement of its culture.
        // Diaspora offices may be hosted abroad, but do not create a territorial following there.
        if (nation.Movement.Id.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal) &&
            !IsHomeland(campaign, definition, region)) return 0f;
        // A campaign for one region's charter is a campaign in that region: the Silesian
        // autonomists had a following among the Poles of Grodno and Kaliningrad, and the
        // Texan nationalists would have had one in California.
        if (definition.Has("civic_independence") && !IsHomeland(campaign, definition, region)) return 0f;
        if (own > 0) return MovementRules.MaxSupport;
        // The shared faith supplies a small sympathetic audience, not a territorial mandate.
        return faithful > 0 ? .04f : 0f;
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
        var own = MovementRules.PeopleOf(c, region, definition, out _);
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
        var own = MovementRules.PeopleOf(campaign, region, definition, out var faithful);
        var following = MathF.Min(nation.Movement.Support.GetValueOrDefault(region.MapIndex),
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
