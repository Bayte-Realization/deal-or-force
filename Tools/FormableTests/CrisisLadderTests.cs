using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// Crisis ladder: open international crises, great power mediation conferences,
/// rival patron pressure, and the 120-day forced compromise.
/// </summary>
public static class CrisisLadderTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL crisis ladder: " + text);
        Console.WriteLine("PASS crisis ladder: " + text);
    }

    public static void Run()
    {
        CrisisOpensWhenTensionStageAtOrAboveCrisis();
        CrisisClosesWhenTensionDropsBelowCrisis();
        MediatorCallsConferenceAndCoolsTension();
        PatronFuelsCrisisAgainstRival();
        ForcedCompromiseAfter120Days();
        FingerprintAndPersistence();
    }

    private static void SetElapsedDays(Campaign c, int days)
    {
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, days * 24L);
    }

    private static Campaign World()
    {
        var c = new Campaign(42100) { ScenarioId = "crisis_ladder_test" };
        c.SetPlayer(0);
        c.Nations.Add(new Nation { Id = 0, Tag = "N0", Name = "Country A", MapIndex = 1, Population = 2_000_000, Gdp = 100, Treasury = 50 });
        c.Nations.Add(new Nation { Id = 1, Tag = "N1", Name = "Country B", MapIndex = 2, Population = 2_000_000, Gdp = 100, Treasury = 50 });
        c.Nations.Add(new Nation { Id = 2, Tag = "GP1", Name = "Great Power Mediator", MapIndex = 3, Population = 50_000_000, Gdp = 5000, Treasury = 500 });
        c.Nations.Add(new Nation { Id = 3, Tag = "GP2", Name = "Great Power Patron", MapIndex = 4, Population = 50_000_000, Gdp = 5000, Treasury = 500 });
        return c;
    }

    private static void CrisisOpensWhenTensionStageAtOrAboveCrisis()
    {
        var c = World();
        var rel = CampaignDiplomacy.Get(c, 0, 1);
        rel.Tension = 75f;
        rel.TensionStage = (int)TensionStage.Crisis;
        rel.CrisisOpenedDay = -1;

        SetElapsedDays(c, 3);
        CrisisLadder.Tick(c);

        Check(rel.CrisisOpenedDay == 3, "crisis opened day recorded on day 3");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "crisis_opened" && e.ActorId == 0 && e.TargetId == 1),
            "crisis_opened recorded in chronicle");
    }

    private static void CrisisClosesWhenTensionDropsBelowCrisis()
    {
        var c = World();
        var rel = CampaignDiplomacy.Get(c, 0, 1);
        rel.Tension = 20f;
        rel.TensionStage = (int)TensionStage.Friction;
        rel.CrisisOpenedDay = 3;

        SetElapsedDays(c, 10);
        CrisisLadder.Tick(c);

        Check(rel.CrisisOpenedDay == -1, "crisis opened day cleared when stage drops below crisis");
    }

    private static void MediatorCallsConferenceAndCoolsTension()
    {
        var c = World();
        var rel = CampaignDiplomacy.Get(c, 0, 1);
        rel.Tension = 80f;
        rel.TensionStage = (int)TensionStage.Crisis;
        rel.CrisisOpenedDay = 3;

        // Nation 2 likes both nations (+25 >= 15)
        var toA = CampaignDiplomacy.Get(c, 2, 0);
        toA.Opinion = 25f;
        var toB = CampaignDiplomacy.Get(c, 2, 1);
        toB.Opinion = 25f;

        // Nation 3 has neutral opinion
        CampaignDiplomacy.Get(c, 3, 0).Opinion = 0f;
        CampaignDiplomacy.Get(c, 3, 1).Opinion = 0f;

        var startingTension = rel.Tension;
        var conferenceHeld = false;

        // Over multiple weekly ticks, mediator should host a conference and cool tension
        for (var day = 10; day <= 45; day += 7)
        {
            SetElapsedDays(c, day);
            CrisisLadder.Tick(c);
            if (c.Chronicle.Entries.Any(e => e.Kind == "crisis_conference"))
            {
                conferenceHeld = true;
                break;
            }
        }

        Check(conferenceHeld, "mediator hosted a crisis conference");
        Check(rel.Tension < startingTension, $"mediation cooled tension from {startingTension} to {rel.Tension}");
    }

    private static void PatronFuelsCrisisAgainstRival()
    {
        var c = World();
        var rel = CampaignDiplomacy.Get(c, 0, 1);
        rel.Tension = 60f;
        rel.TensionStage = (int)TensionStage.Crisis;
        rel.CrisisOpenedDay = 3;

        // No mediators
        CampaignDiplomacy.Get(c, 2, 0).Opinion = 0f;
        CampaignDiplomacy.Get(c, 2, 1).Opinion = 0f;

        // Nation 3 backs Nation 0 (+45 >= 40) against Nation 1 (-25 <= -20)
        CampaignDiplomacy.Get(c, 3, 0).Opinion = 45f;
        CampaignDiplomacy.Get(c, 3, 1).Opinion = -25f;

        SetElapsedDays(c, 10);
        CrisisLadder.Tick(c);

        Check(rel.Tension == 60f + c.Defines.CrisisPatronPressure,
            $"patron heated crisis tension to {rel.Tension}");
    }

    private static void ForcedCompromiseAfter120Days()
    {
        var c = World();
        var rel = CampaignDiplomacy.Get(c, 0, 1);
        rel.Tension = 70f;
        rel.GrievanceA = 40f;
        rel.GrievanceB = 60f;
        rel.TensionStage = (int)TensionStage.Crisis;
        rel.CrisisOpenedDay = 3;

        // 129 is day % 7 == 3, and 129 - 3 = 126 >= 120
        SetElapsedDays(c, 129);
        CrisisLadder.Tick(c);

        Check(MathF.Abs(rel.Tension - 35f) < 0.01f, $"forced compromise halved tension to {rel.Tension}");
        Check(MathF.Abs(rel.GrievanceA - 20f) < 0.01f, $"forced compromise halved GrievanceA to {rel.GrievanceA}");
        Check(MathF.Abs(rel.GrievanceB - 30f) < 0.01f, $"forced compromise halved GrievanceB to {rel.GrievanceB}");
        Check(rel.CrisisOpenedDay == -1, "crisis opened day reset to -1 after compromise");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "crisis_compromise" && e.ActorId == 0 && e.TargetId == 1),
            "crisis_compromise recorded in chronicle");
    }

    private static void FingerprintAndPersistence()
    {
        var c = World();
        var rel = CampaignDiplomacy.Get(c, 0, 1);
        rel.TensionStage = (int)TensionStage.Crisis;
        rel.CrisisOpenedDay = -1;

        var defaultHash = CampaignFingerprint.Of(c);

        rel.CrisisOpenedDay = 15;
        var activeCrisisHash = CampaignFingerprint.Of(c);

        Check(defaultHash != activeCrisisHash, "active crisis modifies campaign fingerprint");

        // Relations JSON serialization
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var json = System.Text.Json.JsonSerializer.Serialize(c.Relations, options);
        var restoredRelations = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<long, DiplomaticRelation>>(json, options);
        Check(restoredRelations != null, "relations deserialized from JSON");

        var restored = World();
        restored.Relations.Clear();
        foreach (var entry in restoredRelations) restored.Relations[entry.Key] = entry.Value;
        var restoredRel = CampaignDiplomacy.Get(restored, 0, 1);

        Check(restoredRel.CrisisOpenedDay == 15,
            $"crisis opened day survived serialization (restored {restoredRel.CrisisOpenedDay})");
        Check(CampaignFingerprint.Of(restored) == activeCrisisHash,
            "restored campaign fingerprint exactly matches original with active crisis");
    }
}
