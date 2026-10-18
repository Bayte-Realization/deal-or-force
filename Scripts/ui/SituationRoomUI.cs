using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _situationLayer;
    private PanelContainer _situationPanel;
    private VBoxContainer _situationList, _situationDetail;
    private ScrollContainer _situationDetailScroll;
    private Label _situationSummary, _situationEmpty, _situationStatus, _situationMusic;
    private LineEdit _situationSearch;
    private Button _situationAction, _situationClose, _situationRailButton;
    private readonly Dictionary<string, Button> _situationFilters = new();
    private readonly Dictionary<string, (Button Button, Label Title, Label Summary, Label Priority, ColorRect Rail)> _situationCards = new();
    private IReadOnlyList<SituationItem> _situations = Array.Empty<SituationItem>();
    private readonly SituationAttention _situationAttention = new();
    private string _situationSelected = "", _situationFilter = "All", _situationDetailKey = "", _situationReturn = "";
    private int _situationNation = -1, _situationHash;
    private ulong _situationCheckedAt;
    private Control _situationReturnFocus;

    private void BuildSituationRoom()
    {
        _situationLayer = FullLayer("SituationRoom"); _situationLayer.Visible = false; _hudLayer.AddChild(_situationLayer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay", .78f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _situationLayer.AddChild(shade);
        var panel = TexturedPanel(_situationLayer); panel.Name = "SituationRoomPanel"; Ui.Place(panel, "workspace.situation");
        _situationPanel = panel;
        var rows = PanelRows(panel, 22, 18, 10);
        var header = new HBoxContainer(); header.AddThemeConstantOverride("separation", (int)Ui.Metric("situation.header_gap", 10)); rows.AddChild(header);
        var title = SituationText("SITUATION ROOM", 25, "text.accent"); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        header.AddChild(HandbookButton("situation_room"));
        _situationClose = ActionButton("×", CloseSituationRoom, 38, 32); _situationClose.Name = "CloseSituationRoom";
        _situationClose.TooltipText = Ui.Tip(Tr("Close")); header.AddChild(_situationClose);
        _situationSummary = SituationText("", 13, "text.body_soft"); _situationSummary.Name = "SituationSummary"; rows.AddChild(_situationSummary);
        var filters = new HFlowContainer(); filters.AddThemeConstantOverride("h_separation", (int)Ui.Metric("situation.filter_h_gap", 6)); filters.AddThemeConstantOverride("v_separation", (int)Ui.Metric("situation.filter_v_gap", 4));
        rows.AddChild(filters);
        foreach (var key in new[] { "All", "Urgent", "Foreign", "Society", "Economy", "Forces", "Development" })
        {
            var captured = key;
            var button = ActionButton(SituationFilterName(key), () =>
            { _situationFilter = captured; RefreshSituationList(); }, 0, 30);
            button.Name = "SituationFilter_" + key; button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
            filters.AddChild(button); _situationFilters[key] = button;
        }
        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", (int)Ui.Metric("situation.body_gap", 16)); rows.AddChild(body);
        var left = new VBoxContainer { CustomMinimumSize = new(Ui.Metric("situation.detail_column_width", 320), 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = .75f, SizeFlagsVertical = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", (int)Ui.Metric("situation.column_gap", 8)); body.AddChild(left);
        _situationSearch = Placeholder(new LineEdit { Name = "SituationSearch", ClearButtonEnabled = true }, "Find an issue or country...");
        _situationSearch.TextChanged += _ => RefreshSituationList(); left.AddChild(_situationSearch);
        var listScroll = new ScrollContainer { Name = "SituationListScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, FollowFocus = true };
        left.AddChild(listScroll);
        _situationList = new VBoxContainer { Name = "SituationList", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _situationList.AddThemeConstantOverride("separation", (int)Ui.Metric("situation.list_gap", 7)); listScroll.AddChild(_situationList);
        _situationEmpty = SituationText("", 14, "text.body_soft"); _situationList.AddChild(_situationEmpty);
        var right = new PanelContainer { Name = "SituationDetailFrame", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.3f, SizeFlagsVertical = SizeFlags.ExpandFill };
        right.SetMeta("atlas_section_ground", "surface.inset");
        right.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(right, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        body.AddChild(right);
        var detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        detail.AddThemeConstantOverride("separation", (int)Ui.Metric("situation.detail_gap", 10)); right.AddChild(detail);
        _situationDetailScroll = new ScrollContainer { Name = "SituationDetailScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, FollowFocus = true };
        detail.AddChild(_situationDetailScroll);
        _situationDetail = new VBoxContainer { Name = "SituationDetail", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _situationDetail.AddThemeConstantOverride("separation", (int)Ui.Metric("situation.detail_gap", 10)); _situationDetailScroll.AddChild(_situationDetail);
        _situationStatus = SituationText("", 12, "text.caption"); _situationStatus.Name = "SituationStatus"; detail.AddChild(_situationStatus);
        _situationAction = ActionButton("", FollowSituation, 0, 40, true);
        _situationAction.Name = "SituationAction"; _situationAction.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _situationAction.SizeFlagsHorizontal = SizeFlags.ExpandFill; detail.AddChild(_situationAction);
        _situationMusic = SituationText("", 12, "text.caption"); _situationMusic.Name = "SituationMusic";
        _situationMusic.MouseFilter = MouseFilterEnum.Stop; rows.AddChild(_situationMusic);
        var media = ActionButton("Listen and inspect artwork", OpenMediaLibrary, 0, 30);
        media.Name = "OpenMediaLibrarySituation"; rows.AddChild(media);
        ApplyAtlasHeading(panel);
        LayoutSituationRoom();
    }

    private void LayoutSituationRoom()
    {
        if (_situationPanel is null) return;
        Ui.Place(_situationPanel, "workspace.situation");
        var authoredTop = _situationPanel.AnchorTop * Size.Y + _situationPanel.OffsetTop;
        var belowHeader = InterfaceSkin.Layout("hud.top").OffsetBottom + _hudHeaderExtra + 8;
        _situationPanel.OffsetTop += Math.Max(0, belowHeader - authoredTop);
    }

    private static string SituationFilterName(string key) => key switch
    {
        "All" => "All issues", "Urgent" => "Needs attention", "Foreign" => "Foreign affairs", "Society" => "Institutions",
        "Economy" => "Economic pressures", "Forces" => "Armed forces", _ => "Development opportunities"
    };
    private static string SituationPriorityName(SituationPriority priority) => priority switch
    {
        SituationPriority.Critical => "CRITICAL", SituationPriority.Warning => "WARNING",
        SituationPriority.Awaiting => "AWAITING YOUR ANSWER", _ => "OPPORTUNITY"
    };
    private static string SituationColour(SituationPriority priority) => priority switch
    {
        SituationPriority.Critical => "state.critical", SituationPriority.Warning => "state.warning",
        SituationPriority.Awaiting => "state.info", _ => "state.positive"
    };
    private Label SituationText(string text, int size, string colour)
    {
        var label = Title(text, size, Ui.Colour(colour));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart; label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.MouseFilter = MouseFilterEnum.Ignore; return label;
    }

    private void OpenSituationRoom()
    {
        if (_campaign?.Player is null || !_hudLayer.Visible) return;
        if (_situationLayer is null) BuildSituationRoom();
        _situationReturnFocus = GetViewport().GuiGetFocusOwner();
        _situationReturn = "";
        foreach (var other in new[] { _sidePanel, _stateAffairsLayer, _sovereignFinanceLayer, _ledgerLayer, _warLedgerLayer,
                     _diplomacyLayer, _policyLayer, _conflictsLayer, _tradeScreenLayer, _productionLayer, _supportLayer, _researchLayer })
            if (other is not null) other.Visible = false;
        SetArmyPanelVisible(false);
        _situationLayer.Visible = true; _situationLayer.MoveToFront(); _openScreen = "Situation";
        RefreshSituationRoom(force: true);
        _situationLayer.FocusMode = FocusModeEnum.All; _situationLayer.GrabFocus(); UpdateScreenButtons();
    }

    private void CloseSituationRoom()
    {
        if (_situationLayer is null) return;
        _situationLayer.Visible = false; _situationReturn = "";
        if (_openScreen == "Situation") _openScreen = "";
        if (IsInstanceValid(_situationReturnFocus) && _situationReturnFocus.IsVisibleInTree()) _situationReturnFocus.GrabFocus();
        UpdateScreenButtons();
    }

    private void RefreshSituationRoom(bool force = false)
    {
        if (_campaign?.Player is not { } nation || !_hudLayer.Visible) return;
        var now = Time.GetTicksMsec();
        if (!force && now - _situationCheckedAt < 750) return;
        _situationCheckedAt = now;
        if (IsInstanceValid(_situationMusic))
        {
            var score = MusicDirector.Ensure(this);
            var muted = SettingsCatalog.Number("volume_master") * SettingsCatalog.Number("volume_music") <= .0005f;
            _situationMusic.Text = Loc.Format("Soundtrack: {0} · {1}",
                Tr(muted ? "Muted" : score.Adaptive ? MusicSituation.Name(score.CurrentMood) : "Free playlist"),
                score.CurrentTrack.Length == 0 ? Tr("No matching track") : score.CurrentTrack);
            _situationMusic.TooltipText = Ui.Tip(score.CurrentReason + "\n" + Tr("Musical context settles before the track changes; no gameplay rule is affected."));
        }
        var report = SituationReport.Read(_campaign, nation, _observerMode);
        // The attention memory is local presentation state and is not part of a save/network hash.
        if (_situationAttention.Observe(nation.Id, report) && !_observerMode) PlayStrategicCue("situation_alert");
        var hash = new HashCode(); hash.Add(nation.Id); hash.Add(Loc.Language); hash.Add(_campaign.ElapsedDays);
        foreach (var item in report)
        {
            hash.Add(item.Id); hash.Add(item.Priority); hash.Add(item.Title); hash.Add(item.Summary);
            foreach (var cause in item.Causes) hash.Add(cause);
            foreach (var consequence in item.Consequences) hash.Add(consequence);
            foreach (var fact in item.Facts) hash.Add(fact);
        }
        var nextHash = hash.ToHashCode();
        if (!force && nextHash == _situationHash) return;
        _situationHash = nextHash; _situations = report;
        var important = report.Count(i => i.Priority != SituationPriority.Opportunity);
        if (IsInstanceValid(_situationRailButton))
        {
            _situationRailButton.Text = Loc.Format("Situation room · {0}", important);
            _situationRailButton.TooltipText = Ui.Tip(Tr("Inspect causes, consequences and available responses."));
        }
        if (_situationLayer is not { Visible: true }) return;
        if (_situationNation != nation.Id)
        {
            _situationNation = nation.Id; _situationFilter = "All"; _situationSelected = "";
            _situationSearch.Text = "";
        }
        _situationSummary.Text = Loc.Format("{0} · {1} critical · {2} warnings · {3} awaiting · {4} opportunities · day {5}",
            nation.DisplayName, report.Count(i => i.Priority == SituationPriority.Critical), report.Count(i => i.Priority == SituationPriority.Warning),
            report.Count(i => i.Priority == SituationPriority.Awaiting), report.Count(i => i.Priority == SituationPriority.Opportunity), _campaign.ElapsedDays);
        RefreshSituationList();
    }

    private void RefreshSituationList()
    {
        if (_situationList is null) return;
        var query = _situationSearch.Text.Trim();
        var visible = _situations.Where(i => (_situationFilter == "All" || _situationFilter == "Urgent" && i.Priority != SituationPriority.Opportunity ||
                    i.Area.ToString() == _situationFilter) && (query.Length == 0 ||
                    (i.Title + " " + i.Summary + " " + string.Join(" ", i.Causes)).Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        foreach (var (key, button) in _situationFilters)
        { button.Text = Tr(SituationFilterName(key)); StyleButton(button, key == _situationFilter); }
        foreach (var key in _situationCards.Keys.Where(id => !_situations.Any(i => i.Id == id)).ToArray())
        { var card = _situationCards[key].Button; _situationList.RemoveChild(card); card.QueueFree(); _situationCards.Remove(key); }
        foreach (var card in _situationCards.Values) card.Button.Visible = false;
        if (!visible.Any(i => i.Id == _situationSelected)) _situationSelected = visible.FirstOrDefault()?.Id ?? "";
        var index = 0;
        foreach (var item in visible)
        {
            if (!_situationCards.TryGetValue(item.Id, out var card))
            {
                var id = item.Id;
                var button = ActionButton("", () =>
                { _situationSelected = id; _situationDetailKey = ""; _situationDetailScroll.ScrollVertical = 0; RefreshSituationList(); }, 0, 110);
                button.Name = "SituationItem_" + id.Replace(':', '_'); button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
                margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); margin.OffsetLeft = 12; margin.OffsetRight = -12;
                margin.OffsetTop = 8; margin.OffsetBottom = -8; button.AddChild(margin);
                var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; content.AddThemeConstantOverride("separation", 4); margin.AddChild(content);
                var priority = SituationText("", 11, "text.caption"); content.AddChild(priority);
                var title = SituationText("", 15, "text.bright"); title.MaxLinesVisible = 2;
                title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; content.AddChild(title);
                var summary = SituationText("", 12, "text.body_soft"); summary.MaxLinesVisible = 2;
                summary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; content.AddChild(summary);
                // A rail down the card's left edge, in the priority's own colour. The list used
                // to be a column of same-weight cards whose only difference was a word at the
                // top of each; scanning it meant reading six captions to learn which one mattered.
                // Now the shape of the column says it before any of the words are read.
                var rail = new ColorRect
                {
                    Name = "SituationRail",
                    Color = Ui.Colour(SituationColour(SituationPriority.Critical)),
                    MouseFilter = MouseFilterEnum.Ignore,
                    CustomMinimumSize = new Vector2(3, 0)
                };
                rail.SetAnchorsAndOffsetsPreset(LayoutPreset.LeftWide);
                rail.OffsetLeft = 0;
                rail.OffsetRight = 3;
                button.AddChild(rail);
                card = (button, title, summary, priority, rail); _situationCards[id] = card; _situationList.AddChild(button);
            }
            card.Button.Visible = true; _situationList.MoveChild(card.Button, index++);
            card.Title.Text = item.Title; card.Summary.Text = item.Summary;
            card.Priority.Text = Tr(SituationPriorityName(item.Priority)); card.Priority.AddThemeColorOverride("font_color", Ui.Colour(SituationColour(item.Priority)));
            card.Rail.Color = Ui.Colour(SituationColour(item.Priority));
            card.Button.AccessibilityName = item.Title + ". " + item.Summary;
            card.Button.TooltipText = Ui.Tip(item.Title + "\n" + item.Summary);
            StyleButton(card.Button, item.Id == _situationSelected);
        }
        _situationEmpty.Visible = visible.Length == 0;
        _situationEmpty.Text = Tr(_situations.Count == 0 ? "Nothing urgent."
            : "No issues match this filter. Clear the search or choose All issues.");
        _situationList.MoveChild(_situationEmpty, index);
        ShowSituationDetails(visible.FirstOrDefault(i => i.Id == _situationSelected));
    }

    private void ShowSituationDetails(SituationItem item)
    {
        var key = $"{_situationHash}|{item?.Id}";
        if (_situationDetailKey == key) return;
        _situationDetailKey = key;
        var previousScroll = _situationDetailScroll.ScrollVertical;
        ClearRuntimeChildren(_situationDetail);
        _situationAction.Visible = item is not null;
        if (item is null)
        {
            _situationDetail.AddChild(SituationText("Select an issue to see its causes, what may follow and where to act.", 15, "text.body_soft"));
            _situationStatus.Text = Tr("No decision is required here."); return;
        }
        _situationDetail.AddChild(SituationText(SituationPriorityName(item.Priority), 12, SituationColour(item.Priority)));
        _situationDetail.AddChild(SituationText(item.Title, 24, "text.bright"));
        _situationDetail.AddChild(SituationText(item.Summary, 16, "text.body_soft"));
        if (item.Facts.Length > 0)
        {
            var facts = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill, Name = "SituationFacts" };
            facts.AddThemeConstantOverride("h_separation", 18); facts.AddThemeConstantOverride("v_separation", 8); _situationDetail.AddChild(facts);
            foreach (var fact in item.Facts)
            { facts.AddChild(SituationText(fact.Label, 13, "text.caption")); facts.AddChild(SituationText(fact.Value, 15, "text.bright")); }
        }
        _situationDetail.AddChild(new HSeparator());
        _situationDetail.AddChild(SituationText("WHY THIS IS HERE", 12, "text.accent"));
        foreach (var cause in item.Causes) _situationDetail.AddChild(SituationText(cause, 15, "text.body_soft"));
        _situationDetail.AddChild(SituationText("WHAT MAY FOLLOW", 12, "text.accent"));
        foreach (var consequence in item.Consequences) _situationDetail.AddChild(SituationText(consequence, 15, "text.body_soft"));
        _situationStatus.Text = Tr("Open the responsible screen to review actions. Esc returns to this room.");
        _situationAction.Text = item.Action;
        _situationAction.Disabled = !SituationDestinationEnabled(item);
        if (_situationAction.Disabled) _situationStatus.Text = Tr("The responsible screen is disabled by this scenario or interface configuration.");
        Callable.From(() => _situationDetailScroll.ScrollVertical = previousScroll).CallDeferred();
    }

    private void FollowSituation()
    {
        // Re-read on click: an army can disappear and an offer can expire while a page is open.
        var item = SituationReport.Read(_campaign, _campaign.Player, _observerMode).FirstOrDefault(i => i.Id == _situationSelected);
        if (item is null || !SituationDestinationEnabled(item)) { RefreshSituationRoom(force: true); return; }
        if (item.Id == "detached_land")
        {
            var note = Corridors.ProposeBuy(_campaign, _campaign.Player);
            if (note.Length == 0)
            {
                SetArmyFeedback(Tr("The ask has been sent."));
                _situationLayer.Visible = false;
                _situationReturn = "Inbox";
                OpenDealInbox();
                UpdateScreenButtons();
            }
            else
            {
                SetArmyFeedback(note);
                RefreshSituationRoom(force: true);
            }
            return;
        }
        if (item.Id == "host_temper")
        {
            var movement = _campaign.Nations.Find(candidate => candidate.Id == item.TargetId);
            if (movement?.Movement is null)
            {
                SetArmyFeedback(Tr("That headquarters is no longer here."));
                RefreshSituationRoom(force: true);
                return;
            }
            _situationLayer.Visible = false;
            _situationReturn = "Movements";
            ShowMovement(movement);
            UpdateScreenButtons();
            return;
        }
        if (item.Id.StartsWith("ultimatum:", StringComparison.Ordinal))
        {
            _ultimatumInboxSelected = item.TargetId;
            _dealInboxSelected = -1;
            _assistanceInboxSelected = -1;
            _situationLayer.Visible = false;
            _situationReturn = "Inbox";
            OpenDealInbox();
            UpdateScreenButtons();
            return;
        }
        if (item.Id == "enclave_swap")
        {
            var note = Corridors.ProposeSwap(_campaign, _campaign.Player);
            if (note.Length == 0)
            {
                SetArmyFeedback(Tr("The exchange has been offered."));
                _situationLayer.Visible = false;
                _situationReturn = "Inbox";
                OpenDealInbox();
                UpdateScreenButtons();
            }
            else SetArmyFeedback(note);
            return;
        }
        if (item.Id == "border_pore")
        {
            var note = Corridors.ProposePore(_campaign, _campaign.Player);
            if (note.Length == 0)
            {
                SetArmyFeedback(Tr("The ask has been sent."));
                _situationLayer.Visible = false;
                _situationReturn = "Inbox";
                OpenDealInbox();
                UpdateScreenButtons();
            }
            else SetArmyFeedback(note);
            return;
        }
        _situationLayer.Visible = false; _situationReturn = "";
        switch (item.Route)
        {
            case SituationRoute.Inbox: _assistanceInboxSelected = -1; _dealInboxSelected = item.TargetId; OpenDealInbox(); _situationReturn = "Inbox"; break;
            case SituationRoute.Assistance: _dealInboxSelected = -1; _assistanceInboxSelected = item.TargetId; OpenDealInbox(); _situationReturn = "Inbox"; break;
            case SituationRoute.PeaceOffer: OpenWarLedger(); ShowWarLedgerDetail(item.TargetId); _situationReturn = "Wars"; break;
            case SituationRoute.Organization:
                _selectedOrganization = item.TargetId; _conflictTab = 4; OpenScreen("Conflicts"); _situationReturn = "Conflicts"; break;
            case SituationRoute.Conflict:
                _conflictTab = 2; OpenScreen("Conflicts"); _situationReturn = "Conflicts"; break;
            case SituationRoute.Crises:
                if (item.TargetId > 0) _crisisSelectedRegion = item.TargetId;
                OpenScreen("Laws"); SelectPolicyTab("Crises"); _situationReturn = "Laws"; break;
            case SituationRoute.Finance: OpenSovereignFinance(); _situationReturn = "Finance"; break;
            case SituationRoute.Market:
                OpenEconomyTab("Market"); _marketSearch.Text = Tr(CommodityCatalog.Get(item.Good.Value).Name);
                _shortagesOnly.ButtonPressed = false; ShowEconomyScreen(); _situationReturn = "Economy"; break;
            case SituationRoute.Army:
                var army = _campaign.Armies.FirstOrDefault(a => a.Id == item.TargetId && a.NationId == _campaign.Player.Id);
                if (army is not null) { FocusArmy(army); _situationReturn = "Army"; }
                break;
            case SituationRoute.Construction:
                if (item.TargetId > 0) _selectedState = _campaign.States.FirstOrDefault(s => s.MapIndex == item.TargetId);
                OpenEconomyTab("Construction"); _situationReturn = "Economy"; break;
        }
        UpdateScreenButtons();
    }

    private bool SituationDestinationEnabled(SituationItem item)
    {
        var screen = item.Route switch
        {
            SituationRoute.Inbox or SituationRoute.Assistance => "Diplomacy", SituationRoute.PeaceOffer => "Wars",
            SituationRoute.Organization or SituationRoute.Conflict => "Conflicts", SituationRoute.Crises => "Laws",
            SituationRoute.Finance or SituationRoute.Market or SituationRoute.Construction => "Economy", _ => "Military"
        };
        return MechanicFeatures.ScreenEnabled(_campaign, screen) &&
            (item.Route != SituationRoute.Crises || _policyTabs.ContainsKey("Crises")) &&
            (item.Route != SituationRoute.Market || item.Good.HasValue);
    }

    private bool HandleSituationEscape(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return false;
        if (_handbookLayer is { Visible: true } || _settingsLayer is { Visible: true } || _saveLoadLayer is { Visible: true } ||
            _pauseMenuLayer is { Visible: true } || _eventLayer is { Visible: true } || _peaceOfferLayer is { Visible: true }) return false;
        // Search keeps the ordinary first-Esc-to-release-focus contract.
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return false;
        if (_situationLayer is { Visible: true }) { CloseSituationRoom(); GetViewport().SetInputAsHandled(); return true; }
        var childOpen = _situationReturn switch
        {
            "Inbox" => IsInstanceValid(_dealComposer), "Wars" => _warLedgerLayer is { Visible: true },
            "Conflicts" => _conflictsLayer is { Visible: true }, "Laws" => _policyLayer is { Visible: true },
            "Finance" => _sovereignFinanceLayer is { Visible: true }, "Economy" => _sidePanel is { Visible: true } && _openScreen == "Economy",
            "Army" => _armyPanel is { Visible: true }, _ => false
        };
        if (!childOpen) return false;
        if (_situationReturn == "Inbox") CloseDealComposer();
        OpenSituationRoom(); GetViewport().SetInputAsHandled(); return true;
    }
}
