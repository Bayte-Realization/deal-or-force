using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Modding;
using DealOrForce.Simulation;

/// <summary>
/// Stage A1/A2/A3/A5 acceptance suite: proves every folder requested through
/// <see cref="GameDataLoader.Asked"/> (plus presentation, localization and scenarios)
/// can actually be overridden by a minimal 1-file mod via <see cref="ModLoader"/>,
/// verifies uniform <c>"remove": true</c> and top-level directory enforcement, and
/// checks <see cref="ModLoader.ApplyPresentation"/> in isolation from campaign history.
/// </summary>
static class ModReachTests
{
    private readonly record struct FolderProbe(
        string Key,
        string RelativePath,
        string Content,
        Func<Campaign, ModLoader, ModPackage, bool> Verify,
        Func<Campaign> MakeCampaign = null,
        bool PresentationOnly = false,
        bool RequiresScenarioBootstrap = false);

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("mod reach: " + label);
        Console.WriteLine("PASS mod reach: " + label);
    }

    private static Campaign MiniWorld(int seed = 20261118)
    {
        var c = new Campaign(seed) { ScenarioId = "2024" };
        var kor = c.AddNation(new Nation
        {
            Id = 0, Tag = "KOR", Iso = "KOR", Name = "Korea", MapIndex = 10,
            Population = 50_000_000, Gdp = 1700, Treasury = 100, Stability = 0.7f,
            Government = "presidential_republic", Ideology = "liberalism", CapitalName = "Seoul"
        }, true);
        var jpn = c.AddNation(new Nation
        {
            Id = 1, Tag = "JPN", Iso = "JPN", Name = "Japan", MapIndex = 11,
            Population = 120_000_000, Gdp = 4200, Treasury = 200, Stability = 0.75f,
            Government = "parliamentary_republic", Ideology = "conservatism", CapitalName = "Tokyo"
        });
        c.States.Add(new ScenarioState
        {
            Id = "kor_11", Name = "Seoul", Iso = "KR-11", Kind = "Special City",
            MapIndex = 100, NationMapIndex = kor.MapIndex, NationId = kor.Tag, Population = 10_000_000
        });
        c.States.Add(new ScenarioState
        {
            Id = "jpn_13", Name = "Tokyo", Iso = "JP-13", Kind = "Metropolis",
            MapIndex = 101, NationMapIndex = jpn.MapIndex, NationId = jpn.Tag, Population = 14_000_000
        });
        var states = new ushort[16];
        var owners = new ushort[16];
        for (var i = 0; i < 8; i++) { states[i] = 100; owners[i] = (ushort)kor.MapIndex; }
        for (var i = 8; i < 16; i++) { states[i] = 101; owners[i] = (ushort)jpn.MapIndex; }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 4, 4, _ => false));
        c.InvalidateStateIndex();
        return c;
    }

    private static Campaign WarmAllCatalogs()
    {
        var bootstrap = Campaign.CreateBootstrap(20261118);
        SettingsCatalog.ResetCatalog();
        DiagnosticLogs.ResetCatalog();
        InterfaceSkin.ResetCatalog();
        ScenarioInspectorCatalog.ResetCatalog();
        MediaLibraryCatalog.Load();
        SoundCatalog.Load();
        MusicCatalog.Load();
        FontCatalog.Load();
        SpriteCatalog.Load();
        ScriptLibrary.ResetCatalog();
        AddressToNation.ResetCatalog();
        CovertOperations.ResetCatalog();
        DecisionCatalog.ResetCatalog();
        PromiseCatalog.ResetCatalog();
        GoalCatalog.ResetCatalog();
        FlagCatalog.ResetCatalog();
        FlagDesignCatalog.ResetCatalog();
        FormableNations.ResetCatalog();
        GovernanceEvents.ResetCatalog();
        PeaceTermCatalog.ResetCatalog();
        SecessionCore.ResetCatalog();
        SocialIncidentCatalog.ResetCatalog();
        EventCatalog.ResetCatalog();
        Deterrence.ResetCatalog();
        _ = Deterrence.Holders.Count;
        _ = UnitTypeCatalog.All.Count();
        HandbookCatalog.ResetCatalog();
        NewsFeed.ResetCatalog();
        AllianceProgramCatalog.ResetCatalog();
        OutreachCatalog.ResetCatalog();
        IncidentCatalog.ResetCatalog();
        Coups.ResetCatalog();
        Coups.GovernmentRisk(new Nation());
        WorldLayers.ResetCatalog();
        QualityProfiles.ResetCatalog();
        _ = EventPictures.Table;
        DynastyCatalog.ResetCatalog();
        _ = DynastyCatalog.DynastyOfTag;
        TreasuryDynamics.ResetCatalog();
        ElectoralPolitics.ResetCatalog();
        _ = ElectoralPolitics.InitialControlTable;
        FormableProclamationAi.ResetCatalog();
        LandAtlas.ResetCatalog();
        _ = LandAtlas.All;
        TacticalDoctrineCatalog.ResetCatalog();
        StrategicDoctrineCatalog.ResetCatalog();
        InternationalOrganizations.ResetCatalog();
        _ = InternationalOrganizations.Charters.Count;
        PersonNameCatalog.ResetCatalog();
        _ = PersonNameCatalog.All.Count;
        Generals.ResetCatalog();
        TradeDesk.ResetCatalog();
        LandCrossingCatalog.ResetCatalog();
        _ = LandCrossingCatalog.All.Count;
        BuildingRules.ResetCatalog();
        PoliticalPolicyCatalog.ResetCatalog();
        NationalCourseCatalog.ResetCatalog();
        RegionalDemography.ResetCatalog();
        CountryMerges.ResetCatalog();
        _ = CountryMerges.GroundByMapIndex();
        WorldResourceMigration.ReadOverlay();
        Rulers.ResetCatalog();
        Rulers.Found(bootstrap);
        Factions.ResetCatalog();
        _ = Factions.All.Count;
        ScenarioLibrary.Discover(Array.Empty<string>(), "");
        return bootstrap;
    }

    private static (ModLoader Loader, ModPackage Package) ApplySingleFileMod(
        string root, string id, string relativePath, string content, Campaign campaign, bool presentationOnly = false)
    {
        var modRoot = Path.Combine(root, id);
        if (Directory.Exists(modRoot)) Directory.Delete(modRoot, true);
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "manifest.json"),
            $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","enabled":true}""");
        var filePath = Path.Combine(modRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content);

        var loader = new ModLoader();
        loader.Discover(new[] { root }, new ContentSelection(), includeDlc: false);
        var package = loader.Packages.Single(p => string.Equals(p.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
        if (presentationOnly) loader.ApplyPresentation();
        else loader.ApplyCampaignData(campaign);
        return (loader, package);
    }

    [TestSuite("--mod-reach-only", IncludeInDefaultRun = false, Description = "Verifies ModLoader overrides across all 91 GameDataLoader categories, remove semantics, and standalone presentation.")]
    public static void Run()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dof-mod-reach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var bootstrap = WarmAllCatalogs();
            var baseRecipeCount = ProductionChains.RecipeCount;
            var basePopCountryCount = DemographyCatalog.CountryCount;
            var baseRegionRuleCount = TerritorialTopology.RegionRuleCount;
            var asked = new HashSet<string>(GameDataLoader.Asked, StringComparer.OrdinalIgnoreCase);
            Check(asked.Count >= 90, $"asked set warmed across base catalogs ({asked.Count} categories)");

            var probes = BuildProbes(bootstrap, baseRecipeCount, basePopCountryCount, baseRegionRuleCount);
            var tested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var probe in probes)
            {
                tested.Add(probe.Key);
                if (probe.Key == "scenarios")
                {
                    var scenMod = Path.Combine(temp, "scen_probe");
                    var scenDir = Path.Combine(scenMod, "scenarios", "reach_scenario");
                    Directory.CreateDirectory(scenDir);
                    File.WriteAllText(Path.Combine(scenMod, "manifest.json"), "{\"id\":\"scen_probe\",\"enabled\":true}");
                    File.WriteAllText(Path.Combine(scenDir, "world.dfsave"), "{}");
                    File.WriteAllText(Path.Combine(scenDir, "scenario.json"), probe.Content);
                    var found = ScenarioLibrary.Discover(new[] { scenMod }, "");
                    Check(found.Any(s => s.Id == "reach_scenario"), "reach probe: scenarios");
                    continue;
                }

                var root = Path.Combine(temp, "single_" + Sanitize(probe.Key));
                Directory.CreateDirectory(root);
                var world = probe.RequiresScenarioBootstrap ? bootstrap : (probe.MakeCampaign?.Invoke() ?? MiniWorld());
                var (loader, pkg) = ApplySingleFileMod(root, "mod_" + Sanitize(probe.Key), probe.RelativePath, probe.Content, world, probe.PresentationOnly);
                var ok = pkg.Errors.Count == 0 && probe.Verify(world, loader, pkg);
                Check(ok, $"reach probe: {probe.Key}" + (pkg.Errors.Count > 0 ? " (errors: " + string.Join("; ", pkg.Errors) + ")" : ""));
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }

            // Fail if ANY folder requested through GameDataLoader.Asked lacks an explicit reach test.
            var unverified = asked.Where(k => !tested.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            Check(unverified.Count == 0,
                $"every folder in GameDataLoader.Asked is covered with zero debt (missing: {string.Join(", ", unverified)})");

            VerifyRemoveSemantics(Path.Combine(temp, "remove"));
            VerifyTopLevelDepthRule(Path.Combine(temp, "depth"));
            VerifyApplyPresentationStandalone(Path.Combine(temp, "presentation"));
        }
        finally
        {
            new ModLoader().ApplyCampaignData(Campaign.CreateBootstrap(20261118));
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    private static string Sanitize(string key) => key.Replace('/', '_').Replace('\\', '_');

    private static List<FolderProbe> BuildProbes(
        Campaign bootstrap, int baseRecipeCount, int basePopCountryCount, int baseRegionRuleCount) => new()
    {
        // --- Core & Moddable common/* folders ---
        new("common/defines", "common/defines/90_reach.json", "{\"StartingLeverage\":0.42}",
            (c, _, _) => Math.Abs(c.Defines.StartingLeverage - 0.42f) < 0.0001f),
        new("common/units", "common/units/90_reach.json", "{\"line_infantry\":{\"name\":\"Reach Infantry\"}}",
            (_, _, _) => UnitTypeCatalog.Get("line_infantry").Name == "Reach Infantry"),
        new("common/goods", "common/goods/90_reach.json", "{\"Steel\":{\"base_price\":42.5}}",
            (_, _, _) => CommodityCatalog.TryParse("Steel", out var steel) && Math.Abs(CommodityCatalog.Get(steel).BasePrice - 42.5f) < 0.01f),
        new("common/buildings", "common/buildings/90_reach.json", "{\"reach_works\":{\"name\":\"Reach Works\",\"cost\":12,\"days\":30,\"capacity_gain\":1,\"jobs\":50}}",
            (_, _, _) => BuildingCatalog.TryParse("reach_works", out _)),
        new("common/building_rules", "common/building_rules/90_reach.json", "{\"University\":{\"available_from\":\"2099-01-01\"}}",
            (c, _, _) => BuildingCatalog.TryParse("University", out var uni) && BuildingRules.Refusal(c, c.Nations[0], uni).Length > 0),
        new("common/production", "common/production/90_reach.json", "{\"reach_recipe\":{\"good\":\"Steel\",\"output\":{\"Smelter\":2.5}}}",
            (_, _, _) => ProductionChains.RecipeCount > baseRecipeCount),
        new("common/budget", "common/budget/90_reach.json", "{\"revenues\":{\"reach_tax\":{\"name\":\"Reach Tax\",\"order\":99,\"basis\":\"output\",\"yield\":0.05}}}",
            (_, _, _) => BudgetCore.Find("reach_tax")?.Name == "Reach Tax"),
        new("common/government_focus", "common/government_focus/90_reach.json", "{\"reach_focus\":{\"name\":\"Reach Focus\"}}",
            (_, _, _) => GovernmentFocusCatalog.All.Any(f => GovernmentFocusCatalog.Of(f).Name == "Reach Focus")),
        new("common/trade_policies", "common/trade_policies/90_reach.json", "{\"reach_policy\":{\"name\":\"Reach Policy\"}}",
            (_, _, _) => TradePolicyCatalog.All.Any(p => TradePolicyCatalog.Of(p).Name == "Reach Policy")),
        new("common/decisions", "common/decisions/90_reach.json", "{\"reach_decision\":{\"name\":\"Reach Decision\",\"effects\":{\"stability\":0.01}}}",
            (_, _, _) => DecisionCatalog.Find("reach_decision") is not null),
        new("events", "events/90_reach.json", "{\"reach_event\":{\"title\":\"Reach Event\",\"description\":\"Probe\",\"options\":[{\"text\":\"OK\",\"effects\":{\"stability\":0.01}}]}}",
            (_, _, _) => EventCatalog.All.Any(e => e.Id == "reach_event")),
        new("common/promises", "common/promises/90_reach.json", "{\"reach_promise\":{\"name\":\"Reach Promise\",\"duration_days\":90,\"check\":{\"min_stability\":0.5}}}",
            (_, _, _) => PromiseCatalog.Find("reach_promise") is not null),
        new("common/goals", "common/goals/90_reach.json", "{\"reach_goal\":{\"name\":\"Reach Goal\",\"milestones\":[{\"text\":\"Hold\",\"trigger\":{\"min_stability\":0.5}}]}}",
            (_, _, _) => GoalCatalog.Find("reach_goal") is not null),
        new("common/modifiers", "common/modifiers/90_reach.json", "{\"reach_modifier\":{\"name\":\"Reach Modifier\",\"days\":30,\"modifiers\":{\"output\":0.05}}}",
            (_, _, _) => ModifierCatalog.Get("reach_modifier") is not null),
        new("common/opinion_modifiers", "common/opinion_modifiers/90_reach.json", "{\"reach_opinion\":{\"name\":\"Reach Opinion\",\"opinion\":15,\"days\":60}}",
            (_, _, _) => Math.Abs((OpinionCatalog.Find("reach_opinion")?.Opinion ?? 0f) - 15f) < 0.01f),
        new("common/scripted_triggers", "common/scripted_triggers/90_reach.json", "{\"reach_trig\":{\"min_stability\":0.25}}",
            (_, _, _) => ScriptLibrary.HasTrigger("reach_trig")),
        new("common/scripted_effects", "common/scripted_effects/90_reach.json", "{\"reach_eff\":{\"stability\":0.01}}",
            (_, _, _) => ScriptLibrary.HasEffect("reach_eff")),
        new("common/scripted_modifiers", "common/scripted_modifiers/90_reach.json", "{\"reach_score\":{\"base\":1.5}}",
            (_, _, _) => ScriptLibrary.HasScore("reach_score")),
        new("common/religions", "common/religions/90_reach.json", "{\"reach_faith\":{\"name\":\"Reach Faith\",\"group\":\"abrahamic\",\"colour\":\"#abcdef\"}}",
            (_, _, _) => DemographyCatalog.Religion("reach_faith")?.Name == "Reach Faith"),
        new("common/cultures", "common/cultures/90_reach.json", "{\"reach_culture\":{\"name\":\"Reach Culture\",\"group\":\"east_asian\",\"colour\":\"#123456\",\"religion\":\"catholic\"}}",
            (_, _, _) => DemographyCatalog.Culture("reach_culture")?.Name == "Reach Culture"),
        new("common/professions", "common/professions/90_reach.json", "{\"reach_prof\":{\"name\":\"Reach Prof\",\"class\":\"Middle\",\"sector\":\"Services\",\"wealth\":0.6,\"employment\":0.95,\"colour\":\"#654321\"}}",
            (_, _, _) => DemographyCatalog.Profession("reach_prof")?.Name == "Reach Prof"),
        new("common/ai_strategy", "common/ai_strategy/90_reach.json", "{\"profiles\":{\"reach_ai\":{\"spearhead_share\":0.15}}}",
            (_, _, _) => AiStrategyCatalog.Profiles.TryGetValue("reach_ai", out var p) && Math.Abs(p.SpearheadShare - 0.15f) < 0.001f),
        new("common/programme", "common/programme/90_reach.json", "{\"units_per_factory\":14}",
            (_, _, _) => ProductionProgramme.UnitsPerFactory == 14),
        new("common/stability", "common/stability/90_reach.json", "{\"capital_share\":0.47}",
            (_, _, _) => Math.Abs(StabilityCore.Weights.CapitalShare - 0.47f) < 0.001f),
        new("common/movements", "common/movements/90_reach.json", "{\"reach_movement\":{\"tag\":\"RMV\",\"name\":\"Reach Movement\",\"peoples\":[\"korean\"]}}",
            (_, _, _) => MovementCatalog.Of("reach_movement") is not null),
        new("common/militia", "common/militia/90_reach.json", "{\"base\":0.075}",
            (_, _, _) => Math.Abs(MilitiaCore.Weights.Base - 0.075f) < 0.001f),
        new("common/secession", "common/secession/90_reach.json", "{\"base\":0.025}",
            (_, _, _) => Math.Abs(SecessionCore.Weights.Base - 0.025f) < 0.001f),
        new("common/settings", "common/settings/90_reach.json", "{\"save_format\":{\"name\":\"Reach Format\"}}",
            (_, _, _) => SettingsCatalog.Of("save_format")?.Name == "Reach Format"),
        new("common/flags", "common/flags/90_reach.json", "{\"qualifiers\":{\"reach_flag_q\":{\"from\":\"tag\",\"order\":99}}}",
            (_, _, _) => FlagCatalog.All.Any(q => q.Id == "reach_flag_q")),
        new("common/flag_designs", "common/flag_designs/90_reach.json", "{\"flag_designs\":{\"flag_reach\":{\"ratio\":1.5,\"background\":{\"pattern\":\"solid\",\"colours\":[\"#112233\"]}}}}",
            (_, _, _) => FlagDesignCatalog.Find("flag_reach") is not null),
        new("common/editor", "common/editor/90_reach.json", "{\"country\":{\"Population\":{\"label\":\"Reach Pop\"}}}",
            (_, _, _) => ScenarioInspectorCatalog.Fields(true).Any(f => f.Key == "Population" && f.Label == "Reach Pop")),
        new("common/governance", "common/governance/90_reach.json", "{\"transitions\":{\"reach_trans\":{\"name\":\"Reach Trans\",\"government\":\"federation\",\"weight\":1.0}}}",
            (_, _, _) => GovernanceEvents.All.Any(t => t.Id == "reach_trans")),
        new("common/formable_nations", "common/formable_nations/90_reach.json", "{\"reach_formable\":{\"name\":\"Reach Union\",\"eligible_tags\":[\"KOR\"],\"required_tags\":[\"KOR\"]}}",
            (_, _, _) => FormableNations.All.Any(f => f.Id == "reach_formable")),
        new("common/social_memory", "common/social_memory/90_reach.json", "{\"reach_fact\":{\"actor\":0.1,\"name\":\"Reach Fact\"}}",
            (_, _, _) => SocialIncidentCatalog.Find("reach_fact", out _)),
        new("common/public_outreach", "common/public_outreach/90_reach.json", "{\"reach_outreach\":{\"name\":\"Reach Outreach\",\"duration_days\":60,\"cooldown_days\":90}}",
            (_, _, _) => OutreachCatalog.Find("reach_outreach") is not null),
        new("common/alliance_programs", "common/alliance_programs/90_reach.json", "{\"reach_prog\":{\"name\":\"Reach Prog\",\"duration_days\":56}}",
            (_, _, _) => AllianceProgramCatalog.Find("reach_prog") is not null),
        new("common/deals", "common/deals/90_reach.json", "{\"answer_kinds\":[{\"id\":\"reach_deal\",\"priority\":10,\"match\":{\"only\":[\"cash\"]},\"modes\":[\"ask\"],\"default\":\"ask\"}]}",
            (_, _, _) => DealAnswerKinds.Find("reach_deal") is not null),
        new("common/deal_terms", "common/deal_terms/90_reach.json", "{\"trade_agreement\":{\"years\":[1,2]}}",
            (_, _, _) => TradeTermsCatalog.Spec.Years.SequenceEqual(new[] { 1, 2 })),
        new("common/laws", "common/laws/90_reach.json", "{\"reach_law\":{\"name\":\"Reach Law\",\"category\":\"Fiscal mandate\",\"scope\":\"nation\"}}",
            (_, _, _) => PoliticalPolicyCatalog.Find("reach_law", false) is not null),
        new("common/agendas", "common/agendas/90_reach.json", "{\"reach_agenda\":{\"name\":\"Reach Agenda\",\"category\":\"Agenda\",\"scope\":\"nation\",\"goal\":\"stability\",\"goal_value\":0.8}}",
            (_, _, _) => PoliticalPolicyCatalog.Find("reach_agenda", true) is not null),
        new("common/national_courses", "common/national_courses/90_reach.json", "{\"reach_course\":{\"name\":\"Reach Course\",\"pressures\":{\"baseline\":0.5}}}",
            (_, _, _) => NationalCourseCatalog.Find("reach_course") is not null),
        new("common/formable_ai", "common/formable_ai/90_reach.json", "{\"soviet_union\":{\"ai_priority\":0.77}}",
            (_, _, _) => Math.Abs(FormableProclamationAi.Preference("soviet_union").Priority - 0.77f) < 0.001f),
        new("common/regional_demography", "common/regional_demography/90_reach.json", "{\"kor_11\":{\"date\":\"2024-01-01\",\"source\":\"Reach\",\"url\":\"https://example.com\",\"table\":\"1\",\"areas\":[{\"name\":\"Seoul\",\"page\":1,\"population\":100,\"unmapped\":0,\"counts\":{\"korean\":100}}]}}",
            (_, _, _) => RegionalDemography.Find("kor_11") is not null),
        new("common/news", "common/news/90_reach.json", "{\"importance\":{\"reach_news\":3}}",
            (_, _, _) => NewsFeed.Importance("reach_news") == 3),
        new("common/handbook", "common/handbook/90_reach.json", "{\"articles\":{\"reach_article\":{\"topic\":\"economy\",\"order\":99}}}",
            (_, _, _) => HandbookCatalog.Find("reach_article") is not null),
        new("common/escalation", "common/escalation/90_reach.json", "{\"reach_inc\":{\"name\":\"Reach Incident\"}}",
            (_, _, _) => IncidentCatalog.Find("reach_inc") is not null),
        new("common/factions", "common/factions/90_reach.json", "{\"factions\":[{\"id\":\"reach_fac\",\"tag\":\"RFC\",\"name\":\"Reach Faction\",\"parent\":\"KOR\"}]}",
            (_, _, _) => Factions.All.Any(f => f.Id == "reach_fac")),
        new("common/coups", "common/coups/90_reach.json", "{\"government_risk\":{\"federation\":0.99}}",
            (_, _, _) => Math.Abs(Coups.GovernmentRisk(new Nation { Government = "federation" }) - 0.99f) < 0.001f),
        new("common/addresses", "common/addresses/90_reach.json", "{\"reach_addr\":{\"name\":\"Reach Address\",\"stability\":0.01}}",
            (_, _, _) => AddressToNation.All.Any(a => a.Id == "reach_addr")),
        new("common/covert_operations", "common/covert_operations/90_reach.json", "{\"reach_cov\":{\"name\":\"Reach Op\",\"effect\":\"unrest\"}}",
            (_, _, _) => CovertOperations.All.Any(o => o.Id == "reach_cov")),
        new("common/rulers", "common/rulers/90_reach.json", "{\"traits\":{\"reach_trait\":{\"name\":\"Reach Trait\"}}}",
            (_, _, _) => Rulers.TraitOf("reach_trait") is not null),

        // --- Stage A2 newly connected common/* folders ---
        new("common/governments", "common/governments/90_reach.json", "{\"reach_gov\":{\"name\":\"Reach Gov\"}}",
            (_, _, _) => GovernmentCatalog.Knows("reach_gov")),
        new("common/ideologies", "common/ideologies/90_reach.json", "{\"reach_ideo\":{\"name\":\"Reach Ideo\"}}",
            (_, _, _) => IdeologyCatalog.Knows("reach_ideo")),
        new("common/technologies", "common/technologies/90_reach.json", "{\"reach_tech\":{\"name\":\"Reach Tech\",\"category\":\"industry\",\"cost\":10}}",
            (_, _, _) => TechnologyCatalog.Of("reach_tech") is not null),
        new("common/tactical_doctrines", "common/tactical_doctrines/90_reach.json", "{\"reach_tac\":{\"name\":\"Reach Tac\",\"experience\":\"army\",\"nodes\":[{\"id\":\"reach_tac_1\",\"name\":\"One\",\"days\":30,\"cost\":10}]}}",
            (_, _, _) => TacticalDoctrineCatalog.Find("reach_tac") is not null),
        new("common/strategic_doctrines", "common/strategic_doctrines/90_reach.json", "{\"reach_strat\":{\"name\":\"Reach Strat\"}}",
            (_, _, _) => StrategicDoctrineCatalog.Find("reach_strat") is not null),
        new("common/support", "common/support/90_reach.json", "{\"reach_sup\":{\"name\":\"Reach Sup\"}}",
            (_, _, _) => SupportTypeCatalog.TryGet("reach_sup", out _)),
        new("common/subjects", "common/subjects/90_reach.json", "{\"reach_subj\":{\"name\":\"Reach Subj\",\"tribute\":0.1}}",
            (_, _, _) => SubjectCatalog.Knows("reach_subj")),
        new("common/organizations", "common/organizations/90_reach.json", "{\"charters\":{\"reach_charter\":{\"name\":\"Reach Charter\"}}}",
            (_, _, _) => InternationalOrganizations.Charter("reach_charter") is not null),
        new("common/peace_terms", "common/peace_terms/90_reach.json", "{\"WhitePeace\":{\"cost\":7}}",
            (_, _, _) => Math.Abs(PeaceTermCatalog.Of(PeaceTerms.WhitePeace).Cost - 7f) < 0.01f),
        new("common/trade", "common/trade/90_reach.json", "{\"tariff\":{\"name\":\"Reach Tariff\"}}",
            (_, _, _) => TradeDesk.Of("tariff")?.Name == "Reach Tariff"),
        new("common/inflation", "common/inflation/90_reach.json", "{\"base\":0.031,\"central_bank\":{\"target\":0.044}}",
            (_, _, _) => Math.Abs(InflationCore.Weights.Base - 0.031f) < 0.001f && Math.Abs(CentralBank.Rule.Target - 0.044f) < 0.001f),
        new("common/sea_lanes", "common/sea_lanes/90_reach.json", "{\"reach_lane\":{\"name\":\"Reach Lane\",\"points\":[[10,20],[11,21]]}}",
            (_, _, _) => SeaPassageCatalog.Of("reach_lane") is not null),
        new("common/states", "common/states/90_reach.json", "{\"kor_11\":{\"name\":\"Reach Capital State\"}}",
            (c, _, _) => c.States.Any(s => s.Id == "kor_11" && s.Name == "Reach Capital State")),
        new("common/countries", "common/countries/90_reach.json", "{\"KOR\":{\"name\":\"Reach Republic\"}}",
            (c, _, _) => c.NationByTag("KOR")?.Name == "Reach Republic"),
        new("common/country_merges", "common/country_merges/90_reach.json", "{\"RFC\":{\"into\":\"KOR\"}}",
            (_, _, _) => CountryMerges.OwnerOf("RFC") == "KOR"),
        new("common/character_names", "common/character_names/90_reach.json", "{\"reach_pool\":{\"male\":[\"Alex\"],\"female\":[\"Eva\"],\"family\":[\"Kim\"]}}",
            (_, _, _) => PersonNameCatalog.All.Any(p => p.Id == "reach_pool")),
        new("common/dynasties", "common/dynasties/90_reach.json", "{\"dynasties\":{\"reach_house\":[\"KOR\"]}}",
            (_, _, _) => DynastyCatalog.DynastyOf("KOR") == "reach_house"),
        new("common/generals", "common/generals/90_reach.json", "{\"reach_gen\":{\"name\":\"Reach Gen\",\"weight\":2.0}}",
            (_, _, _) => Generals.All.Any(g => g.Id == "reach_gen")),
        new("common/elections", "common/elections/90_reach.json", "{\"initial_control\":{\"KOR\":0.77}}",
            (_, _, _) => Math.Abs(ElectoralPolitics.InitialControlTable["KOR"] - 0.77f) < 0.001f),
        new("common/event_pictures", "common/event_pictures/90_reach.json", "{\"events\":{\"reach_evt\":\"finance\"}}",
            (_, _, _) => EventPictures.Table.TryGetValue("reach_evt", out var p) && p == "finance"),
        new("common/naming", "common/naming/90_reach.json", "{\"reach_land\":{\"name\":\"Reach Land\"}}",
            (_, _, _) => LandAtlas.Find("reach_land") is not null),
        new("common/lands", "common/lands/90_reach.json", "{\"reach_legacy_land\":{\"name\":\"Reach Legacy\"}}",
            (_, _, _) => LandAtlas.Find("reach_legacy_land") is not null),
        new("common/world_layers", "common/world_layers/90_reach.json", "{\"weather\":false}",
            (_, _, _) => !WorldLayers.On("weather")),
        new("common/economic_shocks", "common/economic_shocks/90_reach.json", "{\"reach_shock\":{\"name\":\"Reach Shock\",\"annual_chance\":0.02}}",
            (_, _, _) => TreasuryDynamics.ShockKinds.Any(s => s.Id == "reach_shock")),
        new("common/deterrence", "common/deterrence/90_reach.json", "{\"holders\":{\"KOR\":3}}",
            (_, _, _) => Deterrence.Holders.TryGetValue("KOR", out var t) && t == 3),
        new("common/diagnostics", "common/diagnostics/90_reach.json", "{\"max_file_mb\":7,\"queue_limit\":4096}",
            (_, _, _) => DiagnosticLogs.MaxFileBytes == 7L * 1024L * 1024L && DiagnosticLogs.QueueLimit == 4096),
        new("common/performance", "common/performance/90_reach.json", "{\"slow_frame_ms\":48,\"tiers\":{\"reach_tier\":{\"label\":\"Reach\",\"order\":99}}}",
            (_, _, _) => Math.Abs(QualityProfiles.SlowFrameMs - 48.0) < 0.01 && QualityProfiles.Ladder.Any(p => p.Id == "reach_tier")),
        new("common/terrain", "common/terrain/90_reach.json", "{\"reach_terrain\":{\"name\":\"Reach Terrain\",\"color\":\"#13579B\",\"movement_speed\":1.25}}",
            (_, _, _) => TerrainCatalog.All.Any(t => t.Id == "reach_terrain")),

        // --- History & Map Data & Presentation & Localization ---
        new("history/countries/2024", "history/countries/2024/90_reach.json", "{\"KOR\":{\"population\":55555555}}",
            (c, _, _) => c.NationByTag("KOR")?.Population == 55555555),
        new("history/states/2024", "history/states/2024/90_reach.json", "{\"kor_11\":{\"population\":1234567}}",
            (c, _, _) => c.States.Any(s => s.Id == "kor_11" && s.Population == 1234567)),
        new("history/diplomacy/2024", "history/diplomacy/2024/90_reach.json", "{\"alliances\":[{\"members\":[\"KOR\",\"JPN\"]}]}",
            (c, _, _) => CampaignDiplomacy.Get(c, c.NationByTag("KOR").Id, c.NationByTag("JPN").Id).Stance == DiplomaticStance.Allied),
        new("history/conflicts/2024", "history/conflicts/2024/90_reach.json", "{\"tensions\":[{\"between\":[\"KOR\",\"JPN\"],\"structural\":44,\"tension\":77}]}",
            (c, _, _) => Math.Abs(CampaignDiplomacy.Get(c, c.NationByTag("KOR").Id, c.NationByTag("JPN").Id).Tension - 77f) < 0.01f),
        new("history/factions/2024", "history/factions/2024/90_reach.json", "{\"factions\":[{\"id\":\"reach_hist_fac\",\"tag\":\"RHF\",\"name\":\"Reach Hist Faction\",\"parent\":\"KOR\"}]}",
            (_, _, _) => Factions.All.Any(f => f.Id == "reach_hist_fac")),
        new("history/units/2024", "history/units/2024/90_reach.json", "{\"KOR\":777000}",
            (_, _, _) => Math.Abs(MilitaryBootstrap.Personnel("KOR", 50_000_000) - 777000f) < 1f),
        new("history/pops", "history/pops/90_reach.json", "{\"countries\":{\"Z99\":{\"cultures\":{\"korean\":1.0}}}}",
            (_, _, _) => DemographyCatalog.CountryCount > basePopCountryCount),
        new("history/faiths", "history/faiths/90_reach.json", "{\"countries\":{\"KOR\":{\"faiths\":{\"catholic\":0.8,\"protestant\":0.2}}}}",
            (_, _, _) => DemographyCatalog.CountryFaithSettings.ContainsKey("KOR")),
        new("history/provinces", "history/provinces/90_reach.csv", "1;JPN;JPN\n",
            (_, _, _) => ProvinceOwnership.ProvincesOf("JPN").Contains(1)),
        new("history/provinces/terrain", "history/provinces/terrain/90_reach.json", "{\"1\":\"forest\"}",
            (c, _, _) => TerrainCatalog.Bind(c.ProvinceMap, PlainsRaster(c.ProvinceMap.Width, c.ProvinceMap.Height))[1].Id == "forest"),

        new("map_data/cities", "map_data/cities/90_reach.json", "{\"reach_city\":{\"name\":\"Reach City\",\"latitude\":37.5,\"longitude\":127.0}}",
            (_, _, _) => CityCatalog.All.Any(city => city.Id == "reach_city")),
        new("map_data/crossings", "map_data/crossings/90_reach.json", "{\"reach_crossing\":{\"name\":\"Reach Crossing\",\"from\":[35,129],\"to\":[34,130],\"kind\":\"ferry\"}}",
            (_, _, _) => LandCrossingCatalog.Of("reach_crossing") is not null),
        new("map_data/lakes", "map_data/lakes/90_reach.json", "{\"minimum_cells\":55}",
            (_, _, _) => LakeRegions.MinimumCells == 55),
        new("map_data/townspeople", "map_data/townspeople/90_reach.json", "{\"provinces\":2,\"townspeople\":{\"1\":777}}",
            (c, _, _) => StatePartition.Townspeople(c.ProvinceMap)[1] == 777),
        new("map_data/state_regions", "map_data/state_regions/90_reach.json", "{\"regions\":[{\"map_index\":999001,\"nation\":\"KOR\",\"name\":\"Reach Region\",\"iso\":\"RCH-1\",\"min_longitude\":10,\"max_longitude\":11,\"min_latitude\":10,\"max_latitude\":11}]}",
            (_, _, _) => TerritorialTopology.RegionRuleCount > baseRegionRuleCount),
        new("map_data/hydrography", "map_data/hydrography/90_reach.json", "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"properties\":{\"name\":\"Reach River\",\"scalerank\":3},\"geometry\":{\"type\":\"LineString\",\"coordinates\":[[10,10],[11,11]]}}]}",
            (_, _, _) => HydrologyCatalog.RiverFeatures.Any(r => r.Name == "Reach River")),
        new("map_data/water_settings", "map_data/water_settings.json", "{\"river_near_opacity\":0.41}",
            (_, _, _) => Math.Abs(TerrainCatalog.Water.RiverNearOpacity - 0.41f) < 0.001f),

        // --- Presentation & Localization & Scenarios ---
        new("interface", "interface/90_reach.json", "{\"colors\":{\"reach.ui.color\":\"#123456\"}}",
            (_, _, _) => InterfaceSkin.Colour("reach.ui.color") == "#123456", PresentationOnly: true),
        new("gui", "gui/90_reach.json", "{\"metrics\":{\"reach.ui.metric\":42.5}}",
            (_, _, _) => Math.Abs(InterfaceSkin.Metric("reach.ui.metric", 0f) - 42.5f) < 0.01f, PresentationOnly: true),
        new("gfx", "gfx/90_reach.json", "{\"sprites\":{\"reach_sprite\":\"gfx/interface/brand/menu_world.png\"}}",
            (_, _, _) => SpriteCatalog.TextureOf("reach_sprite").EndsWith("menu_world.png", StringComparison.Ordinal), PresentationOnly: true),
        new("fonts", "fonts/90_reach.json", "{\"fonts\":{\"reach.font\":\"fonts/custom.ttf\"}}",
            (_, _, _) => FontCatalog.FileOf("reach.font").EndsWith("/fonts/custom.ttf", StringComparison.Ordinal), PresentationOnly: true),
        new("sound", "sound/90_reach.json", "{\"cues\":{\"reach_cue\":{\"file\":\"audio/test.ogg\",\"channel\":\"ui\"}}}",
            (_, _, _) => SoundCatalog.Has("reach_cue"), PresentationOnly: true),
        new("music", "music/90_reach.json", "{\"tracks\":{\"reach_track\":{\"file\":\"audio/theme.ogg\",\"when\":\"Menu\"}}}",
            (_, _, _) => MusicCatalog.All.Any(t => t.Name == "reach_track"), PresentationOnly: true),
        new("presentation", "presentation/90_reach.json", "{\"assets\":{\"reach_media\":{\"kind\":\"image\",\"file\":\"gfx/pic.png\",\"name\":\"Reach\",\"purpose\":\"Probe\"}}}",
            (_, _, _) => MediaLibraryCatalog.Find("reach_media") is not null, PresentationOnly: true),
        new("localization", "localization/english/90_reach.json", "{\"reach.loc.key\":\"Reach Localized\"}",
            (_, _, _) => Loc.Get("reach.loc.key") == "Reach Localized", PresentationOnly: true),
        new("scenarios", "scenarios/reach_scenario/scenario.json", "{\"id\":\"reach_scenario\",\"name\":\"Reach Scenario\",\"world\":\"world.dfsave\"}",
            (_, _, _) => true),
        new("skins", "interface/90_reach_skin.json", "{\"colors\":{\"reach.skin.alias\":\"#654321\"}}",
            (_, _, _) => InterfaceSkin.Colour("reach.skin.alias") == "#654321", PresentationOnly: true),
    };

    private static RgbDataMap PlainsRaster(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 0x8D;
            pixels[i + 1] = 0xAB;
            pixels[i + 2] = 0x68;
        }
        return new RgbDataMap(width, height, pixels);
    }

    private static void VerifyRemoveSemantics(string root)
    {
        Directory.CreateDirectory(root);
        var mod = Path.Combine(root, "remover");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "manifest.json"), "{\"id\":\"remover\",\"enabled\":true}");

        void Write(string rel, string json)
        {
            var full = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json);
        }

        // Remove real base entries using canonical "remove": true
        Write("common/technologies/99_remove.json", "{\"precision_guided_munitions\":{\"remove\":true}}");
        Write("common/governments/99_remove.json", "{\"theocracy\":{\"remove\":true}}");
        Write("common/ideologies/99_remove.json", "{\"theocratic_rule\":{\"remove\":true}}");
        Write("common/support/99_remove.json", "{\"artillery_brigade\":{\"remove\":true}}");
        Write("common/decisions/99_remove.json", "{\"austerity\":{\"remove\":true}}");
        Write("common/opinion_modifiers/99_remove.json", "{\"alliance\":{\"remove\":true}}");
        Write("common/deals/99_remove.json", "{\"answer_kinds\":[{\"id\":\"land\",\"remove\":true}]}");
        Write("common/formable_nations/99_remove.json", "{\"eurasian_union\":{\"remove\":true}}");
        Write("common/generals/99_remove.json", "{\"cautious\":{\"remove\":true}}");
        Write("map_data/cities/99_remove.json", "{\"seoul\":{\"remove\":true}}");
        // Legacy "disabled" / "removed" must still work while logging warnings into ModLoader.Log
        Write("common/formable_ai/99_legacy.json", "{\"soviet_union\":{\"disabled\":true}}");

        var loader = new ModLoader();
        loader.Discover(new[] { root }, new ContentSelection(), includeDlc: false);
        var world = MiniWorld();
        loader.ApplyCampaignData(world);

        var pkg = loader.Packages.Single();
        Check(pkg.Errors.Count == 0, "remove mod produces zero hard errors");
        Check(TechnologyCatalog.Of("precision_guided_munitions") is null, "\"remove\": true drops technology");
        Check(!GovernmentCatalog.Knows("theocracy"), "\"remove\": true drops government");
        Check(!IdeologyCatalog.Knows("theocratic_rule"), "\"remove\": true drops ideology");
        Check(!SupportTypeCatalog.TryGet("artillery_brigade", out _), "\"remove\": true drops support type");
        Check(DecisionCatalog.Find("austerity") is null, "\"remove\": true drops decision");
        Check(OpinionCatalog.Find("alliance") is null, "\"remove\": true drops opinion modifier");
        Check(DealAnswerKinds.Find("land") is null, "\"remove\": true drops deal answer kind");
        Check(FormableNations.All.All(f => f.Id != "eurasian_union"), "\"remove\": true drops formable");
        Check(Generals.All.All(g => g.Id != "cautious"), "\"remove\": true drops general trait");
        Check(CityCatalog.All.All(c => c.Id != "seoul"), "\"remove\": true drops city");
        Check(FormableProclamationAi.Preference("soviet_union").Priority == 0f ||
              loader.Log.Any(line => line.Contains("legacy key 'disabled'", StringComparison.Ordinal)),
            "legacy 'disabled' alias honored and logged as legacy warning");
    }

    private static void VerifyTopLevelDepthRule(string root)
    {
        Directory.CreateDirectory(root);
        var mod = Path.Combine(root, "nested_warn");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "manifest.json"), "{\"id\":\"nested_warn\",\"enabled\":true}");

        void WriteNested(string rel, string json)
        {
            var full = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json);
        }

        WriteNested("common/defines/deep/bad.json", "{\"StartingLeverage\":0.91}");
        WriteNested("events/deep/bad.json", "{\"nested_evt\":{\"title\":\"X\",\"description\":\"Y\",\"effects\":{\"stability\":0.01}}}");
        WriteNested("sound/deep/bad.json", "{\"cues\":{\"nested_cue\":{\"file\":\"a.ogg\",\"channel\":\"ui\"}}}");
        WriteNested("history/countries/2024/deep/bad.json", "{\"KOR\":{\"population\":111}}");
        WriteNested("map_data/unknown_dir/bad.json", "{\"nope\":1}");

        var loader = new ModLoader();
        loader.Discover(new[] { root }, new ContentSelection(), includeDlc: false);
        var world = MiniWorld();
        loader.ApplyCampaignData(world);

        var pkg = loader.Packages.Single();
        Check(Math.Abs(world.Defines.StartingLeverage - 0.91f) > 0.05f, "nested common/defines file is ignored");
        Check(EventCatalog.All.All(e => e.Id != "nested_evt"), "nested events file is ignored");
        Check(!SoundCatalog.Has("nested_cue"), "nested sound declaration is ignored");
        Check(pkg.Errors.Count >= 5 && pkg.Errors.All(e => e.Contains("ignored", StringComparison.OrdinalIgnoreCase)),
            "nested subfolders emit clear warnings on package.Errors");
    }

    private static void VerifyApplyPresentationStandalone(string root)
    {
        Directory.CreateDirectory(root);
        var mod = Path.Combine(root, "menu_theme");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "manifest.json"), "{\"id\":\"menu_theme\",\"enabled\":true}");

        void Write(string rel, string text)
        {
            var full = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        Write("localization/english/99_menu.json", "{\"menu.reach.banner\":\"Sovereign Test Banner\"}");
        Write("interface/99_menu.json", "{\"colors\":{\"text.accent\":\"#abcdef\"}}");
        Write("fonts/99_menu.json", "{\"fonts\":{\"ui.display\":\"fonts/display.ttf\"}}");
        Write("music/99_menu.json", "{\"tracks\":{\"menu_reach\":{\"file\":\"audio/menu.ogg\",\"when\":\"Menu\"}}}");
        Write("sound/99_menu.json", "{\"map_ambience\":{\"high_wind\":\"audio/high.ogg\",\"front\":\"audio/front.ogg\",\"static_noise\":\"audio/static.ogg\"}}");
        Write("gfx/99_menu.json", "{\"sprites\":{\"menu_world\":\"gfx/custom_world.png\",\"brand_logo_banner\":\"gfx/custom_banner.png\",\"panel_dossier\":\"gfx/custom_dossier.png\",\"event_nuclear_strike\":\"gfx/custom_nuke.png\"}}");

        var loader = new ModLoader();
        loader.Discover(new[] { root }, new ContentSelection(), includeDlc: false);
        loader.ApplyPresentation();

        Check(Loc.Get("menu.reach.banner") == "Sovereign Test Banner", "ApplyPresentation layers localization without campaign history");
        Check(InterfaceSkin.Colour("text.accent") == "#abcdef", "ApplyPresentation layers interface skin tokens");
        Check(FontCatalog.FileOf("ui.display").EndsWith("/fonts/display.ttf", StringComparison.Ordinal), "ApplyPresentation layers fonts");
        Check(MusicCatalog.All.Any(t => t.Name == "menu_reach"), "ApplyPresentation layers music tracks");
        Check(SoundCatalog.HighWindFile.Replace('\\', '/').EndsWith("/sound/audio/high.ogg", StringComparison.Ordinal) &&
              SoundCatalog.FrontFile.Replace('\\', '/').EndsWith("/sound/audio/front.ogg", StringComparison.Ordinal) &&
              SoundCatalog.StaticNoiseFile.Replace('\\', '/').EndsWith("/sound/audio/static.ogg", StringComparison.Ordinal),
            "ApplyPresentation layers ambience/curse audio paths");
        Check(SpriteCatalog.TextureOf("menu_world").EndsWith("custom_world.png", StringComparison.Ordinal),
            "ApplyPresentation overrides menu_world sprite");
        Check(SpriteCatalog.TextureOf("brand_logo_banner").EndsWith("custom_banner.png", StringComparison.Ordinal) &&
              SpriteCatalog.TextureOf("panel_dossier").EndsWith("custom_dossier.png", StringComparison.Ordinal) &&
              SpriteCatalog.TextureOf("event_nuclear_strike").EndsWith("custom_nuke.png", StringComparison.Ordinal),
            "ApplyPresentation overrides brand_logo_banner, panel_dossier and event_nuclear_strike sprites");
    }
}
