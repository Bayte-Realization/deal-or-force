using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

static class DiplomacyOverhaulTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL diplomacy-overhaul: " + label);
        Console.WriteLine("PASS diplomacy-overhaul: " + label);
    }

    [TestSuite("--diplomacy-overhaul-only", Order = 2680, IncludeInDefaultRun = true,
        Description = "Wave 5F: DealComposer/TradeStrip scroll & pinned footer, Terms button height & ARMS chip wrapping, modal audit, and 6 new strategic deal clauses (DIP-01 + CONS-03).")]
    public static void Run()
    {
        RunEnclaveCorridorTests();
        RunStrategicClausesScoringAndExecutionTests();
        RunLocalizationAndModalContracts();
    }

    private static (Campaign c, Nation a, Nation b) BuildTwoCountryWorld(bool withEnclave)
    {
        const int cols = 8, rows = 4, block = 8;
        int Reg(int col, int row) => 7001 + col + row * cols;
        var c = new Campaign(20261121) { ScenarioId = "dip_wave5_fixture" };
        var a = new Nation { Id = 0, MapIndex = 901, Tag = "DWA", Name = "Aurelia", Treasury = 120f, PublicDebt = 5f, Population = 25_000_000, Gdp = 500f, Stability = 0.65f, CreditConfidence = 0.70f, WarSupport = 0.55f };
        var b = new Nation { Id = 1, MapIndex = 902, Tag = "DWB", Name = "Borealis", Treasury = 40f, PublicDebt = 30f, Population = 15_000_000, Gdp = 220f, Stability = 0.55f, CreditConfidence = 0.60f, WarSupport = 0.45f };
        c.Nations.Add(a);
        c.Nations.Add(b);

        int OwnerOf(int col, int row)
        {
            if (withEnclave && col == 1 && row == 1) return b.MapIndex; // B's cut-off enclave inside A
            return col < cols / 2 ? a.MapIndex : b.MapIndex;
        }

        for (var r = 0; r < rows; r++)
        for (var col = 0; col < cols; col++)
        {
            var owner = OwnerOf(col, r);
            c.States.Add(new ScenarioState
            {
                MapIndex = Reg(col, r), NationMapIndex = owner,
                NationId = owner == a.MapIndex ? a.Tag : b.Tag,
                Id = $"dw_{col}_{r}", Name = $"District {col}-{r}", Population = 1_000_000
            });
        }

        var w = cols * block;
        var h = rows * block;
        var cells = new ushort[w * h];
        var owners = new ushort[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            cells[y * w + x] = (ushort)Reg(x / block, y / block);
            owners[y * w + x] = (ushort)OwnerOf(x / block, y / block);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, w, h, _ => false));
        c.ConfigureSinglePlayer(0);
        foreach (var n in c.Nations) n.IsHuman = false;
        a.CapitalStateMapIndex = Reg(0, 0);
        b.CapitalStateMapIndex = Reg(cols - 1, 0);
        return (c, a, b);
    }

    private static void RunEnclaveCorridorTests()
    {
        var (c, a, b) = BuildTwoCountryWorld(withEnclave: true);
        Check(BorderDiplomacy.CountIsolatedPockets(c, b, a) == 1, "CONS-03 detects 1 isolated enclave of B inside A");
        Check(BorderDiplomacy.HasCorridorNeed(c, a, b), "CONS-03 identifies corridor need across shared/enclave border");
        Check(Ultimatum.ClauseRefusal(c, a, b, "enclave_corridor").Length == 0, "enclave corridor clause is allowed when border/enclave touches");
        var relief = BorderDiplomacy.CorridorReliefValue(c, b, a);
        Check(relief > 15f, $"enclave corridor yields strong AI relief value ({relief:0.0} > 15)");

        var rel = CampaignDiplomacy.Get(c, a.Id, b.Id);
        rel.Tension = 20f;
        var fpBefore = CampaignFingerprint.Of(c);
        var corridorDeal = new Ultimatum.Deal(
            new Ultimatum.Side { EnclaveCorridor = true },
            new Ultimatum.Side());
        var preview = Ultimatum.AssessDeal(c, a, b, corridorDeal);
        Check(preview.Valid && preview.Acceptable && preview.ToValuation.Borders > 10f,
            $"AI recipient values enclave corridor positively in Borders ({preview.ToValuation.Borders:+0.0})");
        var weights = Ultimatum.ExplainClauseWeights(c, b, a, corridorDeal);
        Check(weights.Any(w => w.Points > 10f) && DealTally.FormatClauseWeights(weights).Length > 0,
            "ExplainClauseWeights and DealTally.FormatClauseWeights expose line-by-line corridor score");
        Check(CampaignFingerprint.Of(c) == fpBefore, "previewing enclave corridor deal leaves CampaignFingerprint bit-identical");

        var cmd = new ProposeDealCommand { IssuerId = a.Id, ToId = b.Id, FromEnclaveCorridor = true };
        cmd.Apply(c);
        Check(cmd.Outcome.Accepted, "ProposeDealCommand executes EnclaveCorridor deal cleanly");
        Check(rel.GrantsMilitaryAccess(a.Id) && rel.Tension < 20f,
            "EnclaveCorridor grants transit access and lowers bilateral tension");
        Check(Ultimatum.ClauseRefusal(c, a, b, "enclave_corridor").Length > 0,
            "EnclaveCorridor enforces cooldown after signing");
    }

    private static void RunStrategicClausesScoringAndExecutionTests()
    {
        var (c, a, b) = BuildTwoCountryWorld(withEnclave: false);
        var firstTech = TechnologyCatalog.All.FirstOrDefault();
        if (firstTech is not null)
        {
            a.Technologies.Add(firstTech.Id);
            b.CurrentResearch = firstTech.Id;
            b.ResearchProgress[firstTech.Id] = 0f;
        }

        // 1. Preview Tech Transfer + Base Lease + Sovereign Credit + Chokepoint + Ceasefire Mediation
        var fpBefore = CampaignFingerprint.Of(c);
        var richDeal = new Ultimatum.Deal(
            new Ultimatum.Side { TechnologyTransfer = true, SovereignCredit = true },
            new Ultimatum.Side { BaseAccessLease = true })
        {
            ChokepointTransit = true,
            CeasefireMediation = true
        };
        var creditAmt = Ultimatum.SovereignCreditAmount(a, b);
        Check(creditAmt >= 0.25f, $"SovereignCreditAmount computes positive credit ({creditAmt:0.00}B)");
        var assessment = Ultimatum.AssessDeal(c, a, b, richDeal);
        Check(assessment.Valid && assessment.Acceptable,
            $"balanced strategic package (tech+credit+chokepoint+mediation for base lease) is accepted (net={assessment.ToValue:+0.0})");
        Check(!Ultimatum.IsTradeOnly(richDeal), "strategic clause deals are not classified as trade-only");
        var summary = Ultimatum.DealSummary(c, a, b, richDeal);
        Check(summary.Length > 20, "DealSummary describes all strategic clauses");
        Check(CampaignFingerprint.Of(c) == fpBefore, "assessing multi-clause strategic deal never mutates CampaignFingerprint");

        // 2. Mutual exclusivity guard on SovereignCredit
        var doubleCredit = new Ultimatum.Deal(
            new Ultimatum.Side { SovereignCredit = true },
            new Ultimatum.Side { SovereignCredit = true });
        Check(!Ultimatum.ValidateDeal(c, a, b, doubleCredit, out _),
            "both sides cannot simultaneously offer SovereignCredit");

        // 3. DealOffer copy round-trip preserves all 6 Wave 5F clause flags
        var copied = DealNegotiations.Copy(new DealOffer
        {
            Id = 1, FromId = a.Id, ToId = b.Id, CreatedDay = 1, ExpiresDay = 30,
            From = new Ultimatum.Side { EnclaveCorridor = true, TechnologyTransfer = true, BaseAccessLease = true, SovereignCredit = true },
            To = new Ultimatum.Side(),
            ChokepointTransit = true, CeasefireMediation = true
        });
        Check(copied.From.EnclaveCorridor && copied.From.TechnologyTransfer &&
              copied.From.BaseAccessLease && copied.From.SovereignCredit &&
              copied.ChokepointTransit && copied.CeasefireMediation,
            "DealNegotiations.Copy preserves all 6 Wave 5F strategic deal clauses");

        // 4. Execute through ProposeDealCommand and verify mechanical effects & money conservation
        var aTreasuryBefore = a.Treasury;
        var bDebtBefore = b.PublicDebt;
        var aAirXpBefore = a.AirExperience;
        var aNavalXpBefore = a.NavalExperience;
        var rel = CampaignDiplomacy.Get(c, a.Id, b.Id);
        rel.Tension = 25f;

        var cmd = new ProposeDealCommand
        {
            IssuerId = a.Id, ToId = b.Id,
            FromTechnologyTransfer = true, FromSovereignCredit = true,
            ToBaseAccessLease = true,
            ChokepointTransit = true, CeasefireMediation = true
        };
        cmd.Apply(c);
        Check(cmd.Outcome.Accepted, "ProposeDealCommand signs and applies multi-clause strategic package");
        Check(MathF.Abs((aTreasuryBefore - a.Treasury) - creditAmt) < 0.02f &&
              MathF.Abs((bDebtBefore - b.PublicDebt) - MathF.Min(bDebtBefore, creditAmt)) < 0.02f,
            "SovereignCredit deducts giver treasury and reduces receiver PublicDebt with money conservation");
        Check(rel.GrantsMilitaryAccess(b.Id) && a.NavalExperience > aNavalXpBefore && a.AirExperience > aAirXpBefore,
            "BaseAccessLease grants military access and Naval/Air XP to lessee");
        Check(rel.TruceUntil >= c.ElapsedDays + 730 && rel.TalksUntilDay >= c.ElapsedDays + 180,
            "CeasefireMediation establishes 730-day protected truce and 180-day staff talks");
        Check(rel.Tension < 10f, "ChokepointTransit + CeasefireMediation measurably reduce bilateral tension");
        if (firstTech is not null)
            Check(b.ResearchProgress.GetValueOrDefault(firstTech.Id) > 0f,
                "TechnologyTransfer boosts receiver active research progress");
    }

    private static void RunLocalizationAndModalContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // 1. EN + RU 98_diplomacy_wave5.json parity
        var enPath = Path.Combine(dataRoot, "localization", "english", "98_diplomacy_wave5.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "98_diplomacy_wave5.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 98_diplomacy_wave5.json exist");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 25 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 98_diplomacy_wave5.json have identical key counts");
        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU key '{k}' non-empty");
            Check(!string.Equals(enVal, ruVal, StringComparison.Ordinal), $"RU key '{k}' is translated");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }

        // 2. Modal & UI layout regression guards
        string UiSrc(string name) => File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", name));

        var dealSrc = UiSrc("DealComposerUI.cs");
        Check(dealSrc.Contains("Name = \"DealBodyScroll\"", StringComparison.Ordinal) &&
              dealSrc.Contains("Name = \"DealBodyStack\"", StringComparison.Ordinal) &&
              dealSrc.Contains("BuildDealTradeStrip(bodyStack,", StringComparison.Ordinal) &&
              dealSrc.Contains("DealStrategicPanel", StringComparison.Ordinal) &&
              dealSrc.Contains("SizeFlags.ShrinkEnd", StringComparison.Ordinal) &&
              dealSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal) &&
              !dealSrc.Contains("ClipText = true", StringComparison.Ordinal) &&
              !dealSrc.Contains("new Vector2(280", StringComparison.Ordinal) &&
              !dealSrc.Contains("new Color(", StringComparison.Ordinal),
            "DealComposerUI places TradeStrip+StrategicPanel inside DealBodyScroll above pinned ShrinkEnd DealSettlementFooter with Tabular font and zero raw Color");

        var tradeStripSrc = UiSrc("DealTradeTermsUI.cs");
        Check(tradeStripSrc.Contains("DealChipHeight = 30", StringComparison.Ordinal) &&
              tradeStripSrc.Contains("ClipContents = true", StringComparison.Ordinal) &&
              tradeStripSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal) &&
              !tradeStripSrc.Contains("Colors.White", StringComparison.Ordinal) &&
              !tradeStripSrc.Contains("new Color(", StringComparison.Ordinal),
            "DealTradeTermsUI uses compact 30px height, ClipContents, Tabular labels, and Ui.Colour tokens");

        var termsRowSrc = UiSrc("TradeTermsCountryUI.cs");
        Check(termsRowSrc.Contains("deal.btn.custom_trade_terms", StringComparison.Ordinal) &&
              termsRowSrc.Contains("icon_trade.png", StringComparison.Ordinal) &&
              !termsRowSrc.Contains(", 0, 48)", StringComparison.Ordinal),
            "TradeTermsCountryUI uses Custom trade terms label, trade icon, and compact sibling height (never 48px)");

        var dipScreenSrc = UiSrc("DiplomacyScreenUI.cs");
        Check(dipScreenSrc.Contains("DiplomacyDetailMargin", StringComparison.Ordinal) &&
              dipScreenSrc.Contains("new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true }", StringComparison.Ordinal) &&
              dipScreenSrc.Contains("new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true }", StringComparison.Ordinal),
            "DiplomacyScreenUI bounds detail margin, Field panels, and DiplomacyChips HFlowContainer with ClipContents");

        var ultSrc = UiSrc("UltimatumComposerUI.cs");
        var warDeclSrc = UiSrc("WarDeclarationUI.cs");
        var inboxSrc = UiSrc("DealInboxUI.cs");
        var sepPeaceSrc = UiSrc("WarSeparatePeaceUI.cs");
        Check(ultSrc.Contains("SizeFlags.ShrinkEnd", StringComparison.Ordinal) && ultSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "UltimatumComposerUI pins footer and uses Tabular figures");
        Check(warDeclSrc.Contains("WarDeclarationFooter", StringComparison.Ordinal) && warDeclSrc.Contains("SizeFlags.ShrinkEnd", StringComparison.Ordinal),
            "WarDeclarationUI pins WarDeclarationFooter with ClipContents");
        Check(inboxSrc.Contains("DealInboxActions", StringComparison.Ordinal) && inboxSrc.Contains("SizeFlags.ShrinkEnd", StringComparison.Ordinal),
            "DealInboxUI pins DealInboxActions with ClipContents");
        Check(sepPeaceSrc.Contains("new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true }", StringComparison.Ordinal),
            "WarSeparatePeaceUI wraps separate-peace action rows in ClipContents HFlowContainer");
    }
}
