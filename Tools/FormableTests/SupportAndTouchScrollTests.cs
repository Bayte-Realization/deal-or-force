using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class SupportAndTouchScrollTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL support-touch-scroll: " + label);
        Console.WriteLine("PASS support-touch-scroll: " + label);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    [TestSuite("--support-touch-scroll-only", Order = 2660, IncludeInDefaultRun = true,
        Description = "Long-Range Forces panel vertical dock height, map-tap open, immediate paused raise, and Android TouchScrollController.")]
    public static void Run()
    {
        var root = RepoRoot();
        RunSupportPanelLayoutTests(root);
        RunSupportScreenAndNewShellContractTests(root);
        RunTouchScrollControllerContractTests(root);
        RunPausedRaiseSupportCommandTests();
    }

    private static void RunSupportPanelLayoutTests(string root)
    {
        var jsonPath = Path.Combine(root, "game", "gui", "10_campaign_shell.json");
        var text = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var support = doc.RootElement.GetProperty("layout").GetProperty("hud.support_panel");
        var anchors = support.GetProperty("anchors").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var offsets = support.GetProperty("offsets").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Check(anchors.Length == 4 && offsets.Length == 4, "hud.support_panel has 4 anchors and 4 offsets");
        Check(anchors[1] == 0f && anchors[3] == 1f, "hud.support_panel stretches vertically from top to bottom anchor");

        // Galaxy S22 logical viewport (1560x720) and desktop (1920x1080) must both give generous height.
        var s22Height = (anchors[3] * 720f + offsets[3]) - (anchors[1] * 720f + offsets[1]);
        var desktopHeight = (anchors[3] * 1080f + offsets[3]) - (anchors[1] * 1080f + offsets[1]);
        Check(s22Height >= 500f, $"hud.support_panel has generous vertical height at 720p ({s22Height:0}px >= 500px)");
        Check(desktopHeight >= 850f, $"hud.support_panel has generous vertical height at 1080p ({desktopHeight:0}px >= 850px)");

        // NewShell docking bounds at S22 1560x720 (touch) must leave >= 500px vertical room for SupportForcesPanel.
        var s22Profile = ShellLayout.ForViewport(1560f, 720f, atWar: false, touchMode: true);
        var s22Dock = ShellDockRules.ComputeLeftAndBottomDock(s22Profile, anyLeftPanelVisible: true, fullWorkspaceVisible: false);
        var topY = MathF.Max(s22Dock.LeftPanel.Y, ShellLayout.XpStripRect(s22Profile).Bottom + 6f);
        var bottomY = MathF.Min(s22Profile.FullWorkspace.Bottom, s22Dock.BottomStrip.Y - 8f);
        Check(bottomY - topY >= 500f, $"NewShell SupportForcesPanel dock height at S22 1560x720 is generous ({bottomY - topY:0}px >= 500px)");
        Check(topY >= ShellLayout.XpStripRect(s22Profile).Bottom, "NewShell SupportForcesPanel clears XP strip on top");
        Check(bottomY <= s22Dock.BottomStrip.Y, "NewShell SupportForcesPanel clears BottomStrip on bottom");
    }

    private static void RunSupportScreenAndNewShellContractTests(string root)
    {
        var supportSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "SupportScreenUI.cs"));
        Check(supportSrc.Contains("rows.MoveChild(selection, supportFrame.GetIndex());", StringComparison.Ordinal),
            "SupportScreenUI moves selection bar above supportFrame (not _supportScroll index 0)");
        Check(!supportSrc.Contains("rows.MoveChild(selection, _supportScroll.GetIndex());", StringComparison.Ordinal),
            "SupportScreenUI no longer has _supportScroll.GetIndex() child-order bug");
        Check(supportSrc.Contains("Name = \"SupportRosterFrame\"", StringComparison.Ordinal) &&
              supportSrc.Contains("CustomMinimumSize = new Vector2(0, 180)", StringComparison.Ordinal),
            "SupportRosterFrame enforces generous minimum vertical height");
        Check(supportSrc.Contains("ActionMode = BaseButton.ActionModeEnum.Release", StringComparison.Ordinal),
            "SupportRow uses Release action mode so touch-drag scrolling does not fire on touch-down");
        Check(supportSrc.Contains("OpenSupportScreen();", StringComparison.Ordinal) &&
              supportSrc.Contains("var openPanel = _supportSelected.Count > 0", StringComparison.Ordinal),
            "OnSupportPicked immediately opens Long-Range Forces panel when tapping a formation on the map");
        Check(supportSrc.Contains("_simRunner.RunWrite(world =>", StringComparison.Ordinal) &&
              supportSrc.Contains("refreshSnapshot: true", StringComparison.Ordinal),
            "RaiseSupportFormation and CancelSupportOrder execute through _simRunner.RunWrite with refreshSnapshot: true");
        Check(supportSrc.Contains("IsTouchPrimary", StringComparison.Ordinal),
            "SupportScreenUI adapts map control hints for touch devices instead of PC Shift/right-click hints");
        Check(supportSrc.Contains("TouchScrollController.EnsureInstalled(this);", StringComparison.Ordinal),
            "SupportScreenUI ensures TouchScrollController is installed");

        var newShellSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "Shell", "NewShellUI.cs"));
        Check(newShellSrc.Contains("FitNewShellSupportPanel()", StringComparison.Ordinal),
            "NewShellUI docks SupportForcesPanel to full vertical height");
        Check(newShellSrc.Contains("_formationBar", StringComparison.Ordinal),
            "NewShellUI docks and raises _formationBar above _newShellRoot");
        Check(newShellSrc.Contains("TouchScrollController.EnsureInstalled(this);", StringComparison.Ordinal),
            "NewShellUI installs TouchScrollController on shell startup");
    }

    private static void RunTouchScrollControllerContractTests(string root)
    {
        var csPath = Path.Combine(root, "Scripts", "ui", "TouchScrollController.cs");
        var uidPath = csPath + ".uid";
        Check(File.Exists(csPath), "Scripts/ui/TouchScrollController.cs exists");
        Check(File.Exists(uidPath), "Scripts/ui/TouchScrollController.cs.uid exists");
        var uid = File.ReadAllText(uidPath).Trim();
        Check(uid.StartsWith("uid://", StringComparison.Ordinal) && uid.Length >= 10,
            "TouchScrollController.cs.uid has a valid Godot uid");

        var src = File.ReadAllText(csPath);
        Check(!src.Contains("new Color(", StringComparison.Ordinal),
            "TouchScrollController contains zero raw new Color( literals");
        Check(src.Contains("InputEventScreenTouch", StringComparison.Ordinal) &&
              src.Contains("InputEventScreenDrag", StringComparison.Ordinal),
            "TouchScrollController handles InputEventScreenTouch and InputEventScreenDrag");
        Check(src.Contains("mouse.Device == -1", StringComparison.Ordinal),
            "TouchScrollController suppresses synthetic touch-emulated mouse events during/after scroll drag");
        Check(src.Contains("CancelPressedButton", StringComparison.Ordinal),
            "TouchScrollController cancels pressed BaseButton when drag threshold is exceeded");
        Check(src.Contains("ScrollBar or Slider or SpinBox or LineEdit or TextEdit", StringComparison.Ordinal),
            "TouchScrollController leaves ScrollBar/Slider/SpinBox/LineEdit/TextEdit direct manipulation intact");
        Check(src.Contains("ScrollVertical", StringComparison.Ordinal) &&
              src.Contains("ScrollHorizontal", StringComparison.Ordinal),
            "TouchScrollController supports both vertical and horizontal ScrollContainers");
        Check(src.Contains("SetInputAsHandled()", StringComparison.Ordinal),
            "TouchScrollController consumes handled scroll drags so globe camera underneath never moves");
    }

    private static void RunPausedRaiseSupportCommandTests()
    {
        var world = new Campaign(20260711) { IsPaused = true };
        world.AddNation(new Nation
        {
            Tag = "AAA",
            MapIndex = 501,
            Population = 50_000_000,
            ManpowerReserve = 500_000,
            Treasury = 500f
        });
        world.SetPlayer(0);
        world.States.Add(new ScenarioState { MapIndex = 1001, Id = "base1001", NationMapIndex = 501, Population = 5_000_000 });
        ushort[] regions = { 1001, 1001, 1001, 1001 };
        ushort[] nations = { 501, 501, 501, 501 };
        world.AttachProvinceTopology(ProvinceTopology.Build(regions, nations, 2, 2, _ => false));
        world.SupportFormations.Clear();
        world.SupportOrders.Clear();

        var player = world.Player!;
        var state = world.States[0];
        var type = SupportTypeCatalog.All.First(t => SupportCore.Enabled(world, t));
        var cost = SupportCore.CostOf(player, type);
        player.Treasury = MathF.Max(player.Treasury, cost + 10f);
        var beforeTreasury = player.Treasury;
        var beforeOrders = world.SupportOrders.Count;

        var cmd = new RaiseSupportCommand
        {
            IssuerId = player.Id,
            StateMapIndex = state.MapIndex,
            TypeId = type.Id
        };
        world.Commands.Enqueue(cmd);
        world.Commands.ApplyAll(world);

        Check(string.IsNullOrEmpty(cmd.Refusal), $"RaiseSupportCommand succeeds while paused (refusal='{cmd.Refusal}')");
        Check(world.SupportOrders.Count == beforeOrders + 1, "RaiseSupportCommand immediately enqueues training order while paused");
        Check(player.Treasury < beforeTreasury, $"RaiseSupportCommand immediately deducts treasury while paused ({beforeTreasury:0.0} -> {player.Treasury:0.0})");
    }
}
