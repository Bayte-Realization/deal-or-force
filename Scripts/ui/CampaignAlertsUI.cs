using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The alert list under the date plate (docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md, step 4).
///
/// The owner's rule for this screen is that a problem is seen without hovering or clicking. The
/// hints used to be a strip of twelve pictures with four counters in the middle of the map and a
/// blue "Next" bar under it: what was wrong had to be hovered to be read. Now it is a short column
/// of words, always on screen, in the place the eye already goes for the date: the next step first
/// in the accent, then each alert as an icon, its caption with its count and a dot for how bad it is
/// (red must, amber should, green can, blue news), worst first. DEFCON and world tension are rows
/// like any other while their mechanics run, so DEFCON left the resource plate. What does not fit
/// is "+N more", one click to the whole list. A row opens what the hint opened before, and a right
/// click hides it for a while (CampaignAlertSnoozes), as before.
///
/// The ordering, the dots and the cut are AlertListRules, which the tests check without the engine.
/// The panel is `hud.alerts` (game/gui/10_campaign_shell.json); the situation report docks under it.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _alertListPanel, _campaignAlertPopup;
    private VBoxContainer _alertListRows, _campaignAlertList;
    private CampaignHintButton _alertNext;
    private Button _alertMore;
    private Label _campaignAlertHeading, _campaignAlertEmpty;
    private Button _campaignAlertRestore;
    private readonly Dictionary<string, CampaignHintButton> _alertListButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CampaignHintButton> _campaignAlertRows = new(StringComparer.Ordinal);
    private readonly CampaignAlertSnoozes _campaignAlertSnoozes = new();
    private IReadOnlyList<CampaignAlert> _campaignAlerts = Array.Empty<CampaignAlert>();
    private List<AlertLine> _alertLines = new();
    private AlertLine _alertNextLine;
    private Campaign _campaignAlertsWorld;
    private int _campaignAlertsSeat = -1;
    private bool _campaignAlertsShown;
    private (Campaign World, int Day, int Revision, float Tension) _worldTensionToday = (null, -1, -1, 0f);

    /// <summary>
    /// One line of the list: a hint of the player's own (CampaignAlerts), or a line about the world
    /// (DEFCON, world tension) that cannot be hidden and is never "Next" - it is not a thing to do.
    /// </summary>
    private sealed class AlertLine
    {
        public string Id = "", Caption = "", Category = "", Body = "", Glyph = "", Dot = "state.info";
        public int Rank;
        public bool World, Calm;
        public CampaignAlert Alert;
    }

    private static float AlertRowHeight => Mathf.Clamp(Ui.Metric("hud.alerts.row_height", 28f), 22f, 44f);

    private CampaignHintButton NewAlertRow(string name)
    {
        var row = new CampaignHintButton
        {
            Name = name, GlyphOnLeft = true, GlyphBox = 20, GlyphInset = 6, ClipText = true,
            Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, AlertRowHeight), MouseDefaultCursorShape = CursorShape.PointingHand
        };
        row.AddThemeFontSizeOverride("font_size", 13);
        return row;
    }

    private void BuildCampaignAlerts()
    {
        _alertListPanel = new PanelContainer { Name = "CampaignAlerts", MouseFilter = MouseFilterEnum.Stop, Visible = false };
        _hudLayer.AddChild(_alertListPanel);
        // The HUD frame first, so the list looks like its neighbours with materials off; the layout
        // key may then dress it as a plate when ui.materials is on.
        ApplyHudFrame(_alertListPanel);
        Ui.Place(_alertListPanel, "hud.alerts");
        _alertListPanel.GrowHorizontal = GrowDirection.Begin;
        var margin = Margin(_alertListPanel, 5, 4);
        _alertListRows = new VBoxContainer { Name = "CampaignAlertRows" };
        _alertListRows.AddThemeConstantOverride("separation", 2);
        margin.AddChild(_alertListRows);

        _alertNext = NewAlertRow("CampaignAlertNext");
        _alertNext.Pressed += FollowCampaignBrief;
        _alertNext.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
            _alertNext.AcceptEvent();
            if (_alertNextLine is { World: false } line) HideCampaignAlert(line.Id);
        };
        _alertListRows.AddChild(_alertNext);

        // A plain button, not ActionButton: its caption is a count rewritten on every refresh, and a
        // language change must not put a stale translation of an empty string on it in between.
        _alertMore = new Button
        {
            Name = "CampaignAlertMore", ClipText = true, FocusMode = FocusModeEnum.None,
            Alignment = HorizontalAlignment.Center, CustomMinimumSize = new Vector2(0, 22),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        StyleHudButton(_alertMore, false, true);
        _alertMore.AddThemeFontSizeOverride("font_size", 12);
        _alertMore.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
        _alertMore.Pressed += ToggleCampaignAlertList;
        _alertListRows.AddChild(_alertMore);

        // The whole list: every alert, nothing cut, and the way back for hidden ones.
        _campaignAlertPopup = new PanelContainer { Name = "CampaignAlertPopup", Visible = false };
        _campaignAlertPopup.AddThemeStyleboxOverride("panel", ShellPanelStyle()); _hudLayer.AddChild(_campaignAlertPopup);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 5); _campaignAlertPopup.AddChild(body);
        var head = new HBoxContainer(); body.AddChild(head);
        _campaignAlertHeading = Title("", 15, Ui.Colour("text.accent"));
        _campaignAlertHeading.SizeFlagsHorizontal = SizeFlags.ExpandFill; head.AddChild(_campaignAlertHeading);
        var close = ActionButton("×", () => _campaignAlertPopup.Hide(), 32, 28);
        close.Name = "CloseCampaignAlertList";
        close.TooltipText = Ui.Tip(Tr("Close the full list."));
        head.AddChild(close);
        var scroll = new ScrollContainer { Name = "CampaignAlertsScroll", CustomMinimumSize = new(0, 220),
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        body.AddChild(scroll);
        _campaignAlertList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _campaignAlertList.AddThemeConstantOverride("separation", 4); scroll.AddChild(_campaignAlertList);
        _campaignAlertEmpty = Title("", 13, Ui.Colour("text.body_soft")); _campaignAlertList.AddChild(_campaignAlertEmpty);
        _campaignAlertRestore = ActionButton("Show hidden hints", () =>
        { _campaignAlertSnoozes.Clear(); RefreshCampaignAlerts(_situations); }, 0, 28);
        _campaignAlertRestore.Name = "RestoreCampaignAlerts"; body.AddChild(_campaignAlertRestore);
    }

    /// <summary>
    /// Where the data puts the list (`hud.alerts`), pushed below the date plate and below a top bar
    /// that took a second row, as wide as the plate has grown, and as tall as its rows.
    /// </summary>
    private void LayoutCampaignAlerts()
    {
        if (_alertListPanel is null) return;
        Ui.Place(_alertListPanel, "hud.alerts");
        if (_alertListPanel.AnchorTop == 0f)
        {
            _alertListPanel.OffsetTop += _hudHeaderExtra;
            if (DatePlateSpan(out var plateLeft, out var plateRight, out var plateBottom) && _alertListPanel.AnchorLeft == 1f)
            {
                // Under the plate and as wide as it is: a longer date or a deals chip in another
                // language widens the plate leftwards, and the list follows its edge.
                _alertListPanel.OffsetTop = Mathf.Max(_alertListPanel.OffsetTop, plateBottom + DatePlateGap);
                _alertListPanel.OffsetLeft = Mathf.Min(_alertListPanel.OffsetLeft, plateLeft);
                _alertListPanel.OffsetRight = Mathf.Max(_alertListPanel.OffsetRight, plateRight);
            }
            _alertListPanel.AnchorBottom = 0f;
            _alertListPanel.OffsetBottom = _alertListPanel.OffsetTop + _alertListPanel.GetCombinedMinimumSize().Y;
        }
        if (_campaignAlertPopup is null) return;
        // The full list opens beside the short one, to its left, top-aligned: under it is the report.
        var width = MathF.Min(440f, MathF.Max(300f, _alertListPanel.Position.X - 120f));
        var x = MathF.Max(8f, _alertListPanel.Position.X - DatePlateGap - width);
        _campaignAlertPopup.Position = new Vector2(x, _alertListPanel.Position.Y);
        _campaignAlertPopup.Size = new Vector2(width, MathF.Min(460f, MathF.Max(260f, Size.Y - _campaignAlertPopup.Position.Y - 70f)));
    }

    /// <summary>
    /// A right-hand panel in the list's column starts under it: the situation report, and the desk
    /// when it is docked on the right. Its top follows the list's real bottom, so a short list gives
    /// the room back to the report; with the list hidden it starts where the list would.
    /// </summary>
    private void DockUnderAlertList(Control panel)
    {
        if (panel is null || _alertListPanel is null || !GodotObject.IsInstanceValid(panel) || panel.AnchorTop != 0f) return;
        var list = _alertListPanel.GetRect();
        var mine = panel.GetRect();
        if (mine.End.X <= list.Position.X || mine.Position.X >= list.End.X) return;
        var top = (_alertListPanel.Visible ? list.End.Y : list.Position.Y - DatePlateGap) + DatePlateGap;
        var height = panel.OffsetBottom - panel.OffsetTop;
        panel.OffsetTop = top;
        if (panel.AnchorBottom == 0f) panel.OffsetBottom = top + Mathf.Max(0f, height);
    }

    private void RefreshCampaignAlerts(IReadOnlyList<SituationItem> report)
    {
        if (_alertListPanel is null) BuildCampaignAlerts();
        if (IsNewShellActive)
        {
            if (_alertListPanel.Visible) _alertListPanel.Visible = false;
            if (_campaignAlertPopup is { Visible: true }) _campaignAlertPopup.Hide();
            return;
        }
        var viewerId = _campaign?.Player?.Id ?? -1;
        if (_campaignAlertsWorld != _campaign || _campaignAlertsSeat != viewerId)
        {
            _campaignAlertSnoozes.Clear(); _campaignAlertPopup.Hide();
            _campaignAlertAnnounced.Clear(); _campaignAlertAnnouncementsArmed = false;
            _campaignAlertsWorld = _campaign; _campaignAlertsSeat = viewerId;
        }
        _campaignAlerts = CampaignAlerts.Read(_campaign, _campaign.Player, report, _observerMode);
        _campaignAlertSnoozes.Reconcile(_campaignAlerts, _campaign.ElapsedDays);
        _campaignAlertsShown = !_observerMode && _campaign.Defines.CampaignAlertsEnabled;
        AnnounceCampaignAlertChanges();

        _alertLines = AlertLines();
        var (next, rows, more) = AlertListRules.Arrange(_alertLines, line => line.Rank, line => !line.World,
            AlertListRules.MaxRows(Ui.Metric("hud.alerts.max_rows", 6f)));
        _alertNextLine = next;
        var nextShown = RefreshAlertNext(next);
        RefreshAlertListRows(rows);
        var hidden = _campaignAlertSnoozes.Count;
        _alertMore.Visible = more > 0 || hidden > 0;
        _alertMore.Text = more > 0 ? Loc.Format("+{0} more", more) : Loc.Format("{0} hidden · show all", hidden);
        _alertMore.TooltipText = Ui.Tip(Tr("Open the full list of alerts, with the hidden ones and the way to bring them back."));
        _alertListRows.MoveChild(_alertMore, _alertListRows.GetChildCount() - 1);

        // Nothing to say is no plate at all, rather than an empty frame over the map.
        var wasShown = _alertListPanel.Visible;
        var bottom = _alertListPanel.GetRect().End.Y;
        // An observer has no hints but keeps the world's lines (DEFCON, tension), so the list stays.
        _alertListPanel.Visible = nextShown || rows.Length > 0 || _alertMore.Visible;
        if (!_alertListPanel.Visible && hidden == 0) _campaignAlertPopup.Hide();
        LayoutCampaignAlerts();
        if (_campaignAlertPopup.Visible) RefreshCampaignAlertRows();
        // The report under the list follows its bottom; it is placed again only when that moved.
        if (wasShown != _alertListPanel.Visible || !Mathf.IsEqualApprox(bottom, _alertListPanel.GetRect().End.Y))
            Callable.From(LayoutCampaignShell).CallDeferred();
    }

    /// <summary>
    /// The lines of the list: the world's first, then the player's visible hints, most urgent first.
    /// A rank keeps the order its lines come in, so the world's lines lead their rank: DEFCON 2 heads
    /// the must rows instead of trailing every offer, where "+N more" would cut it in exactly the
    /// crisis it reports. An observer gets the world's lines alone - the badge they replace was shown
    /// to observers too, and the world is what an observer is watching.
    /// </summary>
    private List<AlertLine> AlertLines()
    {
        var lines = new List<AlertLine>();
        // DEFCON stays a row even at 5: the badge it replaces was the only door to the ladder's own
        // screen, and a calm row at the foot of the list costs one line and says the world is quiet.
        // The level's own place is the foot of the date plate (WorldStripUI); here it is a row only
        // while it is news a government must act on: DEFCON 3 and worse.
        if (DefconSystem.Enabled(_campaign) && AlertListRules.WorldRow(AlertListRules.DefconRank(DefconSystem.CurrentLevel(_campaign))))
        {
            var level = DefconSystem.CurrentLevel(_campaign);
            var rank = AlertListRules.DefconRank(level);
            lines.Add(new AlertLine
            {
                Id = "world:defcon", World = true, Calm = rank == AlertListRules.Calm, Rank = rank,
                Caption = Tr(DefconSystem.LevelBadge(level)), Category = Tr("The world"),
                Body = DefconSystem.FormatSummary(_campaign) + "\n\n" + Tr("Click for the ladder, the nuclear powers and our posture."),
                Glyph = "alert_battle", Dot = DefconSystem.LevelColourToken(level)
            });
        }
        // World tension only once it is worth a line: a calm number every day would be a row that
        // means nothing. The figure is the conflicts screen's ("The world" tab), where the row goes.
        if (MechanicFeatures.SystemEnabled(_campaign, nameof(WorldWar)))
        {
            var tension = WorldTensionToday();
            var rank = AlertListRules.TensionRank(tension);
            // The figure itself stands on the date plate; a row only from "dangerous" up.
            if (AlertListRules.WorldRow(rank))
                lines.Add(new AlertLine
                {
                    Id = "world:tension", World = true, Rank = rank,
                    Caption = Loc.Format("World tension {0} · {1}", Mathf.RoundToInt(tension),
                        Tr(tension >= 80f ? "on the brink" : tension >= 60f ? "dangerous" : "tense")),
                    Category = Tr("The world"),
                    Body = Tr("How close the great powers are to war with one another. The worst rivalry counts in full, the next two in part. When it runs high, incidents between the blocs become likelier.") +
                           "\n\n" + Tr("Click for the great powers' rivalries."),
                    Glyph = "organisations", Dot = AlertListRules.TensionDot(tension)
                });
        }
        if (_campaignAlertsShown)
            // The reader sorts a group by id; the urgency puts a strike or a stopped treasury above
            // three trade offers, and the offer that expires first above the rest.
            foreach (var alert in _campaignAlerts.Where(a => !_campaignAlertSnoozes.Hidden(a, _campaign.ElapsedDays))
                         .OrderBy(a => AlertListRules.Urgency(a)))
            {
                var explanation = alert.Situation is { } situation
                    ? "\n\n" + string.Join("\n", situation.Facts.Take(3).Select(f => f.Label + ": " + f.Value)) +
                      "\n\n" + string.Join("\n", situation.Consequences.Take(1))
                    : "";
                lines.Add(new AlertLine
                {
                    Id = alert.Id, Alert = alert, Caption = AlertCaption(alert), Category = Tr(CampaignAlerts.GroupName(alert.Group)),
                    Body = alert.Summary + explanation, Glyph = CampaignHintIcon(alert),
                    Rank = AlertListRules.Rank(alert.Group), Dot = AlertListRules.Dot(alert.Group)
                });
            }
        return FoldAlike(lines);
    }

    /// <summary>
    /// Two or more rows of one kind (AlertListRules.GroupKey) become one, standing where the first
    /// of them stood: how many, and the first three by name. The full list still has each of them,
    /// and that is where a click on the folded row goes.
    /// </summary>
    private List<AlertLine> FoldAlike(List<AlertLine> lines)
    {
        var groups = new Dictionary<string, List<AlertLine>>();
        foreach (var line in lines)
        {
            var key = AlertListRules.GroupKey(line.Id, line.Rank);
            if (key.Length == 0) continue;
            if (!groups.TryGetValue(key, out var members)) groups[key] = members = new List<AlertLine>();
            members.Add(line);
        }
        if (groups.Values.All(members => members.Count < 2)) return lines;

        static string Name(AlertLine member)
        {
            var at = member.Caption.IndexOf(": ", StringComparison.Ordinal);
            return at >= 0 ? member.Caption[(at + 2)..] : member.Caption;
        }
        var folded = new List<AlertLine>(lines.Count);
        var done = new HashSet<string>();
        foreach (var line in lines)
        {
            var key = AlertListRules.GroupKey(line.Id, line.Rank);
            if (key.Length == 0 || groups[key].Count < 2) { folded.Add(line); continue; }
            if (!done.Add(key)) continue;
            var members = groups[key];
            folded.Add(new AlertLine
            {
                Id = key, Category = members[0].Category, Glyph = members[0].Glyph, Rank = members[0].Rank, Dot = members[0].Dot,
                Caption = Loc.Format("Shortages · {0}: {1}", members.Count,
                    string.Join(", ", members.Take(3).Select(Name)) + (members.Count > 3 ? " …" : "")),
                Body = string.Join("\n", members.Select(member => member.Caption)) + "\n\n" + Tr("Click for each of them.")
            });
        }
        return folded;
    }

    /// <summary>
    /// What a row says. A war is named: three rows reading "War in progress" told a country at
    /// war with three neighbours nothing about which row was which.
    /// </summary>
    private string AlertCaption(CampaignAlert alert)
    {
        if (alert.Route != CampaignAlertRoute.War) return alert.Title;
        var war = _campaign.Wars.FirstOrDefault(w => w.Id == alert.TargetId);
        var name = war is null ? "" : WarNames.Of(_campaign, war);
        return string.IsNullOrWhiteSpace(name) ? alert.Title : name;
    }

    /// <summary>
    /// World tension is read off every pair of great powers; once a day is often enough for a row.
    /// The world is part of the key: a quick-load on the same day and map revision is another world.
    /// </summary>
    private float WorldTensionToday()
    {
        if (_worldTensionToday.World != _campaign || _worldTensionToday.Day != _campaign.ElapsedDays ||
            _worldTensionToday.Revision != _campaign.MapRevision)
            _worldTensionToday = (_campaign, _campaign.ElapsedDays, _campaign.MapRevision, WorldWar.Tension(_campaign));
        return _worldTensionToday.Tension;
    }

    private string AlertTip(AlertLine line, bool lead = false) =>
        line.Category + "\n" + line.Caption + "\n\n" + line.Body + "\n\n" +
        (lead ? Tr("This is the most pressing thing waiting. Press to go there; the rows below list the rest.") + "\n" : "") +
        (line.World ? Tr("Left-click: open.") : Tr("Left-click: open. Right-click: hide temporarily (urgent hints return tomorrow)."));

    /// <summary>
    /// The first row: the most pressing hint, in words, in the accent. With nothing waiting on a
    /// paused game it offers the one thing that is always left to do, which is to let time run.
    /// </summary>
    private bool RefreshAlertNext(AlertLine next)
    {
        if (next is not null)
        {
            _alertNext.Text = Loc.Format("Next: {0}  →", next.Caption);
            _alertNext.TooltipText = Ui.Tip(AlertTip(next, lead: true));
            _alertNext.AccessibilityName = next.Caption + ". " + next.Body;
            _alertNext.Present(CampaignHintArt.Of(next.Glyph), Ui.Colour("button.primary_border"));
            _alertNext.Mark(Ui.Colour(next.Dot));
        }
        else if (_campaignAlertsShown && _campaign.IsPaused)
        {
            _alertNext.Text = Tr("Nothing is waiting for you. Let time run  [Space]");
            _alertNext.TooltipText = Ui.Tip(Tr("The game is paused. Press to let time run; press Space to stop it again."));
            _alertNext.AccessibilityName = _alertNext.Text;
            _alertNext.Present(null, Ui.Colour("button.primary_border"));
            _alertNext.Mark(Colors.Transparent);
        }
        else
        {
            _alertNext.Visible = false;
            return false;
        }
        StyleAlertRow(_alertNext, "button.primary_border", lead: true, glyph: _alertNext.Glyph is not null);
        _alertNext.Visible = true;
        _alertListRows.MoveChild(_alertNext, 0);
        return true;
    }

    /// <summary>
    /// The ids a row may be kept for: every hint the country has today, hidden or not, and the world
    /// lines. A hidden or cut row is kept and hidden, so it comes back as the same control.
    /// </summary>
    private HashSet<string> AlertIdsAlive()
    {
        var alive = _campaignAlerts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        alive.UnionWith(_alertLines.Select(line => line.Id));
        return alive;
    }

    private void RefreshAlertListRows(AlertLine[] rows)
    {
        var alive = AlertIdsAlive();
        foreach (var id in _alertListButtons.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            var old = _alertListButtons[id];
            _alertListRows.RemoveChild(old); old.QueueFree(); _alertListButtons.Remove(id);
        }
        foreach (var old in _alertListButtons.Values) old.Visible = false;
        var index = 1;
        foreach (var line in rows)
        {
            if (!_alertListButtons.TryGetValue(line.Id, out var button))
            {
                var id = line.Id;
                button = NewAlertRow("CampaignAlertRow_" + id.Replace(':', '_'));
                button.Pressed += () => FollowAlertLine(id);
                button.GuiInput += input =>
                {
                    if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
                    button.AcceptEvent();
                    if (!id.StartsWith("world:", StringComparison.Ordinal)) HideCampaignAlert(id);
                };
                _alertListButtons[id] = button;
                _alertListRows.AddChild(button);
            }
            button.Text = line.Caption;
            button.TooltipText = Ui.Tip(AlertTip(line));
            button.AccessibilityName = line.Caption + ". " + line.Body;
            button.Present(CampaignHintArt.Of(line.Glyph), Ui.Colour(line.Dot));
            button.Mark(Ui.Colour(line.Dot));
            StyleAlertRow(button, line.Dot, calm: line.Calm);
            button.Visible = true;
            _alertListRows.MoveChild(button, index++);
        }
    }

    /// <summary>
    /// A row is a quiet line until the pointer is on it; the dot carries the severity. The lead row
    /// ("Next") is the one accented control: an ink-blue edge and a stripe down its left side.
    /// </summary>
    private static void StyleAlertRow(Button button, string token, bool lead = false, bool calm = false, bool glyph = true)
    {
        var key = token + (lead ? "|lead" : "") + (calm ? "|calm" : "") + (glyph ? "|glyph" : "");
        if (button.HasMeta("alert_row_style") && button.GetMeta("alert_row_style").AsString() == key) return;
        button.SetMeta("alert_row_style", key);
        // Room for the 20-pixel glyph that starts 6 from the edge, or just a margin without one.
        var glyphRoom = glyph ? 32 : 10;
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "hover_pressed" })
        {
            var resting = state is "normal" or "focus";
            var style = new StyleBoxFlat
            {
                BgColor = lead ? Ui.Colour("button.primary_fill", resting ? .55f : .85f)
                    : Ui.Colour(resting ? "surface.inset" : "war.card", resting ? .55f : .9f),
                BorderColor = lead ? Ui.Colour("button.primary_border", resting ? .85f : 1f)
                    : Ui.Colour(resting ? "surface.line_faint" : token, resting ? .5f : .9f),
                BorderWidthLeft = lead ? 3 : 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
                ContentMarginLeft = glyphRoom, ContentMarginRight = 22, ContentMarginTop = 2, ContentMarginBottom = 2
            };
            style.SetCornerRadiusAll(3);
            button.AddThemeStyleboxOverride(state, style);
        }
        var text = Ui.Colour(lead ? "text.accent" : calm ? "text.muted" : "text.body");
        button.AddThemeColorOverride("font_color", text);
        button.AddThemeColorOverride("font_hover_color", Ui.Colour("text.bright"));
        button.AddThemeColorOverride("font_pressed_color", text);
        button.AddThemeColorOverride("font_focus_color", text);
    }

    private void FollowCampaignBrief()
    {
        if (_alertNextLine is { } line) FollowAlertLine(line.Id);
        else if (_campaign is { IsPaused: true }) TogglePause();
        RefreshSituationRoom(force: true);
    }

    /// <summary>A world line opens its own screen; a hint goes where it always went.</summary>
    private void FollowAlertLine(string id)
    {
        _campaignAlertPopup.Hide();
        switch (id)
        {
            case "world:defcon": OpenDefconOverview(); UpdateScreenButtons(); return;
            // The world's page, not the last rivalry looked at: closing the screen keeps the
            // selection, and on tab 5 a remembered pair would open that pair's page instead.
            case "world:tension": _conflictTab = 5; _conflictPair = -1; _conflictWar = -1; OpenConflictsScreen(); UpdateScreenButtons(); return;
            // A folded row stands for several alerts: the full list has each of them.
            case var group when group.StartsWith("group:", StringComparison.Ordinal): ToggleCampaignAlertList(); return;
            default: FollowCampaignAlert(id); return;
        }
    }

    /// <summary>"+N more": the full list beside the short one, and the same button closes it.</summary>
    private void ToggleCampaignAlertList()
    {
        if (_campaignAlertPopup.Visible) { _campaignAlertPopup.Hide(); return; }
        _campaignAlertPopup.Show();
        RefreshCampaignAlertRows();
        LayoutCampaignAlerts();
        _campaignAlertPopup.MoveToFront();
    }

    private static string CampaignHintIcon(CampaignAlert a) => a.Situation?.Good is { } good ? CommodityCatalog.IconNameOf(good) : a.Route switch
    {
        CampaignAlertRoute.Event => "alert_event", CampaignAlertRoute.Elections => "elections", CampaignAlertRoute.Personnel => "personnel",
        CampaignAlertRoute.Research => "research", CampaignAlertRoute.Decisions => "decision",
        CampaignAlertRoute.Battle => "alert_battle", CampaignAlertRoute.War => "wars", CampaignAlertRoute.Defcon => "alert_battle",
        CampaignAlertRoute.Doctrine or CampaignAlertRoute.Army => "wars",
        _ => a.Situation?.Route switch
        {
            SituationRoute.Finance => "treasury", SituationRoute.Market => "trade", SituationRoute.Army => "alert_supply",
            SituationRoute.Construction => "alert_construction", SituationRoute.PeaceOffer => "alert_peace",
            SituationRoute.Crises => "unrest", _ => "diplomacy"
        }
    };

    private void HideCampaignAlert(string id)
    {
        var current = _campaignAlerts.FirstOrDefault(a => a.Id == id);
        if (current is not null) _campaignAlertSnoozes.Hide(current, _campaign.ElapsedDays, _campaign.Defines.CampaignAlertSnoozeDays);
        RefreshCampaignAlerts(_situations);
    }

    // A hint that arrives speaks once, the way a courier does: the urgent group gets the
    // situation horn, the advisory one the softer paper cue. Nothing plays on the first
    // refresh after a load or a seat change, and a hint that was merely snoozed is not news.
    private readonly HashSet<string> _campaignAlertAnnounced = new(StringComparer.Ordinal);
    private bool _campaignAlertAnnouncementsArmed;

    private void AnnounceCampaignAlertChanges()
    {
        if (!_campaignAlertsShown) { _campaignAlertAnnounced.Clear(); _campaignAlertAnnouncementsArmed = false; return; }
        var seen = _campaignAlertAnnounced;
        seen.IntersectWith(_campaignAlerts.Select(a=>a.Id));
        string urgent = null, advisory = null;
        foreach (var alert in _campaignAlerts)
        {
            if (alert.Group is not (CampaignAlertGroup.Must or CampaignAlertGroup.Should)) continue;
            if (!seen.Add(alert.Id)) continue;
            if (_campaignAlertSnoozes.Hidden(alert, _campaign.ElapsedDays)) continue;
            if (alert.Group == CampaignAlertGroup.Must) urgent ??= alert.Id;
            else advisory ??= alert.Id;
        }
        if (!_campaignAlertAnnouncementsArmed)
        {
            // First refresh of a seat: everything is old news. Remember it, stay quiet.
            _campaignAlertAnnouncementsArmed = true;
            return;
        }
        if (urgent is not null) PlayStrategicCue("situation_alert");
        else if (advisory is not null) PlayStrategicCue("deal_received");
    }

    /// <summary>The full list: every line, worst first, nothing cut; hidden hints come back from here.</summary>
    private void RefreshCampaignAlertRows()
    {
        var all = AlertListRules.Arrange(_alertLines, line => line.Rank, _ => false, int.MaxValue).Rows;
        _campaignAlertHeading.Text = Loc.Format("All alerts ({0})", all.Length);
        var alive = AlertIdsAlive();
        foreach (var id in _campaignAlertRows.Keys.Where(id => !alive.Contains(id)).ToArray())
        { var old = _campaignAlertRows[id]; _campaignAlertList.RemoveChild(old); old.QueueFree(); _campaignAlertRows.Remove(id); }
        foreach (var old in _campaignAlertRows.Values) old.Hide();
        var index = 0;
        foreach (var line in all)
        {
            if (!_campaignAlertRows.TryGetValue(line.Id, out var button))
            {
                var id = line.Id;
                button = new CampaignHintButton { CustomMinimumSize = new(0,48), GlyphOnLeft = true };
                button.Pressed += () => FollowAlertLine(id);
                button.Name = "CampaignHint_" + id.Replace(':', '_');
                button.SizeFlagsHorizontal = SizeFlags.ExpandFill; button.Alignment = HorizontalAlignment.Left;
                button.ClipText = true;
                button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                button.GuiInput += input =>
                {
                    if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
                    button.AcceptEvent();
                    if (!id.StartsWith("world:", StringComparison.Ordinal)) HideCampaignAlert(id);
                };
                _campaignAlertRows.Add(id, button); _campaignAlertList.AddChild(button);
            }
            button.Text = line.Caption; button.AccessibilityName = line.Caption + ". " + line.Body;
            button.TooltipText = Ui.Tip(AlertTip(line));
            button.Present(CampaignHintArt.Of(line.Glyph), Ui.Colour(line.Dot));
            button.Mark(Ui.Colour(line.Dot));
            StyleCampaignHint(button, line.Dot);
            button.Visible = true; _campaignAlertList.MoveChild(button, index++);
        }
        _campaignAlertEmpty.Text = Tr("No alerts are showing. Hidden ones come back below."); _campaignAlertEmpty.Visible = index == 0;
        _campaignAlertList.MoveChild(_campaignAlertEmpty, index);
        _campaignAlertRestore.Text = Loc.Format("Show hidden hints · {0}", _campaignAlertSnoozes.Count);
        _campaignAlertRestore.Disabled = _campaignAlertSnoozes.Count == 0;
    }

    private static void StyleCampaignHint(Button button, string token)
    {
        if (button.GetThemeStylebox("normal") is StyleBoxFlat current &&
            current.BorderColor == Ui.Colour(token,.65f) && current.BgColor == Ui.Colour("shell.background") &&
            current.ContentMarginLeft == 50) return;
        foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
        {
            var style = new StyleBoxFlat { BgColor = Ui.Colour(state == "normal" ? "shell.background" : "war.card"),
                BorderColor = Ui.Colour(token, state == "normal" ? .65f : 1),
                BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = state == "normal" ? 1 : 2, BorderWidthBottom = 2 };
            style.SetCornerRadiusAll(4); style.SetContentMarginAll(5);
            style.ContentMarginLeft = 50; style.ContentMarginRight = 24;
            button.AddThemeStyleboxOverride(state, style);
        }
    }

    private void FollowCampaignAlert(string id)
    {
        // Never execute the action attached to an old card. Navigation is not consent
        // to send a territorial offer, accept an election or spend political credit.
        var alert = CampaignAlerts.Read(_campaign, _campaign.Player, observer: _observerMode).FirstOrDefault(a => a.Id == id);
        if (alert is null) { RefreshSituationRoom(force: true); return; }
        _campaignAlertPopup.Hide();
        switch (alert.Route)
        {
            case CampaignAlertRoute.Situation when id == "construction_frozen":
                OpenScreen("Economy"); // the construction queue and the budget are both there
                break;
            case CampaignAlertRoute.Situation:
                if (_situationLayer is null) BuildSituationRoom();
                if (alert.Situation is null) { OpenSituationRoom(); break; }
                if (!SituationDestinationEnabled(alert.Situation)) { OpenSituationRoom(); break; }
                if (id is "detached_land" or "enclave_swap" or "border_pore")
                { OpenSituationRoom(); _situationSelected = id; RefreshSituationList(); break; }
                _situationSelected = id; FollowSituation(); break;
            case CampaignAlertRoute.Event: RefreshEventInterface(); break;
            case CampaignAlertRoute.Elections:
            case CampaignAlertRoute.Personnel: if (MechanicFeatures.ScreenEnabled(_campaign, "State Affairs")) OpenPoliticalAppointments(); break;
            case CampaignAlertRoute.Research: if (_campaign.FeatureEnabled("research")) OpenResearchScreen(); break;
            case CampaignAlertRoute.Doctrine: OpenDoctrineScreen(); break;
            case CampaignAlertRoute.Army: OpenScreen("Military"); break;
            case CampaignAlertRoute.Decisions: OpenScreen("Decisions"); break;
            case CampaignAlertRoute.Battle: OpenFirstPlayerBattle(); break;
            case CampaignAlertRoute.War: OpenWarLedger(); ShowWarLedgerDetail(alert.TargetId); break;
            case CampaignAlertRoute.Defcon: OpenDefconOverview(); break;
        }
        UpdateScreenButtons();
    }
}
