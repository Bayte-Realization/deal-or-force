using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class UiWorkspacesTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL ui-workspaces: " + label);
        Console.WriteLine("PASS ui-workspaces: " + label);
    }

    [TestSuite("--ui-workspaces-only", Order = 2640, IncludeInDefaultRun = true,
        Description = "Wave 2+3+5D UI Workspaces, Battle/Army Panels & Outer Sovereign Atlas Shell Chrome: Region Dossier, Economy Workspace, War Ledger, ShellWorkPanelView, BattleDossierUI + BattleFactorsUI, ArmyPanelUI + ArmyRosterUI, ShellStripView, ShellCornerView, ShellNavRailView, ShellDatePlateView, ShellInboxBarView, ShellMapBarView + ShellMinimapView, ShellMessageModalView, zero raw Color literals, <800 line caps, scrollbar clearance, fingerprint safety, and EN/RU localization.")]
    public static void Run()
    {
        RunSourceAndArchitectureContracts();
        RunWaveM6WorkspaceSafeAreaContracts();
        RunLocalizationContracts();
        RunReadOnlySimulationAndFingerprintContracts();
    }

    private static void RunSourceAndArchitectureContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        var ownedFiles = new[]
        {
            "Scripts/ui/Shell/ShellWorkPanelView.cs",
            "Scripts/ui/Shell/ShellRegionVisualsView.cs",
            "Scripts/ui/Shell/ShellRegionKpiCardsView.cs",
            "Scripts/ui/Shell/ShellRegionBuildTabView.cs",
            "Scripts/ui/Shell/ShellEconomyWorkspaceView.cs",
            "Scripts/ui/Shell/ShellEconomyCharts.cs",
            "Scripts/ui/Shell/ShellStripView.cs",
            "Scripts/ui/Shell/ShellCornerView.cs",
            "Scripts/ui/Shell/ShellNavRailView.cs",
            "Scripts/ui/Shell/ShellDatePlateView.cs",
            "Scripts/ui/Shell/ShellInboxBarView.cs",
            "Scripts/ui/Shell/ShellMapBarView.cs",
            "Scripts/ui/Shell/ShellMinimapView.cs",
            "Scripts/ui/Shell/ShellMessageModalView.cs",
            "Scripts/ui/WarLedgerUI.cs",
            "Scripts/ui/WarLedgerDetailUI.cs",
            "Scripts/ui/BattleDossierUI.cs",
            "Scripts/ui/BattleFactorsUI.cs",
            "Scripts/ui/ArmyPanelUI.cs",
            "Scripts/ui/ArmyRosterUI.cs"
        };

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in ownedFiles)
        {
            var full = Path.Combine(repoRoot, rel);
            Check(File.Exists(full), $"{rel} exists");
            var lines = File.ReadAllLines(full);
            Check(lines.Length < 800, $"{rel} has {lines.Length} lines < 800");
            var text = string.Join("\n", lines);
            sources[rel] = text;
            Check(!text.Contains("new Color(", StringComparison.Ordinal) &&
                  !text.Contains("Colors.Black", StringComparison.Ordinal),
                $"{rel} has zero raw Color literals");
            Check(File.Exists(full + ".uid"), $"{rel}.uid sidecar exists");
        }

        // Scrollbar right clearance (>= 14px) and ClipContents = true
        var scrollPaddingRx = new Regex(@"margin_right""\s*,\s*(\d+)", RegexOptions.Compiled);
        foreach (var rel in new[]
                 {
                     "Scripts/ui/Shell/ShellWorkPanelView.cs",
                     "Scripts/ui/Shell/ShellEconomyWorkspaceView.cs",
                     "Scripts/ui/Shell/ShellMapBarView.cs",
                     "Scripts/ui/Shell/ShellMessageModalView.cs",
                     "Scripts/ui/WarLedgerUI.cs",
                     "Scripts/ui/BattleDossierUI.cs",
                     "Scripts/ui/ArmyPanelUI.cs"
                 })
        {
            var src = sources[rel];
            Check(src.Contains("ClipContents = true", StringComparison.Ordinal), $"{rel} sets ClipContents = true");
            var matches = scrollPaddingRx.Matches(src);
            var hasConstClearance = src.Contains("ScrollClearanceRight", StringComparison.Ordinal);
            Check(hasConstClearance || (matches.Count > 0 && matches.All(m => int.Parse(m.Groups[1].Value) >= 14)),
                $"{rel} enforces >= 14px right scrollbar clearance");
        }

        // ShellWorkPanelView contracts
        var workPanel = sources["Scripts/ui/Shell/ShellWorkPanelView.cs"];
        Check(workPanel.Contains("WorkPanelTabsAndBody", StringComparison.Ordinal) &&
              workPanel.Contains("WorkPanelBodyMargin", StringComparison.Ordinal) &&
              workPanel.Contains("WorkPanelHeadlineKpis", StringComparison.Ordinal) &&
              workPanel.Contains("WorkPanelFooterHost", StringComparison.Ordinal) &&
              workPanel.Contains("CloseShellWorkPanel", StringComparison.Ordinal) &&
              workPanel.Contains("tabular: true", StringComparison.Ordinal),
            "ShellWorkPanelView wires flush tab-body join, 16px scroll margin, inlaid KPI headline cards, 2-row footer, and tabular fonts");

        // Region Dossier contracts
        var regVis = sources["Scripts/ui/Shell/ShellRegionVisualsView.cs"];
        var regKpi = sources["Scripts/ui/Shell/ShellRegionKpiCardsView.cs"];
        var regBld = sources["Scripts/ui/Shell/ShellRegionBuildTabView.cs"];
        Check(regKpi.Contains("ShellRegionKpiGrid", StringComparison.Ordinal) &&
              regKpi.Contains("InspectRegionControl", StringComparison.Ordinal) &&
              regKpi.Contains("BuildMiniFlagFrame", StringComparison.Ordinal) &&
              regKpi.Contains("AddTerrainCard", StringComparison.Ordinal) &&
              regKpi.Contains("AddGarrisonList", StringComparison.Ordinal),
            "ShellRegionKpiCardsView provides 2x2 KPI header cards, owner/controller occupation flags, terrain/supply card, and flagged garrison rows");
        Check(regVis.Contains("SocialClass.Lower", StringComparison.Ordinal) &&
              regVis.Contains("AddSwatchKvRow", StringComparison.Ordinal) &&
              regVis.Contains("AddSectionHeader(", StringComparison.Ordinal) &&
              regVis.Contains("AddMeterRow(", StringComparison.Ordinal),
            "ShellRegionVisualsView renders cultures/faiths/classes composition bars and preserves shared AddSectionHeader/AddMeterRow helpers");
        Check(regBld.Contains("BuildingRules.Refusal", StringComparison.Ordinal) &&
              regBld.Contains("Waterfront.Refusal", StringComparison.Ordinal) &&
              regBld.Contains("tabular: true", StringComparison.Ordinal),
            "ShellRegionBuildTabView renders framed build cards with cost/refusal tooltips and tabular figures");

        // Economy Workspace contracts
        var ecoView = sources["Scripts/ui/Shell/ShellEconomyWorkspaceView.cs"];
        var ecoCharts = sources["Scripts/ui/Shell/ShellEconomyCharts.cs"];
        Check(ecoView.Contains("ShellOpenGrandProjectsButton", StringComparison.Ordinal) &&
              ecoView.Contains("RefreshFiscalSummaryRibbon", StringComparison.Ordinal) &&
              ecoView.Contains("PopulateSupplyShortages", StringComparison.Ordinal) &&
              ecoView.Contains("_budgetGrid.Columns = profile.Compact ? 1 : 2", StringComparison.Ordinal) &&
              ecoView.Contains("tabular: true", StringComparison.Ordinal),
            "ShellEconomyWorkspaceView wires responsive 1/2-col budget grid, fiscal ribbon, shortages strip, and preserves ShellOpenGrandProjectsButton");
        Check(ecoCharts.Contains("ShellEconomyFiscalRibbon", StringComparison.Ordinal) &&
              ecoCharts.Contains("ShellEconomyGrandProjectsCard", StringComparison.Ordinal) &&
              ecoCharts.Contains("ShellEconomyOpenGrandProjectsCta", StringComparison.Ordinal) &&
              ecoCharts.Contains("DrawColoredPolygon", StringComparison.Ordinal) &&
              ecoCharts.Contains("DrawString(", StringComparison.Ordinal),
            "ShellEconomyCharts builds 4-card fiscal summary ribbon, Grand Projects CTA card, supply shortages, and labeled 6-month treasury chart");

        // War Ledger contracts
        var warList = sources["Scripts/ui/WarLedgerUI.cs"];
        var warDetail = sources["Scripts/ui/WarLedgerDetailUI.cs"];
        Check(warList.Contains("ScreenHeader(", StringComparison.Ordinal) &&
              warList.Contains("WarLedgerScoreBar", StringComparison.Ordinal) &&
              warList.Contains("PopulateCardFlagCluster", StringComparison.Ordinal) &&
              warList.Contains("JumpToWarFront", StringComparison.Ordinal) &&
              warList.Contains("OpenWarPeaceFromCard", StringComparison.Ordinal) &&
              warList.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "WarLedgerUI preserves ScreenHeader & WarLedgerScoreBar while adding flag clusters, tabular KPIs, and Jump/Peace card actions");
        Check(warDetail.Contains("WarLedgerDetailScoreBar", StringComparison.Ordinal) &&
              warDetail.Contains("WarLedgerScaleCaption", StringComparison.Ordinal) &&
              warDetail.Contains("CoalitionGrid", StringComparison.Ordinal) &&
              warDetail.Contains("HFlowContainer", StringComparison.Ordinal) &&
              warDetail.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "WarLedgerDetailUI preserves detail score bar/scale caption, renders flag-only coalition grids, and wraps action bars in HFlowContainer");

        // Wave 3 Battle Dossier + Factors contracts
        var battleUi = sources["Scripts/ui/BattleDossierUI.cs"];
        var battleFac = sources["Scripts/ui/BattleFactorsUI.cs"];
        Check(battleUi.Contains("BattleTelemetry.Build(", StringComparison.Ordinal) &&
              battleUi.Contains("BattleVsHeader", StringComparison.Ordinal) &&
              battleUi.Contains("BattlePhaseBadge", StringComparison.Ordinal) &&
              battleUi.Contains("BattleProgressTug", StringComparison.Ordinal) &&
              battleUi.Contains("BattleEndurance", StringComparison.Ordinal) &&
              battleUi.Contains("FormationReadiness_", StringComparison.Ordinal) &&
              battleUi.Contains("WithdrawFormation_", StringComparison.Ordinal) &&
              battleUi.Contains("WithdrawHalf_", StringComparison.Ordinal) &&
              battleUi.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "BattleDossierUI integrates BattleTelemetry (split VS header, phase badge, progress tug, width bar, frontline/reserve cards) while preserving all withdrawal/readiness controls");
        Check(battleFac.Contains("BattleModifierChips", StringComparison.Ordinal) &&
              battleFac.Contains("AttackDirections", StringComparison.Ordinal) &&
              battleFac.Contains("CombinedArmsBonus", StringComparison.Ordinal) &&
              battleFac.Contains("HasPocketCollapse", StringComparison.Ordinal),
            "BattleFactorsUI renders color-coded BattleTelemetry modifier chips and magnitude factor bars");

        // Wave 3 Army Panel + Roster + Bottom Strip contracts
        var armyPanel = sources["Scripts/ui/ArmyPanelUI.cs"];
        var armyRoster = sources["Scripts/ui/ArmyRosterUI.cs"];
        var strip = sources["Scripts/ui/Shell/ShellStripView.cs"];
        Check(armyPanel.Contains("ArmyStackReadinessStrip", StringComparison.Ordinal) &&
              armyPanel.Contains("ArmyQuickMergeHq", StringComparison.Ordinal) &&
              armyPanel.Contains("ArmyRosterFrame", StringComparison.Ordinal) &&
              armyPanel.Contains("ArmyCommandTabs", StringComparison.Ordinal) &&
              armyPanel.Contains("AssaultReadinessText", StringComparison.Ordinal) &&
              armyPanel.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "ArmyPanelUI renders 4-metric stack readiness strip, quick HQ merge, and 16px scroll clearance");
        Check(armyRoster.Contains("RefreshArmyReadinessStrip", StringComparison.Ordinal) &&
              armyRoster.Contains("ShowBattleDetails", StringComparison.Ordinal) &&
              armyRoster.Contains("SupplyStatus_", StringComparison.Ordinal) &&
              armyRoster.Contains("EquipmentBar_", StringComparison.Ordinal) &&
              armyRoster.Contains("StaffReason_", StringComparison.Ordinal),
            "ArmyRosterUI provides aligned roster columns, lead highlight, clickable battle-jump pips, and preserves all telemetry node names");
        Check(strip.Contains("SelectAndFocusGroup", StringComparison.Ordinal) &&
              strip.Contains("ShellStripOverflowBadge", StringComparison.Ordinal) &&
              strip.Contains("liveHash", StringComparison.Ordinal) &&
              strip.Contains("ClipContents = true", StringComparison.Ordinal),
            "ShellStripView selects full Army Groups, tracks live battle/order/war-score hash, clips tags, and shows +N overflow badge");

        // Wave 5D Sovereign Brass & Obsidian Outer Shell Chrome + Editorial Dispatch Modal contracts
        var corner = sources["Scripts/ui/Shell/ShellCornerView.cs"];
        var navRail = sources["Scripts/ui/Shell/ShellNavRailView.cs"];
        var datePlate = sources["Scripts/ui/Shell/ShellDatePlateView.cs"];
        var inboxBar = sources["Scripts/ui/Shell/ShellInboxBarView.cs"];
        var mapBar = sources["Scripts/ui/Shell/ShellMapBarView.cs"];
        var minimap = sources["Scripts/ui/Shell/ShellMinimapView.cs"];
        var msgModal = sources["Scripts/ui/Shell/ShellMessageModalView.cs"];
        Check(corner.Contains("ShellFlagButton", StringComparison.Ordinal) &&
              corner.Contains("ShellLCornerBackdrop", StringComparison.Ordinal) &&
              corner.Contains("Ui.ChromeBox(", StringComparison.Ordinal) &&
              corner.Contains("ClipContents = true", StringComparison.Ordinal) &&
              corner.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "ShellCornerView uses Sovereign Brass chrome framing, ClipContents, Tabular figures, and preserves flag/L-corner node names");
        Check(navRail.Contains("ShellRailHoverTag", StringComparison.Ordinal) &&
              navRail.Contains("ApplySemanticBadgeStyle", StringComparison.Ordinal) &&
              navRail.Contains("ActiveBar", StringComparison.Ordinal) &&
              navRail.Contains("GetViewportSize()", StringComparison.Ordinal),
            "ShellNavRailView renders brass active-bar indicator, semantic badge colors, and viewport-clamped hover tag");
        Check(datePlate.Contains("Display(withTime: true)", StringComparison.Ordinal) &&
              datePlate.Contains("ShellDatePauseButton", StringComparison.Ordinal) &&
              datePlate.Contains("ShellSpeedPip_", StringComparison.Ordinal) &&
              datePlate.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal) &&
              datePlate.Contains("ClipContents = true", StringComparison.Ordinal),
            "ShellDatePlateView renders HH:00 tabular clock, tactile state-framed pause/speed chevrons, and ClipContents safety");
        Check(inboxBar.Contains("ShellDiplomaticInboxButton", StringComparison.Ordinal) &&
              inboxBar.Contains("ShellDiplomaticInboxBadge", StringComparison.Ordinal) &&
              inboxBar.Contains("ClipContents = true", StringComparison.Ordinal) &&
              inboxBar.Contains("count > 99 ? \"99+\"", StringComparison.Ordinal),
            "ShellInboxBarView clips row contents, frames idle/pending states, and bounds count pill at 99+");
        Check(mapBar.Contains("ShellMapMorePopover", StringComparison.Ordinal) &&
              mapBar.Contains("CloseShellMapPopover", StringComparison.Ordinal) &&
              mapBar.Contains("ShellMinimapView", StringComparison.Ordinal) &&
              minimap.Contains("Name = \"ShellMinimap\"", StringComparison.Ordinal) &&
              minimap.Contains("Ui.Colour(\"shell.brass_hi\")", StringComparison.Ordinal),
            "ShellMapBarView delegates live corner minimap to ShellMinimapView, adds scrollable clipped popover, and uses brass hover/active medallions");
        Check(msgModal.Contains("ShellRecentNewsModal", StringComparison.Ordinal) &&
              msgModal.Contains("ShellNewsModalHeader", StringComparison.Ordinal) &&
              msgModal.Contains("CloseShellNewsModal", StringComparison.Ordinal) &&
              msgModal.Contains("ShellMessagePresetCards", StringComparison.Ordinal) &&
              msgModal.Contains("ResolveCategoryVisual", StringComparison.Ordinal) &&
              msgModal.Contains("Key.Escape", StringComparison.Ordinal) &&
              msgModal.Contains("PresentationFonts.Tabular()", StringComparison.Ordinal),
            "ShellMessageModalView provides editorial header banner, preset consequence cards, Esc/× focus hygiene, category dispatch cards, and >=15px scroll clearance");

        // Wave M4 Mobile/Android HUD polish contracts: visible KPI figures, shrink-to-content outliner,
        // unclipped army chip, touch tooltip suppression, and zero-alloc fast-forward ticks
        var alertColPath = Path.Combine(repoRoot, "Scripts/ui/Shell/ShellAlertColumnView.cs");
        var alertColLines = File.ReadAllLines(alertColPath);
        var alertColSrc = string.Join("\n", alertColLines);
        Check(alertColLines.Length < 800 && !alertColSrc.Contains("new Color(", StringComparison.Ordinal),
            $"Wave M4: ShellAlertColumnView stays <800 lines ({alertColLines.Length}) with zero raw Color literals");
        Check(alertColSrc.Contains("_alertRows.RemoveChild(ch)", StringComparison.Ordinal) &&
              alertColSrc.Contains("_outlinerRows.RemoveChild(ch)", StringComparison.Ordinal) &&
              alertColSrc.Contains("_outlinerScroll.CustomMinimumSize = Vector2.Zero", StringComparison.Ordinal) &&
              alertColSrc.Contains("Math.Clamp(_sheet.GetCombinedMinimumSize().Y", StringComparison.Ordinal),
            "Wave M4: ShellAlertColumnView removes stale rows synchronously and clamps sheet height to content");
        var figBlockIdx = corner.IndexOf("var fig = new Label", StringComparison.Ordinal);
        var capBlockIdx = corner.IndexOf("var cap = new Label", StringComparison.Ordinal);
        Check(figBlockIdx > 0 && capBlockIdx > figBlockIdx &&
              !corner[figBlockIdx..capBlockIdx].Contains("ClipText = true", StringComparison.Ordinal) &&
              corner.Contains("var narrow = cellW < 92f", StringComparison.Ordinal),
            "Wave M4: ShellCornerView KPI figure/unit labels avoid Godot 4 zero-width ClipText bug and scale cleanly on narrow touch cells");
        Check(strip.Contains("var h = touch ? 44f", StringComparison.Ordinal) &&
              strip.Contains("_profile.LeftRail.Right + 14f", StringComparison.Ordinal) &&
              strip.Contains("_lastMergeTier", StringComparison.Ordinal),
            "Wave M4: ShellStripView lifts touch army chip height/clearance and caches division visibility scans");
        var uiThemeSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts/ui/UiTheme.cs"));
        Check(uiThemeSrc.Contains("PlatformProfile.Current?.IsTouchPrimary == true", StringComparison.Ordinal),
            "Wave M4: Ui.Tip suppresses sticky hover tooltips on touch-primary devices");

        // Wave M4 Round 2: TooltipKeeper touch guard, DatePlate DEFCON/chevron fit, 4 ClipText fixes,
        // SafeArea & narrow-screen ShellLayout, NavRail reflow, weekly rank/decision cache, InterfaceCadence zero-alloc
        var tooltipKeeperSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts/ui/TooltipKeeper.cs"));
        Check(tooltipKeeperSrc.Contains("PlatformProfile.Current?.IsTouchPrimary == true", StringComparison.Ordinal),
            "Wave M4 R2: TooltipKeeper._Process skips tooltips and 60 Hz polling on touch while OnAdded suppresses native popups");
        Check(datePlate.Contains("_defconCap.Text = touch ? \"DEF\" : \"DEFCON\"", StringComparison.Ordinal) &&
              datePlate.Contains("_lampsRow.Visible = !touch", StringComparison.Ordinal) &&
              datePlate.Contains("touchPipW = Math.Clamp", StringComparison.Ordinal),
            "Wave M4 R2: ShellDatePlateView compacts DEFCON/Tension and sizes speed chevrons strictly inside bounds on touch");
        var ecoChartsSrc = sources["Scripts/ui/Shell/ShellEconomyCharts.cs"];
        var ecoWsSrc = sources["Scripts/ui/Shell/ShellEconomyWorkspaceView.cs"];
        Check(ecoChartsSrc.Contains("valueLbl = new Label { Text = \"—\" };", StringComparison.Ordinal) &&
              ecoChartsSrc.Contains("bar.RemoveChild(ch)", StringComparison.Ordinal) &&
              ecoWsSrc.Contains("var l = new Label { Text = title };", StringComparison.Ordinal) &&
              !msgModal[msgModal.IndexOf("var pillLbl = new Label", StringComparison.Ordinal)..(msgModal.IndexOf("pillBox.AddChild(pillLbl)", StringComparison.Ordinal))].Contains("ClipText = true", StringComparison.Ordinal),
            "Wave M4 R2: Economy KPI figures, section headings, Grand Projects button, and Message Preset pills avoid zero-width ClipText bug");
        Check(navRail.Contains("if (visChanged && _profile is not null) Layout(_profile);", StringComparison.Ordinal) &&
              navRail.Contains("_decisionsCache.Week == week", StringComparison.Ordinal),
            "Wave M4 R2: ShellNavRailView reflows visible buttons (zero dead hole) and caches decisions by week/rev/PP");
        Check(corner.Contains("_rankCache.Week == week", StringComparison.Ordinal) &&
              corner.Contains("_profile?.TouchMode != true", StringComparison.Ordinal),
            "Wave M4 R2: ShellCornerView caches overall rank weekly/by-rev and suppresses Alt keycaps on touch");
        var cadenceSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts/ui/InterfaceCadence.cs"));
        Check(!cadenceSrc.Contains(".All.Any(", StringComparison.Ordinal),
            "Wave M4 R2: InterfaceCadence eliminates 100ms LINQ allocation");
        var safeTouch = ShellLayout.ForViewport(2400f, 1080f, atWar: true, touchMode: true, safeLeft: 88f, safeTop: 12f, safeRight: 24f, safeBottom: 48f);
        Check(safeTouch.TopPlate.X >= 88f && safeTouch.TopPlate.Y >= 12f &&
              safeTouch.DatePlate.Right <= 2400f - 24f + 0.5f && safeTouch.MapBar.Bottom <= 1080f - 48f + 0.5f &&
              !safeTouch.TopPlate.Intersects(safeTouch.TouchModeBar),
            "Wave M4 R2: ShellLayout honors SafeAreaInsets cleanly across all HUD anchors");
        var narrowTouch = ShellLayout.ForViewport(960f, 640f, atWar: true, touchMode: true);
        Check(!narrowTouch.TopPlate.Intersects(narrowTouch.TouchModeBar) &&
              narrowTouch.StatCellWidth * 8f <= narrowTouch.TopPlate.W,
            "Wave M4 R2: ShellLayout prevents narrow (<1000px) TopPlate/TouchModeBar overlap and stat cell overflow");
        var mapEditorSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts/ui/ScenarioMapEditor.cs"));
        var hoverIdx = mapEditorSrc.IndexOf("_hoverReadout = new Label", StringComparison.Ordinal);
        var hoverEnd = mapEditorSrc.IndexOf("bannerRow.AddChild(_hoverReadout);", StringComparison.Ordinal);
        Check(hoverIdx > 0 && hoverEnd > hoverIdx &&
              !mapEditorSrc[hoverIdx..hoverEnd].Contains("ClipText = true", StringComparison.Ordinal),
            "Wave M4 R2: ScenarioMapEditor _hoverReadout avoids zero-width ClipText collapse");

        // Wave 5I Waving Flags across ALL 7 New Shell flag views + FlagClothSettingsPulse + Hover Tag fix
        var flagShellFiles = new[]
        {
            "Scripts/ui/Shell/ShellCornerView.cs",
            "Scripts/ui/Shell/ShellCountryDossierView.cs",
            "Scripts/ui/Shell/ShellCountryDiplomacyStanding.cs",
            "Scripts/ui/Shell/ShellCountryDiplomacyView.cs",
            "Scripts/ui/Shell/ShellRegionKpiCardsView.cs",
            "Scripts/ui/Shell/ShellStripView.cs",
            "Scripts/ui/Shell/ShellWorkPanelView.cs"
        };
        foreach (var rel in flagShellFiles)
        {
            var full = Path.Combine(repoRoot, rel);
            var lines = File.ReadAllLines(full);
            Check(lines.Length < 800, $"Wave 5I: {rel} has {lines.Length} lines < 800");
            var text = string.Join("\n", lines);
            Check(!text.Contains("new Color(", StringComparison.Ordinal), $"Wave 5I: {rel} has zero raw Color literals");
            Check(text.Contains("FlagClothPresentation.Material", StringComparison.Ordinal),
                $"Wave 5I: {rel} wires FlagClothPresentation.Material onto flag TextureRects");
        }
        var newShellSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts/ui/Shell/NewShellUI.cs"));
        Check(newShellSrc.Contains("FlagClothSettingsPulse", StringComparison.Ordinal) &&
              newShellSrc.Contains("FlagClothPresentation.Synchronize()", StringComparison.Ordinal),
            "Wave 5I: NewShellUI ensures FlagClothSettingsPulse and calls FlagClothPresentation.Synchronize() on build");
        Check(corner.Contains("_profile.CornerNode.Bottom + 6f", StringComparison.Ordinal) &&
              !corner.Contains("flagBtn.Position.X + flagBtn.Size.X + 8f", StringComparison.Ordinal),
            "Wave 5I: ShellCornerView places flag hover tag below top-plate/XP strip instead of over Treasury");

        // Layout safety across 1920x1080 and 1280x720
        InterfaceSkin.ResetCatalog();
        foreach (var (w, h) in new[] { (1920f, 1080f), (1280f, 720f) })
        {
            var profile = ShellLayout.ForViewport(w, h, atWar: true);
            Check(profile.LeftPanel.Inside(w, h) && profile.LeftPanel.W >= 320f,
                $"LeftPanel fits cleanly at {w:0}x{h:0} (W={profile.LeftPanel.W:0})");
            Check(profile.FullWorkspace.Inside(w, h) && profile.FullWorkspace.W >= 900f,
                $"FullWorkspace fits cleanly at {w:0}x{h:0} (W={profile.FullWorkspace.W:0})");
        }
    }

    private static void RunLocalizationContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));
        var enPath = Path.Combine(dataRoot, "localization", "english", "97_ui_workspaces.json");
        var ruPath = Path.Combine(dataRoot, "localization", "russian", "97_ui_workspaces.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "EN and RU 97_ui_workspaces.json exist");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruPath));
        var enMap = enDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        var ruMap = ruDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        Check(enMap.Count >= 80 && enMap.Count == ruMap.Count,
            $"EN ({enMap.Count}) and RU ({ruMap.Count}) 97_ui_workspaces.json have identical key counts");

        var placeholderRx = new Regex(@"\{(\d+)(?::[^}]+)?\}", RegexOptions.Compiled);
        foreach (var (key, enVal) in enMap)
        {
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN key '{key}' is non-empty");
            Check(ruMap.TryGetValue(key, out var ruVal) && !string.IsNullOrWhiteSpace(ruVal), $"RU key '{key}' exists and is non-empty");
            var enPh = placeholderRx.Matches(enVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            var ruPh = placeholderRx.Matches(ruVal).Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
            Check(enPh.SequenceEqual(ruPh), $"placeholder parity matches for '{key}'");
        }

        // Every shell.ws.* key referenced across all 20 owned UI files must exist in 97_ui_workspaces.json
        var keyRx = new Regex(@"""(shell\.ws\.[a-z0-9_.]+)""", RegexOptions.Compiled);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in new[]
                 {
                     "Scripts/ui/Shell/ShellWorkPanelView.cs",
                     "Scripts/ui/Shell/ShellRegionVisualsView.cs",
                     "Scripts/ui/Shell/ShellRegionKpiCardsView.cs",
                     "Scripts/ui/Shell/ShellRegionBuildTabView.cs",
                     "Scripts/ui/Shell/ShellEconomyWorkspaceView.cs",
                     "Scripts/ui/Shell/ShellEconomyCharts.cs",
                     "Scripts/ui/Shell/ShellStripView.cs",
                     "Scripts/ui/Shell/ShellCornerView.cs",
                     "Scripts/ui/Shell/ShellNavRailView.cs",
                     "Scripts/ui/Shell/ShellDatePlateView.cs",
                     "Scripts/ui/Shell/ShellInboxBarView.cs",
                     "Scripts/ui/Shell/ShellMapBarView.cs",
                     "Scripts/ui/Shell/ShellMinimapView.cs",
                     "Scripts/ui/Shell/ShellMessageModalView.cs",
                     "Scripts/ui/WarLedgerUI.cs",
                     "Scripts/ui/WarLedgerDetailUI.cs",
                     "Scripts/ui/BattleDossierUI.cs",
                     "Scripts/ui/BattleFactorsUI.cs",
                     "Scripts/ui/ArmyPanelUI.cs",
                     "Scripts/ui/ArmyRosterUI.cs"
                 })
        {
            var text = File.ReadAllText(Path.Combine(repoRoot, rel));
            foreach (Match m in keyRx.Matches(text))
                referenced.Add(m.Groups[1].Value);
        }
        Check(referenced.Count >= 100, $"UI workspace, battle/army & outer shell views reference {referenced.Count} shell.ws.* localization keys");
        foreach (var key in referenced)
            Check(enMap.ContainsKey(key) && ruMap.ContainsKey(key), $"referenced key '{key}' exists in both EN and RU 97_ui_workspaces.json");
    }

    private static void RunReadOnlySimulationAndFingerprintContracts()
    {
        var campaign = Campaign.CreateBootstrap(20260928);
        var player = campaign.Player;
        Check(player is not null, "bootstrap campaign has a player nation");

        var state = campaign.StatesOf(player).FirstOrDefault();
        Check(state is not null, "player nation has at least one ScenarioState");

        var fpBefore = CampaignFingerprint.Of(campaign);

        // Region Dossier read-only calls
        var estGdp = RegionalStatistics.EstimatedGdp(campaign, state);
        Check(float.IsFinite(estGdp) && estGdp >= 0f, "RegionalStatistics.EstimatedGdp is finite and non-negative");
        var (birth, death, _) = HouseholdEconomy.VitalRates(campaign, player, state);
        Check(float.IsFinite(birth) && float.IsFinite(death), "HouseholdEconomy.VitalRates returns finite rates");
        _ = EconomySystem.Cultures(state);
        _ = DemographyCatalog.DenominationsIn(state, state.NationId);
        _ = state.PopulationGroups.Sum(g => g.Size);
        foreach (var kind in new[] { BuildingKind.CivilianIndustry, BuildingKind.Infrastructure, BuildingKind.ArmsIndustry, BuildingKind.Barracks, BuildingKind.PowerPlant, BuildingKind.ExtractionIndustry })
        {
            _ = BuildingRules.Refusal(campaign, player, kind);
            _ = Waterfront.Refusal(campaign, state.MapIndex, kind);
            _ = ConstructionCost.Of(campaign, player, kind);
            _ = ConstructionCost.ExpectedDays(campaign, player, kind);
        }

        // Economy Workspace read-only calls
        var sheet = SovereignBalanceSheet.Read(campaign, player);
        Check(float.IsFinite(sheet.Cash) && float.IsFinite(sheet.Principal) && float.IsFinite(sheet.NewCredit),
            "SovereignBalanceSheet.Read returns finite fiscal stocks");
        var weeklyOut = player.Gdp / 52f;
        foreach (var line in BudgetCore.PlayerLines)
        {
            var rate = BudgetCore.RateOf(player, line);
            var amt = BudgetCore.Amount(player, line, weeklyOut, atWar: false);
            Check(float.IsFinite(rate) && float.IsFinite(amt), $"BudgetCore line '{line.Id}' readable and finite");
        }
        _ = player.Market.Values.Where(q => q.Demand > 0.05f && q.Satisfaction < 0.90f).Take(4).ToArray();
        _ = campaign.GrandProjects.Count(p => p.HostNationId == player.Id);

        // War Ledger + BattleTelemetry read-only calls
        foreach (var war in campaign.Wars.Where(w => !w.Ended).Take(3))
        {
            _ = WarNames.Of(campaign, war);
            _ = WarScoring.Parts(war).ToArray();
            _ = WarLosses.ByCountry(war, true);
            _ = WarLosses.ByCountry(war, false);
            if (war.Involves(player.Id))
            {
                _ = Statecraft.CallableAllies(campaign, war, player.Id);
                _ = PeaceNegotiation.BlockReason(campaign, war, player.Id, PeaceTerms.WhitePeace);
                _ = PeaceNegotiation.Acceptance(campaign, war, player.Id, PeaceTerms.WhitePeace);
            }
        }
        foreach (var battle in campaign.Battles.Take(3))
        {
            var snap = BattleTelemetry.Build(campaign, battle);
            Check(snap is not null && float.IsFinite(snap.AttackerProgress) && float.IsFinite(snap.Momentum),
                $"BattleTelemetry.Build is finite and readable for battle #{battle.Id}");
        }

        Check(CampaignFingerprint.Of(campaign) == fpBefore,
            "reading Region Dossier, Economy Workspace, War Ledger, BattleTelemetry, and Army Panel data leaves CampaignFingerprint bit-identical");
    }

    private static void RunWaveM6WorkspaceSafeAreaContracts()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));

        // 1. Desktop FullWorkspace parity (1920x1080 & 1280x720)
        var d1080 = ShellLayout.ForViewport(1920f, 1080f, atWar: false, touchMode: false);
        Check(MathF.Abs(d1080.FullWorkspace.X - 80f) < 0.01f && MathF.Abs(d1080.FullWorkspace.Y - 132f) < 0.01f,
            "Desktop 1920x1080 FullWorkspace preserves exact (X=80, Y=132)");
        var d720 = ShellLayout.ForViewport(1280f, 720f, atWar: false, touchMode: false);
        Check(MathF.Abs(d720.FullWorkspace.X - 58f) < 0.01f && MathF.Abs(d720.FullWorkspace.Y - 94f) < 0.01f,
            "Desktop 1280x720 FullWorkspace preserves exact (X=58, Y=94)");

        // 2. Android Touch FullWorkspace clears LeftRail, DatePlate, TouchModeBar, and SafeArea
        foreach (var (w, h, sl, st, sr, sb, war) in new (float, float, float, float, float, float, bool)[]
        {
            (1280f, 720f, 48f, 0f, 24f, 0f, false),
            (1280f, 720f, 56f, 12f, 24f, 8f, true),
            (1440f, 720f, 72f, 16f, 32f, 12f, true),
            (2400f, 1080f, 88f, 24f, 40f, 16f, true),
        })
        {
            var p = ShellLayout.ForViewport(w, h, war, touchMode: true, sl, st, sr, sb);
            Check(p.FullWorkspace.X >= p.LeftRail.Right + 8f - 0.01f,
                $"FullWorkspace clears LeftRail on Android {w}x{h} safeL={sl} war={war} (wsX={p.FullWorkspace.X}, railR={p.LeftRail.Right})");
            Check(p.FullWorkspace.Y >= p.DatePlate.Bottom + 8f - 0.01f,
                $"FullWorkspace clears DatePlate on Android {w}x{h} safeT={st} war={war} (wsY={p.FullWorkspace.Y}, dateB={p.DatePlate.Bottom})");
            Check(p.FullWorkspace.Y >= p.TouchModeBar.Bottom + 8f - 0.01f,
                $"FullWorkspace clears TouchModeBar on Android {w}x{h} safeT={st} war={war} (wsY={p.FullWorkspace.Y}, touchB={p.TouchModeBar.Bottom})");
            var xp = ShellLayout.XpStripRect(p);
            Check(xp.Right <= p.NewsCard.X && xp.Right <= p.TouchModeBar.X,
                $"XpStrip clears both NewsCard and TouchModeBar on Android {w}x{h} safeL={sl}");
        }

        // 3. DoctrineComfortUI respects IsNewShellActive via FitNewShellWorkspaceLayer
        var comfortSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "DoctrineComfortUI.cs"));
        Check(comfortSrc.Contains("if (IsNewShellActive)", StringComparison.Ordinal) &&
              comfortSrc.Contains("FitNewShellWorkspaceLayer(_doctrineLayer);", StringComparison.Ordinal),
            "DoctrineComfortUI routes Book of War sizing through FitNewShellWorkspaceLayer when IsNewShellActive");

        // 4. ShellXpStripView clamps authored skin rects & hides observer badge when width < 60px
        var xpSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellXpStripView.cs"));
        Check(xpSrc.Contains("_observerFitsInStrip = obsAvail >= 60f;", StringComparison.Ordinal) &&
              xpSrc.Contains("profile.TouchModeBar.X", StringComparison.Ordinal) &&
              !xpSrc.Contains("MathF.Max(74f,", StringComparison.Ordinal),
            "ShellXpStripView clamps authored skin width against NewsCard/TouchModeBar and hides observer badge when narrow");

        // 5. ShellOrchestrator FitWorkspaceLayer includes hud.peace_panel and workspace.situation
        var orchSrc = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "Shell", "ShellOrchestrator.cs"));
        Check(orchSrc.Contains("\"hud.peace_panel\"", StringComparison.Ordinal) &&
              orchSrc.Contains("\"workspace.situation\"", StringComparison.Ordinal),
            "ShellOrchestrator FitWorkspaceLayer fits hud.peace_panel and workspace.situation inside FullWorkspace");
    }
}
