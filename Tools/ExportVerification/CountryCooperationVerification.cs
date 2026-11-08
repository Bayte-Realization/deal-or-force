using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task<bool> ClickCountryCooperation(Button button, string caption)
    {
        Check(button is { Disabled: false } && button.IsVisibleInTree(), "cooperation control is usable: " + caption);
        if (button is not { Disabled: false } || !button.IsVisibleInTree()) return false;
        for (Node node = button.GetParent(); node is not null; node = node.GetParent())
            if (node is ScrollContainer scroll) scroll.EnsureControlVisible(button);
        await Wait(.2);
        var point = button.GetGlobalTransformWithCanvas() * (button.Size * .5f);
        GetViewport().PushInput(new InputEventMouseMotion { Position = point }, true);
        var hit = button.GetViewport().GuiGetHoveredControl() == button;
        Check(hit, "cooperation mouse reaches the exact scrolled control: " + caption);
        if (!hit) return false;
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            Pressed = false }, true);
        await Wait(.2);
        return true;
    }

    private async Task VerifyCountryCooperation(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var player = c.Player; var target = c.NationByTag("JPN"); var other = c.NationByTag("DEU");
        Check(player is { IsHuman: true } && target is not null && other is not null && target != player && other != player,
            "country cooperation uses real foreign partners and a human player");
        if (player is not { IsHuman: true } || target is null || other is null || target == player || other == player) return;
        // Probe-only in-memory conditions. The source campaign is never saved.
        var relation = CampaignDiplomacy.Get(c, player.Id, target.Id);
        var original = (player.Treasury, relation.Opinion, relation.Stance, target.IsHuman,
            relation.DiplomaticActBy, relation.DiplomaticActDay, relation.DiplomaticActKind, c.DealInboxRevision);
        var languageBefore = Loc.Language; var sizeBefore = GetTree().Root.Size;
        var interceptor = c.Commands.SubmitInterceptor;
        try
        {
            c.IsPaused = true; player.Treasury = MathF.Max(40, player.Treasury);
            relation.Opinion = 80; relation.Stance = DiplomaticStance.Friendly;
            relation.DiplomaticActBy = -1; relation.DiplomaticActKind = -1; target.IsHuman = true;
            var intercepted = new List<DiplomaticActCommand>();
            c.Commands.SubmitInterceptor = command =>
            {
                if (command is DiplomaticActCommand act) { intercepted.Add(act); return true; }
                return interceptor?.Invoke(command) ?? false;
            };
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
                game.Call("CloseMapSelection"); game.VerificationOpenDiplomacy(target.Tag); await Wait(.25);
                var full = DiplomaticActs.All.ToDictionary(act => act, act =>
                {
                    var button = game.FindChild("DiplomaticAct_" + act, true, false) as Button;
                    return (Exists: button is not null, Disabled: button?.Disabled ?? true, Tip: button?.TooltipText ?? "");
                });
                // Leave the wide screen pointed elsewhere. A quick-card act must target
                // the selected card, not the last country viewed in full diplomacy.
                game.Call("CloseDiplomacyScreen"); game.VerificationOpenDiplomacy(other.Tag);
                game.Call("CloseDiplomacyScreen");
                var fingerprint = CampaignFingerprint.Of(c); var random = c.Random.State;
                game.VerificationInspectPick(new GlobePick(target, null, -1, target.CapitalLatitude, target.CapitalLongitude));
                await Wait(.25);
                var caption = language + " " + size.X;
                var tab = game.FindChild("CountryPageActions", true, false) as Button;
                if (!await ClickCountryCooperation(tab, "Actions " + caption)) continue;
                var section = game.FindChild("CountryCooperation", true, false) as Control;
                Check(section is not null && section.IsVisibleInTree() &&
                    section.FindChildren("*", "Label", true, false).OfType<Label>().Any(label => label.Text == Loc.Get("COOPERATION")),
                    "quick country Actions contains the localized Cooperation section: " + caption);
                foreach (var act in DiplomaticActs.All)
                {
                    var button = game.FindChild("CountryDiplomaticAct_" + act, true, false) as Button;
                    var refusal = DiplomaticActs.Refusal(c, player, target, act);
                    var detail = refusal.Length > 0 ? Loc.Get(refusal) :
                        Loc.Format("{0} from the treasury", Fmt.Money(DiplomaticActs.Cost(c, act)));
                    Check(button is not null && button.IsVisibleInTree() && button.Text == Loc.Get(DiplomaticActs.Name(act)) &&
                        full[act].Exists && button.Disabled == full[act].Disabled && button.TooltipText == full[act].Tip &&
                        button.Disabled == (refusal.Length > 0) && button.TooltipText.Contains(detail, StringComparison.Ordinal),
                        "quick cooperation matches full diplomacy, real refusal and price: " + caption + " " + act);
                }
                var available = DiplomaticActs.All.Where(act => DiplomaticActs.Refusal(c, player, target, act).Length == 0).ToArray();
                Check(available.Length > 0, "cooperation fixture has a sendable act: " + caption);
                if (available.Length == 0) continue;
                var selected = available[0];
                var buttonToSend = game.FindChild("CountryDiplomaticAct_" + selected, true, false) as Button;
                for (Node node = buttonToSend?.GetParent(); node is not null; node = node.GetParent())
                    if (node is ScrollContainer scroll) scroll.EnsureControlVisible(buttonToSend);
                await Wait(.2); await Capture("country-cooperation-" + language + "-" + size.X);
                var before = intercepted.Count;
                await ClickCountryCooperation(buttonToSend, "send to host " + caption);
                Check(intercepted.Count == before + 1 && intercepted[^1].IssuerId == player.Id &&
                    intercepted[^1].TargetId == target.Id && intercepted[^1].Act == selected &&
                    intercepted[^1].Step == DiplomaticActStep.Propose,
                    "quick cooperation emits exactly one authoritative command for its own card: " + caption);
                Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random,
                    "reading and transport-intercepted cooperation spend nothing and do not mutate the campaign: " + caption);
                game.Call("CloseMapSelection");
            }
            c.Commands.SubmitInterceptor = interceptor;
            if (interceptor is null)
            {
                game.VerificationInspectPick(new GlobePick(target, null, -1, target.CapitalLatitude, target.CapitalLongitude));
                await Wait(.2);
                await ClickCountryCooperation(game.FindChild("CountryPageActions", true, false) as Button, "human signature Actions");
                var act = DiplomaticActs.All.FirstOrDefault(kind => DiplomaticActs.Refusal(c, player, target, kind).Length == 0);
                var cash = (player.Treasury, target.Treasury);
                await ClickCountryCooperation(game.FindChild("CountryDiplomaticAct_" + act, true, false) as Button, "human signature proposal");
                Check(DiplomaticActs.Pending(c, relation) && relation.DiplomaticActBy == player.Id &&
                    relation.DiplomaticActKind == (int)act && cash == (player.Treasury, target.Treasury),
                    "a human partner receives the ordinary unsigned proposal before any cash moves");
                Check(DiplomaticActs.All.All(kind => (game.FindChild("CountryDiplomaticAct_" + kind, true, false) as Button) is { Disabled: true }),
                    "an unsigned proposal refreshes every quick cooperation gate");
            }
        }
        finally
        {
            c.Commands.SubmitInterceptor = interceptor;
            (player.Treasury, relation.Opinion, relation.Stance, target.IsHuman,
                relation.DiplomaticActBy, relation.DiplomaticActDay, relation.DiplomaticActKind, c.DealInboxRevision) = original;
            Loc.Language = languageBefore; GetTree().Root.Size = sizeBefore;
            game.Call("CloseMapSelection"); game.Call("CloseDiplomacyScreen"); game.Call("RefreshLocalisedText");
        }
    }
}
