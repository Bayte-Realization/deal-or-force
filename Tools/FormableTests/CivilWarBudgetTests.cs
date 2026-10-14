using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class CivilWarBudgetTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("civil-war budget: " + name);
        Console.WriteLine("PASS civil-war budget: " + name);
    }

    public static void Run()
    {
        CoupMemorySurvivesChronicleOverflow();
        MilitaryThenInstitutional();
        CoupKindsAndSentinels();
        RollingWindowAndBoundedState();
        CappedReviewIsRepeatable();
        SuccessfulUprisingRecordsItsBudget();
        CurrentSnapshotDoesNotInferAnOmittedMilitaryDate();
        LegacyMigration();
    }

    static void Day(Campaign c, int day) => typeof(Campaign)
        .GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);

    static void CoupMemorySurvivesChronicleOverflow()
    {
        var c = new Campaign(842);
        var nation = new Nation { Id = 0, MapIndex = 1, Tag = "TST", Name = "Test country",
            Population = 1_000_000, Stability = 0f, LastCoupDay = 100 };
        c.Nations.Add(nation);
        Day(c, 100);
        Coups.Seize(c, nation);
        var before = CivilWarOutbreak.Chance(c, nation);
        for (var i = 0; i <= WorldChronicle.Capacity; i++)
            c.Chronicle.Record(c, "budget_fixture", nation.Id, isPublic: false);
        Check(before > c.Defines.CivilWarWeeklyChance && CivilWarOutbreak.Chance(c, nation) == before,
            "the last coup keeps its risk multiplier after presentation history is evicted");
    }

    static void CoupKindsAndSentinels()
    {
        var c = new Campaign(843);
        var n = new Nation { Id = 0, Tag = "TST", Stability = 0f, LastCoupDay = 100 };
        c.Nations.Add(n);
        var ordinary = c.Defines.CivilWarWeeklyChance;
        n.DomesticCrisis.LastOutcomeDay = 100;
        n.DomesticCrisis.LastOutcomeId = "military_coup";
        Day(c, 100);
        c.Chronicle.Record(c, "institutional_coup", n.Id);
        FillChronicle(c, n.Id);
        Check(CivilWarOutbreak.Chance(c, n) == ordinary,
            "institutional transitions do not gain a new multiplier in this bug fix");
        n.DomesticCrisis.LastOutcomeId = "Military coup";
        n.LastMilitaryCoupDay = 100;
        Check(CivilWarOutbreak.Chance(c, n) == ordinary * 2f,
            "a saved military outcome retains its existing multiplier without chronicle rows");
        Day(c, 465);
        Check(CivilWarOutbreak.Chance(c, n) == ordinary * 2f,
            "coup memory includes its existing 365-day boundary");
        Day(c, 466);
        Check(CivilWarOutbreak.Chance(c, n) == ordinary,
            "coup memory expires on day 366");
        Day(c, 100);
        n.DomesticCrisis = new();
        n.LastMilitaryCoupDay = -99999;
        CivilWarOutbreak.RestoreMilitaryCoupMemory(c);
        Check(CivilWarOutbreak.Chance(c, n) == ordinary * 2f,
            "legacy coup dates without an outcome use the documented military fallback");
        n.LastCoupDay = -99999;
        n.LastMilitaryCoupDay = -99999;
        Check(CivilWarOutbreak.Chance(c, n) == ordinary,
            "the legacy no-coup sentinel never creates a remembered coup");
        n.LastCoupDay = 101;
        n.LastMilitaryCoupDay = 101;
        Check(CivilWarOutbreak.Chance(c, n) == ordinary,
            "a future coup timestamp is not a past coup");
    }

    static void MilitaryThenInstitutional()
    {
        var c = Fixture(849);
        var n = c.Player;
        Day(c, 100);
        Coups.Seize(c, n);
        Day(c, 200);
        n.Stability = 0f;
        Coups.RegisterOutcome(c, n, "quiet_coup");
        c.Chronicle.Record(c, "institutional_coup", n.Id);
        var expected = c.Defines.CivilWarWeeklyChance * 2f;
        Check(CivilWarOutbreak.Chance(c, n) == expected,
            "a later institutional coup does not erase the still-recent military multiplier");
        var legacy = LegacySnapshot(c);
        Check(legacy.Player.LastMilitaryCoupDay == 100 && legacy.Player.LastCoupDay == 200 &&
            CivilWarOutbreak.Chance(legacy, legacy.Player) == expected,
            "legacy mixed coups recover the earlier military date without converting the later transition");
        FillChronicle(c, n.Id);
        Check(CivilWarOutbreak.Chance(c, n) == expected,
            "the military-then-institutional sequence survives chronicle eviction");
        var fingerprint = CampaignFingerprint.Of(c);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "budget"), "budget");
        Check(saved.Success, "the mixed coup sequence snapshot loads: " + saved.Warning);
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CivilWarOutbreak.Chance(saved.Campaign, saved.Campaign.Player) == expected &&
            CampaignFingerprint.Of(saved.Campaign) == fingerprint,
            "the mixed sequence preserves probability and peer fingerprint across load");
    }

    static void RollingWindowAndBoundedState()
    {
        var c = new Campaign(844);
        Day(c, 100);
        CivilWarOutbreak.RecordStart(c);
        CivilWarOutbreak.RecordStart(c);
        Check(CivilWarOutbreak.StartsInLastYear(c) == 2 && c.CivilWarBudget.StartsByDay.Count == 1,
            "two actual starts on one day share one counter without losing either start");
        Day(c, 464);
        Check(CivilWarOutbreak.StartsInLastYear(c) == 2, "age 364 belongs to the rolling year");
        Day(c, 465);
        Check(CivilWarOutbreak.StartsInLastYear(c) == 0, "age 365 is outside the rolling year");
        c.Defines.CivilWarOutbreaks = false;
        CivilWarOutbreak.Tick(c);
        Check(c.CivilWarBudget.StartsByDay.Count == 0,
            "expired counters are cleaned even when outbreaks are disabled");
        for (var day = 0; day < 1500; day++)
        {
            Day(c, day);
            CivilWarOutbreak.RecordStart(c);
            CivilWarOutbreak.RecordStart(c);
        }
        Check(c.CivilWarBudget.StartsByDay.Count == 365 && CivilWarOutbreak.StartsInLastYear(c) == 730,
            "centuries of starts require at most 365 stored daily counters");
        c.CivilWarBudget.StartsByDay[1499] = int.MaxValue;
        Check(CivilWarOutbreak.StartsInLastYear(c) == int.MaxValue,
            "a pathological scripted count saturates without integer overflow");
    }

    static void CappedReviewIsRepeatable()
    {
        var c = Fixture(845);
        Day(c, 1004);
        for (var i = 0; i < c.Defines.CivilWarWorldPerYear; i++)
        {
            CivilWarOutbreak.RecordStart(c);
            c.Chronicle.Record(c, "civil_war", c.Player.Id);
        }
        FillChronicle(c, c.Player.Id);
        c.Player.Stability = 0f;
        c.Defines.CivilWarWeeklyChance = 1f;
        c.Defines.CivilWarStrikesPlayers = true;
        Check(CivilWarOutbreak.EligibilityReason(c, c.Player) == "", "the budget fixture would otherwise permit an outbreak");
        var fingerprint = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        CivilWarOutbreak.Tick(c);
        CivilWarOutbreak.Tick(c);
        Check(c.Nations.Count == 1 && c.Wars.Count == 0 && CivilWarOutbreak.StartsInLastYear(c) == 3,
            "evicted chronicle facts and repeated same-day reviews cannot bypass the global limit");
        Check(c.Random.State == random && CampaignFingerprint.Of(c) == fingerprint,
            "a capped review and its repeated call consume no RNG or authoritative state");
    }

    static void SuccessfulUprisingRecordsItsBudget()
    {
        var c = Fixture(846);
        Day(c, 1004);
        c.Player.Stability = 0f;
        c.Defines.CivilWarWeeklyChance = 1f;
        c.Defines.CivilWarStrikesPlayers = true;
        CivilWarOutbreak.Tick(c);
        Check(c.Nations.Count == 2 && CivilWarOutbreak.StartsInLastYear(c) == 1,
            "a successful uprising records its budget through the existing simulation fact path");
        CivilWarOutbreak.Tick(c);
        Check(CivilWarOutbreak.StartsInLastYear(c) == 1,
            "reviewing the existing civil war again does not record a second start");
        var fingerprint = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "budget"), "budget");
        Check(saved.Success, "a current budget-bearing snapshot loads: " + saved.Warning);
        // Snapshots restore the authoritative raster arrays first; the host attaches its
        // independently loaded topology before comparing peer fingerprints.
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CivilWarOutbreak.StartsInLastYear(saved.Campaign) == 1 &&
            CampaignFingerprint.Of(saved.Campaign) == fingerprint && saved.Campaign.Random.State == random,
            "save/load preserves the rolling budget, peer fingerprint and RNG");
        saved.Campaign.CivilWarBudget.StartsByDay[1004]++;
        Check(CampaignFingerprint.Of(saved.Campaign) != fingerprint,
            "different outbreak budgets are detected by peer fingerprints");
    }

    static void LegacyMigration()
    {
        var c = Fixture(847);
        var parent = c.Player;
        var born = new Nation { Id = 1, MapIndex = 501, Tag = "REB", Name = "Rebels", Population = 0,
            OutputScale = 1f, SecededFromId = parent.Id, LastCivilWarDay = 200 };
        c.Nations.Add(born);
        parent.LastCivilWarDay = 200;
        parent.LastSecessionDay = 200;
        parent.LastCoupDay = 180;
        parent.DomesticCrisis.LastOutcomeDay = 180;
        parent.DomesticCrisis.LastOutcomeId = "Military coup";
        parent.DomesticCrisis.CooldownUntilDay = 910;
        c.Defines.CivilWarStrikesPlayers = true;
        c.Wars.Add(new StrategicWar { Id = 0, AttackerId = parent.Id, DefenderId = born.Id,
            StartedDay = 200, CivilConflict = true, Ended = true, EndedDay = 220 });
        Day(c, 200);
        c.Chronicle.Record(c, "civil_war", born.Id, parent.Id);
        Day(c, 300);
        var random = c.Random.State;
        var cooldown = CivilWarOutbreak.EligibilityReason(c, parent);
        Check(cooldown == "The country is protected from another outbreak during recovery.",
            "the legacy fixture exercises recovery rather than player immunity");
        var migrated = LegacySnapshot(c);
        Check(CivilWarOutbreak.StartsInLastYear(migrated) == 1,
            "legacy war, chronicle and parent/rebel dates describe one start, not three");
        Check(migrated.Random.State == random && migrated.Player.LastCivilWarDay == 200 &&
            migrated.Player.LastSecessionDay == 200 && migrated.Player.LastCoupDay == 180 &&
            migrated.Player.DomesticCrisis.CooldownUntilDay == 910,
            "legacy migration preserves RNG and every existing recovery/coup cooldown");
        Check(CivilWarOutbreak.EligibilityReason(migrated, migrated.Player) == cooldown,
            "migration retains the existing outbreak recovery refusal");
        FillChronicle(c, parent.Id);
        migrated = LegacySnapshot(c);
        Check(CivilWarOutbreak.StartsInLastYear(migrated) == 1,
            "legacy budget survives already-evicted facts using matching saved war and nation dates");
        c.Wars.Clear();
        born.SecededFromId = -1;
        migrated = LegacySnapshot(c);
        Check(CivilWarOutbreak.StartsInLastYear(migrated) == 1,
            "ambiguous orphaned legacy dates use a conservative one-start lower bound");
        var empty = Fixture(848);
        empty.Nations.Add(new Nation { Id = 1, MapIndex = 501, Tag = "INIT", Name = "Scenario faction", OutputScale = 1f });
        empty.Wars.Add(new StrategicWar { Id = 0, AttackerId = 0, DefenderId = 1, StartedDay = 0, CivilConflict = true });
        empty.Player.Stability = 0f;
        Day(empty, 300);
        migrated = LegacySnapshot(empty);
        Check(CivilWarOutbreak.StartsInLastYear(migrated) == 0 && migrated.CivilWarBudget.StartsByDay.Count == 0,
            "old zero sentinels and scenario civil conflicts never invent a day-zero outbreak");
        Check(migrated.Player.LastCoupDay == -99999 &&
            CivilWarOutbreak.Chance(migrated, migrated.Player) == migrated.Defines.CivilWarWeeklyChance,
            "an omitted old-save coup date restores its negative sentinel, not a multiplier");
    }

    static void CurrentSnapshotDoesNotInferAnOmittedMilitaryDate()
    {
        var c = Fixture(850);
        Day(c, 200);
        c.Player.LastCoupDay = 100;
        c.Player.LastMilitaryCoupDay = -99999;
        var fingerprint = CampaignFingerprint.Of(c);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "budget"), "budget");
        Check(saved.Success, "a current snapshot with an intentionally empty military date loads");
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(saved.Campaign.Player.LastMilitaryCoupDay == -99999 &&
            CampaignFingerprint.Of(saved.Campaign) == fingerprint,
            "a current payload preserves its negative date instead of receiving legacy guesses");
    }

    static Campaign Fixture(int seed)
    {
        var c = new Campaign(seed) { ScenarioId = "civil_war_budget_fixture" };
        var parent = new Nation { Id = 0, MapIndex = 500, Tag = "TST", Name = "Test country", Population = 20_000_000,
            Treasury = 20, Gdp = 100, OutputScale = 1f, Stability = .5f, IsHuman = true, CapitalStateMapIndex = 3001 };
        c.Nations.Add(parent);
        for (var i = 0; i < 4; i++)
            c.States.Add(new ScenarioState { MapIndex = 3001 + i, Id = "budget_" + i, Name = "Region " + i,
                NationMapIndex = parent.MapIndex, NationId = parent.Tag, Population = 5_000_000,
                StandardOfLiving = .4f, Literacy = .8f, Urbanization = .5f, Radicalism = .4f });
        var cells = Enumerable.Range(0, 8192).Select(i => (ushort)(3001 + i % 128 / 32)).ToArray();
        var flags = Enumerable.Repeat((ushort)500, 8192).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, flags, 128, 64, _ => false));
        c.ConfigureSinglePlayer(parent.Id);
        // Model a current campaign, whose finance books were opened during bootstrap.
        // Intentionally uninitialised books undergo legacy migration when imported.
        SovereignFinance.NormalizeAccounts(c);
        return c;
    }

    static void FillChronicle(Campaign c, int actor)
    {
        for (var i = 0; i <= WorldChronicle.Capacity; i++)
            c.Chronicle.Record(c, "budget_fixture", actor, isPublic: false);
    }

    static Campaign LegacySnapshot(Campaign c)
    {
        var wire = SaveSystem.ExportNetworkSnapshot(c, "budget");
        using var input = new MemoryStream(wire);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        var payload = JsonNode.Parse(gzip).AsObject();
        payload.Remove("CivilWarBudget");
        foreach (var nation in payload["Nations"].AsArray()) nation.AsObject().Remove("LastMilitaryCoupDay");
        using var output = new MemoryStream();
        using (var compressed = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new StreamWriter(compressed)) writer.Write(payload.ToJsonString());
        var loaded = SaveSystem.ImportNetworkSnapshot(output.ToArray(), "budget");
        Check(loaded.Success, "legacy snapshot imports: " + loaded.Warning);
        return loaded.Campaign;
    }
}
