using System.Collections.Generic;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// The top bar of somebody watching the world rather than playing a country. It used to keep the
/// figures of whichever country the picker had last shown - South Korea's treasury above a world
/// nobody was steering. Now it reads the world: what it produces, how many live in it, how many
/// countries and wars it has, how settled it is on average and how many divisions stand in it.
/// The country's places are borrowed through the same TopBarAs the movement bar uses, so leaving
/// observer mode gives every caption, tooltip and icon back.
/// </summary>
public partial class DealOrForceGame
{
    private readonly List<Control> _topBarObserverHidden = new();

    private void WriteObserverTopBar()
    {
        if (_campaign is null) return;
        _topBarShowsMovement = true; // RestoreCountryTopBar gives the places back on the way out
        var living = _campaign.Nations.Where(n => !n.Landless && n.Population > 0 && _campaign.HasOwnedLand(n)).ToList();
        double gdp = 0, population = 0, settled = 0;
        foreach (var nation in living)
        {
            gdp += System.Math.Max(0f, nation.Gdp);
            population += System.Math.Max(0L, nation.Population);
            settled += nation.Stability * (double)System.Math.Max(0L, nation.Population);
        }
        var stability = population > 0 ? (float)(settled / population) : 0f;
        var wars = _campaign.Wars.Count(war => !war.Ended);
        var divisions = _campaign.Armies.Count;

        _identity.Text = Tr("The world");
        _identity.TooltipText = Ui.Tip(Tr("Nobody is steering: the computer runs every country."));
        _identityTag.Text = Tr("Observer mode");
        _identityTag.TooltipText = "";
        Hide(_identityFlag);
        foreach (var (badge, nation) in _countryRankBadges)
            if (ReferenceEquals(nation(), _campaign.Player)) Hide(badge);

        TopBarAs(_treasury, "world_gdp", Tr("What every country on the map produces in a year, added up."));
        WriteStat(_treasury, Fmt.Money((float)gdp), Fmt.MoneyExact((float)gdp), Tr("a year"), 0);
        TopBarAs(_balance, "world_wars", Tr("Wars being fought right now, between states and inside them."));
        WriteStat(_balance, wars.ToString("N0"), Loc.Format("{0} wars under way", wars), "", wars > 0 ? -1 : 0);
        TopBarAs(_gdp, "world_countries", Tr("Countries holding ground of their own."));
        WriteStat(_gdp, living.Count.ToString("N0"), Loc.Format("{0} countries hold ground", living.Count), "", 0);
        TopBarAs(_population, "world_population", Tr("Everyone living in the world's countries."));
        WriteStat(_population, Fmt.People((long)population), Fmt.PeopleExact((long)population), "", 0);
        TopBarAs(_stability, "world_stability", Tr("How settled the world's people are on average: each country weighed by how many live in it."));
        WriteStat(_stability, Fmt.Percent(stability), Loc.Format("Stability {0:P1}", stability), "", 0);
        TopBarAs(_manpower, "world_divisions", Tr("Divisions standing in the world's armies."));
        WriteStat(_manpower, divisions.ToString("N0"), Loc.Format("{0} divisions", divisions), "", 0);
        foreach (var stat in new[] { _inflation, _armyExperience, _airExperience, _navyExperience, _politicalPoints, _publicDebt })
            Hide(stat?.Value?.GetParent() as Control);
    }

    /// <summary>
    /// The situation report of an observer: more of the world's news than a player's report
    /// carries, and every war being fought, largest first, each opening on its front.
    /// </summary>
    private void DrawObserverOutliner()
    {
        AddOutlinerHeader("WORLD");
        foreach (var line in NewsFeed.Headlines(_campaign, 8))
        {
            NewsFeed.Line captured = line;
            AddOutlinerButton(line.Text, () => FocusNews(captured),
                line.Text + System.Environment.NewLine + Tr("Show it on the map."));
            if (_outlinerGroupRows.GetChild(_outlinerGroupRows.GetChildCount() - 1) is Button news)
            {
                news.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                news.CustomMinimumSize = new Vector2(0, 48);
            }
        }
        AddOutlinerHeader("WARS");
        var wars = _campaign.Wars.Where(war => !war.Ended)
            .OrderByDescending(war => (_campaign.NationById(war.AttackerId)?.Population ?? 0) +
                                      (_campaign.NationById(war.DefenderId)?.Population ?? 0))
            .Take(10).ToArray();
        foreach (var war in wars)
        {
            var attacker = _campaign.NationById(war.AttackerId);
            var defender = _campaign.NationById(war.DefenderId);
            AddOutlinerButton(Loc.Format("{0} vs {1}", attacker?.DisplayName ?? "?", defender?.DisplayName ?? "?"),
                () => FocusWar(war), WarNames.Of(_campaign, war));
        }
        var movements = ObserverUiRules.ObservedLandlessMovements(_campaign);
        if (movements.Count > 0)
        {
            AddOutlinerHeader("obs.ui.movements.short_heading");
            foreach (var movement in movements.Take(8))
            {
                var captured = movement;
                var host = Movements.HostOf(_campaign, movement);
                var label = host is not null
                    ? $"{movement.DisplayName} ({host.Tag}) · {Fmt.Percent(movement.Movement.Legitimacy)}"
                    : $"{movement.DisplayName} · {Fmt.Percent(movement.Movement.Legitimacy)}";
                AddOutlinerButton(label, () => OpenObservedMovement(captured.Id),
                    Loc.Format("obs.ui.movements.row_tip_fmt", movement.DisplayName,
                        host?.DisplayName ?? Tr("nobody"),
                        Fmt.People(movement.Movement.Cadres),
                        Fmt.Percent(movement.Movement.Legitimacy)));
            }
        }
    }

    private void Hide(Control control)
    {
        if (control is null || !control.Visible) return;
        control.Visible = false;
        _topBarObserverHidden.Add(control);
    }

    private void RefreshNullPlayerTopBar()
    {
        if (IsNewShellActive) return;
        _identity.Text = Tr("The world");
        var worldFont = _identity.GetThemeFont("font");
        var worldSize = 18;
        while (worldSize > 12 && worldFont.GetStringSize(_identity.Text, HorizontalAlignment.Left, -1, worldSize).X >
               System.Math.Max(154, _identity.Size.X)) worldSize--;
        _identity.AddThemeFontSizeOverride("font_size", worldSize);
        _identityTag.Text = Tr("Observer mode");
        _identityTag.TooltipText = Ui.Tip(Tr("shell.obs.badge_world"));
        _identity.TooltipText = Ui.Tip(Tr("The world") + "\n" + Tr("Observer mode"));
        if (_identityFlag is not null) _identityFlag.Visible = false;
        RefreshCountryRankBadges();
        if (_identityPortraitSlot is { Visible: true } && _identityPortraitLeaderKey != "obs")
        {
            _identityPortraitLeaderKey = "obs";
            ClearRuntimeChildren(_identityPortraitSlot);
        }
        RefreshDatePlate(null);
        WriteObserverTopBar();
        PaintResourceStates(null);
        ArrangeResourcePlate();
        if (_hudFlagMount is not null) _hudFlagMount.Visible = false;
        foreach (var stat in _hudStats) FitHudStat(stat);
        NoteFigureTips();
        _militarySpending.SetValueNoSignal(0f);
        UpdateSpendingReadout(null);
        UpdateSpeedButtons();
        UpdateScreenButtons();
        if (ResolveHudFigures())
        {
            PlaceHudChrome();
            _strategicHudFingerprint = int.MinValue;
        }
    }

    /// <summary>Called by RestoreCountryTopBar: everything observer mode hid comes back.</summary>
    private void RestoreObserverHidden()
    {
        foreach (var control in _topBarObserverHidden)
            if (IsInstanceValid(control)) control.Visible = true;
        _topBarObserverHidden.Clear();
    }
}
