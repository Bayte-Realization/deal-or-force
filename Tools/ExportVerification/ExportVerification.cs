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
            var pureRule = Rules.AssaultRules.Assess(true, true, true, true, false, 0, 0, 1, 1, .16f, .20f);
            Check(pureRule.Ready && typeof(Rules.AssaultRules).Assembly.GetName().Name == "DealOrForce.Rules",
                "production F# rules load and execute inside Godot");
            var tree = GetTree();
            await Wait(DisplayServer.GetName() == "headless" ? .25 : 6);
            var args = OS.GetCmdlineUserArgs();
            var languageAt = Array.IndexOf(args, "--verify-language");
            if (languageAt >= 0 && languageAt + 1 < args.Length)
                Loc.Language = args[languageAt + 1];
            Check(tree.CurrentScene is MainMenuScene && tree.CurrentScene.FindChildren("*", "Button", true, false).Count > 3,
                "managed main menu and its controls load");
            // The F# pure-rule library is an optional dependency, and the hook that reaches it
            // is deliberately forgiving: on its first exception it answers with the managed
            // implementation from then on and writes one line to the log, so a pack that cannot
            // load the assembly still plays perfectly - on different code than the one that was
            // verified. The editor and the test stand prove the fallback does not happen there
            // (AssaultRulesBackendTests checks the installed hook); nothing proved it about the
            // shipped pack, which is exactly where FSharp.Core is the kind of dependency that
            // goes missing. Checked here, before the campaign, because loadability is a property
            // of the pack and not of a world. The call is what makes the check mean anything:
            // UsingFSharp reads true before the first call ever happens.
            AssaultRulesHook.TryAssess(true, true, true, false, false, 0, 0, 1f, 1f, 0.5f, 0.2f, out _);
            Check(AssaultRulesHook.UsingFSharp, "assault readiness resolves through the shipped F# rules library");
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
                if (args.Contains("--verify-audio-focus-only"))
                {
                    await VerifyAudioFocus(game);
                    GD.Print($"EXPORT_VERIFY failures={_failures}");
                    GetTree().Quit(_failures == 0 ? 0 : 1);
                    return;
                }
                if (args.Contains("--verify-hud-only"))
                {
                    await VerifyHudText(game);
                    GD.Print($"EXPORT_VERIFY failures={_failures}");
                    GetTree().Quit(_failures == 0 ? 0 : 1);
                    return;
                }
                if (args.Contains("--verify-diplomacy-only"))
                {
                    await VerifyDiplomacyLayout(game);
                    GD.Print($"EXPORT_VERIFY failures={_failures}");
                    GetTree().Quit(_failures == 0 ? 0 : 1);
                    return;
                }
                if (args.Contains("--verify-army-activity-only"))
                {
                    await VerifyArmyActivity(game);
                    GD.Print($"EXPORT_VERIFY failures={_failures}");
                    GetTree().Quit(_failures == 0 ? 0 : 1);
                    return;
                }
                if (args.Contains("--verify-movement-only"))
                {
                    VerifyCivilWarBanners(game);
                    await VerifyMovementDiplomacy(game);
                    GD.Print($"EXPORT_VERIFY failures={_failures}");
                    GetTree().Quit(_failures == 0 ? 0 : 1);
                    return;
                }
                await VerifyAgendaWorkspace(game);
                if (args.Contains("--verify-agenda-only"))
                {
                    GD.Print($"EXPORT_VERIFY failures={_failures}");
                    GetTree().Quit(_failures == 0 ? 0 : 1);
                    return;
                }
                await VerifyDiplomacyLayout(game);
                await VerifySovereignFinance(game);
                await VerifyReligionAndOccupation(game);
                await VerifyGarrison(game);
                await VerifyDevelop(game);
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
                await VerifyTerritorialMovementRepresentation(game);
                await VerifyMovementBanners(game);
                await VerifyMovementDiplomacy(game);
                VerifyCivilWarBanners(game);
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

    private async Task VerifyReligionAndOccupation(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var originalLanguage = Loc.Language;
        foreach (var language in new[] { "english", "russian" })
        {
            Loc.Language = language;
            game.Call("RefreshLocalisedText");
            game.Call("OpenStateAffairs");
            await Wait(.2);
            var before = CampaignFingerprint.Of(world);
            var buttons = game.FindChildren("StateReligion_*", "Button", true, false).OfType<Button>().ToArray();
            Check(buttons.Count(b => b.Name == "StateReligion_irreligion") == 1,
                language + " has exactly one irreligion option");
            Check(buttons.Select(b => b.Name.ToString()).Distinct().Count() == buttons.Length,
                language + " religion choices have unique identities");
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                GetTree().Root.Size = size; await Wait(.2);
                game.VerificationRevealReligion(); await Wait(.15);
                await Capture("religion-single-" + language + "-" + size.X);
            }
            Check(before == CampaignFingerprint.Of(world), "religion inspection changes no campaign state");
            game.Call("CloseStateAffairs");
        }
        Loc.Language = originalLanguage; game.Call("RefreshLocalisedText");
        GetTree().Root.Size = new(1920, 1080);
        var map = world.ProvinceMap;
        var owner = world.NationByTag("UKR");
        var occupier = world.NationByTag("RUS");
        var province = Enumerable.Range(1, map.ProvinceCount).First(p =>
            map.ProvinceToNation[p] == owner.MapIndex && !map.IsWater(p) &&
            world.Armies.All(a => a.ProvinceId != p) &&
            CityCatalog.ProvinceUnder(map, map.CenterOf(p).Latitude, map.CenterOf(p).Longitude) == p);
        var oldControl = world.ProvinceController[province];
        world.SetProvinceController(province, (ushort)occupier.MapIndex);
        var occupationBefore = CampaignFingerprint.Of(world);
        game.VerificationFocusProvince(province); await Wait(1);
        foreach (var language in new[] { "english", "russian" })
        {
            Loc.Language = language; game.Call("RefreshLocalisedText");
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                GetTree().Root.Size = size; await Wait(.4);
                var pick = game.VerificationCentrePick();
                Check(pick.ProvinceId == province && pick.Nation?.Id == owner.Id,
                    language + " actual map hit keeps Ukrainian ownership under Russian occupation");
                var tooltip = game.VerificationCentreTooltip();
                // This check is named for a distinction, so it asserts the distinction rather
                // than a sentence. The occupation line was rewritten by the state-identity
                // readability pass into something that names the occupier, names the owner and
                // says an occupation is not an annexation - strictly better for the player - and
                // a check pinned to the old wording started failing on a tooltip that had
                // improved. Requiring both countries to appear, and to be different countries,
                // keeps the check answering the question it was written for.
                Check(occupier.DisplayName != owner.DisplayName &&
                      tooltip.Contains(occupier.DisplayName) && tooltip.Contains(owner.DisplayName),
                    language + " actual map tooltip distinguishes occupier from owner");
                game.VerificationInspectPick(pick); await Wait(.15);
                await Capture("occupation-not-title-" + language + "-" + size.X);
            }
        }
        Check(map.ProvinceToNation[province] == owner.MapIndex && occupationBefore == CampaignFingerprint.Of(world),
            "occupied map inspection never transfers title or alters the campaign");
        world.SetProvinceController(province, oldControl);
        Loc.Language = originalLanguage; game.Call("RefreshLocalisedText");
        GetTree().Root.Size = new(1920, 1080);
        game.Call("OpenScreen", "Nation");
    }

    private async Task VerifyGarrison(DealOrForceGame game)
    {
        var ordered = game.VerificationGarrison(out var report);
        Check(ordered, "province card garrison order reaches a real division: " + report);
        await Wait(.1);
    }

    private async Task VerifyDevelop(DealOrForceGame game)
    {
        var opened = game.VerificationDevelop(out var report);
        Check(opened, "province card development opens construction on that region: " + report);
        await Wait(.1);
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

    private async Task VerifyTerritorialMovementRepresentation(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var actor = world.NationByTag("HTH");
        Check(actor?.FactionId == "ansar_allah" && world.StatesOf(actor).Length > 0 &&
            world.Armies.Any(a => a.NationId == actor.Id), "Ansar Allah retains its territorial administration and army");
        Check(world.NationByTag("HOU") is null, "no duplicate Ansar Allah movement is founded");
        if (actor is null) return;
        var before = CampaignFingerprint.Of(world);
        game.VerificationFocusAdministration(actor);
        await Wait(.8);
        Check(game.FindChild("Headquarters_HOU", true, false) is null,
            "the rendered movement layer contains no duplicate HOU headquarters");
        var language = Loc.Language;
        foreach (var locale in new[] { "english", "russian" })
        {
            Loc.Language = locale;
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                GetTree().Root.Size = size; await Wait(.3);
                await Capture("yemen-territorial-" + locale + "-" + size.X);
            }
        }
        Loc.Language = language;
        GetTree().Root.Size = new(1920, 1080);
        Check(before == CampaignFingerprint.Of(world), "inspecting Yemen does not change the campaign");
    }

    private async Task VerifyMovementBanners(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var before = CampaignFingerprint.Of(world);
        foreach (var tag in new[] { "HMS", "BVC" })
        {
            var movement = world.NationByTag(tag);
            Check(movement?.Movement is not null, tag + " exists for the real flag presentation check");
            if (movement is null) continue;
            var stem = "flag_" + tag.ToLowerInvariant();
            var expected = SettingsCatalog.Flag("movement_flags") && SpriteCatalog.Has(stem + "_insignia")
                ? stem + "_insignia" : stem;
            Check(DealOrForceGame.FlagSpriteFor(tag) == expected, tag + " map/profile resolve the authored banner");
            Check(DealOrForceGame.FlagTextureFor(tag) is not null, tag + " real flag texture loads");
            game.VerificationShowMovement(movement);
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                GetTree().Root.Size = size; await Wait(.2);
                await Capture("restored-flag-" + tag.ToLowerInvariant() + "-" + size.X);
            }
            game.VerificationCloseMovement();
        }
        Check(before == CampaignFingerprint.Of(world), "movement flag inspection changes no simulation state");
        GetTree().Root.Size = new(1920, 1080);
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
            var scroll = game.FindChild("MovementScroll", true, false) as ScrollContainer;
            if (scroll is not null) scroll.ScrollVertical = 120;
            await Wait(.1);
            var position = scroll?.ScrollVertical ?? 0;
            // Run actual campaign time, then force a fresh UI snapshot as the periodic
            // driver does. Do not merely check the old, still-alive Control instances.
            world.TickDay();
            game.VerificationRefreshMovement();
            await Wait(.2);
            foreach (var prefix in new[] { "MovementRecognition", "MovementDesignation" })
                Check((game.FindChild(prefix + "List", true, false) as Control)?.Visible == true,
                    prefix + " remains expanded across a simulation day and rebuilt rows");
            Check(scroll is not null && Math.Abs(scroll.ScrollVertical - position) <= 2,
                "movement scrolling survives the same tick refresh");
            var designation = game.FindChild("MovementDesignationToggle", true, false) as Button;
            designation?.EmitSignal(BaseButton.SignalName.Pressed);
            game.VerificationRefreshMovement(true); await Wait(.15);
            Check((game.FindChild("MovementDesignationList", true, false) as Control)?.Visible == false &&
                (game.FindChild("MovementRecognitionList", true, false) as Control)?.Visible == true,
                "an explicitly collapsed section stays collapsed independently of the other section");
            game.VerificationCloseMovement();
            game.VerificationShowMovement(movement); await Wait(.15);
            Check((game.FindChild("MovementDesignationList", true, false) as Control)?.Visible == false &&
                (game.FindChild("MovementRecognitionList", true, false) as Control)?.Visible == true,
                "closing and reopening the same movement preserves the user's disclosures");
            var otherMovement = world.Nations.First(n => n.Id != movement.Id && n.Movement is { Dissolved: false });
            game.VerificationShowMovement(otherMovement); await Wait(.1);
            game.VerificationShowMovement(movement); await Wait(.15);
            Check((game.FindChild("MovementDesignationList", true, false) as Control)?.Visible == false &&
                (game.FindChild("MovementRecognitionList", true, false) as Control)?.Visible == true,
                "another movement's rebuilt card does not overwrite these disclosure choices");
            before = CampaignFingerprint.Of(world);
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

    private void VerifyCivilWarBanners(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var opposition = world.AddNation(new Nation { Tag = "XCV", Name = "rising:transitional_council:RUS:Russia",
            Government = "provisional_government", Ideology = "social_democracy", MapIndex = 60000 });
        DemographyCatalog.Declare(opposition.Tag, new[] { "ukrainian" });
        var ordinary = DealOrForceGame.FlagSpriteFor(opposition.Tag);
        Check(ordinary.Length > 0 && SpriteCatalog.TextureOf(ordinary) != SpriteCatalog.TextureOf("flag_ukr"),
            "a nationwide civil-war side does not fly its seed region's foreign flag in the real UI");
        opposition.SubjectType = "puppet"; opposition.OverlordId = world.NationByTag("UKR").Id;
        Check(DealOrForceGame.FlagSpriteFor(opposition.Tag) == "flag_rus_civil_war_ukr_social_democracy",
            "real UI flag cache notices actual foreign dependence");
        var texture = DealOrForceGame.FlagTextureFor(opposition.Tag);
        using var image = texture?.GetImage();
        Check(image is not null && image.GetWidth() == 300 && image.GetPixel(150,20).R > .9f &&
            image.GetPixel(150,100).B > .9f && image.GetPixel(150,180).R > .9f,
            "the dependent-opposition white-blue-white PNG imports and renders as a real texture");
        opposition.SubjectType = "";
        Check(DealOrForceGame.FlagSpriteFor(opposition.Tag) == ordinary,
            "ending dependence immediately restores the ordinary civil-war banner");
        var diaspora = world.AddNation(new Nation { Tag = "XCD", Name = "Regional government",
            Government = "provisional_government", Ideology = "nationalism", MapIndex = 60001 });
        DemographyCatalog.Declare(diaspora.Tag,new[]{"indian_diaspora"});
        Check(DealOrForceGame.FlagSpriteFor(diaspora.Tag) == "flag_culture_hindustani",
            "the independent diaspora provisional government does not display a British-style cross");
        diaspora.SubjectType = "puppet"; diaspora.OverlordId = world.NationByTag("GBR").Id;
        Check(DealOrForceGame.FlagSpriteFor(diaspora.Tag) == "flag_culture_indian_diaspora",
            "a restricted colonial-style banner is enabled only by actual British dependence");
        diaspora.SubjectType = "";
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
    internal void VerificationRefreshMovement(bool force = false)
    { if (force) _movementBuiltFrom = ""; RefreshMovementPanel(); }
    internal void VerificationFocusAdministration(Nation nation) =>
        _globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude, 5.5f);
    internal void VerificationRevealReligion()
    {
        for (Node parent = _religionButtons.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) { scroll.EnsureControlVisible(_religionButtons); break; }
    }
    internal void VerificationFocusProvince(int province)
    {
        var point = _campaign.ProvinceMap.CenterOf(province);
        _globe.FocusOn(point.Latitude, point.Longitude, 5.5f);
    }
    internal GlobePick VerificationCentrePick()
    {
        _globe.VerificationPickAt(GetViewport().GetVisibleRect().Size / 2, out var pick);
        return pick;
    }
    internal string VerificationCentreTooltip() => _globe._GetTooltip(GetViewport().GetVisibleRect().Size / 2);
    internal void VerificationInspectPick(GlobePick pick) => ShowMapSelection(pick);
}
