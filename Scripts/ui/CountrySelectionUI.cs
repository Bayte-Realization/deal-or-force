using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _pickerLayer;
    private PanelContainer _pickerHeadingPanel;
    private PanelContainer _pickerCountryPanel;
    private CountryPickerDossier _pickerDossier;
    private LineEdit _pickerSearch = null!;
    private VBoxContainer _pickerResults = null!;
    private Label _pickerName = null!;
    private Label _pickerTier = null!;
    private Label _pickerLocation = null!;
    private Button _pickerBegin = null!;
    private Button _pickerSeatToggle = null!;
    private Label _pickerHeading = null!;
    private Label _pickerSubheading = null!;
    private VBoxContainer _pickerSeats = null!;
    private VBoxContainer _pickerLeaderBox;
    private Nation _pickerNation;
    private bool _pickerHotseatMode;
    private LineEdit _pickerSeed = null!;
    private Button _pickerSeedApply = null!;
    private Label _pickerSeedNote = null!;
    private PanelContainer _pickerActionsPanel;
    private VBoxContainer _pickerActionsStack;
    private bool _pickerActionsLayoutQueued;

    private GridContainer _pickerMovements;
    private VBoxContainer _pickerMovementsBox;
    private void BuildCountryPicker()
    {
        _pickerLayer = FullLayer("CountryPicker");
        _pickerLayer.Visible = false;
        _pickerLayer.Resized += QueuePickerActionsLayout;
        AddChild(_pickerLayer);

        var heading = TexturedPanel(_pickerLayer);
        Ui.Place(heading, "picker.heading");
        MenuChromeTheme.StyleArchitecturalPlate(heading, brassCorners: true, padX: 0, padY: 0);
        ChromePlateArt.AttachHeaderFiligree(heading, 0.36f);
        _pickerHeadingPanel = heading;
        var headingRows = PanelRows(heading, 18, 10, 3);
        _pickerHeading = Title("SELECT A COUNTRY", 24, MenuChromeTheme.Paper);
        MenuChromeTheme.StyleExistingLabel(_pickerHeading, 24, MenuChromeTheme.Paper, serif: true);
        _pickerHeading.AutowrapMode = TextServer.AutowrapMode.Off;
        _pickerHeading.ClipText = false;
        _pickerHeading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        headingRows.AddChild(_pickerHeading);
        _pickerSubheading = Title("1 January 2024 · click the political map or search", 12, MenuChromeTheme.PaperDim);
        MenuChromeTheme.StyleExistingLabel(_pickerSubheading, 12, MenuChromeTheme.PaperDim);
        _pickerSubheading.AutowrapMode = TextServer.AutowrapMode.Off;
        _pickerSubheading.ClipText = false;
        _pickerSubheading.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _pickerSubheading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        headingRows.AddChild(_pickerSubheading);

        var panel = TexturedPanel(_pickerLayer);
        Ui.Place(panel, "picker.country");
        MenuChromeTheme.StyleArchitecturalPlate(panel, brassCorners: true, padX: 0, padY: 0);
        ChromePlateArt.AttachSurfaceTexture(panel, "slate");
        _pickerCountryPanel = panel;
        var rows = PanelRows(panel, 18, 14, 7);

        var listCaption = Title("COUNTRIES", 11, MenuChromeTheme.PaperFaint);
        rows.AddChild(listCaption);
        _pickerSearch = new LineEdit
        {
            CustomMinimumSize = new Vector2(0, 36),
            ClearButtonEnabled = true
        };
        Placeholder(_pickerSearch, "Search countries or tags…");
        MenuChromeTheme.StyleLineEdit(_pickerSearch);
        _pickerSearch.TextChanged += _ => RebuildPickerResults();
        rows.AddChild(_pickerSearch);

        var resultsFrame = new PanelContainer
        {
            Name = "PickerResultsFrame",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 142)
        };
        resultsFrame.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(6, 6));
        rows.AddChild(resultsFrame);
        _pickerResults = new VBoxContainer { CustomMinimumSize = new Vector2(0, 128), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pickerResults.AddThemeConstantOverride("separation", 4);
        resultsFrame.AddChild(_pickerResults);

        // The countries without land (Movements): a headquarters instead of a map, so a row of
        // their own rather than something a player has to know to search for. A movement's
        // name sets its own width, so an unchecked flow left the buttons a ragged pile - two
        // wide, three narrow, wrapping wherever they met. Now a two-column grid of equal cells
        // in a scroll, reading like the country list above it; a long name clips and leaves
        // the full text to the tooltip.
        //
        // Folded away until asked for. Open, it put "Alaska", "Ambazonia" and "Bavaria Party"
        // between the search box and the country a newcomer had just clicked, and nothing on
        // the screen said what they were. One row now says it - a movement is something you
        // can play - and the list is a press away for whoever wants it.
        _pickerMovementsBox = new VBoxContainer();
        _pickerMovementsBox.AddThemeConstantOverride("separation", 4);
        rows.AddChild(_pickerMovementsBox);
        PanelContainer movementWell = null;
        Button movementFold = null;
        movementFold = ActionButton("Play a movement without land", () =>
        {
            movementWell.Visible = !movementWell.Visible;
            MenuChromeTheme.StylePlateButton(movementFold, movementWell.Visible, 12);
        }, 330, 32);
        movementFold.Name = "PickerMovementsFold";
        movementFold.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        movementFold.ClipText = true;
        movementFold.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        MenuChromeTheme.StylePlateButton(movementFold, false, 12);
        movementFold.TooltipText = Tr("Parties, fronts and governments in exile: a headquarters and supporters instead of provinces.");
        _pickerMovementsBox.AddChild(movementFold);

        movementWell = new PanelContainer
        {
            Visible = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        movementWell.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(6, 6));
        _pickerMovementsBox.AddChild(movementWell);

        var movementScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 88),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        movementWell.AddChild(movementScroll);
        _pickerMovements = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _pickerMovements.AddThemeConstantOverride("h_separation", 4);
        _pickerMovements.AddThemeConstantOverride("v_separation", 4);
        movementScroll.AddChild(_pickerMovements);

        rows.AddChild(MenuChromeTheme.CreateEngravedRule("PickerListDivider", 1f));

        var dossierScroll = new ScrollContainer
        {
            Name = "PickerDossierScroll",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        rows.AddChild(dossierScroll);

        _pickerDossier = new CountryPickerDossier();
        _pickerName = _pickerDossier.NameLabel;
        _pickerTier = _pickerDossier.TierLabel;
        _pickerLeaderBox = _pickerDossier.LeaderBox;
        _pickerLocation = _pickerDossier.LocationLabel;
        dossierScroll.AddChild(_pickerDossier.Root);

        rows.AddChild(MenuChromeTheme.CreateEngravedRule("PickerBeginDivider", 1f));

        _pickerSeats = new VBoxContainer { Visible = false, CustomMinimumSize = new Vector2(0, 52) };
        _pickerSeats.AddThemeConstantOverride("separation", 3);
        rows.AddChild(_pickerSeats);

        _pickerSeatToggle = ActionButton("ADD PLAYER", TogglePickerSeat, 330, 34);
        _pickerSeatToggle.Visible = false;
        MenuChromeTheme.StylePlateButton(_pickerSeatToggle, false, 13);
        rows.AddChild(_pickerSeatToggle);

        _pickerBegin = ActionButton("BEGIN CAMPAIGN", BeginPickedCampaign, 330, 44, true);
        _pickerBegin.Name = "PickerBeginButton";
        MenuChromeTheme.StylePrimaryCta(_pickerBegin, 16, serif: true);
        rows.AddChild(_pickerBegin);

        var actions = TexturedPanel(_pickerLayer);
        actions.Name = "CountryPickerActions"; _pickerActionsPanel = actions;
        Ui.Place(actions, "picker.actions");
        MenuChromeTheme.StyleArchitecturalPlate(actions, brassCorners: true, padX: 0, padY: 0);
        var actionMargin = Margin(actions, 10, 8);
        // A MarginContainer gives its one child the whole rectangle, so the rows stack in a
        // box of their own inside it: the buttons, and the seed row underneath them.
        var actionStack = new VBoxContainer();
        _pickerActionsStack = actionStack;
        actionStack.MinimumSizeChanged += QueuePickerActionsLayout;
        actions.Resized += QueuePickerActionsLayout;
        actionStack.AddThemeConstantOverride("separation", 8);
        actionMargin.AddChild(actionStack);
        var actionRow = new HFlowContainer { Name = "PickerActionsFlow", ClipContents = true };
        actionRow.AddThemeConstantOverride("h_separation", 8);
        actionRow.AddThemeConstantOverride("v_separation", 6);
        actionStack.AddChild(actionRow);
        actionRow.AddChild(PickerActionChip("Back", ShowMainMenu, 94));
        actionRow.AddChild(PickerActionChip("Random nation", PickRandomNation, 142));
        // Watching without a country: players asked for it, and before it the only way in was
        // to start as somebody and press F7, which still left that country the player's.
        var watch = PickerActionChip("Watch the world", BeginObserving, 164);
        watch.TooltipText = Ui.Tip(Tr("Start without a country: the computer runs every nation and nothing stops the clock. F7 shows the controls."));
        actionRow.AddChild(watch);
        actionRow.AddChild(PickerActionChip("Scenarios / editor", OpenScenarioLibrary, 164));
        // The language, the step length and the sound are all decisions a player makes
        // BEFORE they pick a country, and until now the only door to them was the pause
        // menu, which does not exist until a campaign is running.
        actionRow.AddChild(PickerActionChip("Settings", ShowSettings, 114));
        // Everything below is for the player who came back for it: the two weather switches and
        // the seed of the world. Shown by default they made the first screen of a first game
        // ask about clouds, fog and a ten-digit number before it asked which country.
        var advanced = new VBoxContainer { Name = "PickerAdvanced", Visible = false };
        advanced.AddThemeConstantOverride("separation", 8);
        Button advancedFold = null;
        advancedFold = ActionButton("Advanced", () =>
        {
            advanced.Visible = !advanced.Visible;
            MenuChromeTheme.StylePlateButton(advancedFold, advanced.Visible, 13);
            QueuePickerActionsLayout();
        }, 114, 36);
        advancedFold.Name = "PickerAdvancedFold";
        MenuChromeTheme.StylePlateButton(advancedFold, false, 13);
        advancedFold.TooltipText = Tr("Clouds, mist and the seed the world is built from.");
        actionRow.AddChild(advancedFold);
        actionStack.AddChild(advanced);
        var weatherRow = new HBoxContainer();
        weatherRow.AddThemeConstantOverride("separation", 8);
        advanced.AddChild(weatherRow);
        // The globe here is a map being studied for its borders, and clouds are in the way of it.
        var pickerClouds = PickerWeatherToggle("map_clouds", "Clouds", "Clouds over the world. Press to hide or show them - the same option as in the settings.", 96);
        weatherRow.AddChild(pickerClouds);
        var pickerFog = PickerWeatherToggle("map_mist", "FOG", "Hide or show the visual mist. Unseen provinces remain unseen.", 70);
        pickerFog.Name = "PickerFog";
        weatherRow.AddChild(pickerFog);

        // The seed the next world is built from. The same seed builds the same world to the
        // digit - which is what a bug report is made of - and a different seed builds a
        // different one out of the same scenario. Changing it asks for a rebuild rather
        // than doing one behind the field: a world is not a small thing to replace.
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        advanced.AddChild(seedRow);
        var seedCaption = Title("WORLD SEED", 11, MenuChromeTheme.PaperFaint);
        seedCaption.AutowrapMode = TextServer.AutowrapMode.Off;
        seedCaption.CustomMinimumSize = new Vector2(92, 0);
        seedRow.AddChild(seedCaption);
        _pickerSeed = new LineEdit { Name = "WorldSeedInput", CustomMinimumSize = new Vector2(146, 34) };
        _pickerSeed.MaxLength = 10;
        _pickerSeed.Text = _campaignSeed.ToString();
        Placeholder(_pickerSeed, "Any number");
        MenuChromeTheme.StyleLineEdit(_pickerSeed);
        _pickerSeed.TextChanged += _ => OnPickerSeedChanged();
        _pickerSeed.TextSubmitted += _ => ApplyPickerSeed();
        seedRow.AddChild(_pickerSeed);
        var randomSeed = ActionButton("Random seed", RollPickerSeed, 126, 34);
        MenuChromeTheme.StyleWellButton(randomSeed, false, 13);
        seedRow.AddChild(randomSeed);
        _pickerSeedApply = ActionButton("Rebuild the world", ApplyPickerSeed, 156, 34, true);
        MenuChromeTheme.StylePrimaryCta(_pickerSeedApply, 13, serif: false);
        _pickerSeedApply.Visible = false;
        seedRow.AddChild(_pickerSeedApply);
        _pickerSeedNote = Title("", 11, MenuChromeTheme.BrassHi);
        _pickerSeedNote.AutowrapMode = TextServer.AutowrapMode.Off;
        _pickerSeedNote.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pickerSeedNote.ClipText = true;
        seedRow.AddChild(_pickerSeedNote);
        RefreshPickerSeedNote();
        QueuePickerActionsLayout();
    }

    private Button PickerActionChip(string text, Action onPressed, float width)
    {
        var button = ActionButton(text, onPressed, width, 36);
        MenuChromeTheme.StyleWellButton(button, selected: false, fontSize: 13);
        return button;
    }

    private Button PickerWeatherToggle(string setting, string caption, string help, float width)
    {
        var ownCaption = "map.toggle." + caption;
        var labelKey = Simulation.Loc.Has(ownCaption) ? ownCaption : caption;
        Button button = null!;
        button = ActionButton(labelKey, () =>
        {
            var next = !SettingsCatalog.Flag(setting);
            SettingsCatalog.Store(setting, next);
            ApplySetting(setting);
            RefreshMapVisualToggles();
            button.SetPressedNoSignal(next);
            MenuChromeTheme.StylePlateButton(button, next, 13);
        }, MathF.Max(width, 86f), 34);
        button.ToggleMode = true;
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        Tip(button, help);
        button.AccessibilityName = Tr(caption);

        void Sync()
        {
            if (!IsInstanceValid(button)) return;
            var on = SettingsCatalog.Flag(setting);
            button.SetPressedNoSignal(on);
            MenuChromeTheme.StylePlateButton(button, on, 13);
        }

        _pickerLayer.VisibilityChanged += Sync;
        Sync();
        return button;
    }

    private Texture2D PickerFlagTexture(string tag)
    {
        var flag = $"flag_{tag.ToLowerInvariant()}";
        var flagPath = SpriteCatalog.TextureOf(flag, $"res://game/gfx/flags/{flag}.png");
        return FlagTexture(tag) ?? (ResourceLoader.Exists(flagPath) ? GD.Load<Texture2D>(flagPath) : null);
    }

    private void QueuePickerActionsLayout()
    {
        if (_pickerActionsLayoutQueued) return;
        _pickerActionsLayoutQueued = true;
        Callable.From(() => { _pickerActionsLayoutQueued = false; LayoutPickerActions(); }).CallDeferred();
    }

    private void LayoutPickerActions()
    {
        if (_pickerActionsPanel is null || _pickerActionsStack is null) return;
        var view = Size.X > 100 && Size.Y > 100 ? Size : GetViewportRect().Size;

        if (_pickerCountryPanel is not null)
        {
            var panelWidth = Mathf.Clamp(view.X * 0.28f, 396f, 434f);
            var panelHeight = Mathf.Min(676f, view.Y - 36f);
            var right = view.X - 18f;
            var leftCountry = right - panelWidth;
            var topCountry = Mathf.Max(18f, (view.Y - panelHeight) * 0.5f);
            PlaceHudBounds(_pickerCountryPanel, new(leftCountry, topCountry, panelWidth, panelHeight));
        }

        if (_pickerHeadingPanel is not null)
        {
            var headingMaxRight = _pickerCountryPanel is not null ? _pickerCountryPanel.OffsetLeft - 18f : view.X - 440f;
            var headingWidth = Mathf.Clamp(437f, 320f, Mathf.Max(320f, headingMaxRight - 18f));
            PlaceHudBounds(_pickerHeadingPanel, new(18f, 18f, headingWidth, 88f));
        }

        var authored = InterfaceSkin.Layout("picker.actions");
        var left = authored.AnchorLeft * view.X + authored.OffsetLeft;
        var rightEdge = authored.AnchorRight * view.X + authored.OffsetRight;
        if (_pickerCountryPanel is not null)
            rightEdge = Mathf.Min(rightEdge, _pickerCountryPanel.OffsetLeft - 18f);
        var bottom = Mathf.Min(authored.AnchorBottom * view.Y + authored.OffsetBottom,
            view.Y - Mathf.Clamp(Ui.Metric("picker.actions_bottom_inset", 36), 18, 120));
        var height = Mathf.Max(92, Mathf.Max(_pickerActionsPanel.GetCombinedMinimumSize().Y,
            _pickerActionsStack.GetCombinedMinimumSize().Y + 16));
        var bounds = ResponsiveHudLayout.Fit(new(left, bottom - height, Mathf.Max(180, rightEdge - left), height), view.X, view.Y);
        PlaceHudBounds(_pickerActionsPanel, bounds);
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
        _campaignSeedChosen = true;
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
        // Built on first open: the picker is 2 980 ms - the single most expensive thing in the
        // interface pass - and it is only ever seen after a click on Singleplayer. See
        // EnsureScreen.
        EnsureScreen("picker", BuildCountryPicker);
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
    private string CapitalName(Simulation.Nation nation) => CountryNames.Capital(nation, _campaign);

    private void SetPickerNation(Nation nation, bool focus)
    {
        if (nation is null) return;
        _pickerNation = nation;
        _selectedNation = nation;
        _globe.ClearSelection();
        _globe.HighlightNation(nation.MapIndex);

        var rank = 1 + _campaign.Nations.Count(other => other.Gdp > nation.Gdp);
        var tier = Tr(rank <= 8 ? "great power"
            : rank <= 24 ? "major power"
            : rank <= 80 ? "regional power"
            : "minor power");
        var states = _campaign.States.Count(state => state.NationMapIndex == nation.MapIndex);
        if (nation.Landless) tier = Tr("movement without land");
        if (focus && CapitalCore.HasSeat(_campaign, nation))
            _globe.FocusOn(CapitalCore.EffectiveLatitude(_campaign, nation), CapitalCore.EffectiveLongitude(_campaign, nation),
                states <= 2 ? 6.5f : states <= 5 ? 7.3f : 8.4f);
        var armies = _campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
        var divisions = armies.Sum(army => army.Divisions);
        var tierLine = Simulation.Loc.Format("№ {0} · {1} · {2}", rank, nation.Tag, tier);
        _pickerDossier?.SetNationHeader(nation.DisplayName, tierLine, nation.Tag, PickerFlagTexture(nation.Tag));
        if (_pickerLeaderBox is not null)
        {
            ClearRuntimeChildren(_pickerLeaderBox);
            var leaderHeader = BuildGrandStrategyLeaderHeader(nation, compact: true);
            CountryPickerDossier.WarmSubtree(leaderHeader);
            _pickerLeaderBox.AddChild(leaderHeader);
        }

        if (_pickerDossier is not null)
        {
            if (nation.Landless)
            {
                var hqRegion = MovementRules.RegionName(_campaign, nation.Movement.HeadquartersStateMapIndex);
                var hostName = Movements.HostOf(_campaign, nation)?.DisplayName ?? "—";
                var chips = new (string, string)[]
                {
                    (Tr("Headquarters"), $"{hqRegion}, {hostName}"),
                    (Tr("Cadres"), Fmt.People(nation.Movement.Cadres)),
                    (Tr("Standing"), Fmt.Percent(nation.Movement.Legitimacy)),
                    (Tr("Treasury"), Fmt.Money(nation.Treasury))
                };
                var note = Tr("No land: it campaigns, raises money and rises up to take some.");
                _pickerDossier.SetStats(chips, note);
            }
            else
            {
                var chips = new (string, string)[]
                {
                    (Tr("Capital"), CapitalName(nation)),
                    (Tr("Population"), Fmt.People(nation.Population)),
                    (Tr("GDP"), Fmt.Money(nation.Gdp)),
                    (Tr("Treasury"), Fmt.Money(nation.Treasury)),
                    (Tr("Regions"), states.ToString("N0")),
                    (Tr("Divisions"), divisions.ToString("0.#"))
                };
                var identityBrief = _campaign.FeatureEnabled("state_identity")
                    ? Simulation.StateIdentity.Brief(_campaign, nation)
                    : "";
                _pickerDossier.SetStats(chips, identityBrief);
            }
        }
        _pickerLocation.Text = Tr("Click a country on the map, or search for it by name.");
        _pickerBegin.Text = _pickerHotseatMode
            ? Simulation.Loc.Format("START HOTSEAT · {0} PLAYERS", _campaign.HumanSeats.Count)
            : Simulation.Loc.Format("BEGIN AS {0}", nation.Tag);
        _pickerBegin.Disabled = _pickerHotseatMode && _campaign.HumanSeats.Count < 2;
        _pickerSeatToggle.Text = _campaign.HumanSeats.Contains(nation.Id)
            ? Simulation.Loc.Format("REMOVE {0}", nation.Tag)
            : Simulation.Loc.Format("ADD {0} AS PLAYER", nation.Tag);
        MenuChromeTheme.StylePlateButton(_pickerSeatToggle, _campaign.HumanSeats.Contains(nation.Id), 13);
        RefreshPickerSeats();
        RebuildPickerResults();
        QueuePickerActionsLayout();
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
                nation.Tag.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                Simulation.StateIdentity.Caption(_campaign, nation, true)
                    .Contains(query, StringComparison.OrdinalIgnoreCase) ||
                Simulation.StateIdentity.Caption(_campaign, nation, false)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(nation => nation.Gdp)
            .Take(4)
            .ToArray();

        foreach (var nation in matches)
        {
            var captured = nation;
            var picked = _pickerHotseatMode ? _campaign.HumanSeats.Contains(nation.Id) : nation == _pickerNation;
            var button = ActionButton($"{nation.Tag}   {nation.DisplayName}", () => SetPickerNation(captured, true), 0, 29, picked);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.ClipText = true;
            button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            if (picked)
                MenuChromeTheme.StylePlateButton(button, active: true, fontSize: 13);
            else
                MenuChromeTheme.StyleWellButton(button, selected: false, fontSize: 13);
            // The flag beside the tag, which the movement rows below always had and these rows
            // did not: the list is where a player learns which country is which.
            ListFlag(button, nation, 24);
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
            var picked = movement == _pickerNation;
            var button = ActionButton(movement.DisplayName, () => SetPickerNation(captured, true), 0, 28, picked);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.ClipText = true;
            // Clipped mid-word read as a typo ("Ambazonia Interi"); an ellipsis reads as "more".
            button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            if (picked)
                MenuChromeTheme.StylePlateButton(button, active: true, fontSize: 12);
            else
                MenuChromeTheme.StyleWellButton(button, selected: false, fontSize: 12);
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
            // The full name first, since the button may show only part of it.
            button.TooltipText = Ui.Tip(movement.DisplayName + "\n" +
                Simulation.Loc.Get(Movements.DefinitionOf(movement)?.Description ?? ""));
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

    /// <summary>
    /// The world with nobody steering it: every nation goes to the computer (the console's
    /// "observe" does the same), the session begins, and observer mode takes the controls out
    /// of the way and starts the clock.
    /// </summary>
    private void BeginObserving()
    {
        if (_campaign is null) return;
        foreach (var nation in _campaign.Nations) nation.IsHuman = false;
        _campaign.HumanSeats.Clear();
        Simulation.FrontPlanner.AssignAi(_campaign);
        _campaignInProgress = true;
        BeginSession();
        _identityTag.Text = Tr("Observer mode");
        SetObserverMode(true);
    }

    private async void OpenLocalMultiplayer()
    {
        EnsureScreen("picker", BuildCountryPicker);
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
        _pickerSubheading.Text = Tr("Choose two or more countries · one shared world");
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
        _pickerSeats.AddChild(Title($"SEATED PLAYERS ({_campaign.HumanSeats.Count})", 11, MenuChromeTheme.PaperFaint));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        _pickerSeats.AddChild(row);
        foreach (var nationId in _campaign.HumanSeats.Take(5))
        {
            var nation = _campaign.NationById(nationId);
            if (nation is null) continue;
            var captured = nation;
            var chip = ActionButton($"× {nation.Tag}", () =>
            {
                _campaign.RemoveHumanSeat(captured.Id);
                SetPickerNation(_pickerNation ?? _campaign.Player, false);
            }, 56, 26);
            MenuChromeTheme.StyleWellButton(chip, false, 12);
            row.AddChild(chip);
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
