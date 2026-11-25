using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class BilateralPeaceTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL bilateral-peace: " + label);
        Console.WriteLine("PASS bilateral-peace: " + label);
    }

    [TestSuite("--bilateral-peace-only", Order = 2698, IncludeInDefaultRun = true,
        Description = "Wave 6J & Wave M1: bilateral give-and-take peace conference (demands + offers), BilateralPeaceCommand, validation, EN/RU parity, touch-scaled ShellLayout, IME focus guard, and Sovereign Brass Touch-Mode bar contracts.")]
    public static void Run()
    {
        RunBilateralOfferOffsetsDemandTest();
        RunLosingSideConcessionPeaceAndExecutionTest();
        RunValidationRefusalsAndFingerprintInvarianceTest();
        RunRepeatedLandPricingTest();
        RunLocalizationAndWaveM1UiContractsTest();
    }

    private static void RunRepeatedLandPricingTest()
    {
        var (campaign, buyer, holder, war) = BuildWarWorld(20261124, warScore: 15f, block: 24);
        campaign.Defines.PeaceResistanceShare = 1f;
        campaign.Defines.PeaceCostReunificationFactor = 0.2f;
        var region = campaign.StatesOf(holder).First(state =>
            campaign.ProvinceMap.ProvincesOfState(state.MapIndex).Length >= 2);
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = buyer.Id };
        foreach (var province in campaign.ProvinceMap.ProvincesOfState(region.MapIndex))
            demand.Provinces.Add(province);

        var ledger = new PeaceLedger(campaign, war, buyer.Id);
        float IndividualPrices()
        {
            var total = 0f;
            foreach (var province in demand.Provinces) total += ledger.Price(province).Cost;
            return total;
        }
        var initial = ledger.DemandCost(demand);
        Check(initial == IndividualPrices(),
            "a multi-province total agrees with the individual province readouts");

        region.CoreTags.Add(buyer.Tag);
        var revised = ledger.DemandCost(demand);
        Check(revised < initial && revised == IndividualPrices(),
            "a later bill on the same ledger sees new regional claims and still matches the readouts");
    }

    private static (Campaign c, Nation a, Nation b, StrategicWar war) BuildWarWorld(int seed, float warScore, int block = 8)
    {
        const int cols = 8, rows = 4;
        int Reg(int col, int row) => 7501 + col + row * cols;
        var c = new Campaign(seed) { ScenarioId = "bilateral_peace_fixture" };
        var a = new Nation
        {
            Id = 0, MapIndex = 911, Tag = "BPA", Name = "Aurelia",
            Treasury = 80f, Population = 24_000_000, Gdp = 420f,
            Stability = 0.65f, WarSupport = 0.65f
        };
        var b = new Nation
        {
            Id = 1, MapIndex = 912, Tag = "BPB", Name = "Borealis",
            Treasury = 60f, Population = 20_000_000, Gdp = 360f,
            Stability = 0.60f, WarSupport = 0.70f
        };
        c.Nations.Add(a);
        c.Nations.Add(b);

        for (var r = 0; r < rows; r++)
        for (var col = 0; col < cols; col++)
        {
            var isA = col < cols / 2;
            c.States.Add(new ScenarioState
            {
                MapIndex = Reg(col, r),
                NationMapIndex = isA ? a.MapIndex : b.MapIndex,
                NationId = isA ? a.Tag : b.Tag,
                Id = $"bp_{col}_{r}",
                Name = $"Province {col}-{r}",
                Population = 1_500_000
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
            owners[y * w + x] = (ushort)(col < cols / 2 ? a.MapIndex : b.MapIndex);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, w, h, _ => false));
        c.ConfigureSinglePlayer(0);
        a.IsHuman = true;
        b.IsHuman = false;
        a.CapitalStateMapIndex = Reg(0, 0);
        b.CapitalStateMapIndex = Reg(cols - 1, 0);

        var war = new StrategicWar
        {
            Id = 1,
            AttackerId = a.Id,
            DefenderId = b.Id,
            StartedDay = 0,
            WarScore = warScore,
            AttackerWarSupport = 0.70f,
            DefenderWarSupport = 0.85f,
            ClaimedStateMapIndex = Reg(4, 0)
        };
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, a.Id, b.Id).Stance = DiplomaticStance.AtWar;
        return (c, a, b, war);
    }

    private static void RunBilateralOfferOffsetsDemandTest()
    {
        var (c, a, b, war) = BuildWarWorld(20261122, warScore: 15f);
        var offerable = PeaceConference.OfferableStates(c, war, a.Id);
        Check(offerable.Count > 0, "OfferableStates finds non-capital regions owned by proposer");
        Check(offerable.All(s => s.MapIndex != a.CapitalStateMapIndex), "OfferableStates excludes proposer capital");

        var demand = new PeaceDemand
        {
            WarId = war.Id,
            ProposerId = a.Id,
            Demilitarise = true,
            Humiliate = true
        };
        var gross = PeaceConference.DemandCost(c, war, demand);
        var budget = PeaceConference.Budget(c, war, a.Id);
        Check(gross > budget, $"gross demand ({gross:0.0}) exceeds raw war budget ({budget:0.0}) before offers");
        Check(PeaceConference.BlockReason(c, war, demand).Length > 0, "over-budget unilateral demand is blocked");

        var offerState = offerable[0];
        foreach (var p in PeaceConference.ProvincesOfState(c.ProvinceMap, offerState.MapIndex))
            demand.OfferProvinces.Add(p);
        demand.OfferReparations = true;
        demand.OfferTradeConcessions = true;
        demand.OfferDropClaims = true;
        demand.OfferLongTruce = true;

        var credit = PeaceConference.OfferCredit(c, war, demand);
        var netCost = PeaceConference.Cost(c, war, demand);
        var balance = PeaceConference.NetBalance(c, war, demand);
        Check(credit > 20f, $"bilateral offers generate meaningful warscore credit (+{credit:0.0})");
        Check(MathF.Abs(netCost - (gross - credit)) < 0.01f, "Cost equals DemandCost minus OfferCredit");
        Check(balance >= 0f, $"offers offset excess demand to a balanced deal (net balance {balance:0.0} >= 0)");
        Check(PeaceConference.BlockReason(c, war, demand).Length == 0,
            "balanced bilateral give-and-take treaty passes BlockReason");
    }

    private static void RunLosingSideConcessionPeaceAndExecutionTest()
    {
        var (c, a, b, war) = BuildWarWorld(20261123, warScore: -25f);
        var offerable = PeaceConference.OfferableStates(c, war, a.Id);
        var offerState = offerable[0];
        var offerProvs = PeaceConference.ProvincesOfState(c.ProvinceMap, offerState.MapIndex);
        Check(offerProvs.Count > 0, "offered non-capital state has provinces");

        var demand = new PeaceDemand
        {
            WarId = war.Id,
            ProposerId = a.Id,
            OfferReparations = true,
            OfferTradeConcessions = true,
            OfferDropClaims = true,
            OfferLongTruce = true
        };
        foreach (var p in offerProvs) demand.OfferProvinces.Add(p);

        var balance = PeaceConference.NetBalance(c, war, demand);
        var block = PeaceConference.BlockReason(c, war, demand);
        Check(balance >= 0f && block.Length == 0,
            $"losing side can buy peace with bilateral concessions (balance={balance:0.0}, block='{block}')");

        var treasuryBeforeA = a.Treasury;
        var treasuryBeforeB = b.Treasury;
        var totalMoneyBefore = treasuryBeforeA + treasuryBeforeB;

        var cmd = BilateralPeaceCommand.From(demand);
        c.Commands.Enqueue(cmd);
        c.Commands.ApplyAll(c);

        Check(cmd.Delivered, $"BilateralPeaceCommand delivered cleanly (note='{cmd.Note}')");
        Check(war.Ended, "war ended after bilateral peace resolution");
        Check(offerProvs.All(p => c.ProvinceMap.ProvinceToNation[p] == (ushort)b.MapIndex),
            "offered provinces transferred ownership to opponent");
        Check(a.Treasury < treasuryBeforeA && b.Treasury > treasuryBeforeB,
            "OfferReparations transferred money from proposer to opponent");
        Check(MathF.Abs((a.Treasury + b.Treasury) - totalMoneyBefore) < 0.01f,
            "OfferReparations strictly conserved total treasury");
        Check(war.ClaimedStateMapIndex == -1, "OfferDropClaims cleared war claim");
        var rel = CampaignDiplomacy.Get(c, a.Id, b.Id);
        Check(rel.Stance == DiplomaticStance.Truce && rel.TruceUntil == c.ElapsedDays + 365 * 10,
            "OfferLongTruce established a 10-year truce");
        Check(AgreementSystem.Signed(rel, AgreementKind.TradeAgreement),
            "OfferTradeConcessions signed a bilateral TradeAgreement");
        Check(!string.IsNullOrWhiteSpace(war.PeaceSummary), "war PeaceSummary recorded bilateral clauses");
    }

    private static void RunValidationRefusalsAndFingerprintInvarianceTest()
    {
        var (c, a, b, war) = BuildWarWorld(20261124, warScore: 10f);
        var fpBefore = CampaignFingerprint.Of(c);

        // 1. Contradictory terms
        var contra = new PeaceDemand { WarId = war.Id, ProposerId = a.Id, Reparations = true, OfferReparations = true };
        Check(PeaceConference.BlockReason(c, war, contra).Length > 0, "contradictory demand+offer is refused");

        // 2. Offering own capital province
        var capProv = PeaceConference.ProvincesOfState(c.ProvinceMap, a.CapitalStateMapIndex)[0];
        var capOffer = new PeaceDemand { WarId = war.Id, ProposerId = a.Id };
        capOffer.OfferProvinces.Add(capProv);
        Check(PeaceConference.BlockReason(c, war, capOffer).Length > 0, "offering capital province is refused");

        // 3. Offering enemy province as own concession
        var enemyProv = PeaceConference.ProvincesOfState(c.ProvinceMap, b.CapitalStateMapIndex)[0];
        var enemyOffer = new PeaceDemand { WarId = war.Id, ProposerId = a.Id };
        enemyOffer.OfferProvinces.Add(enemyProv);
        Check(PeaceConference.BlockReason(c, war, enemyOffer).Length > 0, "offering enemy province is refused");

        // 4. Low treasury with OfferReparations
        var savedTreasury = a.Treasury;
        a.Treasury = 0.01f;
        var poorOffer = new PeaceDemand { WarId = war.Id, ProposerId = a.Id, OfferReparations = true };
        Check(PeaceConference.BlockReason(c, war, poorOffer).Length > 0, "low treasury blocks OfferReparations");
        a.Treasury = savedTreasury;

        // 5. Read-only preview calls never mutate CampaignFingerprint
        _ = PeaceConference.OfferableStates(c, war, a.Id);
        _ = PeaceConference.DemandCost(c, war, contra);
        _ = PeaceConference.OfferCredit(c, war, contra);
        _ = PeaceConference.NetBalance(c, war, contra);
        _ = PeaceConference.Round(c, war, contra);
        Check(CampaignFingerprint.Of(c) == fpBefore,
            "previewing bilateral peace costs, offers, and validation never changes CampaignFingerprint");
    }

    private static void RunLocalizationAndWaveM1UiContractsTest()
    {
        var root = FindRepoRoot();
        var enPath = Path.Combine(root, "game", "localization", "english", "99_bilateral_peace_wave6.json");
        var ruPath = Path.Combine(root, "game", "localization", "russian", "99_bilateral_peace_wave6.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN & RU bilateral peace loc files exist");

        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath))!;
        var ru = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ruPath))!;
        Check(en.Count >= 25 && en.Count == ru.Count, $"EN ({en.Count}) and RU ({ru.Count}) key counts match");
        var phRegex = new Regex(@"\{\d+(?::[^}]+)?\}");
        foreach (var (k, v) in en)
        {
            Check(ru.TryGetValue(k, out var rv), $"RU contains key '{k}'");
            Check(!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(rv), $"non-empty EN/RU for '{k}'");
            var enPh = phRegex.Matches(v).Select(m => m.Value).OrderBy(x => x).ToArray();
            var ruPh = phRegex.Matches(rv!).Select(m => m.Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity for '{k}'");
        }

        // Wave M1 touch-scaled ShellLayout check (desktop 100% preserved, touch scaled comfortably)
        var desktop = ShellLayout.ForViewport(1920, 1080, atWar: false, touchMode: false);
        Check(!desktop.TouchMode && desktop.DatePlate.W > 300f && desktop.DatePlate.W < 470f &&
              desktop.DatePlate.H > 50f && desktop.DatePlate.H < 98f,
            $"desktop ShellLayout stays 100% unchanged when touchMode=false ({desktop.DatePlate.W}x{desktop.DatePlate.H})");
        var touch = ShellLayout.ForViewport(1280, 720, atWar: false, touchMode: true);
        Check(touch.TouchMode && touch.DatePlate.W >= 430f && touch.DatePlate.W <= 470f &&
              touch.DatePlate.H >= 98f && touch.DatePlate.H <= 104f &&
              touch.RailButtonSize >= 48f && touch.MapButtonSize >= 46f,
            $"touch ShellLayout meets mobile ergonomics (DatePlate {touch.DatePlate.W}x{touch.DatePlate.H}, rail={touch.RailButtonSize}, map={touch.MapButtonSize})");

        // Wave M1 static contracts: IME guard, Touch-Mode bar, Rule #88 (<800 lines), pure sim
        var uiTheme = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "UiTheme.cs"));
        Check(uiTheme.Contains("FocusInputIfDesktop") && uiTheme.Contains("VirtualKeyboardShowOnFocus = false"),
            "UiTheme exposes FocusInputIfDesktop and suppresses auto virtual keyboard on touch");

        var mapBarPath = Path.Combine(root, "Scripts", "ui", "Shell", "ShellMapBarView.cs");
        var mapBarLines = File.ReadAllLines(mapBarPath);
        var mapBarText = string.Join("\n", mapBarLines);
        Check(mapBarLines.Length < 800, $"ShellMapBarView.cs stays under 800 lines ({mapBarLines.Length})");
        var touchBarViewPath = Path.Combine(root, "Scripts", "ui", "Shell", "ShellTouchModeBarView.cs");
        var touchBarViewText = File.ReadAllText(touchBarViewPath);
        var orchestratorText = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "Shell", "ShellOrchestrator.cs"));
        var touchMini = ShellLayout.MinimapRect(touch);
        Check(!mapBarText.Contains("ShellTouchModeBar", StringComparison.Ordinal) &&
              touchBarViewText.Contains("ShellTouchModeBar", StringComparison.Ordinal) &&
              orchestratorText.Contains("ShellTouchModeBarView", StringComparison.Ordinal) &&
              touch.TouchModeBar.Right <= touch.DatePlate.X &&
              !touch.TouchModeBar.Intersects(touchMini),
            "ShellTouchModeBarView + ShellOrchestrator dock the Sovereign Brass touch-mode bar beside DatePlate with Minimap unobstructed");

        var peaceUi = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "PeaceConferenceUI.cs"));
        Check(peaceUi.Contains("PeaceBilateralGrid") && peaceUi.Contains("BilateralPeaceCommand") &&
              peaceUi.Contains("PresentationFonts.Tabular()"),
            "PeaceConferenceUI renders two-column bilateral grid with Tabular numbers and BilateralPeaceCommand");

        var peaceSim = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "PeaceConference.cs"));
        var layoutSim = File.ReadAllText(Path.Combine(root, "Scripts", "Simulation", "Shell", "ShellLayout.cs"));
        Check(!peaceSim.Contains("using Godot") && !layoutSim.Contains("using Godot"),
            "PeaceConference.cs and ShellLayout.cs remain 100% free of Godot references");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
