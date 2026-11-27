// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Platform;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

/// <summary>
/// Focused regression tests covering:
/// 1. Battle-plan drawing (PC LMB/RMB + Android touch/pinch handoff + RunWrite + WakeGlobeRendering),
/// 2. Android Army Panel &amp; Planning Bar anti-overlap + z-order across phone/tablet/PC viewports,
/// 3. Zero PC-only control hints on touch (EN + RU) while preserving desktop prompts verbatim,
/// 4. Top-left flag/rank "Malevich square" fix, blank Capital/KV layout fix, and read-only
///    Region Card Movements + Country/Region Telemetry Audit (CampaignFingerprint bit-identical),
/// 5. Doctrine Abandonment &amp; Study Cancellation (tactical primary/companion promotion, pending cancel,
///    mid-book study cancel without XP refund, strategic abandon + anti-exploit full-cost re-adopt,
///    feature gates, NetworkCommandCodec round-trip, Save/Load fingerprint parity, and EN/RU UI/loc).
/// </summary>
public static class UxFixesAndDoctrineCancelTests
{
    private static int _passed;

    private static void Check(bool condition, string label)
    {
        if (!condition)
            throw new Exception("FAIL ux-fixes: " + label);
        _passed++;
        Console.WriteLine("PASS ux-fixes: " + label);
    }

    private static readonly Regex ForbiddenTouchEnRx = new(
        @"\b(?:RMB|LMB|MMB|Right-click|Left-click|Right click|Left click|middle button|Shift\+|Ctrl\+|Alt\+|Shift/|Ctrl/|Alt/|Esc|Escape|WASD|mouse|scroll wheel|mouse wheel|with the wheel)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ForbiddenTouchRuRx = new(
        @"(?<![А-Яа-яЁёA-Za-z0-9_])(?:ПКМ|ЛКМ|СКМ|правой кнопкой|левой кнопкой|средней кнопкой|мыш(?:ь|и|ью)|мышк(?:а|е|и|у|ой)|колёсик\w*|колесик\w*|клавиш\w*|Пробел)(?![А-Яа-яЁёA-Za-z0-9_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [TestSuite("--ux-fixes-only", Order = 2910, IncludeInDefaultRun = true,
        Description = "Verifies battle-plan PC/Android input, Android Army/Planning dock anti-overlap, zero PC hints on touch, ShellCorner/Capital/Region Movements & CardTelemetryAudit read-only invariance, and Doctrine Abandonment/Study Cancellation.")]
    public static void Run()
    {
        _passed = 0;
        var root = FindRepoRoot();

        VerifyBattlePlanInputAndCommands(root);
        VerifyAndroidArmyAndPlanningDockAntiOverlap(root);
        VerifyZeroPcHintsOnTouch(root);
        VerifyCornerCapitalAndCardTelemetryAudit(root);
        VerifyDoctrineAbandonmentAndStudyCancellation(root);
        VerifyNoRawColorsOrGodotInSim(root);

        Console.WriteLine($"ux-fixes: {_passed} PASS");
    }

    // --------------------------------------------------------- 1. Battle-plan drawing (PC + Android)

    private static void VerifyBattlePlanInputAndCommands(string root)
    {
        var inputSrc = ReadText(root, "Scripts/gfx/GlobePresentation.Input.cs");
        var planInputSrc = ReadText(root, "Scripts/gfx/GlobeBattlePlanInput.cs");
        var borderSrc = ReadText(root, "Scripts/gfx/GlobeBorderInput.cs");
        var eraseSrc = ReadText(root, "Scripts/gfx/GlobePlanErasing.cs");
        var overlaySrc = ReadText(root, "Scripts/gfx/GlobePresentation.BordersAndOverlays.cs");
        var brushSrc = ReadText(root, "Scripts/ui/BattlePlanBrushUI.cs");
        var helperSrc = ReadText(root, "Scripts/ui/BattlePlanBrushHelpers.cs");

        var touchIdx = inputSrc.IndexOf("HandleAnyTouchInput(inputEvent)", StringComparison.Ordinal);
        var swallowIdx = inputSrc.IndexOf("IsTouchEmulatedMouse(mouseEvent)", StringComparison.Ordinal);
        Check(touchIdx > 0 && swallowIdx > touchIdx,
            "GlobePresentation.Input routes touch through HandleAnyTouchInput BEFORE IsTouchEmulatedMouse swallow");

        Check(planInputSrc.Contains("TryHandlePlanTouchInput(inputEvent) || HandleTouchInput(inputEvent)", StringComparison.Ordinal),
            "GlobeBattlePlanInput intercepts plan touch BEFORE TouchCameraAdapter camera pan");
        Check(planInputSrc.Contains("if (!PlanEraseMode && !BorderPaintMode && !PaintMode)", StringComparison.Ordinal),
            "GlobeBattlePlanInput only intercepts touch while a battle-plan tool or eraser is armed");
        Check(planInputSrc.Contains("_touchPlanFingers.Count >= 2", StringComparison.Ordinal) &&
              planInputSrc.Contains("CancelInProgressTouchPlanStroke()", StringComparison.Ordinal),
            "GlobeBattlePlanInput cancels single-finger stroke cleanly and hands off to 2-finger pinch zoom");
        Check(planInputSrc.Contains("_savedTouchModeForPlan", StringComparison.Ordinal) &&
              planInputSrc.Contains("_touchAdapter?.SetTapMode(restored)", StringComparison.Ordinal),
            "SetBattlePlanToolActive saves and restores prior TouchTapMode on exit");
        Check(planInputSrc.Contains("TouchPlanEraseRadiusPx = 28f", StringComparison.Ordinal) &&
              planInputSrc.Contains("DesktopPlanEraseRadiusPx = 14f", StringComparison.Ordinal) &&
              eraseSrc.Contains("float maxDistance = DesktopPlanEraseRadiusPx", StringComparison.Ordinal),
            "plan eraser uses generous 28px touch radius on mobile and 14px precision radius on PC");
        Check(planInputSrc.Contains("button.ButtonIndex is MouseButton.Left or MouseButton.Right", StringComparison.Ordinal) &&
              !planInputSrc.Contains("ObjectivePaintMode && button.ButtonIndex == MouseButton.Left", StringComparison.Ordinal),
            "PC battle-plan mouse input accepts BOTH LMB and RMB across Front, Offensive, and Fallback tools");
        Check(borderSrc.Contains("HandleBattlePlanMouseInput(input)", StringComparison.Ordinal),
            "GlobeBorderInput delegates directly to unified HandleBattlePlanMouseInput");

        Check(overlaySrc.Contains("private bool _frontGeometryDirty;", StringComparison.Ordinal) &&
              overlaySrc.Contains("BuildFrontPreview()", StringComparison.Ordinal) &&
              overlaySrc.Contains("WakeGlobeRendering();", StringComparison.Ordinal),
            "GlobePresentation.BordersAndOverlays wakes globe rendering on front preview builds and marks deferred geometry dirty");

        Check(brushSrc.Contains("_globe.SetBattlePlanToolActive(true);", StringComparison.Ordinal) &&
              brushSrc.Contains("_globe.SetBattlePlanToolActive(false);", StringComparison.Ordinal) &&
              brushSrc.Contains("ApplyBattlePlanCommands(", StringComparison.Ordinal) &&
              brushSrc.Contains("HandleEmptyStrokeRetry(kind)", StringComparison.Ordinal) &&
              brushSrc.Contains("TryLazyAnchorBorderStroke(pick)", StringComparison.Ordinal),
            "BattlePlanBrushUI uses write-locked ApplyBattlePlanCommands, SetBattlePlanToolActive, lazy anchoring, and empty-stroke retry");
        Check(helperSrc.Contains("_simRunner.RunWrite(", StringComparison.Ordinal) &&
              helperSrc.Contains("refreshSnapshot: true", StringComparison.Ordinal) &&
              helperSrc.Contains("_globe?.RefreshBattlePlans();", StringComparison.Ordinal) &&
              helperSrc.Contains("_globe?.WakeGlobeRendering();", StringComparison.Ordinal) &&
              helperSrc.Contains("Name = \"PlanDrawCancel\"", StringComparison.Ordinal) &&
              helperSrc.Contains("Name = \"PlanDrawDone\"", StringComparison.Ordinal),
            "BattlePlanBrushHelpers wraps commands in RunWrite(refreshSnapshot:true), wakes globe rendering, and exposes Cancel/Done buttons");

        // Simulation-level battle-plan command execution + deterministic fingerprint check
        var c1 = BuildTwoNationBorderCampaign(271101);
        var c2 = BuildTwoNationBorderCampaign(271101);
        Check(CampaignFingerprint.Of(c1) == CampaignFingerprint.Of(c2),
            "two identical border-plan fixture campaigns share an identical initial fingerprint");

        foreach (var c in new[] { c1, c2 })
        {
            var player = c.Player!;
            var enemy = c.Nations[1];
            var army = c.Armies.First(a => a.NationId == player.Id);
            var borderProvs = BorderFronts.Candidates(c, player.Id, enemy.Id).ToList();
            Check(borderProvs.Count > 0, "border fixture exposes candidate land-border provinces");

            // 1. Front Line (Defence)
            c.Commands.Enqueue(new DrawFrontLineCommand
            {
                IssuerId = player.Id, Kind = FrontLineKind.Defence, Provinces = borderProvs,
                ArmyIds = new List<int> { army.Id }, BorderNationId = enemy.Id, WholeBorder = true
            });
            c.Commands.ApplyAll(c);
            var front = BattlePlanning.LineFor(c, army);
            Check(front is { Kind: FrontLineKind.Defence } && front.Provinces.Count > 0,
                "DrawFrontLineCommand creates border Front Line and assigns army");

            // 2. Fallback Line (Defensive line on own controlled land)
            var ownLand = new List<int> { borderProvs[0] };
            c.Commands.Enqueue(new DrawFrontLineCommand
            {
                IssuerId = player.Id, Kind = FrontLineKind.Fallback, Provinces = ownLand,
                ArmyIds = new List<int> { army.Id }
            });
            c.Commands.ApplyAll(c);
            var fallback = BattlePlanning.LineFor(c, army);
            Check(fallback is { Kind: FrontLineKind.Fallback } && fallback.Provinces.Count > 0,
                "DrawFrontLineCommand creates Fallback Line on controlled land");

            // 3. DeleteBattlePlanCommand + ClearFrontLineCommand
            c.Commands.Enqueue(new DeleteBattlePlanCommand { IssuerId = player.Id, FrontLineId = fallback!.Id });
            c.Commands.Enqueue(new ClearFrontLineCommand { IssuerId = player.Id, ArmyIds = new List<int> { army.Id } });
            c.Commands.ApplyAll(c);
            Check(BattlePlanning.LineFor(c, army) is null,
                "DeleteBattlePlanCommand and ClearFrontLineCommand cleanly remove the drawn plan");
        }
        Check(CampaignFingerprint.Of(c1) == CampaignFingerprint.Of(c2),
            "Front/Fallback/Delete/Clear battle-plan commands produce bit-identical fingerprints across runs");
    }

    // --------------------------------------------------------- 2. Android Army Panel & Planning Bar anti-overlap

    private static void VerifyAndroidArmyAndPlanningDockAntiOverlap(string root)
    {
        var scenarios = new (string Label, float W, float H, float Sl, float St, float Sr, float Sb, float PbarW, float PbarH)[]
        {
            ("Galaxy S22 2340x1080 notch", 2340f, 1080f, 88f, 0f, 24f, 48f, 640f, 76f),
            ("Galaxy S22 2340x1080 clean wide bar", 2340f, 1080f, 0f, 0f, 0f, 0f, 920f, 76f),
            ("Compact phone 1733x800 notch", 1733f, 800f, 64f, 0f, 16f, 36f, 680f, 76f),
            ("Small phone 1280x720 clean", 1280f, 720f, 0f, 0f, 0f, 0f, 640f, 74f),
            ("Tablet 2560x1600 safe insets", 2560f, 1600f, 0f, 32f, 0f, 32f, 760f, 76f),
            ("Android 1920x1080 notch", 1920f, 1080f, 72f, 0f, 20f, 40f, 760f, 76f)
        };

        foreach (var s in scenarios)
        {
            foreach (var atWar in new[] { false, true })
            {
                var (rects, overlaps, offscreen) = ShellPlanningDock.AuditTouchDockScenario(
                    s.W, s.H, atWar, s.Sl, s.St, s.Sr, s.Sb, s.PbarW, s.PbarH);
                Check(overlaps.Count == 0,
                    $"touch dock {s.Label} war={atWar}: zero overlaps (got: {string.Join(", ", overlaps)})");
                Check(offscreen.Count == 0,
                    $"touch dock {s.Label} war={atWar}: zero offscreen (got: {string.Join(", ", offscreen)})");

                var profile = ShellLayout.ForViewport(s.W, s.H, atWar, touchMode: true, s.Sl, s.St, s.Sr, s.Sb);
                var lp = rects["left_panel"];
                var pb = rects["planning_bar"];
                var strip = rects["bottom_strip"];
                Check(!lp.Intersects(profile.NewsCard) && !pb.Intersects(profile.NewsCard),
                    $"touch dock {s.Label} war={atWar}: LeftPanel and PlanningBar clear NewsCard");
                Check(pb.Bottom <= strip.Y - 9.5f,
                    $"touch dock {s.Label} war={atWar}: PlanningBar clears BottomStrip by >=10px ({strip.Y - pb.Bottom:0.0}px)");
                Check(lp.Bottom <= strip.Y - 7.5f,
                    $"touch dock {s.Label} war={atWar}: Army LeftPanel clears BottomStrip ({strip.Y - lp.Bottom:0.0}px)");
                Check(pb.X >= lp.Right + 9.5f,
                    $"touch dock {s.Label} war={atWar}: PlanningBar docks strictly right of Army Panel");
            }
        }

        // Desktop 1920x1080 and 1280x720 must remain overlap-free too
        foreach (var (w, h) in new[] { (1920f, 1080f), (1280f, 720f) })
        {
            foreach (var atWar in new[] { false, true })
            {
                var (_, overlaps, offscreen) = ShellDockRules.AuditDockScenario(
                    w, h, leftPanelOpen: true, planningBarOpen: true, atWar: atWar);
                Check(overlaps.Count == 0 && offscreen.Count == 0,
                    $"desktop dock {w:0}x{h:0} war={atWar}: zero overlaps/offscreen");
            }
        }

        // Source contracts for scroll/clip/z-order
        var armyPanelSrc = ReadText(root, "Scripts/ui/ArmyPanelUI.cs");
        var newShellSrc = ReadText(root, "Scripts/ui/Shell/NewShellUI.cs");
        var helperSrc = ReadText(root, "Scripts/ui/BattlePlanBrushHelpers.cs");
        var ctrlSrc = ReadText(root, "Scripts/ui/Controls/ControlsIntegrationUI.cs");

        Check(helperSrc.Contains("Name = \"PlanningBarScroll\"", StringComparison.Ordinal) &&
              helperSrc.Contains("HorizontalScrollMode = ScrollContainer.ScrollMode.Auto", StringComparison.Ordinal) &&
              helperSrc.Contains("_planningBar.ClipContents = true;", StringComparison.Ordinal),
            "PlanningBar wraps buttons in a horizontal clipped ScrollContainer so narrow viewports never overflow");
        Check(armyPanelSrc.Contains("ClipTabs = true", StringComparison.Ordinal) &&
              armyPanelSrc.Contains("new HFlowContainer", StringComparison.Ordinal) &&
              armyPanelSrc.Contains("RefreshPlanningBar();", StringComparison.Ordinal),
            "ArmyPanelUI clips tabs, wraps reinforcement priority in HFlowContainer, and refreshes planning bar immediately");
        Check(newShellSrc.Contains("RaiseArmyAndPlanningChromeForNewShell(modalOpen);", StringComparison.Ordinal) &&
              helperSrc.Contains("_hudLayer.MoveChild(_armyPanel", StringComparison.Ordinal) &&
              helperSrc.Contains("_hudLayer.MoveChild(_planningBar", StringComparison.Ordinal),
            "NewShell raises ArmyPanel and PlanningBar above _newShellRoot so shell chrome never buries them");
        Check(ctrlSrc.Contains("_planningBar is { Visible: true } ? _planningBar.GetGlobalRect().Position.Y - 10f", StringComparison.Ordinal),
            "CompactProvinceInfo docks strictly above _planningBar when visible");
    }

    // --------------------------------------------------------- 3. Zero PC-only control hints on touch

    private static void VerifyZeroPcHintsOnTouch(string root)
    {
        var prevLang = Loc.Language;
        var prevAdapter = Loc.TextAdapter;
        try
        {
            Loc.ResetCatalog();
            var errors = new List<string>();
            Loc.LoadDirectory(Path.Combine(root, "game", "localization"), errors);
            Check(errors.Count == 0, $"full game localization loads cleanly (errors={errors.Count})");

            // Parity check for zz_449_mobile_orders_ux.json
            var en449 = ReadJsonStringMap(root, "game/localization/english/zz_449_mobile_orders_ux.json");
            var ru449 = ReadJsonStringMap(root, "game/localization/russian/zz_449_mobile_orders_ux.json");
            Check(en449.Count >= 50 && en449.Count == ru449.Count && en449.Keys.All(ru449.ContainsKey),
                $"zz_449_mobile_orders_ux.json has exact 1:1 EN/RU key parity ({en449.Count} keys)");

            // Desktop mode (TextAdapter == null) preserves PC hints verbatim
            Loc.TextAdapter = null;
            var desktopCheckKeys = new[]
            {
                "On the map: click selects · Shift adds · drag a box · right-click moves them there · Shift+right-click adds a waypoint · Esc lets go",
                "Click selects one. Ctrl toggles; Shift selects a range. Right-click a province on the map to send them there.",
                "Alt with the wheel zooms; Ctrl with the wheel moves the view, the middle button drags it, and Shift makes Ctrl up and down.",
                "handbook.map_controls.body"
            };
            Loc.Language = "english";
            Check(Loc.Get("plan.pc.border_idle").Contains("LMB/RMB", StringComparison.Ordinal) &&
                  Loc.Get("plan.pc.border_idle").Contains("Esc", StringComparison.Ordinal),
                "desktop EN preserves plan.pc.border_idle LMB/RMB & Esc wording");
            foreach (var dk in desktopCheckKeys)
                Check(ForbiddenTouchEnRx.IsMatch(Loc.Get(dk)),
                    $"desktop EN preserves original PC control wording for '{dk.Substring(0, Math.Min(36, dk.Length))}'");
            Loc.Language = "russian";
            Check(Loc.Get("plan.pc.border_idle").Contains("ЛКМ/ПКМ", StringComparison.Ordinal) &&
                  Loc.Get("plan.pc.border_idle").Contains("Esc", StringComparison.Ordinal),
                "desktop RU preserves plan.pc.border_idle ЛКМ/ПКМ & Esc wording");
            foreach (var dk in desktopCheckKeys)
                Check(ForbiddenTouchEnRx.IsMatch(Loc.Get(dk)) || ForbiddenTouchRuRx.IsMatch(Loc.Get(dk)) ||
                      Loc.Get(dk).Contains("правый клик", StringComparison.OrdinalIgnoreCase) ||
                      Loc.Get(dk).Contains("средняя кнопка", StringComparison.OrdinalIgnoreCase),
                    $"desktop RU preserves original PC control wording for '{dk.Substring(0, Math.Min(36, dk.Length))}'");

            // MainMenu RESUME RECENT CAMPAIGN localization + live language relabel contract
            Check(Loc.Get("shell.hero_continue_title") == "ПРОДОЛЖИТЬ КАМПАНИЮ",
                "RU translates shell.hero_continue_title to 'ПРОДОЛЖИТЬ КАМПАНИЮ'");
            var mainMenuSrc = ReadText(root, "Scripts/MainMenuScene.cs");
            Check(mainMenuSrc.Contains("heroTitle.SetMeta(SourceMeta, \"shell.hero_continue_title\");", StringComparison.Ordinal),
                "MainMenuScene registers heroTitle with SourceMeta so language switches relabel RESUME RECENT CAMPAIGN");

            // Doctrine Strategic Course scroll preservation contract
            var doctrineScreenSrc = ReadText(root, "Scripts/ui/DoctrineScreenUI.cs");
            Check(doctrineScreenSrc.Contains("_doctrineCourseScrollY = existingCourseScroll.ScrollVertical;", StringComparison.Ordinal) &&
                  doctrineScreenSrc.Contains("courseScroll.ScrollVertical = savedCourseScrollY;", StringComparison.Ordinal),
                "DoctrineScreenUI preserves and restores DoctrineCourseScroll.ScrollVertical across abandon-confirm refreshes");

            // Touch mode (same adapter installed by GodotPlatformBridge.Refresh when IsTouchPrimary == true)
            Loc.TextAdapter = static (key, text) => MobileTextAdapter.Adapt(
                text,
                isTouchPrimary: true,
                localizationKey: key,
                overrideLookup: static overrideKey => Loc.RawText(overrideKey));

            var touchKeysToAudit = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in en449.Keys)
            {
                if (k.StartsWith("mobile.plan.", StringComparison.Ordinal) ||
                    k.StartsWith("plan.bar.", StringComparison.Ordinal))
                    touchKeysToAudit.Add(k);
                else if (k.StartsWith("mobile.override.", StringComparison.Ordinal))
                    touchKeysToAudit.Add(k.Substring("mobile.override.".Length));
            }
            foreach (var k in ReadJsonStringMap(root, "game/localization/english/98_mobile_ux.json").Keys)
            {
                if (k.StartsWith("mobile.mode.", StringComparison.Ordinal) ||
                    k.StartsWith("mobile.override.", StringComparison.Ordinal))
                    touchKeysToAudit.Add(k);
            }
            // Extra raw desktop army/orders strings that may pass through Loc.Get on touch
            touchKeysToAudit.Add("Shift-click adds, Ctrl-click removes.");
            touchKeysToAudit.Add("They are still on their plan - Ctrl+H takes them off it.");
            touchKeysToAudit.Add("Nothing will move them but you, and the front no longer counts them - Shift+H hands them back.");
            touchKeysToAudit.Add("Eraser: click one of your plans. Offensive arrows erase the objective only. Esc cancels.");
            touchKeysToAudit.Add("Select divisions on one defensive line before using Alt+RMB to redraw it.");
            touchKeysToAudit.Add("Shift+RMB adds a phase to an existing offensive with fewer than 24 phases.");
            touchKeysToAudit.Add("Alt+RMB edits an existing offensive objective. Use RMB to create one.");

            foreach (var lang in new[] { "english", "russian" })
            {
                Loc.Language = lang;
                var violations = new List<string>();
                foreach (var key in touchKeysToAudit)
                {
                    var resolved = Loc.Get(key);
                    if (ForbiddenTouchEnRx.IsMatch(resolved) || ForbiddenTouchRuRx.IsMatch(resolved))
                        violations.Add($"{key} => \"{resolved}\"");
                }
                Check(violations.Count == 0,
                    $"touch mode ({lang}) has ZERO PC-only control hints across {touchKeysToAudit.Count} army/plan/mobile keys" +
                    (violations.Count > 0 ? " | violations: " + string.Join("; ", violations.Take(5)) : ""));
            }

            // Specific parenthetical analogy regression check: Order 1x tip/toast must NOT say (like right-click)/(как ПКМ)
            Loc.Language = "english";
            var enOneShot = Loc.Get("mobile.mode.order.oneshot.tip");
            Check(!enOneShot.Contains("right-click", StringComparison.OrdinalIgnoreCase) && enOneShot.Length > 10,
                $"touch EN Order 1x tip strips right-click analogy (\"{enOneShot}\")");
            Loc.Language = "russian";
            var ruOneShot = Loc.Get("mobile.mode.order.oneshot.tip");
            Check(!ruOneShot.Contains("ПКМ", StringComparison.OrdinalIgnoreCase) && ruOneShot.Length > 10,
                $"touch RU Order 1x tip strips ПКМ analogy (\"{ruOneShot}\")");
        }
        finally
        {
            Loc.TextAdapter = prevAdapter;
            Loc.Language = prevLang;
        }
    }

    // --------------------------------------------------------- 4. Corner Malevich fix + Capital/KV + CardTelemetryAudit

    private static void VerifyCornerCapitalAndCardTelemetryAudit(string root)
    {
        // 4A. ShellCornerView Malevich-square regression contracts
        var cornerSrc = ReadText(root, "Scripts/ui/Shell/ShellCornerView.cs");
        Check(cornerSrc.Contains("Name = \"ShellRankInner\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("_rankHost.AddChild(rankInner);", StringComparison.Ordinal) &&
              cornerSrc.Contains("rankInner.AddChild(_rankActiveUnderline);", StringComparison.Ordinal) &&
              !cornerSrc.Contains("_rankHost.AddChild(_rankActiveUnderline);", StringComparison.Ordinal),
            "ShellCornerView wraps _rankActiveUnderline inside plain Control ShellRankInner (never direct child of PanelContainer)");
        Check(cornerSrc.Contains("AnchorTop = 1f, AnchorBottom = 1f, OffsetTop = -2.5f", StringComparison.Ordinal),
            "ShellCornerView anchors _rankActiveUnderline as a bottom 2.5px bar instead of full-rect fill");
        Check(cornerSrc.Contains("AddThemeStyleboxOverride(\"normal\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("AddThemeStyleboxOverride(\"hover\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("AddThemeStyleboxOverride(\"pressed\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("AddThemeStyleboxOverride(\"hover_pressed\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("AddThemeStyleboxOverride(\"focus\"", StringComparison.Ordinal) &&
              cornerSrc.Contains("AddThemeStyleboxOverride(\"disabled\"", StringComparison.Ordinal) &&
              !cornerSrc.Contains("_flagButton.ButtonPressed", StringComparison.Ordinal),
            "ShellCornerView overrides all 6 flagBtn StyleBox states and never mutates non-toggle ButtonPressed");

        // 4B. Blank Capital / zero-width ClipText HBox regression contracts
        var outlookSrc = ReadText(root, "Scripts/ui/Shell/ShellCountryOutlookView.cs");
        var countryAuditSrc = ReadText(root, "Scripts/ui/Shell/ShellCountryAuditCardsView.cs");
        var dossierSrc = ReadText(root, "Scripts/ui/Shell/ShellCountryDossierView.cs");
        var regVisualSrc = ReadText(root, "Scripts/ui/Shell/ShellRegionVisualsView.cs");
        var regAuditSrc = ReadText(root, "Scripts/ui/Shell/ShellRegionAuditCardsView.cs");
        var regKpiSrc = ReadText(root, "Scripts/ui/Shell/ShellRegionKpiCardsView.cs");
        var regClassicSrc = ReadText(root, "Scripts/ui/RegionInteractionUI.cs");

        Check(outlookSrc.Contains("MakeLabel(ctx, title, \"shell.sans\", 11, \"shell.paper_muted\", clip: false, expand: false)", StringComparison.Ordinal) &&
              outlookSrc.Contains("MakeLabel(ctx, capital, \"shell.serif_semibold\", 11, \"shell.brass_hi\", clip: true, expand: true)", StringComparison.Ordinal),
            "ShellCountryOutlookView.AddCapitalRow prevents zero-width collapsed Capital label");
        Check(countryAuditSrc.Contains("public static void AddSafeKvRow(", StringComparison.Ordinal) &&
              countryAuditSrc.Contains("SizeFlagsStretchRatio = 1.45f", StringComparison.Ordinal) &&
              dossierSrc.Contains("ShellCountryAuditCardsView.AddSafeKvRow(", StringComparison.Ordinal) &&
              regVisualSrc.Contains("ShellCountryAuditCardsView.AddSafeKvRow(", StringComparison.Ordinal),
            "Country Dossier & Region Card delegate AddKvRow to zero-width-safe AddSafeKvRow");
        Check(regVisualSrc.Contains("MakeLabel(ctx, title, \"shell.serif_semibold\", 13, \"shell.paper_muted\", clip: false)", StringComparison.Ordinal) &&
              regKpiSrc.Contains("MakeLabel(ctx, shortTag, \"shell.sans\", 9, \"shell.paper_faint\", clip: false)", StringComparison.Ordinal),
            "Region section header and terrain pill keep short labels non-clipped so Godot HBox never collapses them to 0px");
        Check(regAuditSrc.Contains("\"ShellRegionMovementsCard\"", StringComparison.Ordinal) &&
              regAuditSrc.Contains("\"ShellRegionGovernanceCard\"", StringComparison.Ordinal) &&
              regAuditSrc.Contains("\"ShellRegionDamageCrisesCard\"", StringComparison.Ordinal) &&
              regAuditSrc.Contains("\"CompactProvinceMovementsBadge\"", StringComparison.Ordinal) &&
              regClassicSrc.Contains("CardTelemetryAudit.ReadRegionMovements(", StringComparison.Ordinal),
            "Region Card (NewShell + Classic + CompactProvinceInfo) surfaces Movements in Region and audited telemetry cards");

        // 4C. EN/RU parity for zz_450_region_cards_audit.json
        var en450 = ReadJsonStringMap(root, "game/localization/english/zz_450_region_cards_audit.json");
        var ru450 = ReadJsonStringMap(root, "game/localization/russian/zz_450_region_cards_audit.json");
        Check(en450.Count >= 100 && en450.Count == ru450.Count && en450.Keys.All(ru450.ContainsKey) &&
              en450.Values.All(v => !string.IsNullOrWhiteSpace(v)) &&
              ru450.Values.All(v => !string.IsNullOrWhiteSpace(v)),
            $"zz_450_region_cards_audit.json has exact 1:1 non-empty EN/RU key parity ({en450.Count} keys)");

        // 4D. Live CardTelemetryAudit read-only + CampaignFingerprint bit-identical verification
        var campaign = Campaign.CreateBootstrap(20261127);
        Movements.Found(campaign);
        var activeMov = Movements.Active(campaign).FirstOrDefault(m => m.Movement is { Dissolved: false });
        Check(activeMov is not null, "bootstrap campaign with Movements.Found produces at least one active movement");

        var hqState = campaign.States.FirstOrDefault(s => s.MapIndex == activeMov!.Movement!.HeadquartersStateMapIndex)
                      ?? campaign.States.First(s => !s.Unclaimed);
        var hostNation = campaign.NationByMapIndex(hqState.NationMapIndex) ?? campaign.Nations[0];
        campaign.ConfigureSinglePlayer(hostNation.Id);

        // Seed rich regional & national telemetry states before taking fingerprint
        activeMov!.Movement!.HeadquartersStateMapIndex = hqState.MapIndex;
        activeMov.Movement.Support[hqState.MapIndex] = 0.62f;
        activeMov.Movement.Infiltrated[hqState.MapIndex] = 450f;
        hqState.SecessionPressure = 0.44f;
        hqState.WarDamage = 0.28f;
        hqState.RebuildingUntilDay = campaign.ElapsedDays + 45;
        hqState.Displaced["local"] = 12_500;
        hqState.BrainDrainPressure = 0.22f;
        hqState.Buildings[BuildingKind.Infrastructure] = 3;
        hqState.Buildings[BuildingKind.Barracks] = 2;
        hqState.Buildings[BuildingKind.RailNetwork] = 1;
        hostNation.PoliticalPoints = 100f;
        if (!RegionalAutonomy.Grant(campaign, hostNation, hqState))
            hqState.AutonomousSinceDay = campaign.ElapsedDays;
        hostNation.BrainDrainRate = -0.14f;
        hostNation.WeeklyNetMigration = -650;
        hostNation.CumulativeNetMigration = -14_200;
        var peerNation = campaign.Nations.First(n => n.Id != hostNation.Id && n.Movement is null && n.Population > 0);
        hostNation.BrainDrainDestinationId = peerNation.Id;
        hqState.Claims.Add(peerNation.Tag);

        var fpBefore = CampaignFingerprint.Of(campaign);
        var rngBefore = campaign.Random.State;

        var regMovs = CardTelemetryAudit.ReadRegionMovements(campaign, hqState, canInspectAll: true);
        Check(regMovs.Count >= 1 && regMovs[0].NationId == activeMov.Id && regMovs[0].IsHeadquarters &&
              regMovs[0].PeopleSupport >= 0.60f && regMovs[0].InfiltratedFighters >= 400f,
            $"ReadRegionMovements surfaces HQ movement '{regMovs.FirstOrDefault()?.Name}' sorted first with exact support & fighters");

        var regAudit = CardTelemetryAudit.ReadRegionAudit(campaign, hostNation, hqState, -1, canInspectAll: false);
        var regAuditObs = CardTelemetryAudit.ReadRegionAudit(campaign, hostNation, hqState, -1, canInspectAll: true);
        Check(regAudit.Movements.Count >= 1 && regAudit.IsAutonomous &&
              MathF.Abs(regAudit.SecessionPressure - 0.44f) < 0.001f &&
              MathF.Abs(regAudit.WarDamageShare - 0.28f) < 0.001f &&
              regAudit.RebuildingDaysLeft == 45 &&
              regAudit.DisplacedPeople == 12_500 &&
              regAudit.InfrastructureLevel == 3 && regAudit.BarracksLevel == 2 && regAudit.RailLevel == 1 &&
              regAudit.ForeignClaimants.Any(fc => fc.NationId == peerNation.Id) &&
              regAuditObs.Movements.Count == regAudit.Movements.Count,
            "ReadRegionAudit surfaces movements, autonomy, secession, war damage, reconstruction, displaced, buildings, and foreign claimants");

        var countryAudit = CardTelemetryAudit.ReadCountryAudit(campaign, hostNation, canInspectAll: true);
        Check(countryAudit.ActiveMovementsCount >= 1 &&
              countryAudit.MovementsWithHqInsideCount >= 1 &&
              countryAudit.TopMovements.Any(tm => tm.NationId == activeMov.Id) &&
              countryAudit.RestiveRegionsCount >= 1 &&
              countryAudit.AutonomousRegionsCount >= 1 &&
              countryAudit.DamagedRegionsCount >= 1 &&
              countryAudit.BrainDrainDestinationName == peerNation.DisplayName &&
              countryAudit.RankBreakdown is { OverallRank: >= 1, TotalRanked: >= 1 },
            "ReadCountryAudit surfaces active movements, HQ count, restive/autonomous/damaged regions, brain drain destination, and power rank breakdown");

        // Also sweep several other nations & states to ensure zero lazy cache side-effects
        foreach (var n in campaign.Nations.Where(x => x.Movement is null && x.Population > 0).Take(8))
            _ = CardTelemetryAudit.ReadCountryAudit(campaign, n, canInspectAll: false);
        foreach (var s in campaign.States.Where(x => !x.Unclaimed).Take(12))
            _ = CardTelemetryAudit.ReadRegionAudit(campaign, campaign.NationByMapIndex(s.NationMapIndex), s, -1, canInspectAll: false);

        Check(CampaignFingerprint.Of(campaign) == fpBefore && campaign.Random.State == rngBefore,
            "CardTelemetryAudit (ReadRegionMovements, ReadRegionAudit, ReadCountryAudit) leaves CampaignFingerprint & RNG bit-identical");
    }

    // --------------------------------------------------------- 5. Doctrine Abandonment & Study Cancellation

    private static void VerifyDoctrineAbandonmentAndStudyCancellation(string root)
    {
        // 5A. EN/RU parity for zz_451_doctrine_cancel.json
        var en451 = ReadJsonStringMap(root, "game/localization/english/zz_451_doctrine_cancel.json");
        var ru451 = ReadJsonStringMap(root, "game/localization/russian/zz_451_doctrine_cancel.json");
        Check(en451.Count >= 20 && en451.Count == ru451.Count && en451.Keys.All(ru451.ContainsKey) &&
              en451.Values.All(v => !string.IsNullOrWhiteSpace(v)) &&
              ru451.Values.All(v => !string.IsNullOrWhiteSpace(v)),
            $"zz_451_doctrine_cancel.json has exact 1:1 non-empty EN/RU key parity ({en451.Count} keys)");

        // 5B. UI contracts for two-click inline confirmation, observer guard, and companion tracking
        var abandonUiSrc = ReadText(root, "Scripts/ui/DoctrineAbandonUI.cs");
        var docUiSrc = ReadText(root, "Scripts/ui/DoctrineUI.cs");
        var docScreenSrc = ReadText(root, "Scripts/ui/DoctrineScreenUI.cs");
        var docTreeSrc = ReadText(root, "Scripts/ui/DoctrineTreeUI.cs");

        Check(abandonUiSrc.Contains("_doctrineAbandonConfirmKey", StringComparison.Ordinal) &&
              abandonUiSrc.Contains("ObserverUiRules.IsObserving(", StringComparison.Ordinal) &&
              abandonUiSrc.Contains("Name = \"AbandonDoctrineBook\"", StringComparison.Ordinal) &&
              abandonUiSrc.Contains("\"CancelStudy_\" + nodeId", StringComparison.Ordinal) &&
              abandonUiSrc.Contains("\"PreviewAbandon_\"", StringComparison.Ordinal) &&
              abandonUiSrc.Contains("\"PreviewCancelStudy_\"", StringComparison.Ordinal) &&
              abandonUiSrc.Contains("\"AbandonStrategy_\"", StringComparison.Ordinal) &&
              abandonUiSrc.Contains("var comp = track.Companion;", StringComparison.Ordinal),
            "DoctrineAbandonUI implements 2-click inline confirmation, observer guards, toolbar/card/preview/strategy buttons, and companion state tracking");
        Check(docScreenSrc.Contains("EnsureDoctrineToolbarAbandonButton(", StringComparison.Ordinal) &&
              docScreenSrc.Contains("RefreshDoctrineToolbarAbandon(", StringComparison.Ordinal) &&
              docScreenSrc.Contains("ResetDoctrineAbandonConfirm()", StringComparison.Ordinal) &&
              docTreeSrc.Contains("AddDoctrineCancelButton(", StringComparison.Ordinal) &&
              docTreeSrc.Contains("RefreshDoctrineCardButtons(", StringComparison.Ordinal) &&
              docUiSrc.Contains("AddStrategyCardActionRow(", StringComparison.Ordinal) &&
              docUiSrc.Contains("AddPreviewBookHeader(", StringComparison.Ordinal) &&
              docUiSrc.Contains("AddGrandStrategyAbandonRow(", StringComparison.Ordinal),
            "DoctrineScreenUI, DoctrineTreeUI, and DoctrineUI wire all abandon/cancel controls cleanly");

        // 5C. Live simulation lifecycle: Tactical pending cancel, mid-book study cancel, 2-book promotion, companion abandon, sole abandon
        var c = Campaign.CreateBootstrap(20261128);
        var n = c.NationByTag("ROU") ?? c.Nations.First(x => x.Movement is null && x.Population > 0);
        c.ConfigureSinglePlayer(n.Id);
        n.IsHuman = true;
        var docRegion = c.StatesOf(n).First();
        c.AttachProvinceTopology(ProvinceTopology.Build(
            new[] { (ushort)docRegion.MapIndex, (ushort)docRegion.MapIndex },
            new[] { (ushort)n.MapIndex, (ushort)n.MapIndex }, 2, 1, _ => false));
        n.TacticalDoctrine = new TacticalDoctrineMemory();
        n.StrategicDoctrine = new StrategicDoctrineMemory();
        n.ArmyExperience = 5000f;
        n.AirExperience = 5000f;
        n.NavalExperience = 5000f;
        n.PoliticalPoints = 500f;
        NationalModifiers.Invalidate(n);

        var groundBooks = TacticalDoctrineCatalog.All.Where(b => b.Slot == DoctrineSlots.Ground).Take(3).ToArray();
        Check(groundBooks.Length == 3, "catalog provides at least 3 ground tactical books to test 2-book shelf promotion");
        var bookA = groundBooks[0];
        var bookB = groundBooks[1];
        var bookC = groundBooks[2];
        var rootA = bookA.Nodes.First(node => node.Requires.Count == 0);
        var nextA = bookA.Nodes.First(node => node.Requires.Contains(rootA.Id));
        var rootB = bookB.Nodes.First(node => node.Requires.Count == 0);
        var rootC = bookC.Nodes.First(node => node.Requires.Count == 0);

        // (i) Pending first patient study -> free Abandon (0 PP, no retraining)
        var startPending = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = bookA.Id, NodeId = rootA.Id, Rush = false };
        c.Commands.Enqueue(startPending); c.Commands.ApplyAll(c);
        Check(startPending.Refusal.Length == 0 &&
              TacticalDoctrines.IsHeld(n, bookA.Id, out var estPending) && !estPending,
            "starting patient first study marks book held but not yet established");
        var ppBeforeFree = n.PoliticalPoints;
        var abandonPending = new AbandonDoctrineCommand { IssuerId = n.Id, BranchId = bookA.Id };
        c.Commands.Enqueue(abandonPending); c.Commands.ApplyAll(c);
        Check(abandonPending.Refusal.Length == 0 &&
              MathF.Abs(n.PoliticalPoints - ppBeforeFree) < 0.001f &&
              !ModifierSystem.Has(n, TacticalDoctrines.RetrainingModifier) &&
              !TacticalDoctrines.IsHeld(n, bookA.Id, out _),
            "abandoning pending first study is free (0 PP), imposes no retraining, and clears shelf");

        // (ii) Mid-book CancelStudy -> keeps learned articles, does NOT refund spent XP, refuses second cancel
        var rushA = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = bookA.Id, NodeId = rootA.Id, Rush = true };
        c.Commands.Enqueue(rushA); c.Commands.ApplyAll(c);
        Check(rushA.Refusal.Length == 0 && TacticalDoctrines.Known(n, rootA.Id),
            "established bookA with root article");
        var studyNextA = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = bookA.Id, NodeId = nextA.Id, Rush = false };
        c.Commands.Enqueue(studyNextA); c.Commands.ApplyAll(c);
        Check(studyNextA.Refusal.Length == 0, "started patient study of 2nd article in bookA");
        var xpAfterStudyStart = n.ArmyExperience;
        var ppBeforeCancel = n.PoliticalPoints;
        var cancelStudy = new CancelDoctrineStudyCommand { IssuerId = n.Id, BranchId = bookA.Id };
        c.Commands.Enqueue(cancelStudy); c.Commands.ApplyAll(c);
        var trackAfterCancel = n.TacticalDoctrine.Track(DoctrineSlots.Ground);
        Check(cancelStudy.Refusal.Length == 0 &&
              trackAfterCancel.LearningNode.Length == 0 &&
              trackAfterCancel.LearningBranch.Length == 0 &&
              trackAfterCancel.LearningDoneDay == -1 &&
              TacticalDoctrines.Known(n, rootA.Id) &&
              MathF.Abs(n.ArmyExperience - xpAfterStudyStart) < 0.001f &&
              MathF.Abs(n.PoliticalPoints - ppBeforeCancel) < 0.001f &&
              !ModifierSystem.Has(n, TacticalDoctrines.RetrainingModifier),
            "CancelDoctrineStudyCommand cancels active study, keeps learned articles, does not refund XP, and charges 0 PP");

        var fpBeforeSecondCancel = CampaignFingerprint.Of(c);
        var secondCancel = new CancelDoctrineStudyCommand { IssuerId = n.Id, BranchId = bookA.Id };
        c.Commands.Enqueue(secondCancel); c.Commands.ApplyAll(c);
        Check(secondCancel.Refusal.Length > 0 && CampaignFingerprint.Of(c) == fpBeforeSecondCancel,
            "second CancelDoctrineStudyCommand refuses cleanly without mutating fingerprint");

        // (iii) Fill 2-book shelf -> 3rd book blocked -> insufficient PP refused -> abandon primary promotes companion & unlocks 3rd book
        var rushB = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = bookB.Id, NodeId = rootB.Id, Rush = true };
        c.Commands.Enqueue(rushB); c.Commands.ApplyAll(c);
        Check(rushB.Refusal.Length == 0 &&
              n.TacticalDoctrine.Track(DoctrineSlots.Ground).Companion?.Branch == bookB.Id,
            "bookB occupies companion slot on the 2-book ground shelf");
        Check(TacticalDoctrines.Refusal(c, n, bookC.Id, rootC.Id, true).Length > 0,
            "3rd ground book is refused while both shelf slots are full");

        // Insufficient PP refusal
        var rewriteCost = c.Defines.DoctrineRewritePoliticalCost;
        if (rewriteCost > 0f)
        {
            var savedPp = n.PoliticalPoints;
            n.PoliticalPoints = MathF.Max(0f, rewriteCost - 1f);
            var fpLowPp = CampaignFingerprint.Of(c);
            var lowPpAbandon = new AbandonDoctrineCommand { IssuerId = n.Id, BranchId = bookA.Id };
            c.Commands.Enqueue(lowPpAbandon); c.Commands.ApplyAll(c);
            Check(lowPpAbandon.Refusal.Length > 0 && CampaignFingerprint.Of(c) == fpLowPp,
                "AbandonDoctrineCommand refuses when political points are below DoctrineRewritePoliticalCost");
            n.PoliticalPoints = savedPp;
        }

        var chronicleCountBefore = c.Chronicle.Entries.Count;
        var ppBeforeAbandonA = n.PoliticalPoints;
        var fpBeforeAbandonA = CampaignFingerprint.Of(c);
        var abandonA = new AbandonDoctrineCommand { IssuerId = n.Id, BranchId = bookA.Id };
        c.Commands.Enqueue(abandonA); c.Commands.ApplyAll(c);
        var primaryAfterPromote = n.TacticalDoctrine.Track(DoctrineSlots.Ground);
        Check(abandonA.Refusal.Length == 0 &&
              primaryAfterPromote.Branch == bookB.Id &&
              primaryAfterPromote.Known.Contains(rootB.Id) &&
              primaryAfterPromote.Companion is null &&
              !TacticalDoctrines.IsHeld(n, bookA.Id, out _) &&
              MathF.Abs(n.PoliticalPoints - (ppBeforeAbandonA - rewriteCost)) < 0.01f &&
              ModifierSystem.Has(n, TacticalDoctrines.RetrainingModifier) &&
              c.Chronicle.Entries.Skip(chronicleCountBefore).Any(e => e.Kind == "doctrine_abandoned") &&
              CampaignFingerprint.Of(c) != fpBeforeAbandonA,
            "abandoning primary promotes companion (bookB) into primary, clears companion, deducts PP, grants retraining, logs chronicle, and moves fingerprint");

        // Verify bookC can now enter the freed companion slot!
        var rushC = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = bookC.Id, NodeId = rootC.Id, Rush = true };
        c.Commands.Enqueue(rushC); c.Commands.ApplyAll(c);
        Check(rushC.Refusal.Length == 0 &&
              n.TacticalDoctrine.Track(DoctrineSlots.Ground).Companion?.Branch == bookC.Id,
            "shelf promotion unblocks adopting a new companion book (bookC)");

        // (iv) Abandon companion book -> primary stays intact
        var abandonC = new AbandonDoctrineCommand { IssuerId = n.Id, BranchId = bookC.Id };
        c.Commands.Enqueue(abandonC); c.Commands.ApplyAll(c);
        Check(abandonC.Refusal.Length == 0 &&
              n.TacticalDoctrine.Track(DoctrineSlots.Ground).Branch == bookB.Id &&
              n.TacticalDoctrine.Track(DoctrineSlots.Ground).Companion is null,
            "abandoning companion removes only companion and leaves primary (bookB) intact");

        // (v) Abandon sole primary -> shelf becomes empty and LastRewriteDay recorded
        var abandonB = new AbandonDoctrineCommand { IssuerId = n.Id, BranchId = bookB.Id };
        c.Commands.Enqueue(abandonB); c.Commands.ApplyAll(c);
        var emptyTrack = n.TacticalDoctrine.Track(DoctrineSlots.Ground);
        Check(abandonB.Refusal.Length == 0 &&
              emptyTrack.Branch.Length == 0 &&
              emptyTrack.Known.Count == 0 &&
              emptyTrack.Companion is null &&
              emptyTrack.LastRewriteDay == c.ElapsedDays,
            "abandoning sole primary clears shelf completely and stamps LastRewriteDay");

        // 5D. Strategic Doctrine Abandonment + Anti-Exploit Full-Cost Re-Adopt
        var courses = StrategicDoctrineCatalog.All.Take(2).ToArray();
        Check(courses.Length == 2, "catalog provides at least 2 strategic courses");
        var course1 = courses[0];
        var course2 = courses[1];
        var modKey = course1.Modifiers.First().Key;
        var modVal = course1.Modifiers.First().Value;

        Check(StrategicDoctrines.IsFirstChoiceFree(n), "fresh nation has IsFirstChoiceFree == true");
        var modBeforeAdopt = NationalModifiers.Of(n, modKey);
        var ppBeforeFreeStrat = n.PoliticalPoints;
        var adopt1 = new AdoptStrategicDoctrineCommand { IssuerId = n.Id, DoctrineId = course1.Id };
        c.Commands.Enqueue(adopt1); c.Commands.ApplyAll(c);
        Check(adopt1.Refusal.Length == 0 &&
              n.StrategicDoctrine.Id == course1.Id &&
              MathF.Abs(n.PoliticalPoints - ppBeforeFreeStrat) < 0.001f &&
              MathF.Abs(NationalModifiers.Of(n, modKey) - (modBeforeAdopt + modVal)) < 0.001f,
            "first strategic course adoption is free and applies its modifiers immediately");

        var stratChronBefore = c.Chronicle.Entries.Count;
        var fpBeforeStratAbandon = CampaignFingerprint.Of(c);
        var abandonStrat = new AbandonStrategicDoctrineCommand { IssuerId = n.Id };
        c.Commands.Enqueue(abandonStrat); c.Commands.ApplyAll(c);
        Check(abandonStrat.Refusal.Length == 0 &&
              n.StrategicDoctrine.Id.Length == 0 &&
              n.StrategicDoctrine.LastChangeDay == c.ElapsedDays &&
              MathF.Abs(n.PoliticalPoints - ppBeforeFreeStrat) < 0.001f &&
              MathF.Abs(NationalModifiers.Of(n, modKey) - modBeforeAdopt) < 0.001f &&
              c.Chronicle.Entries.Skip(stratChronBefore).Any(e => e.Kind == "strategy_abandoned") &&
              CampaignFingerprint.Of(c) != fpBeforeStratAbandon,
            "AbandonStrategicDoctrineCommand clears course & modifiers for 0 PP, stamps LastChangeDay, logs chronicle, and moves fingerprint");

        // Second abandon refused
        var fpAfterStratAbandon = CampaignFingerprint.Of(c);
        var abandonStrat2 = new AbandonStrategicDoctrineCommand { IssuerId = n.Id };
        c.Commands.Enqueue(abandonStrat2); c.Commands.ApplyAll(c);
        Check(abandonStrat2.Refusal.Length > 0 && CampaignFingerprint.Of(c) == fpAfterStratAbandon,
            "second AbandonStrategicDoctrineCommand refuses cleanly with unchanged fingerprint");

        // Anti-exploit: IsFirstChoiceFree must now be FALSE, and re-adopting MUST cost full course2.PoliticalCost
        Check(!StrategicDoctrines.IsFirstChoiceFree(n),
            "anti-exploit: IsFirstChoiceFree becomes false after abandoning a strategic course");
        n.PoliticalPoints = MathF.Max(0f, course2.PoliticalCost - 1f);
        var fpBeforeCheat = CampaignFingerprint.Of(c);
        var cheapReAdopt = new AdoptStrategicDoctrineCommand { IssuerId = n.Id, DoctrineId = course2.Id };
        c.Commands.Enqueue(cheapReAdopt); c.Commands.ApplyAll(c);
        Check(cheapReAdopt.Refusal.Length > 0 && n.StrategicDoctrine.Id.Length == 0 && CampaignFingerprint.Of(c) == fpBeforeCheat,
            "anti-exploit: re-adopting a strategic course after abandonment is refused without full PoliticalCost");

        n.PoliticalPoints = course2.PoliticalCost + 25f;
        var paidReAdopt = new AdoptStrategicDoctrineCommand { IssuerId = n.Id, DoctrineId = course2.Id };
        c.Commands.Enqueue(paidReAdopt); c.Commands.ApplyAll(c);
        Check(paidReAdopt.Refusal.Length == 0 &&
              n.StrategicDoctrine.Id == course2.Id &&
              MathF.Abs(n.PoliticalPoints - 25f) < 0.01f,
            "re-adopting a strategic course after abandonment deducts exact full PoliticalCost");

        // 5E. Feature gates & NetworkCommandCodec round-trip & Save/Load fingerprint parity
        var tacAbandon = new AbandonDoctrineCommand { IssuerId = n.Id, BranchId = bookA.Id };
        var tacCancel = new CancelDoctrineStudyCommand { IssuerId = n.Id, BranchId = bookA.Id };
        var stratAbandonCmd = new AbandonStrategicDoctrineCommand { IssuerId = n.Id };

        Check(NetworkCommandCodec.TryEncode(tacAbandon, out var t1, out var j1) &&
              NetworkCommandCodec.TryDecode(t1, j1, out var d1) && d1 is AbandonDoctrineCommand a1 && a1.BranchId == bookA.Id,
            "NetworkCommandCodec round-trips AbandonDoctrineCommand");
        Check(NetworkCommandCodec.TryEncode(tacCancel, out var t2, out var j2) &&
              NetworkCommandCodec.TryDecode(t2, j2, out var d2) && d2 is CancelDoctrineStudyCommand c2 && c2.BranchId == bookA.Id,
            "NetworkCommandCodec round-trips CancelDoctrineStudyCommand");
        Check(NetworkCommandCodec.TryEncode(stratAbandonCmd, out var t3, out var j3) &&
              NetworkCommandCodec.TryDecode(t3, j3, out var d3) && d3 is AbandonStrategicDoctrineCommand s3 && s3.IssuerId == n.Id,
            "NetworkCommandCodec round-trips AbandonStrategicDoctrineCommand");

        c.Defines.Features["tactical_doctrines"] = false;
        c.Defines.Features["strategic_doctrines"] = false;
        var fpDisabled = CampaignFingerprint.Of(c);
        Check(!MechanicFeatures.Allows(c, tacAbandon) &&
              !MechanicFeatures.Allows(c, tacCancel) &&
              !MechanicFeatures.Allows(c, stratAbandonCmd),
            "MechanicFeatures gates all three new doctrine commands under tactical_doctrines / strategic_doctrines");
        c.Commands.Enqueue(new AbandonStrategicDoctrineCommand { IssuerId = n.Id });
        c.Commands.ApplyAll(c);
        Check(n.StrategicDoctrine.Id == course2.Id && CampaignFingerprint.Of(c) == fpDisabled,
            "disabled strategic_doctrines blocks AbandonStrategicDoctrineCommand without state mutation");
        c.Defines.Features["tactical_doctrines"] = true;
        c.Defines.Features["strategic_doctrines"] = true;

        // Save/Load snapshot parity after abandonment & re-adoption
        var fpSaved = CampaignFingerprint.Of(c);
        var snap = SaveSystem.ExportNetworkSnapshot(c, "ux-doctrine-cancel");
        var imported = SaveSystem.ImportNetworkSnapshot(snap, "ux-doctrine-cancel");
        Check(imported.Success, "post-abandonment snapshot imports cleanly");
        if (c.ProvinceMap is not null)
            imported.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        var restoredN = imported.Campaign.NationById(n.Id)!;
        var restoredTrack = restoredN.TacticalDoctrine.Track(DoctrineSlots.Ground);
        Check(restoredN.StrategicDoctrine.Id == course2.Id &&
              restoredN.StrategicDoctrine.LastChangeDay == c.ElapsedDays &&
              restoredTrack.Branch.Length == 0 &&
              restoredTrack.Companion is null &&
              CampaignFingerprint.Of(imported.Campaign) == fpSaved,
            $"post-abandonment doctrine state preserves bit-identical CampaignFingerprint across snapshot save/load (expected 0x{fpSaved:X16}, got 0x{CampaignFingerprint.Of(imported.Campaign):X16})");
    }

    // --------------------------------------------------------- 6. Style & architecture hygiene

    private static void VerifyNoRawColorsOrGodotInSim(string root)
    {
        var simNewFiles = new[]
        {
            "Scripts/Simulation/CardTelemetryAudit.cs",
            "Scripts/Simulation/Shell/ShellPlanningDock.cs",
            "Scripts/Simulation/TacticalDoctrines.Abandon.cs",
            "Scripts/Simulation/StrategicDoctrines.Abandon.cs"
        };
        foreach (var rel in simNewFiles)
        {
            var src = ReadText(root, rel);
            Check(!src.Contains("using Godot", StringComparison.Ordinal),
                $"{rel} stays free of Godot scene dependencies");
        }

        var uiFiles = new[]
        {
            "Scripts/ui/Shell/ShellCornerView.cs",
            "Scripts/ui/Shell/ShellCountryOutlookView.cs",
            "Scripts/ui/Shell/ShellCountryAuditCardsView.cs",
            "Scripts/ui/Shell/ShellRegionAuditCardsView.cs",
            "Scripts/ui/BattlePlanBrushHelpers.cs",
            "Scripts/ui/DoctrineAbandonUI.cs"
        };
        foreach (var rel in uiFiles)
        {
            var src = ReadText(root, rel);
            Check(!Regex.IsMatch(src, @"\bnew\s+Color\s*\("),
                $"{rel} contains zero raw 'new Color(' literals (uses Ui.Colour / theme tokens)");
        }
    }

    // --------------------------------------------------------- helpers

    private static Campaign BuildTwoNationBorderCampaign(int seed)
    {
        var c = new Campaign(seed, new Defines()) { ScenarioId = "ux_border_plan_fixture" };
        var a = new Nation { Tag = "UFA", MapIndex = 101, Name = "Alpha", Population = 2_000_000, Gdp = 120, Treasury = 60, OutputScale = 1 };
        var b = new Nation { Tag = "UFB", MapIndex = 102, Name = "Beta", Population = 2_000_000, Gdp = 120, Treasury = 60, OutputScale = 1 };
        c.AddNation(a);
        c.AddNation(b);
        c.States.Add(new ScenarioState { Id = "ufa_1", Name = "Alpha Border", MapIndex = 201, NationMapIndex = 101, NationId = "UFA", Population = 2_000_000 });
        c.States.Add(new ScenarioState { Id = "ufb_1", Name = "Beta Border", MapIndex = 202, NationMapIndex = 102, NationId = "UFB", Population = 2_000_000 });

        // 24x8 grid: left 12 columns belong to nation 101 (state 201), right 12 columns to nation 102 (state 202)
        const int w = 24, h = 8;
        var states = new ushort[w * h];
        var owners = new ushort[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var idx = y * w + x;
                var left = x < 12;
                states[idx] = (ushort)(left ? 201 : 202);
                owners[idx] = (ushort)(left ? 101 : 102);
            }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, w, h, _ => false));
        c.ConfigureSinglePlayer(a.Id);
        a.IsHuman = true;

        var firstOwnProv = Enumerable.Range(1, c.ProvinceMap!.ProvinceCount)
            .First(p => !c.ProvinceMap.IsWater(p) && c.ProvinceMap.ProvinceToNation[p] == 101);
        c.Armies.Add(new Army
        {
            Id = 1, NationId = a.Id, StateMapIndex = 201, ProvinceId = firstOwnProv,
            Divisions = 3, Strength = 1f, Personnel = 30_000
        });
        SovereignFinance.NormalizeAccounts(c);
        return c;
    }

    private static Dictionary<string, string> ReadJsonStringMap(string root, string relPath)
    {
        var raw = ReadText(root, relPath);
        using var doc = JsonDocument.Parse(raw);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            map[prop.Name] = prop.Value.GetString() ?? "";
        return map;
    }

    private static string ReadText(string root, string relPath) =>
        File.ReadAllText(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
