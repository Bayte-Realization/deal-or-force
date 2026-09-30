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
    private LineEdit _pickerSeed = null!;
    private Button _pickerSeedApply = null!;
    private Label _pickerSeedNote = null!;

    private GridContainer _pickerMovements;
    private VBoxContainer _pickerMovementsBox;
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

        // The countries without land (Movements): a headquarters instead of a map, so a row of
        // their own rather than something a player has to know to search for. A movement's
        // name sets its own width, so an unchecked flow left the buttons a ragged pile - two
        // wide, three narrow, wrapping wherever they met. Now a two-column grid of equal cells
        // in a scroll, reading like the country list above it; a long name clips and leaves
        // the full text to the tooltip.
        _pickerMovementsBox = new VBoxContainer();
        rows.AddChild(_pickerMovementsBox);
        var withoutLand = new HBoxContainer();
        withoutLand.AddThemeConstantOverride("separation", 6);
        if (Symbol("movement_headquarters", 14, "text.caption") is { } headquarters) withoutLand.AddChild(headquarters);
        var withoutLandCaption = Title("WITHOUT LAND", 13, Ui.Colour("text.caption"));
        withoutLandCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        withoutLand.AddChild(withoutLandCaption);
        _pickerMovementsBox.AddChild(withoutLand);
        var movementScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 62),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _pickerMovementsBox.AddChild(movementScroll);
        _pickerMovements = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pickerMovements.AddThemeConstantOverride("h_separation", 4);
        _pickerMovements.AddThemeConstantOverride("v_separation", 4);
        movementScroll.AddChild(_pickerMovements);

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
        // A MarginContainer gives its one child the whole rectangle, so the rows stack in a
        // box of their own inside it: the buttons, and the seed row underneath them.
        var actionStack = new VBoxContainer();
        actionStack.AddThemeConstantOverride("separation", 8);
        actionMargin.AddChild(actionStack);
        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 8);
        actionStack.AddChild(actionRow);
        actionRow.AddChild(ActionButton("Back", ShowMainMenu, 100, 38));
        actionRow.AddChild(ActionButton("Random nation", PickRandomNation, 150, 38));
        actionRow.AddChild(ActionButton("Scenarios / editor", OpenScenarioLibrary, 170, 38));
        // The language, the step length and the sound are all decisions a player makes
        // BEFORE they pick a country, and until now the only door to them was the pause
        // menu, which does not exist until a campaign is running.
        actionRow.AddChild(ActionButton("Settings", ShowSettings, 120, 38));

        // The seed the next world is built from. The same seed builds the same world to the
        // digit - which is what a bug report is made of - and a different seed builds a
        // different one out of the same scenario. Changing it asks for a rebuild rather
        // than doing one behind the field: a world is not a small thing to replace.
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        actionStack.AddChild(seedRow);
        var seedCaption = Title("WORLD SEED", 12, Ui.Colour("text.caption"));
        seedCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        seedCaption.CustomMinimumSize = new Vector2(92, 0);
        seedRow.AddChild(seedCaption);
        _pickerSeed = new LineEdit { CustomMinimumSize = new Vector2(150, 34) };
        _pickerSeed.MaxLength = 10;
        _pickerSeed.Text = _campaignSeed.ToString();
        Placeholder(_pickerSeed, "Any number");
        _pickerSeed.TextChanged += _ => OnPickerSeedChanged();
        _pickerSeed.TextSubmitted += _ => ApplyPickerSeed();
        seedRow.AddChild(_pickerSeed);
        seedRow.AddChild(ActionButton("Random seed", RollPickerSeed, 128, 34));
        _pickerSeedApply = ActionButton("Rebuild the world", ApplyPickerSeed, 160, 34, true);
        _pickerSeedApply.Visible = false;
        seedRow.AddChild(_pickerSeedApply);
        _pickerSeedNote = Title("", 11, Ui.Colour("text.caption"));
        _pickerSeedNote.AutowrapMode = TextServer.AutowrapMode.Off;
        _pickerSeedNote.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pickerSeedNote.ClipText = true;
        seedRow.AddChild(_pickerSeedNote);
        RefreshPickerSeedNote();
    }

    /// <summary>What the seed field says, if it says a number at all.</summary>
    private bool PickerSeedParsed(out int seed)
    {
        return CampaignSeedSelection.TryParse(_pickerSeed.Text, out seed);
    }

    private void OnPickerSeedChanged()
    {
        RefreshPickerSeedNote();
    }

    private void RefreshPickerSeedNote()
    {
        if (_campaign is null) return;
        if (!PickerSeedParsed(out var seed))
        {
            _pickerSeedApply.Visible = false;
            _pickerSeedNote.Text = Tr("A seed is any whole number.");
            return;
        }
        var stale = seed != _campaign.Seed;
        _pickerSeedApply.Visible = stale;
        // Nothing to explain when the field already names the world on screen: the note
        // exists for the moment the two stop agreeing, which is when a decision is needed.
        _pickerSeedNote.Text = stale ? Tr("A new seed builds a new world.") : "";
    }

    private void RollPickerSeed()
    {
        // Not the campaign's own generator: that one exists to make worlds reproducible,
        // and a dice that rolled the same world every time would be a strange dice.
        var rolled = CampaignSeedSelection.Roll();
        _pickerSeed.Text = rolled.ToString();
        RefreshPickerSeedNote();
    }

    private async void ApplyPickerSeed()
    {
        if (_isLoading || !PickerSeedParsed(out var seed) || seed == _campaign.Seed) return;
        var previousSeed = _campaignSeed;
        _campaignSeed = seed;
        var rebuilt = _pickerScenarioEntry is null
            ? await RebuildCampaignWithLoading()
            : await RebuildAuthoredScenarioWithLoading(_pickerScenarioEntry);
        if (!rebuilt)
        {
            _campaignSeed = _campaign.Seed == seed ? seed : previousSeed;
            RefreshPickerSeedNote();
            return;
        }
        // The old world's countries are gone with it, and the one the player had highlighted
        // was one of them.
        _pickerNation = null;
        _pickerSubheading.Text = $"{_campaign.Date.Display()} · {Tr("Click the map or search")}";
        SetPickerNation(_campaign.Player, true);
        RebuildPickerResults();
        RefreshPickerSeedNote();
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
        RefreshPickerSeedNote();
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
        if (nation.Landless) tier = Tr("movement without land");
        if (focus && !string.IsNullOrWhiteSpace(nation.CapitalName))
            _globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude,
                states <= 2 ? 6.5f : states <= 5 ? 7.3f : 8.4f);
        var armies = _campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
        var divisions = armies.Sum(army => army.Divisions);
        _pickerName.Text = nation.DisplayName;
        SetFlag(_pickerFlag, nation.Tag, 32);
        _pickerTier.Text = Simulation.Loc.Format("{0} · rank #{1} · {2}", nation.Tag, rank, tier);
        _pickerStats.Text = nation.Landless
            ? Simulation.Loc.Format("Headquarters  {0}, {1}\nCadres  {2}    Standing  {3}\nTreasury  {4}\nNo land: it campaigns, raises money and rises up to take some.",
                MovementRules.RegionName(_campaign, nation.Movement.HeadquartersStateMapIndex),
                Movements.HostOf(_campaign, nation)?.DisplayName ?? "", Fmt.People(nation.Movement.Cadres),
                Fmt.Percent(nation.Movement.Legitimacy), Fmt.Money(nation.Treasury))
            : Simulation.Loc.Format("Capital  {0}\nPopulation  {1}\nGDP  {2}    Treasury  {3}\nRegions  {4:N0}    Divisions  {5:0.#}", CapitalName(nation), Fmt.People(nation.Population), Fmt.Money(nation.Gdp), Fmt.Money(nation.Treasury), states, divisions);
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
            .Where(nation => _campaign.StatesOf(nation).Length > 0 || PlayableMovement(nation))
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

        if (_pickerMovements is null) return;
        foreach (var child in _pickerMovements.GetChildren())
        {
            _pickerMovements.RemoveChild(child);
            child.QueueFree();
        }
        // Alphabetical, so a movement stands where a reader looks for it rather than where the
        // nation list happened to load it.
        var movements = _campaign.Nations.Where(PlayableMovement).OrderBy(movement => movement.DisplayName).ToArray();
        _pickerMovementsBox.Visible = movements.Length > 0;
        foreach (var movement in movements)
        {
            var captured = movement;
            var button = ActionButton(movement.DisplayName, () => SetPickerNation(captured, true), 0, 30,
                movement == _pickerNation);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.ClipText = true;
            // Its own flag on the button, the way a country in the list is known by its flag.
            // Read now rather than left to the background loader: the list is built the moment
            // the picker opens, before the loader has reached six flags nobody has asked for yet.
            var flag = $"flag_{movement.Tag.ToLowerInvariant()}";
            var flagPath = SpriteCatalog.TextureOf(flag, $"res://game/gfx/flags/{flag}.png");
            button.Icon = FlagTexture(movement.Tag) ??
                (ResourceLoader.Exists(flagPath) ? GD.Load<Texture2D>(flagPath) : null);
            // Not expanded: an expanded icon adds nothing to the button's minimum width, and a
            // button in a flowing row is given exactly its minimum - the flag got no room at all.
            // The width cap scales it down to a small flag instead, and counts in the minimum.
            button.ExpandIcon = false;
            button.AddThemeConstantOverride("icon_max_width", 24);
            button.TooltipText = Ui.Tip(Simulation.Loc.Get(Movements.DefinitionOf(movement)?.Description ?? ""));
            _pickerMovements.AddChild(button);
        }
    }

    /// <summary>A movement without land that its definition lets a player be.</summary>
    private static bool PlayableMovement(Nation nation) =>
        nation.Landless && !nation.Movement.Dissolved && Movements.DefinitionOf(nation)?.Playable == true;

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
