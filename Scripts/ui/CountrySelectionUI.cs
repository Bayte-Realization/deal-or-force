using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
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
    private Control _pickerFlag = null!;
    private Nation _pickerNation;
    private bool _pickerHotseatMode;

    private void BuildCountryPicker()
    {
        _pickerLayer = FullLayer("CountryPicker");
        _pickerLayer.Visible = false;
        AddChild(_pickerLayer);

        var heading = TexturedPanel(_pickerLayer);
        Ui.Place(heading, "picker.heading");
        var headingRows = PanelRows(heading, 18, 10, 2);
        _pickerHeading = Title("SELECT A COUNTRY", 26, Ui.Colour("text.accent"));
        headingRows.AddChild(_pickerHeading);
        _pickerSubheading = Title("1 January 2024 · click the political map or search", 13, Ui.Colour("text.muted"));
        headingRows.AddChild(_pickerSubheading);

        var panel = TexturedPanel(_pickerLayer);
        Ui.Place(panel, "picker.country");
        var rows = PanelRows(panel, 20, 16, 8);

        rows.AddChild(Title("COUNTRIES", 13, Ui.Colour("text.caption")));
        _pickerSearch = new LineEdit
        {
            CustomMinimumSize = new Vector2(0, 38),
            ClearButtonEnabled = true
        };
        Placeholder(_pickerSearch, "Search countries or tags…");
        _pickerSearch.TextChanged += _ => RebuildPickerResults();
        rows.AddChild(_pickerSearch);

        _pickerResults = new VBoxContainer { CustomMinimumSize = new Vector2(0, 142) };
        _pickerResults.AddThemeConstantOverride("separation", 4);
        rows.AddChild(_pickerResults);

        var nameRow = new HBoxContainer();
        nameRow.AddThemeConstantOverride("separation", 10);
        rows.AddChild(nameRow);
        _pickerFlag = FlagBadge("KOR", 32);
        nameRow.AddChild(_pickerFlag);
        _pickerName = Title("South Korea", 27, Ui.Colour("text.accent"));
        _pickerName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameRow.AddChild(_pickerName);
        _pickerTier = Title("KOR · major power", 13, Ui.Colour("text.muted"));
        rows.AddChild(_pickerTier);
        _pickerStats = Title("", 15, Ui.Colour("text.bright"));
        _pickerStats.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_pickerStats);
        _pickerLocation = Title("Click a region to inspect its tactical province.", 13, Ui.Colour("text.caption"));
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
        Ui.Place(actions, "picker.actions");
        var actionMargin = Margin(actions, 10, 8);
        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);
        actionMargin.AddChild(actionRow);
        actionRow.AddChild(ActionButton("Back", ShowMainMenu, 100, 38));
        actionRow.AddChild(ActionButton("Random nation", PickRandomNation, 150, 38));
        actionRow.AddChild(ActionButton("Scenarios / editor", OpenScenarioLibrary, 170, 38));
        // The language, the step length and the sound are all decisions a player makes
        // BEFORE they pick a country, and until now the only door to them was the pause
        // menu, which does not exist until a campaign is running.
        actionRow.AddChild(ActionButton("Settings", ShowSettings, 120, 38));
    }

    private async void OpenCountryPicker()
    {
        // A new game is a new world.
        //
        // Returning to the main menu only hid the campaign, so picking a country again
        // dropped the player into the one they had just left - same date, same armies, same
        // war - which is not what "Singleplayer" says on the button. The world is rebuilt
        // here rather than when the menu opens, so looking at the menu costs nothing and
        // only actually starting a game throws the old campaign away.
        if (_isLoading) return;
        if (_campaignInProgress && !await RebuildCampaignWithLoading()) return;

        _pickerHotseatMode = false;
        _campaign.IsPaused = true;
        _menuLayer.Visible = false;
        _hudLayer.Visible = false;
        _pickerLayer.Visible = true;
        _sidePanel.Visible = false;
        _pickerSearch.Text = Tr("");
        _pickerHeading.Text = Tr("SELECT A COUNTRY");
        // Display, not ToString: the bare date is the invariant one that goes into save
        // files, so interpolating it here put an English month beside a translated
        // sentence on the first screen of every campaign.
        _pickerSubheading.Text = $"{_campaign.Date.Display()} · {Tr("Click the map or search")}";
        _pickerSeats.Visible = false;
        _pickerSeatToggle.Visible = false;
        SetPickerNation(_pickerNation ?? _campaign.Player, true);
        _pickerSearch.GrabFocus();
    }

    /// <summary>
    /// A capital's name in the player's language.
    ///
    /// Asked for under "capital.Kyiv" before "Kyiv", because a capital and a region can be
    /// the same word and the table is flat: Kyiv is a city and an oblast, and the oblast's
    /// file is read later, so the city inherited "Kyiv Oblast". Everywhere the two agree -
    /// which is everywhere else - the plain name answers and nothing extra is needed.
    /// </summary>
    private static string CapitalName(Simulation.Nation nation) =>
        nation is null || string.IsNullOrWhiteSpace(nation.CapitalName)
            ? ""
            : Simulation.Loc.GetOr("capital." + nation.CapitalName, Simulation.Loc.Get(nation.CapitalName));

    private void SetPickerNation(Nation nation, bool focus)
    {
        if (nation is null) return;
        _pickerNation = nation;
        _selectedNation = nation;

        var rank = 1 + _campaign.Nations.Count(other => other.Gdp > nation.Gdp);
        var tier = Tr(rank <= 8 ? "great power"
            : rank <= 24 ? "major power"
            : rank <= 80 ? "regional power"
            : "minor power");
        var states = _campaign.States.Count(state => state.NationMapIndex == nation.MapIndex);
        if (focus && !string.IsNullOrWhiteSpace(nation.CapitalName))
            _globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude,
                states <= 2 ? 6.5f : states <= 5 ? 7.3f : 8.4f);
        var armies = _campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
        var divisions = armies.Sum(army => army.Divisions);
        _pickerName.Text = nation.DisplayName;
        SetFlag(_pickerFlag, nation.Tag, 32);
        _pickerTier.Text = Simulation.Loc.Format("{0} · rank #{1} · {2}", nation.Tag, rank, tier);
        _pickerStats.Text =
            Simulation.Loc.Format("Capital  {0}\nPopulation  {1}\nGDP  {2}    Treasury  {3}\nRegions  {4:N0}    Divisions  {5:0.#}", CapitalName(nation), Fmt.People(nation.Population), Fmt.Money(nation.Gdp), Fmt.Money(nation.Treasury), states, divisions);
        _pickerLocation.Text = Tr("Click the map to inspect a region and its tactical province.");
        _pickerBegin.Text = _pickerHotseatMode
            ? Simulation.Loc.Format("START HOTSEAT · {0} PLAYERS", _campaign.HumanSeats.Count)
            : Simulation.Loc.Format("BEGIN AS {0}", nation.Tag);
        _pickerBegin.Disabled = _pickerHotseatMode && _campaign.HumanSeats.Count < 2;
        _pickerSeatToggle.Text = _campaign.HumanSeats.Contains(nation.Id)
            ? Simulation.Loc.Format("REMOVE {0}", nation.Tag)
            : Simulation.Loc.Format("ADD {0} AS PLAYER", nation.Tag);
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
        // A country with no land is not one you can play: Antarctica's, once its ice
        // belongs to nobody, or one a war has already swallowed.
        var matches = _campaign.Nations
            .Where(nation => _campaign.StatesOf(nation).Length > 0)
            .Where(nation => query.Length == 0 ||
                nation.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                nation.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                nation.Tag.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(nation => nation.Gdp)
            .Take(4)
            .ToArray();

        foreach (var nation in matches)
        {
            var captured = nation;
            var button = ActionButton($"{nation.Tag}   {nation.DisplayName}", () => SetPickerNation(captured, true), 0, 31,
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
        _campaignInProgress = true;
        BeginSession();
    }

    private async void OpenLocalMultiplayer()
    {
        if (_isLoading) return;
        if (_campaignInProgress && !await RebuildCampaignWithLoading()) return;
        _pickerHotseatMode = true;
        _campaign.IsPaused = true;
        _menuLayer.Visible = false;
        _hudLayer.Visible = false;
        _pickerLayer.Visible = true;
        _sidePanel.Visible = false;
        _pickerSearch.Text = Tr("");
        _pickerHeading.Text = Tr("LOCAL MULTIPLAYER");
        _pickerSubheading.Text = Tr("Choose 2 or more countries · players share one deterministic world");
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
        _pickerSeats.AddChild(Title($"SEATED PLAYERS ({_campaign.HumanSeats.Count})", 12, Ui.Colour("text.caption")));
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
        var region = pick.State is null
            ? Simulation.Loc.Get("Unmapped region")
            : pick.State.DisplayName + " · " + Simulation.Loc.Get(pick.State.Kind);
        var province = pick.ProvinceId > 0 ? $"tactical province #{pick.ProvinceId:N0}" : "province topology loading";
        _pickerLocation.Text = Simulation.Loc.Format("Selected: {0} · {1}", region, province);
    }
}
