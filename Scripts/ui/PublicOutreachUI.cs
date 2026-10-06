using System;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string _outreachSelectedAction = "public_dialogue";

    private Texture2D SocialIcon(string role, string fallback) =>
        Art("icon_" + CampaignUiCatalog.Icon(role, fallback), "");

    private HBoxContainer SocialMetric(string role, string fallback, string text, string name)
    {
        var row = new HBoxContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 7);
        row.AddChild(new TextureRect { Name = name + "Icon", Texture = SocialIcon(role, fallback),
            CustomMinimumSize = new Vector2(18, 18), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore });
        var value = RowLabel("", 13, Ui.Colour("text.body_soft"));
        value.Name = name + "Value"; value.Text = text;
        value.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(value);
        return row;
    }

    private void AddPublicOutreach(VBoxContainer panel, Nation target, Nation player)
    {
        var card = new PanelContainer { Name = "PublicOutreachCard" };
        card.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10));
        panel.AddChild(card);
        var body = new VBoxContainer { Name = "PublicOutreachBody" };
        body.AddThemeConstantOverride("separation", 7); card.AddChild(body);
        body.AddChild(SocialMetric("social.contact", "diplomacy", Loc.Get("PUBLIC CONTACT"), "PublicOutreachHeading"));
        var note = RowLabel("", 12, Ui.Colour("text.muted"));
        note.Name = "PublicOutreachNote"; note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.Text = Loc.Get("Fund public contact, not a guaranteed opinion. War or an embargo ends the programme without a refund.");
        body.AddChild(note);
        var existing = PublicOutreachActions.Existing(player, target);
        if (existing is not null)
        {
            var status = RowLabel("", 13, Ui.Colour(existing.Cancelled ? "state.warning" : "text.accent"));
            status.Name = "PublicOutreachStatus"; status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            status.Text = existing.Active
                ? Loc.Format("{0}: {1}/{2} days", Loc.Get(OutreachCatalog.Find(existing.ActionId)?.Name ?? existing.ActionId), existing.ProgressDays, existing.DurationDays)
                : Loc.Get(existing.Cancelled ? "Programme interrupted" : "Programme completed");
            body.AddChild(status);
            if (existing.Active)
            {
                var progress = new WarBar { Name = "PublicOutreachProgress", CustomMinimumSize = new Vector2(0, 14),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill, Track = Ui.Colour("war.track") };
                progress.Shares((existing.ProgressDays / (float)Math.Max(1, existing.DurationDays), Ui.Colour("text.accent")));
                body.AddChild(progress);
            }
            body.AddChild(SocialMetric("social.recovery", "calendar", Loc.Format("Next programme in {0} days",
                Math.Max(0, existing.CooldownUntilDay - _campaign.ElapsedDays)), "PublicOutreachRecovery"));
        }
        var actions = OutreachCatalog.All.ToArray();
        if (actions.Length == 0)
        {
            body.AddChild(RowLabel(Loc.Get("No public programmes are defined."), 13, Ui.Colour("text.muted")));
            return;
        }
        var choice = new OptionButton { Name = "PublicOutreachChoice", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34), FocusMode = FocusModeEnum.All, ExpandIcon = true };
        choice.AddThemeConstantOverride("icon_max_width", 20);
        foreach (var action in actions)
            choice.AddIconItem(SocialIcon("action." + action.Name, "diplomacy"), Loc.Get(action.Name));
        var selected = Math.Max(0, Array.FindIndex(actions, a => a.Id == _outreachSelectedAction));
        choice.Select(selected); body.AddChild(choice);
        var funding = SocialMetric("social.funding", "treasury", "", "PublicOutreachFunding");
        var timing = SocialMetric("social.duration", "clock", "", "PublicOutreachTiming");
        body.AddChild(funding); body.AddChild(timing);
        var feedback = RowLabel("", 12, Ui.Colour("text.muted"));
        feedback.Name = "PublicOutreachAvailability"; feedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(feedback);
        var start = ActionButton("Start programme", () =>
        {
            var action = actions[choice.Selected];
            _campaign.Commands.Enqueue(new StartPublicOutreachCommand { IssuerId = player.Id, TargetId = target.Id, ActionId = action.Id });
            _campaign.Commands.ApplyAll(_campaign); ForceDiplomacyRefresh();
            if (GodotObject.IsInstanceValid(_diplomacyLayer))
                (_diplomacyLayer.FindChild("PublicOutreachChoice", true, false) as Control)?.GrabFocus();
        }, 0, 34, true);
        start.Name = "PublicOutreachStart"; body.AddChild(start);
        void RefreshQuote()
        {
            var action = actions[choice.Selected]; _outreachSelectedAction = action.Id;
            var blocked = PublicOutreachActions.Blocked(_campaign, player, target, action);
            ((Label)funding.GetChild(1)).Text = Loc.Format("One-time funding: {0}", Fmt.Money(PublicOutreachActions.Cost(player, action)));
            ((Label)timing.GetChild(1)).Text = Loc.Format("Duration: {0} days · Recovery: {1} days", action.DurationDays, action.CooldownDays);
            start.Disabled = blocked.Length > 0;
            feedback.Text = blocked.Length > 0 ? Loc.Get(blocked) : Loc.Format("Active programmes: {0}/{1}",
                player.Outreach.Values.Count(p => p.Active), Math.Max(1, _campaign.Defines.PublicOutreachMaxActive));
            feedback.AddThemeColorOverride("font_color", Ui.Colour(blocked.Length > 0 ? "state.warning" : "text.muted"));
            start.TooltipText = Ui.Tip(Loc.Get(action.CultureImpulse > 0
                ? "Gradual contact with their society and each other's cultures; no instant status or alliance."
                : "Gradual contact with their society; no instant status or alliance."));
        }
        choice.ItemSelected += _ => RefreshQuote(); RefreshQuote();
    }
}
