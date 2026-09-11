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
    private StatReadout _balance = null!;
    private StatReadout _stability = null!;
    private StatReadout _manpower = null!;
    private StatReadout _gdp = null!;
    private StatReadout _population = null!;
    private readonly Trend _treasuryTrend = new();
    private readonly Trend _gdpTrend = new();
    private readonly Trend _populationTrend = new();
    private readonly System.Collections.Generic.List<Button> _speedButtons = new();
    private readonly System.Collections.Generic.Dictionary<string, Button> _screenButtons = new();
    private Label _militarySpendingReadout = null!;
    private Label _sideTitle = null!;
    private Control _sideFlag;
    private Control _identityFlag;
    private Label _sideBody = null!;
    private VBoxContainer _sideDetail = null!;
    private HFlowContainer _sideLinks = null!;
    private HSlider _militarySpending = null!;
    private Button _pause = null!;
    private Button _mainProjectionButton = null!;
    private Button _languageButton = null!;
    private Button _smoothingButton = null!;
    private Label _navigationHint = null!;
    private Label _scenarioSummary = null!;
    private Nation _selectedNation;
    private double _dayAccumulator;
    private int _speed = 1;
    private int _lastAutosaveDay = -1;
    private string _openScreen = "";

    /// <summary>
    /// Whether the campaign on the table is one somebody has actually been playing.
    ///
    /// The menu's globe is backed by a campaign too, so "is there a campaign" was never
    /// the question; the question is whether throwing it away would lose anything.
    /// </summary>
    private bool _campaignInProgress;

    public override void _Ready()
    {
        StartDiagnostics();
        // `user://` is the engine's idea of where a player's own files go, and the simulation
        // layer does not import Godot - so it is told rather than asked. Read before the
        // interface is built, because the interface shows what was chosen last time.
        Simulation.PlayerSettings.UseDirectory(ProjectSettings.GlobalizePath("user://"));
        // Resolved at startup rather than when somebody first opens the archive: it is
        // what creates the folder and moves an older installation's saves into it, and
        // "where are my saves" is a question worth being able to answer from a log.
        GD.Print($"[dof] saves: {SaveRoot}");
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // The interface is built next and asks for its pictures by name, so the names have
        // to mean something first. Mods layer their own over these when a campaign starts.
        Simulation.SpriteCatalog.Load();
        Simulation.SoundCatalog.Load();
        Simulation.MusicCatalog.Load();
        Simulation.FontCatalog.Load();
        ApplyDeclaredFont();
        BuildInterface();
        StartCampaign();
    }

    public override void _Process(double delta)
    {
        if (_globe is not null) _globe.FogOfWarActive = _campaign is not null && _menuLayer is { Visible: false } && _pickerLayer is { Visible: false };
        if (_globe is not null) _globe.MilitaryMarkersEnabled = _menuLayer is { Visible: false };
        // The lens and the roll move while a key is held, so the line that reports them
        // has to be written every frame rather than when something is pressed.
        if (_freeCameraMode) RefreshFreeCameraReadout();
        NetworkProcess(delta);
        if (IsNetworkClient) { if (_campaign is not null) ReportFrameCost(delta); return; }
        if (_campaign is null) return;
        // Everything that happened since the last frame, drawn once.
        _campaign.FlushInterfaceChanges();
        // Reported whether or not time is running: a measurement that only arrives when
        // everything is already going well is no use for finding out why it is not.
        ReportFrameCost(delta);
        if (_campaign.IsPaused) return;

        _dayAccumulator += delta;
        var secondsPerDay = _campaign.Defines.SecondsForSpeed(_speed);
        var days = 0;

        // Time is given a slice of the frame rather than a number of days.
        //
        // The loop used to run up to four days whatever they cost. A campaign day is
        // fourteen milliseconds in a world with a hundred wars in it, and the first few
        // days of a session are far worse because every path in the simulation is being
        // compiled as it is first walked - so pressing play produced a stall of two hundred
        // milliseconds and several more after it, which is exactly what "it lags when you
        // start time" means.
        //
        // A budget converts that into the right failure: the frame stays inside its
        // sixteen milliseconds and time runs as fast as it can rather than the frame rate
        // collapsing to keep a promise about days. A speed the machine cannot reach becomes
        // a slower campaign instead of an unresponsive one.
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var slice = _campaign.Defines.FrameSimulationBudgetMs;
        var cap = _campaign.Defines.MaxDaysPerFrame;
        while (_dayAccumulator >= secondsPerDay && days < cap &&
               (days == 0 || budget.Elapsed.TotalMilliseconds < slice))
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
                // And every so often, what the world actually looks like, so a client that
                // has quietly drifted finds out within days instead of never.
                BroadcastNetworkChecksum();
            }
            MaybeAutosave();
            days++;
        }
    }

    private double _frameReportAccumulator;
    private int _frameReportFrames;
    private int _frameReportDay;
    private int _frameReportRebuilds;
    private int _frameReportRevision;

    /// <summary>
    /// What a frame actually costs, printed to the log every few seconds while time is
    /// running. The headless bench measures the simulation and says a campaign day costs
    /// eight milliseconds; a player reporting that the game is unplayable is reporting
    /// something the bench cannot see, and guessing at which half is at fault is how an
    /// afternoon disappears.
    /// </summary>

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
        if (key.Keycode == Key.Escape && _recruitmentLayer is { Visible: true })
        { _recruitmentLayer.Hide(); GetViewport().SetInputAsHandled(); return; }
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

        // Everything the flying camera does that is a switch rather than a movement. They
        // are read here rather than polled with the rest because holding G down should not
        // put the camera on and off a turntable sixty times a second - and they are guarded
        // on the camera being up, so none of these letters mean anything to a campaign.
        if (_globe is { FreeCamera: true })
        {
            switch (key.Keycode)
            {
                case Key.G:
                    _globe.SetFreeOrbit(!_globe.FreeOrbiting);
                    RefreshFreeCameraReadout();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.T:
                    // Hands off the mouse and the shot keeps turning, which is the whole
                    // point: a slow circle nobody is steering has no wobble in it.
                    _globe.SetFreeOrbit(true);
                    _globe.SetFreeAutoOrbit(_globe.FreeAutoOrbit > 0.0001f ? 0f : 0.09f);
                    RefreshFreeCameraReadout();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.C:
                    _globe.SetFreeSmoothing(!_globe.FreeSmoothing);
                    RefreshFreeCameraReadout();
                    GetViewport().SetInputAsHandled();
                    return;
                case Key.H:
                    _freeCameraReadoutVisible = !_freeCameraReadoutVisible;
                    RefreshFreeCameraReadout();
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }
        // Next to F2, which swaps the projection: both are things a player flips while
        // looking at the map to see which they prefer, and neither is worth a menu trip.
        if (key.Keycode == Key.F3) { ToggleMapSmoothing(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.Escape && _globe is { FreeCamera: true })
        {
            ToggleFreeCamera();
            GetViewport().SetInputAsHandled();
            return;
        }
        // A brush left down turns the map into a canvas and swallows ordinary clicks, so
        // escape has to reach it before anything else that escape closes.
        if (key.Keycode == Key.Escape && _drawingKind is not null)
        {
            EndDrawing();
            SetArmyFeedback("The line was abandoned.");
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

        // Halt, and come off the plan. Two keys because they are two decisions - see
        // HaltSelectedDivisions for what one key cost. Only while formations are selected
        // and only in the campaign, so H stays a letter everywhere else.
        if (inputEvent is InputEventKey { Pressed: true, Echo: false } key &&
            _hudLayer is { Visible: true } && _selectedArmies.Count > 0)
        {
            switch (key.Keycode)
            {
                case Key.H:
                    if (Input.IsKeyPressed(Key.Ctrl)) DetachSelectedDivisions();
                    else if (Input.IsKeyPressed(Key.Shift)) ReturnSelectedDivisionsToPlan();
                    else HaltSelectedDivisions();
                    GetViewport().SetInputAsHandled();
                    return;

                // Ctrl+B is the key HOI4 uses, and a mode rather than a modifier because
                // Control on the right button already means "support this attack".
                case Key.B when Input.IsKeyPressed(Key.Ctrl):
                    ToggleStrategicRedeployment();
                    GetViewport().SetInputAsHandled();
                    return;

                case Key.K when Input.IsKeyPressed(Key.Ctrl):
                    ConsolidateSelectedDivisions();
                    GetViewport().SetInputAsHandled();
                    return;

                // Guard a region rather than hold a line, and how freely the plan may
                // shuffle the formations on it. Both belong on buttons in the army panel;
                // they are on keys until that panel stops being rewritten under us.
                case Key.G when Input.IsKeyPressed(Key.Ctrl):
                    ToggleDrawing(Simulation.FrontLineKind.AreaDefence);
                    GetViewport().SetInputAsHandled();
                    return;

                case Key.F when Input.IsKeyPressed(Key.Ctrl):
                    CyclePlanCohesion();
                    GetViewport().SetInputAsHandled();
                    return;
            }
        }

        if (!inputEvent.IsActionPressed("ui_cancel")) return;
        if (_handoverLayer is { Visible: true }) return;
        if (_eventLayer is { Visible: true }) return;

        if (_peaceLayer is { Visible: true })
            ClosePeaceConference();
        else if (_pauseMenuLayer is { Visible: true })
            HidePauseMenu();
        else if (_stateAffairsLayer is { Visible: true })
            CloseStateAffairs();
        else if (_ledgerLayer is { Visible: true })
            CloseLedger();
        else if (_warLedgerLayer is { Visible: true })
            CloseWarLedger();
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
        _campaignInProgress = false;

        // Day-zero proof that the emergent economy still reproduces the scenario's
        // authored 2024 figures. If this ever drifts, the calibration broke, not the map.
        GD.Print("[dof] " + Simulation.EconomySystem.LastCalibrationReport);
        if (IsInstanceValid(_scenarioSummary))
            _scenarioSummary.Text = Simulation.Loc.Format("{0} nations · {1} states · deterministic campaign simulation", _campaign.Nations.Count, _campaign.States.Count);
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
        _globe.FleetPicked += OnFleetPicked;
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
        BuildPeaceOfferInterface();
        BuildStrategicAudio();
        BuildDeveloperConsole();
        BuildFreeCameraOverlay();
    }

    private void BuildMainMenu()
    {
        _menuLayer = FullLayer("MainMenu");
        AddChild(_menuLayer);

        var panel = TexturedPanel(_menuLayer);
        // Runtime-created Controls need explicit anchors and offsets. Setting
        // Position after a preset leaves them relative to (0, 0) on some
        // Godot 4.x builds, which pushed this panel off-screen.
        Ui.Place(panel, "menu.main");

        var rows = PanelRows(panel, 28, 24, 9);
        var logo = new TextureRect
        {
            Texture = Art("brand_logo_banner", "res://game/gfx/interface/brand_logo_banner.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(0, 184),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TooltipText = Tr("Deal or Force")
        };
        rows.AddChild(logo);
        rows.AddChild(Title(
            "The world as it stands in 2024 — its borders, its people and its economy.",
            15,
            Ui.Colour("text.muted")));

        rows.AddChild(Title("1 January 2024", 17, Ui.Colour("text.bright")));
        _scenarioSummary = Title("Preparing campaign data…", 14, Ui.Colour("text.hint"));
        rows.AddChild(_scenarioSummary);

        _modStatus = Title(_modLoader.StatusLine, 12, Ui.Colour("text.faint"));
        _modStatus.TooltipText = Tr("Development mods: res://mods · installed mods: user://mods");
        rows.AddChild(_modStatus);

        rows.AddChild(ActionButton("Singleplayer", OpenCountryPicker, 330, 46, true));
        rows.AddChild(ActionButton("Load latest campaign", LoadLatestSave, 330, 42));

        var multiplayer = ActionButton("Multiplayer · LAN / IP", OpenNetworkMultiplayer, 330, 42);
        multiplayer.TooltipText = Tr("Host or join an authoritative ENet campaign over LAN, direct IPv4 or a virtual LAN such as Radmin VPN.");
        rows.AddChild(multiplayer);

        _mainProjectionButton = ActionButton("Map view: 3D globe", ToggleProjection, 330, 42);
        _mainProjectionButton.TooltipText = Tr("Switch between globe and classic infinitely wrapping map (F2).");
        rows.AddChild(_mainProjectionButton);
        _smoothingButton = ActionButton(SmoothingButtonLabel(), ToggleMapSmoothing, 330, 42);
        _smoothingButton.TooltipText =
            Tr("Smoothing rebuilds country, region and terrain boundaries so the map has no visible " +
            "texel grid. Turn it off for the raster exactly as authored - pixels and all - which " +
            "is also the cheaper picture on a slower machine (F3).");
        rows.AddChild(_smoothingButton);
        _languageButton = ActionButton(LanguageButtonLabel(), CycleLanguage, 330, 42);
        _languageButton.TooltipText = Tr("Cycle the interface language. Mods add their own by dropping localization/<language>.json into the mod folder.");
        rows.AddChild(_languageButton);
        rows.AddChild(ActionButton("Quit to desktop", () => GetTree().Quit(), 330, 42));

        _navigationHint = Title("Right-drag to turn the globe · wheel to zoom · WASD to move.", 13,
            Ui.Colour("text.hint"));
        _navigationHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_navigationHint);
    }

    private void BuildHud()
    {
        _hudLayer = FullLayer("GameHud");
        _hudLayer.Visible = false;
        AddChild(_hudLayer);

        var top = TexturedPanel(_hudLayer);
        Ui.Place(top, "hud.top");

        var margin = Margin(top, 18, 12);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 24);
        margin.AddChild(row);

        // Whose war this is, said in the one way that needs no reading.
        _identityFlag = FlagBadge(_campaign?.Player?.Tag ?? "", 34);
        row.AddChild(_identityFlag);

        var identityBox = new VBoxContainer { CustomMinimumSize = new Vector2(196, 0) };
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
        _balance = Stat(stats, "BALANCE", "What the treasury gains or loses each week, and everything that goes into it.",
            () => _campaign?.Player?.Budget);
        _gdp = Stat(stats, "GDP", "Annual gross domestic product, as it emerges from what your economy actually produces.");
        _population = Stat(stats, "POPULATION", "Everyone living in your states, and how that has moved over the last week.");
        _stability = Stat(stats, "STABILITY", "How settled the country is. Unrest costs money to police and output to bear.",
            () => _campaign?.Player?.StabilityDrivers);
        _manpower = Stat(stats, "MANPOWER", "People left to put in uniform. A war that outruns this is a war that ends.");

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
        menu.TooltipText = Tr("Pause menu (Esc) · F5 quick-saves, F9 loads, F10 flies the camera");
        speeds.AddChild(menu);
        _pause = ActionButton("II", TogglePause, 44, 34, true);
        _pause.TooltipText = Tr("Pause and resume campaign time (Space). Space never activates a focused button.");
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

        var navigation = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        Ui.Place(navigation, "hud.navigation");
        var navMargin = Margin(navigation, 10, 8);
        var nav = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        nav.AddThemeConstantOverride("separation", 8);
        navMargin.AddChild(nav);

        // Each screen says what it is for. A player who has never opened one of these games
        // should be able to learn the interface by resting on it rather than by clicking
        // everything to find out.
        var screens = new (string Name, string Icon, string Help)[]
        {
            ("Nation", "nation", "Who you are: capital, people, output, treasury and how settled the country is."),
            ("Economy", "economy", "The budget: what the week raises, what it costs, and what the region you have selected is made of."),
            ("Military", "military", "The armed forces: how many, how ready, how well fed, and what spending buys."),
            ("Diplomacy", "diplomacy", "How the selected country regards you, and what can be done about it."),
            ("Wars", "wars", "Every war being fought, what it is costing, and the table where it can be ended."),
            ("Decisions", "decisions", "One-off national actions, with their price and their consequence."),
            ("State Affairs", "state_affairs", "Running the country: taxation, the armed forces budget, " +
                "the cabinet, welfare and education, trade policy, and what is being built."),
            ("Ledger", "ledger", "The record, for any country: charts, history and rankings. Nothing here " +
                "changes anything - the controls moved to State Affairs.")
        };
        _screenButtons.Clear();
        foreach (var (screen, icon, help) in screens)
        {
            var captured = screen;
            var button = ActionButton(screen, () => OpenScreen(captured), 118, 38,
                false, SpriteCatalog.TextureOf($"icon_{icon}",
                    $"res://game/gfx/interface/icons/icon_{icon}.png"));
            button.TooltipText = help;
            _screenButtons[captured] = button;
            nav.AddChild(button);
        }
        UpdateScreenButtons();

        _sidePanel = TexturedPanel(_hudLayer);
        Ui.Place(_sidePanel, "hud.side_panel");
        _sidePanel.Visible = false;

        var sideRows = ScrollablePanelRows(_sidePanel, 24, 20, 12);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        sideRows.AddChild(header);
        _sideFlag = FlagBadge("", 26);
        _sideFlag.Visible = false;
        header.AddChild(_sideFlag);
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

        // Where to go next from whatever is being read.
        //
        // A number on this panel is almost always a number somebody wants to do something
        // about, and until now doing something about it meant knowing which of eight buttons
        // at the top of the screen owned it. These are the same destinations, put beside the
        // figures that lead to them.
        _sideLinks = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sideLinks.AddThemeConstantOverride("h_separation", 5);
        _sideLinks.AddThemeConstantOverride("v_separation", 4);
        sideRows.AddChild(_sideLinks);

        
        BuildCountryActionControls(sideRows);
        BuildRegionInteractionControls(sideRows);
        BuildEconomyControls(sideRows);
        BuildDecisionControls(sideRows);
        BuildBattleDetailControls(sideRows);

        _militarySpending = new HSlider
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.01,
            TooltipText = Tr("Military allocation; submitted through the deterministic command queue.")
        };
        _militarySpending.ValueChanged += OnSpendingChanged;
        sideRows.AddChild(_militarySpending);

        // A bare slider tells the player nothing about what they just set. The readout
        // below it names the figure and what it costs, and hides with the slider.
        _militarySpendingReadout = Title("", 12, Ui.Colour("text.caption"));
        _militarySpendingReadout.Visible = false;
        sideRows.AddChild(_militarySpendingReadout);

        BuildArmyPanel();
        BuildFleetPanel(_hudLayer);
        BuildPeaceConference();
        BuildLedgerInterface();
        BuildStateAffairsInterface();
        BuildWarLedger();
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
        _pause.Text = Tr("▶");
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
        _pause.Text = Tr("▶");
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
        if (screen == "State Affairs")
        {
            OpenStateAffairs();
            return;
        }
        // Wars are a screen of their own now, not a paragraph per war in the side panel.
        if (screen == "Wars")
        {
            OpenWarLedger();
            UpdateScreenButtons();
            return;
        }
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_warLedgerLayer is not null) _warLedgerLayer.Visible = false;
        _openScreen = screen;
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = false;
        HideRegionInteractionControls();
        _economyControls.Visible = screen == "Economy";
        _decisionControls.Visible = screen == "Decisions";
        _battleDetailControls.Visible = false;
        _militarySpending.Visible = screen == "Military";
        _militarySpendingReadout.Visible = screen == "Military";
        UpdateScreenButtons();

        var player = _campaign.Player;
        var nation = _selectedNation ?? player;
        _sideTitle.Text = screen;
        ShowSideFlag(screen is "Nation" or "Diplomacy" or "Economy" or "Military" ? nation.Tag : "");
        ClearDetail();
        RefreshSideLinks(screen);

        switch (screen)
        {
            case "Nation":
                _sideBody.Text = Simulation.Loc.Format("{0} ({1}) · {2}, {3}", nation.Name, nation.Tag, nation.Region, nation.Continent);
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
                DetailHeading("GOVERNMENT");
                DetailRow("Form of government", Tr(PoliticsCore.GovernmentOf(nation).Name),
                    Tr(PoliticsCore.GovernmentOf(nation).Description));
                DetailRow("Ideology", Tr(PoliticsCore.IdeologyOf(nation).Name),
                    Tr(PoliticsCore.IdeologyOf(nation).Description));
                if (nation != _campaign.Player)
                    DetailRow("Politics", PoliticsCore.Affinity(_campaign, _campaign.Player.Id, nation.Id) >= 0f
                            ? Tr("close to ours") : Tr("unlike ours"),
                        Tr("How alike two governments are moves what they make of each other, every week, " +
                           "whether or not anything has ever happened between them."),
                        PoliticsCore.Affinity(_campaign, _campaign.Player.Id, nation.Id) >= 0f
                            ? "state.positive" : "state.negative");
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
                _sideBody.Text = Simulation.Loc.Format("{0} armed forces", player.Name);

                DetailHeading("FORCES");
                _sideDetail.AddChild(ActionButton("Recruit & deploy", () => OpenRecruitment(), 180, 38, true));
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
        _battleDetailControls.Visible = false;
        _militarySpending.Visible = false;
        _sideTitle.Text = pick.Nation.Name;
        ShowSideFlag(pick.Nation.Tag);
        var state = pick.State is null ? "Unmapped land" : $"{pick.State.Name} ({pick.State.Kind})";
        var province = pick.ProvinceId > 0 ? $"Tactical province #{pick.ProvinceId:N0}" : "Province topology loading";
        ShowCountryInteraction(pick.Nation, $"{state} · {province}");
        if (pick.State is not null) ShowRegionOverview();
    }

    private Control _freeCameraOverlay = null!;
    private Label _freeCameraReadout = null!;
    private bool _freeCameraReadoutVisible = true;

    /// <summary>
    /// One line in the corner saying what the flying camera is doing.
    ///
    /// A cinematic camera has more state than a player can hold in their head - a lens, a
    /// roll, a turntable that may or may not be turning itself - and a shot ruined by not
    /// knowing which of those was on is a shot taken twice. H hides it, because the one
    /// thing that must not be in the footage is the readout.
    /// </summary>
    private void BuildFreeCameraOverlay()
    {
        _freeCameraOverlay = FullLayer("FreeCameraOverlay");
        _freeCameraOverlay.Visible = false;
        AddChild(_freeCameraOverlay);

        _freeCameraReadout = Title("", 13, Ui.Colour("text.hint"));
        _freeCameraReadout.AutowrapMode = TextServer.AutowrapMode.Off;
        _freeCameraReadout.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        _freeCameraReadout.Position = new Vector2(22, -46);
        _freeCameraOverlay.AddChild(_freeCameraReadout);
    }

    private void RefreshFreeCameraReadout()
    {
        if (_freeCameraOverlay is null || _globe is null) return;
        var flying = _globe.FreeCamera;
        _freeCameraOverlay.Visible = flying && _freeCameraReadoutVisible;
        if (!_freeCameraOverlay.Visible) return;

        _freeCameraReadout.Text = Simulation.Loc.Format(
            "{0} · lens {1:0}° · roll {2:0}° · {3} · {4}\n" +
            "QE roll · R level · ZX lens · F lens back · G turntable · T self-turning · C smoothing · H hide",
            _globe.FreeOrbiting ? Tr("turntable") : Tr("free flight"),
            _globe.FreeFieldOfView,
            Mathf.RadToDeg(_globe.FreeRoll),
            _globe.FreeSmoothing ? Tr("smoothed") : Tr("raw"),
            _globe.FreeAutoOrbit > 0.0001f ? Tr("turning itself") : Tr("still"));
    }

    /// <summary>Take the camera off its rig, or put it back and restore the interface.</summary>
    private void ToggleFreeCamera()
    {
        if (_globe is null) return;
        var enabled = !_globe.FreeCamera;
        _freeCameraMode = enabled;
        _globe.SetFreeCamera(enabled);
        RefreshFreeCameraReadout();
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
            _networkHudStatus.Text = Tr("Only the host controls campaign time.");
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
            _networkHudStatus.Text = Tr("Only the host controls campaign speed.");
            return;
        }
        _speed = Mathf.Clamp(speed, 1, 5);
        if (_campaign.IsPaused)
        {
            _campaign.IsPaused = false;
            _pause.Text = Tr("II");
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
        _identity.Text = Simulation.Loc.Format("{0} ({1})", player.Name, player.Tag);
        _identityTag.Text = _observerMode ? "OBS · observer mode" : $"{player.Tag} · player nation";
        SetFlag(_identityFlag, player.Tag, 34);
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

        // A stock without its flow is half a number: a treasury that reads the same two
        // weeks running is a very different country depending on which way it is heading.
        var balanced = player.Budget.HasEntries;
        WriteStat(_balance,
            balanced ? Fmt.MoneyDelta(player.WeeklyBalance) : "—",
            balanced ? $"{Fmt.MoneyExact(player.WeeklyBalance)} a week" : "The first economic week has not closed yet.",
            balanced ? "a week" : "", balanced ? Fmt.Direction(player.WeeklyBalance, 0.005d) : 0);
        WriteStat(_stability, $"{player.Stability:P0}", $"Stability {player.Stability:P1}",
            player.WarSupport > 0f ? $"war support {player.WarSupport:P0}" : "", 0);
        WriteStat(_manpower, Fmt.People(player.ManpowerReserve), Fmt.PeopleExact((long)player.ManpowerReserve),
            $"{Fmt.People(player.ReinforcementCapacity)}/day", 0);

        _militarySpending.SetValueNoSignal(player.MilitarySpending);
        UpdateSpendingReadout(player);
        UpdateSpeedButtons();
        UpdateScreenButtons();
        RefreshStrategicAudio();
        RefreshEventInterface();
        RefreshPeaceOfferInterface();
        RefreshStrategicHud();
        if (_ledgerLayer is { Visible: true }) RefreshLedger();
        RefreshRecruitment();
        if (_stateAffairsLayer is { Visible: true }) RefreshStateAffairs();
        if (_warLedgerLayer is { Visible: true }) RefreshWarLedger();
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

    private StatReadout Stat(Container parent, string caption, string tooltip,
        System.Func<Simulation.Breakdown> explain = null)
    {
        // A figure that can be hovered for the parts it is made of, when there are parts to
        // show; an ordinary tooltip when there are not.
        var box = new ExplainedStat
        {
            CustomMinimumSize = new Vector2(126, 0),
            TooltipText = tooltip,
            Explain = explain,
            Plain = explain is null ? "" : tooltip
        };
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
    private void DetailRow(string caption, string value, string tooltip = null, string valueToken = "text.bright",
        System.Func<Simulation.Breakdown> explain = null)
    {
        // Every screen in the game builds its rows here, so a breakdown offered here is a
        // breakdown offered everywhere: the figure on the right, and what it is made of
        // under the cursor.
        var row = new ExplainedRow
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Explain = explain,
            Plain = tooltip ?? ""
        };
        if (!string.IsNullOrEmpty(tooltip) || explain is not null)
        {
            row.TooltipText = tooltip ?? caption;
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
            Simulation.Loc.Format("{0} of output · about {1} a year", Fmt.Percent(player.MilitarySpending), Fmt.Money(annual));
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
        var open =
            _stateAffairsLayer is { Visible: true } ? "State Affairs" :
            _ledgerLayer is { Visible: true } ? "Ledger" :
            _sidePanel is { Visible: true } ? _openScreen : "";
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

    private enum PanelSurface
    {
        Dossier,
        Command,
        // Horizontal HUD bars use the same artwork as command panels, but need a
        // much smaller nine-slice border. The full 48 px frame cannot fit into a
        // 44-84 px strip and collapses its centre/background.
        CommandStrip
    }

    private PanelContainer TexturedPanel(Node parent, PanelSurface surface = PanelSurface.Dossier)
    {
        var panel = new PanelContainer
        {
            // A panel is a hard layout boundary. This is deliberately enabled in
            // addition to the safe content inset below: if a future mod or dynamic
            // label grows unexpectedly, it can scroll/wrap inside its window but it
            // cannot paint over the map outside the ornamental frame.
            ClipContents = true
        };
        panel.AddThemeStyleboxOverride("panel", PanelStyle(surface));
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

    /// <summary>
    /// Set the interface in whatever face `fonts/` declares, if it declares one.
    ///
    /// Nothing is declared by default and the engine's own face is used. A theme override
    /// on the root Control reaches every label and button under it, which is the whole
    /// interface, so one declaration is enough to make a script the game does not ship
    /// support legible.
    /// </summary>
    private void ApplyDeclaredFont()
    {
        var file = Simulation.FontCatalog.FileOf("ui.default");
        if (string.IsNullOrEmpty(file)) return;
        if (!ResourceLoader.Exists(file))
        {
            GD.PushWarning($"[dof] fonts declare ui.default as {file}, which is not there");
            return;
        }
        var face = GD.Load<Font>(file);
        if (face is not null) AddThemeFontOverride("font", face);
    }

    /// <summary>
    /// The picture the game means by a name. The path the interface was written against is
    /// passed as the fallback, so a mod that removes a declaration loses its own override
    /// rather than leaving the player with an empty frame.
    /// </summary>
    private static Texture2D Art(string sprite, string shipped) =>
        GD.Load<Texture2D>(SpriteCatalog.TextureOf(sprite, shipped));

    private Button ActionButton(string text, System.Action callback, float width, float height,
        bool primary = false, string iconPath = "")
    {
        var button = new Button
        {
            Text = Tr(text),
            CustomMinimumSize = new Vector2(width, height)
        };
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            button.Icon = GD.Load<Texture2D>(iconPath);
            button.ExpandIcon = true;
            // The art is stored far larger than any button draws it - a picture that is
            // downsampled looks right, and one that is stretched does not - so the button
            // has to be told how much room the icon may have. Without this the whole 256
            // pixels arrive and the caption is pushed off the screen, which is exactly what
            // happened the first time the icons stopped being vectors.
            var room = (int)Mathf.Clamp(height * 0.62f, 14f, 28f);
            button.AddThemeConstantOverride("icon_max_width", room);
        }
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
    private string SmoothingButtonLabel() =>
        $"Map smoothing: {(_globe is null || _globe.MapSmoothing ? "on" : "off")}";

    private void ToggleMapSmoothing()
    {
        if (_globe is null) return;
        _globe.MapSmoothing = !_globe.MapSmoothing;
        if (_smoothingButton is not null && GodotObject.IsInstanceValid(_smoothingButton))
        {
            // Written straight onto the button rather than through the localisation sweep,
            // for the same reason the language button is: the caption is built from a state
            // rather than from a source string, so a generic refresh would overwrite it.
            var label = Tr(SmoothingButtonLabel());
            _smoothingButton.Text = label;
            _smoothingButton.SetMeta(LocAppliedMeta, label);
        }
    }

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

    private StyleBox PanelStyle(PanelSurface surface)
    {
        var command = surface != PanelSurface.Dossier;
        var strip = surface == PanelSurface.CommandStrip;
        var textureMargin = strip ? 20f : command ? 48f : 28f;
        // The tall command artwork draws its visible inner rail about 30 px from
        // the outer edge. Keep controls on the inside of that rail. Callers add a
        // further 8-16 px semantic margin, so 22 px lands text and buttons safely
        // inside the frame without sacrificing the useful width of narrow panels.
        var contentHorizontal = strip ? 6f : command ? 28f : 6f;
        var contentVertical = strip ? 4f : command ? 22f : 4f;
        return new StyleBoxTexture
        {
            Texture = strip
                ? Art("panel_command_strip", "res://game/gfx/interface/panels/panel_command_strip.png")
                : command
                    ? Art("panel_command_frame", "res://game/gfx/interface/panels/panel_command_frame.png")
                    : Art("panel_dossier", "res://game/gfx/interface/panels/panel_dossier.png"),
            TextureMarginLeft = textureMargin,
            TextureMarginTop = textureMargin,
            TextureMarginRight = textureMargin,
            TextureMarginBottom = textureMargin,
            // Texture margins describe the nine-slice artwork, not layout padding.
            // Letting Godot derive content margins from them adds 56/96 px to a panel's
            // minimum size, while zero on every surface makes shallow frames collapse.
            // These small explicit values keep the visual frame intact; callers still
            // own the semantic padding through MarginContainer.
            ContentMarginLeft = contentHorizontal,
            ContentMarginTop = contentVertical,
            ContentMarginRight = contentHorizontal,
            ContentMarginBottom = contentVertical,
            ModulateColor = command
                ? new Color(0.88f, 0.92f, 0.98f, 0.985f)
                : new Color(0.9f, 0.94f, 1f, 0.98f)
        };
    }
}
