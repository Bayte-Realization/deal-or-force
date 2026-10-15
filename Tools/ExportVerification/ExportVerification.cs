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
            var args = OS.GetCmdlineUserArgs();
            var languageAt = Array.IndexOf(args, "--verify-language");
            if (languageAt >= 0 && languageAt + 1 < args.Length)
                Loc.Language = args[languageAt + 1];
            Check(tree.CurrentScene is MainMenuScene && tree.CurrentScene.FindChildren("*", "Button", true, false).Count > 3,
                "managed main menu and its controls load");
            // Source-only UI probes may explicitly permit locally installed tooling.
            // Shipped-executable verification never supplies this flag.
            var developmentProbe = OS.GetCmdlineUserArgs().Contains("--verify-development");
            foreach (var property in ProjectSettings.Singleton.GetPropertyList())
            {
                var key = property["name"].AsString();
                if (!developmentProbe && key.StartsWith("autoload/", StringComparison.Ordinal))
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
                // Startup reapplies the user's saved language. Verification must override
                // only this isolated process, never write its choice to user preferences.
                if (languageAt >= 0 && languageAt + 1 < args.Length)
                {
                    Loc.Language = args[languageAt + 1];
                    game.Call("RefreshLocalisedText");
                    Check(Loc.Language == args[languageAt + 1], "requested verification language is active");
                }
                await VerifySovereignFinance(game);
                game.Call("OpenScreen", "Laws");
                await Wait(.25);
                Check(game.FindChild("PolicySearch", true, false) is not null, "laws workspace is available");
                await Capture("laws");
                game.Call("SelectDossierTab", "Course");
                await Wait(.25);
                Check(game.FindChildren("Course_*", "PanelContainer", true, false).Count == 8, "national course definitions and cards load");
                await Capture("course");
                await VerifyDomesticWorkspace(game);
                (game.FindChild("ClosePolicyWorkspace", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
                await VerifyDealWorkspace(game);
                await VerifyConflictWorkspaces(game);
                await VerifyMovementDiplomacy(game);
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

    private async Task VerifyDomesticWorkspace(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var before = CampaignFingerprint.Of(world);
        game.Call("SelectDossierTab", "Crises");
        await Wait(.15);
        Check(game.FindChild("CrisisGauge_couppressure", true, false) is ProgressBar, "actual crisis gauges render");
        Check(before == CampaignFingerprint.Of(world), "inspecting crises changes no simulation state");
        var search = game.FindChild("PolicySearch", true, false) as LineEdit;
        search.Text = "__no_region__";
        search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
        await Wait(.1);
        Check(game.FindChildren("CrisisRegion_*", "PanelContainer", true, false).Count == 0,
            "crisis region search exposes an empty state without stale cards");
        search.Text = ""; search.EmitSignal(LineEdit.SignalName.TextChanged, "");
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            GetTree().Root.Size = size; await Wait(.25);
            CheckFits(game.FindChild("PolicyPanel", true, false) as Control, "crises " + size.X);
            await Capture("crises-" + size.X);
        }
        var player = world.Player;
        var treasury = player.Treasury;
        var action = game.FindChild("CrisisResponse_public_dialogue", true, false) as Button;
        Check(action is { Disabled: false }, "fresh solvent country can review dialogue");
        if (action is { Disabled: false })
        {
            action.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            Check(player.Treasury == treasury, "reviewing dialogue charges nothing");
            action = game.FindChild("CrisisResponse_public_dialogue", true, false) as Button;
            action?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            Check(player.Treasury < treasury && player.DomesticCrisis.ResponseId == "public_dialogue",
                "confirming dialogue starts the real paid response");
            Check((game.FindChild("CrisisResponse_public_dialogue", true, false) as Button)?.Disabled == true,
                "active response cannot be bought repeatedly");
        }
        GetTree().Root.Size = new(1920, 1080);
    }

    private async Task VerifySovereignFinance(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        game.VerificationEnableFinancialOrders();
        var n = world.Player;
        n.PublicDebt = 33; n.DebtArrears = .03f; n.PaymentArrears = .02f;
        game.Call("Refresh"); await Wait(.15);
        var unmigrated = world.Nations.Where(x => x.Movement is null && (!x.DebtInitialized || x.Treasury < 0)).ToArray();
        Check(unmigrated.Length == 0,
            "initial nonplayer sovereign accounts contain no unmigrated overdrafts: " +
            string.Join(", ", unmigrated.Select(x => x.Tag)));
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            GetTree().Root.Size = size; game.Call("Refresh"); await Wait(.2); game.Call("Refresh"); await Wait(.1);
            var debt = game.FindChild("HudStat_DEBT", true, false) as Control;
            Check(debt is { Visible: true, FocusMode: Control.FocusModeEnum.All }, "public debt headline is visible and keyboard reachable");
            CheckFits(game.FindChild("CampaignTopBar", true, false) as Control, "eight-metric top bar " + size.X);
            if (debt is not null)
                Check(GetTree().Root.GetVisibleRect().Encloses(debt.GetGlobalRect()), "debt amount stays on screen at " + size.X);
            await Capture("debt-hud-" + size.X);
        }
        var headline = game.FindChild("HudStat_DEBT", true, false) as Control;
        headline.GrabFocus();
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = true }); await Wait(.15);
        Check((game.FindChild("SovereignFinanceWorkspace", true, false) as Control)?.Visible == true,
            "Enter on the debt headline opens the financial workspace");
        var before = CampaignFingerprint.Of(world);
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            GetTree().Root.Size = size; await Wait(.2);
            CheckFits(game.FindChild("SovereignFinancePanel", true, false) as Control, "financial workspace " + size.X);
            var title = game.FindChild("FinanceTitle", true, false) as Label;
            Check(title is { Text.Length: > 0 } && title.GetGlobalRect().Size.Y >= 20 &&
                title.GetGlobalRect().Size.X >= 200 && title.IsVisibleInTree(),
                "financial workspace title has readable geometry at " + size.X);
            Check((game.FindChild("FinancePrincipal", true, false) as Label)?.Text == Fmt.Money(33), "workspace reports the actual saved principal");
            await Capture("sovereign-finance-" + size.X);
        }
        Check(before == CampaignFingerprint.Of(world), "reading debt, limits and forecasts changes no campaign state");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Space, Pressed = true }); await Wait(.1);
        Check(world.IsPaused, "the finance modal blocks the map's time hotkey");
        var amount = game.FindChild("FinanceAmount", true, false) as SpinBox;
        var issue = game.FindChild("FinanceAction_borrow", true, false) as Button;
        amount.Value = .01; await Wait(.1);
        var cash = n.Treasury; var principal = n.PublicDebt; var issued = n.DebtIssuedWeekly;
        Check(issue is { Disabled: false }, "solvent country can review an affordable bond issue");
        issue?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
        Check(n.Treasury == cash && n.PublicDebt == principal, "first issue click only reviews the operation");
        issue?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
        Check(Math.Abs(n.Treasury - cash - .01f) < .0001f && Math.Abs(n.PublicDebt - principal - .01f) < .0001f &&
            n.DebtIssuedWeekly > issued, "confirmed bond issue raises cash and principal through the real bounded command");
        amount.Value = Math.Max(1, n.Gdp); await Wait(.1);
        Check(issue.Disabled, "an oversized issue is disabled with an explicit reason");
        amount.Value = .01; await Wait(.1);
        var pay = game.FindChild("FinanceAction_arrears", true, false) as Button;
        var debtArrears = n.DebtArrears; var spendingArrears = n.PaymentArrears; cash = n.Treasury;
        pay.EmitSignal(BaseButton.SignalName.Pressed); pay.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
        Check(Math.Abs(n.DebtArrears - debtArrears + .01f) < .0001f && n.PaymentArrears == spendingArrears &&
            Math.Abs(n.Treasury - cash + .01f) < .0001f, "the UI services overdue interest before primary bills");
        (game.FindChild("CloseSovereignFinance", true, false) as Button)?.GrabFocus();
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true }); await Wait(.1);
        Check((game.FindChild("SovereignFinanceWorkspace", true, false) as Control)?.Visible == false,
            "Escape closes the financial modal");
        game.Call("OpenScreen", "State Affairs"); await Wait(.1);
        (game.FindChild("BudgetOpenSovereignFinance", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true }); await Wait(.1);
        Check((game.FindChild("StateAffairs", true, false) as Control)?.Visible == true,
            "finance opened from the budget returns to the budget on Escape");
        game.Call("OpenScreen", "Nation");
        GetTree().Root.Size = new(1920, 1080);
    }

    private async Task VerifyDealWorkspace(DealOrForceGame game)
    {
        game.Call("ConsoleTag", "USA");
        // Changing the player reapplies their saved UI preferences. A language probe
        // overrides those in this process only, without changing the preference file.
        var args = OS.GetCmdlineUserArgs();
        var languageAt = Array.IndexOf(args, "--verify-language");
        if (languageAt >= 0 && languageAt + 1 < args.Length)
        { Loc.Language = args[languageAt + 1]; game.Call("RefreshLocalisedText"); }
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var player = world.Player;
        var partner = world.Nations.First(n => n.Tag == "GBR");
        var fingerprint = CampaignFingerprint.Of(world);
        game.OpenDealComposer(partner); await Wait(.15);
        Check(fingerprint == CampaignFingerprint.Of(world), "opening a peaceful deal is read-only");
        var send = game.FindChild("SendComposedDeal", true, false) as Button;
        Check(send?.Disabled == true, "empty deal cannot be sent");
        var give = game.FindChild("DealGiveCash", true, false) as SpinBox;
        give.Value = Math.Min(.1, give.MaxValue); await Wait(.1);
        Check(send is { Disabled: false } && give.Value > 0, "cash offer between existing allies is allowed");
        var release = game.FindChild("DealGiveRelease", true, false) as CheckBox;
        var region = game.FindChildren("DealGiveRegion_*", "CheckBox", true, false).OfType<CheckBox>().FirstOrDefault();
        Check(release is not null && region is not null, "independence is a visible deal term");
        release.ButtonPressed = true; region.ButtonPressed = true; await Wait(.1);
        Check(send is { Disabled: false }, "whole populated region can be offered as an independent settlement");
        var preview = game.FindChild("DealTerritoryPreview", true, false) as TextureRect;
        Check(preview is { Visible: true, Texture: not null }, "selected territorial package renders its real-map preview");
        var search = game.FindChild("DealAskRegionSearch", true, false) as LineEdit;
        search.Text = "__no_region__"; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
        await Wait(.1);
        Check((game.FindChild("DealAskEmpty", true, false) as Control)?.Visible == true,
            "deal search explains no matching regions");
        var pause = new InputEventKey { Keycode = Key.Space, Pressed = true };
        Input.ParseInputEvent(pause); await Wait(.1);
        Check(world.IsPaused, "deal modal blocks the map's time hotkey");
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            GetTree().Root.Size = size; await Wait(.25);
            CheckFits(game.FindChild("DealPanel", true, false) as Control, "deal " + size.X);
            await Capture("deal-" + size.X);
        }
        var cash = player.Treasury; var partnerCash = partner.Treasury;
        var countryCount = world.Nations.Count;
        send?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(player.Treasury == cash && partner.Treasury == partnerCash, "first deal click only requests confirmation");
        send?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
        Check(player.Treasury < cash && partner.Treasury > partnerCash && send?.Disabled == true,
            "second click settles through the command and blocks repeat sending");
        Check(world.Nations.Count == countryCount + 1 && Math.Abs(player.Treasury + partner.Treasury + world.Nations.Last().Treasury - cash - partnerCash) < .01f,
            "the real UI establishes a third country and conserves all three treasuries");
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, Pressed = true }); await Wait(.1);
        Check(game.FindChild("DealComposer", true, false) is null, "Escape closes and detaches the deal modal");
        GetTree().Root.Size = new(1920, 1080);
    }

    private void CheckFits(Control panel, string caption)
    {
        var viewport = GetTree().Root.GetVisibleRect();
        Check(panel is not null && viewport.Encloses(panel.GetGlobalRect()), caption + " panel remains inside the viewport");
    }

    private async Task VerifyConflictWorkspaces(DealOrForceGame game)
    {
        game.Call("OpenScreen", "Conflicts"); await Wait(.1);
        var world = game.VerificationWorld;
        Check(world.Organizations.Any(o => o.Name == "NATO" && o.Members.Count > 20), "named defence organizations load as separate institutions");
        foreach (var tab in new[] { 3, 4 })
        {
            (game.FindChild("ConflictTab" + tab, true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.1);
            if (tab == 3 && game.FindChildren("TerritoryDispute_*", "Button", true, false).FirstOrDefault() is Button dispute)
                dispute.EmitSignal(BaseButton.SignalName.Pressed);
            if (tab == 4 && world.Organizations.FirstOrDefault(o => !o.Dissolved) is { } first)
                (game.FindChild("Organization_" + first.Id, true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
            var before = CampaignFingerprint.Of(world);
            foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
            {
                GetTree().Root.Size = size; await Wait(.2);
                CheckFits(game.FindChild("ConflictsPanel", true, false) as Control, "conflicts " + tab + " at " + size.X);
                await Capture((tab == 3 ? "territories-" : "organizations-") + size.X);
            }
            Check(before == CampaignFingerprint.Of(world), "conflict workspace inspection does not modify institutions or titles");
        }
        game.Call("OpenScreen", "Nation");
    }

    private async Task VerifyMovementDiplomacy(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var designated = world.Nations.First(n => n.Tag == "TTP" && n.Movement is { Dissolved: false });
        // A real action in this disposable verification campaign, not an invented
        // historical designation or a change to the player's saved campaign.
        world.Commands.Enqueue(new MovementDealingCommand { IssuerId = world.Player.Id, MovementId = designated.Id, Action = "designate" });
        world.Commands.ApplyAll(world);
        Check(designated.Movement.DesignatedBy.Contains(world.Player.Id), "the movement designation action records its actual issuer");
        var recognised = world.Nations.Where(n => n.Movement is { Dissolved: false } && n.Movement.RecognisedBy.Count > 0)
            .OrderByDescending(n => n.Movement.RecognisedBy.Count).First();
        foreach (var movement in new[] { designated, recognised }.Distinct())
        {
            var before = CampaignFingerprint.Of(world);
            game.VerificationShowMovement(movement); await Wait(.15);
            foreach (var prefix in new[] { "MovementRecognition", "MovementDesignation" })
            {
                var toggle = game.FindChild(prefix + "Toggle", true, false) as Button;
                Check(toggle is not null, prefix + " countries can be inspected");
                toggle?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
                var expected = (prefix == "MovementRecognition" ? movement.Movement.RecognisedBy : movement.Movement.DesignatedBy)
                    .Count(id => world.NationById(id) is not null);
                Check(game.FindChildren(prefix + "Country_*", "Button", true, false).Count == expected,
                    prefix + " list reflects exact living records, not an invented count");
            }
            foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
            {
                GetTree().Root.Size = size; await Wait(.2);
                CheckFits(game.FindChild("MovementPanel", true, false) as Control, "movement at " + size.X);
                await Capture("movement-" + movement.Tag.ToLowerInvariant() + "-" + size.X);
            }
            Check(before == CampaignFingerprint.Of(world), "movement diplomacy inspection changes no simulation state");
            var countryLink = game.FindChildren("Movement*Country_*", "Button", true, false).OfType<Button>().FirstOrDefault();
            Check(countryLink is not null, "non-empty movement record exposes a country diplomacy link");
            countryLink?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            Check((game.FindChild("Diplomacy", true, false) as Control)?.Visible == true,
                "movement country link opens the full diplomacy workspace");
            game.Call("OpenScreen", "Nation");
            game.VerificationCloseMovement();
        }
        GetTree().Root.Size = new(1920,1080);
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

public partial class DealOrForceGame
{
    // A narrow internal test seam; not a script-callable command or saved state.
    internal Campaign VerificationWorld => _campaign;
    internal void VerificationEnableFinancialOrders() { if (_observerMode) ToggleObserverMode(); }
    internal void VerificationShowMovement(Nation movement) => ShowMovement(movement);
    internal void VerificationCloseMovement() => _movementPanel.Visible = false;
}
