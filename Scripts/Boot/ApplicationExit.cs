using System;
using System.Threading.Tasks;
using Godot;

namespace DealOrForce;

/// <summary>Shared desktop/window exit. Stop audio while the tree and audio server
/// still run, then give the server a bounded drain before SceneTree teardown.</summary>
public partial class ApplicationExit : Node
{
    private static ApplicationExit _instance;
    private readonly ApplicationExitState _state = new();
    private SceneTree _tree;
    private Window _window;
    private bool _previousAutoAccept;

    public static ApplicationExit Ensure(Node context)
    {
        var tree = context.GetTree();
        if (IsInstanceValid(_instance) && ReferenceEquals(_instance._tree, tree)) return _instance;
        var exit = new ApplicationExit { Name = "ApplicationExit", ProcessMode = ProcessModeEnum.Always };
        _instance = exit;
        exit._tree = tree;
        exit._window = tree.Root;
        exit._previousAutoAccept = tree.AutoAcceptQuit;
        tree.AutoAcceptQuit = false;
        exit._window.CloseRequested += exit.WindowCloseRequested;
        tree.Root.CallDeferred(Node.MethodName.AddChild, exit);
        return exit;
    }

    public static void Request(Node context, int code = 0)
    {
        var exit = Ensure(context);
        if (exit._state.Request(code)) _ = exit.Finish();
    }

    internal static bool Requested => IsInstanceValid(_instance) && _instance._state.Requested;
    internal static int ExitCode => IsInstanceValid(_instance) ? _instance._state.ExitCode : 0;

    private void WindowCloseRequested() => Request(_window);

    // Remains active on pause and after returning to the title screen. Completed
    // optional warmups must be received even when their flag was never displayed.
    public override void _Process(double delta) => DealOrForceGame.PollFlagWarmups();

    private async Task Finish()
    {
        try
        {
            // No automatic save, network mutation or second simulation step on exit.
            // A paused tree still advances this always-processing shutdown timer.
            _tree.Paused = true;
            MusicDirector.PrepareApplicationExit();
            StopAudio(_tree.Root);
            var deadline = Time.GetTicksMsec() + 2_000;
            while (DealOrForceGame.PendingFlagWarmups > 0 && Time.GetTicksMsec() < deadline)
            {
                DealOrForceGame.PollFlagWarmups();
                if (DealOrForceGame.PendingFlagWarmups > 0)
                    await ToSignal(_tree.CreateTimer(.02, true, false, true), SceneTreeTimer.SignalName.Timeout);
            }
            if (DealOrForceGame.PendingFlagWarmups > 0)
                GD.PushWarning("[dof] optional flag warmups still pending at exit: " + DealOrForceGame.PendingFlagWarmups);
            await ToSignal(_tree.CreateTimer(.1, true, false, true), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        }
        catch (Exception error)
        {
            GD.PushError("[dof] application exit cleanup failed: " + error);
            _state.Request(1);
        }
        GD.Print("[dof] application exit ready code=" + _state.ExitCode);
        if (IsInstanceValid(_tree)) _tree.Quit(_state.ExitCode);
    }

    private static void StopAudio(Node root)
    {
        var flat = root.FindChildren("*", "AudioStreamPlayer", true, false);
        foreach (var node in flat)
            if (node is AudioStreamPlayer player) { player.Stop(); player.Stream = null; }
        var spatial = root.FindChildren("*", "AudioStreamPlayer3D", true, false);
        foreach (var node in spatial)
            if (node is AudioStreamPlayer3D player) { player.Stop(); player.Stream = null; }
        var planar = root.FindChildren("*", "AudioStreamPlayer2D", true, false);
        foreach (var node in planar)
            if (node is AudioStreamPlayer2D player) { player.Stop(); player.Stream = null; }
    }

    public override void _ExitTree()
    {
        if (IsInstanceValid(_window)) _window.CloseRequested -= WindowCloseRequested;
        if (IsInstanceValid(_tree)) _tree.AutoAcceptQuit = _previousAutoAccept;
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
