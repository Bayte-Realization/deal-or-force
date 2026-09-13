using System;
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
    private ColorRect _eventVeil = null!;
    private PanelContainer _eventPanel = null!;
    private PendingCampaignEvent _shownEvent;

    /// <summary>Where the player last put the window, and the grab a drag is in.</summary>
    private Vector2? _eventPlaced;
    private bool _eventDragging;
    private Vector2 _eventGrab;

    private void BuildEventInterface()
    {
        _eventLayer = FullLayer("CampaignEvent");
        _eventLayer.Visible = false;
        // Ignore, not Stop. The map underneath stays clickable, so an event is something
        // sitting on the screen rather than something barring the way to it.
        _eventLayer.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_eventLayer);

        _eventVeil = new ColorRect
        {
            Color = new Color(0.01f, 0.02f, 0.04f, 0.66f),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _eventVeil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _eventLayer.AddChild(_eventVeil);

        _eventPanel = TexturedPanel(_eventLayer);
        Ui.Place(_eventPanel, "modal.event");
        _eventPanel.MouseFilter = MouseFilterEnum.Stop;
        var rows = PanelRows(_eventPanel, 34, 30, 14);

        // The title bar is the handle. Dragging anywhere else would fight the buttons.
        var grip = Title("EVENT  ·  drag to move", 12, Ui.Colour("text.caption"));
        grip.MouseFilter = MouseFilterEnum.Stop;
        grip.TooltipText = Ui.Tip(Tr("Drag this bar to move the window. The map underneath stays live."));
        grip.GuiInput += input => DragEventWindow(grip, input);
        rows.AddChild(grip);
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
        if (_eventPlaced is { } remembered)
        {
            Unpin(_eventPanel);
            PlaceEventWindow(remembered);
        }
        _shownEvent = pending;
        // Stopping time is a preference. In multiplayer it is the wrong default outright:
        // one player's event should not stop everybody else's war.
        if (SettingsCatalog.Flag("pause_on_event") && !_networkConnected)
        {
            _campaign.IsPaused = true;
            _pause.Text = "▶";
        }
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

    /// <summary>
    /// Move the window by its title bar, and remember where it was put. A window that goes
    /// back to the middle of the screen every time is a window nobody moves twice.
    /// </summary>
    private void DragEventWindow(Control grip, InputEvent input)
    {
        switch (input)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                if (button.Pressed)
                {
                    _eventGrab = button.GlobalPosition - _eventPanel.GlobalPosition;
                    // The panel is pinned to the middle of the screen by the skin, and a
                    // position set under those anchors is overwritten the next time the
                    // layout runs. Unpin it, keeping exactly where it already is, and from
                    // then on its position is its own.
                    Unpin(_eventPanel);
                }
                _eventDragging = button.Pressed;
                grip.AcceptEvent();
                return;

            case InputEventMouseMotion motion when _eventDragging:
                PlaceEventWindow(motion.GlobalPosition - _eventGrab);
                grip.AcceptEvent();
                return;
        }
    }

    /// <summary>
    /// Put the window somewhere, never far enough off the edge to lose the bar that drags
    /// it back, and remember it.
    /// </summary>
    private void PlaceEventWindow(Vector2 wanted)
    {
        var room = GetViewportRect().Size - _eventPanel.Size;
        var placed = new Vector2(
            Mathf.Clamp(wanted.X, 0f, MathF.Max(0f, room.X)),
            Mathf.Clamp(wanted.Y, 0f, MathF.Max(0f, room.Y)));
        _eventPanel.Position = placed;
        _eventPlaced = placed;
    }

    /// <summary>Take a control off its anchors without moving it.</summary>
    private static void Unpin(Control control)
    {
        var where = control.GlobalPosition;
        var size = control.Size;
        control.SetAnchorsPreset(LayoutPreset.TopLeft, false);
        control.Position = where;
        control.Size = size;
    }
}
