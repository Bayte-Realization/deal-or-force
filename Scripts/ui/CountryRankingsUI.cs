using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private const int CountryRankingPageSize = 32;
    private static readonly float[] RankingColumnWidths = { 100, 160, 140, 116, 80, 100 };
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
    private Button _countryRankingOrder, _countryRankingPrevious, _countryRankingNext;
    private Label _countryRankingCount, _countryRankingDetails, _countryRankingFormula;
    private DecisionDialogLayout _countryRankingLayout;
    private readonly List<(Button Button, Func<Nation> Nation)> _countryRankBadges = new();
    private readonly List<(Button Button, Control Flag, Label[] Labels)> _countryRankingSlots = new();

    private static CountryRankings.Weights RankingWeights => new(
        Ui.Metric("rankings.weight.economy", 40), Ui.Metric("rankings.weight.population", 15),
        Ui.Metric("rankings.weight.field_power", 30), Ui.Metric("rankings.weight.technology", 15));

    // One detached projection is shared by all badges. No per-badge world/army scans.
    // Force a fresh read when opening; while a sliced tick is incomplete retain the last
    // coherent document instead of comparing countries at different points in that tick.
    private bool ReadCountryRankings(bool force = false)
    {
        var c = _campaign;
        var sameViewer = ReferenceEquals(c, _countryRankingCampaign) && _countryRankingPlayer == (c?.Player?.Id ?? -1) &&
            _countryRankingObserver == _observerMode;
        if (c is null || c.MidStep)
        {
            if (sameViewer) return false;
            // A seat/world switch may happen between slices. Never retain somebody
            // else's private statistics while waiting for the new coherent document.
            _countryRankingSnapshot = Array.Empty<CountryRankings.Row>();
            _countryRankingCampaign = c; _countryRankingHour = -1;
            _countryRankingPlayer = c?.Player?.Id ?? -1; _countryRankingObserver = _observerMode;
            _countryRankingSnapshotRevision++;
            return true;
        }
        if (!force && Time.GetTicksMsec() < _countryRankingNextRead && ReferenceEquals(c, _countryRankingCampaign) && _countryRankingHour == c.ElapsedHours &&
            _countryRankingLanguage == Loc.Language && _countryRankingPlayer == (c.Player?.Id ?? -1) &&
            _countryRankingObserver == _observerMode) return false;
        // This is a public world leaderboard, not an intelligence report. Restricting
        // membership to inspectable armies reduced a normal player's list to themselves.
        // Only national aggregates are published here; positions and orders stay private.
        var snapshot = CountryRankings.Read(c, RankingWeights);
        var changed = !sameViewer || _countryRankingLanguage != Loc.Language || !snapshot.SequenceEqual(_countryRankingSnapshot);
        if (changed) _countryRankingSnapshotRevision++;
        _countryRankingSnapshot = snapshot;
        _countryRankingCampaign = c; _countryRankingHour = c.ElapsedHours;
        _countryRankingNextRead = Time.GetTicksMsec() + 1000;
        _countryRankingLanguage = Loc.Language; _countryRankingPlayer = c.Player?.Id ?? -1;
        _countryRankingObserver = _observerMode;
        return changed;
    }

    private VBoxContainer RankedFlag(Control flag, string name, Func<Nation> nation)
    {
        var column = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        column.AddThemeConstantOverride("separation", 2); column.AddChild(flag);
        var badge = ActionButton("", () => OpenCountryRankings(nation()?.Id ?? -1), 48, 24);
        badge.Name = name; badge.ClipText = true;
        badge.AddThemeFontSizeOverride("font_size", 16);
        column.AddChild(badge); _countryRankBadges.Add((badge, nation));
        return column;
    }

    private void RefreshCountryRankBadges()
    {
        ReadCountryRankings();
        foreach (var (button, nation) in _countryRankBadges)
        {
            var n = nation();
            var row = n is null ? null : _countryRankingSnapshot.FirstOrDefault(r => r.NationId == n.Id);
            button.Text = row is null ? "—" : "#" + row.OverallRank;
            button.TooltipText = row is null ? Tr("Country rankings · no visible territorial statistics")
                : n.DisplayName + "\n" + Loc.Format("Overall rank #{0} · index {1}/100", row.OverallRank, row.Overall.ToString("0.0")) +
                    "\n" + Tr("Open the full country rankings");
        }
    }

    private void BuildCountryRankings()
    {
        _countryRankingsLayer = FullLayer("CountryRankings"); AddChild(_countryRankingsLayer);
        var veil = new ColorRect { Color = Ui.Colour("surface.overlay", .92f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _countryRankingsLayer.AddChild(veil);
        _countryRankingsPanel = TexturedPanel(_countryRankingsLayer);
        _countryRankingsPanel.Name = "CountryRankingsPanel"; Ui.Place(_countryRankingsPanel, "modal.rankings");
        var rows = PanelRows(_countryRankingsPanel, 20, 14, 8);
        var close = ActionButton("×", CloseCountryRankings, 44, 34); close.Name = "CloseCountryRankings";
        ScreenHeader(rows, "Country rankings", "Compare all territorial countries. National ranking statistics are public; unit positions and orders remain hidden.", false, close);
        var tools = new HBoxContainer(); tools.AddThemeConstantOverride("separation", 8); rows.AddChild(tools);
        _countryRankingSearch = Placeholder(new LineEdit { Name = "CountryRankingSearch", ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill }, "Find a country or tag...");
        _countryRankingSearch.TextChanged += _ => { _countryRankingPage = 0; RefreshCountryRankingPage(); };
        tools.AddChild(_countryRankingSearch);
        _countryRankingMetric = new OptionButton { Name = "CountryRankingMetric", CustomMinimumSize = new(220, 0) };
        foreach (var metric in Enum.GetValues<CountryRankings.Metric>())
            _countryRankingMetric.AddItem(Tr(RankingMetricName(metric)), (int)metric);
        _countryRankingMetric.ItemSelected += _ => { _countryRankingPage = 0; RefreshCountryRankingPage(); };
        tools.AddChild(_countryRankingMetric);
        _countryRankingOrder = ActionButton("Descending", () =>
        { _countryRankingDescending = !_countryRankingDescending; _countryRankingPage = 0; RefreshCountryRankingPage(); }, 0, 34);
        _countryRankingOrder.Name = "CountryRankingOrder"; tools.AddChild(_countryRankingOrder);
        var refresh = ActionButton("Refresh rankings", () => { ReadCountryRankings(true); RefreshCountryRankBadges(); RefreshCountryRankingPage(); }, 0, 34);
        refresh.Name = "RefreshCountryRankings"; tools.AddChild(refresh);
        var heading = new HBoxContainer { Name = "CountryRankingColumns" }; heading.AddThemeConstantOverride("separation", 8);
        AddRankingCell(heading, "#", 60); AddRankingCell(heading, "", 42);
        AddRankingCell(heading, "Country", 240, true);
        var titles = new[] { "Ranking index", "GDP", "Population", "Ground power", "Tech.", "Living std." };
        for (var i = 0; i < titles.Length; i++)
        {
            var label = AddRankingCell(heading, titles[i], RankingColumnWidths[i]);
            label.Name = "RankingColumn_" + i; label.HorizontalAlignment = HorizontalAlignment.Right;
        }
        rows.AddChild(heading);
        var scroll = new ScrollContainer { Name = "CountryRankingScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        rows.AddChild(scroll);
        _countryRankingRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _countryRankingRows.AddThemeConstantOverride("separation", 3); scroll.AddChild(_countryRankingRows);
        _countryRankingDetails = WrappedUltimatumText("", 14); _countryRankingDetails.Name = "CountryRankingDetails";
        rows.AddChild(_countryRankingDetails);
        var footer = new HBoxContainer(); footer.AddThemeConstantOverride("separation", 10); rows.AddChild(footer);
        _countryRankingPrevious = ActionButton("←", () => { _countryRankingPage--; RefreshCountryRankingPage(); }, 48, 34);
        _countryRankingPrevious.Name = "CountryRankingPrevious"; footer.AddChild(_countryRankingPrevious);
        _countryRankingCount = Title("", 14, Ui.Colour("text.body_soft"));
        _countryRankingCount.Name = "CountryRankingCount"; _countryRankingCount.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(_countryRankingCount);
        _countryRankingNext = ActionButton("→", () => { _countryRankingPage++; RefreshCountryRankingPage(); }, 48, 34);
        _countryRankingNext.Name = "CountryRankingNext"; footer.AddChild(_countryRankingNext);
        footer.AddChild(ActionButton("My country", () =>
        { _countryRankingSearch.Text = ""; _countryRankingSelected = _campaign?.Player?.Id ?? -1; LocateRankingSelection(); RefreshCountryRankingPage(); }, 0, 34));
        footer.AddChild(ActionButton("Ranking country dossier", () =>
        {
            var nation = _campaign?.Nations.FirstOrDefault(n => n.Id == _countryRankingSelected);
            if (nation is null) return;
            CloseCountryRankings(); ShowMapSelection(new GlobePick(nation, null, -1, nation.CapitalLatitude, nation.CapitalLongitude));
        }, 0, 34));
        _countryRankingFormula = WrappedUltimatumText("", 14); rows.AddChild(_countryRankingFormula);
        ApplyAtlasHeading(_countryRankingsPanel);
        _countryRankingLayout = new DecisionDialogLayout { Panel = _countryRankingsPanel, LayoutKey = "modal.rankings" };
        _countryRankingsLayer.AddChild(_countryRankingLayout);
        _countryRankingsLayer.AddChild(new CountryRankingPulse { Refresh = () =>
        {
            if (!_countryRankingsLayer.Visible) return;
            ReadCountryRankings();
            if (_countryRankingRenderedRevision != _countryRankingSnapshotRevision)
            { RefreshCountryRankBadges(); RefreshCountryRankingPage(); }
        }});
        _countryRankingsLayer.Hide();
    }

    private void OpenCountryRankings(int nationId = -1)
    {
        if (_campaign is null) return;
        if (_countryRankingsLayer is null) BuildCountryRankings();
        _countryRankingReturnFocus = GetViewport().GuiGetFocusOwner();
        ReadCountryRankings(true); RefreshCountryRankBadges();
        _countryRankingSelected = nationId >= 0 ? nationId : _campaign.Player?.Id ?? -1;
        _countryRankingSearch.Text = ""; LocateRankingSelection();
        _countryRankingsLayer.Show(); _countryRankingsLayer.MoveToFront();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = true;
        RefreshCountryRankingPage(); _countryRankingSearch.GrabFocus();
    }

    private void CloseCountryRankings()
    {
        _countryRankingsLayer?.Hide();
        if (_globe is not null) _globe.MediaLibraryInputBlocked = _mediaLibraryLayer is { Visible: true };
        RestoreDecisionFocus(ref _countryRankingReturnFocus);
    }

    private void HandleCountryRankingsInput(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        { CloseCountryRankings(); GetViewport().SetInputAsHandled(); }
    }

    private void LocateRankingSelection()
    {
        var metric = (CountryRankings.Metric)(_countryRankingMetric?.Selected ?? 0);
        var query = CountryRankings.Query(_countryRankingSnapshot, metric, _countryRankingDescending, _countryRankingSearch?.Text);
        var index = query.ToList().FindIndex(r => r.NationId == _countryRankingSelected);
        _countryRankingPage = Math.Max(0, index) / CountryRankingPageSize;
    }

    private void RefreshCountryRankingPage()
    {
        if (_countryRankingRows is null) return;
        _countryRankingRenderedRevision = _countryRankingSnapshotRevision;
        _countryRankingQuery = CountryRankings.Query(_countryRankingSnapshot,
            (CountryRankings.Metric)_countryRankingMetric.Selected, _countryRankingDescending, _countryRankingSearch.Text);
        var pages = Math.Max(1, (_countryRankingQuery.Count + CountryRankingPageSize - 1) / CountryRankingPageSize);
        _countryRankingPage = Math.Clamp(_countryRankingPage, 0, pages - 1);
        // A fixed row pool preserves the controls, keyboard focus and scrollbar while
        // sorting, paging or refreshing a live campaign. No per-tick GUI destruction.
        while (_countryRankingSlots.Count < CountryRankingPageSize)
        {
            var slot = _countryRankingSlots.Count;
            var button = ActionButton("", () =>
            {
                var row = _countryRankingQuery.ElementAtOrDefault(_countryRankingPage * CountryRankingPageSize + slot);
                if (row is null) return;
                _countryRankingSelected = row.NationId; RefreshCountryRankingPage();
            }, 0, 40);
            button.Name = "CountryRankingRow_" + slot;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill; _countryRankingRows.AddChild(button);
            var cells = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            cells.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); cells.OffsetLeft = 8; cells.OffsetRight = -8;
            cells.AddThemeConstantOverride("separation", 8); button.AddChild(cells);
            var labels = new List<Label> { AddRankingCell(cells, "", 60) };
            var flag = FlagBadge("", 24); flag.CustomMinimumSize = new(42, 24); cells.AddChild(flag);
            flag.MouseFilter = MouseFilterEnum.Ignore;
            foreach (var child in flag.FindChildren("*", "Control", true, false).OfType<Control>()) child.MouseFilter = MouseFilterEnum.Ignore;
            labels.Add(AddRankingCell(cells, "", 240, true));
            for (var i = 0; i < 6; i++)
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
            labels[7].Text = row.LivingStandards.ToString("0.0");
        }
        _countryRankingPrevious.Disabled = _countryRankingPage == 0; _countryRankingNext.Disabled = _countryRankingPage >= pages - 1;
        _countryRankingOrder.Text = Tr(_countryRankingDescending ? "Descending" : "Ascending");
        for (var i = 0; i < _countryRankingMetric.ItemCount; i++)
            _countryRankingMetric.SetItemText(i, Tr(RankingMetricName((CountryRankings.Metric)i)));
        _countryRankingCount.Text = Loc.Format("{0} countries · page {1}/{2}", _countryRankingQuery.Count, _countryRankingPage + 1, pages);
        var selected = _countryRankingSnapshot.FirstOrDefault(r => r.NationId == _countryRankingSelected);
        _countryRankingDetails.Text = selected is null ? Tr("Select a country to inspect its position.")
            : selected.Name + " · " + Loc.Format("Overall rank #{0} · index {1}/100", selected.OverallRank, selected.Overall.ToString("0.0"));
        var weights = RankingWeights.Normalize();
        _countryRankingFormula.Text = Loc.Format("Index: GDP {0}% · population {1}% · ground power {2}% · technology {3}%.",
            weights.Economy.ToString("0.#"), weights.Population.ToString("0.#"), weights.FieldPower.ToString("0.#"), weights.Technology.ToString("0.#"));
        _countryRankingFormula.TooltipText = Tr("Each component is divided by the largest world value. Ground power is a public national aggregate, not a report of unit locations. Fleets and aircraft are not included. Living standards are a separate comparison. Landless countries are excluded. Weights are moddable in game/interface.");
        _countryRankingLayout?.Refit();
    }

    private static string RankingMetricName(CountryRankings.Metric metric) => metric switch
    {
        CountryRankings.Metric.Economy => "GDP", CountryRankings.Metric.Population => "Population",
        CountryRankings.Metric.FieldPower => "Ground power", CountryRankings.Metric.Technology => "Technology",
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
