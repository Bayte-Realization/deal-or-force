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
        RunWaveShellClarityAaaContracts();
        RunPrestigeTouchUxContracts();
        RunTimelapseSpinAndShellHealthContracts();
        RunAaaUiPolishContracts();
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

    private static void RunWaveShellClarityAaaContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // 1. EN + RU 99_shell_clarity_aaa.json parity & zero key conflicts across localization tables
        var enAaaPath = Path.Combine(dataRoot, "localization", "english", "99_shell_clarity_aaa.json");
        var ruAaaPath = Path.Combine(dataRoot, "localization", "russian", "99_shell_clarity_aaa.json");
        Check(File.Exists(enAaaPath) && File.Exists(ruAaaPath), "EN and RU 99_shell_clarity_aaa.json exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enAaaPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruAaaPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 30 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 99_shell_clarity_aaa.json have identical key counts");

        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(k.StartsWith("shell.aaa.", StringComparison.Ordinal), $"new key '{k}' uses shell.aaa.* namespace");
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN AAA key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU AAA key '{k}' non-empty");
            if (Regex.IsMatch(enVal, "[A-Za-z]"))
                Check(!string.Equals(enVal, ruVal, StringComparison.Ordinal), $"RU AAA key '{k}' translated distinctly");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }

        foreach (var lang in new[] { "english", "russian" })
        {
            var seenElsewhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(dataRoot, "localization", lang), "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), "99_shell_clarity_aaa.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    seenElsewhere.Add(prop.Name);
            }
            var collisions = enMap.Keys.Where(seenElsewhere.Contains).ToArray();
            Check(collisions.Length == 0, $"{lang} 99_shell_clarity_aaa.json has zero conflicts ({string.Join(", ", collisions)})");
        }

        // 2. ShellLayout 8/9 cell geometry + zero overlap across desktop & touch SafeArea + AuditRestHud
        InterfaceSkin.ResetCatalog();
        var layoutCases = new (float W, float H, bool Touch, float SL, float ST, float SR, float SB)[]
        {
            (1920f, 1080f, false, 0f, 0f, 0f, 0f),
            (1280f, 720f, false, 0f, 0f, 0f, 0f),
            (1440f, 900f, false, 0f, 0f, 0f, 0f),
            (1280f, 720f, true, 56f, 12f, 24f, 8f),
            (1440f, 720f, true, 72f, 16f, 32f, 12f),
            (2400f, 1080f, true, 88f, 24f, 40f, 16f),
        };
        foreach (var c in layoutCases)
        {
            foreach (var atWar in new[] { false, true })
            {
                var p = ShellLayout.ForViewport(c.W, c.H, atWar, c.Touch, c.SL, c.ST, c.SR, c.SB);
                var expectedCells = atWar ? 9 : 8;
                var topLimit = c.Touch ? p.TouchModeBar.X - 8f : p.DatePlate.X - 8f;
                var xp = ShellLayout.XpStripRect(p);
                Check(p.TopPlate.Right <= topLimit + 0.5f,
                    $"ShellLayout@{c.W}x{c.H} touch={c.Touch} war={atWar}: TopPlate.Right ({p.TopPlate.Right:0}) <= limit ({topLimit:0})");
                Check(p.StatCellWidth * expectedCells <= p.TopPlate.W + 0.5f,
                    $"ShellLayout@{c.W}x{c.H} touch={c.Touch} war={atWar}: {expectedCells} cells x {p.StatCellWidth:0} <= TopPlate.W ({p.TopPlate.W:0})");
                Check(!p.TopPlate.Intersects(p.NewsCard) && !p.TopPlate.Intersects(xp) &&
                      !p.TopPlate.Intersects(p.LeftPanel) && !p.TopPlate.Intersects(p.DatePlate) &&
                      !p.TopPlate.Intersects(p.LeftRail) && (!c.Touch || !p.TopPlate.Intersects(p.TouchModeBar)),
                    $"ShellLayout@{c.W}x{c.H} touch={c.Touch} war={atWar}: zero TopPlate intersections");
            }
        }
        foreach (var (w, h) in new[] { (1920f, 1080f), (1280f, 720f) })
        {
            foreach (ShellCampaignState state in Enum.GetValues<ShellCampaignState>())
            {
                var census = ShellLayout.AuditRestHud(w, h, state, outlinerOpen: true, newsCardVisible: true, undoCellVisible: true);
                Check(census.Overlaps.Count == 0 && census.Offscreen.Count == 0,
                    $"AuditRestHud@{w}x{h} {state}: zero overlaps ({census.Overlaps.Count}) & offscreen ({census.Offscreen.Count})");
            }
        }
        var shellLayoutSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "Simulation", "Shell", "ShellLayout.cs"));
        Check(shellLayoutSrc.Contains("warCells = 9, peaceCells = 8", StringComparison.Ordinal) &&
              !shellLayoutSrc.Contains("using Godot", StringComparison.Ordinal) &&
              !shellLayoutSrc.Contains("DealOrForce.Platform", StringComparison.Ordinal),
            "ShellLayout.cs sets peaceCells=8, warCells=9 with zero Godot/Platform references");

        // 3. UI files line caps, unique .cs.uid sidecars, zero raw new Color(, and source contracts
        var shellDir = Path.Combine(repoRoot, "Scripts", "ui", "Shell");
        var cornerPath = Path.Combine(shellDir, "ShellCornerView.cs");
        var cornerTelPath = Path.Combine(shellDir, "ShellCornerTelemetry.cs");
        var workPath = Path.Combine(shellDir, "ShellWorkPanelView.cs");
        var dossierPath = Path.Combine(shellDir, "ShellCountryDossierView.cs");
        var dossierTelPath = Path.Combine(shellDir, "ShellCountryTelemetryView.cs");
        var ecoPath = Path.Combine(shellDir, "ShellEconomyWorkspaceView.cs");
        var chartsPath = Path.Combine(shellDir, "ShellEconomyCharts.cs");

        foreach (var (path, maxLines) in new[]
                 {
                     (cornerPath, 760), (cornerTelPath, 800), (workPath, 800),
                     (dossierPath, 760), (dossierTelPath, 800), (ecoPath, 800), (chartsPath, 800)
                 })
        {
            Check(File.Exists(path) && File.Exists(path + ".uid"), $"{Path.GetFileName(path)} and .cs.uid exist");
            var lines = File.ReadAllLines(path);
            Check(lines.Length < maxLines, $"{Path.GetFileName(path)} has {lines.Length} lines < {maxLines}");
            var text = string.Join("\n", lines);
            Check(!text.Contains("new Color(", StringComparison.Ordinal), $"{Path.GetFileName(path)} has zero raw Color literals");
        }

        var allUids = Directory.GetFiles(Path.Combine(repoRoot, "Scripts"), "*.cs.uid", SearchOption.AllDirectories)
            .Select(f => File.ReadAllText(f).Trim())
            .ToArray();
        Check(allUids.Length == allUids.Distinct(StringComparer.Ordinal).Count(),
            $"All {allUids.Length} .cs.uid sidecars under Scripts/ are unique");

        var cornerSrc = File.ReadAllText(cornerPath);
        var cornerTelSrc = File.ReadAllText(cornerTelPath);
        Check(cornerSrc.Contains("\"Population\", \"Nation\", \"icon_population\", \"shell.aaa.cap_population\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("ToggleScreen?.Invoke(\"Nation\")", StringComparison.Ordinal) &&
              cornerSrc.Contains("atWar ? 9 : 8", StringComparison.Ordinal) &&
              cornerSrc.Contains("\"GDP\", \"Population\", \"Debt\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("world.RenderSnapshot", StringComparison.Ordinal) &&
              cornerSrc.Contains("ShellCornerTelemetry.SplitMoney", StringComparison.Ordinal) &&
              cornerSrc.Contains("ShellCornerTelemetry.SplitPeople", StringComparison.Ordinal) &&
              cornerSrc.Contains("ShellCornerTelemetry.BuildPopulationTooltip", StringComparison.Ordinal) &&
              cornerSrc.Contains("ShellCornerTelemetry.BuildGdpTooltip", StringComparison.Ordinal) &&
              cornerSrc.Contains("ShellCornerTelemetry.BuildManpowerTooltip", StringComparison.Ordinal) &&
              cornerSrc.Contains("ShellCornerTelemetry.BuildFlagTooltip", StringComparison.Ordinal),
            "ShellCornerView wires Population cell, 8/9 count, RenderSnapshot rank cache, and ShellCornerTelemetry helpers/tooltips");
        Check(cornerTelSrc.Contains("SplitMoney(", StringComparison.Ordinal) &&
              cornerTelSrc.Contains("SplitPeople(", StringComparison.Ordinal) &&
              cornerTelSrc.Contains("FormatTopDrivers(", StringComparison.Ordinal) &&
              cornerTelSrc.Contains("SampleGdpDeltaPct(", StringComparison.Ordinal) &&
              cornerTelSrc.Contains("1_000_000_000", StringComparison.Ordinal) &&
              cornerTelSrc.Contains("shell.aaa.unit_billion_people", StringComparison.Ordinal),
            "ShellCornerTelemetry implements extracted formatters and billion-scale SplitPeople");

        var workSrc = File.ReadAllText(workPath);
        Check(workSrc.Contains("world.RenderSnapshot", StringComparison.Ordinal) &&
              workSrc.Contains("_subtitleLabel.Text = $\"{rankText} · {Loc.Get(PoliticsCore.GovernmentOf(nation).Name)}", StringComparison.Ordinal) &&
              workSrc.Contains("(Fmt.People(nation.Population), Loc.Get(\"Population\")", StringComparison.Ordinal) &&
              workSrc.Contains("(Fmt.Money(nation.Gdp), Loc.Get(\"GDP\")", StringComparison.Ordinal) &&
              workSrc.Contains("Loc.Get(\"shell.div_short\")", StringComparison.Ordinal) &&
              workSrc.Contains("Loc.Get(\"Army\")", StringComparison.Ordinal),
            "ShellWorkPanelView includes rank in subtitle, headline KPIs (Population, GDP, Stability · Army), and RenderSnapshot rank lookup");

        var dossierSrc = File.ReadAllText(dossierPath);
        var dossierTelSrc = File.ReadAllText(dossierTelPath);
        Check(dossierSrc.Contains("ShellOverviewKpis", StringComparison.Ordinal) &&
              dossierSrc.Contains("ShellCountryTelemetryView.AddTerritoryIndustryCard", StringComparison.Ordinal) &&
              dossierSrc.Contains("ShellCountryTelemetryView.AddSocietyDemographyCard", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("ShellOverviewTerritoryIndustryCard", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("ShellSocietyDemographyCard", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("Fmt.PeopleExact(nation.Population)", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("HouseholdEconomy.Employment", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("BasicNeedsCoverage", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("CountArmedForces", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("ConstructionCost.Limit", StringComparison.Ordinal),
            "ShellCountryDossierView + ShellCountryTelemetryView wire 6-pill standing, territory/industry card, and weighted national demography card");

        var ecoSrc = File.ReadAllText(ecoPath);
        var chartsSrc = File.ReadAllText(chartsPath);
        Check(ecoSrc.Contains("ShellEconomyCharts.BuildBaseMetricsStrip", StringComparison.Ordinal) &&
              ecoSrc.Contains("ShellEconomyCharts.RefreshBaseMetricsStrip", StringComparison.Ordinal) &&
              ecoSrc.Contains("shell.aaa.eco_sub_base_fmt", StringComparison.Ordinal) &&
              chartsSrc.Contains("ShellEconomyBaseStrip", StringComparison.Ordinal) &&
              chartsSrc.Contains("FormatGdpPerCapita", StringComparison.Ordinal) &&
              chartsSrc.Contains("ConstructionCost.Limit", StringComparison.Ordinal),
            "ShellEconomyWorkspaceView + ShellEconomyCharts surface GDP, Population, GDP per Capita, and active Constructions");

        // 4. Read-only telemetry simulation & CampaignFingerprint invariance
        var campaign = Campaign.CreateBootstrap(20261124);
        for (var d = 0; d < 14; d++) campaign.Tick();
        var probe = campaign.NationByTag("CHN") ?? campaign.NationByTag("USA") ?? campaign.Player;
        Check(probe is not null && probe.Population > 0, "multi-region nation available for AAA telemetry read");
        var fpBefore = CampaignFingerprint.Of(campaign);

        var states = campaign.StatesOf(probe);
        var totalBuildings = states.Sum(s => s.Buildings.Values.Sum());
        var activeSites = campaign.Construction.Count(p => p.NationId == probe.Id);
        var siteLimit = ConstructionCost.Limit(campaign, probe, campaign.Defines.MaxConstructionProjects);
        var hulls = campaign.Fleets.Where(f => f.NationId == probe.Id && f.Hulls > 0).Sum(f => f.Hulls);
        var wings = campaign.SupportFormations.Count(f =>
            f.NationId == probe.Id && f.Strength > 0.05f &&
            SupportTypeCatalog.TryGet(f.TypeId, out var st) && st.Kind == SupportKind.Air);
        var gdpPerHead = probe.Population > 0 ? (probe.Gdp * 1_000_000_000.0) / probe.Population : 0.0;
        var ranks = CountryRankings.Read(campaign, CountryRankings.Weights.Default);
        double popSum = 0, youngSum = 0, workSum = 0, oldSum = 0, empSum = 0, needsSum = 0;
        foreach (var s in states)
        {
            if (s.Population <= 0) continue;
            popSum += s.Population;
            youngSum += s.Population * s.YoungShare;
            workSum += s.Population * DemographicCycle.WorkingShare(s);
            oldSum += s.Population * s.OldShare;
            empSum += s.Population * HouseholdEconomy.Employment(s);
            needsSum += s.Population * s.BasicNeedsCoverage;
        }
        Check(states.Length > 0 && totalBuildings > 0 && siteLimit >= 1 &&
              double.IsFinite(gdpPerHead) && gdpPerHead > 0 &&
              hulls >= 0 && wings >= 0 && ranks.Any(r => r.NationId == probe.Id) &&
              popSum > 0 && youngSum / popSum + workSum / popSum + oldSum / popSum > 0.99 &&
              empSum / popSum is >= 0 and <= 1.0 && needsSum / popSum is >= 0 and <= 1.0 &&
              activeSites >= 0,
            $"AAA telemetry metrics readable and finite for {probe.Tag} (states={states.Length}, bld={totalBuildings}, hulls={hulls}, wings={wings})");
        Check(CampaignFingerprint.Of(campaign) == fpBefore,
            "reading all AAA shell telemetry metrics leaves CampaignFingerprint bit-identical");
    }

    private static void RunPrestigeTouchUxContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // 1. EN + RU 99_prestige_touch_ux.json parity, translation, placeholder & zero-collision check
        var enPath = Path.Combine(dataRoot, "localization", "english", "99_prestige_touch_ux.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "99_prestige_touch_ux.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_prestige_touch_ux.json exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 20 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 99_prestige_touch_ux.json have identical key counts");

        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN prestige/touch key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU prestige/touch key '{k}' non-empty");
            if (Regex.IsMatch(enVal, "[A-Za-z]"))
                Check(!string.Equals(enVal, ruVal, StringComparison.Ordinal), $"RU prestige/touch key '{k}' translated distinctly");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }

        foreach (var lang in new[] { "english", "russian" })
        {
            var seenElsewhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(dataRoot, "localization", lang), "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), "99_prestige_touch_ux.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    seenElsewhere.Add(prop.Name);
            }
            var collisions = enMap.Keys.Where(seenElsewhere.Contains).ToArray();
            Check(collisions.Length == 0, $"{lang} 99_prestige_touch_ux.json has zero conflicts ({string.Join(", ", collisions)})");
        }

        // 2. Source contracts across all 5 deliverables
        string ReadUi(string rel) => File.ReadAllText(Path.Combine(repoRoot, rel));

        var ctrlSrc = ReadUi("Scripts/ui/Controls/ControlsIntegrationUI.cs");
        Check(ctrlSrc.Contains("LayoutPreset.TopLeft", StringComparison.Ordinal) &&
              ctrlSrc.Contains("isTouch ? 368f : 336f", StringComparison.Ordinal) &&
              (ctrlSrc.Contains("Fixed(_compactProvinceTitle)", StringComparison.Ordinal) ||
               ctrlSrc.Contains("_compactProvinceTitle = Fixed(", StringComparison.Ordinal)) &&
              ctrlSrc.Contains("MaxLinesVisible = 3", StringComparison.Ordinal) &&
              ctrlSrc.Contains("CloseCompactProvinceInfo", StringComparison.Ordinal) &&
              ctrlSrc.Contains("OpenFullRegionCardBtn", StringComparison.Ordinal) &&
              ctrlSrc.Contains("OpenNewShellMapSelection(resolvedPick)", StringComparison.Ordinal) &&
              ctrlSrc.Contains("_compactProvincePopover.ResetSize()", StringComparison.Ordinal) &&
              !ctrlSrc.Contains("new Color(", StringComparison.Ordinal),
            "ControlsIntegrationUI bounds CompactProvinceInfo width/height, prevents wrap growth, and routes OpenFullRegionCardBtn");

        var workSrc = ReadUi("Scripts/ui/Shell/ShellWorkPanelView.cs");
        var ecoSrc = ReadUi("Scripts/ui/Shell/ShellEconomyWorkspaceView.cs");
        var alertSrc = ReadUi("Scripts/ui/Shell/ShellAlertColumnView.cs");
        var mapBarSrc = ReadUi("Scripts/ui/Shell/ShellMapBarView.cs");
        var msgSrc = ReadUi("Scripts/ui/Shell/ShellMessageModalView.cs");
        var armySrc = ReadUi("Scripts/ui/ArmyPanelUI.cs");
        var mapSelSrc = ReadUi("Scripts/ui/MapSelectionUI.cs");
        var movSrc = ReadUi("Scripts/ui/MovementUI.cs");
        var qaSrc = ReadUi("Scripts/ui/Controls/QuickAmountUI.cs");
        var rankSrc = ReadUi("Scripts/ui/CountryRankingsUI.cs");

        Check(workSrc.Contains("touch ? new Vector2(42, 42) : new Vector2(32, 32)", StringComparison.Ordinal) &&
              workSrc.Contains("touch ? 9f : 7f", StringComparison.Ordinal) &&
              workSrc.Contains("touch ? 38", StringComparison.Ordinal) &&
              workSrc.Contains("touch ? (primary ? 44 : 40)", StringComparison.Ordinal) &&
              ecoSrc.Contains("CloseShellEconomyWorkspace", StringComparison.Ordinal) &&
              ecoSrc.Contains("isTouch ? new Vector2(44, 44) : new Vector2(34, 34)", StringComparison.Ordinal) &&
              alertSrc.Contains("ShellNewsCloseButton", StringComparison.Ordinal) &&
              alertSrc.Contains("new Vector2(34, 34)", StringComparison.Ordinal) &&
              mapBarSrc.Contains("CloseShellMapPopover", StringComparison.Ordinal) &&
              mapBarSrc.Contains("new Vector2(42, 42)", StringComparison.Ordinal) &&
              msgSrc.Contains("CloseShellNewsModal", StringComparison.Ordinal) &&
              msgSrc.Contains("new Vector2(42, 42)", StringComparison.Ordinal) &&
              armySrc.Contains("CloseArmyPanel", StringComparison.Ordinal) &&
              mapSelSrc.Contains("CloseCountryCard", StringComparison.Ordinal) &&
              mapSelSrc.Contains("CloseRegionCard", StringComparison.Ordinal) &&
              movSrc.Contains("CloseMovementPanel", StringComparison.Ordinal) &&
              qaSrc.Contains("CloseQuickAmount", StringComparison.Ordinal) &&
              !qaSrc.Contains("new Color(", StringComparison.Ordinal) &&
              rankSrc.Contains("isTouch ? 42 : 44, isTouch ? 40 : 34", StringComparison.Ordinal),
            "All 10 Android/desktop close buttons and ShellWorkPanelView touch targets satisfy enlarged hit-target contracts with zero raw Color");

        var touchBarSrc = ReadUi("Scripts/ui/Shell/ShellTouchModeBarView.cs");
        Check(touchBarSrc.Contains("ShellTouchMode_Select", StringComparison.Ordinal) &&
              touchBarSrc.Contains("ShellTouchMode_Order", StringComparison.Ordinal) &&
              touchBarSrc.Contains("ShellTouchMode_BoxSelect", StringComparison.Ordinal) &&
              touchBarSrc.Contains("ShellTouchMode_Pan", StringComparison.Ordinal) &&
              touchBarSrc.Contains("TooltipFor(", StringComparison.Ordinal) &&
              touchBarSrc.Contains("mobile.mode.order.oneshot.tip", StringComparison.Ordinal) &&
              touchBarSrc.Contains("mobile.mode.order.sticky.tip", StringComparison.Ordinal) &&
              !touchBarSrc.Contains("new Color(", StringComparison.Ordinal),
            "ShellTouchModeBarView preserves node names and attaches localized hover/long-press tooltips");

        Check(movSrc.Contains("if (!state.Landless)", StringComparison.Ordinal) &&
              movSrc.IndexOf("MovementFigure(\"income\"", StringComparison.Ordinal) > movSrc.IndexOf("if (!state.Landless)", StringComparison.Ordinal) &&
              movSrc.IndexOf("MovementHeading(\"MONEY IN AND OUT\")", StringComparison.Ordinal) > movSrc.LastIndexOf("if (!state.Landless)", StringComparison.Ordinal),
            "MovementUI hides annual income figure and MONEY IN AND OUT ledger rows when state.Landless is true");

        var cornerSrc = ReadUi("Scripts/ui/Shell/ShellCornerView.cs");
        var cornerTelSrc = ReadUi("Scripts/ui/Shell/ShellCornerTelemetry.cs");
        var dossierTelSrc = ReadUi("Scripts/ui/Shell/ShellCountryTelemetryView.cs");
        Check(rankSrc.Contains("\"rankings.weight.prestige\", 15", StringComparison.Ordinal) &&
              rankSrc.Contains("CountryRankings.Metric.Prestige", StringComparison.Ordinal) &&
              rankSrc.Contains("row.Prestige", StringComparison.Ordinal) &&
              rankSrc.Contains("ui.prestige.ranking_formula_fmt", StringComparison.Ordinal) &&
              cornerSrc.Contains("\"rankings.weight.prestige\", 15", StringComparison.Ordinal) &&
              workSrc.Contains("\"rankings.weight.prestige\", 15", StringComparison.Ordinal) &&
              cornerTelSrc.Contains("ui.prestige.flag_tip_fmt", StringComparison.Ordinal) &&
              cornerTelSrc.Contains("player.Prestige", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("Loc.Get(\"Prestige\")", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("nation.Prestige", StringComparison.Ordinal) &&
              dossierTelSrc.Contains("PrestigeSystem.ComputeEquilibrium", StringComparison.Ordinal),
            "Prestige & 5-weight ranking formula are wired across CountryRankingsUI, ShellCorner, ShellWorkPanelView, and Country Overview KPIs");

        // 3. Read-only simulation Prestige & CountryRankings check
        var campaign = Campaign.CreateBootstrap(20261124);
        var fpBefore = CampaignFingerprint.Of(campaign);
        var weights = CountryRankings.Weights.Default;
        Check(Math.Abs(weights.Economy - 42) < 0.001 && Math.Abs(weights.Population - 14) < 0.001 &&
              Math.Abs(weights.FieldPower - 12) < 0.001 && Math.Abs(weights.Technology - 17) < 0.001 &&
              Math.Abs(weights.Prestige - 15) < 0.001,
            "CountryRankings.Weights.Default uses 42/14/12/17/15 mix");
        var rows = CountryRankings.Read(campaign, weights);
        var byPrestige = CountryRankings.Query(rows, CountryRankings.Metric.Prestige, descending: true);
        var eq = PrestigeSystem.ComputeEquilibrium(campaign, campaign.Player);
        Check(rows.Count > 0 && byPrestige.Count == rows.Count &&
              float.IsFinite(eq) && eq is >= 0f and <= PrestigeSystem.MaxPrestige &&
              rows.All(r => double.IsFinite(r.Prestige) && r.Prestige >= 0.0),
            "CountryRankings.Query(Metric.Prestige) and PrestigeSystem.ComputeEquilibrium expose finite non-negative prestige scores");
        Check(CampaignFingerprint.Of(campaign) == fpBefore,
            "reading Prestige ranking snapshot preserves CampaignFingerprint");
    }

    private static void RunTimelapseSpinAndShellHealthContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // 1. EN + RU 99_timelapse_spin.json parity, translation, placeholder & zero-collision check
        var enPath = Path.Combine(dataRoot, "localization", "english", "99_timelapse_spin.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "99_timelapse_spin.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_timelapse_spin.json exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 6 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 99_timelapse_spin.json have identical key counts");

        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(k.StartsWith("shell.showcase_", StringComparison.Ordinal), $"key '{k}' uses shell.showcase_* namespace");
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN timelapse key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU timelapse key '{k}' non-empty");
            if (Regex.IsMatch(enVal, "[A-Za-z]"))
                Check(!string.Equals(enVal, ruVal, StringComparison.Ordinal), $"RU timelapse key '{k}' translated distinctly");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }

        foreach (var lang in new[] { "english", "russian" })
        {
            var seenElsewhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(dataRoot, "localization", lang), "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), "99_timelapse_spin.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    seenElsewhere.Add(prop.Name);
            }
            var collisions = enMap.Keys.Where(seenElsewhere.Contains).ToArray();
            Check(collisions.Length == 0, $"{lang} 99_timelapse_spin.json has zero conflicts ({string.Join(", ", collisions)})");
        }

        // 2. ShellMapBarView, ShellContext, NewShellUI, MapControlsUI & ShellMinimapView source contracts
        string ReadRel(string rel) => File.ReadAllText(Path.Combine(repoRoot, rel));

        var mapBarPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellMapBarView.cs");
        var mapBarLines = File.ReadAllLines(mapBarPath);
        var mapBarSrc = string.Join("\n", mapBarLines);
        Check(mapBarLines.Length < 800 && File.Exists(mapBarPath + ".uid"),
            $"ShellMapBarView.cs has {mapBarLines.Length} lines < 800 and .cs.uid sidecar");
        Check(mapBarSrc.Contains("ShellShowcasePanBtn", StringComparison.Ordinal) &&
              mapBarSrc.Contains("ShellShowcasePanMoreBtn", StringComparison.Ordinal) &&
              mapBarSrc.Contains("\"icon_world\"", StringComparison.Ordinal) &&
              mapBarSrc.Contains("\"F6\"", StringComparison.Ordinal) &&
              mapBarSrc.Contains("ToggleShowcasePan", StringComparison.Ordinal) &&
              mapBarSrc.Contains("IsShowcasePan", StringComparison.Ordinal) &&
              mapBarSrc.Contains("\"shell.brass_hi\"", StringComparison.Ordinal) &&
              mapBarSrc.Contains("MathF.Max(44f, medW)", StringComparison.Ordinal) &&
              !mapBarSrc.Contains("new Color(", StringComparison.Ordinal) &&
              !mapBarSrc.Contains("Colors.Black", StringComparison.Ordinal),
            "ShellMapBarView wires ShellShowcasePanBtn + More chip, Ui.Colour only, and >=44px touch hit target");

        // Verify 7 medallions fit inside profile.MapBar across desktop (1920/1280) and Android touch (1280/2400)
        InterfaceSkin.ResetCatalog();
        foreach (var (w, h, touch) in new[] { (1920f, 1080f, false), (1280f, 720f, false), (1280f, 720f, true), (2400f, 1080f, true) })
        {
            var p = ShellLayout.ForViewport(w, h, atWar: false, touchMode: touch);
            var padX = p.Compact || touch ? 6f : 8f;
            var sep = touch || p.Compact ? 3f : 5f;
            var baseMed = touch ? 42f : (p.Compact ? 26f : 38f);
            var availW = MathF.Max(140f, p.MapBar.W - padX * 2f);
            var medW = MathF.Min(baseMed, MathF.Floor((availW - 6f * sep) / 7f));
            var medH = touch ? MathF.Max(44f, medW) : medW;
            var rowW = 7f * medW + 6f * sep;
            Check(rowW <= availW + 0.01f && medW >= 26f && (!touch || (medW >= 42f && medH >= 42f)),
                $"ShellMapBar 7-medallion row ({rowW:0} <= {availW:0}, {medW:0}x{medH:0}) fits cleanly at {w:0}x{h:0} touch={touch}");
        }

        var ctxSrc = ReadRel("Scripts/ui/Shell/ShellContext.cs");
        Check(ctxSrc.Contains("IsShowcasePan", StringComparison.Ordinal) &&
              ctxSrc.Contains("ToggleShowcasePan", StringComparison.Ordinal),
            "ShellContext exposes IsShowcasePan and ToggleShowcasePan");

        var newShellPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "NewShellUI.cs");
        var newShellLines = File.ReadAllLines(newShellPath);
        var newShellSrc = string.Join("\n", newShellLines);
        Check(newShellLines.Length < 800 &&
              newShellSrc.Contains("IsShowcasePan", StringComparison.Ordinal) &&
              newShellSrc.Contains("ToggleShowcasePan", StringComparison.Ordinal) &&
              newShellSrc.Contains("\"MapLayerDrawer\"", StringComparison.Ordinal) &&
              newShellSrc.Contains("anyLegacyLeft", StringComparison.Ordinal) &&
              newShellSrc.Contains("_lastModalSyncFrame", StringComparison.Ordinal) &&
              newShellSrc.Contains("CloseMapLayerDrawer()", StringComparison.Ordinal) &&
              !newShellSrc.Contains("new Color(", StringComparison.Ordinal),
            $"NewShellUI ({newShellLines.Length} L < 800) wires ShowcasePan, disables MapLayerDrawer, skips idle left-panel/modal work, and uses zero raw Color");

        var mapControlsSrc = ReadRel("Scripts/ui/MapControlsUI.cs");
        Check(mapControlsSrc.Contains("SyncShowcasePanButton", StringComparison.Ordinal) &&
              mapControlsSrc.Contains("_shell?.MapBar.Refresh()", StringComparison.Ordinal) &&
              mapControlsSrc.Contains("IsNewShellActive", StringComparison.Ordinal) &&
              !mapControlsSrc.Contains("new Color(", StringComparison.Ordinal),
            "MapControlsUI syncs ShowcasePan across F6/Classic/NewShell and skips hidden classic rebuilds in NewShell");

        var minimapPath = Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellMinimapView.cs");
        var minimapLines = File.ReadAllLines(minimapPath);
        var minimapSrc = string.Join("\n", minimapLines);
        Check(minimapLines.Length < 800 &&
              minimapSrc.Contains("PanRedrawIntervalMsec", StringComparison.Ordinal) &&
              minimapSrc.Contains("ActiveMoveDeg", StringComparison.Ordinal) &&
              minimapSrc.Contains("SettleEpsilonDeg", StringComparison.Ordinal) &&
              minimapSrc.Contains("_pendingExactRedraw", StringComparison.Ordinal) &&
              minimapSrc.Contains("QueueCameraViewRedraw", StringComparison.Ordinal) &&
              !minimapSrc.Contains("new Color(", StringComparison.Ordinal),
            $"ShellMinimapView ({minimapLines.Length} L < 800) throttles active pan redraws and flushes exact settle redraw with zero raw Color");
    }

    private static void RunAaaUiPolishContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // 1. EN + RU 99_aaa_ui_polish.json parity, translation, placeholder & zero-collision check
        var enPath = Path.Combine(dataRoot, "localization", "english", "99_aaa_ui_polish.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "99_aaa_ui_polish.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 99_aaa_ui_polish.json exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 14 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 99_aaa_ui_polish.json have identical key counts");

        var phRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (k, enVal) in enMap)
        {
            Check(k.StartsWith("aaa.ui.", StringComparison.Ordinal), $"key '{k}' uses aaa.ui.* namespace");
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN AAA polish key '{k}' non-empty");
            Check(ruMap.TryGetValue(k, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU AAA polish key '{k}' non-empty");
            if (Regex.IsMatch(enVal, "[A-Za-z]"))
                Check(!string.Equals(enVal, ruVal, StringComparison.Ordinal), $"RU AAA polish key '{k}' translated distinctly");
            var enPh = phRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = phRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{k}'");
        }

        foreach (var lang in new[] { "english", "russian" })
        {
            var seenElsewhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Path.Combine(dataRoot, "localization", lang), "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), "99_aaa_ui_polish.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                    seenElsewhere.Add(prop.Name);
            }
            var collisions = enMap.Keys.Where(seenElsewhere.Contains).ToArray();
            Check(collisions.Length == 0, $"{lang} 99_aaa_ui_polish.json has zero conflicts ({string.Join(", ", collisions)})");
        }

        // 2. Country Selection scrollable list, Dossier Prestige hierarchy & zero raw Color contracts
        string ReadRel(string rel) => File.ReadAllText(Path.Combine(repoRoot, rel));

        var pickerSrc = ReadRel("Scripts/ui/CountrySelectionUI.cs");
        var dossierSrc = ReadRel("Scripts/ui/CountryPickerDossier.cs");
        var dossierLines = File.ReadAllLines(Path.Combine(repoRoot, "Scripts", "ui", "CountryPickerDossier.cs")).Length;
        Check(dossierLines < 800 && !dossierSrc.Contains("new Color(", StringComparison.Ordinal) &&
              !pickerSrc.Contains("new Color(", StringComparison.Ordinal),
            $"CountrySelectionUI and CountryPickerDossier ({dossierLines} L < 800) have zero raw Color literals");

        Check(pickerSrc.Contains("Name = \"PickerResultsFrame\"", StringComparison.Ordinal) &&
              pickerSrc.Contains("Name = \"PickerResultsScroll\"", StringComparison.Ordinal) &&
              pickerSrc.Contains("HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled", StringComparison.Ordinal) &&
              pickerSrc.Contains("margin_right\", 14", StringComparison.Ordinal) &&
              pickerSrc.Contains("query.Length == 0 ? 16 : 24", StringComparison.Ordinal) &&
              pickerSrc.Contains("touchBtn ? 42 : 30", StringComparison.Ordinal) &&
              pickerSrc.Contains("HorizontalAlignment.Left", StringComparison.Ordinal) &&
              pickerSrc.Contains("ListFlag(button, nation, 24)", StringComparison.Ordinal) &&
              pickerSrc.Contains("MenuChromeTheme.SansTabular()", StringComparison.Ordinal) &&
              pickerSrc.Contains("PickerBeginButton", StringComparison.Ordinal) &&
              pickerSrc.Contains("CountryPickerActions", StringComparison.Ordinal),
            "CountrySelectionUI provides bounded 16/24-row scrollable PickerResultsScroll with >=14px clearance, left-aligned flags, tabular numerals, and touch-safe rows");

        Check(dossierSrc.Contains("PickerCountryName", StringComparison.Ordinal) &&
              dossierSrc.Contains("PickerCountryTier", StringComparison.Ordinal) &&
              dossierSrc.Contains("PickerCountryPrestige", StringComparison.Ordinal) &&
              dossierSrc.Contains("PickerFlagPocket", StringComparison.Ordinal) &&
              dossierSrc.Contains("MenuChromeTheme.TrackedCaps()", StringComparison.Ordinal),
            "CountryPickerDossier upgrades tier pill, Prestige header readout, and tracked stat chip hierarchy");

        // 3. NewShell HUD polish contracts (zero raw Color, line caps preserved)
        var cornerSrc = ReadRel("Scripts/ui/Shell/ShellCornerView.cs");
        var mapBarSrc = ReadRel("Scripts/ui/Shell/ShellMapBarView.cs");
        var navRailSrc = ReadRel("Scripts/ui/Shell/ShellNavRailView.cs");
        var telSrc = ReadRel("Scripts/ui/Shell/ShellCountryTelemetryView.cs");
        Check(!cornerSrc.Contains("new Color(", StringComparison.Ordinal) &&
              !mapBarSrc.Contains("new Color(", StringComparison.Ordinal) &&
              !navRailSrc.Contains("new Color(", StringComparison.Ordinal) &&
              !telSrc.Contains("new Color(", StringComparison.Ordinal) &&
              cornerSrc.Contains("aaa.ui.shell_rank_tip_prestige_fmt", StringComparison.Ordinal) &&
              mapBarSrc.Contains("_medActiveHoverBox", StringComparison.Ordinal) &&
              mapBarSrc.Contains("\"shell.state.positive\"", StringComparison.Ordinal) &&
              navRailSrc.Contains("ApplyIconRelief(item.Icon", StringComparison.Ordinal) &&
              telSrc.Contains("aaa.ui.prestige_delta_up_fmt", StringComparison.Ordinal) &&
              telSrc.Contains("aaa.ui.prestige_delta_down_fmt", StringComparison.Ordinal),
            "ShellCornerView, ShellMapBarView, ShellNavRailView, and ShellCountryTelemetryView implement AAA clarity & contrast upgrades with zero raw Color literals");
    }
}
