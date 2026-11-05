using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using DealOrForce.Modding;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyRelease0530(DealOrForceGame game, string[] args)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "dof-0530-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        game.VerificationAuthorRoot(fixture);
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("RUS").Id);
        game.VerificationEnableFinancialOrders();
        world.IsPaused = true;
        var before = CampaignFingerprint.Of(world);
        try
        {
            foreach (var (tag, project) in new[] { ("CHN", "china_reunified"), ("ITA", "italia_unita"),
                         ("LNA", "libya_reunited"), ("PSE", "palestine_united"), ("SDN", "sudan_reunited") })
                Check(!FormableNations.Evaluate(world, world.NationByTag(tag), FormableNations.Find(project)).CanForm,
                    "real opening map cannot proclaim " + project + " by ignoring its small founding partners");
            game.Call("OpenNetworkMultiplayer");
            await Wait(.15);
            Check(game.FindChild("NetworkMultiplayer", true, false) is Control { Visible: true },
                "cold multiplayer lobby opens without visiting the lazy single-player picker (no socket opened)");
            game.Call("ShowLegacyMenuPanel");
            game.Call("BeginSession");
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language; game.Call("RefreshLocalisedText"); GetTree().Root.Size = size;
                var suffix = language + "-" + size.X;
                game.VerificationOpenDiplomacy("CHN");
                await Wait(.3);
                var acts = game.FindChildren("DiplomaticAct_*", "Button", true, false).OfType<Button>().ToArray();
                // Compact typography raises ALL action targets to 44 logical units.
                // Compare to the real shared factory button, not a hardcoded desktop size.
                Check(acts.Length == DiplomaticActs.All.Count() && acts.All(b =>
                    b.CustomMinimumSize.Y == game.VerificationDiplomacyStandardHeight && b.Text != Loc.Get("Propose")),
                    "Cooperation uses standard named diplomacy buttons " + suffix);
                if (acts.FirstOrDefault() is { } first)
                {
                    for (Node p = first.GetParent(); p is not null; p = p.GetParent())
                        if (p is ScrollContainer scroll) { scroll.EnsureControlVisible(first); break; }
                    await Wait(.15);
                    CheckFits(first, "reachable Cooperation action " + suffix);
                }
                await Capture("cooperation-" + suffix);
                game.Call("CloseDiplomacyScreen");
                game.VerificationPrimeMapHover();
                await Wait(1.1);
                Check(game.VerificationCentreTooltip().Length > 0, "unblocked map still supplies a real tooltip " + suffix);
                Check(game.VerificationReadinessPopupText().Length > 0, "actual persistent map tooltip is open before the modal " + suffix);
                game.Call("OpenContentEditor");
                await Wait(.25);
                Check(game.VerificationReadinessPointerTooltip().Length == 0 && game.VerificationReadinessPopupText().Length == 0,
                    "workshop dismisses the actual map popup and blocks new map hover " + suffix);
                var bar = game.FindChild("AuthorPackageBar", true, false) as Control;
                CheckFits(bar, "visible mod destination and package action " + suffix);
                Check(game.VerificationLoadingInputKeepsWorkshop(), "loading cover owns Escape before workshop handlers " + suffix);
                var destination = bar.GetChildren().OfType<Label>().Single();
                Check(destination.Size.X > 100 && destination.Size.Y >= 30 && destination.Text.Length > 0,
                    "current destination has a readable non-zero layout " + suffix);
                var picker = bar.GetChildren().OfType<Button>().Single();
                picker.EmitSignal(Button.SignalName.Pressed);
                await Wait(.15);
                Check(game.VerificationReadinessPopupText().Length == 0, "package picker cannot resurrect the map tooltip " + suffix);
                var dialog = game.FindChild("AuthorPackagePicker", true, false) as Window;
                Check(dialog is { Visible: true } && dialog.Size.Y <= size.Y, "mod picker opens inside the screen " + suffix);
                await Capture("mod-picker-" + suffix);
                var id = dialog.FindChild("AuthorNewModId", true, false) as LineEdit;
                var name = dialog.FindChild("AuthorNewModName", true, false) as LineEdit;
                id.Text = "fixture_" + language + "_" + size.X;
                var createdId = id.Text;
                name.Text = "UI fixture " + suffix;
                var create = dialog.FindChildren("*", "Button", true, false).OfType<Button>().Single(b => b.Text == Loc.Get("Create mod"));
                create.EmitSignal(Button.SignalName.Pressed);
                await Wait(.25);
                var root = game.VerificationSelectedAuthorRoot;
                Check(root.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(root, "manifest.json")), "real create button writes only the isolated package " + suffix);
                Check(game.FindChild("AuthorPackageBar", true, false).GetChildren().OfType<Label>().Single().Text.Contains(createdId),
                    "editor visibly switches to the new package " + suffix);
                await Capture("mod-destination-" + suffix);
                game.Call("CloseAuthorWindow", false);
                await Wait(.1);
                game.VerificationAuthorRoot(fixture);
            }
            Check(before == CampaignFingerprint.Of(world), "diplomacy inspection and package creation do not change the campaign");
            var codeAt = Array.IndexOf(args, "--verify-corecode-package");
            if (codeAt >= 0 && codeAt + 1 < args.Length) VerifyCompiledTemplate(args[codeAt + 1]);
        }
        finally
        {
            game.Call("CloseAuthorWindow", false);
            game.VerificationAuthorRoot(null);
            Directory.Delete(fixture, true); // Only the isolated folder created by this probe.
        }
    }

    private void VerifyCompiledTemplate(string root)
    {
        // Exercise the production loader/context/hooks, not a second plugin implementation.
        var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var package = new ModPackage { Manifest = manifest, PhysicalRoot = root, VirtualRoot = "user://mods/probe" };
        var loader = new ModLoader();
        var packages = (List<ModPackage>)typeof(ModLoader).GetField("_packages", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(loader);
        packages.Add(package);
        var world = Campaign.CreateBootstrap(20261105);
        var korea = world.NationByTag("KOR");
        korea.Stability = .5f;
        using var api = new ModApi(_ => { });
        try
        {
            loader.StartCode(api, world);
            Check(package.Errors.Count == 0 && loader.Log.Any(s => s.Contains("Example corecode mod loaded")) &&
                loader.Log.Any(s => s.Contains("Campaign ready:")), "compiled template really executes OnLoad and OnCampaignCreated");
            Check(ModSimulationPasses.Owner(world, SimulationPass.Construction) == manifest.Id,
                "production loader installs the DLL's declared core replacement");
            world.IsPaused = false;
            world.Tick();
            world.IsPaused = true;
            Check(loader.Log.Count(s => s.Contains("Construction pass forwarded once")) == 1,
                "compiled replacement runs at the real simulation pass, not a rendered frame");
            korea.Stability = .5f;
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(world, 30 * 24L);
            loader.OnDayAdvanced(world);
            Check(Math.Abs(korea.Stability - .502f) < .00001f, "compiled daily hook changes the real campaign once");
            loader.StopCode();
            Check(ModSimulationPasses.Owner(world, SimulationPass.Construction) == "",
                "production StopCode releases replacement delegates before unloading the DLL");
            loader.OnDayAdvanced(world);
            Check(Math.Abs(korea.Stability - .502f) < .00001f, "StopCode detaches the old plugin before another campaign");
            loader.StartCode(api, world);
            Check(ModSimulationPasses.Owner(world, SimulationPass.Construction) == manifest.Id,
                "compiled replacement can be installed again without a leaked owner");
            loader.OnDayAdvanced(world);
            Check(Math.Abs(korea.Stability - .504f) < .00001f, "reloading a compiled package does not duplicate its daily hook");
        }
        finally { loader.StopCode(); }
    }
}

public partial class DealOrForceGame
{
    internal string VerificationSelectedAuthorRoot => AuthorRoot;
    internal float VerificationDiplomacyStandardHeight => _diplomacyImproveButton.CustomMinimumSize.Y;
    internal void VerificationPrimeMapHover()
    {
        // Send a viewport-local hover without moving the user's OS pointer. The
        // production TooltipKeeper owns the popup, not the verification fixture.
        var point = GetViewport().GetVisibleRect().Size / 2;
        using var motion = new InputEventMouseMotion { Position = point, GlobalPosition = point };
        GetViewport().PushInput(motion, true);
    }
    internal bool VerificationLoadingInputKeepsWorkshop()
    {
        var canvas = _authorCanvas;
        var wasLoading = _isLoading;
        try
        {
            _isLoading = true;
            using var escape = new InputEventKey { Pressed = true, Keycode = Key.Escape };
            _Input(escape);
            return IsInstanceValid(canvas) && canvas == _authorCanvas;
        }
        finally { _isLoading = wasLoading; }
    }
}
