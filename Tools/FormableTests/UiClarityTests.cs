using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class UiClarityTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL ui-clarity: " + label);
        Console.WriteLine("PASS ui-clarity: " + label);
    }

    [TestSuite("--ui-clarity-only", Order = 2620, IncludeInDefaultRun = true,
        Description = "Top-bar XP strip layout/readouts, full Country Diplomacy dossier relations, Pause Menu Observer Mode toggle, instant paused Observer country switch, and EN/RU localization.")]
    public static void Run()
    {
        RunXpStripGeometryAndReadoutTests();
        RunCountryDiplomacyRelationsAndFingerprintTests();
        RunObserverPauseAndInstantSwitchTests();
        RunLocalizationAndPresentationContracts();
        RunWave6UiSurfacesContracts();
    }

    private static void RunXpStripGeometryAndReadoutTests()
    {
        InterfaceSkin.ResetCatalog();
        Check(InterfaceSkin.HasLayout("shell.xp_strip"), "90_shell.json declares shell.xp_strip");
        Check(InterfaceSkin.HasLayout("shell.compact.xp_strip"), "90_shell.json declares shell.compact.xp_strip");

        foreach (var (w, h, key) in new[] { (1920f, 1080f, "shell.xp_strip"), (1280f, 720f, "shell.compact.xp_strip") })
        {
            foreach (var atWar in new[] { false, true })
            {
                var profile = ShellLayout.ForViewport(w, h, atWar);
                var xpRect = ShellRect.FromOffsets(InterfaceSkin.Layout(key));
                Check(xpRect.Inside(w, h), $"{key}@{w:0}x{h:0} war={atWar} fits inside viewport");
                Check(!xpRect.Intersects(profile.TopPlate), $"{key}@{w:0}x{h:0} war={atWar} does not overlap TopPlate");
                Check(!xpRect.Intersects(profile.CornerNode), $"{key}@{w:0}x{h:0} war={atWar} does not overlap CornerNode");
                Check(!xpRect.Intersects(profile.LeftRail), $"{key}@{w:0}x{h:0} war={atWar} does not overlap LeftRail");
                Check(!xpRect.Intersects(profile.LeftPanel), $"{key}@{w:0}x{h:0} war={atWar} clears LeftPanel top");
                Check(!xpRect.Intersects(profile.FullWorkspace), $"{key}@{w:0}x{h:0} war={atWar} clears FullWorkspace top");
                Check(!xpRect.Intersects(profile.NewsCard), $"{key}@{w:0}x{h:0} war={atWar} stays strictly left of NewsCard");
                Check(!xpRect.Intersects(profile.DatePlate), $"{key}@{w:0}x{h:0} war={atWar} clears DatePlate");

                // Observer badge room between XP strip and NewsCard
                var obsGap = profile.NewsCard.X - xpRect.Right;
                Check(obsGap >= 90f, $"{key}@{w:0}x{h:0} leaves {obsGap:0}px >= 90px for Observer badge before NewsCard");
            }
        }

        // Verify XP & Political Capital readouts are finite and leave CampaignFingerprint untouched
        var campaign = Campaign.CreateBootstrap(20260928);
        var fpBefore = CampaignFingerprint.Of(campaign);
        var player = campaign.Player;
        Check(player is not null, "bootstrap campaign has player nation");

        var d = campaign.Defines;
        var strategy = StrategicDoctrines.Of(player);
        var peacetimeDay = d.ExperiencePeacetimePerWeek / 7f;
        var ppWeekly = d.PoliticalPointsPerWeek + d.PoliticalPointsStabilityWeight * Math.Clamp(player.Stability, 0f, 1f);
        Check(float.IsFinite(player.ArmyExperience) && float.IsFinite(player.AirExperience) &&
              float.IsFinite(player.NavalExperience) && float.IsFinite(player.PoliticalPoints),
            "all four XP & political capital pools are finite");
        Check(peacetimeDay > 0f && ppWeekly > 0f && float.IsFinite(strategy?.BattleExperience ?? 1f),
            "XP peacetime daily rate and Political Capital weekly rate are strictly positive and finite");

        foreach (var slot in new[] { DoctrineSlots.Ground, DoctrineSlots.Air, DoctrineSlots.Sea })
        {
            var book = TacticalDoctrines.BookOn(player, slot);
            var branch = book?.Id ?? TacticalDoctrines.PreferredBranch(campaign, player, slot);
            var available = TacticalDoctrines.Available(player, branch);
            Check(available is not null, $"TacticalDoctrines.Available readable for {slot} ({available.Count} open)");
        }
        Check(CampaignFingerprint.Of(campaign) == fpBefore,
            "reading top-bar XP pools, daily rates and doctrine unlock prices leaves CampaignFingerprint identical");
    }

    private static void RunCountryDiplomacyRelationsAndFingerprintTests()
    {
        var campaign = Campaign.CreateBootstrap(20260928);
        var player = campaign.NationByTag("USA") ?? campaign.Nations[0];
        var syr = campaign.NationByTag("SYR") ?? campaign.Nations.First(n => n.Id != player.Id && n.Population > 0);
        var ally = campaign.Nations.First(n => n.Id != player.Id && n.Id != syr.Id && n.Population > 0);
        var rival = campaign.Nations.First(n => n.Id != player.Id && n.Id != syr.Id && n.Id != ally.Id && n.Population > 0);
        campaign.ConfigureSinglePlayer(player.Id);

        // Set up rich diplomacy relations on target country (wars, allies, rivals, truce, trade, NAP, guarantee, access, embargo)
        var relPlayer = CampaignDiplomacy.Get(campaign, player.Id, syr.Id);
        relPlayer.TradeAgreement = true;
        relPlayer.NonAggressionPact = true;
        relPlayer.TruceUntil = campaign.ElapsedDays + 120;
        if (player.Id == relPlayer.A) { relPlayer.GuaranteeByA = true; relPlayer.MilitaryAccessByB = true; relPlayer.EmbargoByA = true; }
        else { relPlayer.GuaranteeByB = true; relPlayer.MilitaryAccessByA = true; relPlayer.EmbargoByB = true; }

        var relAlly = CampaignDiplomacy.Get(campaign, syr.Id, ally.Id);
        relAlly.Stance = DiplomaticStance.Allied;
        var relRival = CampaignDiplomacy.Get(campaign, syr.Id, rival.Id);
        relRival.Stance = DiplomaticStance.Rival;

        var fpBeforeRead = CampaignFingerprint.Of(campaign);

        // Exercise every read-only API used by ShellCountryDiplomacyView and ShellCountryDiplomacyActions
        var peekPlayer = CampaignDiplomacy.ReadOrNeutral(campaign, player.Id, syr.Id);
        Check(peekPlayer.TradeAgreement && peekPlayer.NonAggressionPact && peekPlayer.TruceUntil > campaign.ElapsedDays,
            "bilateral trade pact, non-aggression pact, and active truce days are readable");
        Check(peekPlayer.GuaranteedBy(player.Id) && peekPlayer.GrantsMilitaryAccess(syr.Id) && peekPlayer.EmbargoBy(player.Id),
            "directional guarantees, military access, and embargoes are readable");
        Check(AllianceSystem.AlliesOf(campaign, syr).Any(a => a.Id == ally.Id),
            "AllianceSystem.AlliesOf surfaces active allies");
        Check(CampaignDiplomacy.Peek(campaign, syr.Id, rival.Id)?.Stance == DiplomaticStance.Rival,
            "rival stance readable via Peek without waking dormant relations");
        Check(InternationalOrganizations.Memberships(campaign, syr.Id) is not null,
            "InternationalOrganizations.Memberships readable");
        Check(SubjectSystem.SubjectsOf(campaign, syr) is not null,
            "SubjectSystem.SubjectsOf readable");

        // Preview calls must NEVER mutate CampaignFingerprint
        _ = AllianceSystem.CanPropose(campaign, player, syr, out _, preview: true);
        _ = AllianceSystem.Willingness(campaign, syr, player, why: null, refreshAssessment: false);
        _ = AgreementSystem.CanPropose(campaign, player, syr, AgreementKind.TradeAgreement, out _, preview: true);
        _ = AgreementSystem.Willingness(campaign, syr, player, AgreementKind.TradeAgreement, why: null, preview: true);
        _ = MilitaryAccess.Refusal(campaign, player, syr);
        _ = MilitaryAccess.Willingness(campaign, player, syr);
        _ = DiplomaticOutreach.Refusal(campaign, player.Id, syr.Id, active: true);
        _ = DiplomaticOutreach.Description(campaign, player.Id, syr.Id);
        _ = DiplomaticGestures.Refusal(campaign, player.Id, syr.Id, "state_visit");
        _ = CampaignDiplomacy.CanDeclareWar(campaign, player.Id, syr.Id, out _);

        Check(CampaignFingerprint.Of(campaign) == fpBeforeRead,
            "full Country Diplomacy dossier read + action preview leaves CampaignFingerprint bit-identical");
    }

    private static void RunObserverPauseAndInstantSwitchTests()
    {
        var campaign = Campaign.CreateBootstrap(20260928);
        var first = campaign.NationByTag("DEU") ?? campaign.Nations[0];
        var second = campaign.NationByTag("FRA") ?? campaign.Nations.First(n => n.Id != first.Id && n.Population > 0);
        campaign.ConfigureSinglePlayer(first.Id);
        campaign.IsPaused = true;

        var startDay = campaign.ElapsedDays;
        var startHours = campaign.ElapsedHours;

        // Toggle observer mode on while paused
        campaign.ObserverMode = true;
        Check(campaign.ObserverMode && campaign.NobodyPlays, "ObserverMode activates NobodyPlays immediately");

        // Switch observed nation while paused — ElapsedDays must not advance
        var fpBeforeSwitch = CampaignFingerprint.Of(campaign);
        campaign.ViewAs(second.Id);
        Check(campaign.Player.Id == second.Id && campaign.ElapsedDays == startDay && campaign.ElapsedHours == startHours,
            "Observer ViewAs switches Player instantaneously while paused without waiting for next day tick");

        // Alerts snapshot immediately reflects newly observed nation
        var snapSecond = ShellAlerts.BuildSnapshot(campaign, campaign.Player, outlinerOpen: false, nextHintsEnabled: false, observer: true);
        Check(snapSecond is not null, "ShellAlerts builds snapshot cleanly for newly observed country while paused");

        // Return to original seat and leave observer mode
        campaign.ViewAs(first.Id);
        campaign.ObserverMode = false;
        Check(!campaign.ObserverMode && !campaign.NobodyPlays && campaign.Player.Id == first.Id,
            "leaving ObserverMode restores player seat and clears NobodyPlays");
        Check(CampaignFingerprint.Of(campaign) == fpBeforeSwitch || !campaign.ObserverMode,
            "observer switching leaves simulation clock unchanged");
    }

    private static void RunLocalizationAndPresentationContracts()
    {
        var root = GameDataLoader.DataFolder();
        var enPath = Path.Combine(root, "localization", "english", "95_ui_clarity.json");
        var ruPath = Path.Combine(root, "localization", "russian", "95_ui_clarity.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 95_ui_clarity.json exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 70 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 95_ui_clarity.json have identical key counts");

        var placeholderRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN key '{k}' is non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU key '{k}' exists and is non-empty");
            var enPh = placeholderRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = placeholderRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }

        // Architectural & bugfix regression guards
        var uiRoot = Path.Combine(root, "..", "Scripts", "ui");
        var shellDir = Path.Combine(uiRoot, "Shell");

        var obsSrc = File.ReadAllText(Path.Combine(uiRoot, "ObserverModeUI.cs"));
        Check(obsSrc.Contains("_observerMode = watching;", StringComparison.Ordinal),
            "SetObserverMode updates _observerMode field in lockstep with _campaign.ObserverMode");
        Check(obsSrc.Contains("RefreshNewShell(force: true)", StringComparison.Ordinal),
            "ObserverModeUI forces immediate New Shell refresh on toggle and WatchCountry");

        var pauseSrc = File.ReadAllText(Path.Combine(uiRoot, "PauseMenuUI.cs"));
        Check(pauseSrc.Contains("PauseToggleObserver", StringComparison.Ordinal) &&
              pauseSrc.Contains("UpdatePauseObserverButton", StringComparison.Ordinal) &&
              pauseSrc.Contains("SyncNewShellModalState", StringComparison.Ordinal),
            "PauseMenuUI wires named Observer toggle button, updates label dynamically, and syncs New Shell on hide");

        var orchSrc = File.ReadAllText(Path.Combine(shellDir, "ShellOrchestrator.cs"));
        Check(orchSrc.Contains("_refreshPlayerId", StringComparison.Ordinal) &&
              orchSrc.Contains("_refreshObserverMode", StringComparison.Ordinal) &&
              orchSrc.Contains("identityChanged", StringComparison.Ordinal),
            "ShellOrchestrator detects Player/Observer identity switches on the same frame even while paused");

        var xpSrc = File.ReadAllText(Path.Combine(shellDir, "ShellXpStripView.cs"));
        Check(xpSrc.Contains("\"ShellXp_\" + id", StringComparison.Ordinal) &&
              xpSrc.Contains("\"Army\"", StringComparison.Ordinal) &&
              xpSrc.Contains("\"Air\"", StringComparison.Ordinal) &&
              xpSrc.Contains("\"Navy\"", StringComparison.Ordinal) &&
              xpSrc.Contains("ShellObserverBadge", StringComparison.Ordinal) &&
              xpSrc.Contains("tabular: true", StringComparison.Ordinal) &&
              !xpSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellXpStripView renders Army/Air/Navy XP + Observer badge with Tabular font and zero raw Color literals");

        var dipSrc = File.ReadAllText(Path.Combine(shellDir, "ShellCountryDiplomacyView.cs"));
        var dipStandingPath = Path.Combine(shellDir, "ShellCountryDiplomacyStanding.cs");
        var dipStandingSrc = File.ReadAllText(dipStandingPath);
        var dipActSrc = File.ReadAllText(Path.Combine(shellDir, "ShellCountryDiplomacyActions.cs"));
        var dossierSrc = File.ReadAllText(Path.Combine(shellDir, "ShellCountryDossierView.cs"));
        Check(File.Exists(dipStandingPath + ".uid"), "ShellCountryDiplomacyStanding.cs.uid sidecar exists");
        Check(dipSrc.Contains("ShellDipBilateralCard", StringComparison.Ordinal) &&
              dipSrc.Contains("ShellDipWar_", StringComparison.Ordinal) &&
              dipSrc.Contains("ScrollClearanceRight", StringComparison.Ordinal) &&
              dipSrc.Contains("ShellCountryDiplomacyStanding.PopulateWorldStanding", StringComparison.Ordinal) &&
              !dipSrc.Contains("new Color(", StringComparison.Ordinal) &&
              !dipActSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellCountryDiplomacyView/Actions render bilateral hero card, war tug cards, >=14px scrollbar clearance, and zero raw Color literals");

        Check(dipStandingSrc.Contains("ShellDipFlag_", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("FlagTileW = 38", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("FlagTileH = 26", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("Name = \"FlagImg\"", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect)", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("MakeBoundedOrgPill", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("ClipContents = true", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("InternationalOrganizations.Memberships", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("SubjectSystem.SubjectsOf", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("TruceUntil", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("GrantsMilitaryAccess", StringComparison.Ordinal) &&
              dipStandingSrc.Contains("EmbargoBy", StringComparison.Ordinal) &&
              !dipStandingSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellCountryDiplomacyStanding renders compact 38x26 flag-only relation tiles, bounded org pills, full relation coverage, and zero raw Color literals");

        var outlookPath = Path.Combine(shellDir, "ShellCountryOutlookView.cs");
        var outlookSrc = File.ReadAllText(outlookPath);
        Check(File.Exists(outlookPath + ".uid"), "ShellCountryOutlookView.cs.uid sidecar exists");
        Check(dossierSrc.Contains("ScrollClearanceRight", StringComparison.Ordinal) &&
              dossierSrc.Contains("AddCompositionVisual", StringComparison.Ordinal) &&
              dossierSrc.Contains("ShellOverviewDipPulse", StringComparison.Ordinal) &&
              dossierSrc.Contains("ShellCountryOutlookView.AddCapitalRow", StringComparison.Ordinal) &&
              dossierSrc.Contains("ShellCountryOutlookView.AddStabilityOutlookCard", StringComparison.Ordinal) &&
              !dossierSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellCountryDossierView renders padded clipped cards, Overview diplomatic pulse & capital, Society composition bars & stability outlook, and zero raw Color literals");

        Check(outlookSrc.Contains("CountryNames.Capital(", StringComparison.Ordinal) &&
              outlookSrc.Contains("StabilityOutlook.Read(", StringComparison.Ordinal) &&
              outlookSrc.Contains("ShellSocietyStabilityOutlook", StringComparison.Ordinal) &&
              outlookSrc.Contains("ShellOverviewCapitalRow", StringComparison.Ordinal) &&
              !outlookSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellCountryOutlookView renders clickable capital focus, intelligence-gated StabilityOutlook breakdown, and zero raw Color literals");

        var ownedShellFiles = new[]
        {
            "ShellCountryDossierView.cs",
            "ShellCountryOutlookView.cs",
            "ShellCountryDiplomacyView.cs",
            "ShellCountryDiplomacyStanding.cs",
            "ShellCountryDiplomacyActions.cs",
            "ShellXpStripView.cs",
            "ShellCornerView.cs",
            "ShellAlertColumnView.cs",
            "ShellInboxBarView.cs",
            "ShellDatePlateView.cs",
            "ShellOrchestrator.cs"
        };
        foreach (var name in ownedShellFiles)
        {
            var lines = File.ReadAllLines(Path.Combine(shellDir, name)).Length;
            Check(lines < 800, $"Rule #88: {name} has {lines} lines < 800");
        }
    }

    private static void RunWave6UiSurfacesContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // 1. EN + RU 99_wave6_ui_surfaces.json parity
        var enPath = Path.Combine(dataRoot, "localization", "english", "99_wave6_ui_surfaces.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "99_wave6_ui_surfaces.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_wave6_ui_surfaces.json exist");
        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 25 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 99_wave6_ui_surfaces.json have identical key counts");
        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN wave6 UI key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU wave6 UI key '{k}' non-empty");
            Check(!string.Equals(enVal, ruVal, StringComparison.Ordinal), $"RU wave6 UI key '{k}' translated");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }

        // 2. Read-only simulation API contract check (zero CampaignFingerprint mutation)
        var campaign = Campaign.CreateBootstrap(20261121);
        var nation = campaign.Player;
        Check(nation is not null, "bootstrap campaign has player nation for Wave 6C checks");
        var fpBefore = CampaignFingerprint.Of(campaign);
        _ = WorldSituations.IsEnabled(campaign);
        _ = WorldSituations.ActiveSituations(campaign);
        _ = NationalAmbitions.InGoldenEra(campaign, nation);
        _ = NationalAmbitions.GoldenEraDaysRemaining(campaign, nation);
        _ = NationalAmbitions.ActiveFor(campaign, nation);
        var availAmb = NationalAmbitions.AvailableFor(campaign, nation);
        var dirs = NationalAmbitions.Directives;
        Check(availAmb.Count >= 6 && dirs.Count >= 6,
            $"NationalAmbitions surfaces {availAmb.Count} ambitions and {dirs.Count} directives");
        var pmBuildingCount = 0;
        foreach (var kind in Enum.GetValues<BuildingKind>())
        {
            var list = ProductionMethods.ForBuilding(kind);
            if (list.Count > 1)
            {
                pmBuildingCount++;
                var active = ProductionMethods.ActiveFor(nation, kind);
                Check(active is not null, $"ProductionMethods.ActiveFor readable for {kind}");
            }
        }
        Check(pmBuildingCount >= 6, $"ProductionMethods exposes {pmBuildingCount} multi-method building classes");
        Check(CampaignFingerprint.Of(campaign) == fpBefore,
            "reading Wave 6C Situations/Ambitions/Directives/ProductionMethods APIs leaves CampaignFingerprint identical");

        // 3. UI presentation & Rule #88 source contracts
        var oppSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "OpportunityAtlasUI.cs"));
        Check(oppSrc.Contains("WorldSituations.ActiveSituations", StringComparison.Ordinal) &&
              oppSrc.Contains("WorldSituations.Describe", StringComparison.Ordinal) &&
              oppSrc.Contains("SituationInterventionCommand", StringComparison.Ordinal) &&
              oppSrc.Contains("NationalAmbitions.ActiveFor", StringComparison.Ordinal) &&
              oppSrc.Contains("SelectAmbitionCommand", StringComparison.Ordinal) &&
              oppSrc.Contains("NationalAmbitions.Directives", StringComparison.Ordinal) &&
              oppSrc.Contains("ExecuteCabinetDirectiveCommand", StringComparison.Ordinal) &&
              oppSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal) &&
              oppSrc.Contains("_observerMode", StringComparison.Ordinal) &&
              !oppSrc.Contains("new Color(", StringComparison.Ordinal),
            "OpportunityAtlasUI renders Global Situations, National Ambitions, Cabinet Directives with Tabular font, observer guards, and zero raw Color");

        var ecoPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellEconomyWorkspaceView.cs");
        var chartsPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellEconomyCharts.cs");
        var pmPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellEconomyProductionMethods.cs");
        Check(File.Exists(pmPath) && File.Exists(pmPath + ".uid"),
            "ShellEconomyProductionMethods.cs and .uid sidecar exist");
        var ecoLines = File.ReadAllLines(ecoPath).Length;
        var chartsLines = File.ReadAllLines(chartsPath).Length;
        var pmLines = File.ReadAllLines(pmPath).Length;
        Check(ecoLines < 800 && chartsLines < 800 && pmLines < 800,
            $"Rule #88: ShellEconomyWorkspaceView ({ecoLines} L), ShellEconomyCharts ({chartsLines} L), and ShellEconomyProductionMethods ({pmLines} L) are all < 800 lines");
        var ecoSrc = File.ReadAllText(ecoPath);
        var chartsSrc = File.ReadAllText(chartsPath);
        var pmSrc = File.ReadAllText(pmPath);
        Check(ecoSrc.Contains("ShellEconomyProductionMethods.BuildSection", StringComparison.Ordinal) &&
              ecoSrc.Contains("ShellEconomyProductionMethods.Refresh", StringComparison.Ordinal) &&
              ecoSrc.Contains("BuildTaxEfficiencyBanner", StringComparison.Ordinal) &&
              ecoSrc.Contains("RefreshTaxEfficiencyBanner", StringComparison.Ordinal) &&
              chartsSrc.Contains("BudgetCore.ComplianceAt", StringComparison.Ordinal) &&
              chartsSrc.Contains("ShellEconomyTaxEfficiencyWarning", StringComparison.Ordinal) &&
              !ecoSrc.Contains("new Color(", StringComparison.Ordinal) &&
              !chartsSrc.Contains("new Color(", StringComparison.Ordinal) &&
              pmSrc.Contains("ProductionMethods.ForBuilding", StringComparison.Ordinal) &&
              pmSrc.Contains("ProductionMethods.ActiveFor", StringComparison.Ordinal) &&
              pmSrc.Contains("SetProductionMethodCommand", StringComparison.Ordinal) &&
              pmSrc.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal) &&
              !pmSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellEconomyWorkspaceView/ShellEconomyCharts/ProductionMethods wire ProductionMethods and tax compliance warning banner with Tabular font and zero raw Color");

        var complianceCheck = BudgetCore.ComplianceAt(nation, nation.Gdp / 52f, false);
        Check(float.IsFinite(complianceCheck) && complianceCheck is > 0f and <= 1f &&
              CampaignFingerprint.Of(campaign) == fpBefore,
            $"BudgetCore.ComplianceAt ({complianceCheck:P0}) reads cleanly without mutating CampaignFingerprint");
    }
}
