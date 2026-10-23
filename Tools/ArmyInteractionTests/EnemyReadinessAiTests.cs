using System;
using System.Linq;
using DealOrForce.Simulation;

// Enemy condition is already part of Army.Power, which the ordinary autonomous
// selector reads only for observed contacts. Guard that contract with actual
// assaults, not a parallel test-only target scorer or an omniscient AI bonus.
static class EnemyReadinessAiTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL enemy readiness AI: " + label);
        Console.WriteLine("PASS enemy readiness AI: " + label);
    }

    public static void Run()
    {
        PreferObservedWeakSector("organisation", a => a.Organisation = .25f);
        PreferObservedWeakSector("equipment", a => a.Equipment = .05f);
        HiddenConditionDoesNotChooseTargets();
        OwnReadinessStillMatters();
        ObservationIsReadOnly();
    }

    static (Campaign Campaign, Army Attacker, Army[] Defenders) World(bool visible = true)
    {
        var c = AdvanceTargetTests.Fixture();
        c.Defines.HoursPerTick = 1;
        c.Defines.BorderVisionDepth = visible ? 1 : 0;
        c.Defines.ArmyVisionDepth = visible ? 1 : 0;
        var a = AdvanceTargetTests.Unit(c, 1, 0, 14, ArmyOrder.AdvanceFront);
        a.Equipment = 1;
        a.OrderProvinceId = -1;
        a.OrderIssuedDay = c.ElapsedDays;
        var defenders = c.ProvinceMap.Neighbors[a.ProvinceId].OrderBy(p => p)
            .Select((p, index) => AdvanceTargetTests.Unit(c, 2 + index, 1, p,
                ArmyOrder.DefendFront, divisions: 2)).ToArray();
        foreach (var defender in defenders) defender.Equipment = 1;
        return (c, a, defenders);
    }

    static void PreferObservedWeakSector(string metric, Action<Army> weaken)
    {
        var (c, a, defenders) = World();
        // Pick the last neighbour, so a successful choice cannot be explained by
        // the selector's stable lowest-province-id tie break.
        var target = defenders[^1];
        var fullPower = target.Power;
        weaken(target);
        Check(target.Power < fullPower, metric + " reduces the existing combat power estimate");
        Check(defenders.All(d => CampaignIntel.CanSeeArmy(c, a.NationId, d)),
            metric + " fixture presents observed contacts, not hidden world knowledge");
        Check(defenders.All(d => d.Divisions == target.Divisions),
            metric + " changes the decision without changing division counts");
        Warfare.Tick(c);
        Check(c.Battles.Count == 1 && c.Battles[0].ProvinceId == target.ProvinceId,
            "autonomous advance actually assaults the visible weak-" + metric + " sector");

        var (other, _, otherDefenders) = World();
        var alternate = otherDefenders[^2];
        weaken(alternate);
        Warfare.Tick(other);
        Check(other.Battles.Count == 1 && other.Battles[0].ProvinceId == alternate.ProvinceId,
            "moving the " + metric + " weakness changes the real assault target");
    }

    static void HiddenConditionDoesNotChooseTargets()
    {
        foreach (var metric in new[] { "organisation", "equipment" })
        {
            var (c, a, defenders) = World(visible: false);
            var target = defenders[^1];
            if (metric == "organisation") target.Organisation = .25f;
            else target.Equipment = .05f;
            Check(defenders.All(d => !CampaignIntel.CanSeeArmy(c, a.NationId, d)),
                "unobserved " + metric + " fixture remains outside reconnaissance");
            Warfare.Tick(c);
            Check(c.Battles.Count == 1 && c.Battles[0].ProvinceId == defenders[0].ProvinceId,
                "hidden enemy " + metric + " does not pull the planner to an unseen weak sector");
        }
    }

    static void OwnReadinessStillMatters()
    {
        var (c, a, defenders) = World();
        defenders[^1].Organisation = .25f;
        a.Organisation = c.Defines.AutonomousAssaultMinimumOrganisation;
        Warfare.Tick(c);
        Check(c.Battles.Count == 0 && !a.InBattle,
            "knowing the enemy is disorganised does not bypass own organisation floor");
        (c, a, defenders) = World();
        defenders[^1].Equipment = .05f;
        a.SupplyLevel = c.Defines.AutonomousAssaultMinimumSupply;
        Warfare.Tick(c);
        Check(c.Battles.Count == 0 && !a.InBattle,
            "knowing the enemy lacks equipment does not bypass own supply floor");
    }

    static void ObservationIsReadOnly()
    {
        var (c, a, defenders) = World();
        var before = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        var power = 0f;
        foreach (var defender in defenders)
            if (CampaignIntel.CanSeeArmy(c, a.NationId, defender)) power += defender.Power;
        Check(power > 0, "observed readiness has a finite nonzero tactical weight");
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == random,
            "reading enemy condition changes neither campaign state nor random stream");
    }
}
