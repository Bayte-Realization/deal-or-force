using System;
using System.Linq;
using DealOrForce.Simulation;

static class StabilityOutlookTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("stability outlook: " + label); Console.WriteLine("PASS stability outlook: " + label); }
    public static void Run()
    {
        var (c, nation, other) = TerritorialRecoveryTests.World();
        nation.Stability = .2f; nation.StabilityGovernance = .08f;
        nation.ImperialOverstretch = .73f;
        foreach (var state in c.StatesOf(nation)) state.Stability = .1234f;
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        var outlook = StabilityOutlook.Read(c, nation);
        Check(outlook is not null && outlook.Target > outlook.Current && outlook.NextReviewChange > 0,
            "a weakened country can have improving conditions, independent of its tag");
        Check(outlook.Direction == "Conditions favour recovery" && outlook.Tone == "state.positive", "recovery is distinguished from a high current value");
        Check(CampaignFingerprint.Of(c) == before && random == c.Random.State && nation.ImperialOverstretch == .73f &&
            c.StatesOf(nation).All(s => s.Stability == .1234f), "fresh preview writes neither regional stability, overstretch nor random state");
        Check(Math.Abs(outlook.Drivers.Total - outlook.Target) < .00001f, "the explanation adds up to the actual bounded target");
        Check(Math.Abs(StabilityCore.Target(c, nation) - outlook.Target) < .00001f && nation.ImperialOverstretch != .73f,
            "simulation review keeps its old writes and returns exactly the preview's rule");
        nation.Stability = .95f;
        outlook = StabilityOutlook.Read(c, nation);
        Check(outlook.Direction == "Pressure is building" && outlook.NextReviewChange < 0, "a currently strong state can be losing stability");
        nation.Stability = outlook.Target;
        Check(StabilityOutlook.Read(c, nation).Direction == "Holding steady", "a settled country is not forced into a rise or a collapse");
        var peace = StabilityOutlook.Read(c, nation);
        nation.WarSupport = .3f;
        c.Wars.Add(new StrategicWar { Id = 1, AttackerId = nation.Id, DefenderId = other.Id });
        var wartime = StabilityOutlook.Read(c, nation);
        Check(wartime.Target < peace.Target && wartime.MainDrag is { Value: < 0 }, "actual war pressure lowers the target and supplies a named cause");
        Check(wartime.Drivers.Entries.Any(e => e.Source == "The war" && e.Value < 0), "war pressure is the same contribution used by the economic tick");
        c.Wars[0].Ended = true;
        Check(StabilityOutlook.Read(c, nation).Target > wartime.Target, "ending war opens recovery without granting instant stability");
        Check(nation.Stability == peace.Current, "reading postwar recovery never awards a stability bonus");
        var approach = StabilityCore.Weights.Approach;
        try
        {
            StabilityCore.Weights.Approach = 0;
            Check(StabilityOutlook.Read(c, nation).NextReviewChange == 0, "a mod that freezes stability also freezes the shown direction");
        }
        finally { StabilityCore.Weights.Approach = approach; }
        var landless = c.AddNation(new Nation { Tag = "QLO", MapIndex = 599, Stability = .5f });
        Check(StabilityOutlook.Read(c, landless) is null && StabilityOutlook.Read(null, nation) is null,
            "landless and unavailable countries are not given invented domestic outlooks");
        nation.Stability = float.NaN;
        Check(StabilityOutlook.Read(c, nation) is null, "invalid figures are not painted as a reassuring outlook");
    }
}
