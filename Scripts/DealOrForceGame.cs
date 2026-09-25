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
    private StatReadout _inflation = null!;
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

    public override async void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // A campaign's own options are read from whichever campaign is on screen now - asked
        // each time, so a loaded save or a new scenario is never answered for by the last one.
        Simulation.SettingsCatalog.CurrentCampaign = () => _campaign;
        await RunLoadingOperation("Preparing the world", async () =>
        {
            StartDiagnostics();
            // The engine-neutral settings layer is told where the player's files live.
            Simulation.PlayerSettings.UseDirectory(ProjectSettings.GlobalizePath("user://"));
            await LoadingPhase("Loading interface and content catalogs", 10);
            // Resolve/migrate old saves only after the first loading frame is visible.
            GD.Print($"[dof] saves: {SaveRoot}");
            Simulation.SpriteCatalog.Load();
            Simulation.SoundCatalog.Load();
            Simulation.MusicCatalog.Load();
            Simulation.FontCatalog.Load();
            ApplyDeclaredFont();
            await LoadingPhase("Preparing the interface", 20);
            BuildInterface();
            _globe.LoadingInputBlocked = true;
            await LoadingPhase("Creating countries and regions", 30);
            StartCampaign();
            await WaitForMapPresentation();
            // Again, now that there is a map to apply them to. Several preferences reach
            // into the globe and the globe refuses them while it is still being built -
            // the projection is the visible one, because SetFlatMap gives up if the camera
            // is not there yet and says nothing about having done so.
            ApplyAllSettings();
            _startupComplete = true;
        });

        // And then go where the player asked to go. After the loading operation, not inside
        // BuildInterface: the intent can mean "load the last save", and a save cannot be
        // loaded into a world that has not been created yet - which is exactly what happened
        // the first time this was called from the interface pass.
        OpenWhateverWasAskedFor();
    }

    public override void _Process(double delta)
    {
        if (_isLoading || !_startupComplete) return;
        // Before any of the early returns below it: a panel is read while the game is
        // paused, and a caption squeezed to nothing on a paused screen is still squeezed.
        LayoutAudit.Watch(this, delta);
        if (_globe is not null) _globe.FogOfWarActive = _campaign is not null && _menuLayer is { Visible: false } && _pickerLayer is { Visible: false };
        if (_globe is not null) _globe.MilitaryMarkersEnabled = _menuLayer is { Visible: false };
        // The lens and the roll move while a key is held, so the line that reports them
        // has to be written every frame rather than when something is pressed.
        if (_freeCameraMode) RefreshFreeCameraReadout();
        NetworkProcess(delta);
        // Everything that happened since the interface last drew, drawn once - for a network
        // client as well, whose world arrives by the host's steps rather than its own.
        if (_campaign is not null) FlushInterfaceIfDue(delta);
        if (IsNetworkClient) { if (_campaign is not null) ReportFrameCost(delta); return; }
        if (_campaign is null) return;
        // Reported whether or not time is running: a measurement that only arrives when
        // everything is already going well is no use for finding out why it is not.
        ReportFrameCost(delta);
        if (_campaign.IsPaused) return;

        _dayAccumulator += delta;
        // Real seconds one step of the simulation is worth. The speed setting is still in
        // days, because that is what a player thinks in; a campaign stepping six hours at a
        // time simply takes four steps to spend one day's worth of them.
        var secondsPerDay = _campaign.Defines.SecondsForSpeed(_speed) * _campaign.TickScale;
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
            // A hook called "on day advanced" is called when a day advances. At the shipped
            // hourly step a call per step would fire it twenty-four times a day, which would
            // silently multiply by twenty-four whatever every mod in the world does in it.
            if (_campaign.StepOpensNewDay) _modLoader.OnDayAdvanced(_campaign);
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
        if (IsInstanceValid(_authorCanvas))
        {
            // Escape takes the window's OWN way out, which is not always "close".
            //
            // It used to close unconditionally, and opened to choose a country there is no
            // campaign behind the window - so escape took the window away and left a globe
            // with no interface on it, which is exactly the bug it looked like. See
            // `AuthorWindow`: the window is told where its exit leads when it is opened.
            if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            { EscapeAuthorWindow(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (_isLoading || !_startupComplete)
        {
            // Tab/hotkeys must not move focus to the covered game. Only the error
            // action may receive keyboard activation; mouse delivery stays with the cover.
            if (inputEvent is InputEventKey loadingKey &&
                !(_loadingScreen is { CanDismiss: true } && loadingKey.Keycode is Key.Enter or Key.Space))
                GetViewport().SetInputAsHandled();
            return;
        }
        // Space is a sacred strategy-game time key: it never activates a focused
        // button, confirms a dialog or changes projection. During a campaign it
        // only toggles time, and outside a campaign it is deliberately consumed.
        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        // Text entry owns letters and space; Escape first leaves the field.
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)
        {
            if (key.Keycode == Key.Escape)
            {
                GetViewport().GuiGetFocusOwner().ReleaseFocus();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

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

        // Watching rather than playing. F7 rather than F8 for the same reason F10 is not F8:
        // the editor takes that one to stop the running project.
        if (key.Keycode == Key.F7) { ToggleObserverMode(); GetViewport().SetInputAsHandled(); return; }

        // Everything the flying camera does that is a switch rather than a movement. They
        // are read here rather than polled with the rest because holding G down should not
        // put the camera on and off a turntable sixty times a second - and they are guarded
        // on the camera being up, so none of these letters mean anything to a campaign.
        if (_globe is { FreeCamera: true })
        {
            switch (Letter(key))
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
            SetArmyFeedback(Tr("The line was abandoned."));
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

    /// <summary>
    /// Which key was pressed, by where it is rather than by what it types.
    ///
    /// `Keycode` is the letter the current layout puts on the key, so with a Russian layout
    /// the key marked H types Р and every letter hotkey here - halt, the free camera, the
    /// plan shortcuts - silently did nothing for anybody playing in Russian. The physical
    /// code is the key's place on the board, which is what a hotkey means and what HOI4 uses.
    /// </summary>
    internal static Key Letter(InputEventKey key) =>
        key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;

    /// <summary>The same question for a key being held: its place on the board, whatever the layout.</summary>
    internal static bool Held(Key key) => Input.IsPhysicalKeyPressed(key) || Input.IsKeyPressed(key);

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        // Escape while the workshop is up is answered in `_Input`, which runs first - see
        // there. Nothing else in the campaign hears a key while that window covers it.
        if (IsInstanceValid(_authorCanvas)) return;
        if (_isLoading || !_startupComplete) return;
        if (inputEvent is InputEventKey && GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
        if (_developerConsoleLayer is { Visible: true }) return;
        HandleProjectionHotkey(inputEvent);
        if (GetViewport().IsInputHandled()) return;

        // Halt, and come off the plan. Two keys because they are two decisions - see
        // HaltSelectedDivisions for what one key cost. Only while formations are selected
        // and only in the campaign, so H stays a letter everywhere else.
        if (inputEvent is InputEventKey { Pressed: true, Echo: false } key &&
            _hudLayer is { Visible: true } && _selectedArmies.Count > 0)
        {
            switch (Letter(key))
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
        else if (MapSelectionShowing)
            CloseMapSelection();
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

        // Every preference that reaches into the campaign, now that there is one.
        //
        // ApplyAllSettings runs once as the game comes up, and at that moment there is no
        // campaign for it to reach into - so every setting written as "if there is a
        // campaign, set this on it" was quietly doing nothing, and a fresh campaign got the
        // compiled defaults however the player had set the game up. The step length was the
        // visible half of that: choosing six hours changed nothing at all, because the
        // campaign that was about to be created had not been told.
        ApplyAllSettings();
        BindFlags(_campaign);
        Simulation.SpriteCatalog.Changed += ForgetFlags;
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
        _globe.UnclaimedPicked += OnUnclaimedPicked;
        _globe.ArmyPicked += OnArmyPicked;
        _globe.FleetPicked += OnFleetPicked;
        _globe.BattlePicked += ShowBattleDetails;
        _globe.ArmiesBoxPicked += OnArmiesBoxPicked;
        _globe.ProvinceCommanded += OnProvinceCommanded;
        AddChild(_globe);

        BuildMainMenu();
        BuildCountryPicker();
        BuildHud();
        BuildMovementPanel();
        BuildHotseatInterface();
        BuildNetworkMultiplayerInterface();
        BuildPauseMenu();
        BuildSaveLoadInterface();
        BuildEventInterface();
        BuildPeaceOfferInterface();
        BuildStrategicAudio();
        BuildDeveloperConsole();
        BuildFreeCameraOverlay();
        BuildObserverHint();
        BuildSettingsInterface();

        // Last, because it reaches into everything the lines above have just built. From
        // here on the screen and the running game cannot disagree: both go through
        // ApplySetting, and this is the same call the screen makes when a row changes.
        ApplyAllSettings();
    }

    /// <summary>
    /// Do the thing the front door was pressed for, once, on the way up.
    ///
    /// The intent is cleared as it is read: a campaign that later returns to the menu and
    /// comes back must be told again what it is for, and a stale "load the last save" is the
    /// kind of bug that only shows up on the second game of a session.
    /// </summary>
    private void OpenWhateverWasAskedFor()
    {
        var wanted = MainMenuScene.Wanted;
        MainMenuScene.Wanted = MenuIntent.None;
        _menuLayer.Visible = false;

        switch (wanted)
        {
            case MenuIntent.NewCampaign: OpenScenarioLibrary(); break;
            case MenuIntent.LoadLatest: LoadLatestSave(); break;
            case MenuIntent.LoadSlot:
                var slot = MainMenuScene.WantedSlot;
                MainMenuScene.WantedSlot = "";
                if (string.IsNullOrWhiteSpace(slot)) LoadLatestSave();
                else LoadFromSlot(slot);
                break;
            case MenuIntent.Multiplayer: OpenNetworkMultiplayer(); break;
            case MenuIntent.Settings: ShowSettings(); break;
            // Opened directly - from the editor, or by a scene reload - so there is nothing
            // to act on. Only the layer is shown, not the whole hide-everything routine:
            // that one pauses the campaign, and at this point in the boot there is not one.
            default: _menuLayer.Visible = true; break;
        }
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
            TooltipText = Ui.Tip(Tr("Deal or Force"))
        };
        rows.AddChild(logo);
        rows.AddChild(Title(
            "Choose a world. Shape its future.",
            15,
            Ui.Colour("text.muted")));

        // The heading that used to sit here said "Scenarios & workshop" over two status
        // lines about the world that is loaded and the mods that are not. It named a
        // section that is not on this screen - the workshop is a window reached from a
        // campaign - and a player reading the menu top to bottom was told they were
        // looking at something they were not. The two lines say what they are.
        _scenarioSummary = Title("Preparing campaign data…", 14, Ui.Colour("text.hint"));
        rows.AddChild(_scenarioSummary);

        _modStatus = Title(_modLoader.StatusLine, 12, Ui.Colour("text.faint"));
        _modStatus.TooltipText = Ui.Tip(Tr("Development mods: res://mods · installed mods: user://mods"));
        rows.AddChild(_modStatus);

        rows.AddChild(ActionButton("Singleplayer", OpenScenarioLibrary, 330, 46, true));
        rows.AddChild(ActionButton("Load latest campaign", LoadLatestSave, 330, 42));

        var multiplayer = ActionButton("Multiplayer · LAN / IP", OpenNetworkMultiplayer, 330, 42);
        multiplayer.TooltipText = Ui.Tip(Tr("Host or join an authoritative ENet campaign over LAN, direct IPv4 or a virtual LAN such as Radmin VPN."));
        rows.AddChild(multiplayer);

        _mainProjectionButton = ActionButton("Map view: 3D globe", ToggleProjection, 330, 42);
        _mainProjectionButton.TooltipText = Ui.Tip(Tr("Switch between globe and classic infinitely wrapping map (F2)."));
        rows.AddChild(_mainProjectionButton);
        _smoothingButton = ActionButton(SmoothingButtonLabel(), ToggleMapSmoothing, 330, 42);
        _smoothingButton.TooltipText = Ui.Tip(
            Tr("Smoothing rebuilds country, region and terrain boundaries so the map has no visible " +
            "texel grid. Turn it off for the raster exactly as authored - pixels and all - which " +
            "is also the cheaper picture on a slower machine (F3)."));
        rows.AddChild(_smoothingButton);
        rows.AddChild(ActionButton("Settings", ShowSettings, 330, 42));
        _languageButton = ActionButton(LanguageButtonLabel(), CycleLanguage, 330, 42);
        _languageButton.TooltipText = Ui.Tip(Tr("Cycle the interface language. Mods add their own by dropping localization/<language>.json into the mod folder."));
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
        top.Name = "CampaignTopBar";
        Ui.Place(top, "hud.top");

        var margin = Margin(top, 12, 6);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        margin.AddChild(row);

        // Whose war this is, said in the one way that needs no reading.
        _identityFlag = FlagBadge(_campaign?.Player?.Tag ?? "", 34);
        row.AddChild(_identityFlag);

        var identityBox = new VBoxContainer { CustomMinimumSize = new Vector2(154, 0) };
        identityBox.AddThemeConstantOverride("separation", 2);
        row.AddChild(identityBox);
        _identity = Title("South Korea", 18, Ui.Colour("text.accent"));
        _identity.ClipText = true;
        _identity.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        // The bar is two lines tall and no more. A name too wide for the column has to
        // widen the column, not wrap into the line underneath it - which is what
        // "South Korea" did the moment it became "Южная Корея".
        _identity.AutowrapMode = TextServer.AutowrapMode.Off;
        identityBox.AddChild(_identity);
        _identityTag = Title("KOR · player nation", 13, Ui.Colour("text.muted"));
        _identityTag.AutowrapMode = TextServer.AutowrapMode.Off;
        identityBox.AddChild(_identityTag);

        var stats = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        stats.AddThemeConstantOverride("separation", 12);
        row.AddChild(stats);
        foreach (var field in CampaignUiCatalog.TopStats)
            switch (field)
            {
                case "TREASURY": _treasury = Stat(stats, field, "Cash on hand. The line below is the change over the last week of campaign time."); break;
                case "BALANCE": _balance = Stat(stats, field, "What the treasury gains or loses each week, and everything that goes into it.", () => _campaign?.Player?.Budget); break;
                case "GDP": _gdp = Stat(stats, field, "Annual gross domestic product, as it emerges from what your economy actually produces."); break;
                case "POPULATION": _population = Stat(stats, field, "Everyone living in your states, and how that has moved over the last week."); break;
                case "STABILITY": _stability = Stat(stats, field, "How settled the country is. Unrest costs money to police and output to bear.", () => _campaign?.Player?.StabilityDrivers); break;
                case "MANPOWER": _manpower = Stat(stats, field, "People left to put in uniform. A war that outruns this is a war that ends."); break;
                // Hover for the reasons, the same as stability: a rate with nothing behind
                // it cannot answer the question a player actually asks about it.
                case "INFLATION": _inflation = Stat(stats, field, "How fast prices are rising. Caused by what the government spends, what the market cannot supply and what the war costs; answered with the policy rate in the budget.", () => _campaign?.Player?.InflationDrivers); break;
            }

        var clock = new VBoxContainer { CustomMinimumSize = new Vector2(224, 0) };
        clock.AddThemeConstantOverride("separation", 2);
        row.AddChild(clock);
        _date = Title("1 January 2024", 15, Ui.Colour("text.bright"));
        _date.HorizontalAlignment = HorizontalAlignment.Right;
        clock.AddChild(_date);

        var speeds = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        speeds.AddThemeConstantOverride("separation", 5);
        clock.AddChild(speeds);

        // Labelled rather than drawn. This was a hamburger glyph, and the one tester who
        // plays grand strategy for an audience reported that the game had no way out to the
        // main menu - it had two, Escape and this, and neither of them said so.
        var menu = ActionButton("Menu", ShowPauseMenu, 62, 28);
        menu.TooltipText = Ui.Tip(Tr("Pause menu (Esc): save, load, settings, and the way back to the main menu. F5 quick-saves, F9 loads, F10 flies the camera."));
        speeds.AddChild(menu);
        _pause = ActionButton("II", TogglePause, 32, 28, true);
        _pause.TooltipText = Ui.Tip(Tr("Pause and resume campaign time (Space). Space never activates a focused button."));
        speeds.AddChild(_pause);
        _speedButtons.Clear();
        for (var speed = 1; speed <= 5; speed++)
        {
            var captured = speed;
            var button = ActionButton(speed.ToString(), () => SetSpeed(captured), 27, 28);
            _speedButtons.Add(button);
            speeds.AddChild(button);
        }
        UpdateSpeedButtons();

        var navigation = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        navigation.Name = "CampaignNavigation";
        Ui.Place(navigation, "hud.navigation");
        var navMargin = Margin(navigation, 4, 6);
        var navScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        navMargin.AddChild(navScroll);
        var nav = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        nav.AddThemeConstantOverride("separation", 6);
        navScroll.AddChild(nav);

        // Each screen says what it is for. A player who has never opened one of these games
        // should be able to learn the interface by resting on it rather than by clicking
        // everything to find out.
        _screenButtons.Clear();
        foreach (var (screen, label, icon, help) in CampaignUiCatalog.NavigationItems)
        {
            var captured = screen;
            var navSize = Mathf.Clamp(InterfaceSkin.Metric("shell.navigation_size", 54), 28, 96);
            var button = ActionButton("", () => ToggleCampaignScreen(captured), navSize, navSize,
                false, SpriteCatalog.TextureOf($"icon_{icon}",
                    $"res://game/gfx/interface/icons/icon_{icon}.png"));
            button.Name = "Nav" + screen.Replace(" ", "");
            Tip(button, Tr(label) + "\n" + Tr(help));
            button.AccessibilityName = Tr(label);
            _screenButtons[captured] = button;
            nav.AddChild(button);
        }
        UpdateScreenButtons();

        _sidePanel = TexturedPanel(_hudLayer);
        _sidePanel.Name = "CampaignDossier";
        Ui.Place(_sidePanel, "hud.side_panel");
        _sidePanel.Visible = false;

        var dossierRows = PanelRows(_sidePanel, 16, 12, 10);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        dossierRows.AddChild(header);
        _sideFlag = FlagBadge("", 26);
        _sideFlag.Visible = false;
        header.AddChild(_sideFlag);
        _sideTitle = Title("Nation", 24, Ui.Colour("text.bright"));
        _sideTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _sideTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        _sideTitle.ClipText = true;
        _sideTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        header.AddChild(_sideTitle);
        header.AddChild(ActionButton("×", CloseSidePanel, 38, 34));

        _dossierTabs = new HBoxContainer();
        _dossierTabs.AddThemeConstantOverride("separation", 4);
        dossierRows.AddChild(_dossierTabs);
        _dossierScroll = new ScrollContainer
        {
            Name = "DossierScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FollowFocus = true
        };
        dossierRows.AddChild(_dossierScroll);
        var sideRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sideRows.AddThemeConstantOverride("separation", 10);
        _dossierScroll.AddChild(sideRows);

        _sideBody = Title("", 16, Ui.Colour("text.body"));
        _sideBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _sideBody.SizeFlagsVertical = SizeFlags.Fill;
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
            Scrollable = false,
            TooltipText = Ui.Tip(Tr("Military allocation; submitted through the deterministic command queue."))
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
        Resized += LayoutCampaignShell;
        LayoutCampaignShell();
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

    /// <summary>
    /// Leave the campaign and go back to the front door.
    ///
    /// One funnel, called by the pause menu, the lobby and the country picker's Back button,
    /// so there is one path out of a campaign rather than three that have to agree. The
    /// world is dropped with the scene, which is what "quit to menu" always meant and never
    /// used to do - the old menu was a panel over a world that carried on existing.
    /// </summary>
    private void ShowMainMenu()
    {
        MainMenuScene.ReturnTo(GetTree());
    }

    /// <summary>The panel the campaign scene shows when it is opened without an intent.</summary>
    private void ShowLegacyMenuPanel()
    {
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        if (_eventLayer is not null) _eventLayer.Visible = false;
        if (_campaign is not null) _campaign.IsPaused = true;
        if (_pause is not null) _pause.Text = Tr("▶");
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
        if (_stateAffairsLayer is not null) _stateAffairsLayer.Visible = false;
        if (_fleetPanel is not null) _fleetPanel.Visible = false;
        PrepareDossierTabs(screen);
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
        _sideTitle.Text = Tr(screen);
        ShowSideFlag(screen is "Economy" or "Military" ? player.Tag : screen is "Nation" or "Diplomacy" ? nation.Tag : "");
        ClearDetail();
        RefreshSideLinks(screen);

        switch (screen)
        {
            case "Nation":
                _sideBody.Text = Simulation.Loc.Format("{0} ({1}) · {2}, {3}", nation.DisplayName, nation.Tag,
            Tr(nation.Region), Tr(nation.Continent));
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
                _sideBody.Text = Simulation.Loc.Format("{0} armed forces", player.DisplayName);

                DetailHeading("FORCES");
                _sideDetail.AddChild(ActionButton("Recruit & deploy", () => OpenRecruitment(), 180, 38, true));
                DetailRow("Divisions", divisions.ToString("0.0"),
                    "Independent formations under your flag. Each takes frontage in a battle, so more of " +
                    "them is not the same as more strength in one province.");
                DetailRow("Field armies", armies.Length.ToString("N0"),
                    "Formations on the map, counted as units rather than as strength.");
                DetailRow("Fleets", Simulation.Loc.Format("{0:N0} · {1:N0} hulls",
                        fleets.Length, fleets.Sum(fleet => fleet.Hulls)),
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
        ApplyDossierTab();
    }

    private void CloseSidePanel()
    {
        _sidePanel.Visible = false;
        _globe.SetMapModeSubject(null);
        _openScreen = "";
        UpdateScreenButtons();
        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
    }

    private void OnCountryPicked(GlobePick pick)
    {
        if (_networkLayer is not null && _networkLayer.Visible)
        {
            SelectNetworkCountry(pick.Nation, false);
            return;
        }
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
        // The diplomatic and war layers now answer for the country that was picked.
        _globe.SetMapModeSubject(pick.Nation);

        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
        _openScreen = "";
        // The country on the left and the ground along the bottom, rather than the dossier:
        // see MapSelectionUI. The dossier is still a button away on either panel.
        _sidePanel.Visible = false;
        SetArmyPanelVisible(false);
        UpdateScreenButtons();
        ShowMapSelection(pick);
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
        PlayStrategicCue(_campaign.IsPaused ? "game_pause" : "game_resume");
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
            PlayStrategicCue("game_resume");
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
        _globe?.RefreshCountryNames();

        var player = _campaign.Player;
        _identity.Text = player.DisplayName;
        _identityTag.Text = _observerMode
            ? Tr("OBS · observer mode")
            : player.Tag + " · " + Tr("player nation");
        SetFlag(_identityFlag, player.Tag, 34);
        // The clock, whenever the campaign is running on something shorter than a day. At a
        // whole-day step the hour is always midnight and printing it would be noise; below
        // that it is the whole point, and it moves.
        _date.Text = _campaign.Date.Display(_campaign.TickHours < 24);

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
            balanced
                ? Simulation.Loc.Format("{0} a week", Fmt.MoneyExact(player.WeeklyBalance))
                : Tr("The first economic week has not closed yet."),
            balanced ? Tr("a week") : "", balanced ? Fmt.Direction(player.WeeklyBalance, 0.005d) : 0);
        WriteStat(_stability, Fmt.Percent(player.Stability),
            Simulation.Loc.Format("Stability {0:P1}", player.Stability),
            player.WarSupport > 0f
                ? Simulation.Loc.Format("war support {0}", Fmt.Percent(player.WarSupport))
                : "", 0);
        WriteStat(_manpower, Fmt.People(player.ManpowerReserve), Fmt.PeopleExact((long)player.ManpowerReserve),
            Simulation.Loc.Format("{0}/day", Fmt.People(player.ReinforcementCapacity)), 0);

        // Rising prices are bad news, so the arrow points the other way from every other
        // stat on this bar: up is not good here.
        if (_inflation is not null)
        {
            var comfort = Simulation.InflationCore.Weights.Comfort;
            var rate = player.Inflation;
            WriteStat(_inflation, rate.ToString("P1"),
                Simulation.Loc.Format("Prices are rising at {0} a year", rate.ToString("P2")),
                rate < 0f ? Tr("prices falling")
                    : rate > comfort ? Tr("above the comfortable rate")
                    : Tr("comfortable"),
                rate > comfort ? -1 : rate < 0f ? -1 : 0);
        }
        WriteMovementTopBar(player);

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
        else if (_sidePanel.Visible && _openScreen is "Wars" or "Military" or "Economy" or "Nation" or "Decisions")
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


    /// <summary>
    /// Which symbol stands for a figure on the top bar.
    ///
    /// Kept beside the bar rather than in the data because the seven names here are the
    /// seven the code itself switches on a few lines further down - a mod that wants an
    /// eighth figure is adding a case there too, and a name with no symbol simply keeps its
    /// caption rather than showing nothing.
    /// </summary>
    private static string StatIcon(string caption) => caption switch
    {
        "TREASURY" => "treasury",
        "BALANCE" => "balance",
        "GDP" => "gdp",
        "INFLATION" => "inflation",
        "POPULATION" => "population",
        "STABILITY" => "stability",
        "MANPOWER" => "manpower",
        _ => ""
    };

    private StatReadout Stat(Container parent, string caption, string tooltip,
        System.Func<Simulation.Breakdown> explain = null)
    {
        // A figure that can be hovered for the parts it is made of, when there are parts to
        // show; an ordinary tooltip when there are not.
        var box = new ExplainedStat
        {
            CustomMinimumSize = new Vector2(84, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Explain = explain,
            Plain = explain is null ? "" : Tr(tooltip)
        };
        // The caption is in the tooltip now rather than gone: a symbol a player has not
        // learned yet has to be able to say what it is.
        Tip(box, Tr(caption) + " \u00b7 " + Tr(tooltip));
        box.AddThemeConstantOverride("separation", 1);
        box.MouseFilter = MouseFilterEnum.Stop;
        parent.AddChild(box);
        // The symbol where the word was. A stat with no icon declared keeps its caption, so
        // a mod that adds an eighth figure to `top_stats` is not obliged to draw for it.
        var symbol = StatIcon(caption);
        if (symbol.Length > 0 && SpriteCatalog.Has("icon_" + symbol))
        {
            var glyph = new TextureRect
            {
                Texture = Art("icon_" + symbol, $"res://game/gfx/interface/icons/icon_{symbol}.png"),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                // Both dimensions. A minimum height with a width of zero is a rect with no
                // width, which is a texture nobody ever sees - and the alert rail's icons
                // rendered fine the whole time, which is what said it was the size and not
                // the sprite.
                CustomMinimumSize = new Vector2(16, 16),
                MouseFilter = MouseFilterEnum.Ignore,
                Modulate = Ui.Colour("text.accent_dim")
            };
            glyph.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            box.AddChild(glyph);
        }
        else
        {
            var statCaption = Title(caption, 11, Ui.Colour("text.caption"));
            statCaption.AutowrapMode = TextServer.AutowrapMode.Off;
            statCaption.ClipText = true;
            box.AddChild(statCaption);
        }

        var readout = new StatReadout
        {
            Value = Title("—", 17, Ui.Colour("text.bright")),
            Delta = Title("", 11, Ui.Colour("text.faint"))
        };
        // Neither of these may wrap. The bar is one row tall and the column is sized to the
        // widest thing in it, so a line that wraps grows downwards out of the bar instead of
        // widening its column - which is what "war support 63 %" did the moment it became
        // "поддержка войны 63 %".
        readout.Value.AutowrapMode = TextServer.AutowrapMode.Off;
        readout.Delta.AutowrapMode = TextServer.AutowrapMode.Off;
        readout.Value.ClipText = true;
        readout.Delta.ClipText = true;
        readout.Delta.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
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
        stat.Value.TooltipText = Ui.Tip(exact);
        stat.Value.MouseFilter = MouseFilterEnum.Stop;
        stat.Delta.Text = delta;
        stat.Delta.TooltipText = Ui.Tip(delta);
        stat.Delta.MouseFilter = MouseFilterEnum.Stop;
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
        _detailGroup = "";
        if (_sideDetail is null) return;
        foreach (var child in _sideDetail.GetChildren())
        {
            _sideDetail.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>One caption/value row, the caption left and the value right against it.</summary>

    /// <summary>
    /// The proportion a formatted figure represents, or -1 when it is not one.
    ///
    /// Only a percentage counts, and only one between nought and a hundred: a war score of
    /// -40 % and an inflation of 300 % are both real figures and neither is a proportion of
    /// anything, so drawing them as a full or empty bar would be inventing information. The
    /// separator is read either way round, because the game formats numbers in the player's
    /// culture and Russian writes 74,5 where English writes 74.5.
    /// </summary>
    private static float ShareOf(string value)
    {
        if (string.IsNullOrEmpty(value)) return -1f;
        var text = value.Trim();
        if (!text.EndsWith("%", System.StringComparison.Ordinal)) return -1f;
        text = text[..^1].Trim().Replace(',', '.').Replace("\u00a0", "").Replace(" ", "");
        if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var percent)) return -1f;
        if (percent < 0f || percent > 100f) return -1f;
        return percent / 100f;
    }

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
            Plain = tooltip ?? "",
            // A figure printed as a percentage is a share, and a share can be drawn. Read
            // off the text rather than passed in by eighty-nine call sites, because the
            // formatter is the same one everywhere and this is what its output means.
            Share = ShareOf(value),
            Fill = Ui.Colour(valueToken)
        };
        row.QueueRedraw();
        if (!string.IsNullOrEmpty(tooltip) || explain is not null)
        {
            row.TooltipText = Ui.Tip(tooltip ?? caption);
            row.MouseFilter = MouseFilterEnum.Stop;
        }

        row.CustomMinimumSize = new Vector2(0, 30);
        row.AddThemeConstantOverride("separation", 14);
        // The same mark for the same idea wherever it appears. Reusing one symbol across
        // the top bar, the alert rail and every dossier row is the whole of what makes an
        // icon set worth having, and a caption with no symbol declared simply has none.
        var symbol = RowIcon(caption);
        if (symbol.Length > 0 && SpriteCatalog.Has("icon_" + symbol))
            row.AddChild(new TextureRect
            {
                Texture = Art("icon_" + symbol, $"res://game/gfx/interface/icons/icon_{symbol}.png"),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(15, 15),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore,
                Modulate = Ui.Colour("text.faint")
            });

        var label = Title(CampaignUiCatalog.DetailLabel(_dossierScreen, caption, caption), 14, Ui.Colour("text.body_soft"));
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsStretchRatio = 1.1f;
        row.AddChild(label);

        var figure = Title(value, 14, Ui.Colour(valueToken));
        figure.HorizontalAlignment = HorizontalAlignment.Right;
        figure.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        figure.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(figure);

        var card = new PanelContainer();
        card.SetMeta("dossier_field", caption);
        card.SetMeta("dossier_group", _detailGroup);
        card.AddThemeStyleboxOverride("panel", ShellRowStyle(_sideDetail.GetChildCount() % 2 == 0));
        card.AddChild(row);
        _sideDetail.AddChild(card);
    }

    /// <summary>A small capitalised rule between groups of rows.</summary>

    /// <summary>
    /// Which of the thirty-two symbols stands for a dossier row, by its caption.
    ///
    /// Deliberately partial. A caption nobody has drawn for has no symbol and the row simply
    /// carries none, so a mod or a new screen never has to supply art before it can add a
    /// line - and the marks that ARE here are the same ones the top bar and the alert rail
    /// use for the same ideas, which is the point of having a set at all.
    /// </summary>

    /// <summary>
    /// A symbol, at the size and weight every other symbol in the interface is drawn at.
    ///
    /// Returns null when nothing is declared for that name, so a caller can put it straight
    /// into a row and get nothing rather than a missing-texture box: a screen is never
    /// obliged to have art before it can list something.
    /// </summary>
    private TextureRect Symbol(string name, int size = 16, string colourToken = "text.faint")
    {
        if (string.IsNullOrEmpty(name) || !SpriteCatalog.Has("icon_" + name)) return null;
        return new TextureRect
        {
            Texture = Art("icon_" + name, $"res://game/gfx/interface/icons/icon_{name}.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(size, size),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            Modulate = Ui.Colour(colourToken)
        };
    }

    /// <summary>The mark for one good. Twenty of them, named after the good itself.</summary>
    private static string GoodIcon(Simulation.Commodity commodity) =>
        "good_" + commodity.ToString().ToLowerInvariant();

    /// <summary>And for one building. Seventeen, named the same way.</summary>
    private static string BuildingIcon(Simulation.BuildingKind building) =>
        "building_" + building.ToString().ToLowerInvariant();

    private static string RowIcon(string caption) => caption switch
    {
        // ---- the country -------------------------------------------------------------
        "Population" or "Largest people" or "Largest minority" or "Minorities" => "population",
        "Capital" or "Form of government" or "Politics" or "Ideology" or "Status" => "government",
        "Literacy" => "literacy",
        "Standard of living" => "welfare",
        "Radicalism" or "Wants out" => "unrest",
        "Stability" => "stability",
        "Urbanisation" or "Urbanization" or "Buildings" or "Productivity" => "industry",
        "Largest faith" => "research",

        // ---- the money ---------------------------------------------------------------
        "Treasury" => "treasury",
        "GDP" or "GDP per head" => "gdp",
        "Balance" or "Taxes" or "Tax rate" or "Expenses" or "Military spending"
            or "Tariffs" or "Tribute" => "balance",
        "Trade balance" or "Market access" => "trade",
        "Inflation" => "inflation",

        // ---- the ground --------------------------------------------------------------
        "Provinces" or "States held" or "Neighbours" or "Owner" or "Controller" => "world",
        "Occupation" => "war",
        "River crossings" or "Lake shore" => "port",

        // ---- the forces --------------------------------------------------------------
        "Divisions" or "Divisions here" or "Field armies" or "Visible divisions"
            or "Fighting strength" or "Reserve" or "Daily reinforcement" => "manpower",
        "Fleets" => "navy",
        "Supply" or "Cut off" => "alert_supply",
        "Organisation" => "alert_battle",
        "War score" => "victory",

        // ---- the neighbours ----------------------------------------------------------
        "Opinion" or "Stance" or "Intelligence" => "diplomacy",
        "Guarantees" or "Guaranteed by" or "Answers to" or "Arranged" => "alliance",
        "Non-aggression pact" or "Offered" => "treaty",

        _ => ""
    };

    private void DetailHeading(string text)
    {
        _detailGroup = text;
        // A rule along the baseline, in the same brass the dossier frame uses. A heading was
        // a dim line of small capitals with nothing between it and the rows above, so a
        // window read as one column with occasional shouting in it; this is what makes the
        // divisions inside a window agree with the window.
        var source = CampaignUiCatalog.DetailLabel(_dossierScreen, "#" + text, text);
        var heading = new SectionHeading
        {
            Text = Tr(source),
            Rule = Ui.Colour("button.primary_border"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        // The same three marks Title sets, so a heading follows a language change like every
        // other caption in the game rather than freezing in whatever it was built in.
        heading.SetMeta(LocSourceMeta, source);
        heading.SetMeta(LocAppliedMeta, heading.Text);
        heading.AddThemeColorOverride("font_color", Ui.Colour("text.accent_dim"));
        heading.AddThemeFontSizeOverride("font_size", 12);
        heading.SetMeta("dossier_field", "#" + text);
        heading.SetMeta("dossier_group", text);
        heading.CustomMinimumSize = new Vector2(0, 32);
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
            button.TooltipText = Ui.Tip(_campaign is null
                ? Simulation.Loc.Format("Campaign speed {0}", index + 1)
                : Simulation.Loc.Format("Campaign speed {0} · {1:0.##}s per day",
                    index + 1, _campaign.Defines.SecondsForSpeed(index + 1)));
        }
    }

    /// <summary>The screen currently open, lit in the bottom bar.</summary>
    private void UpdateScreenButtons()
    {
        var open =
            _stateAffairsLayer is { Visible: true } ? "State Affairs" :
            _ledgerLayer is { Visible: true } ? "Ledger" :
            _warLedgerLayer is { Visible: true } ? "Wars" :
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
        CommandStrip,

        /// <summary>
        /// The game has stopped to say something. An event, a settlement put to you, a
        /// question with no map behind it - the only windows that are allowed to look like
        /// an interruption, because they are one.
        /// </summary>
        Modal
    }


    /// <summary>
    /// What a window looks like inside a campaign, by what kind of window it is.
    ///
    /// Every panel used to get the same flat box here, because this asked for a surface and
    /// then ignored it. A strip of speed controls, a rail of alerts, a dossier and a modal
    /// that has stopped the game are four different objects, and the first thing a frame has
    /// to say is which one a player is looking at.
    ///
    /// Built ON the shell style rather than instead of it: the colours are still the shell's
    /// tokens, so a reskin of `shell.*` still drives all four, and what is added here is only
    /// the difference between them.
    /// </summary>
    private static StyleBoxFlat CampaignPanelStyle(PanelSurface surface)
    {
        var style = ShellPanelStyle();
        var brass = Ui.Colour("button.primary_border");

        switch (surface)
        {
            case PanelSurface.Modal:
                // The one window on screen that is an interruption, and the only one with
                // rounded corners - so it reads as a card laid over the game rather than a
                // part of it.
                style.BorderColor = brass;
                style.BorderWidthTop = 2;
                style.BorderWidthLeft = 2;
                style.BorderWidthRight = 2;
                style.BorderWidthBottom = 2;
                style.SetCornerRadiusAll(6);
                style.ShadowColor = new Color(0, 0, 0, 0.55f);
                style.ShadowSize = 14;
                style.ShadowOffset = new Vector2(0, 6);
                break;

            case PanelSurface.CommandStrip:
                // Part of the frame of the screen, not a thing sitting on it.
                style.BorderWidthTop = 1;
                style.ShadowSize = 0;
                style.ShadowOffset = Vector2.Zero;
                break;

            case PanelSurface.Command:
                // An instrument: tight, cool, and quiet enough to sit beside the map for an
                // entire campaign without asking for attention.
                style.BorderWidthTop = 1;
                style.ShadowColor = new Color(0, 0, 0, 0.18f);
                style.ShadowSize = 3;
                style.ShadowOffset = new Vector2(0, 2);
                break;

            default:
                // A document. The brass rule along the top is the one ornament a dossier
                // gets, and it is what makes a reading window look like a page.
                style.BorderColor = brass;
                style.BorderWidthTop = 3;
                style.ShadowColor = new Color(0, 0, 0, 0.34f);
                style.ShadowSize = 8;
                style.ShadowOffset = new Vector2(0, 4);
                break;
        }
        return style;
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
        var inCampaign = _hudLayer is not null && (parent == _hudLayer || _hudLayer.IsAncestorOf(parent));
        panel.AddThemeStyleboxOverride("panel",
            inCampaign ? CampaignPanelStyle(surface) : PanelStyle(surface));
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

    /// <summary>
    /// A label that will never wrap, for a crowded row.
    ///
    /// The single most repeated layout bug in this interface, three times now: `Title`
    /// wraps by default, which is right for a paragraph and catastrophic in an HBox where
    /// something else already claims the width. The label gets no width of its own, wraps to
    /// one character per line, and turns a two-line card into one the height of the panel.
    /// Anything sharing a row with an ExpandFill sibling goes through this.
    /// </summary>
    private static Label Fixed(Label label, float minimumWidth = 0f)
    {
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.ClipText = true;
        label.VerticalAlignment = VerticalAlignment.Center;
        if (minimumWidth > 0f) label.CustomMinimumSize = new Vector2(minimumWidth, 0);
        return label;
    }

    private static Label Title(string text, int size, Color color, bool upper = false)
    {
        var label = new Label
        {
            Text = upper ? Tr(text).ToUpperInvariant() : Tr(text),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.SetMeta(LocSourceMeta, text);
        if (upper) label.SetMeta(LocUpperMeta, true);
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
        AddThemeFontOverride("font", PresentationFonts.For("ui.default"));
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

    /// <summary>
    /// Marks a caption the interface shows in capitals. The shouting has to happen
    /// after the lookup rather than before it: a caption uppercased at the call site
    /// stores the shouted spelling as the string to translate from, and no table has a
    /// row under that name.
    /// </summary>
    private const string LocUpperMeta = "loc_upper";

    /// <summary>
    /// A tooltip's untranslated source, and the translation last written from it.
    ///
    /// Captions have two factories they all pass through, and so keep their source for
    /// free; tooltips are written straight onto a control from a hundred places. The
    /// ones that matter here are the ones built once when the game starts - the top bar
    /// and the screen buttons - because everything else is rewritten on the next refresh
    /// anyway, in whatever language is current by then.
    /// </summary>
    private const string LocHintMeta = "loc_hint";

    /// <summary>
    /// Give a text field a placeholder that survives a change of language. The sweep
    /// knows about a Label's text and a Button's; a placeholder is neither, which is why
    /// the country picker still said "Search countries or tags" to a Russian player.
    /// </summary>
    private static LineEdit Placeholder(LineEdit field, string source)
    {
        field.SetMeta(LocHintMeta, source);
        field.PlaceholderText = Tr(source);
        return field;
    }

    private const string LocTipMeta = "loc_tip";
    private const string LocTipAppliedMeta = "loc_tip_applied";

    /// <summary>Give a control a tooltip that survives a change of language.</summary>
    private static T Tip<T>(T control, string source) where T : Control
    {
        control.SetMeta(LocTipMeta, source);
        control.TooltipText = Ui.Tip(source);
        control.SetMeta(LocTipAppliedMeta, control.TooltipText);
        return control;
    }

    /// <summary>Re-translate every caption still showing its own translation, in place.</summary>
    /// <summary>
    /// Put the whole game into the language that has just been chosen.
    ///
    /// The tree walk below handles every caption that was built from a key, which is most of
    /// them. What it cannot reach is anything whose text is not a key any more: the names
    /// written across the MAP, which are three-dimensional text built once and left; and the
    /// items of a dropdown, which carry no meta for the walk to find. Both are listed here
    /// by hand, because there is no way to find them automatically and a half-translated
    /// game is worse than an untranslated one.
    /// </summary>
    private void RefreshLocalisedText()
    {
        RefreshLocalisedText(this);
        _strategicHudFingerprint = int.MinValue;
        RefreshStrategicHud();

        // The names across the map. Built as 3D text once and rebuilt only when a country
        // renames itself - which is what a change of language is, for every country at once.
        _globe?.RefreshCountryNames();

        // Dropdowns. An OptionButton item is a plain string with nothing attached, so each
        // list is re-filled by whoever owns it.
        RefreshBuildingPicker();
        if (_stateAffairsLayer is { Visible: true }) RefreshStateAffairs();
        if (_settingsLayer is { Visible: true }) ShowSettingsCategory(_settingsCategory);
        if (_saveLoadLayer is { Visible: true }) RefreshSaveSlots();

        if (_campaign is not null && _dossierScreen == "Economy") ShowEconomyScreen();
    }

    private static void RefreshLocalisedText(Node node)
    {
        if (node.HasMeta(LocSourceMeta))
        {
            var source = node.GetMeta(LocSourceMeta).AsString();
            var applied = node.HasMeta(LocAppliedMeta) ? node.GetMeta(LocAppliedMeta).AsString() : null;
            var translated = Tr(source);
            if (node.HasMeta(LocUpperMeta)) translated = translated.ToUpperInvariant();

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

        if (node is LineEdit field && node.HasMeta(LocHintMeta))
            field.PlaceholderText = Tr(node.GetMeta(LocHintMeta).AsString());

        if (node is Control control && node.HasMeta(LocTipMeta))
        {
            var applied = node.HasMeta(LocTipAppliedMeta)
                ? node.GetMeta(LocTipAppliedMeta).AsString()
                : null;
            // Same discipline as a caption: a tooltip rewritten since must not be dragged
            // back to the wording it was created with.
            if (applied is null || control.TooltipText == applied)
            {
                control.TooltipText = Ui.Tip(node.GetMeta(LocTipMeta).AsString());
                node.SetMeta(LocTipAppliedMeta, control.TooltipText);
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
        Tr(_globe is null || _globe.MapSmoothing ? "Map smoothing: on" : "Map smoothing: off");

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
            PlayerSettings.Set("map_smoothing", _globe.MapSmoothing);
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
        _globe?.RefreshCountryNames();

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
        // A button carrying a picture and no caption is centred on the picture, and two
        // separate things stopped it being. Measured by rendering one and counting pixels:
        // the icon landed three pixels left and one pixel high in a fifty-four pixel button.
        //
        // Three of those are `icon_alignment`, which defaults to the left because the usual
        // button is a picture followed by words. The fourth is this stylebox: six pixels of
        // margin above and eight below is the optical lift that makes a CAPTION sit right in
        // a button, and applied to a picture it is simply a pixel of error.
        //
        // Asked of the button rather than passed in, so that the re-paints that mark a
        // screen as open - UpdateScreenButtons calls this on every refresh - cannot quietly
        // put the offset back.
        var iconOnly = button.Icon is not null && string.IsNullOrEmpty(button.Text);
        button.IconAlignment = iconOnly ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        button.VerticalIconAlignment = VerticalAlignment.Center;

        button.AddThemeStyleboxOverride("normal", ButtonStyle(primary, 0, iconOnly));
        button.AddThemeStyleboxOverride("hover", ButtonStyle(primary, 1, iconOnly));
        button.AddThemeStyleboxOverride("pressed", ButtonStyle(primary, 2, iconOnly));
        // `primary` survives into the disabled look, and that is the whole of telling
        // "this is what we are" apart from "you may not have this". The politics screen
        // disables the form of government and the ideology a country already has - you
        // cannot adopt what you already are - and with a flat grey disabled style that made
        // the country's own ideology look exactly like one the game was refusing.
        button.AddThemeStyleboxOverride("disabled", ButtonStyle(primary, 3, iconOnly));
        button.AddThemeStyleboxOverride("focus", ButtonStyle(primary, 1, iconOnly));
        button.AddThemeColorOverride("font_color", primary ? Ui.Colour("button.text_hover") : Ui.Colour("button.text"));
        button.AddThemeColorOverride("font_hover_color", Ui.Colour("button.text_hover"));
        button.AddThemeColorOverride("font_pressed_color", Ui.Colour("button.text_pressed"));
        button.AddThemeColorOverride("font_disabled_color",
            primary ? Ui.Colour("button.text_hover") : Ui.Colour("button.text_disabled"));
    }

    private static StyleBoxFlat ButtonStyle(bool primary, int state, bool iconOnly = false)
    {
        var background = state switch
        {
            1 => primary ? Ui.Colour("button.primary_fill_hover") : Ui.Colour("button.fill_hover"),
            2 => primary ? Ui.Colour("button.primary_fill_pressed") : Ui.Colour("button.fill_pressed"),
            // Settled rather than unavailable: the accent stays, so a chosen option reads as
            // chosen even though it cannot be pressed again.
            3 => primary ? Ui.Colour("button.primary_fill") : Ui.Colour("button.fill_disabled"),
            _ => primary ? Ui.Colour("button.primary_fill") : Ui.Colour("button.fill")
        };
        var border = state switch
        {
            1 => Ui.Colour("button.border_hover"),
            2 => Ui.Colour("button.border_pressed"),
            // The border stays the quiet one even when the fill is the accent: a settled
            // choice should not shout as loudly as one you can still make.
            3 => primary ? Ui.Colour("button.primary_border") : Ui.Colour("button.border_disabled"),
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
            // A caption rests a pixel high and travels two pixels down when the button is
            // pressed; a picture rests dead centre and travels the same two.
            ContentMarginTop = iconOnly ? (state == 2 ? 9 : 7) : (state == 2 ? 7 : 6),
            ContentMarginBottom = iconOnly ? (state == 2 ? 5 : 7) : (state == 2 ? 5 : 8),
            AntiAliasing = true
        };
    }

    private StyleBox PanelStyle(PanelSurface surface)
    {
        // A modal is a document, not an instrument: it takes the dossier artwork. Testing
        // "not Dossier" put it on the command frame, which is the tall rail meant to sit
        // beside a map for an hour - the wrong shape entirely for a window that appears,
        // asks one question and goes.
        var command = surface is PanelSurface.Command or PanelSurface.CommandStrip;
        var modal = surface == PanelSurface.Modal;
        var strip = surface == PanelSurface.CommandStrip;
        var textureMargin = strip ? 20f : command ? 48f : 28f;
        // The tall command artwork draws its visible inner rail about 30 px from
        // the outer edge. Keep controls on the inside of that rail. Callers add a
        // further 8-16 px semantic margin, so 22 px lands text and buttons safely
        // inside the frame without sacrificing the useful width of narrow panels.
        // A modal breathes: it is the one window with nothing competing for its space.
        var contentHorizontal = strip ? 6f : command ? 28f : modal ? 14f : 6f;
        var contentVertical = strip ? 4f : command ? 22f : modal ? 12f : 4f;
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
            // Lit rather than tinted cool: the interruption should feel like a lamp has
            // been turned on over it, which is most of what makes it read as urgent
            // without a single new piece of artwork.
            ModulateColor = modal
                ? new Color(1.04f, 1.0f, 0.94f, 1f)
                : command
                    ? new Color(0.88f, 0.92f, 0.98f, 0.985f)
                    : new Color(0.9f, 0.94f, 1f, 0.98f)
        };
    }
}
