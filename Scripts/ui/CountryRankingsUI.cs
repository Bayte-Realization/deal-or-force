using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private const int CountryRankingPageSize = 32;
    private static readonly float[] RankingColumnWidths = { 96, 108, 100, 98, 62, 74, 80 };
    private IReadOnlyList<CountryRankings.Row> _countryRankingSnapshot = Array.Empty<CountryRankings.Row>();
    private IReadOnlyList<CountryRankings.Row> _countryRankingQuery = Array.Empty<CountryRankings.Row>();
    private Campaign _countryRankingCampaign;
    private long _countryRankingHour = -1;
    private ulong _countryRankingNextRead;
    private int _countryRankingSnapshotRevision, _countryRankingRenderedRevision = -1;
    private string _countryRankingLanguage;
    private int _countryRankingPlayer = -1, _countryRankingPage, _countryRankingSelected = -1;
    private bool _countryRankingObserver, _countryRankingDescending = true;
    private Control _countryRankingsLayer, _countryRankingReturnFocus;
    private PanelContainer _countryRankingsPanel;
    private VBoxContainer _countryRankingRows;
    private LineEdit _countryRankingSearch;
    private OptionButton _countryRankingMetric;
    private Button _countryRankingOrder, _countryRankingPrevious, _countryRankingNext, _countryRankingMyCountry;
    private Label _countryRankingCount, _countryRankingDetails, _countryRankingFormula;
    private DecisionDialogLayout _countryRankingLayout;
    private readonly List<(Button Button, Func<Nation> Nation)> _countryRankBadges = new();
    private readonly List<(Button Button, Control Flag, Label[] Labels)> _countryRankingSlots = new();

    private static CountryRankings.Weights RankingWeights => new(
        Ui.Metric("rankings.weight.economy", 42), Ui.Metric("rankings.weight.population", 14),
        Ui.Metric("rankings.weight.field_power", 12), Ui.Metric("rankings.weight.technology", 17),
        Ui.Metric("rankings.weight.prestige", 15));

    private bool TryAdoptLockFreeRankingSnapshot(Campaign c)
    {
        if (c is null) return false;
        var snap = c.RenderSnapshot;
        if (snap is null || snap.Version <= 0 || snap.RankingRows is not { Count: > 0 } rows) return false;
        if (ReferenceEquals(c, _countryRankingCampaign) && _countryRankingHour == snap.ElapsedHours &&
            _countryRankingLanguage == Loc.Language && _countryRankingSnapshot.Count > 0 &&
            ReferenceEquals(_countryRankingSnapshot, rows))
            return false;
        StoreRankingSnapshot(c, rows, snap.ElapsedHours, c.Player?.Id ?? -1);
        _countryRankingSnapshotRevision++;
        return true;
    }

    private void StoreRankingSnapshot(Campaign c, IReadOnlyList<CountryRankings.Row> rows, long hour, int viewer)
    {
        _countryRankingSnapshot = rows; _countryRankingCampaign = c; _countryRankingHour = hour;
        _countryRankingNextRead = Time.GetTicksMsec() + 1000; _countryRankingLanguage = Loc.Language;
        _countryRankingPlayer = viewer; _countryRankingObserver = _observerMode;
    }

    private bool HandleMidStepRankings(Campaign c, int viewer)
    {
        if (TryAdoptLockFreeRankingSnapshot(c)) return true;
        if (!ReferenceEquals(c, _countryRankingCampaign) && _countryRankingSnapshot.Count > 0)
        {
            _countryRankingSnapshot = Array.Empty<CountryRankings.Row>();
            _countryRankingSnapshotRevision++;
        }
        _countryRankingCampaign = c; _countryRankingHour = -1; _countryRankingPlayer = viewer; _countryRankingObserver = _observerMode;
        return false;
    }

    private bool ReadCountryRankings(bool force = false)
    {
        var c = _campaign;
        var entered = c is null || (force ? c.AccessGate.TryEnterPriorityRead(80) : c.TryEnterRead(0, requestWindowOnMiss: false));
        if (!entered)
            return (force || _countryRankingSnapshot.Count == 0 || !ReferenceEquals(c, _countryRankingCampaign)) && TryAdoptLockFreeRankingSnapshot(c);
        try
        {
            var viewer = c?.Player?.Id ?? -1;
            var sameViewer = ReferenceEquals(c, _countryRankingCampaign) && _countryRankingPlayer == viewer && _countryRankingObserver == _observerMode;
            if (c is null)
            {
                if (sameViewer && _countryRankingSnapshot.Count == 0) return false;
                StoreRankingSnapshot(null, Array.Empty<CountryRankings.Row>(), -1, -1);
                _countryRankingSnapshotRevision++; return true;
            }
            if (c.MidStep) return HandleMidStepRankings(c, viewer);
            if (!force && Time.GetTicksMsec() < _countryRankingNextRead && sameViewer &&
                _countryRankingHour == c.ElapsedHours && _countryRankingLanguage == Loc.Language && _countryRankingSnapshot.Count > 0)
                return false;
            var snapshot = CountryRankings.Read(c, RankingWeights);
            var changed = !sameViewer || _countryRankingLanguage != Loc.Language || !snapshot.SequenceEqual(_countryRankingSnapshot);
            if (changed) _countryRankingSnapshotRevision++;
            StoreRankingSnapshot(c, snapshot, c.ElapsedHours, viewer);
            return changed;
        }
        finally { c?.ExitRead(); }
    }

    private VBoxContainer RankedFlag(Control flag, string name, Func<Nation> nation)
    {
        var column = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        column.AddThemeConstantOverride("separation", 2); column.AddChild(flag);
        var badge = ActionButton("", () =>
        {
            var targetId = -1;
            var c = _campaign;
            if (c is not null && c.AccessGate.TryEnterPriorityRead(80))
            {
                try { targetId = nation()?.Id ?? -1; } finally { c.ExitRead(); }
            }
            OpenCountryRankings(targetId);
        }, 48, 24);
        badge.Name = name; badge.ClipText = false;
        badge.AddThemeFontSizeOverride("font_size", 16);
        column.AddChild(badge); _countryRankBadges.Add((badge, nation));
        return column;
    }

    private void RefreshCountryRankBadges()
    {
        var c = _campaign;
        var entered = c is null || c.TryEnterRead(0, requestWindowOnMiss: false);
        try
        {
            if (entered) ReadCountryRankings(); else TryAdoptLockFreeRankingSnapshot(c);
            foreach (var (button, nation) in _countryRankBadges)
            {
                var n = nation();
                var row = n is null ? null : _countryRankingSnapshot.FirstOrDefault(r => r.NationId == n.Id);
                button.Text = row is null ? "—" : "#" + row.OverallRank;
                button.AddThemeFontSizeOverride("font_size", row is { OverallRank: >= 100 } ? 13 : 16);
                button.TooltipText = row is null ? Tr("Country rankings · no visible territorial statistics")
                    : n.DisplayName + "\n" + Loc.Format("Overall rank #{0} · index {1}/100", row.OverallRank, row.Overall.ToString("0.0")) +
                        "\n" + Tr("Open the full country rankings");
            }
        }
        finally { if (entered) c?.ExitRead(); }
    }

    private void BuildCountryRankings()
    {
        _countryRankingsLayer = FullLayer("CountryRankings"); AddChild(_countryRankingsLayer);
        var veil = new ColorRect { Color = Ui.Colour("surface.overlay", .92f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _countryRankingsLayer.AddChild(veil);
        _countryRankingsPanel = TexturedPanel(_countryRankingsLayer);
        _countryRankingsPanel.Name = "CountryRankingsPanel"; Ui.Place(_countryRankingsPanel, "modal.rankings");
        var rows = PanelRows(_countryRankingsPanel, 20, 14, 8);
        var isTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var close = ActionButton("×", CloseCountryRankings, isTouch ? 42 : 44, isTouch ? 40 : 34); close.Name = "CloseCountryRankings";
        close.AddThemeFontSizeOverride("font_size", isTouch ? 20 : 18);
        ScreenHeader(rows, "Country rankings", "Compare all territorial countries. National ranking statistics are public; unit positions and orders remain hidden.", false, close);
        var tools = new HBoxContainer(); tools.AddThemeConstantOverride("separation", 8); rows.AddChild(tools);
        _countryRankingSearch = Placeholder(new LineEdit { Name = "CountryRankingSearch", ClearButtonEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill }, "Find a country or tag...");
        _countryRankingSearch.TextChanged += _ => { _countryRankingPage = 0; RefreshCountryRankingPage(); }; tools.AddChild(_countryRankingSearch);
        _countryRankingMetric = new OptionButton { Name = "CountryRankingMetric", CustomMinimumSize = new(220, 0) };
        foreach (var metric in Enum.GetValues<CountryRankings.Metric>()) _countryRankingMetric.AddItem(Tr(RankingMetricName(metric)), (int)metric);
        _countryRankingMetric.ItemSelected += _ => { _countryRankingPage = 0; RefreshCountryRankingPage(); }; tools.AddChild(_countryRankingMetric);
        _countryRankingOrder = ActionButton("Descending", () => { _countryRankingDescending = !_countryRankingDescending; _countryRankingPage = 0; RefreshCountryRankingPage(); }, 0, 34);
        _countryRankingOrder.Name = "CountryRankingOrder"; tools.AddChild(_countryRankingOrder);
        var refresh = ActionButton("Refresh rankings", () =>
        {
            var c = _campaign;
            if (c is not null && c.AccessGate.TryEnterPriorityRead(80))
            {
                try { ReadCountryRankings(true); RefreshCountryRankBadges(); } finally { c.ExitRead(); }
            }
            else { TryAdoptLockFreeRankingSnapshot(c); RefreshCountryRankBadges(); }
            RefreshCountryRankingPage();
        }, 0, 34);
        refresh.Name = "RefreshCountryRankings"; tools.AddChild(refresh);
        var heading = new HBoxContainer { Name = "CountryRankingColumns" }; heading.AddThemeConstantOverride("separation", 8);
        AddRankingCell(heading, "#", 50); AddRankingCell(heading, "", 42); AddRankingCell(heading, "Country", 185, true);
        var titles = new[] { "Ranking index", "GDP", "Population", "Ground power", "Tech.", "Prestige", "Living std." };
        for (var i = 0; i < titles.Length; i++) { var l = AddRankingCell(heading, titles[i], RankingColumnWidths[i]); l.Name = "RankingColumn_" + i; l.HorizontalAlignment = HorizontalAlignment.Right; }
        rows.AddChild(heading);
        var scroll = new ScrollContainer { Name = "CountryRankingScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        rows.AddChild(scroll);
        _countryRankingRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _countryRankingRows.AddThemeConstantOverride("separation", 3); scroll.AddChild(_countryRankingRows);
        _countryRankingDetails = WrappedUltimatumText("", 14); _countryRankingDetails.Name = "CountryRankingDetails"; rows.AddChild(_countryRankingDetails);
        var footer = new HBoxContainer(); footer.AddThemeConstantOverride("separation", 10); rows.AddChild(footer);
        _countryRankingPrevious = ActionButton("←", () => { _countryRankingPage--; RefreshCountryRankingPage(); }, 48, 34); _countryRankingPrevious.Name = "CountryRankingPrevious"; footer.AddChild(_countryRankingPrevious);
        _countryRankingCount = Title("", 14, Ui.Colour("text.body_soft")); _countryRankingCount.Name = "CountryRankingCount"; _countryRankingCount.SizeFlagsHorizontal = SizeFlags.ExpandFill; footer.AddChild(_countryRankingCount);
        _countryRankingNext = ActionButton("→", () => { _countryRankingPage++; RefreshCountryRankingPage(); }, 48, 34); _countryRankingNext.Name = "CountryRankingNext"; footer.AddChild(_countryRankingNext);
        AddCountryRankingFooterActions(footer);
        _countryRankingFormula = WrappedUltimatumText("", 14); rows.AddChild(_countryRankingFormula);
        ApplyAtlasHeading(_countryRankingsPanel);
        _countryRankingLayout = new DecisionDialogLayout { Panel = _countryRankingsPanel, LayoutKey = "modal.rankings" };
        _countryRankingsLayer.AddChild(_countryRankingLayout);
        _countryRankingsLayer.AddChild(new CountryRankingPulse { Refresh = TickCountryRankingsPulse });
        _countryRankingsLayer.Hide();
    }

    private void TickCountryRankingsPulse()
    {
        if (!_countryRankingsLayer.Visible) return;
        var campaign = _campaign;
        if (campaign is null) return;
        if (_simRunner.IsActive && ReferenceEquals(_simRunner.Campaign, campaign))
        {
            TryAdoptLockFreeRankingSnapshot(campaign);
            if (_countryRankingSnapshot.Count == 0 && campaign.AccessGate.TryEnterPriorityRead(40))
            {
                try { ReadCountryRankings(true); } finally { campaign.ExitRead(); }
            }
            if (_countryRankingRenderedRevision != _countryRankingSnapshotRevision) { RefreshCountryRankBadges(); RefreshCountryRankingPage(); }
            return;
        }
        ReadCountryRankings();
        if (_countryRankingRenderedRevision != _countryRankingSnapshotRevision) { RefreshCountryRankBadges(); RefreshCountryRankingPage(); }
    }

    private void OpenCountryRankings(int nationId = -1)
    {
        if (_campaign is null) return;
        if (_countryRankingsLayer is null) BuildCountryRankings();
        _countryRankingReturnFocus = GetViewport().GuiGetFocusOwner();
        var campaign = _campaign;
        var entered = campaign.AccessGate.TryEnterPriorityRead(80);
        if (entered)
        {
            try { ReadCountryRankings(true); RefreshCountryRankBadges(); _countryRankingSelected = nationId >= 0 ? nationId : campaign.Player?.Id ?? -1; }
            finally { campaign.ExitRead(); }
        }
        if (!entered || _countryRankingSnapshot.Count == 0)
        {
            TryAdoptLockFreeRankingSnapshot(campaign);
            if (!entered) _countryRankingSelected = nationId >= 0 ? nationId : _countryRankingPlayer;
        }
        _countryRankingSearch.Text = ""; LocateRankingSelection();
        _countryRankingsLayer.Show(); _countryRankingsLayer.MoveToFront();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = true;
        RefreshCountryRankingPage(); Ui.FocusInputIfDesktop(_countryRankingSearch);
    }

    private void CloseCountryRankings()
    {
        _countryRankingsLayer?.Hide();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = _mediaLibraryLayer is { Visible: true };
        RestoreMapHighlightAfterRankings();
        RestoreDecisionFocus(ref _countryRankingReturnFocus);
    }

    private void FocusAndHighlightRankingNation(int nationId)
    {
        var campaign = _campaign;
        if (campaign is null || !campaign.AccessGate.TryEnterPriorityRead(80)) return;
        Nation nation; int mapIndex;
        try { nation = campaign.Nations.FirstOrDefault(n => n.Id == nationId); if (nation is null) return; mapIndex = nation.MapIndex; FocusNationCapital(nation); }
        finally { campaign.ExitRead(); }
        _globe?.HighlightNation(mapIndex);
    }

    private void AddCountryRankingFooterActions(HBoxContainer footer)
    {
        _countryRankingMyCountry = ActionButton("My country", () =>
        {
            var campaign = _campaign;
            if (campaign is null || !campaign.AccessGate.TryEnterPriorityRead(80)) return;
            int playerId;
            try { playerId = campaign.Player?.Id ?? -1; } finally { campaign.ExitRead(); }
            if (playerId <= 0) return;
            _countryRankingSearch.Text = ""; _countryRankingSelected = playerId; LocateRankingSelection(); RefreshCountryRankingPage(); FocusAndHighlightRankingNation(_countryRankingSelected);
        }, 0, 34);
        footer.AddChild(_countryRankingMyCountry);
        footer.AddChild(ActionButton("Ranking country dossier", () =>
        {
            var campaign = _campaign;
            if (campaign is null || !campaign.AccessGate.TryEnterPriorityRead(80)) return;
            try
            {
                var nation = campaign.Nations.FirstOrDefault(n => n.Id == _countryRankingSelected);
                if (nation is null) return;
                var lat = nation.CapitalLatitude; var lon = nation.CapitalLongitude;
                CloseCountryRankings(); FocusNationCapital(nation); ShowMapSelection(new GlobePick(nation, null, -1, lat, lon));
            }
            finally { campaign.ExitRead(); }
        }, 0, 34));
    }

    private void RestoreMapHighlightAfterRankings()
    {
        if (_globe is null) return;
        if (IsNewShellActive && _shell?.WorkPanel is { IsVisible: true, Mode: ShellWorkPanelView.DossierMode.Country } wp)
        {
            var campaign = _campaign;
            if (campaign is null || !campaign.AccessGate.TryEnterPriorityRead(80)) return;
            int activeMapIndex;
            try { activeMapIndex = campaign.Nations.FirstOrDefault(n => n.Id == wp.TargetNationId)?.MapIndex ?? -1; } finally { campaign.ExitRead(); }
            _globe.HighlightNation(activeMapIndex);
            return;
        }
        if (!IsNewShellActive && _mapSelectionOpen && _mapSelectionHighlights && _selectedNation is not null)
        {
            _globe.HighlightNation(_selectedNation.MapIndex);
            return;
        }
        _globe.HighlightNation(-1);
    }

    private void HandleCountryRankingsInput(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        { CloseCountryRankings(); GetViewport().SetInputAsHandled(); }
    }

    private void LocateRankingSelection()
    {
        var metric = _countryRankingMetric is not null
            ? (CountryRankings.Metric)_countryRankingMetric.GetSelectedId()
            : CountryRankings.Metric.Overall;
        var query = CountryRankings.Query(_countryRankingSnapshot, metric, _countryRankingDescending, _countryRankingSearch?.Text);
        var index = query.ToList().FindIndex(r => r.NationId == _countryRankingSelected);
        _countryRankingPage = Math.Max(0, index) / CountryRankingPageSize;
    }

    private void RefreshCountryRankingPage()
    {
        if (_countryRankingRows is null) return;
        _countryRankingRenderedRevision = _countryRankingSnapshotRevision;
        var selectedMetric = (CountryRankings.Metric)_countryRankingMetric.GetSelectedId();
        _countryRankingQuery = CountryRankings.Query(_countryRankingSnapshot,
            selectedMetric, _countryRankingDescending, _countryRankingSearch.Text);
        var pages = Math.Max(1, (_countryRankingQuery.Count + CountryRankingPageSize - 1) / CountryRankingPageSize);
        _countryRankingPage = Math.Clamp(_countryRankingPage, 0, pages - 1);
        // A fixed row pool preserves the controls, keyboard focus and scrollbar while
        // sorting, paging or refreshing a live campaign. No per-tick GUI destruction.
        var isTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        while (_countryRankingSlots.Count < CountryRankingPageSize)
        {
            var slot = _countryRankingSlots.Count;
            var button = ActionButton("", () =>
            {
                var row = _countryRankingQuery.ElementAtOrDefault(_countryRankingPage * CountryRankingPageSize + slot);
                if (row is null) return;
                _countryRankingSelected = row.NationId;
                RefreshCountryRankingPage();
                FocusAndHighlightRankingNation(row.NationId);
            }, 0, isTouch ? 46 : 40);
            button.Name = "CountryRankingRow_" + slot;
            button.MouseFilter = MouseFilterEnum.Pass;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill; _countryRankingRows.AddChild(button);
            var cells = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            cells.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); cells.OffsetLeft = 8; cells.OffsetRight = -8;
            cells.AddThemeConstantOverride("separation", 8); button.AddChild(cells);
            var labels = new List<Label> { AddRankingCell(cells, "", 50) };
            var flag = FlagBadge("", 24); flag.CustomMinimumSize = new(42, 24); cells.AddChild(flag);
            flag.MouseFilter = MouseFilterEnum.Ignore;
            foreach (var child in flag.FindChildren("*", "Control", true, false).OfType<Control>()) child.MouseFilter = MouseFilterEnum.Ignore;
            labels.Add(AddRankingCell(cells, "", 185, true));
            for (var i = 0; i < RankingColumnWidths.Length; i++)
            {
                var value = AddRankingCell(cells, "", RankingColumnWidths[i]);
                value.AddThemeFontOverride("font", Ui.NumberFont); value.HorizontalAlignment = HorizontalAlignment.Right;
                labels.Add(value);
            }
            _countryRankingSlots.Add((button, flag, labels.ToArray()));
        }
        for (var i = 0; i < _countryRankingSlots.Count; i++)
        {
            var (button, flag, labels) = _countryRankingSlots[i];
            var row = _countryRankingQuery.ElementAtOrDefault(_countryRankingPage * CountryRankingPageSize + i);
            button.Visible = row is not null;
            if (row is null) continue;
            button.SetMeta("nation_id", row.NationId); button.TooltipText = row.Name + " · " + row.Tag;
            StyleButton(button, row.NationId == _countryRankingSelected); SetFlag(flag, row.Tag, 24);
            flag.CustomMinimumSize = new(42, 24);
            labels[0].Text = "#" + row.OverallRank; labels[1].Text = row.Name;
            labels[2].Text = row.Overall.ToString("0.0"); labels[3].Text = Fmt.Money((float)row.Economy);
            labels[4].Text = Fmt.People(row.Population >= long.MaxValue ? long.MaxValue : (long)row.Population);
            labels[5].Text = row.FieldPower.ToString("0.0"); labels[6].Text = row.Technology.ToString("0");
            labels[7].Text = row.Prestige.ToString("0"); labels[8].Text = row.LivingStandards.ToString("0.0");
        }
        _countryRankingPrevious.Disabled = _countryRankingPage == 0; _countryRankingNext.Disabled = _countryRankingPage >= pages - 1;
        _countryRankingOrder.Text = Tr(_countryRankingDescending ? "Descending" : "Ascending");
        for (var i = 0; i < _countryRankingMetric.ItemCount; i++)
            _countryRankingMetric.SetItemText(i, Tr(RankingMetricName((CountryRankings.Metric)_countryRankingMetric.GetItemId(i))));
        _countryRankingCount.Text = Loc.Format("{0} countries · page {1}/{2}", _countryRankingQuery.Count, _countryRankingPage + 1, pages);
        var selected = _countryRankingSnapshot.FirstOrDefault(r => r.NationId == _countryRankingSelected);
        _countryRankingDetails.Text = selected is null ? Tr("Select a country to inspect its position.")
            : selected.Name + " · " + Loc.Format("Overall rank #{0} · index {1}/100", selected.OverallRank, selected.Overall.ToString("0.0"));
        var weights = RankingWeights.Normalize();
        _countryRankingFormula.Text = Loc.Format("ui.prestige.ranking_formula_fmt",
            weights.Economy.ToString("0.#"), weights.Population.ToString("0.#"),
            weights.FieldPower.ToString("0.#"), weights.Technology.ToString("0.#"), weights.Prestige.ToString("0.#"));
        _countryRankingFormula.TooltipText = Tr("ui.prestige.ranking_formula_tip");
        if (IsInstanceValid(_countryRankingMyCountry))
        {
            var hasOwn = _campaign?.Player is not null && !WatchingWorld;
            _countryRankingMyCountry.Disabled = !hasOwn;
            _countryRankingMyCountry.TooltipText = Ui.Tip(hasOwn ? "" : Tr("obs.ui.rankings.no_own_country"));
        }
        _countryRankingLayout?.Refit();
    }

    private static string RankingMetricName(CountryRankings.Metric metric) => metric switch
    {
        CountryRankings.Metric.Economy => "GDP", CountryRankings.Metric.Population => "Population",
        CountryRankings.Metric.FieldPower => "Ground power", CountryRankings.Metric.Technology => "Technology",
        CountryRankings.Metric.Prestige => "Prestige",
        CountryRankings.Metric.LivingStandards => "Living standards", _ => "Overall index"
    };

    private static Label AddRankingCell(HBoxContainer parent, string text, float width, bool stretch = false)
    {
        var label = Title(text, 14, Ui.Colour("text.body")); label.MouseFilter = MouseFilterEnum.Ignore;
        label.CustomMinimumSize = new(width, 28); label.ClipText = true;
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = stretch ? SizeFlags.ExpandFill : SizeFlags.Fill;
        label.SizeFlagsVertical = SizeFlags.Fill; parent.AddChild(label);
        return label;
    }

    internal IReadOnlyList<CountryRankings.Row> VerificationRankingRows => _countryRankingQuery;
    internal IReadOnlyList<CountryRankings.Row> VerificationRankingSnapshot => _countryRankingSnapshot;
    internal int VerificationRankingPage => _countryRankingPage;
    internal int VerificationRankingSelected => _countryRankingSelected;
    internal bool VerificationRankingDescending => _countryRankingDescending;
    internal bool VerificationRankingCanInspect(Nation n) => CanInspectNation(n.Id);
}

public partial class CountryRankingPulse : Node
{
    public Action Refresh;
    private double _remaining;
    public override void _Process(double delta)
    { if ((_remaining -= delta) > 0) return; _remaining = .25; Refresh?.Invoke(); }
}
