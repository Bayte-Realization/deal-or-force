using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PeaceAnnexationTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL peace-annexation: " + label);
        Console.WriteLine("PASS peace-annexation: " + label);
    }

    [TestSuite("--peace-annexation-only", Order = 2699, IncludeInDefaultRun = true,
        Description = "Peace conference total annexation blocker fix (human & collapsed/occupied AI), post-annexation unit cleanup, Annex buttons in PeaceConferenceUI & WarLedgerDetailUI, and EN/RU parity.")]
    public static void Run()
    {
        RunHumanAnnexationIgnoresFightsOnWithoutGroundTest();
        RunAiAnnexationWaivedOnCollapseOrTotalOccupationTest();
        RunPostAnnexationUnitCleanupTest();
        RunSeparatePeaceHumanCooldownTest();
        RunUiAndLocalizationContractsTest();
    }

    private static (Campaign c, Nation winner, Nation loser, StrategicWar war, List<int> loserProvinces)
        BuildAnnexFixture(int seed, bool humanProposer, float warScore, bool occupyAll)
    {
        const int cols = 4, rows = 2, block = 6;
        int Reg(int col, int row) => 7801 + col + row * cols;
        var c = new Campaign(seed) { ScenarioId = "peace_annex_fixture" };
        c.Defines.RisingsFightOnWithoutGround = true;

        var winner = new Nation
        {
            Id = 0, MapIndex = 941, Tag = "PXA", Name = "Ukraine",
            Treasury = 80f, Population = 36_000_000, Gdp = 180f,
            Stability = 0.65f, WarSupport = 0.80f, IsHuman = humanProposer
        };
        var loser = new Nation
        {
            Id = 1, MapIndex = 942, Tag = "PXB", Name = "Novorossiya",
            Treasury = 20f, Population = 4_000_000, Gdp = 20f,
            Stability = 0.55f, WarSupport = 0.90f, IsHuman = false,
            SecededFromId = 0, FoundedInRevolt = "revolt"
        };
        c.Nations.Add(winner);
        c.Nations.Add(loser);

        for (var r = 0; r < rows; r++)
        for (var col = 0; col < cols; col++)
        {
            var isWinner = col < cols / 2;
            c.States.Add(new ScenarioState
            {
                MapIndex = Reg(col, r),
                NationMapIndex = isWinner ? winner.MapIndex : loser.MapIndex,
                NationId = isWinner ? winner.Tag : loser.Tag,
                Id = $"px_{col}_{r}",
                Name = $"Region {col}-{r}",
                Population = 1_000_000
            });
        }

        var w = cols * block;
        var h = rows * block;
        var cells = new ushort[w * h];
        var owners = new ushort[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var col = x / block;
            var row = y / block;
            cells[y * w + x] = (ushort)Reg(col, row);
            owners[y * w + x] = (ushort)(col < cols / 2 ? winner.MapIndex : loser.MapIndex);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, w, h, _ => false));
        c.ConfigureSinglePlayer(0);
        winner.IsHuman = humanProposer;
        loser.IsHuman = false;
        winner.CapitalStateMapIndex = Reg(0, 0);
        loser.CapitalStateMapIndex = Reg(cols - 1, 0);

        var loserProvinces = new List<int>();
        for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
            if (c.ProvinceMap.ProvinceToNation[p] == (ushort)loser.MapIndex)
                loserProvinces.Add(p);

        if (occupyAll)
            foreach (var p in loserProvinces)
                c.SetProvinceController(p, (ushort)winner.MapIndex);

        var war = new StrategicWar
        {
            Id = 1,
            AttackerId = winner.Id,
            DefenderId = loser.Id,
            StartedDay = 0,
            WarScore = warScore,
            AttackerWarSupport = 0.80f,
            DefenderWarSupport = 0.90f,
            CivilConflict = true
        };
        c.Wars.Add(war);
        c.MarkWarsChanged();

        var fieldProv = loserProvinces[0];
        var formations = Math.Max(4, c.Defines.FactionFightOnFormations * 2);
        for (var i = 0; i < formations; i++)
        {
            c.Armies.Add(new Army
            {
                Id = 501 + i,
                NationId = loser.Id,
                ProvinceId = fieldProv,
                Divisions = 4,
                Strength = 1f,
                Organisation = 1f,
                AssignedWarId = war.Id
            });
        }

        return (c, winner, loser, war, loserProvinces);
    }

    private static void RunHumanAnnexationIgnoresFightsOnWithoutGroundTest()
    {
        var (c, winner, loser, war, loserProvinces) = BuildAnnexFixture(20261128, humanProposer: true, warScore: 100f, occupyAll: true);
        Check(Factions.FightsOnWithoutGround(c, loser),
            "fixture defender has FightsOnWithoutGround == true with active army in the field");

        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id, Unconditional = false };
        foreach (var p in loserProvinces) demand.Provinces.Add(p);

        var block = PeaceConference.BlockReason(c, war, demand);
        Check(block.Length == 0, $"human total annexation is not blocked by army in the field (got: '{block}')");

        var cmd = WrittenPeaceCommand.From(demand);
        c.Commands.Enqueue(cmd);
        c.Commands.ApplyAll(c);
        Check(cmd.Delivered, "WrittenPeaceCommand delivers human total annexation");
        Check(war.Ended, "war ends after human total annexation");
        Check(!c.HasOwnedLand(loser), "annexed defender owns zero provinces");
        Check(loserProvinces.All(p => c.ProvinceMap.ProvinceToNation[p] == (ushort)winner.MapIndex),
            "human winner owns 100% of annexed provinces");
    }

    private static void RunAiAnnexationWaivedOnCollapseOrTotalOccupationTest()
    {
        var (c, winner, loser, war, loserProvinces) = BuildAnnexFixture(20261129, humanProposer: false, warScore: 60f, occupyAll: false);
        Check(Factions.FightsOnWithoutGround(c, loser), "AI fixture defender fights on without ground");

        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id, Unconditional = false };
        foreach (var p in loserProvinces) demand.Provinces.Add(p);

        var earlyBlock = PeaceConference.BlockReason(c, war, demand);
        Check(earlyBlock.Contains("army in the field", StringComparison.OrdinalIgnoreCase) || earlyBlock.Length > 0,
            $"AI vs AI early-war uncollapsed insurgency still refuses total annexation (got: '{earlyBlock}')");

        war.Collapsed = -1;
        war.WarScore = 100f;
        var collapsedBlock = PeaceConference.BlockReason(c, war, demand);
        Check(collapsedBlock.Length == 0,
            $"AI vs AI total annexation is waived when enemy side collapsed without Unconditional (got: '{collapsedBlock}')");

        war.Collapsed = 0;
        foreach (var p in loserProvinces) c.SetProvinceController(p, (ushort)winner.MapIndex);
        var totalOccBlock = PeaceConference.BlockReason(c, war, demand);
        Check(totalOccBlock.Length == 0,
            $"AI vs AI total annexation is waived when defender is totally occupied without Unconditional (got: '{totalOccBlock}')");
    }

    private static void RunPostAnnexationUnitCleanupTest()
    {
        var (c, winner, loser, war, loserProvinces) = BuildAnnexFixture(20261130, humanProposer: true, warScore: 100f, occupyAll: true);
        // Clear capital seat so CapitalCore.Tick alone would skip Fall(c, loser); PeaceConference cleanup must still disband everything.
        loser.CapitalStateMapIndex = 0;
        loser.CapitalProvinceId = 0;
        loser.GovernmentSeatProvinceId = 0;

        c.Fleets.Add(new Fleet { Id = 701, NationId = loser.Id, Name = "Stranded Flotilla", Hulls = 3 });
        c.SupportFormations.Add(new SupportFormation { Id = 801, NationId = loser.Id, TypeId = "artillery", ProvinceId = loserProvinces[0] });

        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var p in loserProvinces) demand.Provinces.Add(p);
        var cmd = WrittenPeaceCommand.From(demand);
        c.Commands.Enqueue(cmd);
        c.Commands.ApplyAll(c);

        Check(cmd.Delivered && war.Ended, "annexation treaty with stranded forces succeeds");
        Check(c.Armies.Count(a => a.NationId == loser.Id) == 0, "zero zombie armies remain after full annexation");
        Check(c.Fleets.Count(f => f.NationId == loser.Id) == 0, "zero stranded fleets remain after full annexation");
        Check(c.SupportFormations.Count(f => f.NationId == loser.Id) == 0, "zero stranded support formations remain after full annexation");
    }

    private static void RunSeparatePeaceHumanCooldownTest()
    {
        var (c, winner, loser, war, loserProvinces) = BuildAnnexFixture(20261201, humanProposer: true, warScore: 80f, occupyAll: true);
        // Add a third nation as a totally-occupied enemy ally (like Belorussia).
        var ally = new Nation
        {
            Id = 2, MapIndex = 943, Tag = "PXC", Name = "Belorussia",
            Treasury = 15f, Population = 3_000_000, Gdp = 18f,
            Stability = 0.50f, WarSupport = 0.40f, IsHuman = false
        };
        c.Nations.Add(ally);
        var allyState = c.States.Last(s => s.NationMapIndex == loser.MapIndex);
        allyState.NationMapIndex = ally.MapIndex;
        allyState.NationId = ally.Tag;
        for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
            if (c.ProvinceMap.ProvinceToState[p] == (ushort)allyState.MapIndex)
            {
                c.ProvinceMap.ProvinceToNation[p] = (ushort)ally.MapIndex;
                c.SetProvinceController(p, (ushort)winner.MapIndex);
            }
        c.MarkMapChanged();
        war.DefenderAllies.Add(ally.Id);
        war.CivilConflict = false;
        c.Defines.SeparatePeaceEnabled = true;
        c.Defines.ForcePeaceOnTotalOccupationOrFullScore = true;
        c.StepOpensNewDay = true;
        void SetDay(int day) => c.RestoreRuntimeState(c.Date, day, false, c.Random.State,
            winner.Id, new[] { winner.Id }, winner.Id, 1, c.MapRevision, true, null, null, null, null, noHumanAuthority: false);
        SetDay(100);

        Check(PeaceNegotiation.IsTotallyOccupiedInWar(c, war, ally), "ally is totally occupied in war");
        foreach (var _ in SeparatePeace.TickStages(c)) { }
        Check(c.PendingEvents.Count == 1, "totally occupied ally sends one separate peace offer to human war leader");

        // Human clicks "They stay in this war to its end" (dismisses the pending event).
        c.PendingEvents.Clear();
        for (var day = 1; day <= 60; day++)
        {
            SetDay(100 + day);
            foreach (var _ in SeparatePeace.TickStages(c)) { }
            if (c.PendingEvents.Count > 0)
                throw new Exception($"FAIL peace-annexation: separate peace popup re-spawned on day +{day} after decline");
        }
        Check(c.PendingEvents.Count == 0, "60 days after declining, separate peace modal does not re-spawn");

        // After SeparatePeaceCooldownDays (120 days), ally may ask again.
        SetDay(100 + Math.Max(30, c.Defines.SeparatePeaceCooldownDays));
        foreach (var _ in SeparatePeace.TickStages(c)) { }
        Check(c.PendingEvents.Count == 1, "after SeparatePeaceCooldownDays elapses, ally may ask again");

        // PeaceOffers.ToPlayerBlockReason also respects interval after refusal even when totally occupied.
        war.RefusedOfferDay = c.ElapsedDays;
        var intervalBlock = PeaceOffers.ToPlayerBlockReason(c, war, false, null, PeaceTerms.WhitePeace);
        Check(intervalBlock == "interval", $"PeaceOffers.ToPlayerBlockReason respects interval after refusal under total occupation (got: '{intervalBlock}')");
    }

    private static void RunUiAndLocalizationContractsTest()
    {
        var root = FindRepoRoot();
        var peaceUi = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "PeaceConferenceUI.cs"));
        var warUi = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "WarLedgerDetailUI.cs"));
        var annexUi = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "PeaceAnnexationUI.cs"));

        Check(peaceUi.Contains("CreatePeaceConferenceAnnexButton", StringComparison.Ordinal) &&
              annexUi.Contains("\"ConferenceAnnex\"", StringComparison.Ordinal) &&
              annexUi.Contains("AnnexEnemyTerritory", StringComparison.Ordinal),
            "PeaceConferenceUI wires ConferenceAnnex and AnnexEnemyTerritory");
        Check(peaceUi.Contains("CommitImmediateCampaignCommand", StringComparison.Ordinal),
            "PeaceConferenceUI commits peace immediately");
        Check(!peaceUi.Contains("new Color(", StringComparison.Ordinal) &&
              !annexUi.Contains("new Color(", StringComparison.Ordinal),
            "PeaceConferenceUI and PeaceAnnexationUI have zero raw new Color(");

        Check(warUi.Contains("AddWarQuickAnnexButton", StringComparison.Ordinal) &&
              annexUi.Contains("\"WarQuickAnnex\"", StringComparison.Ordinal) &&
              annexUi.Contains("ProposeQuickAnnexation", StringComparison.Ordinal),
            "WarLedgerDetailUI wires WarQuickAnnex and ProposeQuickAnnexation");
        Check(annexUi.Contains("CommitImmediateCampaignCommand", StringComparison.Ordinal),
            "WarQuickAnnex commits quick annexation immediately");
        Check(!warUi.Contains("new Color(", StringComparison.Ordinal),
            "WarLedgerDetailUI has zero raw new Color(");

        var enPath = Path.Combine(root, "game", "localization", "english", "zz_453_peace_annex.json");
        var ruPath = Path.Combine(root, "game", "localization", "russian", "zz_453_peace_annex.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU zz_453_peace_annex.json exist");

        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath));
        var ru = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ruPath));
        Check(en is not null && ru is not null && en.Count == ru.Count && en.Count >= 5,
            "EN and RU zz_453_peace_annex.json have matching non-empty key counts");
        foreach (var (k, v) in en)
        {
            Check(!string.IsNullOrWhiteSpace(v), $"EN key '{k}' is non-empty");
            Check(ru.TryGetValue(k, out var rv) && !string.IsNullOrWhiteSpace(rv), $"RU key '{k}' exists and is non-empty");
            if (v.Contains("{0}", StringComparison.Ordinal))
                Check(rv.Contains("{0}", StringComparison.Ordinal), $"RU key '{k}' preserves {{0}}");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
