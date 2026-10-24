using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The stop-press band: epochal world news as a headline, not a rail row.
///
/// The right rail shows the world three lines at a time, forty-eight pixels tall, beside
/// everything else that competes for a glance. A war declared, a country born, a default -
/// the news the game is named for - is asked to survive in that column, and does not. So
/// the chronicle's importance-3 entries stop the presses: one band under the top bar, a
/// masthead with the campaign day, the chronicle's own sentence as the headline in the
/// large type, and what the player's exchanges are doing about it underneath. It stands
/// in the hints strip's place for its nine seconds - two announcements must not fight for
/// one slot - then gives the strip back. A click flies to where it happened; the facts a
/// rail tooltip would carry are in the band's tooltip.
///
/// What was already in the chronicle when the game opened is not news, and neither is
/// anything below importance 3: those keep their rail row and their sting, and nothing
/// here adds a sound of its own.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _teletypeBand;
    private Label _teletypeMasthead, _teletypeHeadline, _teletypeMarkets;
    private Button _teletypeDismiss;
    private Tween _teletypeTween;
    private readonly List<NewsFeed.Line> _teletypeQueue = new();
    private NewsFeed.Line _teletypeShowing;
    private long _teletypeSeen = long.MinValue;
    private Campaign _teletypeWorld;
    private int _teletypeSeat = -1;

    /// <summary>How long the presses stay stopped, in seconds, before the band steps aside.</summary>
    private const float TeletypeShowSeconds = 9f;

    /// <summary>
    /// Called from the strategic HUD refresh, which already runs whenever the chronicle
    /// grows. Reads what is new since the last look, keeps the epochal part, and shows at
    /// most what the queue holds - never more than three, because a world that declares
    /// four wars in one day is telling the player to open the situation room, not to read.
    /// </summary>
    private void RefreshTeletype()
    {
        if (_campaign?.Player is null)
        {
            HideTeletype();
            return;
        }

        // What was on the presses when this seat took over is the old edition, not news.
        if (_teletypeWorld != _campaign || _teletypeSeat != _campaign.Player.Id)
        {
            _teletypeWorld = _campaign;
            _teletypeSeat = _campaign.Player.Id;
            _teletypeSeen = _campaign.Chronicle.Entries.Count > 0
                ? _campaign.Chronicle.Entries[^1].Sequence
                : 0;
            _teletypeQueue.Clear();
            HideTeletype();
        }

        var fresh = NewsFeed.Lines(_campaign, _teletypeSeen)
            .Where(line => NewsFeed.Importance(line.Kind) >= 3)
            .Take(3 - _teletypeQueue.Count)
            .ToList();
        // Lines arrive newest first; the newest leads.
        foreach (var line in fresh) _teletypeQueue.Insert(0, line);
        _teletypeSeen = _campaign.Chronicle.Entries.Count > 0
            ? _campaign.Chronicle.Entries[^1].Sequence
            : 0;

        if (_teletypeBand is { Visible: false } && _teletypeQueue.Count > 0)
            ShowTeletype(_teletypeQueue[0]);
    }

    private void ShowTeletype(NewsFeed.Line line)
    {
        _teletypeQueue.RemoveAt(0);
        if (_teletypeBand is null)
        {
            _teletypeBand = new PanelContainer
            {
                Name = "TeletypeBand",
                MouseFilter = Control.MouseFilterEnum.Stop,
                Visible = false
            };
            _teletypeBand.AddThemeStyleboxOverride("panel", ShellPanelStyle());
            _teletypeBand.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                    FocusNews(_teletypeShowing);
            };
            _hudLayer.AddChild(_teletypeBand);
            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", 2);
            _teletypeBand.AddChild(body);

            var mast = new HBoxContainer();
            body.AddChild(mast);
            _teletypeMasthead = Title("", 12, Ui.Colour("text.accent"), true);
            _teletypeMasthead.Name = "TeletypeMasthead";
            _teletypeMasthead.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            mast.AddChild(_teletypeMasthead);
            _teletypeDismiss = ActionButton("×", DismissTeletype, 30, 26);
            _teletypeDismiss.Name = "TeletypeDismiss";
            mast.AddChild(_teletypeDismiss);

            _teletypeHeadline = Title("", 16, Ui.Colour("text.body"));
            _teletypeHeadline.Name = "TeletypeHeadline";
            _teletypeHeadline.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _teletypeHeadline.CustomMinimumSize = new Vector2(0, 40);
            body.AddChild(_teletypeHeadline);

            _teletypeMarkets = Title("", 12, Ui.Colour("text.body_soft"));
            _teletypeMarkets.Name = "TeletypeMarkets";
            _teletypeMarkets.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _teletypeMarkets.MaxLinesVisible = 2;
            body.AddChild(_teletypeMarkets);
        }

        _teletypeShowing = line;
        _teletypeMasthead.Text = Loc.Get("Stop press") + "  ·  " + Loc.Format("Campaign day {0}", line.Day);
        _teletypeHeadline.Text = line.Text;
        var facts = NewsFeed.Context(_campaign, line);
        _teletypeBand.TooltipText = Ui.Tip((facts.Length > 0 ? facts + "\n" : "") + Tr("Show it on the map."));
        _teletypeMarkets.Text = TeletypeMarketLine();

        // The band takes the hints strip's slot for its seconds; two announcements must
        // not fight over one place on the screen.
        if (_campaignAlertStrip is { Visible: true }) _campaignAlertStrip.Hide();
        _teletypeBand.MoveToFront();
        _teletypeBand.Visible = true;
        LayoutTeletype();

        _teletypeTween?.Kill();
        _teletypeTween = CreateTween();
        _teletypeBand.Modulate = new Color(1f, 1f, 1f, 0f);
        _teletypeTween.TweenProperty(_teletypeBand, "modulate", Colors.White, 0.25f);
        _teletypeTween.TweenInterval(TeletypeShowSeconds);
        _teletypeTween.TweenProperty(_teletypeBand, "modulate", new Color(1f, 1f, 1f, 0f), 0.4f);
        _teletypeTween.TweenCallback(Callable.From(DismissTeletype));
    }

    /// <summary>
    /// What the player's own exchanges are doing with the news: the two prices furthest
    /// from their base. A pure read of the quotes the weekly market already wrote - this
    /// band reports the world, it does not move prices to look informed.
    /// </summary>
    private string TeletypeMarketLine()
    {
        var movers = _campaign.Player.Market.Values
            .Select(quote => (Quote: quote,
                Move: quote.Price / MathF.Max(0.01f, CommodityCatalog.Get(quote.Commodity).BasePrice) - 1f))
            .Where(entry => MathF.Abs(entry.Move) >= 0.02f)
            .OrderByDescending(entry => MathF.Abs(entry.Move))
            .Take(2)
            .Select(entry => $"{Tr(CommodityCatalog.Get(entry.Quote.Commodity).Name)} {Fmt.PercentDelta(entry.Move)}")
            .ToList();
        if (movers.Count == 0) return Loc.Get("The exchanges are quiet.");
        return Loc.Format("Markets: {0}", string.Join("  ·  ", movers));
    }

    private void DismissTeletype()
    {
        if (_teletypeBand is null) return;
        _teletypeTween?.Kill();
        _teletypeTween = null;
        HideTeletype();
        if (_teletypeQueue.Count > 0) ShowTeletype(_teletypeQueue[0]);
    }

    private void HideTeletype()
    {
        if (_teletypeBand is not null) _teletypeBand.Visible = false;
        _teletypeShowing = null;
        // The strip's own refresh sets the same condition; this is only the give-back after
        // the band borrowed its slot, so it must not invent a different verdict.
        if (_campaignAlertStrip is not null && _campaign is not null && !_observerMode &&
            _campaign.Defines.CampaignAlertsEnabled)
            _campaignAlertStrip.Show();
    }

    /// <summary>The hints strip's slot: centred under the top bar, as wide as it needs and no wider.</summary>
    private void LayoutTeletype()
    {
        if (_teletypeBand is not { Visible: true }) return;
        var width = MathF.Min(700f, MathF.Max(360f, Size.X - 180f));
        _teletypeBand.Position = new((Size.X - width) * 0.5f, _hudTopPanel.OffsetBottom + 6f);
        _teletypeBand.Size = new(width, 0f);
    }
}
