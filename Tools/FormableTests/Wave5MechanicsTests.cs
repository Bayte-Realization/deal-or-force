using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Verification suite for Wave 5B mechanics &amp; content:
/// POL-01 (Dynamic Political Population Cleavages),
/// WAR-02 (Nuclear Doctrines, Triad Upkeep &amp; Bilateral Arms Control Treaties),
/// and Wave 5 Geopolitical Events, Decisions &amp; EN+RU Localization.
/// </summary>
static class Wave5MechanicsTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL wave5-mechanics: " + label);
        Console.WriteLine("PASS wave5-mechanics: " + label);
    }

    [TestSuite("--wave5-mechanics-only", Order = 2660, IncludeInDefaultRun = true,
        Description = "POL-01 political cleavages, WAR-02 nuclear doctrines & arms control, and Wave 5 content.")]
    public static void Run()
    {
        TestPol01CleavagesNormalizationAndDrift();
        TestPol01SplitMergeAndCrisisHook();
        TestWar02DoctrinesUpkeepAndNoFirstUse();
        TestWar02ArmsControlWarFractureAndEraGating();
        TestWave5SaveFingerprintAndNetworkCodec();
        TestWave5ContentAndLocalization();
        TestWave5HFormablesMovementsFlagsAndUiGuard();
        TestWave6DoctrineExpansion();
        Console.WriteLine("WAVE5_MECHANICS_TESTS_OK");
    }

    private static (Campaign Campaign, Nation Alpha, Nation Beta, ScenarioState CapitalA, ScenarioState PeripheryA, ScenarioState CapitalB)
        BuildFixture(int seed = 20261122)
    {
        var c = new Campaign(seed) { ScenarioId = "wave5-fixture" };
        var alpha = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "USA", Name = "Alpha Republic", IsHuman = true,
            Population = 80_000_000, Gdp = 12_000f, Treasury = 400f, Stability = 0.85f,
            StandardOfLiving = 0.80f, MarketAccess = 0.90f, TaxRate = 0.22f, MilitarySpending = 0.03f,
            PoliticalPoints = 120f, CapitalStateMapIndex = 10, CapitalProvinceId = 1,
            Ideology = "liberal_democracy", Government = "parliamentary_republic", OutputScale = 1f
        };
        var beta = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "RUS", Name = "Beta Federation", IsHuman = false,
            Population = 70_000_000, Gdp = 9_000f, Treasury = 300f, Stability = 0.78f,
            StandardOfLiving = 0.72f, MarketAccess = 0.82f, TaxRate = 0.24f, MilitarySpending = 0.04f,
            PoliticalPoints = 120f, CapitalStateMapIndex = 20, CapitalProvinceId = 2,
            Ideology = "conservatism", Government = "presidential_republic", OutputScale = 1f
        };
        c.Nations.Add(alpha);
        c.Nations.Add(beta);

        var capA = new ScenarioState
        {
            Id = "alp_metro", MapIndex = 10, NationMapIndex = alpha.MapIndex, Name = "Alpha Metro",
            Population = 50_000_000, Literacy = 0.94f, Urbanization = 0.88f, StandardOfLiving = 0.86f, Radicalism = 0.04f
        };
        var perA = new ScenarioState
        {
            Id = "alp_frontier", MapIndex = 11, NationMapIndex = alpha.MapIndex, Name = "Alpha Frontier",
            Population = 30_000_000, Literacy = 0.45f, Urbanization = 0.22f, StandardOfLiving = 0.42f, Radicalism = 0.52f
        };
        var capB = new ScenarioState
        {
            Id = "bet_metro", MapIndex = 20, NationMapIndex = beta.MapIndex, Name = "Beta Metro",
            Population = 70_000_000, Literacy = 0.86f, Urbanization = 0.76f, StandardOfLiving = 0.72f, Radicalism = 0.08f
        };
        c.States.Add(capA);
        c.States.Add(perA);
        c.States.Add(capB);
        c.InvalidateStateIndex();
        c.SetPlayer(alpha.Id);
        c.AttachProvinceTopology(ProvinceTopology.Build(
            new ushort[] { 10, 20 },
            new ushort[] { 1, 2 }, 2, 1, _ => false));
        return (c, alpha, beta, capA, perA, capB);
    }

    private static void TestPol01CleavagesNormalizationAndDrift()
    {
        var (c, alpha, _, capA, perA, _) = BuildFixture();
        Check(capA.PoliticalBlocs is null, "unreviewed state starts with null PoliticalBlocs (zero legacy save bloat)");

        var fpUnreviewed = CampaignFingerprint.Of(c);
        var unreviewedSummary = PoliticalCohorts.NationalBlocs(c, alpha);
        Check(!unreviewedSummary.Reviewed && PoliticalCohorts.CrisisPressureContribution(c, alpha) == 0f,
            "unreviewed nation reports Reviewed=false and 0 crisis pressure");
        Check(CampaignFingerprint.Of(c) == fpUnreviewed, "read-only NationalBlocs never mutates fingerprint");

        // Initial review seeds normalized 5-bloc arrays
        PoliticalCohorts.Review(c, alpha, 1);
        Check(capA.PoliticalBlocs is { Length: 5 } && perA.PoliticalBlocs is { Length: 5 },
            "Review seeds 5-bloc PoliticalBlocs arrays");

        var capSum = capA.PoliticalBlocs.Sum();
        var perSum = perA.PoliticalBlocs.Sum();
        Check(MathF.Abs(capSum - 1f) < 0.0005f && MathF.Abs(perSum - 1f) < 0.0005f,
            $"bloc shares strictly normalize to 1.0 (cap={capSum:0.0000}, per={perSum:0.0000})");
        Check(capA.PoliticalBlocs.All(v => v > 0f && float.IsFinite(v)), "all bloc shares stay strictly positive and finite");

        var fpSeeded = CampaignFingerprint.Of(c);
        Check(fpSeeded != fpUnreviewed, "seeding PoliticalBlocs changes CampaignFingerprint");

        // Socioeconomic differentiation: affluent metro vs stressed frontier
        Check(capA.PoliticalBlocs[(int)PoliticalBloc.Reformist] > perA.PoliticalBlocs[(int)PoliticalBloc.Reformist],
            "literate urban capital holds higher Reformist share than poor frontier");
        Check(perA.PoliticalBlocs[(int)PoliticalBloc.Regionalist] > capA.PoliticalBlocs[(int)PoliticalBloc.Regionalist],
            "poor radical frontier holds higher Regionalist share than capital");

        // Drift under sustained wartime & shortage shifts Security + Regionalist upward
        var war = new StrategicWar { Id = 1, AttackerId = alpha.Id, DefenderId = 1, StartedDay = 100 };
        c.Wars.Add(war);
        alpha.ShortageIndex = 0.45f;
        alpha.MilitarySpending = 0.14f;
        var secBefore = capA.PoliticalBlocs[(int)PoliticalBloc.Security];
        PoliticalCohorts.Review(c, alpha, 90);
        Check(capA.PoliticalBlocs[(int)PoliticalBloc.Security] > secBefore,
            $"wartime + high military spending drifts Security bloc upward ({secBefore:0.000} -> {capA.PoliticalBlocs[(int)PoliticalBloc.Security]:0.000})");
        Check(MathF.Abs(capA.PoliticalBlocs.Sum() - 1f) < 0.0005f, "drifted blocs remain normalized to 1.0");
    }

    private static void TestPol01SplitMergeAndCrisisHook()
    {
        var (c, alpha, _, capA, perA, _) = BuildFixture();
        PoliticalCohorts.Review(c, alpha, 1);

        // Prosperous stable nation gets ZERO cleavage crisis contribution
        capA.Radicalism = 0.02f;
        perA.StandardOfLiving = 0.82f;
        perA.Radicalism = 0.02f;
        alpha.Stability = 0.88f;
        PoliticalCohorts.Review(c, alpha, 200);
        Check(PoliticalCohorts.CrisisPressureContribution(c, alpha) == 0f,
            "prosperous stable state receives 0 cleavage crisis penalty");

        // Split conservation (CopyBlocs)
        var splitChild = new ScenarioState { Id = "split_child", MapIndex = 99, Name = "Split Child", Population = 10_000_000 };
        PoliticalCohorts.CopyBlocs(capA, splitChild);
        Check(splitChild.PoliticalBlocs is { Length: 5 } && MathF.Abs(splitChild.PoliticalBlocs.Sum() - 1f) < 0.0005f,
            "CopyBlocs preserves normalized distribution on state split");
        for (var i = 0; i < 5; i++)
            Check(MathF.Abs(splitChild.PoliticalBlocs[i] - capA.PoliticalBlocs[i]) < 0.0001f, $"CopyBlocs matches bloc {i}");

        // Population-weighted merge conservation (MergeBlocs)
        var target = new ScenarioState { PoliticalBlocs = new[] { 0.50f, 0.10f, 0.20f, 0.10f, 0.10f } };
        var absorbed = new ScenarioState { PoliticalBlocs = new[] { 0.10f, 0.10f, 0.20f, 0.10f, 0.50f } };
        PoliticalCohorts.MergeBlocs(target, absorbed, 3_000_000, 1_000_000);
        Check(MathF.Abs(target.PoliticalBlocs[0] - 0.40f) < 0.001f && MathF.Abs(target.PoliticalBlocs[4] - 0.20f) < 0.001f,
            "MergeBlocs computes exact population-weighted bloc blend");
        Check(MathF.Abs(target.PoliticalBlocs.Sum() - 1f) < 0.0005f, "MergeBlocs preserves sum == 1.0");

        // Fractured low-stability state receives bounded positive crisis pressure & mood friction
        alpha.Stability = 0.35f;
        capA.PoliticalBlocs = new[] { 0.10f, 0.12f, 0.13f, 0.15f, 0.50f };
        perA.PoliticalBlocs = new[] { 0.08f, 0.12f, 0.10f, 0.15f, 0.55f };
        var crisisBoost = PoliticalCohorts.CrisisPressureContribution(c, alpha);
        var friction = PoliticalMood.CleavageFriction(c, alpha);
        Check(crisisBoost > 0.02f && crisisBoost <= c.Defines.CleavageCrisisWeight,
            $"fractured regionalist state adds bounded crisis pressure ({crisisBoost:0.000})");
        Check(friction > 0.05f, $"PoliticalMood.CleavageFriction reflects high cleavage stress ({friction:0.000})");
    }

    private static void TestWar02DoctrinesUpkeepAndNoFirstUse()
    {
        var (c, alpha, beta, _, _, _) = BuildFixture();
        Check(NuclearPosture.IsHolder(c, alpha) && NuclearPosture.IsHolder(c, beta),
            "USA and RUS fixtures are deterrent holders");
        Check(alpha.NuclearDoctrine == NuclearDoctrine.RetaliatoryTriad,
            "default nuclear doctrine is RetaliatoryTriad");

        var baseUpkeep = NuclearPosture.WeeklyTriadUpkeep(c, alpha);
        Check(baseUpkeep > 0f, $"RetaliatoryTriad has positive weekly triad upkeep ({baseUpkeep:0.000})");

        // Switch to NoFirstUse via lockstep command
        var fpBefore = CampaignFingerprint.Of(c);
        var cmdNfu = new SetNuclearDoctrineCommand { IssuerId = alpha.Id, Doctrine = NuclearDoctrine.NoFirstUse };
        cmdNfu.Apply(c);
        Check(cmdNfu.Changed && alpha.NuclearDoctrine == NuclearDoctrine.NoFirstUse,
            "SetNuclearDoctrineCommand switches holder to NoFirstUse");
        Check(CampaignFingerprint.Of(c) != fpBefore, "switching nuclear doctrine updates CampaignFingerprint");

        var nfuUpkeep = NuclearPosture.WeeklyTriadUpkeep(c, alpha);
        Check(MathF.Abs(nfuUpkeep - baseUpkeep * 0.75f) < 0.001f,
            $"NoFirstUse lowers weekly triad upkeep to 0.75x ({nfuUpkeep:0.000} vs {baseUpkeep:0.000})");

        // Cooldown blocks immediate second switch
        Check(!NuclearPosture.CanSetDoctrine(c, alpha, NuclearDoctrine.EscalationToDeescalate, out var cdReason) && cdReason.Length > 0,
            "doctrine cooldown refuses immediate switch: " + cdReason);

        // NoFirstUse blocks nuclear first-use strike at DEFCON 1, but permits retaliation when struck
        var war = new StrategicWar { Id = 1, AttackerId = alpha.Id, DefenderId = beta.Id, StartedDay = 100 };
        c.Wars.Add(war);
        c.Armies.Add(new Army { Id = 1, NationId = beta.Id, ProvinceId = 1, Divisions = 4f, Personnel = 40_000, Strength = 1f, Organisation = 1f });
        alpha.NuclearPosture = 1;
        var firstUseRefusal = NuclearPosture.Refusal(c, alpha, "strike", beta);
        Check(firstUseRefusal.Length > 0 && firstUseRefusal == Loc.Get("nuc.refuse.no_first_use"),
            "NoFirstUse blocks nuclear first strike with localized refusal");

        // After being struck by Beta, retaliation IS permitted even under NoFirstUse!
        alpha.NuclearStruckBy = beta.Id;
        alpha.NuclearStruckDay = c.ElapsedDays;
        var retalRefusal = NuclearPosture.Refusal(c, alpha, "strike", beta);
        Check(retalRefusal.Length == 0, "NoFirstUse permits retaliatory second strike against attacker");

        // Move past doctrine cooldown -> switch to EscalationToDeescalate
        alpha.NuclearDoctrineDay = c.ElapsedDays - c.Defines.NuclearDoctrineCooldownDays - 1;
        var cmdEsc = new SetNuclearDoctrineCommand { IssuerId = alpha.Id, Doctrine = NuclearDoctrine.EscalationToDeescalate };
        cmdEsc.Apply(c);
        Check(cmdEsc.Changed && alpha.NuclearDoctrine == NuclearDoctrine.EscalationToDeescalate,
            "SetNuclearDoctrineCommand switches to EscalationToDeescalate after cooldown");

        var escUpkeep = NuclearPosture.WeeklyTriadUpkeep(c, alpha);
        Check(MathF.Abs(escUpkeep - baseUpkeep * 1.25f) < 0.001f,
            $"EscalationToDeescalate raises weekly triad upkeep to 1.25x ({escUpkeep:0.000})");

        // Verify EscalationToDeescalate grants stronger umbrella credibility than RetaliatoryTriad
        var gamma = new Nation { Id = 2, MapIndex = 3, Tag = "POL", Name = "Gamma", Population = 20_000_000, Gdp = 500f };
        c.Nations.Add(gamma);
        CampaignDiplomacy.Get(c, alpha.Id, gamma.Id).Stance = DiplomaticStance.Allied;
        alpha.NuclearPosture = 2;
        var escUmbrella = NuclearPosture.UmbrellaPrice(c, beta, gamma, out _);
        alpha.NuclearDoctrine = NuclearDoctrine.RetaliatoryTriad;
        var baseUmbrella = NuclearPosture.UmbrellaPrice(c, beta, gamma, out _);
        Check(escUmbrella > baseUmbrella * 1.2f,
            $"EscalationToDeescalate strengthens allied umbrella price ({escUmbrella:0.00} vs {baseUmbrella:0.00})");
    }

    private static void TestWar02ArmsControlWarFractureAndEraGating()
    {
        var (c, alpha, beta, _, _, _) = BuildFixture();
        var relAB = CampaignDiplomacy.Get(c, alpha.Id, beta.Id);
        relAB.Tension = 45f;
        var baseUpkeep = NuclearPosture.WeeklyTriadUpkeep(c, alpha);

        // Sign bilateral arms control treaty
        var fpBefore = CampaignFingerprint.Of(c);
        var signCmd = new NuclearArmsControlCommand { IssuerId = alpha.Id, TargetId = beta.Id, Propose = true };
        signCmd.Apply(c);
        Check(signCmd.Changed && NuclearPosture.HasArmsControlWith(c, alpha, beta) && NuclearPosture.HasArmsControlWith(c, beta, alpha),
            "NuclearArmsControlCommand ratifies mutual bilateral arms control treaty");
        Check(CampaignFingerprint.Of(c) != fpBefore, "signing arms control pact updates CampaignFingerprint");
        Check(relAB.Tension < 45f, $"signing arms control treaty reduces tension (45 -> {relAB.Tension:0.#})");

        var discountedUpkeep = NuclearPosture.WeeklyTriadUpkeep(c, alpha);
        Check(MathF.Abs(discountedUpkeep - baseUpkeep * (1f - c.Defines.ArmsControlUpkeepDiscount)) < 0.001f,
            $"active arms control pact discounts weekly triad upkeep by 25% ({discountedUpkeep:0.000} vs {baseUpkeep:0.000})");

        // Weekly tick charges discounted upkeep cleanly to treasury
        var treasuryBefore = alpha.Treasury;
        NuclearPosture.Week(c);
        Check(alpha.Treasury < treasuryBefore, "NuclearPosture.Week books weekly triad upkeep against treasury");

        // War outbreak fractures bilateral arms control pact automatically on next weekly review
        c.Wars.Add(new StrategicWar { Id = 7, AttackerId = alpha.Id, DefenderId = beta.Id, StartedDay = c.ElapsedDays });
        NuclearPosture.Week(c);
        Check(!NuclearPosture.HasArmsControlWith(c, alpha, beta) && !NuclearPosture.HasArmsControlWith(c, beta, alpha),
            "open war between signatories automatically fractures arms control treaty");

        // Era feature gating: disabling "nukes" features blocks doctrines, arms control & upkeep
        c.Wars.Clear();
        c.SetFeature("defcon", false);
        c.SetFeature("nuclear_posture", false);
        c.SetFeature("nuclear_strikes", false);
        Check(!Deterrence.IsNuclearEraEnabled(c) && !NuclearPosture.Active(c),
            "disabling nukes era features disables Deterrence.IsNuclearEraEnabled and NuclearPosture.Active");
        Check(Deterrence.TierOf(c, alpha) == 0, "disabling all three nukes era features zeroes Deterrence.TierOf");
        Check(NuclearPosture.WeeklyTriadUpkeep(c, alpha) == 0f, "disabled nuclear era charges 0 triad upkeep");

        var blockedDoc = new SetNuclearDoctrineCommand { IssuerId = alpha.Id, Doctrine = NuclearDoctrine.NoFirstUse };
        blockedDoc.Apply(c);
        Check(!blockedDoc.Changed, "disabled nuclear era blocks SetNuclearDoctrineCommand");

        var blockedPact = new NuclearArmsControlCommand { IssuerId = alpha.Id, TargetId = beta.Id, Propose = true };
        blockedPact.Apply(c);
        Check(!blockedPact.Changed, "disabled nuclear era blocks NuclearArmsControlCommand");
    }

    private static void TestWave5SaveFingerprintAndNetworkCodec()
    {
        var (c, alpha, beta, capA, _, _) = BuildFixture();
        PoliticalCohorts.Review(c, alpha, 1);
        new SetNuclearDoctrineCommand { IssuerId = alpha.Id, Doctrine = NuclearDoctrine.NoFirstUse }.Apply(c);
        new NuclearArmsControlCommand { IssuerId = alpha.Id, TargetId = beta.Id, Propose = true }.Apply(c);

        // NetworkCommandCodec round-trip for both new lockstep commands
        ICampaignCommand[] cmds =
        {
            new SetNuclearDoctrineCommand { IssuerId = alpha.Id, Doctrine = NuclearDoctrine.EscalationToDeescalate },
            new NuclearArmsControlCommand { IssuerId = alpha.Id, TargetId = beta.Id, Propose = false }
        };
        foreach (var cmd in cmds)
        {
            Check(NetworkCommandCodec.TryEncode(cmd, out var typeName, out var json), $"NetworkCommandCodec encodes {cmd.GetType().Name}");
            Check(NetworkCommandCodec.TryDecode(typeName, json, out var decoded) && decoded.GetType() == cmd.GetType(),
                $"NetworkCommandCodec decodes {typeName}");
        }

        // SaveSystem round-trip preserves PoliticalBlocs, NuclearDoctrine, NuclearDoctrineDay & ArmsControlPacts
        var dir = Path.Combine(Path.GetTempPath(), "dof_w5_save_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var fpOrig = CampaignFingerprint.Of(c);
            SaveSystem.Save(dir, "w5_slot", c, "", "Wave5 Test");
            var loaded = SaveSystem.Load(dir, "w5_slot", "");
            Check(loaded.Success && loaded.Campaign is not null, "SaveSystem round-trip succeeds for Wave 5 state");

            var loadedAlpha = loaded.Campaign.NationById(alpha.Id);
            var loadedCap = loaded.Campaign.States.First(s => s.MapIndex == capA.MapIndex);
            Check(loadedCap.PoliticalBlocs is { Length: 5 } && MathF.Abs(loadedCap.PoliticalBlocs[0] - capA.PoliticalBlocs[0]) < 0.0005f,
                "SaveSystem round-trip preserves ScenarioState.PoliticalBlocs");
            Check(loadedAlpha.NuclearDoctrine == NuclearDoctrine.NoFirstUse && loadedAlpha.NuclearDoctrineDay == alpha.NuclearDoctrineDay,
                "SaveSystem round-trip preserves Nation.NuclearDoctrine and NuclearDoctrineDay");
            Check(loadedAlpha.ArmsControlPacts.TryGetValue(beta.Id, out var loadedExpiry) && loadedExpiry == alpha.ArmsControlPacts[beta.Id],
                "SaveSystem round-trip preserves Nation.ArmsControlPacts");
            loaded.Campaign.AttachProvinceTopology(ProvinceTopology.Build(
                new ushort[] { 10, 20 },
                new ushort[] { 1, 2 }, 2, 1, _ => false));
            var fpLoaded = CampaignFingerprint.Of(loaded.Campaign);
            var snap2 = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(loaded.Campaign, "wave5-snap"), "wave5-snap");
            Check(snap2.Success && CampaignFingerprint.Of(snap2.Campaign) == fpLoaded,
                "CampaignFingerprint matches identically across network snapshot round-trip with Wave 5 state");
            loadedAlpha.NuclearDoctrine = NuclearDoctrine.EscalationToDeescalate;
            Check(CampaignFingerprint.Of(loaded.Campaign) != fpLoaded,
                "mutating loaded NuclearDoctrine changes CampaignFingerprint");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void TestWave5ContentAndLocalization()
    {
        string[] expectedEvents =
        {
            "w5_regional_fiscal_revolt", "w5_industrial_reform_standoff", "w5_security_establishment_memorandum",
            "w5_arms_control_verification_dispute", "w5_doctrine_white_paper_leak", "w5_megaproject_consortium_overrun",
            "w5_chokepoint_transit_summit", "w5_postwar_reconstruction_tenders", "w5_emergency_grain_and_energy_corridor"
        };
        foreach (var evId in expectedEvents)
        {
            var ev = EventCatalog.All.FirstOrDefault(e => string.Equals(e.Id, evId, StringComparison.OrdinalIgnoreCase));
            Check(ev is not null && ev.Options.Length >= 2, $"Wave 5 event '{evId}' loaded with >= 2 options");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", ev.Title)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", ev.Title)),
                $"Wave 5 event '{evId}' has EN+RU title");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", ev.Description)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", ev.Description)),
                $"Wave 5 event '{evId}' has EN+RU description");
        }

        string[] expectedDecisions =
        {
            "w5_federal_fiscal_compact", "w5_multilateral_crisis_summit", "w5_postwar_rebuilding_authority",
            "w5_strategic_triad_modernization", "w5_industrial_retooling_charter", "w5_civic_consensus_dialogue"
        };
        foreach (var decId in expectedDecisions)
        {
            var dec = DecisionCatalog.All.FirstOrDefault(d => string.Equals(d.Id, decId, StringComparison.OrdinalIgnoreCase));
            Check(!string.IsNullOrEmpty(dec.Id), $"Wave 5 decision '{decId}' loaded");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", dec.Name)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", dec.Name)),
                $"Wave 5 decision '{decId}' has EN+RU name");
            Check(!string.IsNullOrWhiteSpace(dec.AiWillDo), $"Wave 5 decision '{decId}' has AI weight");
        }

        string[] expectedModifiers =
        {
            "w5_fiscal_compact_spirit", "w5_crisis_summit_accord", "w5_rebuilding_authority",
            "w5_triad_modernization", "w5_industrial_retooling", "w5_civic_consensus",
            "w5_ev_verifiers_deployed", "w5_ev_wartime_repatriation"
        };
        foreach (var modId in expectedModifiers)
        {
            var mod = ModifierCatalog.Get(modId);
            Check(mod is not null && mod.Modifiers.Count > 0, $"Wave 5 modifier '{modId}' loaded with valid keys");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", mod.Name)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", mod.Name)),
                $"Wave 5 modifier '{modId}' has EN+RU name");
        }

        string[] requiredLocKeys =
        {
            "Deep political and regional polarization",
            "pol.bloc.reformist", "pol.bloc.security", "pol.bloc.industrialist", "pol.bloc.traditionalist", "pol.bloc.regionalist",
            "nuc.doctrine.retaliatory_triad", "nuc.doctrine.no_first_use", "nuc.doctrine.escalation_to_deescalate",
            "nuc.refuse.era_disabled", "nuc.refuse.no_first_use", "nuc.refuse.arms_at_war",
            "nuc.op.arms_control_pact", "nuc.op.arms_control_broken",
            "chronicle.nuclear_doctrine_changed", "chronicle.nuclear_arms_control_signed", "chronicle.nuclear_arms_control_broken"
        };
        foreach (var key in requiredLocKeys)
        {
            var en = Loc.TextIn("english", key);
            var ru = Loc.TextIn("russian", key);
            Check(!string.IsNullOrWhiteSpace(en) && !string.IsNullOrWhiteSpace(ru),
                $"Wave 5 localization key '{key}' present in EN+RU");
        }
    }

    private static void TestWave5HFormablesMovementsFlagsAndUiGuard()
    {
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        var boot = Campaign.CreateBootstrap(20261122);
        Movements.Found(boot);

        string[] wave5Formables =
        {
            "celtic_union", "francophonie_europe", "alpen_bund", "nordic_baltic_eight",
            "hellenic_commonwealth", "carpathian_danube_league", "mesopotamian_federation",
            "arabian_peninsula_union", "iranic_commonwealth", "bay_of_bengal_union",
            "mekong_confederation", "maritime_nusantara", "pacific_rim_accord",
            "southern_african_union", "central_african_community", "guinea_gulf_federation",
            "caribbean_commonwealth", "mercosur_federation", "pacific_islands_confederation",
            "mesoamerican_union"
        };
        foreach (var id in wave5Formables)
        {
            var def = FormableNations.Find(id);
            Check(def is not null, $"Wave 5H formable '{id}' loaded");
            var founder = def.EligibleTags.Select(boot.NationByTag).FirstOrDefault(n => n is not null);
            Check(founder is not null, $"Wave 5H formable '{id}' has valid 2024 founder tag");
            var sprite = FlagCatalog.ResolveFormationPreview(boot, founder, id);
            var tex = SpriteCatalog.TextureOf(sprite);
            var physical = tex.StartsWith("res://game/", StringComparison.Ordinal)
                ? Path.Combine(GameDataLoader.DataFolder(), tex["res://game/".Length..]) : tex;
            Check(sprite == "flag_formable_" + id && File.Exists(physical),
                $"Wave 5H formable '{id}' resolves to bespoke flag file '{sprite}'");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", def.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", def.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", def.Description)),
                $"Wave 5H formable '{id}' has EN+RU name and description");
        }

        string[] wave5MovementTags = { "EUS", "BRZ", "FLA", "SRD", "VNT", "GLC", "ACH", "BSM", "SRW", "WCP", "ABT", "SCZ" };
        foreach (var tag in wave5MovementTags)
        {
            var nation = boot.NationByTag(tag);
            Check(nation is not null && nation.Movement is not null,
                $"Wave 5H landless movement '{tag}' founded cleanly in 2024 bootstrap");
            var expectedSprite = "flag_" + tag.ToLowerInvariant();
            var resolved = FlagCatalog.Resolve(boot, nation);
            var tex = SpriteCatalog.TextureOf(resolved);
            var physical = tex.StartsWith("res://game/", StringComparison.Ordinal)
                ? Path.Combine(GameDataLoader.DataFolder(), tex["res://game/".Length..]) : tex;
            Check(resolved == expectedSprite && File.Exists(physical),
                $"Wave 5H movement '{tag}' flies bespoke banner '{resolved}'");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", nation.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", nation.Name)),
                $"Wave 5H movement '{tag}' has EN+RU name");
        }

        string[] wave5HEvents =
        {
            "w5h_confederal_charter_congress", "w5h_regional_language_charter",
            "w5h_cross_border_customs_union", "w5h_provincial_devolution_referendum",
            "w5h_shared_currency_corridor", "w5h_pan_regional_rail_gauge",
            "w5h_diaspora_investment_summit", "w5h_heraldic_identity_convention",
            "w5h_maritime_archipelago_compact"
        };
        foreach (var evId in wave5HEvents)
        {
            var ev = EventCatalog.All.FirstOrDefault(e => string.Equals(e.Id, evId, StringComparison.OrdinalIgnoreCase));
            Check(ev is not null && ev.Options.Length >= 2, $"Wave 5H event '{evId}' loaded with >= 2 options");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", ev.Title)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", ev.Title)),
                $"Wave 5H event '{evId}' has EN+RU title");
        }

        // Structural UI regression guard for FormableNationsUI.cs (waving cloth flag + zero magenta missing token)
        var uiPath = Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts", "ui", "FormableNationsUI.cs");
        Check(File.Exists(uiPath), "FormableNationsUI.cs exists");
        var uiSource = File.ReadAllText(uiPath);
        Check(uiSource.Contains("FlagClothPresentation.Material", StringComparison.Ordinal),
            "FormableNationsUI wires FlagClothPresentation.Material onto formable preview flag");
        Check(!uiSource.Contains("\"surface.card\"", StringComparison.Ordinal),
            "FormableNationsUI no longer references missing 'surface.card' token (magenta pill fix)");
        Check(!uiSource.Contains("new Color(", StringComparison.Ordinal),
            "FormableNationsUI contains zero raw Color literals");
        Check(uiSource.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "FormableNationsUI applies PresentationFonts.Tabular() to progress pill");
        var expandPos = uiSource.IndexOf("ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize", StringComparison.Ordinal);
        var texPos = uiSource.IndexOf("Texture = FlagSpriteTexture(futureSprite)", StringComparison.Ordinal);
        Check(expandPos > 0 && texPos > expandPos,
            "FormableNationsUI sets ExpandMode before Texture to prevent cropping");
    }

    private static void TestWave6DoctrineExpansion()
    {
        StrategicDoctrineCatalog.ResetCatalog();
        TacticalDoctrineCatalog.ResetCatalog();
        AiStrategyCatalog.ResetCatalog();

        string[] wave6Strategies =
        {
            "hybrid_asymmetric_war", "expeditionary_intervention", "network_centric_warfare",
            "fortress_deterrence", "coalition_interoperability"
        };
        foreach (var id in wave6Strategies)
        {
            var strat = StrategicDoctrineCatalog.Find(id);
            Check(strat is not null, $"Wave 6E strategic doctrine '{id}' loaded");
            Check(strat.PoliticalCost > 0f && strat.Modifiers.Count > 0 &&
                  strat.Modifiers.Keys.All(NationalModifiers.Knows),
                $"Wave 6E strategic doctrine '{id}' has valid political cost and known NationalModifiers");
            Check(!string.IsNullOrWhiteSpace(strat.TacticalBias) && TacticalDoctrineCatalog.Find(strat.TacticalBias) is not null,
                $"Wave 6E strategic doctrine '{id}' routes tactical_bias to valid book '{strat.TacticalBias}'");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", strat.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", strat.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", strat.Description)),
                $"Wave 6E strategic doctrine '{id}' has EN+RU name and description");
        }

        var expectedBooks = new (string Id, string Experience)[]
        {
            ("drone_swarm_tactics", "army"),
            ("urban_counterinsurgency", "army"),
            ("standoff_precision_air", "air"),
            ("low_cost_unmanned_air", "air"),
            ("amphibious_expeditionary", "navy"),
            ("undersea_hunter_killer", "navy"),
            ("cyber_electronic_staff", "command"),
            ("wartime_industrial_staff", "command")
        };
        var c = Campaign.CreateBootstrap(20261122);
        var n = c.NationByTag("POL");
        c.ConfigureSinglePlayer(n.Id);
        foreach (var (bookId, exp) in expectedBooks)
        {
            var book = TacticalDoctrineCatalog.Find(bookId);
            Check(book is not null && book.Experience == exp,
                $"Wave 6E tactical book '{bookId}' loaded under experience '{exp}'");
            Check(book.Nodes.Count >= 10 && book.Nodes.Count <= 12,
                $"Wave 6E tactical book '{bookId}' has {book.Nodes.Count} nodes (10..12)");
            Check(book.AiProfiles.Count > 0 && book.AiProfiles.All(p => AiStrategyCatalog.Profiles.ContainsKey(p.Key) && p.Value > 0f),
                $"Wave 6E tactical book '{bookId}' has valid positive AiStrategyCatalog profile weights");
            Check(book.Nodes.Count(node => node.Requires.Count == 0) == 1,
                $"Wave 6E tactical book '{bookId}' has exactly one root node");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", book.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", book.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", book.Description)),
                $"Wave 6E tactical book '{bookId}' has EN+RU name and description");

            var subNodes = book.Nodes.Where(node => node.Excludes.Count > 0).ToArray();
            Check(subNodes.Length >= 4, $"Wave 6E tactical book '{bookId}' contains at least 2 sub-doctrine pairs ({subNodes.Length} choices)");
            foreach (var sub in subNodes)
            {
                Check(sub.Excludes.Count == 1 && book.Find(sub.Excludes[0]) is { } opp && opp.Excludes.Contains(sub.Id),
                    $"Wave 6E sub-doctrine '{sub.Id}' has reciprocal exclusion");
                Check(sub.Modifiers.Any(m => NationalModifiers.IsGood(m.Key, m.Value)) &&
                      sub.Modifiers.Any(m => !NationalModifiers.IsGood(m.Key, m.Value)),
                    $"Wave 6E sub-doctrine '{sub.Id}' carries both a clear advantage and a real trade-off cost");
                Check(book.Nodes.Any(cont => cont.Requires.Contains(sub.Id)),
                    $"Wave 6E sub-doctrine '{sub.Id}' leads to a continuation article");
            }

            foreach (var node in book.Nodes)
            {
                Check(node.Modifiers.Count > 0 && node.Modifiers.Keys.All(NationalModifiers.Knows),
                    $"Wave 6E node '{node.Id}' uses only valid NationalModifiers keys");
                Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", node.Name)) &&
                      !string.IsNullOrWhiteSpace(Loc.TextIn("russian", node.Name)) &&
                      !string.IsNullOrWhiteSpace(Loc.TextIn("russian", node.Description)),
                    $"Wave 6E node '{node.Id}' has EN+RU name and description");
            }

            // Exercise one live sub-doctrine choice + mutual-exclusion refusal via StudyDoctrineCommand
            n.TacticalDoctrine = new TacticalDoctrineMemory();
            n.ArmyExperience = n.AirExperience = n.NavalExperience = n.PoliticalPoints = 5000f;
            NationalModifiers.Invalidate(n);
            var firstChoice = subNodes[0];
            var track = n.TacticalDoctrine.Track(book.Slot);
            track.Branch = book.Id;
            var prereqs = new HashSet<string>(StringComparer.Ordinal);
            void CollectPrereqs(DoctrineNode nd) { foreach (var req in nd.Requires) if (prereqs.Add(req)) CollectPrereqs(book.Find(req)); }
            CollectPrereqs(firstChoice);
            track.Known = prereqs.OrderBy(x => x, StringComparer.Ordinal).ToList();

            var pick = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = book.Id, NodeId = firstChoice.Id, Rush = true };
            c.Commands.Enqueue(pick);
            c.Commands.ApplyAll(c);
            Check(pick.Refusal.Length == 0 && TacticalDoctrines.Known(n, firstChoice.Id),
                $"Wave 6E sub-doctrine '{firstChoice.Id}' studied cleanly via StudyDoctrineCommand");

            var blocked = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = book.Id, NodeId = firstChoice.Excludes[0], Rush = true };
            c.Commands.Enqueue(blocked);
            c.Commands.ApplyAll(c);
            Check(blocked.Refusal.Length > 0 && !TacticalDoctrines.Known(n, firstChoice.Excludes[0]),
                $"Wave 6E opposite sub-doctrine '{firstChoice.Excludes[0]}' cleanly refused after choosing '{firstChoice.Id}'");
        }
    }
}
