using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Opt-in verification of the shipped executable, not an editor-only script.
/// Official release templates disable external script/scene overrides. Keep the
/// probe built in so CI can test the actual pack, assembly and loose data together.
/// It never loads an existing save or writes a campaign; normal launches do nothing.
/// </summary>
public partial class ExportVerification : Node
{
    private int _failures;
    private string _captureDirectory = "";

    public static void AttachIfRequested(MainMenuScene menu)
    {
        var args = OS.GetCmdlineUserArgs();
        if (!args.Contains("--verify-build")) return;
        var tree = menu.GetTree();
        if (tree.Root.GetNodeOrNull("ExportVerification") is not null) return;
        var captureAt = Array.IndexOf(args, "--verify-build-output");
        var probe = new ExportVerification { Name = "ExportVerification", ProcessMode = ProcessModeEnum.Always };
        if (captureAt >= 0 && captureAt + 1 < args.Length)
            probe._captureDirectory = args[captureAt + 1];
        tree.Root.CallDeferred(Node.MethodName.AddChild, probe);
    }

    public override async void _Ready()
    {
        try
        {
            AudioServer.SetBusMute(0, true);
            var tree = GetTree();
            await Wait(DisplayServer.GetName() == "headless" ? .25 : 6);
            Check(tree.CurrentScene is MainMenuScene && tree.CurrentScene.FindChildren("*", "Button", true, false).Count > 3,
                "managed main menu and its controls load");
            foreach (var property in ProjectSettings.Singleton.GetPropertyList())
            {
                var key = property["name"].AsString();
                if (key.StartsWith("autoload/", StringComparison.Ordinal))
                    Check(!ProjectSettings.GetSetting(key).AsString().TrimStart('*').StartsWith("res://addons/", StringComparison.Ordinal),
                        "export autoload is not editor tooling: " + key);
            }
            await Capture("menu");
            Check(tree.ChangeSceneToFile("res://Scenes/Main.tscn") == Error.Ok, "campaign scene opens from the pack");
            var deadline = Time.GetTicksMsec() + 120_000;
            DealOrForceGame game = null;
            while (Time.GetTicksMsec() < deadline)
            {
                await Wait(.25);
                game = tree.CurrentScene as DealOrForceGame;
                var loading = game?.FindChild("LoadingScreen", true, false) as Control;
                if (game?.FindChild("MapModePolitical", true, false) is not null && (loading is null || !loading.Visible)) break;
                game = null;
            }
            Check(game is not null, "world, loose data and managed campaign finish loading");
            if (game is not null)
            {
                game.Call("BeginSession");
                game.Call("OpenScreen", "Laws");
                await Wait(.25);
                Check(game.FindChild("PolicySearch", true, false) is not null, "laws workspace is available");
                await Capture("laws");
                game.Call("SelectDossierTab", "Course");
                await Wait(.25);
                Check(game.FindChildren("Course_*", "PanelContainer", true, false).Count == 8, "national course definitions and cards load");
                await Capture("course");
                (game.FindChild("ClosePolicyWorkspace", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
                GD.Print(game.Call("ConsoleAdvance", "7").AsString());
                foreach (var flag in new[] { "roman_empire_v2", "eastern_roman_empire_v2", "western_roman_empire" })
                {
                    var texture = GD.Load<Texture2D>("res://game/gfx/flags/flag_" + flag + ".png");
                    Check(texture?.GetWidth() == 384 && texture.GetHeight() == 256, "Roman flag imports: " + flag);
                }
                Check(Loc.Get("chronicle.denunciation") != "chronicle.denunciation", "chronicle caption has a translation");
                await Capture("map");
            }
        }
        catch (Exception error)
        {
            _failures++;
            GD.PushError("EXPORT_VERIFY " + error);
        }
        GD.Print("EXPORT_VERIFY failures=" + _failures);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Check(bool condition, string caption)
    {
        GD.Print((condition ? "PASS " : "FAIL ") + "EXPORT_VERIFY " + caption);
        if (!condition) _failures++;
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task Capture(string label)
    {
        if (_captureDirectory.Length == 0 || DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        // Screenshots are generated output in the explicitly requested directory.
        Check(DirAccess.MakeDirRecursiveAbsolute(_captureDirectory) == Error.Ok, "capture directory is available");
        using var image = GetTree().Root.GetTexture().GetImage();
        Check(image.SavePng(System.IO.Path.Combine(_captureDirectory, "export-" + label + ".png")) == Error.Ok,
            "capture " + label);
    }
}
