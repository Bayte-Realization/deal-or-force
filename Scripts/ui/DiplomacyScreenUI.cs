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
    private Control _diplomacyActionColumn = null!;
    private Label _diplomacyFeedback = null!;
    private Button _diplomacyImproveButton = null!;
    private Button _diplomacyDenounceButton = null!;
    private Button _diplomacyAllianceButton = null!;
    private Button _diplomacyGuaranteeButton = null!;
    private Button _diplomacyWarButton = null!;
    private Button _diplomacyUltimatumButton = null!;
    private Button _diplomacyDealButton = null!;
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
        public Label Identity;
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
        var ownCountry = ActionButton("My country", () => SelectDiplomacyNation(_campaign.Player), 0, 32,
            iconPath: "res://game/gfx/interface/icons/icon_diplomacy.png");
        ownCountry.Name = "DiplomacyMyCountry";
        ownCountry.TooltipText = Ui.Tip(Tr("Open your own country's diplomacy dossier."));
        header.AddChild(ownCountry);
        _diplomacyInboxButton = ActionButton("Diplomatic inbox", OpenDealInbox, 0, 32);
        _diplomacyInboxButton.Name = "DiplomacyInbox";
        _diplomacyInboxButton.Disabled = _observerMode;
        header.AddChild(_diplomacyInboxButton);
        header.AddChild(ActionButton("Organizations", () => { CloseDiplomacyScreen(); _conflictTab = 4; OpenScreen("Conflicts"); }, 0, 32,
            iconPath: "res://game/gfx/interface/icons/icon_diplomacy.png"));
        header.AddChild(HandbookButton("diplomacy"));
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

        _diplomacySearch = Placeholder(new LineEdit
        {
            CustomMinimumSize = new Vector2(0, 30),
            RightIcon = null,
            ClearButtonEnabled = true
        }, "Find a country...");
        _diplomacySearch.Name = "DiplomacySearch";
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
        _diplomacyListRows = new VBoxContainer { Name = "DiplomacyListRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
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
            // The same idiom as the action column below: a VBoxContainer inside a ScrollContainer
            // is sized to its content unless it is told to fill, so the page's inset panels stop
            // short of the viewport and the column reads as unfinished rather than as a page that
            // simply has less on it today. The children keep their own flags, so nothing moves -
            // the container is the thing that now reaches the bottom.
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420, 0)
        };
        _diplomacyDetailBody.AddThemeConstantOverride("separation", 12);
        pageScroll.AddChild(_diplomacyDetailBody);
        _diplomacyDetailRows = _diplomacyDetailBody;

        // ---- the actions: what you can do about any of it.
        var actions = new VBoxContainer
        {
            Name = "DiplomacyActionColumn",
            CustomMinimumSize = new Vector2(270, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        actions.AddThemeConstantOverride("separation", 7);
        _diplomacyActionColumn = actions;
        body.AddChild(actions);
        var actionScroll = new ScrollContainer
        {
            Name = "WideDiplomaticActionScroll", SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true, MouseForcePassScrollEvents = false
        };
        actions.AddChild(actionScroll);
        _diplomacyActionRows = new VBoxContainer { Name = "WideDiplomaticActions", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
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
        _diplomacyDealButton = AddDiplomacyAction("AGREEMENTS", "Propose a deal",
            ConfirmOrProposeDeal);
        _diplomacyDealButton.Name = "ProposeDealWide";
        _diplomacyDealButton.TooltipText = Ui.Tip(Tr(
            "Lay a table both sides put something on: cash, provinces, promises. Review before sending."));
        _diplomacyAidButton = AddDiplomacyAction("AGREEMENTS", "Send aid",
            () => SendAidTo(DiplomacyTarget(), _diplomacyFeedback));
        _diplomacyRequestAssistance = AddDiplomacyAction("AGREEMENTS", "Request funds",
            () => RequestAssistanceFrom(DiplomacyTarget()));
        _diplomacyRequestAssistance.Name = "RequestFinancialAssistance";
        _diplomacyEmbargoButton = AddDiplomacyAction("AGREEMENTS", "Embargo",
            () => ToggleEmbargoOn(DiplomacyTarget(), _diplomacyFeedback));
        AddIntelNetworkAction();

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
        _diplomacyUltimatumButton = AddDiplomacyAction("War and peace", "Issue ultimatum",
            ConfirmOrIssueUltimatum);
        _diplomacyUltimatumButton.Name = "IssueUltimatumWide";
        _diplomacyUltimatumButton.TooltipText = Ui.Tip(Tr(
            "Compose territorial, financial and treaty demands. Review before sending."));

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

        // Spacer takes all leftover vertical room so the action buttons never float in an
        // empty actions column when only a few actions are available for the selected country.
        var _ = new Control { SizeFlagsVertical = SizeFlags.ExpandFill };
        _diplomacyActionRows.AddChild(_);

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
        button.ClipText = true;
        button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
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
        if (_selectedNation is null || _selectedNation.Id == _campaign.Player.Id)
            _selectedNation = DiplomacyDefaultPartner() ?? _selectedNation;
        _diplomacySelectedId = _selectedNation?.Id ?? -1;
        RaiseWorkspaceLayer(_diplomacyLayer);
        RebuildDiplomacyList(force: true);
        ForceDiplomacyRefresh();
        UpdateScreenButtons();
    }

    /// <summary>
    /// Who diplomacy opens on when the player has not chosen anybody: the neighbour the player
    /// is on worst terms with, or the largest country in the world if there is no land
    /// neighbour. The choice itself lives in the simulation (<see cref="CampaignDiplomacy.DefaultPartner"/>)
    /// so it can be tested without a scene; this only asks for it.
    /// </summary>
    private Nation DiplomacyDefaultPartner() =>
        CampaignDiplomacy.DefaultPartner(_campaign, _campaign?.Player);

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
                             nation.Tag.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             Simulation.StateIdentity.Caption(_campaign, nation, true)
                                 .Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             Simulation.StateIdentity.Caption(_campaign, nation, false)
                                 .Contains(query, StringComparison.OrdinalIgnoreCase))
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
            // A stable name per place in the list, for tests and tours to press.
            _diplomacyRows[index].Select.Name = "DiplomacyRow" + index;
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
        row.Select = new Button { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 44) };
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
        // The name and, under it, what the state says it is: the two facts a player scans a
        // country list for before opening anything.
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        text.AddThemeConstantOverride("separation", 0);
        inner.AddChild(text);
        row.Name = Title("", 13, Ui.Colour("text.body"));
        row.Name.Name = "DiplomacyCountryName";
        Fixed(row.Name, 0);
        row.Name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.Name.MaxLinesVisible = 3;
        row.Name.VerticalAlignment = VerticalAlignment.Center;
        row.Name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AddChild(row.Name);
        row.Identity = Title("", 10, Ui.Colour("text.muted"));
        row.Identity.Name = "DiplomacyCountryIdentity";
        row.Identity.ClipText = true;
        // Both demographics are useful when choosing a diplomatic partner. Ellipsising
        // this line hid the culture almost everywhere, especially in Russian at 720p.
        // Wrap in the existing column instead of widening the whole window over the map.
        row.Identity.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.Identity.MaxLinesVisible = -1;
        text.AddChild(row.Identity);
        row.Stance = Title("", 11, Ui.Colour("text.caption"));
        Fixed(row.Stance, 64);
        row.Stance.VerticalAlignment = VerticalAlignment.Center;
        inner.AddChild(row.Stance);
        row.Opinion = Title("", 13, Ui.Colour("text.bright"));
        Fixed(row.Opinion, 44);
        row.Opinion.HorizontalAlignment = HorizontalAlignment.Right;
        row.Opinion.VerticalAlignment = VerticalAlignment.Center;
        inner.AddChild(row.Opinion);

        float lastNameWidth = -1;
        row.Name.Resized += () =>
        {
            if (Math.Abs(row.Name.Size.X - lastNameWidth) < .5f) return;
            lastNameWidth = row.Name.Size.X;
            FitDiplomacyName(row);
        };

        row.Select.Pressed += () => SelectDiplomacyNation(row.Nation);
        _diplomacyListRows.AddChild(row.Select);
        return row;
    }

    /// <summary>The declared name with the exact share of the country it stands on.</summary>
    private string StateIdentityWithShare(Nation nation, bool religion)
    {
        var caption = Simulation.StateIdentity.Caption(_campaign, nation, religion);
        if (caption == Tr("none declared")) return caption;
        var share = religion
            ? Simulation.StateIdentity.ReligionShare(_campaign, nation, nation.DeclaredReligion)
            : Simulation.StateIdentity.CultureShare(_campaign, nation, nation.DeclaredCulture);
        return Simulation.Loc.Format("{0} · {1:0.0%} of the country", caption, share);
    }

    private void RepaintDiplomacyRow(DiplomacyRow row, Nation nation)
    {
        row.Nation = nation;
        SetFlag(row.Flag, nation.Tag, 18);
        // Who the state says it is, on every row: the two names a diplomat scans for, with
        // the exact share each stands on. The same line answers the map tooltip and the
        // country dossier, so the number cannot disagree between screens.
        var identity = Simulation.StateIdentity.Brief(_campaign, nation);
        row.Identity.Text = identity;
        row.Identity.TooltipText = Ui.Tip(identity);
        row.Select.TooltipText = Ui.Tip(nation.DisplayName + " · " + nation.Tag + "\n" + identity);
        row.Name.Text = nation.DisplayName;
        FitDiplomacyName(row);
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

    // Use the label's shaped lines, not a separate font estimate: fallback glyphs,
    // theme line spacing and the actual HBox width all affect the final height.
    private static float ReserveWrappedLabelLines(Label label)
    {
        var lines = Math.Max(1, label.GetLineCount());
        if (label.MaxLinesVisible > 0) lines = Math.Min(lines, label.MaxLinesVisible);
        var height = Mathf.Ceil(Math.Max(14f, label.GetLineHeight()) * lines +
            label.GetThemeConstant("line_spacing") * (lines - 1) + 2);
        label.CustomMinimumSize = new Vector2(label.CustomMinimumSize.X, height);
        return height;
    }

    private static void FitDiplomacyName(DiplomacyRow row)
    {
        ReserveWrappedLabelLines(row.Name);
        ReserveWrappedLabelLines(row.Identity);
        // Both the wrapped name AND identity line (plus flag/opinion) must fit.
        // Reserving only the first label shrank a two-line row to 28 pixels.
        var content = row.Name.GetParent().GetParent() as Control;
        row.Select.CustomMinimumSize = new Vector2(0, Math.Max(44f, (content?.GetCombinedMinimumSize().Y ?? 38f) + 6f));
    }

    // ------------------------------------------------------------------ the page

    private string _diplomacyPageKey = "";

    /// <summary>The page again now, whatever the day: something on it has just been done.</summary>
    private void ForceDiplomacyRefresh()
    {
        _diplomacyPageKey = "";
        RefreshDiplomacyScreen();
        foreach (var row in _diplomacyRows)
            if (row.Nation is not null) RepaintDiplomacyRow(row, row.Nation);
    }

    /// <summary>Another country on this page - from the list, or from a flag on the page itself.</summary>
    private void SelectDiplomacyNation(Nation nation)
    {
        if (nation is null) return;
        _selectedNation = nation;
        _diplomacySelectedId = nation.Id;
        _globe.SetMapModeSubject(nation);
        ForceDiplomacyRefresh();
    }

    /// <summary>The selected row lit, without rebuilding a list that has not changed.</summary>
    private void MarkDiplomacySelection()
    {
        foreach (var row in _diplomacyRows)
            if (row.Nation is not null && GodotObject.IsInstanceValid(row.Select))
                row.Select.Modulate = row.Nation.Id == _diplomacySelectedId ? new Color(1.15f, 1.10f, 0.9f) : Colors.White;
    }

    /// <summary>The war page for a war, from wherever the war was mentioned.</summary>
    private void OpenWarPage(int warId)
    {
        CloseDiplomacyScreen();
        OpenWarLedger();
        ShowWarLedgerDetail(warId);
    }

    /// <summary>
    /// A country, as a button: its flag and its name. Pressed, this page turns to it - so an
    /// ally of an ally is one click away rather than a search.
    /// </summary>
    private Button DiplomacyChip(Nation member, string suffix = "")
    {
        var chip = new Button
        {
            Text = member.DisplayName + suffix,
            Icon = FlagTexture(member.Tag),
            // Not expanded: an expanded icon adds nothing to a button's minimum width, and a
            // chip in a flow is given exactly its minimum - the flag would get no room at all.
            ExpandIcon = false,
            CustomMinimumSize = new Vector2(0, 24),
            TooltipText = Ui.Tip(member.DisplayName + " · " + member.Tag)
        };
        chip.AddThemeConstantOverride("icon_max_width", 22);
        chip.AddThemeConstantOverride("h_separation", 6);
        chip.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button_small", 12));
        chip.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
        chip.AddThemeColorOverride("font_hover_color", Ui.Colour("text.bright"));
        StyleBoxFlat Style(bool hover)
        {
            var style = new StyleBoxFlat
            {
                BgColor = Ui.Colour(hover ? "war.card_hover" : "war.card"),
                BorderColor = Ui.Colour(hover ? "war.card_border_hover" : "war.card_border"),
                ContentMarginLeft = 6, ContentMarginRight = 8, ContentMarginTop = 2, ContentMarginBottom = 2
            };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(3);
            return style;
        }
        chip.AddThemeStyleboxOverride("normal", Style(false));
        chip.AddThemeStyleboxOverride("hover", Style(true));
        chip.AddThemeStyleboxOverride("pressed", Style(true));
        chip.AddThemeStyleboxOverride("focus", Style(true));
        var captured = member;
        chip.Pressed += () => SelectDiplomacyNation(captured);
        return chip;
    }

    /// <summary>
    /// Countries as a flow of chips, or a word saying there are none. A bloc of sixty is shown
    /// by its first two dozen and a count: the page is about one country, not about its bloc.
    /// </summary>
    private Control DiplomacyChips(IEnumerable<Nation> members, string none, Func<Nation, string> suffix = null)
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        var limit = (int)Ui.Metric("diplomacy.chips_shown", 24f);
        var shown = 0;
        var more = 0;
        foreach (var member in members)
        {
            if (shown >= limit) { more++; continue; }
            flow.AddChild(DiplomacyChip(member, suffix?.Invoke(member) ?? ""));
            shown++;
        }
        if (more > 0)
        {
            var rest = RowLabel("", 12, Ui.Colour("text.muted"));
            rest.Text = Simulation.Loc.Format("+{0} more", more.ToString("N0"));
            rest.CustomMinimumSize = new Vector2(0, 24);
            flow.AddChild(rest);
        }
        if (flow.GetChildCount() > 0) return flow;
        flow.QueueFree();
        var label = RowLabel(none, 12, Ui.Colour("text.muted"));
        return label;
    }

    /// <summary>
    /// The page for one country, laid out as the Wars screen is: the country across the top with
    /// what it thinks of us, then fields to read across rather than down - the balance of power
    /// as bars of ours against theirs, what the opinion is made of, the war if there is one, and
    /// every tie between us, the countries in them shown by their flags and one click away.
    /// Opened on the player's own country, it is the player's standing in the world: allies,
    /// subjects, wars, and who thinks well and ill of us.
    ///
    /// It is rebuilt when the day turns or the country changes rather than on every step: a page
    /// torn down several times a second is a page whose buttons cannot be clicked.
    /// </summary>
    private void RefreshDiplomacyScreen()
    {
        if (_diplomacyLayer is null || !_diplomacyLayer.Visible || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var nation = _campaign.Nations.Find(candidate => candidate.Id == _diplomacySelectedId) ?? player;
        _diplomacySelectedId = nation.Id;

        RebuildDiplomacyList();
        MarkDiplomacySelection();
        var key = $"{nation.Id}|{_campaign.ElapsedDays}|{_diplomacyPendingWarTarget}";
        if (key == _diplomacyPageKey) return;
        _diplomacyPageKey = key;
        foreach (var child in _diplomacyDetailBody.GetChildren())
        {
            _diplomacyDetailBody.RemoveChild(child);
            child.QueueFree();
        }

        Label Text(string words, int size, Color colour)
        {
            var label = RowLabel("", size, colour);
            label.Text = words;
            return label;
        }

        Label Giving(Label label)
        {
            label.ClipText = false;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }

        // A recessed field with a caption, and a note on the caption's right when it has one.
        VBoxContainer Field(string caption, string aside = "", Color? asideColour = null)
        {
            var panel = new PanelContainer();
            panel.AddThemeStyleboxOverride("panel", WarInsetStyle(16, 12));
            AtlasSurfaceSkin.Attach(panel, AtlasSurfaceSkin.Level.Inset, "war.card");
            _diplomacyDetailBody.AddChild(panel);
            var stack = new VBoxContainer();
            stack.AddThemeConstantOverride("separation", 7);
            panel.AddChild(stack);
            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 10);
            head.AddChild(Giving(RowLabel(caption, 11, Ui.Colour("text.caption"))));
            if (aside.Length > 0)
            {
                var note = Text(aside, 13, asideColour ?? Ui.Colour("text.accent"));
                note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                note.HorizontalAlignment = HorizontalAlignment.Right;
                head.AddChild(note);
            }
            stack.AddChild(head);
            return stack;
        }

        WarBar Bar(float height, float width = 0f) => new()
        {
            CustomMinimumSize = new Vector2(width, height),
            SizeFlagsHorizontal = width > 0f ? SizeFlags.ShrinkCenter : SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Track = Ui.Colour("war.track"),
            MarkerColour = Ui.Colour("war.front_line")
        };

        var ourColour = WarSideColour(player);
        var theirColour = WarSideColour(nation);
        var positive = Ui.Colour("state.positive");
        var negative = Ui.Colour("state.negative");
        var own = nation.Id == player.Id;
        var canSee = own || CanInspectNation(nation.Id);

        // ---- who they are, and what they think of us.
        var banner = new PanelContainer { Name = "DiplomacyCountryBanner" };
        banner.AddThemeStyleboxOverride("panel", WarInsetStyle(18, 14));
        AtlasSurfaceSkin.Attach(banner, AtlasSurfaceSkin.Level.Raised, "war.card");
        _diplomacyDetailBody.AddChild(banner);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 14);
        banner.AddChild(top);
        var flag = FlagBadge(nation.Tag, 46);
        TintBlankFlag(flag, nation);
        top.AddChild(flag);
        var who = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        who.AddThemeConstantOverride("separation", 1);
        top.AddChild(who);
        who.AddChild(Giving(Text(nation.DisplayName, 22, Ui.Colour("text.bright"))));
        who.AddChild(Giving(Text(Simulation.Loc.Format("{0} · {1} · {2}", nation.Tag,
            Tr(PoliticsCore.GovernmentOf(nation).Name), Tr(PoliticsCore.IdeologyOf(nation).Name)), 12, Ui.Colour("text.caption"))));
        // What the state says it is, over what its people are - the line a diplomat reads
        // before deciding whom they are actually talking to, with the exact share the
        // declaration stands on.
        who.AddChild(Giving(Text(Simulation.Loc.Format("{0}: {1} · {2}: {3}",
            Tr("State religion"), StateIdentityWithShare(nation, true),
            Tr("State culture"), StateIdentityWithShare(nation, false)), 12, Ui.Colour("text.body_soft"))));
        who.AddChild(Giving(Text(canSee
            ? Simulation.Loc.Format("{0} people · GDP {1} · stability {2}",
                Fmt.People(nation.Population), Fmt.Money(nation.Gdp), Fmt.Percent(nation.Stability))
            : Tr("What they have is not known to us."), 12, Ui.Colour("text.muted"))));

        var aside = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(own ? 0 : 160, 0) };
        aside.AddThemeConstantOverride("separation", 2);
        top.AddChild(aside);
        Label Right(Label label)
        {
            label.HorizontalAlignment = HorizontalAlignment.Right;
            return label;
        }

        // What a society thinks and what it has got used to come after what a player opens this
        // page for - who is bound to whom, who is at war, what is between us. They used to open
        // the page, and the first thing a player read about their own country was that its
        // reputation "has not formed yet" and its habit of its institutions stood at 0%.
        void Society()
        {
            AddSocialMemoryDiplomacy(nation, player, Field);
            if (_campaign.FeatureEnabled("national_courses") && nation.NationalCourse.Initialized)
                Field("National agenda", Tr(NationalCourseCatalog.Find(nation.NationalCourse.Dominant)?.Name ?? nation.NationalCourse.Dominant));
        }
        if (own)
        {
            _diplomacyActionColumn.Visible = false;
            _diplomacyActionRows.Visible = false;
            aside.AddChild(Right(RowLabel("YOUR COUNTRY", 11, Ui.Colour("text.accent"))));
            RefreshOwnDiplomacy(player, Field, Text, Giving, Bar);
            Society();
            return;
        }
        _diplomacyActionColumn.Visible = true;
        _diplomacyActionRows.Visible = true;

        var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, player.Id, nation.Id);
        var opinionColour = relation.Opinion > 0 ? positive : relation.Opinion < 0 ? negative : Ui.Colour("text.body_soft");
        aside.AddChild(Right(RowLabel("THEIR OPINION OF US", 10, Ui.Colour("text.caption"))));
        aside.AddChild(Right(Text(relation.Opinion.ToString("+0;-0;0"), 28, opinionColour)));
        var mood = Bar(8, 160);
        mood.Lean(relation.Opinion / 100f, positive, negative);
        aside.AddChild(mood);
        aside.AddChild(Right(Text(DiplomaticVocabulary.StanceName(relation.Stance).ToUpperInvariant(), 11,
            relation.Stance switch
            {
                DiplomaticStance.Allied or DiplomaticStance.Friendly => positive,
                DiplomaticStance.Rival or DiplomaticStance.AtWar => negative,
                _ => Ui.Colour("text.caption")
            })));

        // ---- what they are in: wars, blocs, backers, claims (DiplomacyStandingUI).
        AddWorldStanding(nation, Field, Text, Bar);

        // ---- the balance of power: a sentence, then each weight as a bar of ours against theirs.
        var mine = DiplomacyStrength(player);
        var theirs = DiplomacyStrength(nation);
        var ratio = theirs <= 0.0001f ? 99f : mine / theirs;
        var verdict = !canSee ? Tr("We cannot see enough of them to judge.") :
            ratio > 2.5f ? Tr("We are much stronger than them.") :
            ratio > 1.25f ? Tr("We are stronger than them.") :
            ratio > 0.8f ? Tr("We are evenly matched.") :
            ratio > 0.4f ? Tr("We are weaker than them.") :
            Tr("We are much weaker than them.");
        var balance = Field("THE BALANCE OF POWER", verdict);
        var sides = new HBoxContainer();
        sides.AddThemeConstantOverride("separation", 10);
        sides.AddChild(new Control { CustomMinimumSize = new Vector2(100, 0) });
        var ourName = Right(Text(player.DisplayName, 11, Ui.Colour("text.caption")));
        ourName.CustomMinimumSize = new Vector2(96, 0);
        ourName.ClipText = true;
        ourName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        ourName.MaxLinesVisible = 3;
        ourName.Name = "DiplomacyComparisonPlayer";
        ourName.TooltipText = Ui.Tip(player.DisplayName);
        float lastPlayerComparisonWidth = -1;
        ourName.Resized += () =>
        {
            if (Math.Abs(ourName.Size.X - lastPlayerComparisonWidth) < .5f) return;
            lastPlayerComparisonWidth = ourName.Size.X;
            ReserveWrappedLabelLines(ourName);
        };
        sides.AddChild(ourName);
        sides.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var theirName = Text(nation.DisplayName, 11, Ui.Colour("text.caption"));
        theirName.CustomMinimumSize = new Vector2(96, 0);
        theirName.ClipText = true;
        theirName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        theirName.MaxLinesVisible = 3;
        theirName.Name = "DiplomacyComparisonCountry";
        float lastComparisonWidth = -1;
        theirName.Resized += () =>
        {
            if (Math.Abs(theirName.Size.X - lastComparisonWidth) < .5f) return;
            lastComparisonWidth = theirName.Size.X;
            ReserveWrappedLabelLines(theirName);
        };
        theirName.TooltipText = Ui.Tip(nation.DisplayName);
        sides.AddChild(theirName);
        balance.AddChild(sides);
        // Measure only after attachment: the UI's inherited font is unavailable off-tree.
        ReserveWrappedLabelLines(theirName);
        ReserveWrappedLabelLines(ourName);

        var divisions = DivisionsByNation();
        void Weigh(string caption, float ours, float others, string ourText, string theirText)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var label = Text(Tr(caption), 13, Ui.Colour("text.body_soft"));
            label.CustomMinimumSize = new Vector2(100, 0);
            row.AddChild(label);
            var us = Right(Text(ourText, 13, Ui.Colour(!canSee || ours >= others ? "text.bright" : "text.muted")));
            us.CustomMinimumSize = new Vector2(96, 0);
            row.AddChild(us);
            var bar = Bar(10);
            if (canSee && ours + others > 0f)
                bar.Shares((ours / (ours + others), ourColour), (others / (ours + others), theirColour));
            bar.CentreTick = true;
            row.AddChild(bar);
            var them = Text(canSee ? theirText : "?", 13, Ui.Colour(canSee && others > ours ? "text.bright" : "text.muted"));
            them.CustomMinimumSize = new Vector2(96, 0);
            row.AddChild(them);
            balance.AddChild(row);
        }
        Weigh("GDP", (float)player.Gdp, (float)nation.Gdp, Fmt.Money(player.Gdp), Fmt.Money(nation.Gdp));
        Weigh("Divisions", divisions.GetValueOrDefault(player.Id), divisions.GetValueOrDefault(nation.Id),
            divisions.GetValueOrDefault(player.Id).ToString("0.#"), divisions.GetValueOrDefault(nation.Id).ToString("0.#"));
        Weigh("Population", player.Population, nation.Population, Fmt.People(player.Population), Fmt.People(nation.Population));
        Weigh("Stability", player.Stability, nation.Stability, Fmt.Percent(player.Stability), Fmt.Percent(nation.Stability));

        // ---- the opinion, and what it is made of.
        var drivers = relation.OpinionDrivers.Entries
            .OrderByDescending(item => Math.Abs(item.Value))
            .Take(8)
            .ToList();
        var reasons = Field("WHAT THEIR OPINION IS MADE OF",
            Simulation.Loc.Format("Their opinion of us: {0:+0;-0;0}", relation.Opinion), opinionColour);
        if (drivers.Count == 0)
            reasons.AddChild(Text(Tr("Nothing in particular: they have no reason to think of us either way."), 12, Ui.Colour("text.muted")));
        var largest = drivers.Count == 0 ? 1f : Math.Max(1f, drivers.Max(item => Math.Abs((float)item.Value)));
        foreach (var entry in drivers)
        {
            var value = (float)entry.Value;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            row.AddChild(Giving(Text(TranslateOpinionDriver(entry.Source), 12, Ui.Colour("text.body_soft"))));
            var bar = Bar(8, 120);
            bar.Lean(value / largest, positive, negative);
            row.AddChild(bar);
            var figure = Right(Text(value.ToString("+0.0;-0.0;0.0"), 12, value >= 0f ? positive : negative));
            figure.CustomMinimumSize = new Vector2(46, 0);
            row.AddChild(figure);
            reasons.AddChild(row);
        }

        // ---- the war, if there is one: the same bar as on the Wars screen, our side on the left.
        var war = DiplomaticPeaceWar(nation);
        if (war is not null)
        {
            var attacking = war.IsAttacking(player.Id);
            var score = attacking ? war.WarScore : -war.WarScore;
            var front = Field("AT WAR", WarNames.Of(_campaign, war), negative);
            var tug = Bar(18);
            tug.Tug(Mathf.Clamp(score / WarLedgerBarScale, -1f, 1f), ourColour, theirColour);
            front.AddChild(tug);
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);
            line.AddChild(Giving(Text(Simulation.Loc.Format("War score {0} for us · {1} lost on our side, {2} on theirs",
                score.ToString("+0.0;-0.0;0.0"),
                Fmt.People(attacking ? war.AttackerLosses : war.DefenderLosses),
                Fmt.People(attacking ? war.DefenderLosses : war.AttackerLosses)), 12, Ui.Colour("text.body_soft"))));
            var warId = war.Id;
            line.AddChild(ActionButton("Open the war's page", () => OpenWarPage(warId), 190, 28));
            front.AddChild(line);
        }

        // ---- every tie between us, and what their trade is worth.
        var ties = Field("TIES");
        _diplomacyPactRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _diplomacyPactRows.AddThemeConstantOverride("separation", 5);
        ties.AddChild(_diplomacyPactRows);
        RefreshDiplomacyPacts(player, nation, relation, war is not null);
        if (_diplomacyPactRows.GetChildCount() == 0)
            _diplomacyPactRows.AddChild(Text(Tr("No treaty, no tie and no trade between us."), 12, Ui.Colour("text.muted")));

        Society();
        RefreshDiplomacyActions(player, nation);
    }

    /// <summary>
    /// The player's own page: where the country stands in the world rather than against one
    /// other country - its figures, who it is bound to, the wars it is in, and who thinks well
    /// and ill of it, each of them a click away.
    /// </summary>
    private void RefreshOwnDiplomacy(Nation player,
        Func<string, string, Color?, VBoxContainer> field,
        Func<string, int, Color, Label> text,
        Func<Label, Label> giving,
        Func<float, float, WarBar> bar)
    {
        var divisions = DivisionsByNation();
        var standing = field("YOUR STANDING", "", null);
        var readouts = new HBoxContainer();
        readouts.AddThemeConstantOverride("separation", 18);
        standing.AddChild(readouts);
        foreach (var (caption, value) in new[]
                 {
                     ("GDP", Fmt.Money(player.Gdp)),
                     ("Divisions", divisions.GetValueOrDefault(player.Id).ToString("0.#")),
                     ("Population", Fmt.People(player.Population)),
                     ("Stability", Fmt.Percent(player.Stability))
                 })
        {
            var readout = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            readout.AddThemeConstantOverride("separation", 0);
            readout.AddChild(RowLabel(caption, 10, Ui.Colour("text.caption"), upper: true));
            readout.AddChild(text(value, 20, Ui.Colour("text.bright")));
            readouts.AddChild(readout);
        }

        AddWorldStanding(player, field, text, bar);

        // Who thinks well and ill of us, five each - the people to call on and the people to watch.
        var opinions = _campaign.Nations
            .Where(other => other.Id != player.Id && other.Population > 0)
            .Select(other => (Nation: other, Opinion: CampaignDiplomacy.Peek(_campaign, player.Id, other.Id)?.Opinion ?? 0))
            .ToList();
        var friends = opinions.Where(item => item.Opinion > 0).OrderByDescending(item => item.Opinion).Take(6).ToList();
        var critics = opinions.Where(item => item.Opinion < 0).OrderBy(item => item.Opinion).Take(6).ToList();
        var opinion = friends.ToDictionary(item => item.Nation.Id, item => item.Opinion);
        foreach (var item in critics) opinion[item.Nation.Id] = item.Opinion;
        string Says(Nation other) => "  " + opinion.GetValueOrDefault(other.Id).ToString("+0;-0;0");
        field("WHO THINKS WELL OF US", "", null)
            .AddChild(DiplomacyChips(friends.Select(item => item.Nation), Tr("Nobody in particular."), Says));
        field("WHO THINKS ILL OF US", "", null)
            .AddChild(DiplomacyChips(critics.Select(item => item.Nation), Tr("Nobody in particular."), Says));
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
        {
            Pact(Tr("Alliance"), Tr("signed with you"), "state.positive");
            var strained = relation.AllianceCohesion < _campaign.Defines.AllianceRuptureCohesion;
            Pact(Tr("Alliance cohesion"), $"{relation.AllianceCohesion:P0} · " +
                Tr(strained ? "At risk" : "Holding together"), strained ? "state.warning" : "state.positive");
            var why = new Simulation.Breakdown();
            AllianceMaintenance.Target(_campaign, relation, why);
            var meter = new ProgressBar { MinValue = 0, MaxValue = 100,
                Value = relation.AllianceCohesion * 100, ShowPercentage = false,
                CustomMinimumSize = new Vector2(0, 12), MouseFilter = MouseFilterEnum.Stop };
            meter.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset") });
            meter.AddThemeStyleboxOverride("fill", new StyleBoxFlat
                { BgColor = Ui.Colour(strained ? "state.warning" : "state.positive") });
            meter.TooltipText = Ui.Tip(string.Join("\n", why.Entries.Select(e =>
                $"{Tr(e.Source)}: {e.Value:+0%;-0%;0%}")));
            _diplomacyPactRows.AddChild(meter);
            var council = new PanelContainer { Name = "AllianceCouncil" };
            council.AddThemeStyleboxOverride("panel", new StyleBoxFlat {
                BgColor = Ui.Colour("surface.inset"), BorderColor = Ui.Colour("text.accent"),
                BorderWidthLeft = 2, ContentMarginLeft = 10, ContentMarginRight = 10,
                ContentMarginTop = 8, ContentMarginBottom = 8 });
            _diplomacyPactRows.AddChild(council);
            var agenda = new VBoxContainer(); agenda.AddThemeConstantOverride("separation", 6);
            council.AddChild(agenda);
            agenda.AddChild(Title(Tr("Alliance council"), 15, Ui.Colour("text.accent")));
            var explanation = Title(Tr("Trust grows with every commitment kept."), 12, Ui.Colour("text.body_soft"));
            explanation.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            agenda.AddChild(explanation);
            var ceiling = Title(Simulation.Loc.Format("Sustainable trust: {0:P0}", AllianceMaintenance.Target(_campaign, relation)), 12, Ui.Colour("text.body_soft"));
            ceiling.TooltipText = meter.TooltipText; agenda.AddChild(ceiling);
            if (AlliancePrograms.Active(relation))
            {
                var programme = AllianceProgramCatalog.Find(relation.AllianceProgram);
                var sponsor = _campaign.NationById(relation.AllianceProgramSponsor);
                var progress = Math.Clamp(_campaign.ElapsedDays - relation.AllianceProgramStarted, 0, relation.AllianceProgramDuration);
                agenda.AddChild(Title(Tr(programme?.Name ?? relation.AllianceProgram), 13, Ui.Colour("text.accent")));
                var terms = Title(Simulation.Loc.Format("Sponsor: {0} · {1}/{2} days · {3} per week",
                    sponsor?.DisplayName ?? "—", progress, relation.AllianceProgramDuration, Fmt.Money(relation.AllianceProgramWeeklyCost)), 12, Ui.Colour("text.body_soft"));
                terms.AutowrapMode = TextServer.AutowrapMode.WordSmart; agenda.AddChild(terms);
                agenda.AddChild(new ProgressBar { MinValue = 0, MaxValue = Math.Max(1, relation.AllianceProgramDuration),
                    Value = progress, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 8) });
                if (sponsor?.Id == player.Id)
                {
                    var cancel = ActionButton(Tr("End contribution"), () =>
                    {
                        _campaign.Commands.Enqueue(new ReaffirmAllianceCommand { IssuerId = player.Id, TargetId = nation.Id, Cancel = true });
                        _campaign.Commands.ApplyAll(_campaign); ForceDiplomacyRefresh();
                    }, 0, 32, iconPath: "res://game/gfx/interface/icons/icon_plan_erase.png");
                    cancel.TooltipText = Ui.Tip(Tr("Ending an unfulfilled promise costs trust. Past payments are not refunded."));
                    agenda.AddChild(cancel);
                }
            }
            else foreach (var programme in AllianceProgramCatalog.All)
            {
                var button = ActionButton(Tr(programme.Name), () =>
                {
                    _campaign.Commands.Enqueue(new ReaffirmAllianceCommand {
                        IssuerId = player.Id, TargetId = nation.Id, ProgramId = programme.Id });
                    _campaign.Commands.ApplyAll(_campaign); ForceDiplomacyRefresh();
                }, 0, 34, iconPath: "res://game/gfx/interface/icons/icon_alliance_commitment.png");
                button.Disabled = !AlliancePrograms.CanStart(_campaign, player, nation, programme.Id, out var refusal);
                button.TooltipText = Ui.Tip(button.Disabled ? Tr(refusal) : Simulation.Loc.Format(
                    "{0} days · {1} per week · up to {2:P0} earned trust. First payment now; trust arrives weekly. Only your treasury pays.",
                    programme.DurationDays, Fmt.Money(AlliancePrograms.Cost(player, programme)), programme.TrustBudget));
                agenda.AddChild(button);
            }
        }
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
        // The countries in a tie by their flags, each one a click away.
        void Members(string caption, IEnumerable<Nation> members)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            _diplomacyPactRows.AddChild(row);
            var label = Title(caption, 13, Ui.Colour("text.body_soft"));
            Fixed(label, 150);
            label.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            row.AddChild(label);
            row.AddChild(DiplomacyChips(members, ""));
        }
        if (allies.Count > 0)
            Members(Tr("Allied to"), allies);
        if (nation.AnswersTo(player.Id))
            Pact(Tr("Answers to"), Tr("you") + " · " + nation.SubjectType);
        var subjects = SubjectSystem.SubjectsOf(_campaign, nation);
        if (subjects.Count > 0)
            Members(Tr("Subjects"), subjects);

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
        RefreshAssistanceAction(nation);
        RefreshSubmissionButtons(nation, _diplomacySubmissionActions, _diplomacySubmissionButtons);
        RefreshDiplomaticPeaceButton(_diplomacyPeaceButton, nation);
        RefreshIntelNetworkAction(player, nation);
        _diplomacyWarButton.Visible = !_diplomacyPeaceButton.Visible;
        var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, player.Id, nation.Id);
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
            var can = AllianceSystem.CanPropose(_campaign, player, nation, out var refusal, preview: true);
            _diplomacyAllianceButton.Disabled = !can;
            if (!can)
            {
                _diplomacyAllianceButton.TooltipText = Ui.Tip(refusal);
            }
            else
            {
                var why = new Simulation.Breakdown();
                var willingness = AllianceSystem.Willingness(_campaign, nation, player, why, refreshAssessment: false);
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

        _diplomacyUltimatumButton.Disabled = !canDeclare || nation.IsHuman;
        _diplomacyUltimatumButton.TooltipText = Ui.Tip(!canDeclare ? reason : nation.IsHuman
            ? Tr("Human-controlled countries cannot be forced to answer automatically.")
            : Tr("Compose territorial, financial and treaty demands. Review before sending."));
        StyleButton(_diplomacyUltimatumButton, false);
        _diplomacyUltimatumButton.Text = Tr("Issue ultimatum");
        var canDeal = Ultimatum.CanNegotiateDeal(_campaign, player, nation, out var dealRefusal);
        _diplomacyDealButton.Disabled = !canDeal;
        _diplomacyDealButton.TooltipText = Ui.Tip(!canDeal ? dealRefusal : nation.IsHuman
            ? Tr("Send an offer to this player's inbox. They must explicitly accept; nothing is signed automatically.")
            : Tr("Compose a peaceful exchange of cash, treaties or regions. Review before sending."));

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
        ForceDiplomacyRefresh();
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
        ForceDiplomacyRefresh();
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
        ForceDiplomacyRefresh();
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
        ForceDiplomacyRefresh();
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
            ForceDiplomacyRefresh();
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
        ForceDiplomacyRefresh();
    }

    private void ConfirmOrIssueUltimatum() => OpenUltimatumComposer(DiplomacyTarget());

    private void ConfirmOrProposeDeal()
    {
        if (!ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        if (!Ultimatum.CanNegotiateDeal(_campaign, _campaign.Player, target, out var reason))
        { _diplomacyFeedback.Text = reason; return; }
        OpenDealComposer(target);
    }

    private void IssueSelectedUltimatum(Nation target, Label feedback) => OpenUltimatumComposer(target);

    private void ReleaseDiplomacySubject()
    {
        if (_campaign?.Player is null || !ValidDiplomacyTarget()) return;
        var target = DiplomacyTarget();
        if (!target.AnswersTo(_campaign.Player.Id)) return;
        SubjectSystem.Release(_campaign, target);
        _diplomacyFeedback.Text = Simulation.Loc.Format("{0} answers to nobody now.", target.DisplayName);
        ForceDiplomacyRefresh();
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
        ForceDiplomacyRefresh();
        Refresh();
    }
}
