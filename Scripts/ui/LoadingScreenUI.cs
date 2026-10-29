using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private bool _isLoading;
    private bool _startupComplete;
    private bool _loadingCanReturn;
    private LoadingScreen _loadingScreen;
    private CanvasLayer _loadingCanvas;
    private string _loadingHeading;
    private string _loadingStage;
    private readonly Stopwatch _loadingClock = new();
    private double _loadingStageStart;
    private Control _loadingPreviousFocus;

    private async Task<bool> RunLoadingOperation(string heading, Func<Task> operation, bool canReturn = false)
    {
        if (_isLoading) return false;
        _isLoading = true;
        _loadingCanReturn = canReturn;
        _loadingHeading = heading;
        _loadingStage = "";
        _loadingStageStart = 0;
        _loadingClock.Restart();
        _loadingPreviousFocus = GetViewport().GuiGetFocusOwner();
        _loadingCanvas = new CanvasLayer { Name = "LoadingCanvas", Layer = 100 };
        AddChild(_loadingCanvas);
        _loadingScreen = new LoadingScreen { Name = "LoadingScreen" };
        _loadingCanvas.AddChild(_loadingScreen);
        _loadingScreen.GrabFocus();
        if (_globe is not null) _globe.LoadingInputBlocked = true;
        try
        {
            await LoadingPhase("Opening the archive", 0);
            await operation();
            EnsureLoadingAlive();
            LogLoadingStage();
            _loadingScreen.SetStage(_loadingHeading, "Ready", 100, "Loading advances as each stage is completed.");
            DiagnosticLogs.Write("performance", $"LOADING_COMPLETE operation={heading} elapsed_ms={_loadingClock.Elapsed.TotalMilliseconds:F1}");
            // Present the completed map once before uncovering it. No timer/minimum duration.
            await PresentLoadingFrame();
            CloseLoadingScreen();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            if (!IsInsideTree()) return false;
            DiagnosticLogs.Write("errors", $"LOADING_FAILED operation={heading} stage={_loadingStage} elapsed_ms={_loadingClock.Elapsed.TotalMilliseconds:F1} error={exception}");
            if (_loadingCanReturn) GD.Print($"[dof] load rejected ({_loadingStage}): {exception.Message}");
            else GD.PushError($"[dof] loading failed ({_loadingStage}): {exception.Message}");
            _loadingScreen.ShowFailure(exception.Message, _loadingCanReturn, () =>
            {
                if (_loadingCanReturn) CloseLoadingScreen();
                else ApplicationExit.Request(this);
            });
            return false;
        }
    }

    private async Task LoadingPhase(string stage, double completed, string detail = "Loading advances as each stage is completed.")
    {
        EnsureLoadingAlive();
        if (_loadingStage != stage)
        {
            LogLoadingStage();
            _loadingStage = stage;
            _loadingStageStart = _loadingClock.Elapsed.TotalMilliseconds;
        }
        _loadingScreen.SetStage(_loadingHeading, stage, completed, detail);
        await PresentLoadingFrame();
    }

    private void LogLoadingStage()
    {
        if (_loadingStage.Length > 0)
            DiagnosticLogs.Write("performance", $"LOADING_STAGE stage={_loadingStage} elapsed_ms={_loadingClock.Elapsed.TotalMilliseconds - _loadingStageStart:F1}");
    }

    private async Task PresentLoadingFrame()
    {
        EnsureLoadingAlive();
        var tree = GetTree();
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (DisplayServer.GetName() != "headless")
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        // Work starts on the next main-thread frame, after the updated cover was rendered.
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        EnsureLoadingAlive();
    }

    private void EnsureLoadingAlive()
    {
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) throw new OperationCanceledException();
    }

    private async Task WaitForMapPresentation()
    {
        _globe.LoadingInputBlocked = true;
        // A map that stops answering used to be an endless "Ready" on the loading screen: the
        // loop waited for a ready flag that would never be set and had no idea anything was
        // wrong. Two minutes with no progress at all is not a slow map, it is a map that is
        // not coming - and the player is owed the stage it stopped at rather than a still frame.
        var lastStage = "";
        var lastProgress = -1d;
        var lastChange = Time.GetTicksMsec();
        while (!_globe.IsMapPresentationReady)
        {
            if (!string.IsNullOrEmpty(_globe.MapLoadingError)) throw new InvalidOperationException(_globe.MapLoadingError);
            var stage = _globe.MapLoadingStage;
            var progress = _globe.MapLoadingProgress;
            var now = Time.GetTicksMsec();
            if (!string.Equals(stage, lastStage, StringComparison.Ordinal) || Math.Abs(progress - lastProgress) > 0.001f)
            {
                lastStage = stage;
                lastProgress = progress;
                lastChange = now;
            }
            else if (now - lastChange > 120000)
                throw new InvalidOperationException(
                    $"The map stopped answering while {stage} was in progress. " +
                    "Close any other copy of the game that is running, then try again.");
            await LoadingPhase(stage, progress);
        }
    }

    private void CloseLoadingScreen()
    {
        _loadingCanvas.Hide();
        _loadingCanvas.QueueFree();
        if (_globe is not null) _globe.LoadingInputBlocked = false;
        _isLoading = false;
        _dayAccumulator = 0;
        if (GodotObject.IsInstanceValid(_loadingPreviousFocus) && _loadingPreviousFocus.IsVisibleInTree())
            _loadingPreviousFocus.GrabFocus();
    }

    private Task<bool> RebuildCampaignWithLoading() => RunLoadingOperation("Preparing a new campaign", async () =>
    {
        await LoadingPhase("Creating countries and regions", 20);
        StartCampaign();
        await WaitForMapPresentation();
        // The second campaign of a session gets the preferences the first one had. Without
        // this the map came back as a globe however the player had left it, because the
        // settings were pushed at a globe that was still being assembled.
        ApplyAllSettings();
    });
}
