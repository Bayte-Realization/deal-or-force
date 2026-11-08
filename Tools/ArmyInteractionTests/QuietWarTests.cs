using System;
using System.Linq;
using DealOrForce.Simulation;

static class QuietWarTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run(Campaign c)
    {
        var war = c.Wars.First(w => !w.Ended);
        c.NationById(war.AttackerId).IsHuman = false;
        c.NationById(war.DefenderId).IsHuman = false;
        war.StartedDay = c.ElapsedDays - c.Defines.AiNoContactPeaceDays - 1;
        war.AiNegotiationUnlockDay = c.ElapsedDays;
        war.AttackerLosses = 0;
        war.DefenderLosses = 0;
        war.OriginalController.Clear();
        c.Battles.Clear();

        Check(WarWeariness.NoContactPeaceReady(c, war), "an AI war with a year of no contact can end");
        Check(PeaceNegotiation.Acceptance(c, war, war.AttackerId, PeaceTerms.WhitePeace) == 100f,
            "both governments accept a no-contact white peace");
        war.OriginalController[1] = c.ProvinceController[1];
        Check(!WarWeariness.NoContactPeaceReady(c, war), "contested ground prevents an early dismissal");
        war.OriginalController.Clear();
        war.AttackerLosses = 1;
        Check(!WarWeariness.NoContactPeaceReady(c, war), "real casualties prevent an early dismissal");
        war.AttackerLosses = 0;
        c.NationById(war.DefenderId).IsHuman = true;
        Check(!WarWeariness.NoContactPeaceReady(c, war), "a player decides whether their war ends");
        c.NationById(war.DefenderId).IsHuman = false;
        war.AiNegotiationUnlockDay = c.ElapsedDays + 1;
        Check(!WarWeariness.NoContactPeaceReady(c, war), "scenario negotiation lock is respected");
        war.AiNegotiationUnlockDay = c.ElapsedDays;
        c.Defines.AiNoContactPeaceDays = 0;
        Check(!WarWeariness.NoContactPeaceReady(c, war), "mods can disable the safeguard");
        c.Defines.AiNoContactPeaceDays = 365;

        var switzerland = c.NationByTag("CHE");
        var america = c.NationByTag("USA");
        var unreachable = new StrategicWar {
            Id = -101, AttackerId = switzerland.Id, DefenderId = america.Id,
            StartedDay = c.ElapsedDays - c.Defines.AiUnreachableWarPeaceDays - 1
        };
        Check(!AiStatecraft.CanStageWar(c, switzerland, america) &&
            !AiStatecraft.CanStageWar(c, america, switzerland),
            "landlocked-to-overseas fixture has no staging route");
        Check(WarWeariness.NoContactPeaceReady(c, unreachable),
            "unreachable AI-only war ends before a full year without contact");
        unreachable.AttackerFront.Add(1);
        // A front on paper is not a route (WarWeariness.NoContactPeaceReady): an army assigned to
        // a frontier the two sides do not share waited there for a year with nothing to fight.
        Check(WarWeariness.NoContactPeaceReady(c, unreachable),
            "a front on paper with no route between the sides does not keep an unreachable war alive");
        unreachable.AttackerFront.Clear();
        c.Defines.AiUnreachableWarPeaceDays = 0;
        Check(!WarWeariness.NoContactPeaceReady(c, unreachable),
            "mods can disable early unreachable-war peace separately");
        c.Defines.AiUnreachableWarPeaceDays = 180;

        war.WarScore = 45f; // diplomatic points are not a territorial victory
        PeaceNegotiation.Resolve(c, war, war.AttackerId, PeaceTerms.WhitePeace);
        Check(war.Ended && war.WinnerId < 0, "no-contact conflict signs an ordinary white peace");
    }
}
