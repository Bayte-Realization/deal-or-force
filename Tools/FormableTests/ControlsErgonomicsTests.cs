using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;

static class ControlsErgonomicsTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("ControlsErgonomicsTests FAILED: " + label);
        Console.WriteLine("PASS " + label);
    }

    static Campaign CreateSyntheticCampaignWithMap(out Nation player, out ScenarioState stateA, out ScenarioState stateB,
        out int provA, out int provB)
    {
        InterfaceSkin.ResetCatalog();
        SettingsCatalog.ResetCatalog();
        SettingsCatalog.Reset("mouse_scheme");
        SettingsCatalog.Reset("rmb_everywhere");
        SettingsCatalog.Reset("edge_pan");
        SettingsCatalog.Reset("drag_threshold_px");
        SettingsCatalog.Reset("drag_threshold_ms");
        var campaign = Campaign.CreateBootstrap(42);
        player = campaign.Nations.FirstOrDefault(n =>
            n.Movement is not { Landless: true } &&
            campaign.StatesOf(n).Count(s => !s.Unclaimed && !LakeRegions.IsWaterState(s)) >= 2)
                 ?? campaign.Nations[0];
        campaign.ConfigureSinglePlayer(player.Id);
        var playerStates = campaign.StatesOf(player).Where(s => !s.Unclaimed && !LakeRegions.IsWaterState(s)).ToArray();
        stateA = playerStates[0];
        stateB = playerStates[1];
        var idxA = (ushort)stateA.MapIndex;
        var idxB = (ushort)stateB.MapIndex;

        const int w = 16, h = 8;
        var states = new ushort[w * h];
        var control = new ushort[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            states[y * w + x] = x < 8 ? idxA : idxB;
            control[y * w + x] = (ushort)player.MapIndex;
        }
        var map = ProvinceTopology.Build(states, control, w, h, _ => false);
        campaign.AttachProvinceTopology(map);

        provA = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == idxA);
        provB = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == idxB);
        return campaign;
    }

    public static void RunControlsErgonomicsOnly()
    {
        Console.WriteLine("=== ControlsErgonomicsTests (--controls-ergonomics-only) ===");
        InterfaceSkin.ResetCatalog();
        SettingsCatalog.ResetCatalog();
        SettingsCatalog.Reset("mouse_scheme");
        SettingsCatalog.Reset("rmb_everywhere");
        SettingsCatalog.Reset("edge_pan");
        SettingsCatalog.Reset("drag_threshold_px");
        SettingsCatalog.Reset("drag_threshold_ms");

        // 1. Settings defaults (Owner Nov 18 decisions)
        Check(string.Equals(SettingsCatalog.Of("mouse_scheme")?.DefaultValue, "aoh", StringComparison.OrdinalIgnoreCase),
            "default mouse scheme is AoH");
        Check(ControlSchemeRules.ActiveMouseScheme == MouseControlScheme.AoH,
            "ControlSchemeRules defaults to AoH");
        Check(ControlSchemeRules.RmbEverywhereEnabled, "rmb_everywhere defaults to true");
        Check(!ControlSchemeRules.EdgePanEnabled, "edge_pan defaults to false");
        Check(MathF.Abs(ControlSchemeRules.DragThresholdPx - 8f) < 0.01f, "drag_threshold_px defaults to 8");
        Check(Math.Abs(ControlSchemeRules.DragThresholdMs - 120.0) < 0.01, "drag_threshold_ms defaults to 120");

        // 2. MapGestureSession: AoH scheme vs Paradox scheme + 8 px / 120 ms disambiguation
        var g = new MapGestureSession();

        // AoH: LMB drag past 8 px pans
        g.BeginPress(MouseControlScheme.AoH, MapPointerButton.Left, 100f, 100f, 1000, false, false, false, true);
        Check(!g.BoxStarted, "AoH LMB press does not start army box");
        Check(!g.UpdateMotion(105f, 102f, 1040, 8f, 120.0, out _, out _), "AoH LMB < 8 px does not pan yet");
        Check(g.UpdateMotion(114f, 104f, 1060, 8f, 120.0, out var dx, out var dy) && dx > 0f,
            "AoH LMB >= 8 px triggers smooth pan immediately");
        Check(g.ReleaseLeft(120f, 105f, 8f) == LmbReleaseOutcome.PanEnded,
            "AoH LMB drag ends cleanly as PanEnded with zero click side-effect");

        // AoH: Ctrl+LMB draws region box; MMB draws army box
        g.BeginPress(MouseControlScheme.AoH, MapPointerButton.Left, 200f, 200f, 2000, true, false, false, true);
        Check(g.BoxStarted && g.BoxRegions, "AoH Ctrl+LMB starts multi-region selection box");
        g.UpdateMotion(260f, 250f, 2040, 8f, 120.0, out _, out _);
        Check(g.ReleaseLeft(260f, 250f, 8f) == LmbReleaseOutcome.BoxRegionSelect, "AoH Ctrl+LMB releases BoxRegionSelect");

        g.BeginPress(MouseControlScheme.AoH, MapPointerButton.Middle, 300f, 300f, 2100, false, false, false, false);
        Check(g.BoxStarted && !g.BoxRegions, "AoH MMB press starts army selection box");
        Check(!g.UpdateMotion(345f, 330f, 2130, 8f, 120.0, out _, out _), "AoH MMB drag never pans camera");
        Check(g.ReleaseMiddle(345f, 330f, 8f) == MmbReleaseOutcome.BoxArmySelect, "AoH MMB releases BoxArmySelect");

        // Paradox: LMB drag draws army box; MMB drag pans immediately
        g.BeginPress(MouseControlScheme.Paradox, MapPointerButton.Left, 100f, 100f, 3000, false, false, false, true);
        Check(g.BoxStarted && !g.BoxRegions, "Paradox LMB starts army selection box");
        Check(g.ReleaseLeft(150f, 140f, 8f) == LmbReleaseOutcome.BoxArmySelect, "Paradox LMB releases BoxArmySelect");
        g.BeginPress(MouseControlScheme.Paradox, MapPointerButton.Middle, 100f, 100f, 3100, false, false, false, false);
        Check(g.PanStarted && g.UpdateMotion(110f, 102f, 3110, 8f, 120.0, out _, out _), "Paradox MMB pans immediately");
        Check(g.ReleaseMiddle(115f, 104f, 8f) == MmbReleaseOutcome.PanEnded, "Paradox MMB ends PanEnded");

        // Strict RMB 8 px + 120 ms gate
        g.BeginPress(MouseControlScheme.AoH, MapPointerButton.Right, 400f, 400f, 4000, false, false, false, false);
        Check(!g.UpdateMotion(420f, 400f, 4060, 8f, 120.0, out _, out _),
            "RMB > 8 px before 120 ms does not start pan yet");
        Check(g.ReleaseRight(420f, 400f, 4070, 8f, 120.0, false) == RmbReleaseOutcome.IgnoredDrift,
            "fast swiped RMB (> 8 px before 120 ms) is suppressed (zero accidental march)");

        g.BeginPress(MouseControlScheme.AoH, MapPointerButton.Right, 400f, 400f, 5000, false, false, false, false);
        Check(!g.UpdateMotion(404f, 403f, 5200, 8f, 120.0, out _, out _),
            "RMB < 8 px even after 200 ms stays a click");
        Check(g.ReleaseRight(404f, 403f, 5220, 8f, 120.0, false) == RmbReleaseOutcome.CommandOrContextMenu,
            "clean RMB release <= 8 px triggers CommandOrContextMenu");

        g.BeginPress(MouseControlScheme.AoH, MapPointerButton.Right, 400f, 400f, 6000, false, true, false, false);
        Check(g.ReleaseRight(403f, 402f, 6040, 8f, 120.0, true) == RmbReleaseOutcome.AltSplitMove,
            "clean Alt+RMB click triggers AltSplitMove");

        g.BeginPress(MouseControlScheme.AoH, MapPointerButton.Right, 400f, 400f, 7000, false, false, false, false);
        Check(g.UpdateMotion(415f, 406f, 7140, 8f, 120.0, out var rDx, out var rDy) && rDx > 0f && rDy > 0f,
            "RMB > 8 px AND >= 120 ms transitions cleanly into pan");
        Check(g.ReleaseRight(430f, 412f, 7250, 8f, 120.0, false) == RmbReleaseOutcome.PanEnded,
            "dragged RMB finishes as PanEnded with zero accidental march");

        // Button drift guard for "Do Everywhere"
        Check(!ControlSchemeRules.ShouldTriggerEverywhere(false, true, true, 12f, 8f),
            "Everywhere RMB suppressed when pointer drifts > 8 px");
        Check(ControlSchemeRules.ShouldTriggerEverywhere(false, true, true, 4f, 8f),
            "Everywhere RMB allowed when drift <= 8 px and setting enabled");
        Check(!ControlSchemeRules.ShouldTriggerEverywhere(false, true, false, 2f, 8f),
            "Everywhere RMB respects rmb_everywhere=false");
        Check(ControlSchemeRules.ShouldTriggerEverywhere(true, false, false, 3f, 8f),
            "Everywhere Shift+LMB always works when drift <= 8 px regardless of rmb_everywhere");

        // 3. 3D globe cursor-anchored zoom solver accuracy & fine zoom & edge pan
        var px = 1120f; var py = 390f; float vw = 1920f, vh = 1080f, fov = 38f, r = 5f, maxP = 1.50f;
        double initPitch = 0.42, initYaw = -0.85; float oldDist = 11.2f, newDist = 7.4f;
        Check(ControlSchemeRules.TryGlobeRayHitLocal(px, py, vw, vh, fov, r, oldDist, out var oldHitLocal),
            "ray hits 3D globe under off-center cursor");
        var anchorWorld = ControlSchemeRules.OrbitLocalToWorld(oldHitLocal, initPitch, initYaw);
        Check(ControlSchemeRules.SolveGlobeZoomAnchor(px, py, vw, vh, fov, r, maxP,
                anchorWorld.X, anchorWorld.Y, anchorWorld.Z, initPitch, initYaw, newDist,
                out var nextPitch, out var nextYaw),
            "SolveGlobeZoomAnchor converges cleanly");
        Check(ControlSchemeRules.TryGlobeRayHitLocal(px, py, vw, vh, fov, r, newDist, out var verifyLocal),
            "ray still hits globe at new distance under same pixel");
        var verifyWorld = ControlSchemeRules.OrbitLocalToWorld(verifyLocal, nextPitch, nextYaw);
        var err = Math.Sqrt(Math.Pow(verifyWorld.X - anchorWorld.X, 2) +
                            Math.Pow(verifyWorld.Y - anchorWorld.Y, 2) +
                            Math.Pow(verifyWorld.Z - anchorWorld.Z, 2));
        Check(err < 1e-4, $"3D cursor-anchored zoom keeps exact ground under pixel (err={err:E2})");
        Check(MathF.Abs(ControlSchemeRules.ComputeZoomStep(1.0f, shiftPressed: true, 0.35f) - 0.35f) < 1e-5f,
            "Shift+Wheel reduces zoom step cleanly");
        var (epx, epy) = ControlSchemeRules.ComputeEdgePanDelta(2f, 3f, 1920f, 1080f);
        Check(epx > 0f && epy > 0f, "edge pan activates at top-left viewport edge");
        var (cpx, cpy) = ControlSchemeRules.ComputeEdgePanDelta(960f, 540f, 1920f, 1080f);
        Check(cpx == 0f && cpy == 0f, "edge pan stays zero in viewport center");

        // 4. Physical-layout Hotkeys (QWERTY == JCUKEN) & 0 collisions
        var collisions = HotkeyCatalog.FindCollisions();
        Check(collisions.Count == 0, $"canonical 91_controls_hotkeys.json has 0 collisions (found {collisions.Count})");
        Check(HotkeyCatalog.All.Count >= 45, $"HotkeyCatalog loaded {HotkeyCatalog.All.Count} actions");
        Check(HotkeyCatalog.MapCharToPhysicalKey('й') == "Q" && HotkeyCatalog.MapCharToPhysicalKey('й') == HotkeyCatalog.MapCharToPhysicalKey('q'),
            "Russian Й maps identically to physical Q");
        Check(HotkeyCatalog.MapCharToPhysicalKey('ф') == "A" && HotkeyCatalog.MapCharToPhysicalKey('ы') == "S" &&
              HotkeyCatalog.MapCharToPhysicalKey('в') == "D" && HotkeyCatalog.MapCharToPhysicalKey('а') == "F",
            "Russian Ф Ы В А map identically to physical A S D F");
        Check(HotkeyCatalog.MapCharToPhysicalKey('ю') == "Period", "Russian Ю maps to physical Period (idle army cycle)");
        Check(HotkeyCatalog.ResolveActive("й", false, false, false, false, false, false, false)?.Id == "screen_nation",
            "physical Й resolves Nation screen");
        Check(HotkeyCatalog.ResolveActive("п", false, false, false, true, false, false, false)?.Id == "army_merge",
            "physical П with selection resolves G army merge");
        Check(HotkeyCatalog.ResolveActive("A", false, false, false, false, false, false, true)?.Id == "qa_25" &&
              HotkeyCatalog.ResolveActive("A", false, false, false, false, false, false, false) is null,
            "QuickAmount A/S/D/F active ONLY while QuickAmount holds focus");
        Check(HotkeyCatalog.ResolveActive("K", true, false, false, true, false, false, false)?.Id == "search_navigator",
            "Ctrl+K stays strictly Search/Navigator even when armies selected");
        Check(HotkeyCatalog.ResolveActive("/", false, false, false, false, false, false, false)?.Id == "search_navigator_slash",
            "Slash opens quick search/navigator");
        Check(HotkeyCatalog.ResolveInScope("campaign", "E", false, false, false)?.Id == "screen_state_affairs" &&
              HotkeyCatalog.ResolveInScope("campaign", "R", false, false, false)?.Id == "screen_economy" &&
              HotkeyCatalog.ResolveInScope("campaign", "T", false, false, false)?.Id == "screen_trade" &&
              HotkeyCatalog.ResolveInScope("campaign", "Y", false, false, false)?.Id == "screen_production" &&
              HotkeyCatalog.ResolveInScope("campaign", "U", false, false, false)?.Id == "screen_research" &&
              HotkeyCatalog.ResolveInScope("campaign", "I", false, false, false)?.Id == "screen_military" &&
              HotkeyCatalog.ResolveInScope("campaign", "O", false, false, false)?.Id == "screen_diplomacy" &&
              HotkeyCatalog.ResolveInScope("campaign", "P", false, false, false)?.Id == "screen_wars" &&
              HotkeyCatalog.ResolveInScope("campaign", "L", false, false, false)?.Id == "screen_ledger" &&
              HotkeyCatalog.ResolveInScope("campaign", "J", false, false, false)?.Id == "toggle_outliner" &&
              HotkeyCatalog.ResolveInScope("campaign", "C", false, false, false)?.Id == "map_mode_economy" &&
              HotkeyCatalog.ResolveInScope("campaign", "V", false, false, false)?.Id == "map_mode_resources" &&
              HotkeyCatalog.ResolveInScope("campaign", "N", false, false, false)?.Id == "map_mode_wars" &&
              HotkeyCatalog.ResolveInScope("campaign", "M", false, false, false)?.Id == "toggle_map_layers",
            "canonical §5.3 screen & map keys match ShellHotkeys 100%");

        // 5. Army suite: Control groups (6..0), idle cycle (.), Gather idle, Alt+RMB split-move, Emergency brake (Ctrl+Alt+H)
        var campaign = CreateSyntheticCampaignWithMap(out var player, out var stateA, out var stateB, out var provA, out var provB);
        campaign.Armies.Clear();
        player.ManpowerReserve = 500_000;
        var a1 = new Army { Id = 101, NationId = player.Id, StateMapIndex = stateA.MapIndex, ProvinceId = provA, Divisions = 10f, Organisation = 1f, Strength = 1f, SupplyLevel = 1f, Name = "1st Corps" };
        var a2 = new Army { Id = 102, NationId = player.Id, StateMapIndex = stateA.MapIndex, ProvinceId = provA, Divisions = 2f, Organisation = 1f, Strength = 1f, SupplyLevel = 1f, Name = "2nd Corps" };
        var a3 = new Army { Id = 103, NationId = player.Id, StateMapIndex = stateA.MapIndex, ProvinceId = provB, Divisions = 1f, Organisation = 1f, Strength = 1f, SupplyLevel = 1f, Name = "3rd Corps", BattleId = 1 };
        campaign.Armies.AddRange(new[] { a1, a2, a3 });

        var groups = new ControlGroupTracker();
        groups.Bind(6, new[] { a1.Id, a2.Id });
        var resolved6 = groups.Resolve(campaign, player.Id, 6);
        Check(resolved6.Count == 2 && groups.BadgeForArmy(a1.Id) == "6", "Ctrl+6 binds and badges armies");
        Check(!groups.RecordActivateAndShouldFocus(6, 1000.0, Array.Empty<Army>(), resolved6),
            "first press selects group without jumping camera");
        Check(groups.RecordActivateAndShouldFocus(6, 1220.0, resolved6, resolved6),
            "double-tap/repeat within 450 ms signals camera centroid focus");
        Check(ArmySelectionRules.ComputeCentroid(resolved6, campaign.ProvinceMap, out _, out _),
            "ComputeCentroid calculates group geographic center");

        var nextIdle = ArmySelectionRules.NextIdleArmy(campaign, player.Id, -1);
        var nextIdle2 = ArmySelectionRules.NextIdleArmy(campaign, player.Id, nextIdle.Id);
        var nextIdleWrap = ArmySelectionRules.NextIdleArmy(campaign, player.Id, nextIdle2.Id);
        Check(nextIdle.Id == 101 && nextIdle2.Id == 102 && nextIdleWrap.Id == 101,
            "Period (.) cycles idle armies and skips in-battle a3");

        var fpBeforeUi = CampaignFingerprint.Of(campaign);
        var gatherable = ArmySelectionRules.GatherableIdleArmies(campaign, player, provB);
        Check(gatherable.Count == 2, "GatherableIdleArmies finds both idle player divisions able to march to target");
        Check(CampaignFingerprint.Of(campaign) == fpBeforeUi,
            "selection/group/gather queries never mutate CampaignFingerprint");

        // Alt+RMB split-move: send 3 of 10 divisions from a1 in provA toward provB
        var splitMove = ArmySelectionRules.ExecuteSplitMove(campaign, player.Id, new[] { a1 }, provB, wantedDivisions: 3);
        Check(string.IsNullOrEmpty(splitMove.Refusal) && splitMove.MovedDivisions == 3 && splitMove.MovingArmyIds.Count == 1,
            "Alt+RMB split-move cleanly detaches 3 divisions and orders march");
        var detached = campaign.Armies.First(x => x.Id == splitMove.MovingArmyIds[0]);
        Check(MathF.Abs(a1.Divisions - 7f) < 0.01f && MathF.Abs(detached.Divisions - 3f) < 0.01f,
            "parent kept 7 divisions and detachment got 3 divisions");
        Check(detached.OrderProvinceId == provB || detached.Route.Count > 0,
            "detachment received MoveArmyCommand destination to target province");

        // Ctrl+Alt+H Emergency Brake halts moving orders without breaking a3 out of active battle
        a2.Route.Add(provB);
        a2.OrderProvinceId = provB;
        a2.DirectOrder = true;
        var brake = new HaltAllPlayerMovementsCommand { IssuerId = player.Id };
        Check(NetworkCommandCodec.TryEncode(brake, out var brakeType, out var brakeJson) &&
              NetworkCommandCodec.TryDecode(brakeType, brakeJson, out _),
            "HaltAllPlayerMovementsCommand round-trips through NetworkCommandCodec");
        campaign.Commands.Enqueue(brake);
        campaign.Commands.ApplyAll(campaign);
        Check(brake.HaltedCount >= 2 && a2.OrderProvinceId < 0 && a2.Route.Count == 0 && detached.OrderProvinceId < 0 && a3.InBattle,
            "Ctrl+Alt+H halts all moving player armies immediately while preserving active combat safety");
    }

    public static void RunQuickAmountOnly()
    {
        Console.WriteLine("=== QuickAmountTests (--quick-amount-only) ===");
        var campaign = CreateSyntheticCampaignWithMap(out var player, out var stateA, out var stateB, out var provA, out var provB);
        player.Treasury = 500f;
        player.ManpowerReserve = 400_000;

        // 1. Quarter fractions & direct parsing (%, млрд/B, млн/M, int, clamp)
        Check(QuickAmountRules.QuarterInteger(10, 1) == 3 && QuickAmountRules.QuarterInteger(10, 2) == 5 &&
              QuickAmountRules.QuarterInteger(10, 3) == 8 && QuickAmountRules.QuarterInteger(10, 4) == 10,
            "QuarterInteger rounds 25/50/75/100% crisply");
        Check(QuickAmountRules.TryParseDirectValue("50%", 1, 200, 1, true, out var vPct) && MathF.Abs(vPct - 100f) < 0.01f,
            "TryParseDirectValue parses '50%' relative to max");
        Check(QuickAmountRules.TryParseDirectValue("1,5 млрд", 0, 50, 0.01f, false, out var vRuB) && MathF.Abs(vRuB - 1.5f) < 0.01f,
            "TryParseDirectValue parses Russian '1,5 млрд'");
        Check(QuickAmountRules.TryParseDirectValue("250M", 0, 50, 0.01f, false, out var vEnM) && MathF.Abs(vEnM - 0.25f) < 0.01f,
            "TryParseDirectValue parses English '250M'");
        Check(!QuickAmountRules.TryParseDirectValue("not-a-number", 0, 10, 1, true, out _),
            "TryParseDirectValue rejects invalid text");

        // 2. Recruitment quote caps & Everywhere dry-run
        var tpl = UnitTypeCatalog.All.First();
        var quote = QuickAmountRules.EvaluateRecruitmentCap(campaign, player, stateA, tpl.Id);
        Check(quote.MaxDivisions >= 1 && quote.MaxDivisions <= quote.MaxByBatch && quote.SingleCost > 0f,
            "EvaluateRecruitmentCap quotes valid max divisions");

        var fpRecruitBefore = CampaignFingerprint.Of(campaign);
        var planRecruit = QuickAmountRules.PlanEverywhereRecruitment(campaign, player, new[] { stateA, stateB },
            tpl.Id, MobilisationMode.Standard, quarterOrCount: 1, isQuarter: false);
        Check(CampaignFingerprint.Of(campaign) == fpRecruitBefore,
            "PlanEverywhereRecruitment is read-only (CampaignFingerprint untouched)");
        Check(planRecruit.Items.Count >= 1 && planRecruit.TotalDivisions == planRecruit.Items.Sum(x => x.Divisions),
            "PlanEverywhereRecruitment quotes exact eligible region breakdown");

        // 3. Timeless Ctrl+Z BatchActionUndo on Construction Everywhere -> exact fingerprint restore
        var fpBeforeBuild = CampaignFingerprint.Of(campaign);
        var buildPlan = BatchActionUndo.ExecuteEverywhereConstruction(campaign, player, new[] { stateA, stateB },
            BuildingKind.CivilianIndustry, multiplierPerRegion: 1);
        Check(buildPlan.Items.Count >= 1 && BatchActionUndo.CanUndo(campaign, player.Id),
            "Everywhere Construction queues items and arms timeless BatchActionUndo");

        // Another nation executing a command does NOT expire the player's timeless Undo
        var otherNation = campaign.Nations.First(n => n.Id != player.Id);
        var firstBudgetLine = BudgetCore.PlayerLines.First();
        campaign.Commands.Enqueue(new SetPolicyRateCommand
        {
            IssuerId = otherNation.Id,
            LineId = firstBudgetLine.Id,
            Value = BudgetCore.RateOf(otherNation, firstBudgetLine)
        });
        campaign.Commands.ApplyAll(campaign);
        Check(BatchActionUndo.CanUndo(campaign, player.Id),
            "BatchActionUndo stays valid even after AI commands (timeless)");

        var undoCmd = BatchActionUndo.CreateUndoCommand(campaign, player.Id);
        ICampaignCommand decodedBuildUndo = null;
        Check(NetworkCommandCodec.TryEncode(undoCmd, out var undoType, out var undoJson) &&
              NetworkCommandCodec.TryDecode(undoType, undoJson, out decodedBuildUndo) &&
              decodedBuildUndo is UndoBatchActionCommand,
            "UndoBatchActionCommand round-trips through NetworkCommandCodec");
        campaign.Commands.Enqueue(decodedBuildUndo);
        campaign.Commands.ApplyAll(campaign);
        Check(((UndoBatchActionCommand)decodedBuildUndo).UndoneCount == buildPlan.TotalBuildings,
            "Decoded Ctrl+Z construction command undoes all unstarted queued construction items");
        Check(!BatchActionUndo.CanUndo(campaign, player.Id), "PendingBatchUndo clears after decoded undo");
        Check(CampaignFingerprint.Of(campaign) == fpBeforeBuild,
            "Decoded Ctrl+Z Construction Everywhere restores exact CampaignFingerprint");

        // 4. Timeless Ctrl+Z clears immediately on the owning player's NEXT command
        var buildPlan2 = BatchActionUndo.ExecuteEverywhereConstruction(campaign, player, new[] { stateA, stateB },
            BuildingKind.CivilianIndustry, multiplierPerRegion: 1);
        Check(buildPlan2.TotalBuildings >= 1 && BatchActionUndo.CanUndo(campaign, player.Id),
            "second Everywhere action arms BatchActionUndo");
        campaign.Commands.Enqueue(new QueueConstructionCommand
        {
            IssuerId = player.Id,
            StateMapIndex = stateA.MapIndex,
            Building = BuildingKind.Infrastructure
        });
        campaign.Commands.ApplyAll(campaign);
        Check(!BatchActionUndo.CanUndo(campaign, player.Id),
            "player's next non-undo command cleanly expires PendingBatchUndo");

        // 5. Timeless Ctrl+Z on Recruitment Everywhere restores Treasury, Manpower, Stockpiles & Fingerprint via wire roundtrip
        var fpBeforeRecruit = CampaignFingerprint.Of(campaign);
        var execRecruit = BatchActionUndo.ExecuteEverywhereRecruitment(campaign, player, new[] { stateA, stateB },
            tpl.Id, MobilisationMode.Standard, quarterOrCount: 1, isQuarter: false);
        Check(execRecruit.TotalDivisions >= 1 && BatchActionUndo.CanUndo(campaign, player.Id),
            "Everywhere Recruitment arms BatchActionUndo");
        var recruitUndoCmd = BatchActionUndo.CreateUndoCommand(campaign, player.Id);
        ICampaignCommand decodedRecruitUndo = null;
        Check(NetworkCommandCodec.TryEncode(recruitUndoCmd, out var rUndoType, out var rUndoJson) &&
              NetworkCommandCodec.TryDecode(rUndoType, rUndoJson, out decodedRecruitUndo) &&
              decodedRecruitUndo is UndoBatchActionCommand,
            "Recruitment UndoBatchActionCommand round-trips through NetworkCommandCodec");
        campaign.Commands.Enqueue(decodedRecruitUndo);
        campaign.Commands.ApplyAll(campaign);
        var executedRecruitUndo = (UndoBatchActionCommand)decodedRecruitUndo;
        Check(executedRecruitUndo.UndoneCount == execRecruit.Items.Count &&
              executedRecruitUndo.RefundedCash > 0f && executedRecruitUndo.RefundedManpower > 0,
            "Decoded Ctrl+Z Recruitment Everywhere refunds 100% unstarted cash + manpower");
        Check(CampaignFingerprint.Of(campaign) == fpBeforeRecruit,
            "Decoded Ctrl+Z Recruitment Everywhere restores exact CampaignFingerprint");

        // 6. Debt preview & Budget "Balance to zero" solver + decoded Ctrl+Z restore
        var debtPreview = QuickAmountRules.PreviewDebtAction(campaign, player, "borrow", 1.5f);
        var sheet = SovereignBalanceSheet.Read(campaign, player);
        if (sheet.NewCredit >= 0.01f)
            Check(debtPreview.Valid && debtPreview.CashAfter > sheet.Cash && debtPreview.PrincipalAfter > sheet.Principal,
                "PreviewDebtAction models cash and debt accurately");

        var firstTax = BudgetCore.PlayerLines.First(l => l.Side == BudgetSide.Revenue && l.Charge == BudgetBasis.Output);
        BudgetCore.SetRate(player, firstTax, firstTax.Minimum);
        var fpBeforeZero = CampaignFingerprint.Of(campaign);
        var zeroPlanPreview = QuickAmountRules.PlanBalanceToZero(campaign, player, adjustTaxes: true);
        Check(CampaignFingerprint.Of(campaign) == fpBeforeZero, "PlanBalanceToZero is read-only before confirm");
        var zeroExec = BatchActionUndo.ExecuteZeroBudget(campaign, player, adjustTaxes: true);
        if (zeroExec.Changes.Count > 0)
        {
            Check(MathF.Abs(zeroExec.BalanceAfter) <= MathF.Abs(zeroExec.BalanceBefore) + 0.001f,
                $"Balance-to-zero reduces deficit/surplus ({zeroExec.BalanceBefore:0.###} -> {zeroExec.BalanceAfter:0.###})");
            var zeroUndoCmd = BatchActionUndo.CreateUndoCommand(campaign, player.Id);
            ICampaignCommand decodedZeroUndo = null;
            Check(NetworkCommandCodec.TryEncode(zeroUndoCmd, out var zType, out var zJson) &&
                  NetworkCommandCodec.TryDecode(zType, zJson, out decodedZeroUndo) &&
                  decodedZeroUndo is UndoBatchActionCommand,
                "Zero-budget UndoBatchActionCommand round-trips through NetworkCommandCodec");
            campaign.Commands.Enqueue(decodedZeroUndo);
            campaign.Commands.ApplyAll(campaign);
            Check(CampaignFingerprint.Of(campaign) == fpBeforeZero, "Decoded Ctrl+Z Zero-budget restores exact CampaignFingerprint");
        }
    }
}
