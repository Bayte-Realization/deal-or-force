using System;
using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
{
    private Control _pickerLayer = null!;
    private LineEdit _pickerSearch = null!;
    private VBoxContainer _pickerResults = null!;
    private Label _pickerName = null!;
    private Label _pickerTier = null!;
    private Label _pickerStats = null!;
    private Label _pickerLocation = null!;
    private Button _pickerBegin = null!;
    private Button _pickerSeatToggle = null!;
    private Label _pickerHeading = null!;
    private Label _pickerSubheading = null!;
    private VBoxContainer _pickerSeats = null!;
    private Nation _pickerNation;
    private bool _pickerHotseatMode;

    private void BuildCountryPicker()
    {
        _pickerLayer = FullLayer("CountryPicker");
        _pickerLayer.Visible = false;
        AddChild(_pickerLayer);

        var heading = TexturedPanel(_pickerLayer);
        heading.AnchorLeft = 0;
        heading.AnchorRight = 0;
        heading.AnchorTop = 0;
        heading.AnchorBottom = 0;
        heading.OffsetLeft = 18;
        heading.OffsetRight = 455;
        heading.OffsetTop = 18;
        heading.OffsetBottom = 106;
        var headingRows = PanelRows(heading, 18, 10, 2);
        _pickerHeading = Title("SELECT A COUNTRY", 26, new Color("#e7c966"));
        headingRows.AddChild(_pickerHeading);
        _pickerSubheading = Title("1 January 2024 · click the political map or search", 13, new Color("#9dabbc"));
        headingRows.AddChild(_pickerSubheading);

        var panel = TexturedPanel(_pickerLayer);
        panel.AnchorLeft = 1;
        panel.AnchorRight = 1;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -420;
        panel.OffsetRight = -18;
        panel.OffsetTop = -338;
        panel.OffsetBottom = 338;
        var rows = PanelRows(panel, 20, 16, 8);

        rows.AddChild(Title("THE WORLD IN 2024", 13, new Color("#8d9caf")));
        _pickerSearch = new LineEdit
        {
            PlaceholderText = "Search countries or tags…",
            CustomMinimumSize = new Vector2(0, 38),
            ClearButtonEnabled = true
        };
        _pickerSearch.TextChanged += _ => RebuildPickerResults();
        rows.AddChild(_pickerSearch);

        _pickerResults = new VBoxContainer { CustomMinimumSize = new Vector2(0, 142) };
        _pickerResults.AddThemeConstantOverride("separation", 4);
        rows.AddChild(_pickerResults);

        _pickerName = Title("South Korea", 27, new Color("#e7c966"));
        rows.AddChild(_pickerName);
        _pickerTier = Title("KOR · major power", 13, new Color("#9eacbc"));
        rows.AddChild(_pickerTier);
        _pickerStats = Title("", 15, new Color("#edf2f8"));
        _pickerStats.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_pickerStats);
        _pickerLocation = Title("Click a region to inspect its tactical province.", 13, new Color("#8d9caf"));
        _pickerLocation.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_pickerLocation);

        _pickerSeats = new VBoxContainer { Visible = false, CustomMinimumSize = new Vector2(0, 58) };
        _pickerSeats.AddThemeConstantOverride("separation", 3);
        rows.AddChild(_pickerSeats);

        _pickerSeatToggle = ActionButton("ADD PLAYER", TogglePickerSeat, 330, 38);
        _pickerSeatToggle.Visible = false;
        rows.AddChild(_pickerSeatToggle);

        _pickerBegin = ActionButton("BEGIN CAMPAIGN", BeginPickedCampaign, 330, 46, true);
        rows.AddChild(_pickerBegin);

        var actions = TexturedPanel(_pickerLayer);
        actions.AnchorLeft = 0;
        actions.AnchorRight = 0;
        actions.AnchorTop = 1;
        actions.AnchorBottom = 1;
        actions.OffsetLeft = 18;
        actions.OffsetRight = 486;
        actions.OffsetTop = -82;
        actions.OffsetBottom = -18;
        var actionMargin = Margin(actions, 10, 8);
        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);
        actionMargin.AddChild(actionRow);
        actionRow.AddChild(ActionButton("Back", ShowMainMenu, 100, 38));
        actionRow.AddChild(ActionButton("Random nation", PickRandomNation, 150, 38));
        var observe = ActionButton("Observe", () => { }, 100, 38);
        observe.Disabled = true;
        observe.TooltipText = "Observer authority will be enabled with the multiplayer/session ownership slice.";
        actionRow.AddChild(observe);
    }

    private void OpenCountryPicker()
    {
        _pickerHotseatMode = false;
        _campaign.IsPaused = true;
        _menuLayer.Visible = false;
        _hudLayer.Visible = false;
        _pickerLayer.Visible = true;
        _sidePanel.Visible = false;
        _pickerSearch.Text = "";
        _pickerHeading.Text = "SELECT A COUNTRY";
        _pickerSubheading.Text = "1 January 2024 · click the political map or search";
        _pickerSeats.Visible = false;
        _pickerSeatToggle.Visible = false;
        SetPickerNation(_pickerNation ?? _campaign.Player, true);
        _pickerSearch.GrabFocus();
    }

    private void SetPickerNation(Nation nation, bool focus)
    {
        if (nation is null) return;
        _pickerNation = nation;
        _selectedNation = nation;

        var rank = 1 + _campaign.Nations.Count(other => other.Gdp > nation.Gdp);
        var tier = rank <= 8 ? "great power" : rank <= 24 ? "major power" : rank <= 80 ? "regional power" : "minor power";
        var states = _campaign.States.Count(state => state.NationMapIndex == nation.MapIndex);
        if (focus && !string.IsNullOrWhiteSpace(nation.CapitalName))
            _globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude,
                states <= 2 ? 6.5f : states <= 5 ? 7.3f : 8.4f);
        var armies = _campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
        var divisions = armies.Sum(army => army.Divisions);
        _pickerName.Text = nation.Name;
        _pickerTier.Text = $"{nation.Tag} · rank #{rank} · {tier}";
        _pickerStats.Text =
            $"Capital  {nation.CapitalName}\n" +
            $"Population  {nation.Population:N0}\n" +
            $"GDP  ${nation.Gdp:0.00}B    Treasury  ${nation.Treasury:0.00}B\n" +
            $"Regions  {states:N0}    Divisions  {divisions:0.#}";
        _pickerLocation.Text = "Click the map to inspect a region and its tactical province.";
        _pickerBegin.Text = _pickerHotseatMode
            ? $"START HOTSEAT · {_campaign.HumanSeats.Count} PLAYERS"
            : $"BEGIN AS {nation.Tag}";
        _pickerBegin.Disabled = _pickerHotseatMode && _campaign.HumanSeats.Count < 2;
        _pickerSeatToggle.Text = _campaign.HumanSeats.Contains(nation.Id)
            ? $"REMOVE {nation.Tag}"
            : $"ADD {nation.Tag} AS PLAYER";
        RefreshPickerSeats();
        RebuildPickerResults();
    }

    private void RebuildPickerResults()
    {
        if (_campaign is null || _pickerResults is null) return;
        foreach (var child in _pickerResults.GetChildren())
        {
            _pickerResults.RemoveChild(child);
            child.QueueFree();
        }

        var query = _pickerSearch?.Text?.Trim() ?? "";
        var matches = _campaign.Nations
            .Where(nation => query.Length == 0 ||
                nation.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                nation.Tag.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(nation => nation.Gdp)
            .Take(4)
            .ToArray();

        foreach (var nation in matches)
        {
            var captured = nation;
            var button = ActionButton($"{nation.Tag}   {nation.Name}", () => SetPickerNation(captured, true), 0, 31,
                _pickerHotseatMode ? _campaign.HumanSeats.Contains(nation.Id) : nation == _pickerNation);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _pickerResults.AddChild(button);
        }
    }

    private void PickRandomNation()
    {
        var eligible = _campaign.Nations
            .Where(nation => _campaign.States.Count(state => state.NationMapIndex == nation.MapIndex) >= 3)
            .ToArray();
        if (eligible.Length == 0) return;
        SetPickerNation(eligible[Random.Shared.Next(eligible.Length)], true);
    }

    private void BeginPickedCampaign()
    {
        if (_pickerNation is null) return;
        if (_pickerHotseatMode)
        {
            if (_campaign.HumanSeats.Count < 2) return;
            _campaign.TakeSeat(0);
        }
        else
        {
            _campaign.ConfigureSinglePlayer(_pickerNation.Id);
        }
        BeginSession();
    }

    private void OpenLocalMultiplayer()
    {
        _pickerHotseatMode = true;
        _campaign.IsPaused = true;
        _menuLayer.Visible = false;
        _hudLayer.Visible = false;
        _pickerLayer.Visible = true;
        _sidePanel.Visible = false;
        _pickerSearch.Text = "";
        _pickerHeading.Text = "LOCAL MULTIPLAYER";
        _pickerSubheading.Text = "Choose 2 or more countries · players share one deterministic world";
        _pickerSeats.Visible = true;
        _pickerSeatToggle.Visible = true;
        SetPickerNation(_pickerNation ?? _campaign.Player, true);
        _pickerSearch.GrabFocus();
    }

    private void TogglePickerSeat()
    {
        if (!_pickerHotseatMode || _pickerNation is null) return;
        if (_campaign.HumanSeats.Contains(_pickerNation.Id))
            _campaign.RemoveHumanSeat(_pickerNation.Id);
        else
            _campaign.AddHumanSeat(_pickerNation.Id);
        SetPickerNation(_pickerNation, false);
    }

    private void RefreshPickerSeats()
    {
        if (_pickerSeats is null) return;
        foreach (var child in _pickerSeats.GetChildren()) child.QueueFree();
        if (!_pickerHotseatMode) return;
        _pickerSeats.AddChild(Title($"SEATED PLAYERS ({_campaign.HumanSeats.Count})", 12, new Color("#8d9caf")));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        _pickerSeats.AddChild(row);
        foreach (var nationId in _campaign.HumanSeats.Take(5))
        {
            var nation = _campaign.NationById(nationId);
            if (nation is null) continue;
            var captured = nation;
            row.AddChild(ActionButton($"× {nation.Tag}", () =>
            {
                _campaign.RemoveHumanSeat(captured.Id);
                SetPickerNation(_pickerNation ?? _campaign.Player, false);
            }, 56, 28));
        }
    }

    private void UpdatePickerLocation(GlobePick pick)
    {
        var region = pick.State is null ? "Unmapped region" : $"{pick.State.Name} · {pick.State.Kind}";
        var province = pick.ProvinceId > 0 ? $"tactical province #{pick.ProvinceId:N0}" : "province topology loading";
        _pickerLocation.Text = $"Selected: {region} · {province}";
    }
}
