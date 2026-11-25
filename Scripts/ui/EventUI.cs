using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _eventLayer = null!;
    private Label _eventTitle = null!;
    private Label _eventDescription = null!;
    private TextureRect _eventPicture = null!;
    private ScrollContainer _eventOptionsScroll = null!;
    private VBoxContainer _eventOptions = null!;
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

        _eventPanel = TexturedPanel(_eventLayer, PanelSurface.Modal);
        Ui.Place(_eventPanel, "modal.event");
        _eventPanel.MouseFilter = MouseFilterEnum.Stop;
        var rows = PanelRows(_eventPanel, 34, 30, 14);

        // The title bar is the handle. Dragging anywhere else would fight the buttons.
        var grip = Title("EVENT  ·  drag to move", 12, Ui.Colour("text.caption"));
        grip.MouseFilter = MouseFilterEnum.Stop;
        grip.TooltipText = Ui.Tip(Tr("Drag this bar to move the window. The map underneath stays live."));
        grip.GuiInput += input => DragEventWindow(grip, input);
        rows.AddChild(grip);
        // The picture above the title (common/event_pictures), cut to a band; an event
        // without one keeps the plain window.
        _eventPicture = new TextureRect
        {
            Name = "EventPicture", ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(0, 170), MouseFilter = MouseFilterEnum.Ignore, Visible = false
        };
        rows.AddChild(_eventPicture);
        _eventTitle = Title("", 28, Ui.Colour("text.accent"));
        rows.AddChild(_eventTitle);
        _eventDescription = Title("", 16, Ui.Colour("text.body_soft"));
        _eventDescription.SizeFlagsVertical = SizeFlags.ExpandFill;
        rows.AddChild(_eventDescription);
        _eventOptionsScroll = new ScrollContainer
        {
            Name = "EventChoicesScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        rows.AddChild(_eventOptionsScroll);
        _eventOptions = new VBoxContainer { Name = "EventChoices", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _eventOptions.AddThemeConstantOverride("separation", 8);
        _eventOptionsScroll.AddChild(_eventOptions);
    }

    private void RefreshEventInterface()
    {
        var campaign = _campaign;
        if (campaign?.Player is null || !_hudLayer.Visible) return;
        if (!campaign.TryEnterRead(50, true)) return;
        try
        {
            var player = campaign.Player;
            if (player is null) return;
            // Nobody playing, nobody asked: the simulation answers what is on the table (EventSystem).
            var pending = campaign.NobodyPlays ? null : campaign.PendingEvents.FirstOrDefault(item => item.NationId == player.Id);
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
                campaign.IsPaused = true;
                _pause.Text = "▶";
            }
            // Through the table, every one of them. These strings come out of game/events as
            // values rather than as literals in the source, so nothing looked them up and the
            // window stayed English in a Russian game - while the tooltips beneath the buttons
            // were translated, because Ui.Tip does its own lookup.
            _eventTitle.Text = Loc.Get(pending.Title);
            _eventDescription.Text = Loc.Get(pending.Description);
            var picture = EventPictures.PathFor(EventCatalog.All.FirstOrDefault(d => d.Title == pending.Title));
            _eventPicture.Texture = picture.Length > 0 && ResourceLoader.Exists(picture) ? GD.Load<Texture2D>(picture) : null;
            _eventPicture.Visible = _eventPicture.Texture is not null;
            // Events and mods may offer one, two or many choices; never assume two.
            foreach (var child in _eventOptions.GetChildren())
            {
                _eventOptions.RemoveChild(child);
                child.QueueFree();
            }
            var isTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
            var choiceHeight = isTouch ? 64 : 58;
            for (var index = 0; index < pending.Options.Length; index++)
            {
                var captured = index;
                var option = pending.Options[index];
                var title = Loc.Get(option.Title);
                var summary = ScriptedEffects.InlineSummary(campaign, player, option.Effect, option.Modifiers);
                var button = ActionButton(option.Title, () => ResolveShownEvent(captured), 0, choiceHeight, index == 0);
                button.Name = $"EventChoice{index}";
                button.ClipContents = true;
                button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                if (!string.IsNullOrEmpty(summary))
                    AttachEventChoiceLabels(button, option.Title, title, summary, index == 0, isTouch);
                button.TooltipText = Ui.Tip(OptionTooltip(campaign, player, option));
                _eventOptions.AddChild(button);
            }
            _eventOptionsScroll.CustomMinimumSize = new Vector2(0,
                Math.Clamp(pending.Options.Length * (choiceHeight + 8) - 8, 0, isTouch ? 280 : 256));
            _eventOptionsScroll.ScrollVertical = 0;
            _eventLayer.Visible = true;
            _eventLayer.MoveToFront();
            if (newlyShown) PlayStrategicCue("event_alert");
        }
        finally
        {
            campaign.ExitRead();
        }
    }

    private static void AttachEventChoiceLabels(Button button, string titleKey, string titleText,
        string summary, bool primary, bool isTouch)
    {
        // Keep Button.Text equal to Loc.Get(option.Title) for accessibility/tests while drawing the
        // two-line choice title + inline income/GDP summary via Ui.Colour tokens without overlap.
        var hiddenText = Ui.Colour("text.body", 0f);
        button.AddThemeColorOverride("font_color", hiddenText);
        button.AddThemeColorOverride("font_hover_color", hiddenText);
        button.AddThemeColorOverride("font_pressed_color", hiddenText);
        button.AddThemeColorOverride("font_focus_color", hiddenText);
        button.AddThemeColorOverride("font_hover_pressed_color", hiddenText);
        button.AddThemeColorOverride("font_disabled_color", hiddenText);

        var hasIcon = button.Icon is not null;
        var align = hasIcon ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        var pad = new MarginContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true
        };
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        pad.AddThemeConstantOverride("margin_left", hasIcon ? 44 : 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 6);
        pad.AddThemeConstantOverride("margin_bottom", 6);
        button.AddChild(pad);

        var col = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true
        };
        col.AddThemeConstantOverride("separation", 2);
        pad.AddChild(col);

        var titleLbl = Fixed(Title(titleKey, isTouch ? 15 : 14, Ui.Colour(primary ? "text.accent" : "text.body")));
        titleLbl.Text = titleText;
        titleLbl.SetMeta(LocAppliedMeta, titleText);
        titleLbl.HorizontalAlignment = align;
        titleLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        titleLbl.MouseFilter = MouseFilterEnum.Ignore;
        col.AddChild(titleLbl);

        var summaryLbl = Fixed(Title(summary, isTouch ? 12 : 11, Ui.Colour("text.body_soft")));
        summaryLbl.RemoveMeta(LocSourceMeta);
        summaryLbl.RemoveMeta(LocAppliedMeta);
        summaryLbl.HorizontalAlignment = align;
        summaryLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        summaryLbl.MouseFilter = MouseFilterEnum.Ignore;
        col.AddChild(summaryLbl);
    }

    private void ResolveShownEvent(int option)
    {
        var shown = _shownEvent;
        var campaign = _campaign;
        if (shown is null || campaign?.Player is null) return;
        var playerId = campaign.Player.Id;
        var eventId = shown.Id;
        RunConsoleWrite(world =>
        {
            world.Commands.Enqueue(new ResolveCampaignEventCommand
            {
                IssuerId = playerId, EventId = eventId, OptionIndex = option
            });
            world.Commands.ApplyAll(world);
        });
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

    /// <summary>
    /// What choosing an option says it will do, and then what it will actually do: the
    /// prose first, and under it every number the choice moves, in the one format every
    /// tooltip in the game now uses.
    /// </summary>
    private static string OptionTooltip(Campaign campaign, Nation player, CampaignEventOption option)
    {
        var text = Loc.Get(option.Consequence);
        if (option.Effect is null && option.Modifiers.Length == 0) return text;

        var locked = campaign is not null && campaign.TryEnterRead(50, true);
        if (campaign is not null && !locked) return text;
        try
        {
            var effectLines = campaign is not null && player is not null
                ? ScriptedEffects.PreviewForNation(campaign, player, option.Effect)
                : ScriptedEffects.Lines(option.Effect);
            var lines = new List<string>(effectLines.Count + option.Modifiers.Length);
            lines.AddRange(effectLines);

            // Include option modifiers that are not already granted inside option.Effect.Grants.
            if (option.Modifiers.Length > 0)
            {
                var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (option.Effect is not null)
                    foreach (var (id, _) in option.Effect.Grants) granted.Add(id);
                var extraMods = option.Modifiers.Where(m => !granted.Contains(m.Id))
                    .Select(m => (m.Id, m.Days)).ToArray();
                if (extraMods.Length > 0)
                    lines.AddRange(ScriptedEffects.SpiritLines(extraMods));
            }

            if (lines.Count == 0) return text;
            return text + "\n\n" + Loc.Get("Effects:") + "\n" + string.Join("\n", lines.Select(line => "  " + line));
        }
        finally
        {
            if (locked) campaign!.ExitRead();
        }
    }
}
