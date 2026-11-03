using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Headlines that come up as they happen (HeadlinePopups): a card under the top bar with the
/// country's flag, the sentence, why it concerns the player, and a button that shows the place.
///
/// Never a dialog: nothing stops, nothing must be answered. A card stays nine seconds, longer
/// while the mouse is on it, and the queue (HeadlineQueue) decides how often a new one may come.
/// </summary>
public partial class DealOrForceGame
{
    private const double HeadlineLifeSeconds = 9.0;
    private const double HeadlineFadeSeconds = 0.6;

    private VBoxContainer _headlineStack;
    private readonly HeadlineQueue _headlineQueue = new();
    private readonly List<(PanelContainer Card, double Age)> _headlineCards = new();
    private long _headlineSeen = -1;
    private Campaign _headlineWorld;
    private double _headlineClock;

    private void KeepHeadlinePopups(double delta)
    {
        if (_campaign?.Player is null || _hudLayer is not { Visible: true }) return;
        _headlineClock += delta;
        if (_headlineStack is null || !IsInstanceValid(_headlineStack)) BuildHeadlineStack();

        // A loaded or new campaign starts with what is already history, not with a backlog of it.
        if (!ReferenceEquals(_headlineWorld, _campaign))
        {
            _headlineWorld = _campaign;
            _headlineQueue.Clear();
            _headlineSeen = _campaign.Chronicle.Entries.Count > 0 ? _campaign.Chronicle.Entries[^1].Sequence : 0;
            foreach (var (card, _) in _headlineCards) if (IsInstanceValid(card)) card.QueueFree();
            _headlineCards.Clear();
        }

        var mode = SettingsCatalog.Chosen(HeadlinePopups.Setting);
        var newest = _headlineSeen;
        foreach (var line in NewsFeed.Lines(_campaign, _headlineSeen, 40))
        {
            if (line.Sequence > newest) newest = line.Sequence;
            var tie = HeadlinePopups.TieOf(_campaign, line);
            var importance = NewsFeed.Importance(line.Kind);
            if (!HeadlinePopups.Worth(mode, importance, tie,
                    tie == HeadlineTie.World && HeadlinePopups.InvolvesGreatPower(_campaign, line))) continue;
            _headlineQueue.Offer(line, tie, importance);
        }
        _headlineSeen = newest;

        if (_headlineQueue.Next(_headlineClock, _headlineCards.Count) is { } next) ShowHeadline(next);

        var mouse = GetViewport().GetMousePosition();
        for (var index = _headlineCards.Count - 1; index >= 0; index--)
        {
            var (card, age) = _headlineCards[index];
            if (!IsInstanceValid(card)) { _headlineCards.RemoveAt(index); continue; }
            // Read while the mouse is on it: it waits.
            if (!card.GetGlobalRect().HasPoint(mouse)) age += delta;
            _headlineCards[index] = (card, age);
            var left = HeadlineLifeSeconds - age;
            card.Modulate = card.Modulate with { A = Mathf.Clamp((float)(left / HeadlineFadeSeconds), 0f, 1f) };
            if (left > 0) continue;
            card.QueueFree();
            _headlineCards.RemoveAt(index);
        }
    }

    private void BuildHeadlineStack()
    {
        _headlineStack = new VBoxContainer { Name = "HeadlinePopups", MouseFilter = MouseFilterEnum.Ignore };
        _headlineStack.AddThemeConstantOverride("separation", 6);
        _headlineStack.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        var width = Ui.Metric("headline.width", 480f);
        _headlineStack.OffsetLeft = -width / 2f;
        _headlineStack.OffsetRight = width / 2f;
        _headlineStack.OffsetTop = Ui.Metric("headline.top", 142f);
        _hudLayer.AddChild(_headlineStack);
    }

    private void ShowHeadline(HeadlineQueue.Card next)
    {
        var line = next.Line;
        // The flag of the country the headline is about for this player (HeadlinePopups.Judge).
        var subject = HeadlinePopups.Judge(_campaign, line).Through;
        var other = subject;

        var card = new PanelContainer { Name = "Headline", MouseFilter = MouseFilterEnum.Stop };
        card.AddThemeStyleboxOverride("panel", ShellPanelStyle());
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        card.AddChild(row);
        if (subject is not null) row.AddChild(FlagBadge(subject.Tag, 34, subject.DisplayName));
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        text.AddThemeConstantOverride("separation", 2);
        row.AddChild(text);
        var reason = Title(HeadlinePopups.Reason(next.Tie, other?.DisplayName ?? ""), 11, Ui.Colour(next.Tie switch
        {
            HeadlineTie.Own => "text.accent",
            HeadlineTie.Enemy => "state.negative",
            HeadlineTie.Ally => "state.positive",
            HeadlineTie.Neighbour => "state.warning",
            _ => "text.caption"
        }));
        text.AddChild(reason);
        var sentence = next.Count > 1
            ? Simulation.Loc.Format("{0} (and {1} more like it)", line.Text, next.Count - 1)
            : line.Text;
        var headline = Title(sentence, 14, Ui.Colour("text.bright"));
        headline.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.AddChild(headline);
        var buttons = new VBoxContainer();
        buttons.AddThemeConstantOverride("separation", 4);
        row.AddChild(buttons);
        if (subject is not null)
        {
            var place = subject;
            buttons.AddChild(ActionButton("Show", () =>
            {
                _globe?.FocusOn(place.CapitalLatitude, place.CapitalLongitude, 9f);
                card.QueueFree();
            }, 64, 26));
        }
        buttons.AddChild(ActionButton("×", () => card.QueueFree(), 64, 26));
        _headlineStack.AddChild(card);
        _headlineCards.Add((card, 0d));
    }
}
