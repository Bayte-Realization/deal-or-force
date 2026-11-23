using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Verification suite for Strategic Megaprojects &amp; Joint International Consortia
/// (<see cref="GrandProjects"/>, <see cref="GrandProjectCatalog"/>, and commands).
/// </summary>
static class GameplayContentTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL gameplay-content: " + label);
        Console.WriteLine("PASS gameplay-content: " + label);
    }

    private static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);

    private static void AdvanceDays(Campaign c, int fromDay, int count)
    {
        for (var i = 0; i < count; i++)
        {
            SetDay(c, fromDay + i);
            GrandProjects.Tick(c);
        }
    }

    private static (Campaign Campaign, Nation Alpha, Nation Beta, Nation Gamma, ScenarioState StateA1, ScenarioState StateA2, ScenarioState StateB1) BuildFixture(int seed = 20261121)
    {
        var c = new Campaign(seed) { ScenarioId = "grand-projects-fixture" };
        c.Defines.GrandProjectsEnabled = true;
        c.Defines.GrandProjectAiEnabled = false; // deterministic manual tests unless testing AI explicitly

        var alpha = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "ALP", Name = "Alpha Republic", IsHuman = true,
            Population = 25_000_000, Gdp = 800f, Treasury = 60f, Stability = 0.80f,
            PoliticalPoints = 100f, ArmyExperience = 50f, AirExperience = 50f, NavalExperience = 50f,
            OutputScale = 1f, Government = "republic", Ideology = "liberalism"
        };
        var beta = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BET", Name = "Beta Federation", IsHuman = false,
            Population = 20_000_000, Gdp = 650f, Treasury = 50f, Stability = 0.78f,
            PoliticalPoints = 80f, ArmyExperience = 40f, AirExperience = 40f, NavalExperience = 40f,
            OutputScale = 1f, Government = "republic", Ideology = "liberalism"
        };
        var gamma = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "GAM", Name = "Gamma Dominion", IsHuman = false,
            Population = 18_000_000, Gdp = 600f, Treasury = 40f, Stability = 0.72f,
            PoliticalPoints = 70f, ArmyExperience = 40f, AirExperience = 40f, NavalExperience = 40f,
            OutputScale = 1f, Government = "autocracy", Ideology = "nationalism"
        };
        c.Nations.Add(alpha);
        c.Nations.Add(beta);
        c.Nations.Add(gamma);

        var stA1 = new ScenarioState
        {
            Id = "alp_core", MapIndex = 10, NationMapIndex = 1, Name = "Alpha Core",
            Population = 10_000_000, Literacy = 0.85f, StandardOfLiving = 0.75f, WarDamage = 0f
        };
        var stA2 = new ScenarioState
        {
            Id = "alp_frontier", MapIndex = 11, NationMapIndex = 1, Name = "Alpha Frontier",
            Population = 3_000_000, Literacy = 0.55f, StandardOfLiving = 0.48f, WarDamage = 0.15f
        };
        var stB1 = new ScenarioState
        {
            Id = "bet_core", MapIndex = 20, NationMapIndex = 2, Name = "Beta Core",
            Population = 9_000_000, Literacy = 0.82f, StandardOfLiving = 0.72f, WarDamage = 0f
        };
        var stG1 = new ScenarioState
        {
            Id = "gam_core", MapIndex = 30, NationMapIndex = 3, Name = "Gamma Core",
            Population = 8_000_000, Literacy = 0.78f, StandardOfLiving = 0.68f, WarDamage = 0f
        };
        c.States.Add(stA1);
        c.States.Add(stA2);
        c.States.Add(stB1);
        c.States.Add(stG1);
        c.InvalidateStateIndex();
        return (c, alpha, beta, gamma, stA1, stA2, stB1);
    }

    [TestSuite("--gameplay-content-only", Order = 2610, IncludeInDefaultRun = true,
        Description = "Strategic Megaprojects & Joint International Consortia: catalog, lifecycle, refusals, consortia, sabotage, conquest, AI, save/load, fingerprint & toggle.")]
    public static void Run()
    {
        TestCatalogAndLocalization();
        TestWave2EventsAndDecisionsCatalog();
        TestLifecycleAndModifiers();
        TestRefusalReasons();
        TestCategoryExclusivity();
        TestPauseAndCancel();
        TestJointConsortiumAndFracture();
        TestMarketSupplyCushion();
        TestStrategicChokepoints();
        TestWarDamageAndConquestInheritance();
        TestCovertSabotage();
        TestAiParticipation();
        TestSaveLoadAndFingerprint();
        TestCampaignToggleAndNetworkCodec();
    }

    private static void TestCatalogAndLocalization()
    {
        GrandProjectCatalog.ResetCatalog();
        Check(GrandProjectCatalog.All.Count >= 16, $"loaded {GrandProjectCatalog.All.Count} grand project blueprints (Wave 2 target >= 16)");
        Check(SettingsCatalog.Of(GrandProjects.SettingId) is { Campaign: true },
            "grand_projects setting is registered as a campaign-scope toggle");
        Check(OpinionCatalog.Find("grand_project_consortium_partner") is not null &&
              OpinionCatalog.Find("grand_project_consortium_completed") is not null &&
              OpinionCatalog.Find("grand_project_consortium_fractured") is not null &&
              OpinionCatalog.Find("grand_project_sabotaged_us") is not null &&
              OpinionCatalog.Find("chokepoint_weaponized") is not null &&
              OpinionCatalog.Find("strategic_supply_anchor") is not null,
            "all 6 grand project & chokepoint opinion modifiers are loaded");

        foreach (var def in GrandProjectCatalog.All)
        {
            Check(def.HostModifiers.Count > 0, $"blueprint '{def.Id}' has host modifiers");
            foreach (var key in def.HostModifiers.Keys.Concat(def.PartnerModifiers.Keys))
                Check(NationalModifiers.Knows(key), $"modifier key '{key}' on '{def.Id}' is valid in NationalModifiers");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", def.NameKey)), $"EN name exists for '{def.Id}'");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("russian", def.NameKey)), $"RU name exists for '{def.Id}'");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", def.DescriptionKey)), $"EN desc exists for '{def.Id}'");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("russian", def.DescriptionKey)), $"RU desc exists for '{def.Id}'");
        }

        // Mod overlay & removal support
        var errors = new List<string>();
        using (var addDoc = JsonDocument.Parse(@"{""mod_skybridge"":{""name"":""gp.proj.transcontinental_hsr.name"",""description"":""gp.proj.transcontinental_hsr.desc"",""category"":""infrastructure"",""stage_days"":[30,30,30],""host_modifiers"":{""trade_reach"":0.05}}}"))
            GrandProjectCatalog.Merge(addDoc.RootElement, errors);
        Check(errors.Count == 0 && GrandProjectCatalog.Find("mod_skybridge") is not null, "mod overlay adds new megaproject cleanly");
        using (var remDoc = JsonDocument.Parse(@"{""mod_skybridge"":{""remove"":true}}"))
            GrandProjectCatalog.Merge(remDoc.RootElement, errors);
        Check(GrandProjectCatalog.Find("mod_skybridge") is null, "mod overlay removes megaproject via remove:true");
        GrandProjectCatalog.ResetCatalog();
    }

    private static void TestWave2EventsAndDecisionsCatalog()
    {
        string[] expectedEvents =
        {
            "gp_sovereign_wealth_rebalance", "gp_chokepoint_insurance_spike", "gp_tech_sanctions_backfire",
            "gp_energy_corridor_windfall", "gp_regional_arms_race", "gp_peace_broker_summit",
            "gp_megaproject_breakthrough", "gp_megaproject_contractor_strike",
            "gp_rare_earth_cartel_squeeze", "gp_sovereign_ai_labor_shift"
        };
        foreach (var evId in expectedEvents)
        {
            var ev = EventCatalog.All.FirstOrDefault(e => string.Equals(e.Id, evId, StringComparison.OrdinalIgnoreCase));
            Check(ev is not null, $"Wave 2 event '{evId}' loaded in EventCatalog");
            Check(ev.Options.Length >= 2, $"Wave 2 event '{evId}' has >= 2 dilemma options");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", ev.Title)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", ev.Title)),
                $"Wave 2 event '{evId}' has EN+RU title");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", ev.Description)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", ev.Description)),
                $"Wave 2 event '{evId}' has EN+RU description");
            foreach (var opt in ev.Options)
            {
                Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", opt.Title)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", opt.Title)),
                    $"Wave 2 event '{evId}' option '{opt.Title}' has EN+RU title");
                foreach (var (modId, days) in opt.Modifiers)
                    Check(days > 0 && ModifierCatalog.Get(modId) is not null, $"Wave 2 event '{evId}' modifier '{modId}' exists");
            }
        }

        string[] expectedDecisions =
        {
            "strategic_supply_diversification", "sovereign_export_controls", "maritime_convoy_charter",
            "megaproject_fast_track_act", "sovereign_debt_swap_accord", "strategic_deterrence_drills"
        };
        foreach (var decId in expectedDecisions)
        {
            var dec = DecisionCatalog.All.FirstOrDefault(d => string.Equals(d.Id, decId, StringComparison.OrdinalIgnoreCase));
            Check(!string.IsNullOrEmpty(dec.Id), $"Wave 2 decision '{decId}' loaded in DecisionCatalog");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", dec.Name)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", dec.Name)),
                $"Wave 2 decision '{decId}' has EN+RU name");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", dec.Description)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", dec.Description)),
                $"Wave 2 decision '{decId}' has EN+RU description");
            Check(!string.IsNullOrWhiteSpace(dec.AiWillDo), $"Wave 2 decision '{decId}' has AI weight");
        }

        string[] expectedModifiers =
        {
            "supply_diversification_pact", "export_control_regime", "convoy_escort_charter",
            "fast_track_overdrive", "creditor_austerity_covenant", "joint_deterrence_posture",
            "gp_ev_freight_reroute", "gp_ev_lithography_crash_program",
            "gp_ev_transit_corridor_boom", "gp_ev_engineering_breakthrough"
        };
        foreach (var modId in expectedModifiers)
        {
            var mod = ModifierCatalog.Get(modId);
            Check(mod is not null && mod.Modifiers.Count > 0, $"Wave 2 modifier '{modId}' loaded with valid keys");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", mod.Name)) && !string.IsNullOrWhiteSpace(Loc.TextIn("russian", mod.Name)),
                $"Wave 2 modifier '{modId}' has EN+RU name");
        }
    }

    private static void TestCategoryExclusivity()
    {
        var (c, alpha, _, _, stA1, stA2, _) = BuildFixture();
        c.Defines.GrandProjectMaxActivePerNation = 2;
        new StartGrandProjectCommand(alpha.Id, "sovereign_fusion_grid", stA1.MapIndex).Apply(c);
        Check(!GrandProjects.CanStart(c, alpha, stA2, "geothermal_baseload_ring", out var rCat) && rCat.Length > 0,
            "per-category exclusivity blocks stacking two 'energy' megaprojects: " + rCat);
        Check(GrandProjects.CanStart(c, alpha, stA2, "continental_grain_belt", out _),
            "different category ('agriculture') remains allowed on second region");
    }

    private static void TestStrategicChokepoints()
    {
        var (c, alpha, beta, gamma, stA1, _, _) = BuildFixture();
        // Set up real TradeRoutes: Gamma supplies 80% of Alpha's RareEarths, Beta supplies 20%.
        c.TradeRoutesDay = 7;
        c.TradeRoutes.Add(new TradeRoute { SellerId = gamma.Id, BuyerId = alpha.Id, Commodity = Commodity.RareEarths, Volume = 80f, Value = 8.0f });
        c.TradeRoutes.Add(new TradeRoute { SellerId = beta.Id, BuyerId = alpha.Id, Commodity = Commodity.RareEarths, Volume = 20f, Value = 2.0f });
        // Beta supplies 90% of Alpha's Oil reliably as an allied anchor.
        c.TradeRoutes.Add(new TradeRoute { SellerId = beta.Id, BuyerId = alpha.Id, Commodity = Commodity.Oil, Volume = 90f, Value = 9.0f });
        alpha.Market[Commodity.RareEarths] = new MarketQuote { Commodity = Commodity.RareEarths, Satisfaction = 0.95f };
        alpha.Market[Commodity.Oil] = new MarketQuote { Commodity = Commodity.Oil, Satisfaction = 0.98f };

        var relBeta = CampaignDiplomacy.Get(c, alpha.Id, beta.Id);
        relBeta.Stance = DiplomaticStance.Allied;

        // Start Semiconductor Fab (consumes Electronics + RareEarths)
        new StartGrandProjectCommand(alpha.Id, "sovereign_semiconductor_fab", stA1.MapIndex).Apply(c);
        var inst = c.GrandProjects[0];

        var fpBeforeRead = CampaignFingerprint.Of(c);
        var unsqueezedPace = GrandProjects.DailyBuildPace(c, inst, out _, out _);
        var exposuresBefore = StrategicChokepoints.ReadForNation(c, alpha);
        Check(CampaignFingerprint.Of(c) == fpBeforeRead, "read-only StrategicChokepoints analysis never mutates CampaignFingerprint");
        Check(exposuresBefore.Any(e => e.Commodity == Commodity.Oil && e.Status == ChokepointStatus.HealthyAnchor),
            "detects reliable allied Oil supplier as HealthyAnchor");
        Check(exposuresBefore.Any(e => e.Commodity == Commodity.RareEarths && e.Status == ChokepointStatus.Concentrated && e.ImpactsActiveMegaproject),
            "detects concentrated RareEarths supplier impacting active Semiconductor Fab");

        // Gamma embargoes Alpha -> RareEarths chokepoint becomes Squeezed & throttles Semiconductor Fab!
        var relGamma = CampaignDiplomacy.Get(c, alpha.Id, gamma.Id);
        relGamma.EmbargoByB = true;
        var squeezedPace = GrandProjects.DailyBuildPace(c, inst, out _, out _);
        Check(squeezedPace < unsqueezedPace * 0.72f,
            $"weaponized RareEarths embargo throttles active Semiconductor Fab ({unsqueezedPace:0.00}x -> {squeezedPace:0.00}x)");

        var exposuresSqueezed = StrategicChokepoints.ReadForNation(c, alpha);
        Check(exposuresSqueezed.Any(e => e.Commodity == Commodity.RareEarths && e.Status == ChokepointStatus.Squeezed),
            "embargoed dominant supplier flips exposure to Squeezed");

        // Allied consortium partner Beta with abundant RareEarths shields the megaproject!
        beta.Market[Commodity.RareEarths] = new MarketQuote { Commodity = Commodity.RareEarths, Satisfaction = 0.95f };
        new JoinGrandProjectConsortiumCommand(beta.Id, inst.Id).Apply(c);
        var shieldedPace = GrandProjects.DailyBuildPace(c, inst, out _, out _);
        Check(shieldedPace > unsqueezedPace * 1.15f,
            $"allied consortium partner shields megaproject from rival chokepoint squeeze ({squeezedPace:0.00}x -> {shieldedPace:0.00}x)");

        // Weekly chokepoint tick records diplomatic opinion memories & public Chronicle dispatch
        SetDay(c, 7);
        StrategicChokepoints.TickWeekly(c);
        Check(relGamma.Reasons.Any(r => r.Id == "chokepoint_weaponized"), "weekly chokepoint tick records chokepoint_weaponized opinion against embargoing rival");
        Check(relBeta.Reasons.Any(r => r.Id == "strategic_supply_anchor"), "weekly chokepoint tick records strategic_supply_anchor opinion with reliable ally");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "supply_chokepoint_squeezed"), "weekly chokepoint tick emits public supply_chokepoint_squeezed Chronicle news");
    }

    private static void TestLifecycleAndModifiers()
    {
        var (c, alpha, _, _, stA1, _, _) = BuildFixture();
        var def = GrandProjectCatalog.Find("orbital_launch_complex");
        Check(def is not null, "orbital_launch_complex exists");

        var startMoney = alpha.Treasury;
        var startPp = alpha.PoliticalPoints;
        var startAirXp = alpha.AirExperience;
        var cmd = new StartGrandProjectCommand(alpha.Id, def.Id, stA1.MapIndex);
        c.Commands.Enqueue(cmd);
        c.Commands.ApplyAll(c);

        Check(c.GrandProjects.Count == 1, "StartGrandProjectCommand creates 1 instance");
        var inst = c.GrandProjects[0];
        Check(alpha.Treasury < startMoney, "Stage 1 deducts treasury");
        Check(alpha.PoliticalPoints < startPp, "Stage 1 deducts political credit");
        Check(alpha.AirExperience < startAirXp, "Stage 1 deducts air experience");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "grand_project_started"), "start records chronicle entry");

        // Advance through Stage 1 -> AwaitingStageFunding
        AdvanceDays(c, 1, def.DurationForStage(1) + 5);
        Check(inst.Status == GrandProjectStatus.AwaitingStageFunding && inst.Stage == 1,
            "completing Stage 1 waits for Stage 2 funding");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "grand_project_stage"), "stage completion recorded in chronicle");

        // Fund Stage 2 -> run to completion
        c.Commands.Enqueue(new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id));
        c.Commands.ApplyAll(c);
        Check(inst.Stage == 2 && inst.Status == GrandProjectStatus.Active, "Stage 2 funded and active");
        AdvanceDays(c, 200, def.DurationForStage(2) + 5);
        Check(inst.Status == GrandProjectStatus.AwaitingStageFunding && inst.Stage == 2, "Stage 2 awaits Stage 3 funding");

        // Fund Stage 3 -> run to completion
        c.Commands.Enqueue(new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id));
        c.Commands.ApplyAll(c);
        Check(inst.Stage == 3 && inst.Status == GrandProjectStatus.Active, "Stage 3 funded and active");
        AdvanceDays(c, 500, def.DurationForStage(3) + 5);
        Check(inst.Status == GrandProjectStatus.Completed && inst.CompletedDay > 0, "Stage 3 finishes and marks Completed");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "grand_project_completed"), "completion recorded in chronicle");

        // Host modifiers live on Nation & in NationalModifiers
        var slices = GrandProjects.Sources(alpha).ToList();
        Check(slices.Count == 1, "completed megaproject exposes 1 modifier slice on host nation");
        var sum = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        GrandProjects.AddTo(sum, alpha);
        Check(sum.GetValueOrDefault("research_speed") >= 0.139f && sum.GetValueOrDefault("deterrence") >= 0.079f,
            "host receives permanent research_speed and deterrence bonuses");
    }

    private static void TestRefusalReasons()
    {
        var (c, alpha, beta, _, stA1, stA2, stB1) = BuildFixture();

        // Wrong region owner
        Check(!GrandProjects.CanStart(c, alpha, stB1, "transcontinental_hsr", out var rOwner) && rOwner.Length > 0,
            "refuses region owned by another nation: " + rOwner);

        // Distressed-region requirement for Metropolitan Renewal
        Check(!GrandProjects.CanStart(c, alpha, stA1, "metropolitan_renewal_plan", out var rHealthy) && rHealthy.Length > 0,
            "refuses Metropolitan Renewal on healthy undamaged region: " + rHealthy);
        Check(GrandProjects.CanStart(c, alpha, stA2, "metropolitan_renewal_plan", out _),
            "allows Metropolitan Renewal on war-damaged/low-SoL region");

        // Insufficient treasury
        var savedCash = alpha.Treasury;
        alpha.Treasury = 0.01f;
        Check(!GrandProjects.CanStart(c, alpha, stA1, "sovereign_fusion_grid", out var rCash) && rCash.Length > 0,
            "refuses when treasury is insufficient: " + rCash);
        alpha.Treasury = savedCash;

        // Insufficient political credit
        var savedPp = alpha.PoliticalPoints;
        alpha.PoliticalPoints = 1f;
        Check(!GrandProjects.CanStart(c, alpha, stA1, "sovereign_fusion_grid", out var rPp) && rPp.Length > 0,
            "refuses when political credit is insufficient: " + rPp);
        alpha.PoliticalPoints = savedPp;

        // Insufficient service XP
        var savedNavy = alpha.NavalExperience;
        alpha.NavalExperience = 0f;
        Check(!GrandProjects.CanStart(c, alpha, stA1, "deepwater_megaport", out var rXp) && rXp.Length > 0,
            "refuses when naval experience is insufficient: " + rXp);
        alpha.NavalExperience = savedNavy;

        // Active cap & unique global
        new StartGrandProjectCommand(alpha.Id, "international_science_citadel", stA1.MapIndex).Apply(c);
        Check(!GrandProjects.CanStart(c, alpha, stA2, "transcontinental_hsr", out var rCap) && rCap.Length > 0,
            "refuses second active megaproject when MaxActivePerNation=1: " + rCap);
        Check(!GrandProjects.CanStart(c, beta, stB1, "international_science_citadel", out var rUnique) && rUnique.Length > 0,
            "refuses unique_global megaproject already claimed by another power: " + rUnique);
    }

    private static void TestPauseAndCancel()
    {
        var (c, alpha, _, _, stA1, _, _) = BuildFixture();
        new StartGrandProjectCommand(alpha.Id, "sovereign_reserve_vault", stA1.MapIndex).Apply(c);
        var inst = c.GrandProjects[0];
        AdvanceDays(c, 1, 15);
        var progressBeforePause = inst.ProgressDays;
        Check(progressBeforePause > 10f, "active project accumulates progress");

        new ToggleGrandProjectPauseCommand(alpha.Id, inst.Id, true).Apply(c);
        Check(inst.Status == GrandProjectStatus.Paused && inst.PausedByPlayer, "project mothballed cleanly");
        AdvanceDays(c, 20, 25);
        Check(MathF.Abs(inst.ProgressDays - progressBeforePause) < 1e-4f, "mothballed project preserves progress without advancing");

        new ToggleGrandProjectPauseCommand(alpha.Id, inst.Id, false).Apply(c);
        Check(inst.Status == GrandProjectStatus.Active, "resumed project returns to Active");
        AdvanceDays(c, 50, 10);
        Check(inst.ProgressDays > progressBeforePause + 5f, "resumed project advances again");

        var cashBeforeCancel = alpha.Treasury;
        new CancelGrandProjectCommand(alpha.Id, inst.Id).Apply(c);
        Check(c.GrandProjects.Count == 0, "CancelGrandProjectCommand removes unfinished project");
        Check(alpha.Treasury > cashBeforeCancel, "cancel refunds 25% salvage");
    }

    private static void TestJointConsortiumAndFracture()
    {
        var (c, alpha, beta, _, stA1, _, stB1) = BuildFixture();
        new StartGrandProjectCommand(alpha.Id, "transcontinental_hsr", stA1.MapIndex).Apply(c);
        var inst = c.GrandProjects[0];

        // Refused without alliance or shared IO
        Check(!GrandProjects.CanJoinConsortium(c, beta, inst, out var noAlign) && noAlign.Length > 0,
            "refuses consortium join without alliance or shared IO: " + noAlign);

        // Form alliance -> Beta joins consortium
        var rel = CampaignDiplomacy.Get(c, alpha.Id, beta.Id);
        rel.Stance = DiplomaticStance.Allied;
        var soloPace = GrandProjects.DailyBuildPace(c, inst, out _, out _);
        var soloStage2Cost = GrandProjects.StageTreasuryCost(c, alpha, GrandProjectCatalog.Find("transcontinental_hsr"), 2, false);

        Check(GrandProjects.CanJoinConsortium(c, beta, inst, out _), "allied partner can join consortium");
        new JoinGrandProjectConsortiumCommand(beta.Id, inst.Id).Apply(c);
        Check(inst.PartnerNationId == beta.Id && inst.PartnerActive, "Beta registered as active consortium partner");

        var jointPace = GrandProjects.DailyBuildPace(c, inst, out _, out _);
        var jointStage2Cost = GrandProjects.StageTreasuryCost(c, alpha, GrandProjectCatalog.Find("transcontinental_hsr"), 2, true);
        Check(jointPace > soloPace * 1.20f, $"consortium partner accelerates build pace ({soloPace:0.00} -> {jointPace:0.00})");
        Check(jointStage2Cost < soloStage2Cost * 0.75f, "consortium partner discounts host stage cost by 30%");
        Check(rel.Reasons.Any(r => r.Id == "grand_project_consortium_partner"), "consortium join records Opinion memory");

        // Finish all 3 stages -> verify both Host & Partner Dividend modifiers
        var def = GrandProjectCatalog.Find("transcontinental_hsr");
        AdvanceDays(c, 1, def.DurationForStage(1) + 5);
        new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id).Apply(c);
        AdvanceDays(c, 150, def.DurationForStage(2) + 5);
        new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id).Apply(c);
        AdvanceDays(c, 350, def.DurationForStage(3) + 5);

        Check(inst.Status == GrandProjectStatus.Completed, "joint megaproject completed");
        Check(rel.Reasons.Any(r => r.Id == "grand_project_consortium_completed"), "completion records consortium_completed Opinion");
        var partnerMods = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        GrandProjects.AddTo(partnerMods, beta);
        Check(partnerMods.GetValueOrDefault("trade_reach") >= 0.049f, "active partner receives Partner Dividend trade_reach bonus");

        // Fracture consortium via embargo -> Partner Dividend immediately suspended!
        rel.EmbargoByA = true;
        AdvanceDays(c, 500, 1);
        Check(!inst.PartnerActive, "embargo fractures active consortium");
        Check(rel.Reasons.Any(r => r.Id == "grand_project_consortium_fractured"), "fracture records Opinion penalty");
        partnerMods.Clear();
        GrandProjects.AddTo(partnerMods, beta);
        Check(partnerMods.Count == 0, "fractured partner immediately loses Partner Dividend modifiers");
    }

    private static void TestMarketSupplyCushion()
    {
        var (c, alpha, beta, _, stA1, _, stB1) = BuildFixture();
        alpha.Market[Commodity.Electronics] = new MarketQuote { Commodity = Commodity.Electronics, Satisfaction = 0.40f };
        beta.Market[Commodity.Electronics] = new MarketQuote { Commodity = Commodity.Electronics, Satisfaction = 1.00f };
        var rel = CampaignDiplomacy.Get(c, alpha.Id, beta.Id);
        rel.Stance = DiplomaticStance.Allied;

        new StartGrandProjectCommand(alpha.Id, "sovereign_semiconductor_fab", stA1.MapIndex).Apply(c);
        var inst = c.GrandProjects[0];
        var soloPace = GrandProjects.DailyBuildPace(c, inst, out var bottleGood, out var bottleSat);
        Check(!string.IsNullOrEmpty(bottleGood) && bottleSat < 0.50f && soloPace < 0.85f,
            $"severe Electronics shortage slows Semiconductor Fab ({bottleGood} @ {bottleSat:P0}, pace {soloPace:0.00}x)");

        new JoinGrandProjectConsortiumCommand(beta.Id, inst.Id).Apply(c);
        var jointPace = GrandProjects.DailyBuildPace(c, inst, out _, out var blendedSat);
        Check(blendedSat > bottleSat + 0.20f && jointPace > soloPace * 1.35f,
            $"partner with abundant Electronics cushions bottleneck ({bottleSat:P0} -> {blendedSat:P0})");
    }

    private static void TestWarDamageAndConquestInheritance()
    {
        var (c, alpha, _, gamma, stA1, _, _) = BuildFixture();
        new StartGrandProjectCommand(alpha.Id, "continental_defense_shield", stA1.MapIndex).Apply(c);
        var inst = c.GrandProjects[0];
        var def = GrandProjectCatalog.Find("continental_defense_shield");

        // Heavy war damage pauses construction
        stA1.WarDamage = 0.60f;
        AdvanceDays(c, 1, 5);
        Check(inst.Status == GrandProjectStatus.Damaged && inst.ProgressDays < 0.01f,
            "heavy war damage halts active construction");

        // Repair allows construction to complete
        stA1.WarDamage = 0.0f;
        AdvanceDays(c, 10, def.DurationForStage(1) + 5);
        new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id).Apply(c);
        AdvanceDays(c, 200, def.DurationForStage(2) + 5);
        new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id).Apply(c);
        AdvanceDays(c, 400, def.DurationForStage(3) + 5);
        Check(inst.Status == GrandProjectStatus.Completed, "repaired defense shield completes");

        // Conquest inheritance: Gamma captures stA1 -> Gamma inherits completed megaproject & modifiers!
        stA1.NationMapIndex = gamma.MapIndex;
        c.InvalidateStateIndex();
        AdvanceDays(c, 550, 1);
        Check(inst.HostNationId == gamma.Id, "conquering the region transfers megaproject ownership to victor");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "grand_project_captured"), "capture recorded in chronicle");

        var alphaMods = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var gammaMods = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        GrandProjects.AddTo(alphaMods, alpha);
        GrandProjects.AddTo(gammaMods, gamma);
        Check(alphaMods.Count == 0, "former owner loses captured megaproject modifiers");
        Check(gammaMods.GetValueOrDefault("army_defense") >= 0.079f, "conqueror inherits permanent Host modifiers");
    }

    private static void TestCovertSabotage()
    {
        var (c, alpha, _, gamma, stA1, _, _) = BuildFixture();
        new StartGrandProjectCommand(alpha.Id, "sovereign_fusion_grid", stA1.MapIndex).Apply(c);
        var inst = c.GrandProjects[0];
        AdvanceDays(c, 1, 55);
        var progressBefore = inst.ProgressDays;

        // Refused without IntelNetwork
        Check(!GrandProjects.CanSabotage(c, gamma, inst, out var noIntel) && noIntel.Length > 0,
            "sabotage refused without sufficient IntelNetwork: " + noIntel);

        // Give strong IntelNetwork in Alpha -> sabotage executes & triggers cooldown
        gamma.IntelNetworks[alpha.Id] = 0.85f;
        Check(GrandProjects.CanSabotage(c, gamma, inst, out _), "rival with strong IntelNetwork can attempt sabotage");
        new SabotageGrandProjectCommand(gamma.Id, inst.Id).Apply(c);
        Check(inst.LastSabotageDay == c.ElapsedDays, "sabotage records cooldown timestamp");
        Check(!GrandProjects.CanSabotage(c, gamma, inst, out var onCd) && onCd.Length > 0,
            "immediate repeat sabotage blocked by cooldown: " + onCd);
        Check(inst.ProgressDays <= progressBefore, "sabotage never increases project progress");
    }

    private static void TestAiParticipation()
    {
        var (c, _, beta, _, _, _, _) = BuildFixture();
        c.Defines.GrandProjectAiEnabled = true;
        beta.Treasury = 250f;
        beta.PoliticalPoints = 120f;
        // Run 35 days so beta's staggered bucket (beta.Id % 30 == 1) fires
        AdvanceDays(c, 1, 35);
        Check(c.GrandProjects.Any(p => p.HostNationId == beta.Id),
            "staggered monthly AI commissions a megaproject when holding safe reserves");
    }

    private static void TestSaveLoadAndFingerprint()
    {
        var (c, alpha, beta, _, stA1, _, stB1) = BuildFixture();
        var fpEmpty = CampaignFingerprint.Of(c);
        // Read-only UI queries must NEVER mutate fingerprint
        GrandProjects.CanStart(c, alpha, stA1, "sovereign_reserve_vault", out _);
        GrandProjects.CountActive(c, alpha.Id);
        Check(CampaignFingerprint.Of(c) == fpEmpty, "read-only GrandProjects queries do not mutate CampaignFingerprint");

        // Start project -> fingerprint changes
        var rel = CampaignDiplomacy.Get(c, alpha.Id, beta.Id);
        rel.Stance = DiplomaticStance.Allied;
        new StartGrandProjectCommand(alpha.Id, "sovereign_reserve_vault", stA1.MapIndex).Apply(c);
        var fpStarted = CampaignFingerprint.Of(c);
        Check(fpStarted != fpEmpty, "starting a megaproject changes CampaignFingerprint");

        new JoinGrandProjectConsortiumCommand(beta.Id, c.GrandProjects[0].Id).Apply(c);
        var fpPartner = CampaignFingerprint.Of(c);
        Check(fpPartner != fpStarted, "joining a consortium changes CampaignFingerprint");

        // Capture & Restore save helper round-trip
        var captured = GrandProjects.CaptureSave(c);
        var nextId = c.NextGrandProjectId;
        var (restored, _, _, _, _, _, _) = BuildFixture();
        CampaignDiplomacy.Get(restored, 0, 1).Stance = DiplomaticStance.Allied;
        GrandProjects.RestoreSave(restored, captured, nextId);
        Check(restored.GrandProjects.Count == 1 && restored.GrandProjects[0].PartnerNationId == beta.Id &&
              restored.GrandProjects[0].PartnerActive && restored.NextGrandProjectId == nextId,
            "CaptureSave/RestoreSave preserves full megaproject & consortium state");

        // Full SaveSystem JSON round-trip in temp directory
        c.SetPlayer(alpha.Id);
        c.AttachProvinceTopology(ProvinceTopology.Build(
            new ushort[] { (ushort)stA1.MapIndex, (ushort)stB1.MapIndex },
            new ushort[] { (ushort)alpha.MapIndex, (ushort)beta.MapIndex }, 2, 1, _ => false));
        var dir = Path.Combine(Path.GetTempPath(), "dof_gp_save_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var meta = SaveSystem.Save(dir, "gp_slot", c, "", "GP Test");
            var loaded = SaveSystem.Load(dir, "gp_slot", "");
            Check(loaded.Success, "SaveSystem.Load succeeds for campaign with megaproject");
            Check(loaded.Campaign.GrandProjects.Count == 1 &&
                  loaded.Campaign.GrandProjects[0].DefinitionId == "sovereign_reserve_vault" &&
                  loaded.Campaign.GrandProjects[0].PartnerNationId == beta.Id,
                "SaveSystem round-trip preserves GrandProjects list and partner state");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void TestCampaignToggleAndNetworkCodec()
    {
        var (c, alpha, _, _, stA1, _, _) = BuildFixture();
        new StartGrandProjectCommand(alpha.Id, "sovereign_reserve_vault", stA1.MapIndex).Apply(c);
        var inst = c.GrandProjects[0];
        var def = GrandProjectCatalog.Find("sovereign_reserve_vault");
        AdvanceDays(c, 1, def.DurationForStage(1) + 5);
        new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id).Apply(c);
        AdvanceDays(c, 120, def.DurationForStage(2) + 5);
        new AdvanceGrandProjectStageCommand(alpha.Id, inst.Id).Apply(c);
        AdvanceDays(c, 300, def.DurationForStage(3) + 5);
        Check(GrandProjects.Sources(alpha).Any(), "completed project grants modifiers while toggle is ON");

        // Turn OFF via campaign setting
        c.Settings[GrandProjects.SettingId] = "false";
        Check(!GrandProjects.IsEnabled(c), "setting grand_projects=false disables mechanic");
        GrandProjects.Tick(c);
        Check(!GrandProjects.Sources(alpha).Any(), "turning toggle OFF suspends active megaproject modifiers");
        Check(!GrandProjects.CanStart(c, alpha, stA1, "transcontinental_hsr", out var rOff) && rOff.Length > 0,
            "turning toggle OFF refuses new commissions: " + rOff);

        // Turn back ON -> modifiers return
        c.Settings[GrandProjects.SettingId] = "true";
        GrandProjects.RebuildAllModifiers(c);
        Check(GrandProjects.Sources(alpha).Any(), "turning toggle back ON restores completed megaproject modifiers");

        // NetworkCommandCodec round-trip for all 6 commands
        ICampaignCommand[] commands =
        {
            new StartGrandProjectCommand(0, "transcontinental_hsr", 10),
            new AdvanceGrandProjectStageCommand(0, 1),
            new ToggleGrandProjectPauseCommand(0, 1, true),
            new CancelGrandProjectCommand(0, 1),
            new JoinGrandProjectConsortiumCommand(1, 1),
            new SabotageGrandProjectCommand(2, 1)
        };
        foreach (var cmd in commands)
        {
            Check(NetworkCommandCodec.TryEncode(cmd, out var typeName, out var json) &&
                  NetworkCommandCodec.TryDecode(typeName, json, out var decoded) &&
                  decoded.GetType() == cmd.GetType(),
                $"NetworkCommandCodec round-trips {cmd.GetType().Name}");
        }
    }
}
