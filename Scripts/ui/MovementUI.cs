using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// A movement's panel (Movements): who it is, where its headquarters stands, its money,
/// cadres, standing and the host's temper, where its following is strongest and what an uprising
/// there would stand - and, for the one the player is, its actions, each with its price and, when
/// it cannot be taken, why. Opens by clicking a headquarters on the map, by itself when a campaign
/// starts as a movement, and with O.
///
/// It stands where a country's dossier stands, on the left, and for a player who is an
/// movement it IS the dossier: the Nation button opens it, since a country's figures say
/// nothing about a movement with no ground. Whatever else opens in that place - a dossier, the
/// army panel, another country's card - takes it, the newer one standing, so the situation report
/// on the right is never covered. `hud.movement` in `gui/` moves it.
///
/// Regional actions act on the region selected on the map, and the ones aimed at a country on the
/// country selected - the same selection every other order uses. While the panel is the player's
/// own, a click on the map only aims: the country card that would open over it stays shut. A row
/// of its following selects that region and takes the camera there, which is the short way to aim
/// a campaign or a rising.
/// </summary>
public partial class DealOrForceGame
{
    private PanelContainer _movementPanel;
    private VBoxContainer _movementRows;
    private Nation _movementShown;
    private double _movementRefresh;
    private Simulation.Campaign _movementOpenedFor;
    /// <summary>What the panel was last built from; it is rebuilt only when this changes, so a
    /// button is never pulled out from under a click that is half made.</summary>
    private string _movementBuiltFrom = "";

    /// <summary>Built once with the rest of the interface.</summary>
    private void BuildMovementPanel()
    {
        _movementPanel = TexturedPanel(_hudLayer);
        _movementPanel.Name = "MovementPanel";
        _movementPanel.Visible = false;
        Ui.Place(_movementPanel, InterfaceSkin.HasLayout("hud.movement") ? "hud.movement" : "hud.side_panel");
        var outer = PanelRows(_movementPanel, 16, 12, 6);
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        outer.AddChild(scroll);
        _movementRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _movementRows.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(_movementRows);

        _globe.MovementPicked += ShowMovement;
        // Not behind the main menu; on the country picker they stay, because a movement is
        // one of the things that can be picked there and its headquarters is where it is.
        _globe.CampaignInView = () => _campaign is not null && _menuLayer is not { Visible: true };
        AddChild(new MovementPanelDriver(this));
    }

    /// <summary>Open the panel on this movement.</summary>
    private void ShowMovement(Nation nation)
    {
        if (nation?.Movement is null || _movementPanel is null) return;
        // On the picker, choose the movement rather than opening the hidden campaign HUD.
        if (_pickerLayer is { Visible: true })
        {
            SetPickerNation(nation, false);
            return;
        }
        // It takes the place of whatever stood there.
        if (_sidePanel is { Visible: true }) CloseSidePanel();
        if (_armyPanel is { Visible: true }) SetArmyPanelVisible(false);
        if (_countryCard is { Visible: true }) HideMapSelection(false);
        _movementSideSeen = false;
        _movementShown = nation;
        _movementPanel.Visible = true;
        _movementBuiltFrom = "";
        RefreshMovementPanel();
    }

    /// <summary>Whether a dossier or the army panel stood open on the last frame.</summary>
    private bool _movementSideSeen;

    /// <summary>
    /// Keep the panel and the others that share its place from standing on each other, once a
    /// frame, after the rest of the interface has had its say: the Nation screen of an
    /// movement turns into this panel, something opened since the last frame closes it, and
    /// while it is the player's own a click on the map aims it rather than opening a country card.
    /// </summary>
    private void KeepMovementPanelInItsPlace()
    {
        var player = _campaign?.Player;
        if (player?.Movement is { Landless: true } && _sidePanel is { Visible: true } && _openScreen == "Nation")
            ShowMovement(player);
        var sideOpen = _sidePanel is { Visible: true } || _armyPanel is { Visible: true };
        if (_movementPanel.Visible)
        {
            if (sideOpen && !_movementSideSeen) _movementPanel.Visible = false;
            else if (_countryCard is { Visible: true })
            {
                if (_movementShown == player) _countryCard.Visible = false;
                else _movementPanel.Visible = false;
            }
        }
        _movementSideSeen = sideOpen;
    }

    private void ToggleMovementPanel()
    {
        if (_movementPanel is null) return;
        if (_movementPanel.Visible) { _movementPanel.Visible = false; return; }
        var own = _campaign?.Player;
        ShowMovement(own?.Movement is not null ? own : _movementShown ?? Movements.Of(_campaign).FirstOrDefault());
    }

    /// <summary>Everything the panel shows, as one string: when it has not moved, nothing is rebuilt.</summary>
    private string MovementSnapshot(Nation nation)
    {
        var state = nation.Movement;
        return string.Join("|", nation.Id, _campaign.ElapsedDays, _campaign.MovementRevision, Simulation.Loc.Language,
            _selectedState?.MapIndex ?? -1, _selectedNation?.Id ?? -1, nation == _campaign.Player,
            nation.Treasury.ToString("0.0000"), state.Cadres.ToString("0"), state.Heat.ToString("0.000"),
            state.Legitimacy.ToString("0.000"), state.RecognisedBy.Count, state.MovingToStateMapIndex,
            state.HeadquartersStateMapIndex, state.LastReport, state.Support.Count, state.Dissolved,
            state.DesignatedBy.Count, state.SponsoredBy.Count, state.InvitedBy.Count, state.OnTheWay.Count,
            state.Infiltrated.Count, _campaign.Player?.Id ?? -1);
    }

    private void RefreshMovementPanel()
    {
        var nation = _movementShown;
        if (_movementRows is null || nation?.Movement is null || _campaign is null) return;
        var snapshot = MovementSnapshot(nation);
        if (snapshot == _movementBuiltFrom) return;
        _movementBuiltFrom = snapshot;
        ClearChildren(_movementRows);

        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        var mine = nation == _campaign.Player;
        var defines = _campaign.Defines;
        var host = Movements.HostOf(_campaign, nation);

        // ---- who it is ----------------------------------------------------------------
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        _movementRows.AddChild(header);
        header.AddChild(FlagBadge(nation.Tag, 34, definition is null ? "" : Simulation.Loc.Get(definition.Description)));
        var identity = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        identity.AddThemeConstantOverride("separation", 0);
        header.AddChild(identity);
        var name = Fixed(Title(nation.DisplayName, 21, Ui.Colour("text.accent")));
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        identity.AddChild(name);
        var seat = new HBoxContainer();
        seat.AddThemeConstantOverride("separation", 5);
        identity.AddChild(seat);
        if (state.Dissolved)
        {
            if (Symbol("unrest", 14, "state.negative") is { } mark) seat.AddChild(mark);
            seat.AddChild(Ellipsed(Tr("no more"), 12, "state.negative", true));
        }
        else if (state.Landless && state.HeadquartersStateMapIndex < 0)
        {
            if (Symbol("movement_headquarters", 14, "state.warning") is { } mark) seat.AddChild(mark);
            seat.AddChild(Ellipsed(Simulation.Loc.Format("no headquarters: nobody will take it in ({0} of {1} weeks)",
                state.HomelessWeeks, defines.MovementDissolveHomelessWeeks), 12, "state.warning", true));
        }
        else if (state.Landless)
        {
            if (Symbol("movement_headquarters", 14, "text.muted") is { } mark) seat.AddChild(mark);
            seat.AddChild(Ellipsed(Simulation.Loc.Format("{0}, {1}",
                MovementRules.RegionName(_campaign, state.HeadquartersStateMapIndex), host?.DisplayName ?? Tr("nobody")),
                12, "text.muted", true));
        }
        else
        {
            if (Symbol("province", 14, "text.muted") is { } mark) seat.AddChild(mark);
            seat.AddChild(Ellipsed(Simulation.Loc.Format("{0} region(s) since day {1}",
                _campaign.StatesOf(nation).Length, state.StatehoodDay), 12, "text.muted", true));
        }
        var close = ActionButton("×", () => _movementPanel.Visible = false, 30, 28);
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        header.AddChild(close);

        var chips = new HFlowContainer();
        chips.AddThemeConstantOverride("h_separation", 5);
        chips.AddThemeConstantOverride("v_separation", 5);
        _movementRows.AddChild(chips);
        if (state.Dissolved) chips.AddChild(MovementChip(Tr("DISSOLVED"), "state.negative", "unrest"));
        else chips.AddChild(state.Landless
            ? MovementChip(Tr("WITHOUT LAND"), "state.info", "movement_headquarters")
            : MovementChip(Tr("HOLDS LAND"), "state.positive", "province"));
        if (state.Exile) chips.AddChild(MovementChip(Tr("GOVERNMENT IN EXILE"), "state.info", "government"));
        var listed = state.DesignatedBy.Count(id => _campaign.NationById(id) is not null);
        if (listed > 0)
        {
            var chip = MovementChip(Simulation.Loc.Format("LISTED AS TERRORIST BY {0}", listed), "state.negative", "war");
            chip.MouseFilter = Control.MouseFilterEnum.Stop;
            chip.TooltipText = Ui.Tip(string.Join(", ", state.DesignatedBy.Select(id => _campaign.NationById(id)?.DisplayName).Where(n => n is not null)));
            chips.AddChild(chip);
        }
        if (definition is not null)
            foreach (var trait in definition.Traits)
                chips.AddChild(MovementChip(Tr("trait." + trait), "text.accent_dim"));

        if (definition is not null)
        {
            var whole = Simulation.Loc.Get(definition.Description);
            var about = Title(FirstSentence(whole), 12, Ui.Colour("text.body_soft"));
            about.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            about.MouseFilter = Control.MouseFilterEnum.Stop;
            about.TooltipText = Ui.Tip(whole);
            _movementRows.AddChild(about);
        }

        if (state.MovingToStateMapIndex >= 0)
            _movementRows.AddChild(MovementNotice("movement_move", Simulation.Loc.Format("The headquarters is on its way to {0}: {1} days.",
                MovementRules.RegionName(_campaign, state.MovingToStateMapIndex),
                Math.Max(0, state.ArrivesOnDay - _campaign.ElapsedDays)), "state.warning"));
        foreach (var journey in state.OnTheWay)
            _movementRows.AddChild(MovementNotice("movement_cadres", Simulation.Loc.Format("{0} fighters are on their way to {1}: {2} days.",
                Fmt.People(journey.Fighters), MovementRules.RegionName(_campaign, journey.Region),
                Math.Max(0, journey.ArrivesOnDay - _campaign.ElapsedDays)), "state.warning"));
        if (state.Dissolved)
        {
            if (!string.IsNullOrWhiteSpace(state.LastReport))
                _movementRows.AddChild(MovementNotice("unrest", state.LastReport, "state.negative"));
            return;
        }

        // ---- where it stands ----------------------------------------------------------
        _movementRows.AddChild(MovementHeading("WHERE IT STANDS"));
        var figures = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        figures.AddThemeConstantOverride("h_separation", 12);
        figures.AddThemeConstantOverride("v_separation", 8);
        _movementRows.AddChild(figures);
        var behind = state.Support.Count(pair => pair.Value >= defines.MovementUprisingSupport);
        figures.AddChild(MovementFigure("treasury", Fmt.Money(nation.Treasury), Tr("treasury"),
            Tr("Cash on hand: what every action is paid from.")));
        figures.AddChild(MovementFigure("income", Fmt.Money(state.IncomeWeekly * 52f), Tr("income a year"),
            Simulation.Loc.Format("{0} a year from donations and {1} from sponsors and contracts.",
                Fmt.Money(state.DonationsWeekly * 52f), Fmt.Money(state.SponsorsWeekly * 52f)),
            state.IncomeWeekly > 0f ? "state.positive" : "text.bright"));
        figures.AddChild(MovementFigure("movement_cadres", Fmt.People(state.Cadres), Tr("cadres"),
            Simulation.Loc.Format("Organisers and volunteers. An uprising takes {0} of them, and every {1} is a militia division.",
                Fmt.People(defines.MovementUprisingCadres), Fmt.People(defines.MovementMilitiaPerCadres))));
        figures.AddChild(MovementFigure("diplomacy", state.RecognisedBy.Count.ToString("N0"), Tr("recognitions"),
            Tr("Countries that recognise it. Each one lifts its standing a little every week.")));
        figures.AddChild(MovementFigure("movement_support", behind.ToString("N0"), Tr("regions ready to rise"),
            Simulation.Loc.Format("Regions where its support has reached {0}, enough to call an uprising.",
                Fmt.Percent(defines.MovementUprisingSupport)), behind > 0 ? "text.accent" : "text.bright"));
        figures.AddChild(MovementFigure("movement_legitimacy", Fmt.Percent(state.Legitimacy), Tr("standing"),
            Tr("How far the world takes it seriously: its donations, its recognitions and the chance of every uprising.")));

        _movementRows.AddChild(MovementGauge("movement_legitimacy", Tr("Standing"), state.Legitimacy, -1f, "text.accent",
            Tr("How far the world takes it seriously: its donations, its recognitions and the chance of every uprising.")));
        var heatToken = state.Heat >= defines.MovementExpelHeat * 0.75f ? "state.negative"
            : state.Heat >= defines.MovementExpelHeat * 0.45f ? "state.warning" : "state.info";
        if (state.Landless)
            _movementRows.AddChild(MovementGauge("movement_heat",
                Simulation.Loc.Format("The host's temper · {0}", host?.DisplayName ?? Tr("nobody")), state.Heat,
                defines.MovementExpelHeat, heatToken,
                Simulation.Loc.Format("Everything it does on the host's ground warms the host's temper, and it cools by itself. At {0} the host expels the headquarters.",
                    Fmt.Percent(defines.MovementExpelHeat))));

        // ---- its following ------------------------------------------------------------
        _movementRows.AddChild(MovementHeading("ITS FOLLOWING"));
        var strongest = state.Support.OrderByDescending(pair => pair.Value).Take(6).ToList();
        if (strongest.Count == 0)
            _movementRows.AddChild(Title("No following anywhere yet.", 12, Ui.Colour("text.muted")));
        foreach (var (mapIndex, support) in strongest)
        {
            var region = _campaign.States.Find(candidate => candidate.MapIndex == mapIndex);
            if (region is null) continue;
            _movementRows.AddChild(MovementFollowingRow(nation, region, support));
        }

        if (!string.IsNullOrWhiteSpace(state.LastReport))
            _movementRows.AddChild(MovementNotice("alert_event", state.LastReport, "text.body"));

        // ---- what it can do -----------------------------------------------------------
        if (!mine)
        {
            BuildDealings(nation);
            return;
        }

        _movementRows.AddChild(MovementHeading("ACTIONS"));
        var target = _selectedState;
        var targetNation = _selectedNation;
        var aim = new HBoxContainer();
        aim.AddThemeConstantOverride("separation", 5);
        _movementRows.AddChild(aim);
        if (Symbol("province", 13, "text.muted") is { } regionMark) aim.AddChild(regionMark);
        aim.AddChild(Ellipsed(target?.DisplayName ?? Tr("no region chosen"), 12, target is null ? "text.faint" : "text.body_soft", true));
        aim.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0) });
        if (Symbol("nation", 13, "text.muted") is { } nationMark) aim.AddChild(nationMark);
        aim.AddChild(Ellipsed(targetNation?.DisplayName ?? Tr("no country chosen"), 12, targetNation is null ? "text.faint" : "text.body_soft", true));
        aim.TooltipText = Ui.Tip(Tr("Click a region on the map, or a row of its following, to aim what it does there."));
        aim.MouseFilter = Control.MouseFilterEnum.Stop;

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        _movementRows.AddChild(grid);
        foreach (var kind in MovementActions.For(nation))
            grid.AddChild(MovementActionButton(nation, kind, target, targetNation));
    }

    /// <summary>What the player's own country can do about a movement it is looking at.</summary>
    private void BuildDealings(Nation movement)
    {
        var player = _campaign.Player;
        if (player is null || player.Movement is not null || _campaign.StatesOf(player).Length == 0)
        {
            _movementRows.AddChild(Title("Play as this movement to give it orders.", 12, Ui.Colour("text.muted")));
            return;
        }
        var state = movement.Movement;
        _movementRows.AddChild(MovementHeading(Simulation.Loc.Format("{0} AND IT", player.DisplayName.ToUpperInvariant())));
        var theirs = CampaignDiplomacy.Get(_campaign, movement.Id, player.Id).Opinion;
        var where = new List<string>();
        if (state.RecognisedBy.Contains(player.Id)) where.Add(Tr("you recognise it"));
        if (state.SponsoredBy.ContainsKey(player.Id)) where.Add(Tr("you fund it"));
        if (state.DesignatedBy.Contains(player.Id)) where.Add(Tr("you list it as terrorist"));
        if (state.InvitedBy.Contains(player.Id)) where.Add(Tr("you have offered it a home"));
        if (Movements.HostOf(_campaign, movement) == player) where.Add(Tr("its headquarters is in your country"));
        if (MovementDealings.HoldsItsClaim(_campaign, player, movement)) where.Add(Tr("it claims your ground"));
        _movementRows.AddChild(MovementNotice("diplomacy", Simulation.Loc.Format("It thinks {0} of you{1}.",
            theirs.ToString("+0;-0;0"), where.Count == 0 ? "" : " · " + string.Join(" · ", where)), "text.body"));

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        _movementRows.AddChild(grid);
        foreach (var kind in MovementDealings.For(_campaign, player, movement))
        {
            var refusal = MovementDealings.Refusal(_campaign, player, movement, kind.Id);
            var cost = MovementDealings.Cost(_campaign, kind.Id);
            var price = kind.Id == "sponsor"
                ? Simulation.Loc.Format("{0} a year", Fmt.Money(_campaign.Defines.MovementSponsorPerYear))
                : cost > 0f ? Fmt.Money(cost) : Tr("no cost");
            var captured = kind.Id;
            grid.AddChild(MovementButton(kind.Icon, Tr(kind.Name), price,
                Tr(kind.Help) + (refusal.Length > 0 ? "\n\n" + refusal : ""), refusal.Length > 0,
                kind.Id is "designate" or "crack_down", () =>
                {
                    _campaign.Commands.Enqueue(new MovementDealingCommand { IssuerId = player.Id, MovementId = movement.Id, Action = captured });
                    _campaign.Commands.ApplyAll(_campaign);
                }));
        }
    }

    /// <summary>A button in the panel's grids: a mark, a name, a line under it, and why not when it cannot be pressed.</summary>
    private Button MovementButton(string icon, string title, string line, string tip, bool refused, bool primary, Action pressed)
    {
        var button = new Button
        {
            CustomMinimumSize = new Vector2(0, 46),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None,
            ClipContents = true,
            Disabled = refused
        };
        StyleButton(button, primary && !refused);
        var inner = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        inner.OffsetLeft = 9;
        inner.OffsetRight = -6;
        inner.AddThemeConstantOverride("separation", 8);
        button.AddChild(inner);
        if (Symbol(icon, 22, refused ? "text.faint" : "text.accent") is { } mark) inner.AddChild(mark);
        var words = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        words.AddThemeConstantOverride("separation", -2);
        inner.AddChild(words);
        words.AddChild(Ellipsed(title, 13, refused ? "text.muted" : "text.bright", true));
        words.AddChild(Ellipsed(line, 11, refused ? "text.faint" : "text.caption", true));
        button.TooltipText = Ui.Tip(tip);
        button.Pressed += () =>
        {
            PlayStrategicCue("ui_select");
            pressed();
            _movementBuiltFrom = "";
            RefreshMovementPanel();
        };
        return button;
    }

    /// <summary>One of its actions: its mark, its name and its price, and why not when it is not possible.</summary>
    private Button MovementActionButton(Nation nation, MovementActions.Kind kind, ScenarioState target, Nation targetNation)
    {
        var region = kind.NeedsRegion ? target?.MapIndex ?? -1 : -1;
        var country = kind.NeedsNation ? targetNation?.Id ?? -1 : -1;
        var refusal = MovementActions.Refusal(_campaign, nation, kind.Id, region, country);
        var cost = MovementActions.Cost(_campaign, nation, kind.Id, region);
        var refused = refusal.Length > 0;
        var price = kind.Id == "rise" && target is not null && !refused
            ? Simulation.Loc.Format("{0} · chance {1}", Fmt.Money(cost), Fmt.Percent(MovementRules.UprisingChance(_campaign, nation, target)))
            : cost > 0f ? Fmt.Money(cost) : Tr("no cost");
        var tip = Tr(kind.Help);
        if (kind.Id == "rise" && target is not null)
            tip += "\n" + Simulation.Loc.Format("Chance in {0} now: {1}.", target.DisplayName,
                Fmt.Percent(MovementRules.UprisingChance(_campaign, nation, target)));
        if (refused) tip += "\n\n" + refusal;
        var captured = kind.Id;
        // The one that changes the map is the one drawn in brass, whenever it can be taken.
        return MovementButton(ActionIcon(kind.Id), Tr(kind.Name), price, tip, refused, kind.Id == "rise", () =>
        {
            _campaign.Commands.Enqueue(new MovementActionCommand
            {
                IssuerId = nation.Id, Action = captured, StateMapIndex = region, TargetNationId = country
            });
            _campaign.Commands.ApplyAll(_campaign);
        });
    }

    /// <summary>A region where it has a following: the support against the line an uprising needs, and the chance.</summary>
    private Button MovementFollowingRow(Nation nation, ScenarioState region, float support)
    {
        var defines = _campaign.Defines;
        var chance = MovementRules.UprisingChance(_campaign, nation, region);
        var ready = support >= defines.MovementUprisingSupport;
        var row = new Button
        {
            CustomMinimumSize = new Vector2(0, 30),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None,
            ClipContents = true
        };
        // A row, not a button to look at: nothing behind it until the cursor is over it.
        var lit = new StyleBoxFlat { BgColor = Ui.Colour("button.fill_hover", 0.7f) };
        lit.SetCornerRadiusAll(2);
        row.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        row.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        row.AddThemeStyleboxOverride("hover", lit);
        row.AddThemeStyleboxOverride("pressed", lit);
        var inner = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inner.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        inner.OffsetLeft = 4;
        inner.OffsetRight = -4;
        inner.AddThemeConstantOverride("separation", 8);
        row.AddChild(inner);
        var name = Ellipsed(region.DisplayName, 13, region == _selectedState ? "text.accent" : "text.body", true);
        inner.AddChild(name);
        inner.AddChild(new MovementMeter
        {
            Value = support, Mark = defines.MovementUprisingSupport,
            Fill = Ui.Colour(ready ? "text.accent" : "text.accent_dim"),
            Back = Ui.Colour("surface.inset"), MarkColour = Ui.Colour("text.body_soft"),
            CustomMinimumSize = new Vector2(92, 6),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        });
        var share = Ellipsed(Fmt.Percent(support), 12, ready ? "text.accent" : "text.body_soft");
        share.CustomMinimumSize = new Vector2(40, 0);
        share.HorizontalAlignment = HorizontalAlignment.Right;
        inner.AddChild(share);
        var odds = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(58, 0) };
        odds.AddThemeConstantOverride("separation", 3);
        inner.AddChild(odds);
        var oddsToken = chance >= 0.5f ? "state.positive" : chance >= 0.25f ? "state.warning" : "text.faint";
        if (Symbol("movement_uprising", 13, oddsToken) is { } mark) odds.AddChild(mark);
        odds.AddChild(Ellipsed(Fmt.Percent(chance), 12, oddsToken));
        row.TooltipText = Ui.Tip(Simulation.Loc.Format(
            "{0}: support {1}; an uprising takes {2}. An uprising here would stand {3}. Click to choose the region and look at it.",
            region.DisplayName, Fmt.Percent(support), Fmt.Percent(defines.MovementUprisingSupport), Fmt.Percent(chance)));
        var captured = region;
        row.Pressed += () =>
        {
            PlayStrategicCue("ui_select");
            _selectedState = captured;
            var owner = _campaign.Nations.Find(candidate => candidate.MapIndex == captured.NationMapIndex);
            if (owner is not null) _selectedNation = owner;
            var (latitude, longitude) = Movements.CentreOf(_campaign, captured);
            if (latitude != 0f || longitude != 0f) _globe.FocusOn(latitude, longitude, 9f);
            RefreshMovementPanel();
        };
        return row;
    }

    /// <summary>
    /// The movements a region has to do with - their headquarters here, or a following worth the
    /// name - as small buttons with their flags, in the region card. Each opens its panel.
    /// </summary>
    private void AddMovementsToRegionCard(VBoxContainer rows, ScenarioState region)
    {
        var here = Movements.Active(_campaign)
            .Where(nation => nation.Movement.Landless && nation.Movement.HeadquartersStateMapIndex == region.MapIndex)
            .ToList();
        var following = Movements.Active(_campaign)
            .Where(nation => !here.Contains(nation))
            .Select(nation => (Nation: nation, Support: nation.Movement.Support.GetValueOrDefault(region.MapIndex)))
            .Where(entry => entry.Support >= 0.1f)
            .OrderByDescending(entry => entry.Support)
            .Take(3)
            .ToList();
        if (here.Count == 0 && following.Count == 0) return;
        var caption = Title(here.Count > 0 ? "HEADQUARTERS AND MOVEMENTS HERE" : "MOVEMENTS HERE", 11, Ui.Colour("text.caption"));
        caption.AutowrapMode = TextServer.AutowrapMode.Off;
        rows.AddChild(caption);
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 4);
        flow.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(flow);
        void Add(Nation nation, string text)
        {
            var captured = nation;
            var button = ActionButton(text, () => ShowMovement(captured), 0, 26);
            var flagName = $"flag_{nation.Tag.ToLowerInvariant()}";
            var flagPath = SpriteCatalog.TextureOf(flagName, $"res://game/gfx/flags/{flagName}.png");
            button.Icon = FlagTexture(nation.Tag) ?? (ResourceLoader.Exists(flagPath) ? GD.Load<Texture2D>(flagPath) : null);
            button.ExpandIcon = false;
            button.AddThemeConstantOverride("icon_max_width", 20);
            button.AddThemeFontSizeOverride("font_size", 12);
            button.TooltipText = Ui.Tip(Simulation.Loc.Get(Movements.DefinitionOf(nation)?.Description ?? ""));
            flow.AddChild(button);
        }
        foreach (var nation in here) Add(nation, Simulation.Loc.Format("HQ · {0}", nation.DisplayName));
        foreach (var (nation, support) in following) Add(nation, $"{nation.DisplayName} {Fmt.Percent(support)}");
    }

    /// <summary>A headline figure: its mark, the number, and what it is under it.</summary>
    private Control MovementFigure(string icon, string value, string caption, string tip, string token = "text.bright")
    {
        var cell = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = Ui.Tip(tip)
        };
        cell.AddThemeConstantOverride("separation", 8);
        if (Symbol(icon, 24, "text.accent_dim") is { } mark) cell.AddChild(mark);
        var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", -3);
        cell.AddChild(text);
        text.AddChild(Ellipsed(value, 17, token, true));
        text.AddChild(Ellipsed(caption, 11, "text.caption", true));
        return cell;
    }

    /// <summary>A share drawn as a bar with its caption and figure, and a line where something happens.</summary>
    private Control MovementGauge(string icon, string caption, float value, float mark, string token, string tip)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop, TooltipText = Ui.Tip(tip) };
        box.AddThemeConstantOverride("separation", 3);
        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 6);
        box.AddChild(line);
        if (Symbol(icon, 14, token) is { } symbol) line.AddChild(symbol);
        line.AddChild(Ellipsed(caption, 12, "text.body_soft", true));
        line.AddChild(Ellipsed(mark >= 0f
            ? Simulation.Loc.Format("{0} of {1}", Fmt.Percent(value), Fmt.Percent(mark))
            : Fmt.Percent(value), 12, token));
        box.AddChild(new MovementMeter
        {
            Value = value, Mark = mark, Fill = Ui.Colour(token), Back = Ui.Colour("surface.inset"),
            MarkColour = Ui.Colour("state.negative"),
            CustomMinimumSize = new Vector2(0, 7),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        });
        return box;
    }

    /// <summary>A small capitalised heading with a rule under it, the way the dossiers divide theirs.</summary>
    private static SectionHeading MovementHeading(string source)
    {
        var heading = new SectionHeading
        {
            Text = Tr(source),
            Rule = Ui.Colour("button.primary_border"),
            CustomMinimumSize = new Vector2(0, 26),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        heading.SetMeta(LocSourceMeta, source);
        heading.SetMeta(LocAppliedMeta, heading.Text);
        heading.AddThemeColorOverride("font_color", Ui.Colour("text.accent_dim"));
        heading.AddThemeFontSizeOverride("font_size", 12);
        return heading;
    }

    /// <summary>A line of news set apart from the figures: a mark on the left and the words beside it.</summary>
    private Control MovementNotice(string icon, string text, string token)
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("surface.inset", 0.75f),
            BorderColor = Ui.Colour(token == "text.body" ? "button.primary_border" : token),
            ContentMarginLeft = 9, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6
        };
        style.BorderWidthLeft = 2;
        style.SetCornerRadiusAll(2);
        panel.AddThemeStyleboxOverride("panel", style);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        if (Symbol(icon, 16, token == "text.body" ? "text.accent_dim" : token) is { } mark)
        {
            mark.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            row.AddChild(mark);
        }
        var label = Title(text, 12, Ui.Colour(token));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(label);
        return panel;
    }

    /// <summary>A word in a frame: its status, or one of its traits.</summary>
    private PanelContainer MovementChip(string text, string token, string icon = null)
    {
        var chip = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour(token, 0.12f),
            BorderColor = Ui.Colour(token, 0.6f),
            ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 2, ContentMarginBottom = 2
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(3);
        chip.AddThemeStyleboxOverride("panel", style);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);
        chip.AddChild(row);
        if (icon is not null && Symbol(icon, 12, token) is { } mark) row.AddChild(mark);
        row.AddChild(Ellipsed(text, 11, token));
        return chip;
    }

    /// <summary>
    /// A one-line label. As wide as its words by default; `squeeze` lets it take the room left
    /// in its row and end in an ellipsis instead - only ever for the one label in a row that
    /// fills, because a clipped label with nothing telling it to grow is given no width at all.
    /// </summary>
    private static Label Ellipsed(string text, int size, string token, bool squeeze = false)
    {
        var label = Title(text, size, Ui.Colour(token));
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.VerticalAlignment = VerticalAlignment.Center;
        if (!squeeze) return label;
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    /// <summary>The mark of an action: its own, or the one it shares with the figure it moves.</summary>
    private static string ActionIcon(string action) => action switch
    {
        "recognition" => "movement_legitimacy",
        "rise" => "movement_uprising",
        "infiltrate" => "movement_cadres",
        "call_to_arms" => "manpower",
        _ => "movement_" + action
    };

    /// <summary>The first sentence of a description: the panel says what it is, and the tooltip says the rest.</summary>
    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        for (var index = 1; index < text.Length - 2; index++)
            if (text[index] == '.' && text[index + 1] == ' ' && char.IsUpper(text[index + 2]) && !char.IsUpper(text[index - 1]))
                return text[..(index + 1)];
        return text;
    }

    // ---- the top bar ------------------------------------------------------------------------

    private bool _topBarShowsMovement;
    private readonly Dictionary<StatReadout, (Func<Breakdown> Explain, string Plain, string Tooltip, Texture2D Icon)> _topBarAsCountry = new();

    /// <summary>
    /// A movement without land has no GDP, no population and no prices of its own, and a
    /// top bar of dashes and zeroes says nothing about it. While the player is one, the same seven
    /// places carry its money, income, cadres, standing, following, the host's temper and its best
    /// chance of a rising - each with its own mark - and they go back to the country's figures the
    /// moment it holds ground or the campaign is another country's.
    /// </summary>
    private void WriteMovementTopBar(Nation player)
    {
        var state = player?.Movement;
        if (state is null || !state.Landless)
        {
            RestoreCountryTopBar();
            return;
        }
        _topBarShowsMovement = true;
        var defines = _campaign.Defines;
        _identityTag.Text = player.Tag + " · " + Tr("movement without land");

        var strongest = state.Support.OrderByDescending(pair => pair.Value).FirstOrDefault();
        var strongestName = strongest.Value > 0f ? MovementRules.RegionName(_campaign, strongest.Key) : "";
        ScenarioState bestRegion = null;
        var bestChance = 0f;
        foreach (var (mapIndex, _) in state.Support.OrderByDescending(pair => pair.Value).Take(8))
        {
            var region = _campaign.States.Find(candidate => candidate.MapIndex == mapIndex);
            if (region is null) continue;
            var chance = MovementRules.UprisingChance(_campaign, player, region);
            if (chance <= bestChance) continue;
            bestChance = chance;
            bestRegion = region;
        }

        TopBarAs(_balance, "income", Tr("What its following and its sponsors give it each week."));
        WriteStat(_balance, Fmt.MoneyDelta(state.IncomeWeekly),
            Simulation.Loc.Format("{0} a week: {1} donations, {2} sponsors and contracts", Fmt.MoneyExact(state.IncomeWeekly),
                Fmt.Money(state.DonationsWeekly), Fmt.Money(state.SponsorsWeekly)),
            Tr("a week"), Fmt.Direction(state.IncomeWeekly, 0.0001d));
        TopBarAs(_gdp, "movement_cadres", Tr("Organisers and volunteers: what an uprising is made of."));
        WriteStat(_gdp, Fmt.People(state.Cadres), Fmt.PeopleExact(state.Cadres), Tr("cadres"), 0);
        TopBarAs(_inflation, "movement_legitimacy", Tr("How far the world takes it seriously."));
        WriteStat(_inflation, Fmt.Percent(state.Legitimacy), Simulation.Loc.Format("Standing {0}", Fmt.Percent(state.Legitimacy)),
            Simulation.Loc.Format("recognised by {0}", state.RecognisedBy.Count), 0);
        TopBarAs(_population, "movement_support", Tr("Its strongest following, region by region."));
        WriteStat(_population, strongest.Value > 0f ? Fmt.Percent(strongest.Value) : "—",
            strongestName.Length > 0 ? Simulation.Loc.Format("{0} in {1}", Fmt.Percent(strongest.Value), strongestName) : Tr("No following anywhere yet."),
            strongestName, 0);
        TopBarAs(_stability, "movement_heat", Tr("The host's temper. When it boils over, the headquarters is expelled."));
        WriteStat(_stability, Fmt.Percent(state.Heat),
            Simulation.Loc.Format("The host's temper · {0}", Movements.HostOf(_campaign, player)?.DisplayName ?? Tr("nobody")),
            Simulation.Loc.Format("expels at {0}", Fmt.Percent(defines.MovementExpelHeat)),
            state.Heat >= defines.MovementExpelHeat * 0.75f ? -1 : 0);
        TopBarAs(_manpower, "movement_uprising", Tr("Its best chance of an uprising now, and where."));
        WriteStat(_manpower, bestRegion is null ? "—" : Fmt.Percent(bestChance),
            bestRegion is null ? Tr("No following anywhere yet.") : Simulation.Loc.Format("Chance in {0} now: {1}.", bestRegion.DisplayName, Fmt.Percent(bestChance)),
            bestRegion?.DisplayName ?? "", bestChance >= 0.5f ? 1 : 0);
    }

    /// <summary>Give one top-bar place a movement's mark and tooltip, remembering the country's.</summary>
    private void TopBarAs(StatReadout stat, string icon, string tooltip)
    {
        if (stat?.Value?.GetParent() is not ExplainedStat box) return;
        var glyph = stat.Glyph;
        if (!_topBarAsCountry.ContainsKey(stat))
            _topBarAsCountry[stat] = (box.Explain, box.Plain, box.TooltipText, glyph?.Texture);
        box.Explain = null;
        box.Plain = "";
        box.TooltipText = Ui.Tip(tooltip);
        stat.Caption.Text = Tr(icon switch
        {
            "income" => "Income",
            "movement_cadres" => "Cadres",
            "movement_legitimacy" => "Standing",
            "movement_support" => "Support",
            "movement_heat" => "Host's temper",
            "movement_uprising" => "Uprising",
            _ => stat.CountryCaption
        });
        if (glyph is not null && SpriteCatalog.Has("icon_" + icon))
            glyph.Texture = Art("icon_" + icon, $"res://game/gfx/interface/icons/icon_{icon}.png");
    }

    private void RestoreCountryTopBar()
    {
        if (!_topBarShowsMovement) return;
        _topBarShowsMovement = false;
        foreach (var (stat, country) in _topBarAsCountry)
        {
            if (stat?.Value?.GetParent() is not ExplainedStat box) continue;
            box.Explain = country.Explain;
            box.Plain = country.Plain;
            box.TooltipText = country.Tooltip;
            stat.Caption.Text = Tr(stat.CountryCaption);
            if (stat.Glyph is { } glyph && country.Icon is not null) glyph.Texture = country.Icon;
        }
        _topBarAsCountry.Clear();
    }

    /// <summary>A thin bar with an optional line across it where something happens.</summary>
    private sealed partial class MovementMeter : Control
    {
        public float Value;
        public float Mark = -1f;
        public Color Fill = Colors.White;
        public Color Back = Colors.Black;
        public Color MarkColour = Colors.White;

        public override void _Draw()
        {
            var size = Size;
            DrawRect(new Rect2(Vector2.Zero, size), Back);
            DrawRect(new Rect2(0f, 0f, size.X * Mathf.Clamp(Value, 0f, 1f), size.Y), Fill);
            if (Mark < 0f) return;
            var x = Mathf.Round(size.X * Mathf.Clamp(Mark, 0f, 1f));
            DrawRect(new Rect2(x - 1f, -2f, 2f, size.Y + 4f), MarkColour);
        }
    }

    /// <summary>Keeps the panel current, opens it for a player who starts as a movement, and answers O.</summary>
    private sealed partial class MovementPanelDriver : Node
    {
        private readonly DealOrForceGame _game;

        public MovementPanelDriver(DealOrForceGame game) => _game = game;

        public MovementPanelDriver() { }

        public override void _Process(double delta)
        {
            if (_game?._campaign is null || _game._movementPanel is null) return;
            var player = _game._campaign.Player;
            if (player?.Movement is not null && _game._hudLayer.Visible &&
                !ReferenceEquals(_game._movementOpenedFor, _game._campaign))
            {
                _game._movementOpenedFor = _game._campaign;
                _game.ShowMovement(player);
            }
            _game.KeepMovementPanelInItsPlace();
            if (!_game._movementPanel.Visible) return;
            _game._movementRefresh += delta;
            if (_game._movementRefresh < 0.5) return;
            _game._movementRefresh = 0;
            _game.RefreshMovementPanel();
        }

        public override void _UnhandledInput(InputEvent inputEvent)
        {
            if (inputEvent is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.O }) return;
            if (_game?._campaign is null || !_game._hudLayer.Visible) return;
            _game.ToggleMovementPanel();
            GetViewport().SetInputAsHandled();
        }
    }
}
