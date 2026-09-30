using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The diplomacy screen: one country at a time, the whole of what that relationship is.
///
/// Diplomacy used to be a side dossier: a flag, six figures, two buttons and a line about
/// the non-aggression pact, rebuilt by a method that also had to keep the map, the armies
/// and the region panel in step. Nothing in it was wrong - and nothing in it could be
/// read. The opinion was a number with its reasons hidden under the cursor, there was no
/// way to see the country against your own, the actions were five buttons in a column with
/// no order to them, and finding the country you wanted meant clicking your way around the
/// globe one neighbour at a time.
///
/// This is the shape EU5 and Hearts of Iron have taught players to expect:
///
///   - a LIST, searchable and filterable, so the country you want is found by its name
///     rather than by geography;
///   - a PAGE for the country: what it is, how it stands against yours, what its opinion
///     is made of, what it has signed, what it answers to, what its trade is worth to you;
///   - ACTIONS by category - influence, agreements, hostile - each saying what it costs
///     and, where the answer is knowable, what the answer would be before you ask.
///
/// The side dossier a map click opens stays: a click on the map asks "what is this", and
/// this screen is for "who do I deal with and how".
/// </summary>
public partial class DealOrForceGame
{
    private Control _diplomacyLayer = null!;
    private Label _diplomacySummary = null!;
    private LineEdit _diplomacySearch = null!;
    private VBoxContainer _diplomacyListRows = null!;
    private Control _diplomacyDetailRows = null!;
    private VBoxContainer _diplomacyDetailBody = null!;
    private VBoxContainer _diplomacyPactRows = null!;
    private VBoxContainer _diplomacyActionRows = null!;
    private Label _diplomacyFeedback = null!;
    private Button _diplomacyImproveButton = null!;
    private Button _diplomacyDenounceButton = null!;
    private Button _diplomacyAllianceButton = null!;
    private Button _diplomacyGuaranteeButton = null!;
    private Button _diplomacyWarButton = null!;
    private Button _diplomacyPeaceButton = null!;
    private Button _diplomacyTradeButton = null!;
    private Button _diplomacyPactButton = null!;
    private Button _diplomacyAidButton = null!;
    private Button _diplomacyEmbargoButton = null!;
    private VBoxContainer _diplomacySubmissionActions = null!;
    private readonly Dictionary<SubmissionRoute, Button> _diplomacySubmissionButtons = new();
    private VBoxContainer _diplomacySubjectActions = null!;
    private Button _diplomacyReleaseButton = null!;
    private Button _diplomacyIntegrateButton = null!;
    private int _diplomacySelectedId = -1;
    private int _diplomacyPendingWarTarget = -1;
    private int _diplomacyListBuiltDay = int.MinValue;
    private string _diplomacyListBuiltKey = "";

    /// <summary>One row of the country list, kept open so the list can move under it.</summary>
    private sealed class DiplomacyRow
    {
        public Button Select;
        public Control Flag;
        public Label Name;
        public Label Opinion;
        public Label Stance;
        public Nation Nation;
    }

    private enum DiplomacyFilter
    {
        All,
        Allies,
        Rivals,
        AtWar,
        Neighbours
    }

    private DiplomacyFilter _diplomacyFilter = DiplomacyFilter.All;
    private readonly Dictionary<DiplomacyFilter, Button> _diplomacyFilterButtons = new();
    private readonly List<DiplomacyRow> _diplomacyRows = new();

    private void BuildDiplomacyScreen()
    {
        _diplomacyLayer = FullLayer("Diplomacy");
        _diplomacyLayer.Visible = false;
        _hudLayer.AddChild(_diplomacyLayer);

        var shade = new ColorRect
        {
            Color = new Color(0.015f, 0.026f, 0.041f, 0.72f),
            MouseFilter = MouseFilterEnum.Stop
        };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _diplomacyLayer.AddChild(shade);

        var panel = TexturedPanel(_diplomacyLayer);
        Ui.Place(panel, "modal.diplomacy");

        var rows = PanelRows(panel, 22, 16, 9);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        var title = Title("DIPLOMACY", 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(ActionButton("×", CloseDiplomacyScreen, 38, 32));

        _diplomacySummary = Title("", 13, Ui.Colour("text.body_soft"));
        _diplomacySummary.CustomMinimumSize = new Vector2(0, 22);
        rows.AddChild(_diplomacySummary);

        // ------------------------------------------------------------------ the three columns
        var body = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.AddThemeConstantOverride("separation", 14);
        rows.AddChild(body);

        // ---- the list: find a country by its name rather than by walking the globe.
        var list = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(360, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        list.AddThemeConstantOverride("separation", 6);
        body.AddChild(list);

        _diplomacySearch = new LineEdit
        {
            PlaceholderText = Tr("Find a country..."),
            CustomMinimumSize = new Vector2(0, 30),
            RightIcon = null,
            ClearButtonEnabled = true
        };
        _diplomacySearch.TextChanged += _ => RebuildDiplomacyList();
        list.AddChild(_diplomacySearch);

        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 4);
        list.AddChild(filters);
        AddDiplomacyFilter(filters, "All", DiplomacyFilter.All);
        AddDiplomacyFilter(filters, "Allies", DiplomacyFilter.Allies);
        AddDiplomacyFilter(filters, "Rivals", DiplomacyFilter.Rivals);
        AddDiplomacyFilter(filters, "At war", DiplomacyFilter.AtWar);
        AddDiplomacyFilter(filters, "Neighbours", DiplomacyFilter.Neighbours);

        var listScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        list.AddChild(listScroll);
        _diplomacyListRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _diplomacyListRows.AddThemeConstantOverride("separation", 2);
        listScroll.AddChild(_diplomacyListRows);

        // ---- the page: the country itself.
        var pageScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        body.AddChild(pageScroll);
        _diplomacyDetailBody = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420, 0)
        };
        _diplomacyDetailBody.AddThemeConstantOverride("separation", 7);
        pageScroll.AddChild(_diplomacyDetailBody);
        _diplomacyDetailRows = _diplomacyDetailBody;
        _diplomacyPactRows = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _diplomacyPactRows.AddThemeConstantOverride("separation", 3);

        // ---- the actions: what you can do about any of it.
        var actions = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(270, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        actions.AddThemeConstantOverride("separation", 7);
        body.AddChild(actions);
        var actionScroll = new ScrollContainer
        {
            Name = "WideDiplomaticActionScroll", SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true, MouseForcePassScrollEvents = false
        };
        actions.AddChild(actionScroll);
        _diplomacyActionRows = new VBoxContainer { Name = "WideDiplomaticActions", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _diplomacyActionRows.AddThemeConstantOverride("separation", 7);
        actionScroll.AddChild(_diplomacyActionRows);

        _diplomacyImproveButton = AddDiplomacyAction("INFLUENCE", "Improve relations",
            ImproveDiplomacyRelations, primary: true);
        _diplomacyImproveButton.TooltipText = Ui.Tip(Tr(
            "A diplomatic mission: money for goodwill. It costs treasury, it may be sent once a " +
            "fortnight to the same country, and what it buys is remembered, not permanent."));
        _diplomacyDenounceButton = AddDiplomacyAction("INFLUENCE", "Denounce",
            DenounceDiplomacyTarget);
        _diplomacyDenounceButton.TooltipText = Ui.Tip(Tr(
            "Say in public what you think of them. Their opinion of you falls - and so, a little, " +
            "does the opinion of anyone who likes them."));
        _diplomacyAllianceButton = AddDiplomacyAction("AGREEMENTS", "Propose alliance",
            ToggleDiplomacyAlliance);
        _diplomacyGuaranteeButton = AddDiplomacyAction("AGREEMENTS", "Guarantee",
            ToggleDiplomacyGuarantee);
        _diplomacyTradeButton = AddDiplomacyAction("AGREEMENTS", "Trade agreement",
            () => ToggleAgreement(AgreementKind.TradeAgreement, DiplomacyTarget(), _diplomacyFeedback));
        _diplomacyPactButton = AddDiplomacyAction("AGREEMENTS", "Non-aggression pact",
            () => ToggleAgreement(AgreementKind.NonAggressionPact, DiplomacyTarget(), _diplomacyFeedback));
        _diplomacyAidButton = AddDiplomacyAction("AGREEMENTS", "Send aid",
            () => SendAidTo(DiplomacyTarget(), _diplomacyFeedback));
        _diplomacyEmbargoButton = AddDiplomacyAction("AGREEMENTS", "Embargo",
            () => ToggleEmbargoOn(DiplomacyTarget(), _diplomacyFeedback));

        _diplomacySubmissionActions = new VBoxContainer { Name = "WideInfluenceActions" };
        _diplomacySubmissionActions.AddThemeConstantOverride("separation", 6);
        _diplomacyActionRows.AddChild(_diplomacySubmissionActions);
        _diplomacySubmissionActions.AddChild(Title("Influence and protection", 12, Ui.Colour("text.caption")));
        foreach (var (route, caption) in new[]
        {
            (SubmissionRoute.Protection, "Offer protection"),
            (SubmissionRoute.Economic, "Buy influence"),
            (SubmissionRoute.Political, "Back a government")
        })
        {
            var captured = route;
            var button = ActionButton(caption, () => OfferSubmission(captured, DiplomacyTarget(), _diplomacyFeedback), 0, 34);
            _diplomacySubmissionActions.AddChild(button);
            _diplomacySubmissionButtons[route] = button;
        }
        _diplomacyPeaceButton = AddDiplomacyAction("War and peace", "Negotiate peace",
            () => OpenDiplomaticPeace(DiplomacyTarget()));
        _diplomacyPeaceButton.Name = "WideNegotiatePeace";
        _diplomacyWarButton = AddDiplomacyAction("War and peace", "Declare war",
            ConfirmOrDeclareDiplomacyWar);
        _diplomacyWarButton.TooltipText = Ui.Tip(Tr("This begins an immediate land war. Click the highlighted button again to confirm."));

        _diplomacySubjectActions = new VBoxContainer { Visible = false };
        _diplomacySubjectActions.AddThemeConstantOverride("separation", 6);
        _diplomacyActionRows.AddChild(_diplomacySubjectActions);
        var subjectHeading = Title("SUBJECTS", 12, Ui.Colour("text.caption"));
        _diplomacySubjectActions.AddChild(subjectHeading);
        _diplomacyReleaseButton = ActionButton("Release", ReleaseDiplomacySubject, 0, 32);
        _diplomacyReleaseButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _diplomacySubjectActions.AddChild(_diplomacyReleaseButton);
        _diplomacyIntegrateButton = ActionButton("Integrate", IntegrateDiplomacySubject, 0, 32);
        _diplomacyIntegrateButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _diplomacySubjectActions.AddChild(_diplomacyIntegrateButton);

        _diplomacyFeedback = Title("", 12, Ui.Colour("text.body_soft"));
        _diplomacyFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _diplomacyFeedback.CustomMinimumSize = new Vector2(0, 44);
        actions.AddChild(_diplomacyFeedback);
    }

    private readonly HashSet<string> _diplomacyActionSections = new();

    private Button AddDiplomacyAction(string section, string caption, System.Action callback, bool primary = false)
    {
        // One heading per section, not one per button: a category is a shelf, and the
        // shelf does not need its own label under every jar on it.
        if (_diplomacyActionSections.Add(section))
            _diplomacyActionRows.AddChild(Title(section, 12, Ui.Colour("text.caption")));
        var button = ActionButton(caption, callback, 0, 34, primary);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _diplomacyActionRows.AddChild(button);
        return button;
    }

    private void AddDiplomacyFilter(HBoxContainer parent, string caption, DiplomacyFilter filter)
    {
        var button = ActionButton(caption, () => SetDiplomacyFilter(filter), 0, 28, filter == _diplomacyFilter);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        parent.AddChild(button);
        _diplomacyFilterButtons[filter] = button;
    }

    private void SetDiplomacyFilter(DiplomacyFilter filter)
    {
        _diplomacyFilter = filter;
        foreach (var (known, button) in _diplomacyFilterButtons)
            StyleButton(button, known == filter);
        RebuildDiplomacyList();
    }

    private void OpenDiplomacyScreen()
    {
        _openScreen = "Diplomacy";
        _sidePanel.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_warLedgerLayer is not null) _warLedgerLayer.Visible = false;
        if (_stateAffairsLayer is not null) _stateAffairsLayer.Visible = false;
        if (_productionLayer is not null) _productionLayer.Visible = false;
        if (_tradeScreenLayer is not null) _tradeScreenLayer.Visible = false;
        if (_selectedNation is null) _selectedNation = _campaign.Player;
        _diplomacySelectedId = _selectedNation?.Id ?? -1;
        _diplomacyLayer.Visible = true;
        _diplomacyLayer.MoveToFront();
        RebuildDiplomacyList(force: true);
        RefreshDiplomacyScreen();
        UpdateScreenButtons();
    }

    private void CloseDiplomacyScreen()
    {
        _diplomacyLayer.Visible = false;
        if (_openScreen == "Diplomacy") _openScreen = "";
        UpdateScreenButtons();
    }

    // ------------------------------------------------------------------ the list

    /// <summary>
    /// Whether a country belongs on the list at all. Neighbours is the one that has to be
    /// asked for rather than scanned for, because "who is next to me" is a question about
    /// the map, and the map's answer is cached on the other side of the diplomacy table.
    /// </summary>
    private bool DiplomacyListShows(Nation nation, HashSet<int> neighbours)
    {
        var player = _campaign.Player;
        if (nation.Id == player.Id) return false;
        if (_diplomacyFilter == DiplomacyFilter.All) return true;
        var relation = CampaignDiplomacy.Peek(_campaign, player.Id, nation.Id);
        return _diplomacyFilter switch
        {
            DiplomacyFilter.Allies => relation is { Stance: DiplomaticStance.Allied } ||
                                      AllianceSystem.AlliesOf(_campaign, nation).Contains(player),
            DiplomacyFilter.Rivals => relation is { Stance: DiplomaticStance.Rival } ||
                                      relation is { Stance: DiplomaticStance.AtWar },
            DiplomacyFilter.AtWar => CampaignDiplomacy.AtWar(_campaign, player.Id, nation.Id),
            DiplomacyFilter.Neighbours => neighbours.Contains(nation.MapIndex),
            _ => true
        };
    }

    private void RebuildDiplomacyList(bool force = false)
    {
        if (_diplomacyLayer is null || !_diplomacyLayer.Visible || _campaign?.Player is null) return;

        var query = _diplomacySearch.Text?.Trim() ?? "";
        var key = $"{_diplomacyFilter}|{query}|{_campaign.ElapsedDays}";
        // The list is rebuilt when what it is made of changes - a filter, a search, a new
        // day - and only has its figures repainted the rest of the time. A list torn down
        // and remade between a mouse going down and coming up is a list that cannot be
        // clicked, and at speed five that is five times a second.
        if (!force && key == _diplomacyListBuiltKey) return;
        _diplomacyListBuiltKey = key;
        _diplomacyListBuiltDay = _campaign.ElapsedDays;

        var player = _campaign.Player;
        HashSet<int> neighbours = null;
        if (_diplomacyFilter == DiplomacyFilter.Neighbours)
        {
            neighbours = new HashSet<int>();
            foreach (var mapIndex in CampaignDiplomacy.Neighbours(_campaign, player.MapIndex))
                neighbours.Add(mapIndex);
        }

        var matches = _campaign.Nations
            .Where(nation => DiplomacyListShows(nation, neighbours))
            .Where(nation => query.Length == 0 ||
                             nation.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             nation.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             nation.Tag.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(nation => nation.Gdp)
            .ToArray();

        while (_diplomacyRows.Count > matches.Length)
        {
            var spare = _diplomacyRows[^1];
            _diplomacyRows.RemoveAt(_diplomacyRows.Count - 1);
            if (GodotObject.IsInstanceValid(spare.Select)) spare.Select.QueueFree();
        }
        for (var index = 0; index < matches.Length; index++)
        {
            if (index >= _diplomacyRows.Count) _diplomacyRows.Add(BuildDiplomacyRow());
            RepaintDiplomacyRow(_diplomacyRows[index], matches[index]);
            _diplomacyListRows.MoveChild(_diplomacyRows[index].Select, index);
        }

        _diplomacySummary.Text = Simulation.Loc.Format("Countries shown: {0:N0}", (float)matches.Length) +
                                 (query.Length > 0 ? " · " + Tr("found by name") : "");
    }

    private DiplomacyRow BuildDiplomacyRow()
    {
        var row = new DiplomacyRow();
        // A Button is not a container: a child laid out inside it goes by its anchors, and
        // a child with no anchors goes nowhere - it sits at the top-left corner at its own
        // minimum size, so the row reads as a one-pixel smear. Anchor the inner box to the
        // full rectangle, inset by the row style's own padding, and give the button the
        // height the row needs, which a plain Button does not take from its children.
        row.Select = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 28) };
        row.Select.AddThemeStyleboxOverride("normal", ShellRowStyle(false));
        row.Select.AddThemeStyleboxOverride("hover", ShellRowStyle(true));
        row.Select.AddThemeStyleboxOverride("pressed", ShellRowStyle(true));
        row.Select.AddThemeStyleboxOverride("focus", ShellRowStyle(false));

        var inner = new HBoxContainer();
        inner.AddThemeConstantOverride("separation", 8);
        inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        inner.OffsetLeft = 9;
        inner.OffsetRight = -9;
        inner.OffsetTop = 3;
        inner.OffsetBottom = -3;
        row.Select.AddChild(inner);

        row.Flag = FlagBadge("", 18);
        inner.AddChild(row.Flag);
        row.Name = Title("", 13, Ui.Colour("text.body"));
        Fixed(row.Name, 0);
        row.Name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        inner.AddChild(row.Name);
        row.Stance = Title("", 11, Ui.Colour("text.caption"));
        Fixed(row.Stance, 64);
        inner.AddChild(row.Stance);
        row.Opinion = Title("", 13, Ui.Colour("text.bright"));
        Fixed(row.Opinion, 44);
        row.Opinion.HorizontalAlignment = HorizontalAlignment.Right;
        inner.AddChild(row.Opinion);

        row.Select.Pressed += () =>
        {
            if (row.Nation is null) return;
            _selectedNation = row.Nation;
            _diplomacySelectedId = row.Nation.Id;
            _globe.SetMapModeSubject(row.Nation);
            RefreshDiplomacyScreen();
        };
        _diplomacyListRows.AddChild(row.Select);
        return row;
    }

    private void RepaintDiplomacyRow(DiplomacyRow row, Nation nation)
    {
        row.Nation = nation;
        SetFlag(row.Flag, nation.Tag, 18);
        row.Select.TooltipText = Ui.Tip(nation.DisplayName + " · " + nation.Tag);
        row.Name.Text = nation.DisplayName;
        var relation = CampaignDiplomacy.Peek(_campaign, _campaign.Player.Id, nation.Id);
        var opinion = relation?.Opinion ?? 0;
        row.Opinion.Text = opinion == 0 ? "·" : opinion.ToString("+0;-0;0");
        row.Opinion.AddThemeColorOverride("font_color",
            opinion > 20 ? Ui.Colour("state.positive") :
            opinion < -20 ? Ui.Colour("state.negative") : Ui.Colour("text.caption"));
        row.Stance.Text = relation is null ? "" : Tr(DiplomaticVocabulary.StanceName(relation.Stance));
        row.Select.Modulate = nation.Id == _diplomacySelectedId
            ? new Color(1.15f, 1.10f, 0.9f)
            : Colors.White;
    }

    // ------------------------------------------------------------------ the page

    private void RefreshDiplomacyScreen()
    {
        if (_diplomacyLayer is null || !_diplomacyLayer.Visible || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var nation = _campaign.Nations.Find(candidate => candidate.Id == _diplomacySelectedId) ?? player;
        _diplomacySelectedId = nation.Id;

        RebuildDiplomacyList();
        foreach (var child in _diplomacyDetailBody.GetChildren())
        {
            _diplomacyDetailBody.RemoveChild(child);
            // The pact rows are kept for the life of the screen and put back further down;
            // freeing them with the rest made every refresh after the first throw there,
            // before the action buttons were brought up to date for the new country.
            if (child != _diplomacyPactRows) child.QueueFree();
        }

        // ---- who they are, in one line at the top of the page.
        var heading = new HBoxContainer();
        heading.AddThemeConstantOverride("separation", 10);
        _diplomacyDetailBody.AddChild(heading);
        var flag = FlagBadge(nation.Tag, 34);
        heading.AddChild(flag);
        var nameBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        heading.AddChild(nameBox);
        var name = Title(nation.DisplayName, 22, Ui.Colour("text.bright"));
        name.AutowrapMode = TextServer.AutowrapMode.Off;
        name.ClipText = true;
        name.AutowrapMode = TextServer.AutowrapMode.Off;
        nameBox.AddChild(name);
        var caption = Title(Simulation.Loc.Format("{0} · {1} · {2}", nation.Tag,
            Tr(PoliticsCore.GovernmentOf(nation).Name), Tr(PoliticsCore.IdeologyOf(nation).Name)),
            12, Ui.Colour("text.caption"));
        caption.AutowrapMode = TextServer.AutowrapMode.Off;
        caption.ClipText = true;
        caption.AutowrapMode = TextServer.AutowrapMode.Off;
        nameBox.AddChild(caption);
        foreach (var info in new[]
        {
            ("Form of government", PoliticsCore.GovernmentOf(nation).Name),
            ("Ideology", PoliticsCore.IdeologyOf(nation).Name)
        })
        {
            var politics = Title(Tr(info.Item1) + ": " + Tr(info.Item2), 13, Ui.Colour("text.body_soft"));
            politics.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _diplomacyDetailBody.AddChild(politics);
        }

        if (nation.Id == player.Id)
        {
            // There is nothing to do to yourself here, so say so and take the buttons
            // away rather than leaving five dead ones on the page.
            _diplomacyActionRows.Visible = false;
            var own = Title("This is your country.", 13, Ui.Colour("text.body_soft"));
            _diplomacyDetailBody.AddChild(own);

            var ownDivisions = DivisionsByNation();
            void OwnRow(string caption, string value)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                _diplomacyDetailBody.AddChild(row);
                var label = Title(caption, 13, Ui.Colour("text.body_soft"));
                Fixed(label, 130);
                row.AddChild(label);
                var figure = Title(value, 13, Ui.Colour("text.bright"));
                Fixed(figure, 0);
                figure.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                figure.HorizontalAlignment = HorizontalAlignment.Right;
                row.AddChild(figure);
            }
            OwnRow(Tr("GDP"), Fmt.Money(player.Gdp));
            OwnRow(Tr("Divisions"), ownDivisions.GetValueOrDefault(player.Id).ToString("0.0"));
            OwnRow(Tr("Population"), Fmt.People(player.Population));
            OwnRow(Tr("Stability"), Fmt.Percent(player.Stability));
            return;
        }
        _diplomacyActionRows.Visible = true;

        // ---- what the balance of power says, as a sentence and then as the rows behind it.
        var mine = DiplomacyStrength(player);
        var theirs = DiplomacyStrength(nation);
        var ratio = theirs <= 0.0001f ? 99f : mine / theirs;
        var banner = Title(
            ratio > 2.5f ? Tr("We are much stronger than them.") :
            ratio > 1.25f ? Tr("We are stronger than them.") :
            ratio > 0.8f ? Tr("We are evenly matched.") :
            ratio > 0.4f ? Tr("We are weaker than them.") :
            Tr("We are much weaker than them."),
            14, Ui.Colour("text.accent"));
        _diplomacyDetailBody.AddChild(banner);

        var divisions = DivisionsByNation();
        void CompareRow(string caption, float ours, float theirs2, string ourText, string theirText)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _diplomacyDetailBody.AddChild(row);
            var label = Title(caption, 13, Ui.Colour("text.body_soft"));
            Fixed(label, 130);
            row.AddChild(label);
            var us = Title(ourText, 13, Ui.Colour(ours >= theirs2 ? "text.bright" : "text.muted"));
            Fixed(us, 130);
            us.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(us);
            var them = Title(theirText, 13, Ui.Colour(ours >= theirs2 ? "text.muted" : "text.bright"));
            Fixed(them, 130);
            them.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(them);
        }

        var canSee = CanInspectNation(nation.Id);
        CompareRow(Tr("GDP"), player.Gdp, nation.Gdp, Fmt.Money(player.Gdp),
            canSee ? Fmt.Money(nation.Gdp) : "?");
        CompareRow(Tr("Divisions"), divisions.GetValueOrDefault(player.Id), divisions.GetValueOrDefault(nation.Id),
            divisions.GetValueOrDefault(player.Id).ToString("0.0"), canSee ? divisions.GetValueOrDefault(nation.Id).ToString("0.0") : "?");
        CompareRow(Tr("Population"), player.Population, nation.Population,
            Fmt.People(player.Population), canSee ? Fmt.People(nation.Population) : "?");
        CompareRow(Tr("Stability"), player.Stability, nation.Stability,
            Fmt.Percent(player.Stability), canSee ? Fmt.Percent(nation.Stability) : "?");

        // ---- the opinion, and what it is made of.
        var relation = CampaignDiplomacy.Get(_campaign, player.Id, nation.Id);
        var opinionHeading = Title(Simulation.Loc.Format("Their opinion of us: {0:+0;-0;0}", relation.Opinion),
            14, relation.Opinion >= 0 ? Ui.Colour("state.positive") : Ui.Colour("state.negative"));
        _diplomacyDetailBody.AddChild(opinionHeading);

        foreach (var entry in relation.OpinionDrivers.Entries
                     .OrderByDescending(item => Math.Abs(item.Value))
                     .Take(6))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _diplomacyDetailBody.AddChild(row);
            var dot = Title(entry.Value >= 0 ? "▲" : "▼", 11,
                Ui.Colour(entry.Value >= 0 ? "state.positive" : "state.negative"));
            Fixed(dot, 14);
            row.AddChild(dot);
            var reason = Title(entry.Source, 12, Ui.Colour("text.body_soft"));
            Fixed(reason, 0);
            reason.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            reason.ClipText = true;
            reason.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(reason);
            var size = Title(entry.Value.ToString("+0.0;-0.0;0.0"), 12,
                Ui.Colour(entry.Value >= 0 ? "state.positive" : "state.negative"));
            Fixed(size, 42);
            row.AddChild(size);
        }

        // ---- the war, if there is one.
        var war = DiplomaticPeaceWar(nation);
        if (war is not null)
        {
            var score = war.IsAttacking(player.Id) ? war.WarScore : -war.WarScore;
            var warLine = Title(Simulation.Loc.Format("At war · war score {0:+0.0;-0.0;0.0}", score),
                13, Ui.Colour("state.negative"));
            _diplomacyDetailBody.AddChild(warLine);
        }

        // ---- pacts, embargoes, arrangements, and what their trade is worth.
        _diplomacyDetailBody.AddChild(_diplomacyPactRows);
        while (_diplomacyPactRows.GetChildCount() > 0)
        {
            var child = _diplomacyPactRows.GetChild(0);
            _diplomacyPactRows.RemoveChild(child);
            child.QueueFree();
        }
        RefreshDiplomacyPacts(player, nation, relation, war is not null);

        RefreshDiplomacyActions(player, nation);
    }

    /// <summary>
    /// One number that answers "could we take them". GDP for the weight of the country,
    /// divisions for the weight of the army, population for the weight left to draw on.
    /// Deliberately coarse: the sentence at the top of the page is an opening position,
    /// not a verdict.
    /// </summary>
    private static float DiplomacyStrength(Nation nation) =>
        (float)(nation.Gdp / 1_000_000_000_000.0) * 1.5f + nation.Population / 100_000_000f;

    private Dictionary<int, float> DivisionsByNation()
    {
        var table = new Dictionary<int, float>();
        foreach (var army in _campaign.Armies)
            table[army.NationId] = table.GetValueOrDefault(army.NationId) + army.Divisions;
        return table;
    }

    private void RefreshDiplomacyPacts(Nation player, Nation nation, DiplomaticRelation relation, bool atWar)
    {
        void Pact(string caption, string value, string colourToken = "text.bright")
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _diplomacyPactRows.AddChild(row);
            var label = Title(caption, 13, Ui.Colour("text.body_soft"));
            Fixed(label, 150);
            row.AddChild(label);
            var figure = Title(value, 13, Ui.Colour(colourToken));
            figure.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            figure.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            figure.ClipText = true;
            figure.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(figure);
        }

        var allies = AllianceSystem.AlliesOf(_campaign, nation);
        if (relation.Stance == DiplomaticStance.Allied)
            Pact(Tr("Alliance"), Tr("signed with you"), "state.positive");
        if (AllianceSystem.OfferedTo(_campaign, nation) is { } offered && offered.Id == player.Id)
            Pact(Tr("Alliance"), Tr("they have offered; answer it here"), "state.info");
        if (relation.NonAggressionPact)
            Pact(Tr("Non-aggression pact"), Tr("signed with you"));
        if (relation.GuaranteedBy(player.Id))
            Pact(Tr("Guarantee"), Tr("you have promised to fight for them"), "state.info");
        if (relation.GuaranteedBy(nation.Id))
            Pact(Tr("Guaranteed by them"), Tr("they have promised to fight for you"), "state.positive");
        if (relation.EmbargoBy(player.Id))
            Pact(Tr("Embargo"), Tr("yours: nothing of theirs crosses your border"), "state.warning");
        if (relation.EmbargoBy(nation.Id))
            Pact(Tr("Embargo"), Tr("theirs: nothing of yours crosses theirs"), "state.warning");
        if (allies.Count > 0)
            Pact(Tr("Allied to"), string.Join(", ", allies.Take(6).Select(ally => ally.Tag)) +
                                 (allies.Count > 6 ? " +" + (allies.Count - 6) : ""));
        if (nation.AnswersTo(player.Id))
            Pact(Tr("Answers to"), Tr("you") + " · " + nation.SubjectType);
        var subjects = SubjectSystem.SubjectsOf(_campaign, nation);
        if (subjects.Count > 0)
            Pact(Tr("Subjects"), string.Join(", ", subjects.Take(6).Select(subject => subject.Tag)));

        // What their trade is worth is the first thing a peace treaty weighs and the last
        // thing the old screen ever showed.
        var through = player.TradePartners.GetValueOrDefault(nation.Id);
        if (through > 0.0001f)
            Pact(Tr("Trade through them"), Simulation.Loc.Format("{0} a week · {1:P0} of all your trade",
                Fmt.Money(through), player.TradeDependenceOn(nation.Id)));
    }

    // ------------------------------------------------------------------ the actions

    private void RefreshDiplomacyActions(Nation player, Nation nation)
    {
        RefreshAgreementButtons(nation, _diplomacyTradeButton, _diplomacyPactButton, _diplomacyAidButton, _diplomacyEmbargoButton);
        RefreshSubmissionButtons(nation, _diplomacySubmissionActions, _diplomacySubmissionButtons);
        RefreshDiplomaticPeaceButton(_diplomacyPeaceButton, nation);
        _diplomacyWarButton.Visible = !_diplomacyPeaceButton.Visible;
        var relation = CampaignDiplomacy.Get(_campaign, player.Id, nation.Id);
        var cooldown = _campaign.ElapsedDays - relation.LastActionDay < 14;
        _diplomacyImproveButton.Disabled = cooldown || player.Treasury < 0.1f;
        _diplomacyDenounceButton.Disabled = cooldown;

        if (relation.Stance == DiplomaticStance.Allied)
        {
            _diplomacyAllianceButton.Text = Tr("Break alliance");
            _diplomacyAllianceButton.Disabled = false;
            _diplomacyAllianceButton.TooltipText = Ui.Tip(Tr("Walk out. They remember it, and so does everybody watching."));
        }
        else if (AllianceSystem.OfferedTo(_campaign, player) is { } waiting && waiting.Id == nation.Id)
        {
            _diplomacyAllianceButton.Text = Tr("Accept alliance");
            _diplomacyAllianceButton.Disabled = false;
            _diplomacyAllianceButton.TooltipText = Ui.Tip(Tr("They asked. Accepting binds you to their wars and them to yours."));
        }
        else
        {
            _diplomacyAllianceButton.Text = Tr("Propose alliance");
            var can = AllianceSystem.CanPropose(_campaign, player, nation, out var refusal);
            _diplomacyAllianceButton.Disabled = !can;
            if (!can)
            {
                _diplomacyAllianceButton.TooltipText = Ui.Tip(refusal);
            }
            else
            {
                var why = new Simulation.Breakdown();
                var willingness = AllianceSystem.Willingness(_campaign, nation, player, why);
                var needed = _campaign.Defines.AllianceWillingness;
                var parts = why.Entries
                    .OrderByDescending(entry => Math.Abs(entry.Value))
                    .Take(3)
                    .Select(entry => $"{entry.Source} {entry.Value:+0.00;-0.00;0.00}");
                _diplomacyAllianceButton.TooltipText = Ui.Tip(
                    (willingness > needed
                        ? Simulation.Loc.Format("They would sign: {0:0.00} against {1:0.00} needed.", willingness, needed)
                        : Simulation.Loc.Format("They would refuse: {0:0.00} against {1:0.00} needed.", willingness, needed)) +
                    "\n" + string.Join("\n", parts));
            }
        }

        var guaranteed = relation.GuaranteedBy(player.Id);
        _diplomacyGuaranteeButton.Text = guaranteed ? Tr("Withdraw guarantee") : Tr("Guarantee");
        _diplomacyGuaranteeButton.Disabled = relation.Stance == DiplomaticStance.AtWar;
        _diplomacyGuaranteeButton.TooltipText = Ui.Tip(guaranteed
            ? Tr("Take back the promise. They will notice.")
            : Tr("Promise to fight for them if they are attacked. It costs nothing until it costs everything."));

        var canDeclare = CampaignDiplomacy.CanDeclareWar(_campaign, player.Id, nation.Id, out var reason);
        _diplomacyWarButton.Disabled = !canDeclare;
        if (!canDeclare) _diplomacyWarButton.TooltipText = Ui.Tip(reason);
        _diplomacyWarButton.Text = _diplomacyPendingWarTarget == nation.Id
            ? Simulation.Loc.Format("CONFIRM WAR ON {0}", nation.Tag)
            : Tr("Declare war");

        _diplomacySubjectActions.Visible = nation.AnswersTo(player.Id);
        if (_diplomacySubjectActions.Visible)
        {
            var may = SubjectSystem.MayAnnex(_campaign, player, nation, out var why);
            _diplomacyIntegrateButton.Disabled = !may;
            _diplomacyIntegrateButton.TooltipText = Ui.Tip(may
                ? Tr("Take it into your country. Its regions become yours.")
                : why);
        }
    }

    private Nation DiplomacyTarget()
    {
        if (_campaign?.Player is null) return null;
        return _campaign.Nations.Find(candidate => candidate.Id == _diplomacySelectedId);
    }

    private bool ValidDiplomacyTarget()
    {
        var target = DiplomacyTarget();
        return target is not null && target != _campaign.Player;
    }

    private void ImproveDiplomacyRelations()
    {
        if (!ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, target.Id);
        var before = relation.Opinion;
        _campaign.Commands.Enqueue(new ImproveRelationsCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id });
        _campaign.Commands.ApplyAll(_campaign);
        _diplomacyPendingWarTarget = -1;
        _diplomacyFeedback.Text = relation.Opinion > before
            ? Simulation.Loc.Format("Mission received: their opinion of us is {0:+0;-0;0}.", relation.Opinion)
            : Tr("Diplomatic action is on cooldown or the treasury is insufficient.");
        RefreshDiplomacyScreen();
    }

    private void DenounceDiplomacyTarget()
    {
        if (!ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, target.Id);
        var before = relation.Opinion;
        _campaign.Commands.Enqueue(new DenounceCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id });
        _campaign.Commands.ApplyAll(_campaign);
        _diplomacyPendingWarTarget = -1;
        _diplomacyFeedback.Text = relation.Opinion < before
            ? Simulation.Loc.Format("Denunciation issued: their opinion of us is {0:+0;-0;0}.", relation.Opinion)
            : Tr("Diplomatic action is still on cooldown.");
        RefreshDiplomacyScreen();
    }

    private void ToggleDiplomacyAlliance()
    {
        if (!ValidDiplomacyTarget()) return;
        var player = _campaign.Player;
        var target = DiplomacyTarget();
        var relation = CampaignDiplomacy.Get(_campaign, player.Id, target.Id);

        if (relation.Stance == DiplomaticStance.Allied)
        {
            _campaign.Commands.Enqueue(new BreakAllianceCommand { IssuerId = player.Id, TargetId = target.Id });
            _campaign.Commands.ApplyAll(_campaign);
            _diplomacyFeedback.Text = Simulation.Loc.Format("Left the alliance with {0}.", target.DisplayName);
        }
        else if (AllianceSystem.OfferedTo(_campaign, player) is { } waiting && waiting.Id == target.Id)
        {
            _campaign.Commands.Enqueue(new AnswerAllianceCommand { IssuerId = player.Id, Accept = true });
            _campaign.Commands.ApplyAll(_campaign);
            _diplomacyFeedback.Text = Simulation.Loc.Format("Allied with {0}.", target.DisplayName);
        }
        else if (!AllianceSystem.CanPropose(_campaign, player, target, out var refusal))
        {
            _diplomacyFeedback.Text = refusal;
        }
        else
        {
            _campaign.Commands.Enqueue(new ProposeAllianceCommand { IssuerId = player.Id, TargetId = target.Id });
            _campaign.Commands.ApplyAll(_campaign);
            _diplomacyFeedback.Text = relation.Stance == DiplomaticStance.Allied
                ? Simulation.Loc.Format("{0} accepted.", target.DisplayName)
                : Simulation.Loc.Format("{0} declined.", target.DisplayName);
        }
        RefreshDiplomacyScreen();
    }

    private void ToggleDiplomacyGuarantee()
    {
        if (!ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, target.Id);
        var on = !relation.GuaranteedBy(_campaign.Player.Id);
        _campaign.Commands.Enqueue(new GuaranteeCommand
        {
            IssuerId = _campaign.Player.Id,
            TargetId = target.Id,
            Guarantee = on
        });
        _campaign.Commands.ApplyAll(_campaign);
        _diplomacyFeedback.Text = relation.GuaranteedBy(_campaign.Player.Id)
            ? Simulation.Loc.Format("You guarantee {0}.", target.DisplayName)
            : Simulation.Loc.Format("You no longer guarantee {0}.", target.DisplayName);
        RefreshDiplomacyScreen();
    }

    private void ConfirmOrDeclareDiplomacyWar()
    {
        if (!ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        if (!CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, target.Id, out var reason))
        {
            _diplomacyFeedback.Text = reason;
            return;
        }
        if (_diplomacyPendingWarTarget != target.Id)
        {
            _diplomacyPendingWarTarget = target.Id;
            _diplomacyFeedback.Text = Tr("This begins an immediate land war. Click the highlighted button again to confirm.");
            RefreshDiplomacyScreen();
            return;
        }
        _campaign.Commands.Enqueue(new DeclareWarCampaignCommand
        {
            IssuerId = _campaign.Player.Id,
            TargetId = target.Id,
            ClaimedStateMapIndex = _selectedState?.NationMapIndex == target.MapIndex
                ? _selectedState.MapIndex
                : -1
        });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("war_declared");
        _diplomacyPendingWarTarget = -1;
        _diplomacyFeedback.Text = Simulation.Loc.Format("War declared on {0}.", target.DisplayName);
        RefreshDiplomacyScreen();
    }

    private void ReleaseDiplomacySubject()
    {
        if (_campaign?.Player is null || !ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        if (!target.AnswersTo(_campaign.Player.Id)) return;
        SubjectSystem.Release(_campaign, target);
        _diplomacyFeedback.Text = Simulation.Loc.Format("{0} answers to nobody now.", target.DisplayName);
        RefreshDiplomacyScreen();
        Refresh();
    }

    private void IntegrateDiplomacySubject()
    {
        if (_campaign?.Player is null || !ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        var name = target.DisplayName;
        if (!SubjectSystem.Annex(_campaign, _campaign.Player, target, out var refusal))
        {
            _diplomacyFeedback.Text = refusal;
            return;
        }
        _diplomacySelectedId = _campaign.Player.Id;
        _diplomacyFeedback.Text = Simulation.Loc.Format("{0} is part of your country now.", name);
        RebuildDiplomacyList(force: true);
        RefreshDiplomacyScreen();
        Refresh();
    }
}
