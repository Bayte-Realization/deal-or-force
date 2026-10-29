using System;
using System.IO;
using DealOrForce;

static class ApplicationExitTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("Application exit: " + label);
        Console.WriteLine("PASS application exit: " + label);
    }

    public static void Run()
    {
        var state = new ApplicationExitState();
        Check(!state.Requested && state.ExitCode == 0, "initial state does not request termination");
        Check(state.Request(0) && state.Requested, "normal request starts shutdown once");
        Check(!state.Request(0) && state.ExitCode == 0, "repeated normal click does not start another shutdown");
        Check(!state.Request(7) && state.ExitCode == 7, "a later error cannot be lost behind a normal close");
        Check(!state.Request(0) && state.ExitCode == 7, "normal repeat does not clear an error");
        Check(!state.Request(9) && state.ExitCode == 7, "first failure reason is retained");
        foreach (var first in new[] { int.MinValue, -1, 1, 7, 255, int.MaxValue })
        {
            var error = new ApplicationExitState();
            Check(error.Request(first) && !error.Request(0) && error.ExitCode == first,
                "initial nonzero exit code is retained: " + first);
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scripts/MainMenuScene.cs")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new Exception("Game sources not found above test output");
        foreach (var file in new[] { "Scripts/MainMenuScene.cs", "Scripts/ui/MainMenuUI.cs", "Scripts/ui/PauseMenuUI.cs",
            "Scripts/ui/LoadingScreenUI.cs", "Scripts/ui/DeveloperConsoleExtras.cs" })
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            Check(text.Contains("ApplicationExit.Request(this)") && !text.Contains("GetTree().Quit("),
                "player-facing quit route uses production cleanup: " + file);
        }
        foreach (var file in new[] { "Scripts/MainMenuScene.cs", "Scripts/DealOrForceGame.cs" })
            Check(File.ReadAllText(Path.Combine(root, file)).Contains("ApplicationExit.Ensure(this)"),
                "window close is wired in either entry scene: " + file);
    }
}
