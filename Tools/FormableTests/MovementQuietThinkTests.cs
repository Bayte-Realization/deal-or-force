using System;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// A movement whose host, region owner, population stamp and patience have not moved
/// since its last think is not thought again. A movement whose region owner changed is.
/// A second campaign keeps its own memory.
/// </summary>
static class MovementQuietThinkTests
{
    static int _passed;

    static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("quiet movement think: " + note);
        _passed++;
        Console.WriteLine("PASS quiet movement think: " + note);
    }

    public static void Run()
    {
        var first = World();
        var second = World();
        Check(first.NationById(0) == first.Nations[0] && first.NationById(2) == first.Nations[2] &&
              second.NationById(2) == second.Nations[2],
            "nation id is the list index");

        AtDay(first, 5);
        MovementAi.Tick(first);
        Check(MovementAi.ThinksRun(first) == 1 && MovementAi.QuietSkips(first) == 0,
            "the first think runs");

        AtDay(second, 5);
        MovementAi.Tick(second);
        Check(MovementAi.ThinksRun(second) == 1 && MovementAi.ThinksRun(first) == 1,
            "two campaigns do not share the think memory");

        AtDay(first, 12);
        var quiet = CampaignFingerprint.Of(first);
        MovementAi.Tick(first);
        Check(MovementAi.ThinksRun(first) == 1 && MovementAi.QuietSkips(first) == 1,
            "a quiet movement is not rethought");
        Check(CampaignFingerprint.Of(first) == quiet,
            "skipping the think does not change the fingerprint");

        first.States[0].NationMapIndex = 11;
        AtDay(first, 19);
        MovementAi.Tick(first);
        Check(MovementAi.ThinksRun(first) == 2 && MovementAi.QuietSkips(first) == 1,
            "a movement whose region owner changed is thought");

        AtDay(second, 12);
        MovementAi.Tick(second);
        Check(MovementAi.ThinksRun(second) == 1 && MovementAi.QuietSkips(second) == 1 &&
              MovementAi.ThinksRun(first) == 2,
            "the other campaign still skips its own quiet week");

        Console.WriteLine($"PASS quiet movement think: {_passed} checks");
    }

    static Campaign World()
    {
        var campaign = new Campaign(12012);
        campaign.AddNation(new Nation
        {
            Tag = "HST", Name = "Host", MapIndex = 10, Population = 1_000, Stability = 0.8f
        });
        campaign.AddNation(new Nation
        {
            Tag = "OTH", Name = "Other", MapIndex = 11, Population = 1_000, Stability = 0.8f
        });
        campaign.AddNation(new Nation
        {
            Tag = "MOV", Name = "Movement", MapIndex = 12, Population = 1_000,
            Movement = new MovementState
            {
                Id = "quiet_test", HeadquartersStateMapIndex = 100, Dissolved = false, Landless = false, Heat = 0.1f
            }
        });
        campaign.States.Add(new ScenarioState
        {
            Id = "home", Name = "Home", MapIndex = 100, NationMapIndex = 10, Population = 1_000
        });
        return campaign;
    }

    static void AtDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!
            .SetValue(campaign, (long)day * 24);
}
