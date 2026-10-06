using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Religious affinity, diplomatic recognition and an actual local following are different things.</summary>
public static class MovementReach
{
    private static ProvinceTopology _map;
    private static Campaign _campaign;
    private static int _revision = -1;
    private static readonly Dictionary<int, (float Latitude, float Longitude)> Centres = new();

    private static (float Latitude, float Longitude) Centre(Campaign campaign, ScenarioState region)
    {
        var map = campaign.ProvinceMap;
        if (map is null) return Movements.CentreOf(campaign, region);
        if (!ReferenceEquals(_map, map) || !ReferenceEquals(_campaign, campaign) || _revision != campaign.MapRevision)
        {
            _map = map; _campaign = campaign; _revision = campaign.MapRevision; Centres.Clear();
            var sums = new Dictionary<int, (double Latitude, double X, double Y, int Count)>();
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                var id = map.ProvinceToState[province];
                var (latitude, longitude) = map.CenterOf(province);
                var old = sums.GetValueOrDefault(id);
                sums[id] = (old.Latitude + latitude, old.X + Math.Cos(longitude * Math.PI / 180),
                    old.Y + Math.Sin(longitude * Math.PI / 180), old.Count + 1);
            }
            foreach (var (id, sum) in sums)
                Centres[id] = ((float)(sum.Latitude / sum.Count), (float)(Math.Atan2(sum.Y, sum.X) * 180 / Math.PI));
        }
        return Centres.GetValueOrDefault(region.MapIndex);
    }

    public static bool CanOperate(Campaign campaign, Nation nation, ScenarioState region, MovementDefinition definition)
    {
        if (campaign is null || nation?.Movement is not { } movement || region is null || definition is null) return false;
        var owner = MovementRules.OwnerOf(campaign, region);
        var hostile = owner is not null && (movement.DesignatedBy.Contains(owner.Id) ||
            CampaignDiplomacy.AtWar(campaign, nation.Id, owner.Id));
        if (MovementDefinition.Listed(definition.Homeland, region.Id) || region.MapIndex == movement.HeadquartersStateMapIndex)
            return true;
        // A humanitarian mission needs permission, not simply believers of the same faith.
        var invited = owner is not null && !hostile && (movement.InvitedBy.Contains(owner.Id) ||
            movement.SponsoredBy.ContainsKey(owner.Id) || definition.Sponsors.ContainsKey(owner.Tag));
        if (invited) return true;
        if (definition.Has("humanitarian") && owner is not null && !hostile && movement.RecognisedBy.Contains(owner.Id)) return true;
        var reach = definition.Has("diaspora_lobby") ? definition.DiasporaReachKm : definition.OutreachKm;
        var point = Centre(campaign, region);
        if (Distance(movement.HeadquartersLatitude, movement.HeadquartersLongitude, point.Latitude, point.Longitude) <= reach)
            return !definition.Has("humanitarian") || !hostile;
        foreach (var id in definition.Homeland)
            if (Movements.StateById(campaign, id) is { } home)
            {
                var homePoint = Centre(campaign, home);
                if (Distance(homePoint.Latitude, homePoint.Longitude, point.Latitude, point.Longitude) <= reach)
                    return !definition.Has("humanitarian") || !hostile;
            }
        return false;
    }

    public static float SupportCeiling(Campaign campaign, Nation nation, ScenarioState region,
        MovementDefinition definition, long own, long faithful)
    {
        if (!CanOperate(campaign, nation, region, definition)) return 0f;
        if (own > 0) return MovementRules.MaxSupport;
        // The shared faith supplies a small sympathetic audience, not a territorial mandate.
        return faithful > 0 ? .04f : 0f;
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
