using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Verifies the WAR-02 nuclear standoff and DEFCON readiness ladder (DEFCON 5..1).
/// </summary>
static class DefconTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("defcon: " + label);
        Console.WriteLine("PASS defcon: " + label);
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(947986505);
        // The world ladder alone: the holders' own decisions and the incidents are tested below.
        c.Defines.AiNuclearPosture = false;
        c.Defines.NuclearIncidentChance = 0f;
        Check(c.DefconLevel == 5, "campaign opens at peacetime DEFCON 5");
        Check(c.DefconPeakLevel == 5, "campaign opens at peak DEFCON 5");
        Check(c.DefconDaysInStage == 0, "campaign opens with 0 dwell days");

        c.Wars.Clear();
        c.MarkWarsChanged();
        foreach (var r in c.Relations.Values) { r.TensionStage = (int)TensionStage.Calm; r.CrisisOpenedDay = -1; }

        var before = CampaignFingerprint.Of(c);

        // Baseline modifiers
        var us = c.Nations.FirstOrDefault(n => n.Tag == "USA");
        var ru = c.Nations.FirstOrDefault(n => n.Tag == "RUS");
        var nonNuclear = c.Nations.FirstOrDefault(n => Deterrence.TierOf(c, n) == 0);

        Check(us != null && ru != null && nonNuclear != null, "scenario nations available for test");
        c.AttachProvinceTopology(ProvinceTopology.Build(
            new ushort[] { 1001, 1002, 1001, 1002 },
            new ushort[] { (ushort)us.MapIndex, (ushort)ru.MapIndex, (ushort)us.MapIndex, (ushort)ru.MapIndex },
            2, 2, _ => false));
        c.ConfigureSinglePlayer(us.Id);
        before = CampaignFingerprint.Of(c);

        Check(Deterrence.TierOf(c, us) >= 1, "USA holds nuclear deterrent");
        Check(Deterrence.TierOf(c, ru) >= 1, "RUS holds nuclear deterrent");

        Check(MathF.Abs(DefconSystem.UpkeepMultiplier(c, us) - 1.0f) < 0.001f, "DEFCON 5 carries 1.0x upkeep multiplier");
        Check(MathF.Abs(DefconSystem.ReadinessRecoveryMultiplier(c, us.Id) - 1.0f) < 0.001f, "DEFCON 5 carries 1.0x readiness recovery");
        Check(MathF.Abs(DefconSystem.GlobalTradeMultiplier(c) - 1.0f) < 0.001f, "DEFCON 5 carries 1.0x trade multiplier");

        // Format summary reads without crashing or mutating fingerprint
        var summary = DefconSystem.FormatSummary(c);
        Check(summary.Contains("DEFCON 5"), "summary describes DEFCON 5 in peacetime");
        Check(CampaignFingerprint.Of(c) == before, "reading summary leaves fingerprint unchanged");

        // Simulate tension escalation between holders
        var rel = CampaignDiplomacy.Get(c, us.Id, ru.Id);
        rel.TensionStage = (int)TensionStage.Crisis;

        var target = DefconSystem.EvaluateTarget(c, out var hotspot);
        Check(target == 4, "crisis between two deterrent holders commands target DEFCON 4");
        Check(hotspot.Contains("diplomatic crisis"), "hotspot names diplomatic crisis");

        // Review day (ElapsedDays % 7 == 4)
        SetDay(c, 4);

        DefconSystem.Tick(c);
        Check(c.DefconLevel == 4, "escalates to DEFCON 4 on review day");
        Check(c.DefconPeakLevel == 4, "peak DEFCON updates to 4");
        Check(CampaignFingerprint.Of(c) != before, "elevated DEFCON participates in campaign fingerprint");

        // Border clashes
        rel.TensionStage = (int)TensionStage.Clashes;
        target = DefconSystem.EvaluateTarget(c, out _);
        Check(target == 3, "clashes between holders command target DEFCON 3");

        DefconSystem.Tick(c);
        Check(c.DefconLevel == 3, "escalates to DEFCON 3");
        Check(DefconSystem.ReadinessRecoveryMultiplier(c, us.Id) > 1.0f, "DEFCON 3 boosts military readiness recovery");
        Check(DefconSystem.UpkeepMultiplier(c, us) > 1.10f, "DEFCON 3 increases military alert upkeep");

        // Direct hot war
        var war = new StrategicWar
        {
            Id = 88001,
            AttackerId = us.Id,
            DefenderId = ru.Id,
            StartedDay = c.ElapsedDays - 10,
            WarScore = 15f
        };
        c.Wars.Add(war);
        c.MarkWarsChanged();

        target = DefconSystem.EvaluateTarget(c, out hotspot);
        Check(target == 2, "active armed conflict between holders commands target DEFCON 2");
        DefconSystem.Tick(c);
        Check(c.DefconLevel == 2, "escalates to DEFCON 2 (deployment in 6 hours)");
        Check(DefconSystem.GlobalTradeMultiplier(c) < 1.0f, "DEFCON 2 depresses world trade confidence");

        // Prolonged war with high score
        war.StartedDay = c.ElapsedDays - 50;
        war.WarScore = 45f;
        target = DefconSystem.EvaluateTarget(c, out _);
        Check(target == 1, "prolonged high-score war between holders commands maximum DEFCON 1");
        DefconSystem.Tick(c);
        Check(c.DefconLevel == 1, "escalates to DEFCON 1 (launch-on-warning triad alert)");
        Check(c.DefconPeakLevel == 1, "peak level reaches 1");

        // Save & Load roundtrip preservation
        var tempFolder = Path.Combine(Path.GetTempPath(), "dof-defcon-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveSystem.Save(tempFolder, "defcon_test", c, "tests");
            var disk = SaveSystem.Load(tempFolder, "defcon_test", "tests");
            Check(disk.Success, "save file successfully loaded");
            var loaded = disk.Campaign;
            loaded.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
            Check(loaded.DefconLevel == 1, "loaded campaign preserves DEFCON 1");
            Check(loaded.DefconPeakLevel == 1, "loaded campaign preserves peak DEFCON 1");
            Check(CampaignFingerprint.Of(loaded) == CampaignFingerprint.Of(c), "fingerprint bit-for-bit identical after save roundtrip");
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }

        // De-escalation: end the war and calm tensions
        war.Ended = true;
        c.Wars.Remove(war);
        c.MarkWarsChanged();
        rel.TensionStage = (int)TensionStage.Calm;

        target = DefconSystem.EvaluateTarget(c, out _);
        Check(target == 5, "peace commands target DEFCON 5");

        // Dwell days requirement
        c.DefconDaysInStage = 5;
        DefconSystem.Tick(c);
        Check(c.DefconLevel == 1, "cannot de-escalate prematurely without holding calm for de-escalation period");

        c.DefconDaysInStage = c.Defines.DefconDeescalationDays + 1;
        DefconSystem.Tick(c);
        Check(c.DefconLevel == 2, "de-escalates step by step (1 -> 2)");

        c.DefconDaysInStage = c.Defines.DefconDeescalationDays + 1;
        DefconSystem.Tick(c);
        Check(c.DefconLevel == 3, "de-escalates step by step (2 -> 3)");

        Posture();
    }

    /// <summary>A holder's own alert and its four acts (NuclearPosture), for the player and the computer alike.</summary>
    static void Posture()
    {
        var c = Campaign.CreateBootstrap(947986505);
        c.Defines.AiNuclearPosture = false;
        c.Defines.NuclearIncidentChance = 0f;
        c.Wars.Clear();
        foreach (var r in c.Relations.Values) { r.TensionStage = (int)TensionStage.Calm; r.CrisisOpenedDay = -1; }
        var us = c.NationByTag("USA"); var ru = c.NationByTag("RUS"); var ua = c.NationByTag("UKR");
        us.IsHuman = false; ru.IsHuman = false;
        SetDay(c, 200);
        us.PoliticalPoints = 100f; ru.PoliticalPoints = 100f;

        Check(NuclearPosture.Refusal(c, ua, "raise").Length > 0, "a country without the weapon has no posture to raise");
        c.Commands.Enqueue(new NuclearPostureCommand { IssuerId = ru.Id, Action = "raise" });
        c.Commands.ApplyAll(c);
        Check(ru.NuclearPosture == 4 && Math.Abs(ru.PoliticalPoints - (100f - c.Defines.NuclearPostureRaiseCost)) < 0.001f,
            "raising the alert takes one step and costs political credit");
        Check(NuclearPosture.Refusal(c, ru, "raise").Length > 0, "the alert cannot be raised again within the month");
        Check(DefconSystem.EvaluateTarget(c, out var why) == 4 && why.Length > 0,
            "the world is never calmer than its most alarmed holder");
        Check(DefconSystem.UpkeepMultiplier(c, ru) > DefconSystem.UpkeepMultiplier(c, us),
            "the alarmed holder pays for its own alert before the world follows");

        SetDay(c, 240); NuclearPosture.Apply(c, ru, "raise");
        SetDay(c, 280); NuclearPosture.Apply(c, ru, "raise");
        Check(ru.NuclearPosture == 2, "the alert can be walked down to DEFCON 2 a month at a time");
        var war = new StrategicWar { Id = 91001, AttackerId = ru.Id, DefenderId = ua.Id, StartedDay = 100 };
        c.Wars.Add(war); c.MarkWarsChanged();
        var support = ua.WarSupport = 0.8f;
        SetDay(c, 284);
        NuclearPosture.Week(c);
        Check(ua.WarSupport < support - 0.001f, "an enemy of a holder at DEFCON 2 loses war support to the threat");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "nuclear_alert_raised" && e.ActorId == ru.Id), "the world hears of the alert");

        var tension = CampaignDiplomacy.Get(c, us.Id, ru.Id);
        tension.Stance = DiplomaticStance.Rival; tension.Tension = 30f;
        Check(NuclearPosture.Apply(c, us, "test") && tension.Tension > 30f + c.Defines.NuclearTestTension - 0.01f,
            "a test raises the tension with every rival");
        Check(NuclearPosture.Refusal(c, us, "test").Length > 0, "and the next test waits a year");

        var line = CampaignDiplomacy.Get(c, us.Id, ru.Id); line.Tension = 60f;
        Check(NuclearPosture.Apply(c, us, "hotline", ru) && ru.NuclearPosture == 3 && line.Tension < 60f,
            "the red telephone takes both a step back and lowers the tension");

        // The computer's governments, by the same rules.
        c.Defines.AiNuclearPosture = true;
        ru.NuclearPosture = 5; ru.NuclearPostureDay = -100000; ru.PoliticalPoints = 100f;
        war.AttackerId = us.Id; war.DefenderId = ru.Id; war.StartedDay = 250;
        SetDay(c, 410);
        NuclearPosture.Week(c);
        Check(ru.NuclearPosture == 4, "a computer-run holder at war with another holder raises its alert");
        war.Ended = true; c.Wars.Remove(war); c.MarkWarsChanged();
        ru.NuclearPosture = 3; ru.NuclearPostureDay = 300;
        SetDay(c, 420);
        NuclearPosture.Week(c);
        Check(ru.NuclearPosture == 4, "and stands down once the danger has passed");
    }
}
