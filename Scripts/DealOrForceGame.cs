using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Godot presentation shell following the source GameHud/MainMenu information architecture.
/// Campaign rules remain engine-neutral and all mutations pass through campaign commands.
/// </summary>
public partial class DealOrForceGame : Control
{
    private Campaign _campaign;
    private GlobePresentation _globe = null!;
    private Control _menuLayer = null!;
    private Control _hudLayer = null!;
    private PanelContainer _sidePanel = null!;
    private Label _date = null!;
    private Label _identity = null!;
    private Label _identityTag = null!;
    private StatReadout _treasury = null!;
    private StatReadout _gdp = null!;
    private StatReadout _population = null!;
    private readonly Trend _treasuryTrend = new();
    private readonly Trend _gdpTrend = new();
    private readonly Trend _populationTrend = new();
    private readonly System.Collections.Generic.List<Button> _speedButtons = new();
    private readonly System.Collections.Generic.Dictionary<string, Button> _screenButtons = new();
    private Label _militarySpendingReadout = null!;
    private Label _sideTitle = null!;
    private Label _sideBody = null!;
    private VBoxContainer _sideDetail = null!;
    private HSlider _militarySpending = null!;
    private Button _pause = null!;
    private Button _mainProjectionButton = null!;
    private Button _languageButton = null!;
    private Label _navigationHint = null!;
    private Label _scenarioSummary = null!;
    private Nation _selectedNation;
    private double _dayAccumulator;
    private int _speed = 1;
    private int _lastAutosaveDay = -1;
    private string _openScreen = "";

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildInterface();
        StartCampaign();
    }

    public override void _Process(double delta)
    {
        NetworkProcess(delta);
        if (IsNetworkClient) return;
        if (_campaign is null || _campaign.IsPaused) return;

        _dayAccumulator += delta;
        var secondsPerDay = _campaign.Defines.SecondsForSpeed(_speed);
        var days = 0;
        while (_dayAccumulator >= secondsPerDay && days < 4)
        {
            _dayAccumulator -= secondsPerDay;
            if (_networkConnected && _networkIsHost && _networkGameStarted) _applyingNetworkCommand = true;
            using (SimProfiler.Measure("campaign.day"))
                _campaign.Tick();
            _modLoader.OnDayAdvanced(_campaign);
            if (_networkConnected && _networkIsHost && _networkGameStarted)
            {
                _applyingNetworkCommand = false;
                Rpc(nameof(ClientAdvanceNetworkDay), _campaign.ElapsedDays);
            }
            MaybeAutosave();
            days++;
        }
    }

    public override void _Input(InputEvent inputEvent)
    {
        // Space is a sacred strategy-game time key: it never activates a focused
        // button, confirms a dialog or changes projection. During a campaign it
        // only toggles time, and outside a campaign it is deliberately consumed.
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.Unicode is 96 or 126 || key.Keycode == Key.Quoteleft || key.PhysicalKeycode == Key.Quoteleft)
        {
            ToggleDeveloperConsole();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_developerConsoleLayer is { Visible: true })
        {
            if (key.Keycode == Key.Escape)
            {
                CloseDeveloperConsole();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        // The free camera is a demonstration tool, and a demonstration tool that needs a
        // console command typed out first is one nobody reaches for. F10 flies - not F8,
        // which the Godot editor already uses to stop the running project.
        if (key.Keycode == Key.F10) { ToggleFreeCamera(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.Escape && _globe is { FreeCamera: true })
        {
            ToggleFreeCamera();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.Keycode == Key.F5) { QuickSave(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.F9) { QuickLoad(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode != Key.Space && key.PhysicalKeycode != Key.Space) return;

        if (_campaign is not null && _hudLayer is { Visible: true })
            TogglePause();
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (_developerConsoleLayer is { Visible: true }) return;
        HandleProjectionHotkey(inputEvent);
        if (GetViewport().IsInputHandled()) return;
        if (!inputEvent.IsActionPressed("ui_cancel")) return;
        if (_handoverLayer is { Visible: true }) return;
        if (_eventLayer is { Visible: true }) return;

        if (_peaceLayer is { Visible: true })
            ClosePeaceConference();
        else if (_pauseMenuLayer is { Visible: true })
            HidePauseMenu();
        else if (_ledgerLayer is { Visible: true })
            CloseLedger();
        else if (_sidePanel.Visible)
            CloseSidePanel();
        else if (_pickerLayer.Visible)
            ShowMainMenu();
        else if (_hudLayer.Visible)
            ShowPauseMenu();

        GetViewport().SetInputAsHandled();
    }

    private void StartCampaign()
    {
        if (_campaign is not null)
            _campaign.Changed -= Refresh;

        _campaign = Campaign.CreateBootstrap(20260830);
        ResetTrends();
        _lastAutosaveDay = -1;

        // Day-zero proof that the emergent economy still reproduces the scenario's
        // authored 2024 figures. If this ever drifts, the calibration broke, not the map.
        GD.Print("[dof] " + Simulation.EconomySystem.LastCalibrationReport);
        if (IsInstanceValid(_scenarioSummary))
            _scenarioSummary.Text = $"{_campaign.Nations.Count} nations · {_campaign.States.Count} states · deterministic campaign simulation";
        BindCampaignMods();
        _campaign.Changed += Refresh;
        _campaign.IsPaused = true;
        _selectedNation = _campaign.Player;
        _selectedState = _campaign.States.FirstOrDefault(state => state.NationMapIndex == _campaign.Player.MapIndex);
        _pickerNation = _campaign.Player;
        _globe.BindCampaign(_campaign);
        ResetStrategicAudioState();
        Refresh();
    }

    private void BuildInterface()
    {
        InitializeModding();
        var background = new ColorRect
        {
            Color = Ui.Colour("surface.background"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(background);

        _globe = new GlobePresentation();
        _globe.CountryPicked += OnCountryPicked;
        _globe.ArmyPicked += OnArmyPicked;
        _globe.BattlePicked += ShowBattleDetails;
        _globe.ArmiesBoxPicked += OnArmiesBoxPicked;
        _globe.ProvinceCommanded += OnProvinceCommanded;
        AddChild(_globe);

        BuildMainMenu();
        BuildCountryPicker();
        BuildHud();
        BuildHotseatInterface();
        BuildNetworkMultiplayerInterface();
        BuildPauseMenu();
        BuildSaveLoadInterface();
        BuildEventInterface();
        BuildStrategicAudio();
        BuildDeveloperConsole();
    }

    private void BuildMainMenu()
    {
        _menuLayer = FullLayer("MainMenu");
        AddChild(_menuLayer);

        var panel = TexturedPanel(_menuLayer);
        // Runtime-created Controls need explicit anchors and offsets. Setting
        // Position after a preset leaves them relative to (0, 0) on some
        // Godot 4.x builds, which pushed this panel off-screen.
        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -230;
        panel.OffsetRight = 230;
        panel.OffsetTop = -315;
        panel.OffsetBottom = 315;

        var rows = PanelRows(panel, 28, 24, 16);
        rows.AddChild(Title("DEAL OR FORCE", 34, Ui.Colour("text.accent")));
        rows.AddChild(Title(
            "The world as it stands in 2024 — its borders, its people and its economy.",
            15,
            Ui.Colour("text.muted")));

        rows.AddChild(Title("1 January 2024", 17, Ui.Colour("text.bright")));
        _scenarioSummary = Title("Preparing campaign data…", 14, Ui.Colour("text.hint"));
        rows.AddChild(_scenarioSummary);

        _modStatus = Title(_modLoader.StatusLine, 12, Ui.Colour("text.faint"));
        _modStatus.TooltipText = "Development mods: res://Mods · installed mods: user://mods";
        rows.AddChild(_modStatus);

        rows.AddChild(ActionButton("Singleplayer", OpenCountryPicker, 330, 46, true));
        rows.AddChild(ActionButton("Load latest campaign", LoadLatestSave, 330, 42));

        var multiplayer = ActionButton("Multiplayer · LAN / IP", OpenNetworkMultiplayer, 330, 42);
        multiplayer.TooltipText = "Host or join an authoritative ENet campaign over LAN, direct IPv4 or a virtual LAN such as Radmin VPN.";
        rows.AddChild(multiplayer);

        _mainProjectionButton = ActionButton("Map view: 3D globe", ToggleProjection, 330, 42);
        _mainProjectionButton.TooltipText = "Switch between globe and classic infinitely wrapping map (F2).";
        rows.AddChild(_mainProjectionButton);
        _languageButton = ActionButton(LanguageButtonLabel(), CycleLanguage, 330, 42);
        _languageButton.TooltipText = "Cycle the interface language. Mods add their own by dropping localization/<language>.json into the mod folder.";
        rows.AddChild(_languageButton);
        rows.AddChild(ActionButton("Quit to desktop", () => GetTree().Quit(), 330, 42));

        _navigationHint = Title("Right-drag to turn the globe · wheel to zoom · WASD to move.", 13, Ui.Colour("text.hint"));
        _navigationHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_navigationHint);
    }

    private void BuildHud()
    {
        _hudLayer = FullLayer("GameHud");
        _hudLayer.Visible = false;
        AddChild(_hudLayer);

        var top = TexturedPanel(_hudLayer);
        top.AnchorLeft = 0;
        top.AnchorRight = 1;
        top.AnchorTop = 0;
        top.AnchorBottom = 0;
        top.OffsetLeft = 14;
        top.OffsetRight = -14;
        top.OffsetTop = 12;
        top.OffsetBottom = 104;

        var margin = Margin(top, 18, 12);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 24);
        margin.AddChild(row);

        var identityBox = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        identityBox.AddThemeConstantOverride("separation", 2);
        row.AddChild(identityBox);
        _identity = Title("South Korea", 21, Ui.Colour("text.accent"));
        identityBox.AddChild(_identity);
        _identityTag = Title("KOR · player nation", 13, Ui.Colour("text.muted"));
        identityBox.AddChild(_identityTag);

        var stats = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        stats.AddThemeConstantOverride("separation", 28);
        row.AddChild(stats);
        _treasury = Stat(stats, "TREASURY", "Cash on hand. The line below is the change over the last week of campaign time.");
        _gdp = Stat(stats, "GDP", "Annual gross domestic product, as it emerges from what your economy actually produces.");
        _population = Stat(stats, "POPULATION", "Everyone living in your states, and how that has moved over the last week.");

        var clock = new VBoxContainer { CustomMinimumSize = new Vector2(276, 0) };
        clock.AddThemeConstantOverride("separation", 6);
        row.AddChild(clock);
        _date = Title("1 January 2024", 17, Ui.Colour("text.bright"));
        _date.HorizontalAlignment = HorizontalAlignment.Right;
        clock.AddChild(_date);

        var speeds = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        speeds.AddThemeConstantOverride("separation", 5);
        clock.AddChild(speeds);

        var menu = ActionButton("≡", ShowPauseMenu, 44, 34);
        menu.TooltipText = "Pause menu (Esc) · F5 quick-saves, F9 loads, F10 flies the camera";
        speeds.AddChild(menu);
        _pause = ActionButton("II", TogglePause, 44, 34, true);
        _pause.TooltipText = "Pause and resume campaign time (Space). Space never activates a focused button.";
        speeds.AddChild(_pause);
        _speedButtons.Clear();
        for (var speed = 1; speed <= 5; speed++)
        {
            var captured = speed;
            var button = ActionButton(speed.ToString(), () => SetSpeed(captured), 38, 34);
            _speedButtons.Add(button);
            speeds.AddChild(button);
        }
        UpdateSpeedButtons();

        var navigation = TexturedPanel(_hudLayer);
        navigation.AnchorLeft = 0.5f;
        navigation.AnchorRight = 0.5f;
        navigation.AnchorTop = 1;
        navigation.AnchorBottom = 1;
        navigation.OffsetLeft = -475;
        navigation.OffsetRight = 475;
        // The panel texture contributes 29 px of minimum vertical chrome.
        // Reserve its real 87 px height so the last row is never clipped.
        navigation.OffsetTop = -97;
        navigation.OffsetBottom = -10;
        var navMargin = Margin(navigation, 10, 8);
        var nav = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        nav.AddThemeConstantOverride("separation", 8);
        navMargin.AddChild(nav);

        // Each screen says what it is for. A player who has never opened one of these games
        // should be able to learn the interface by resting on it rather than by clicking
        // everything to find out.
        var screens = new (string Name, string Help)[]
        {
            ("Nation", "Who you are: capital, people, output, treasury and how settled the country is."),
            ("Economy", "The budget: what the week raises, what it costs, and what the region you have selected is made of."),
            ("Military", "The armed forces: how many, how ready, how well fed, and what spending buys."),
            ("Diplomacy", "How the selected country regards you, and what can be done about it."),
            ("Wars", "Every war being fought, what it is costing, and the table where it can be ended."),
            ("Decisions", "One-off national actions, with their price and their consequence."),
            ("Ledger", "The full record: charts, budget, trade, population and the cabinet.")
        };
        _screenButtons.Clear();
        foreach (var (screen, help) in screens)
        {
            var captured = screen;
            var button = ActionButton(screen, () => OpenScreen(captured), 118, 38);
            button.TooltipText = help;
            _screenButtons[captured] = button;
            nav.AddChild(button);
        }
        UpdateScreenButtons();

        _sidePanel = TexturedPanel(_hudLayer);
        _sidePanel.AnchorLeft = 0;
        _sidePanel.AnchorRight = 0;
        _sidePanel.AnchorTop = 0;
        _sidePanel.AnchorBottom = 1;
        _sidePanel.OffsetLeft = 20;
        _sidePanel.OffsetRight = 404;
        _sidePanel.OffsetTop = 140;
        _sidePanel.OffsetBottom = -170;
        _sidePanel.Visible = false;

        var sideRows = ScrollablePanelRows(_sidePanel, 24, 20, 12);
        var header = new HBoxContainer();
        sideRows.AddChild(header);
        _sideTitle = Title("Nation", 27, Ui.Colour("text.accent"));
        _sideTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_sideTitle);
        header.AddChild(ActionButton("×", CloseSidePanel, 38, 34));

        _sideBody = Title("", 16, Ui.Colour("text.body"));
        _sideBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _sideBody.SizeFlagsVertical = SizeFlags.ExpandFill;
        sideRows.AddChild(_sideBody);

        // Figures belong in aligned rows, not in a paragraph. A player comparing two
        // numbers should not have to find them inside a sentence first.
        _sideDetail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sideDetail.AddThemeConstantOverride("separation", 3);
        sideRows.AddChild(_sideDetail);

        
        BuildCountryActionControls(sideRows);
        BuildRegionInteractionControls(sideRows);
        BuildEconomyControls(sideRows);
        BuildDecisionControls(sideRows);
        BuildWarControls(sideRows);
        BuildBattleDetailControls(sideRows);

        _militarySpending = new HSlider
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.01,
            TooltipText = "Military allocation; submitted through the deterministic command queue."
        };
        _militarySpending.ValueChanged += OnSpendingChanged;
        sideRows.AddChild(_militarySpending);

        // A bare slider tells the player nothing about what they just set. The readout
        // below it names the figure and what it costs, and hides with the slider.
        _militarySpendingReadout = Title("", 12, Ui.Colour("text.caption"));
        _militarySpendingReadout.Visible = false;
        sideRows.AddChild(_militarySpendingReadout);

        BuildArmyPanel();
        BuildPeaceConference();
        BuildLedgerInterface();
        BuildMapControls();
        BuildStrategicHud();
    }

    private void BeginSession()
    {
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        if (_eventLayer is not null) _eventLayer.Visible = false;
        _menuLayer.Visible = false;
        _pickerLayer.Visible = false;
        _hudLayer.Visible = true;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        _campaign.IsPaused = true;
        _pause.Text = "▶";
        StyleButton(_pause, true);
        UpdateHotseatHud();
        UpdateNetworkHud();
        if (_campaign.IsHotseat && !_networkConnected) ShowSeatHandover();
        Refresh();
    }

    private void ShowMainMenu()
    {
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        if (_eventLayer is not null) _eventLayer.Visible = false;
        _campaign.IsPaused = true;
        _pause.Text = "▶";
        _hudLayer.Visible = false;
        _pickerLayer.Visible = false;
        _menuLayer.Visible = true;
        _sidePanel.Visible = false;
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_hotseatHud is not null) _hotseatHud.Visible = false;
        if (_handoverLayer is not null) _handoverLayer.Visible = false;
        if (_networkLayer is not null) _networkLayer.Visible = false;
        if (_networkHud is not null) _networkHud.Visible = false;
    }

    private void OpenScreen(string screen)
    {
        if (screen == "Ledger")
        {
            OpenLedger();
            return;
        }
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        _openScreen = screen;
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = screen == "Economy";
        _decisionControls.Visible = screen == "Decisions";
        _warControls.Visible = screen == "Wars";
        _battleDetailControls.Visible = false;
        _militarySpending.Visible = screen == "Military";
        _militarySpendingReadout.Visible = screen == "Military";
        UpdateScreenButtons();

        var player = _campaign.Player;
        var nation = _selectedNation ?? player;
        _sideTitle.Text = screen;
        ClearDetail();

        switch (screen)
        {
            case "Nation":
                _sideBody.Text = $"{nation.Name} ({nation.Tag}) · {nation.Region}, {nation.Continent}";
                DetailHeading("STATE");
                DetailRow("Capital", nation.CapitalName);
                DetailRow("States held", _campaign.StatesOf(nation).Length.ToString("N0"));
                DetailRow("Population", Fmt.People(nation.Population), Fmt.PeopleExact(nation.Population));
                DetailHeading("ECONOMY");
                DetailRow("GDP", Fmt.Money(nation.Gdp), Fmt.MoneyExact(nation.Gdp));
                DetailRow("GDP per head", Fmt.Money(nation.Gdp / Mathf.Max(1f, nation.Population)),
                    "Annual output divided by everyone living in the country.");
                DetailRow("Treasury", Fmt.Money(nation.Treasury), Fmt.MoneyExact(nation.Treasury),
                    nation.Treasury < 0f ? "state.negative" : "text.bright");
                DetailRow("Tax rate", Fmt.Percent(nation.TaxRate));
                DetailHeading("SOCIETY");
                DetailRow("Standard of living", Fmt.Percent(nation.StandardOfLiving));
                DetailRow("Literacy", Fmt.Percent(nation.Literacy));
                DetailRow("Stability", Fmt.Percent(nation.Stability),
                    "Below a third, radicalism starts to bite.",
                    nation.Stability < 0.34f ? "state.negative" : nation.Stability < 0.55f ? "state.warning" : "state.positive");
                break;

            case "Economy":
                ShowEconomyScreen();
                break;

            case "Military":
                var armies = _campaign.Armies.Where(army => army.NationId == player.Id).ToArray();
                var divisions = armies.Sum(army => army.Divisions);
                var fleets = _campaign.Fleets.Where(fleet => fleet.NationId == player.Id).ToArray();
                var personnel = MilitaryBootstrap.Personnel(player.Tag, player.Population);
                _sideBody.Text = $"{player.Name} armed forces";

                DetailHeading("FORCES");
                DetailRow("Divisions", divisions.ToString("0.0"),
                    "Independent formations under your flag. Each takes frontage in a battle, so more of " +
                    "them is not the same as more strength in one province.");
                DetailRow("Field armies", armies.Length.ToString("N0"),
                    "Formations on the map, counted as units rather than as strength.");
                DetailRow("Fleets", $"{fleets.Length:N0} · {fleets.Sum(fleet => fleet.Hulls):N0} hulls",
                    "Commissioned squadrons and the hulls in them.");
                DetailRow("Active personnel", Fmt.People(personnel), Fmt.PeopleExact(personnel));

                DetailHeading("READINESS");
                var organisation = armies.Length == 0 ? 0f : armies.Average(army => army.Organisation);
                var strength = armies.Length == 0 ? 0f : armies.Average(army => army.Strength);
                var supply = armies.Length == 0 ? 0f : armies.Average(army => army.SupplyLevel);
                DetailRow("Organisation", Fmt.Percent(organisation),
                    "The will to keep fighting, averaged. It empties in battle and recovers out of it; " +
                    "a formation at zero is broken, not destroyed.",
                    organisation < 0.35f ? "state.negative" : organisation < 0.6f ? "state.warning" : "state.positive");
                DetailRow("Fighting strength", Fmt.Percent(strength),
                    "How many of the establishment are still standing. This one is men, and it comes back slowly.",
                    strength < 0.6f ? "state.warning" : "state.positive");
                DetailRow("Supply", Fmt.Percent(supply),
                    "How well the army is being fed. Infrastructure widens the throughput that decides it.",
                    supply < 0.5f ? "state.negative" : supply < 0.8f ? "state.warning" : "state.positive");
                DetailRow("Cut off", armies.Count(army => army.CutOff).ToString("N0"),
                    "Formations with no supply route home. They bleed until the route is reopened.",
                    armies.Any(army => army.CutOff) ? "state.negative" : "text.bright");

                DetailHeading("MANPOWER");
                DetailRow("Reserve", Fmt.People(player.ManpowerReserve),
                    "Men available to replace losses. It refills from population and conscription.");
                DetailRow("Daily reinforcement", Fmt.People(player.ReinforcementCapacity),
                    "How many can be moved into formations each day. Barracks and military spending raise it.");
                DetailRow("Military spending", Fmt.Percent(player.MilitarySpending),
                    "Share of output going to the armed forces. The slider below sets it.");

                _militarySpending.Value = player.MilitarySpending;
                break;

            case "Diplomacy":
                ShowCountryInteraction(nation, "Diplomatic overview");
                break;

            case "Wars":
                var activeWars = _campaign.Wars.Where(war => !war.Ended).ToArray();
                _sideBody.Text = activeWars.Length == 0
                    ? "WARS AND FRONTS\n\nNo active wars." + PeaceHistory()
                    : "WARS AND FRONTS\n\n" + string.Join("\n\n", activeWars.Select(war =>
                    {
                        var attacker = _campaign.NationById(war.AttackerId);
                        var defender = _campaign.NationById(war.DefenderId);
                        var involvement = war.Involves(player.Id) ? "YOU ARE A BELLIGERENT" : "Foreign war";
                        var battles = _campaign.Battles.Where(battle => battle.WarId == war.Id && !battle.Ended).ToArray();
                        var latest = battles.OrderByDescending(battle => battle.StartedDay).FirstOrDefault();
                        var objective = _campaign.States.FirstOrDefault(state => state.MapIndex == war.ClaimedStateMapIndex);
                        var attackerDoctrine = attacker is null ? "unknown" : AiStrategyCatalog.Resolve(_campaign, war, attacker).Id;
                        var defenderDoctrine = defender is null ? "unknown" : AiStrategyCatalog.Resolve(_campaign, war, defender).Id;
                        return $"{war.Name}\n{attacker?.Name} vs {defender?.Name}\n" +
                               $"War goal: {(objective?.Name ?? "strategic supremacy")}\n" +
                               $"Operational profiles: {attackerDoctrine} / {defenderDoctrine}\n" +
                               $"Front: {war.AttackerFront.Count} / {war.DefenderFront.Count} provinces\n" +
                               $"War score: {war.WarScore:+0.0;-0.0;0.0} · active battles: {battles.Length}\n" +
                               $"  occupation {war.OccupationScore:+0.0;-0.0;0.0} · battles {war.BattleScore:+0.0;-0.0;0.0}\n" +
                               $"  objective {war.ObjectiveScore:+0.0;-0.0;0.0} · support {war.SupportScore:+0.0;-0.0;0.0}\n" +
                               $"Losses: {war.AttackerLosses:N0} / {war.DefenderLosses:N0}\n" +
                               $"Support: {war.AttackerWarSupport:P0} / {war.DefenderWarSupport:P0}\n" +
                               (latest is null ? "" : $"Latest contact: province #{latest.ProvinceId:N0} · {latest.Outcome}\n") +
                               involvement;
                    }));
                RefreshWarControls();
                break;

            case "Decisions":
                ShowDecisionsScreen();
                break;
        }
    }

    private void CloseSidePanel()
    {
        _sidePanel.Visible = false;
        _openScreen = "";
        UpdateScreenButtons();
        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
    }

    private void OnCountryPicked(GlobePick pick)
    {
        _selectedNation = pick.Nation;
        _selectedState = pick.State;
        _selectedProvinceId = pick.ProvinceId;
        if (_pickerLayer.Visible)
        {
            SetPickerNation(pick.Nation, false);
            UpdatePickerLocation(pick);
            return;
        }
        if (!_hudLayer.Visible) return;

        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
        _openScreen = "";
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = pick.Nation != _campaign.Player;
        _regionControls.Visible = pick.State is not null;
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _warControls.Visible = false;
        _battleDetailControls.Visible = false;
        _militarySpending.Visible = false;
        _sideTitle.Text = pick.Nation.Name;
        var state = pick.State is null ? "Unmapped land" : $"{pick.State.Name} ({pick.State.Kind})";
        var province = pick.ProvinceId > 0 ? $"Tactical province #{pick.ProvinceId:N0}" : "Province topology loading";
        ShowCountryInteraction(pick.Nation, $"{state} · {province}");
        if (pick.State is not null) ShowRegionOverview();
    }

    /// <summary>Take the camera off its rig, or put it back and restore the interface.</summary>
    private void ToggleFreeCamera()
    {
        if (_globe is null) return;
        var enabled = !_globe.FreeCamera;
        _freeCameraMode = enabled;
        _globe.SetFreeCamera(enabled);
        if (enabled && _peaceLayer is { Visible: true }) ClosePeaceConference();
        if (_hudLayer is not null) _hudLayer.Visible = !enabled && _campaign is not null && !_menuLayer.Visible;
        if (enabled)
        {
            if (_menuLayer is not null) _menuLayer.Visible = false;
            if (_pickerLayer is not null) _pickerLayer.Visible = false;
            CloseDeveloperConsole();
        }
    }

    private void TogglePause()
    {
        if (IsNetworkClient)
        {
            _networkHudStatus.Text = "Only the host controls campaign time.";
            return;
        }
        _campaign.IsPaused = !_campaign.IsPaused;
        _pause.Text = _campaign.IsPaused ? "▶" : "II";
        StyleButton(_pause, _campaign.IsPaused);
        Refresh();
    }

    private void SetSpeed(int speed)
    {
        if (IsNetworkClient)
        {
            _networkHudStatus.Text = "Only the host controls campaign speed.";
            return;
        }
        _speed = Mathf.Clamp(speed, 1, 5);
        if (_campaign.IsPaused)
        {
            _campaign.IsPaused = false;
            _pause.Text = "II";
            StyleButton(_pause, false);
        }
        Refresh();
    }

    private void OnSpendingChanged(double value)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetMilitarySpendingCommand
        {
            IssuerId = _campaign.Player.Id,
            Value = (float)value
        });
        _campaign.Commands.ApplyAll(_campaign);
        Refresh();
        if (_openScreen == "Military") OpenScreen("Military");
    }

    private void Refresh()
    {
        if (_campaign?.Player is null) return;

        var player = _campaign.Player;
        _identity.Text = $"{player.Name} ({player.Tag})";
        _identityTag.Text = _observerMode ? "OBS · observer mode" : $"{player.Tag} · player nation";
        _date.Text = _campaign.Date.ToString();

        var day = _campaign.ElapsedDays;
        _treasuryTrend.Sample(day, player.Treasury);
        _gdpTrend.Sample(day, player.Gdp);
        _populationTrend.Sample(day, player.Population);

        WriteStat(_treasury, Fmt.Money(player.Treasury), Fmt.MoneyExact(player.Treasury),
            Fmt.MoneyDelta((float)_treasuryTrend.Delta), Fmt.Direction(_treasuryTrend.Delta, 0.005d));
        WriteStat(_gdp, Fmt.Money(player.Gdp), Fmt.MoneyExact(player.Gdp),
            Fmt.MoneyDelta((float)_gdpTrend.Delta), Fmt.Direction(_gdpTrend.Delta, 0.005d));
        WriteStat(_population, Fmt.People(player.Population), Fmt.PeopleExact(player.Population),
            Fmt.PeopleDelta(_populationTrend.Delta), Fmt.Direction(_populationTrend.Delta, 1d));

        _militarySpending.SetValueNoSignal(player.MilitarySpending);
        UpdateSpendingReadout(player);
        UpdateSpeedButtons();
        UpdateScreenButtons();
        RefreshStrategicAudio();
        RefreshEventInterface();
        RefreshStrategicHud();
        if (_ledgerLayer is { Visible: true }) RefreshLedger();
        if (_selectedArmy is not null && _armyPanel is { Visible: true }) RefreshArmyPanel();
        else if (_sidePanel.Visible && _openScreen is "Wars" or "Military" or "Economy" or "Nation")
            OpenScreen(_openScreen);
    }

    /// <summary>
    /// One headline figure in the top bar: its caption, the compact value, and the week's
    /// movement underneath. A number with no direction is only half the information a
    /// player needs - "$40B" answers nothing that "$40B, −$1.2B this week" does not.
    /// </summary>
    private sealed class StatReadout
    {
        internal Label Value = null!;
        internal Label Delta = null!;
    }

    private StatReadout Stat(Container parent, string caption, string tooltip)
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(132, 0), TooltipText = tooltip };
        box.AddThemeConstantOverride("separation", 1);
        box.MouseFilter = MouseFilterEnum.Stop;
        parent.AddChild(box);
        box.AddChild(Title(caption, 11, Ui.Colour("text.caption")));

        var readout = new StatReadout
        {
            Value = Title("—", 17, Ui.Colour("text.bright")),
            Delta = Title("", 11, Ui.Colour("text.faint"))
        };
        box.AddChild(readout.Value);
        box.AddChild(readout.Delta);
        return readout;
    }

    /// <summary>
    /// Write one headline figure and its movement. The exact figure goes into the tooltip:
    /// the compact form is for scanning, and the moment a player wants the real number
    /// they want every digit of it.
    /// </summary>
    private static void WriteStat(StatReadout stat, string compact, string exact, string delta, int direction)
    {
        if (stat is null) return;
        stat.Value.Text = compact;
        stat.Value.TooltipText = exact;
        stat.Value.MouseFilter = MouseFilterEnum.Stop;
        stat.Delta.Text = delta;
        stat.Delta.AddThemeColorOverride("font_color", direction switch
        {
            > 0 => Ui.Colour("state.positive"),
            < 0 => Ui.Colour("state.negative"),
            _ => Ui.Colour("text.faint")
        });
    }

    /// <summary>
    /// A rolling week of daily samples of one figure, so the top bar can show real movement
    /// rather than a number that simply is what it is. Kept to eight days: the campaign
    /// advances one day at a time, and a week is the horizon a player plans against.
    /// </summary>
    private sealed class Trend
    {
        private readonly System.Collections.Generic.List<(int Day, double Value)> _samples = new();

        internal void Sample(int day, double value)
        {
            if (_samples.Count > 0 && _samples[^1].Day == day)
            {
                _samples[^1] = (day, value);
                return;
            }
            _samples.Add((day, value));
            while (_samples.Count > 1 && day - _samples[0].Day > 7) _samples.RemoveAt(0);
        }

        /// <summary>Movement across the sampled window; exactly zero until a second day exists.</summary>
        internal double Delta => _samples.Count < 2 ? 0d : _samples[^1].Value - _samples[0].Value;

        internal void Reset() => _samples.Clear();
    }

    /// <summary>Empty the structured rows before a screen writes its own.</summary>
    private void ClearDetail()
    {
        if (_sideDetail is null) return;
        foreach (var child in _sideDetail.GetChildren())
        {
            _sideDetail.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>One caption/value row, the caption left and the value right against it.</summary>
    private void DetailRow(string caption, string value, string tooltip = null, string valueToken = "text.bright")
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        if (!string.IsNullOrEmpty(tooltip))
        {
            row.TooltipText = tooltip;
            row.MouseFilter = MouseFilterEnum.Stop;
        }

        var label = Title(caption, 13, Ui.Colour("text.caption"));
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        row.AddChild(label);

        var figure = Title(value, 14, Ui.Colour(valueToken));
        figure.HorizontalAlignment = HorizontalAlignment.Right;
        figure.AutowrapMode = TextServer.AutowrapMode.Off;
        row.AddChild(figure);

        _sideDetail.AddChild(row);
    }

    /// <summary>A small capitalised rule between groups of rows.</summary>
    private void DetailHeading(string text)
    {
        var heading = Title(text, 11, Ui.Colour("text.caption"));
        heading.CustomMinimumSize = new Vector2(0, 22);
        heading.VerticalAlignment = VerticalAlignment.Bottom;
        _sideDetail.AddChild(heading);
    }

    /// <summary>
    /// What the military slider is actually setting. The slider itself carries no units,
    /// and "0.34" means nothing until it is named as a share of output and priced.
    /// </summary>
    private void UpdateSpendingReadout(Nation player)
    {
        if (_militarySpendingReadout is null || player is null) return;
        var annual = player.Gdp * player.MilitarySpending;
        _militarySpendingReadout.Text =
            $"{Fmt.Percent(player.MilitarySpending)} of output · about {Fmt.Money(annual)} a year";
    }

    /// <summary>Forget the week of history behind the top bar - a new campaign has none.</summary>
    private void ResetTrends()
    {
        _treasuryTrend.Reset();
        _gdpTrend.Reset();
        _populationTrend.Reset();
    }

    /// <summary>The speed the campaign is actually running at, lit so the player can see it.</summary>
    private void UpdateSpeedButtons()
    {
        var running = _campaign is not null && !_campaign.IsPaused;
        for (var index = 0; index < _speedButtons.Count; index++)
        {
            var button = _speedButtons[index];
            if (!GodotObject.IsInstanceValid(button)) continue;
            StyleButton(button, running && index + 1 == _speed);
            button.TooltipText = _campaign is null
                ? $"Campaign speed {index + 1}"
                : $"Campaign speed {index + 1} · {_campaign.Defines.SecondsForSpeed(index + 1):0.##}s per day";
        }
    }

    /// <summary>The screen currently open, lit in the bottom bar.</summary>
    private void UpdateScreenButtons()
    {
        var open = _ledgerLayer is { Visible: true } ? "Ledger" : _sidePanel is { Visible: true } ? _openScreen : "";
        foreach (var (screen, button) in _screenButtons)
        {
            if (!GodotObject.IsInstanceValid(button)) continue;
            StyleButton(button, screen == open);
        }
    }

    private Control FullLayer(string name)
    {
        var layer = new Control { Name = name, MouseFilter = MouseFilterEnum.Ignore };
        layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return layer;
    }

    private PanelContainer TexturedPanel(Node parent)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", PanelStyle());
        parent.AddChild(panel);
        return panel;
    }

    private VBoxContainer PanelRows(PanelContainer panel, int horizontal, int vertical, int separation)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        panel.AddChild(margin);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", separation);
        margin.AddChild(rows);
        return rows;
    }

    private VBoxContainer ScrollablePanelRows(PanelContainer panel, int horizontal, int vertical, int separation)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            FollowFocus = true
        };
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        panel.AddChild(scroll);

        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        scroll.AddChild(margin);

        var rows = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(320, 0)
        };
        rows.AddThemeConstantOverride("separation", separation);
        margin.AddChild(rows);
        return rows;
    }

    private MarginContainer Margin(Container parent, int horizontal, int vertical)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", horizontal);
        margin.AddThemeConstantOverride("margin_right", horizontal);
        margin.AddThemeConstantOverride("margin_top", vertical);
        margin.AddThemeConstantOverride("margin_bottom", vertical);
        parent.AddChild(margin);
        return margin;
    }

    /// <summary>Localised text helper. Every static caption in the UI goes through Title
    /// or ActionButton, so translating the game is these two lookups rather than 300 call
    /// sites - and an untranslated string still renders as its English source.</summary>
    internal static string Tr(string text) => Simulation.Loc.Get(text);

    private static Label Title(string text, int size, Color color)
    {
        var label = new Label
        {
            Text = Tr(text),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.SetMeta(LocSourceMeta, text);
        label.SetMeta(LocAppliedMeta, label.Text);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private Button ActionButton(string text, System.Action callback, float width, float height, bool primary = false)
    {
        var button = new Button
        {
            Text = Tr(text),
            CustomMinimumSize = new Vector2(width, height)
        };
        button.SetMeta(LocSourceMeta, text);
        button.SetMeta(LocAppliedMeta, button.Text);
        StyleButton(button, primary);
        button.AddThemeFontSizeOverride("font_size", height >= 42 ? 15 : 13);
        button.Pressed += () =>
        {
            PlayStrategicCue("ui_select");
            callback();
        };
        return button;
    }

    /// <summary>
    /// Node metadata key holding a caption's untranslated English source. Translating in
    /// place would lose it, and then a second language switch would have nothing left to
    /// translate from - so the source travels with the widget.
    /// </summary>
    private const string LocSourceMeta = "loc_source";

    /// <summary>
    /// The translated text this widget was last given. A caption whose text has since been
    /// rewritten at runtime - a live figure, a status line, the language button itself -
    /// must not be dragged back to its creation-time wording by a language switch, so the
    /// refresh only touches widgets still showing exactly what it last wrote.
    /// </summary>
    private const string LocAppliedMeta = "loc_applied";

    /// <summary>Re-translate every caption still showing its own translation, in place.</summary>
    private void RefreshLocalisedText() => RefreshLocalisedText(this);

    private static void RefreshLocalisedText(Node node)
    {
        if (node.HasMeta(LocSourceMeta))
        {
            var source = node.GetMeta(LocSourceMeta).AsString();
            var applied = node.HasMeta(LocAppliedMeta) ? node.GetMeta(LocAppliedMeta).AsString() : null;
            var translated = Tr(source);

            switch (node)
            {
                case Button button when applied is null || button.Text == applied:
                    button.Text = translated;
                    node.SetMeta(LocAppliedMeta, translated);
                    break;
                case Label label when applied is null || label.Text == applied:
                    label.Text = translated;
                    node.SetMeta(LocAppliedMeta, translated);
                    break;
            }
        }

        foreach (var child in node.GetChildren()) RefreshLocalisedText(child);
    }

    /// <summary>
    /// Both halves are localised: the word, through the ordinary lookup, and the language's
    /// own name, which every table gives in that language ("Русский", not "Russian").
    /// </summary>
    private static string LanguageButtonLabel() =>
        $"{Tr("Language")}: {Simulation.Loc.DisplayName(Simulation.Loc.Language)}";

    /// <summary>
    /// Steps through every language that actually has a table loaded, base game or mod.
    /// </summary>
    private void CycleLanguage()
    {
        var languages = Simulation.Loc.Languages;
        if (languages.Count <= 1) return;

        var index = languages.ToList().FindIndex(item =>
            string.Equals(item, Simulation.Loc.Language, System.StringComparison.OrdinalIgnoreCase));
        Simulation.Loc.Language = languages[(index + 1) % languages.Count];

        RefreshLocalisedText();

        // Written after the sweep, not before: this caption is built from the language's
        // own name rather than from a source string, so the generic refresh cannot
        // produce it and would otherwise overwrite it with the English it was created as.
        if (_languageButton is not null && GodotObject.IsInstanceValid(_languageButton))
        {
            var label = LanguageButtonLabel();
            _languageButton.Text = label;
            _languageButton.SetMeta(LocAppliedMeta, label);
        }
    }

    /// <summary>
    /// Paint one button as ordinary or accented. Split out of button construction because
    /// accent is not a fixed property of a button any more: the speed the campaign is
    /// running at and the screen that is open are both shown by lighting the control that
    /// represents them, which means re-painting a button that already exists.
    /// </summary>
    internal static void StyleButton(Button button, bool primary)
    {
        button.AddThemeStyleboxOverride("normal", ButtonStyle(primary, 0));
        button.AddThemeStyleboxOverride("hover", ButtonStyle(primary, 1));
        button.AddThemeStyleboxOverride("pressed", ButtonStyle(primary, 2));
        button.AddThemeStyleboxOverride("disabled", ButtonStyle(false, 3));
        button.AddThemeStyleboxOverride("focus", ButtonStyle(primary, 1));
        button.AddThemeColorOverride("font_color", primary ? Ui.Colour("button.text_hover") : Ui.Colour("button.text"));
        button.AddThemeColorOverride("font_hover_color", Ui.Colour("button.text_hover"));
        button.AddThemeColorOverride("font_pressed_color", Ui.Colour("button.text_pressed"));
        button.AddThemeColorOverride("font_disabled_color", Ui.Colour("button.text_disabled"));
    }

    private static StyleBoxFlat ButtonStyle(bool primary, int state)
    {
        var background = state switch
        {
            1 => primary ? Ui.Colour("button.primary_fill_hover") : Ui.Colour("button.fill_hover"),
            2 => primary ? Ui.Colour("button.primary_fill_pressed") : Ui.Colour("button.fill_pressed"),
            3 => Ui.Colour("button.fill_disabled"),
            _ => primary ? Ui.Colour("button.primary_fill") : Ui.Colour("button.fill")
        };
        var border = state switch
        {
            1 => Ui.Colour("button.border_hover"),
            2 => Ui.Colour("button.border_pressed"),
            3 => Ui.Colour("button.border_disabled"),
            _ => primary ? Ui.Colour("button.primary_border") : Ui.Colour("button.border")
        };
        return new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            BorderWidthLeft = state == 1 ? 2 : 1,
            BorderWidthTop = state == 1 ? 2 : 1,
            BorderWidthRight = state == 1 ? 2 : 1,
            BorderWidthBottom = state == 2 ? 1 : 2,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            CornerDetail = 4,
            ShadowColor = new Color(0f, 0f, 0f, state == 3 ? 0.18f : 0.58f),
            ShadowSize = state == 2 ? 1 : 3,
            ShadowOffset = new Vector2(0, state == 2 ? 0 : 2),
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = state == 2 ? 7 : 6,
            ContentMarginBottom = state == 2 ? 5 : 8,
            AntiAliasing = true
        };
    }

    private StyleBox PanelStyle()
    {
        return new StyleBoxTexture
        {
            Texture = GD.Load<Texture2D>("res://Assets/Textures/LegacyGlobality/panel_chrome_v2.png"),
            TextureMarginLeft = 16,
            TextureMarginTop = 16,
            TextureMarginRight = 16,
            TextureMarginBottom = 16,
            ModulateColor = new Color(0.78f, 0.86f, 0.94f, 0.98f)
        };
    }
}
