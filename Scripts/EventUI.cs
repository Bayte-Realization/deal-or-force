using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _eventLayer = null!;
    private Label _eventTitle = null!;
    private Label _eventDescription = null!;
    private Button _eventOptionA = null!;
    private Button _eventOptionB = null!;
    private PendingCampaignEvent _shownEvent;

    private void BuildEventInterface()
    {
        _eventLayer = FullLayer("CampaignEvent");
        _eventLayer.Visible = false;
        _eventLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_eventLayer);
        var veil = new ColorRect { Color = new Color(0.01f, 0.02f, 0.04f, 0.66f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _eventLayer.AddChild(veil);
        var panel = TexturedPanel(_eventLayer);
        Ui.Place(panel, "modal.event");
        var rows = PanelRows(panel, 34, 30, 14);
        rows.AddChild(Title("EVENT", 12, Ui.Colour("text.caption")));
        _eventTitle = Title("", 28, Ui.Colour("text.accent"));
        rows.AddChild(_eventTitle);
        _eventDescription = Title("", 16, Ui.Colour("text.body_soft"));
        _eventDescription.SizeFlagsVertical = SizeFlags.ExpandFill;
        rows.AddChild(_eventDescription);
        _eventOptionA = ActionButton("", () => ResolveShownEvent(0), 520, 54, true);
        _eventOptionB = ActionButton("", () => ResolveShownEvent(1), 520, 54);
        rows.AddChild(_eventOptionA);
        rows.AddChild(_eventOptionB);
    }

    private void RefreshEventInterface()
    {
        if (_campaign?.Player is null || !_hudLayer.Visible) return;
        var pending = _campaign.PendingEvents.FirstOrDefault(item => item.NationId == _campaign.Player.Id);
        if (pending is null)
        {
            _eventLayer.Visible = false;
            _shownEvent = null;
            return;
        }
        if (_shownEvent == pending && _eventLayer.Visible) return;
        var newlyShown = _shownEvent != pending;
        _shownEvent = pending;
        _campaign.IsPaused = true;
        _pause.Text = "▶";
        _eventTitle.Text = pending.Title;
        _eventDescription.Text = pending.Description;
        _eventOptionA.Text = pending.Options[0].Title;
        _eventOptionA.TooltipText = Ui.Tip(pending.Options[0].Consequence);
        _eventOptionB.Text = pending.Options[1].Title;
        _eventOptionB.TooltipText = Ui.Tip(pending.Options[1].Consequence);
        _eventLayer.Visible = true;
        _eventLayer.MoveToFront();
        if (newlyShown) PlayStrategicCue("event_alert");
    }

    private void ResolveShownEvent(int option)
    {
        if (_shownEvent is null) return;
        _campaign.Commands.Enqueue(new ResolveCampaignEventCommand
        {
            IssuerId = _campaign.Player.Id, EventId = _shownEvent.Id, OptionIndex = option
        });
        _campaign.Commands.ApplyAll(_campaign);
        _eventLayer.Visible = false;
        _shownEvent = null;
        Refresh();
    }
}
