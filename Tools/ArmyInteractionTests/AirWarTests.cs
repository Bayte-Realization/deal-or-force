using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The air war (item 87): a formation over a defended front pays for it, a resting one does not,
/// and the fighters overhead are what the attack aircraft are not losing.
/// </summary>
static class AirWarTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL air war: " + name);
        Console.WriteLine("PASS air war: " + name);
    }

    public static void Run(Campaign campaign)
    {
        SupportTypeCatalog.ResetCatalog();
        var ukraine = campaign.NationByTag("UKR");
        var russia = campaign.NationByTag("RUS");
        var kharkiv = campaign.States.First(state => state.Id == "ua_63");
        var belgorod = campaign.States.First(state => state.Id == "ru_bel");
        var ukrainianBase = SupportCore.BaseProvinceOf(campaign, ukraine, kharkiv.MapIndex);
        var russianBase = SupportCore.BaseProvinceOf(campaign, russia, belgorod.MapIndex);
        Check(ukrainianBase > 0 && russianBase > 0, "Kharkiv and Belgorod each have a province to base in");

        // Only the formations of this test fly: the opening establishments stand down.
        foreach (var formation in campaign.SupportFormations) formation.Mission = SupportCore.MissionStandby;
        var sam = campaign.SupportFormations.Where(f => SupportTypeCatalog.Get(f.TypeId)?.Kind == SupportKind.AirDefence).ToList();
        foreach (var formation in sam) campaign.SupportFormations.Remove(formation);

        var attack = Formation(90001, ukraine, kharkiv, ukrainianBase, "attack_aircraft_wing", SupportCore.MissionCloseAirSupport);
        var fighters = Formation(90002, russia, belgorod, russianBase, "fighter_wing", SupportCore.MissionAirSuperiority);
        campaign.SupportFormations.Add(attack);
        campaign.SupportFormations.Add(fighters);
        SupportCore.IndexReach(campaign);

        var front = ukrainianBase;
        var attackers = new HashSet<int> { ukraine.Id };
        var defenders = new HashSet<int> { russia.Id };
        campaign.SupportExposure.Clear();
        SupportCore.Engagement(campaign, front, attackers, defenders);
        var exposed = campaign.SupportExposure.GetValueOrDefault(attack.Id);
        Check(exposed > 0f, $"an attack wing over a front the enemy's fighters cover is exposed ({exposed:0.0})");
        Check(campaign.SupportExposure.GetValueOrDefault(fighters.Id) == 0f,
            "fighters meeting nothing in the sky pay nothing");

        // A day of it.
        var before = attack.Strength;
        SupportCore.Tick(campaign);
        var lost = before - attack.Strength;
        Check(lost > 0.002f && lost < 0.05f, $"and loses strength for it: {lost:P1} in a day");
        Check(campaign.SupportLossesToday.ContainsKey(attack.Id), "the day's losses are there for the panel to show");

        // Covered by its own fighters, it loses less.
        var cover = Formation(90003, ukraine, kharkiv, ukrainianBase, "fighter_wing", SupportCore.MissionAirSuperiority);
        campaign.SupportFormations.Add(cover);
        SupportCore.IndexReach(campaign);
        attack.Mission = SupportCore.MissionCloseAirSupport;
        fighters.Mission = SupportCore.MissionAirSuperiority;
        campaign.SupportExposure.Clear();
        SupportCore.Engagement(campaign, front, attackers, defenders);
        var covered = campaign.SupportExposure.GetValueOrDefault(attack.Id);
        Check(covered < exposed, $"under its own fighters the same wing meets less ({covered:0.0} against {exposed:0.0})");
        Check(campaign.SupportExposure.GetValueOrDefault(fighters.Id) > 0f,
            "and the enemy's fighters now have a fight of their own");

        // Resting, it is not there at all.
        attack.Mission = SupportCore.MissionStandby;
        campaign.SupportExposure.Clear();
        SupportCore.Engagement(campaign, front, attackers, defenders);
        Check(!campaign.SupportExposure.ContainsKey(attack.Id), "a resting wing is not over the front and pays nothing");

        // Never shot to nothing: what is left is pulled back.
        attack.Mission = SupportCore.MissionCloseAirSupport;
        attack.Strength = campaign.Defines.SupportAttritionFloor + 0.001f;
        for (var day = 0; day < 30; day++)
        {
            campaign.SupportExposure[attack.Id] = 500f;
            SupportCore.Tick(campaign);
        }
        Check(attack.Strength >= campaign.Defines.SupportAttritionFloor - 0.0001f,
            $"attrition stops at the floor ({attack.Strength:P0})");

        campaign.SupportFormations.Remove(attack);
        campaign.SupportFormations.Remove(fighters);
        campaign.SupportFormations.Remove(cover);
        campaign.SupportFormations.AddRange(sam);
        SupportCore.IndexReach(campaign);
    }

    private static SupportFormation Formation(int id, Nation nation, ScenarioState state, int province, string type, string mission) =>
        new()
        {
            Id = id, NationId = nation.Id, TypeId = type, StateMapIndex = state.MapIndex,
            BaseProvinceId = province, Strength = 1f, Experience = 0.3f, Mission = mission
        };
}
