using System;
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
    private StatReadout _publicDebt;
    private StatReadout _balance = null!;
    private StatReadout _stability = null!;
    private StatReadout _manpower = null!;
    private StatReadout _inflation = null!;
    private StatReadout _gdp = null!;
    private StatReadout _population = null!;
    // The four currencies the war doctrines are paid in (MilitaryExperience, TacticalDoctrines,
    // StrategicDoctrines). Reserved on the bar from the first day the mechanic ships: they are
    // what a player watches while deciding which article to study next.
    private StatReadout _armyExperience;
    private StatReadout _airExperience;
    private StatReadout _navyExperience;
    private StatReadout _politicalPoints;
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
    private double _clockStepSeconds;
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
        ApplicationExit.Ensure(this);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        WrapGuard.Install(GetTree());
        InterfaceInteractionGuard.Install(GetTree());
        ButtonMotion.Install(GetTree());
        TooltipKeeper.Install(GetTree());
        ControlSkin.Apply();
        ApplyCustomCursors();
        // A campaign's own options are read from whichever campaign is on screen now - asked
        // each time, so a loaded save or a new scenario is never answered for by the last one.
        Simulation.SettingsCatalog.CurrentCampaign = () => _campaign;
        var loaded = await RunLoadingOperation("Preparing the world", async () =>
        {
            // Folders first: the journal below writes into `logs/`, and the first thing a
            // support conversation needs is one known folder rather than two.
            GamePaths.Prepare();
            StartDiagnostics();
            // The engine-neutral settings layer is told where the player's files live: one
            // folder under Documents, not the engine's folder in AppData.
            Simulation.PlayerSettings.UseDirectory(GamePaths.Settings);
            await LoadingPhase("Loading interface and content catalogs", 10);
            // Resolve/migrate old saves only after the first loading frame is visible.
            GD.Print($"[dof] saves: {SaveRoot}");
            Simulation.SpriteCatalog.Load();
            Simulation.SoundCatalog.Load();
            Simulation.MusicCatalog.Load();
            Simulation.FontCatalog.Load();
            ApplyDeclaredFont();
            ApplyCustomCursors();
            await LoadingPhase("Preparing the interface", 20);
            BuildInterface();
            _globe.LoadingInputBlocked = true;
            await LoadingPhase("Creating countries and regions", 30);
            // Schema discovery is independent of the live world. Await it while loading,
            // before creating a campaign; it must not become the first autosave's freeze.
            await System.Threading.Tasks.Task.Run(SaveSystem.WarmSerializationContracts);
            if (!IsInstanceValid(this) || !IsInsideTree()) return;
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
        // A failed boot keeps its error cover. Do not open an intent against a
        // half-built campaign/UI (the cold multiplayer route used to do exactly that).
        if (loaded && _startupComplete && IsInsideTree()) OpenWhateverWasAskedFor();
    }

    public override void _Process(double delta)
    {
        if (_isLoading || !_startupComplete) return;
        // Before anything else in the frame: a machine that cannot hold thirty frames a second
        // has to be allowed to spend less on this frame, not the one after a settings screen.
        MaintainQuality(delta);
        PaceBackgroundFrames();
        BindStepCompletion();
        RefreshMediaPreview();
        RefreshSoundtrackPlayer(delta);
        // Before any of the early returns below it: a panel is read while the game is
        // paused, and a caption squeezed to nothing on a paused screen is still squeezed.
        LayoutAudit.Watch(this, delta);
        // The country card's facts panel is sized to its own contents, and that height is only
        // knowable after the layout pass rather than when the card is built. See the method.
        SyncCountryFactsHeight();
        TickNuclearAlert(delta);
        if (_globe is not null) _globe.FogOfWarActive = _campaign is not null && _menuLayer is { Visible: false } && (_pickerLayer is null || !_pickerLayer.Visible);
        if (_globe is not null) _globe.MilitaryMarkersEnabled = _menuLayer is { Visible: false } && (_pickerLayer is null || !_pickerLayer.Visible);
        // The lens and the roll move while a key is held, so the line that reports them
        // has to be written every frame rather than when something is pressed.
        if (_freeCameraMode) RefreshFreeCameraReadout();
        NetworkProcess(delta);
        // Everything that happened since the interface last drew, drawn once - for a network
        // client as well, whose world arrives by the host's steps rather than its own.
        if (_campaign is not null)
        {
            FlushInterfaceIfDue(delta);
            _modLoader.CaptureCrossoverSurface ??= () => _globe?.CaptureCrossoverSurface();
            _modLoader.CaptureCrossoverHydrology ??= () => _globe?.CaptureCrossoverHydrology();
            _modLoader.PresentCrossoverVoxels ??= blocks => _globe?.PresentCrossoverVoxels(blocks);
            _modLoader.PresentCrossoverWorks ??= works => _globe?.PresentCrossoverWorks(works);
            _modLoader.OnFrame(_campaign, delta);
        }
        UpdateWorldCurse(delta);
        if (IsNetworkClient) { if (_campaign is not null) ReportFrameCost(delta); return; }
        if (_campaign is null) return;
        // Reported whether or not time is running: a measurement that only arrives when
        // everything is already going well is no use for finding out why it is not.
        ReportFrameCost(delta);
        // A step the smoothed clock has begun is carried on even under pause: pausing stops the
        // next step, never half of this one.
        if (_campaign.IsPaused && !_campaign.MidStep) { _dayAccumulator = 0; return; }
        // Real seconds one step of the simulation is worth. The speed setting is still in
        // days, because that is what a player thinks in; a campaign stepping six hours at a
        // time simply takes four steps to spend one day's worth of them.
        var secondsPerDay = _campaign.Defines.SecondsForSpeed(_speed) * _campaign.TickScale;
        // A new cadence must not reinterpret time queued under the old one (including
        // console edits). A 24-hour remainder used to become 24 hourly steps at once.
        if (secondsPerDay != _clockStepSeconds) _dayAccumulator = 0;
        _clockStepSeconds = secondsPerDay;
        _dayAccumulator = SimulationPacing.Accumulate(_dayAccumulator, delta, secondsPerDay,
            _campaign.Defines.MaxDaysPerFrame);
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
        //
        // And with smooth time on, a step is not one piece of work either. The day's passes and
        // the week's economy run as many stages (Campaign.StepFor), as many of them in a frame
        // as its budget holds, and the clock moves on when the step is done: the same world,
        // without the frame that stopped for a third of a second every simulated week.
        var budget = System.Diagnostics.Stopwatch.StartNew();
        var slice = _campaign.Defines.FrameSimulationBudgetMs;
        var cap = _campaign.Defines.MaxDaysPerFrame;
        var smooth = SettingsCatalog.Flag("smooth_ticks");
        while ((_campaign.MidStep || _dayAccumulator >= secondsPerDay) && days < cap &&
               (days == 0 || budget.Elapsed.TotalMilliseconds < slice))
        {
            if (!_campaign.MidStep) _dayAccumulator -= secondsPerDay;
            if (_networkConnected && _networkIsHost && _networkGameStarted) _applyingNetworkCommand = true;
            try
            {
                var finished = true;
                using (SimProfiler.Measure("campaign.day"))
                {
                    if (smooth || _campaign.MidStep)
                        finished = _campaign.StepFor(System.Math.Max(1.0, slice - budget.Elapsed.TotalMilliseconds));
                    else _campaign.Tick();
                }
                // The rest of this step belongs to the next frame.
                if (!finished) { _applyingNetworkCommand = false; break; }
            }
            catch (System.Exception exception) when (exception is not System.OutOfMemoryException && exception is not System.AccessViolationException)
            {
                _applyingNetworkCommand = false;
                _dayAccumulator = 0;
                _campaign.BlockSimulation(exception.Message, exception);
                DiagnosticLogs.Write("errors", "SIMULATION_STOPPED " + exception);
                var path = SaveSystem.WriteDiagnosticSnapshot(SaveRoot, _campaign, exception.Message);
                ShowCampaignFault(Simulation.Loc.Format(
                    "Simulation stopped at {0}. The last good save is unchanged. Load it before continuing.", _campaign.Date.Display()),
                    path, exception.Message);
                return;
            }
            _applyingNetworkCommand = false;
            if (_campaign.SimulationFault.Length > 0) return;
            MaybeAutosave();
            days++;
        }
    }

    private Campaign _stepCompletionCampaign;

    private void BindStepCompletion()
    {
        // Every role can finish a staged step from a save or UI command. Mod hooks and
        // held network items must see that stable boundary exactly once, not an RPC loop.
        var campaign = _campaign;
        if (ReferenceEquals(campaign, _stepCompletionCampaign)) return;
        if (_stepCompletionCampaign is not null) _stepCompletionCampaign.StepCompleted -= CampaignStepCompleted;
        _stepCompletionCampaign = campaign;
        if (campaign is not null) campaign.StepCompleted += CampaignStepCompleted;
    }

    private void CampaignStepCompleted(Campaign campaign)
    {
        if (!ReferenceEquals(campaign, _campaign)) return;
        var wasApplying = _applyingNetworkCommand;
        _applyingNetworkCommand = true;
        try
        {
            // One hook for a newly opened day, including a day drained by saving.
            if (campaign.StepOpensNewDay) _modLoader.OnDayAdvanced(campaign);
        }
        catch (System.Exception exception) when (exception is not System.OutOfMemoryException && exception is not System.AccessViolationException)
        {
            _dayAccumulator = 0;
            campaign.BlockSimulation(exception.Message, exception);
            DiagnosticLogs.Write("errors", "SIMULATION_STOPPED " + exception);
            var path = SaveSystem.WriteDiagnosticSnapshot(SaveRoot, campaign, exception.Message);
            ShowCampaignFault(Simulation.Loc.Format(
                "Simulation stopped at {0}. The last good save is unchanged. Load it before continuing.", campaign.Date.Display()),
                path, exception.Message);
            return;
        }
        finally { _applyingNetworkCommand = wasApplying; }
        if (IsNetworkClient) { ApplyDeferredNetworkCommands(); return; }
        if (_networkConnected && _networkIsHost && _networkGameStarted)
        {
            Rpc(nameof(ClientAdvanceNetworkHours), campaign.ElapsedHours, campaign.TickHours);
            BroadcastNetworkChecksum();
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
        ButtonFocusPresentation.Observe(GetViewport(), inputEvent);
        // Loading can yield before any covered panel exists. Gate BEFORE dispatching
        // to helpers or authoring dialogs, not half way through the input chain.
        if (_isLoading || !_startupComplete)
        {
            if (inputEvent is InputEventKey loadingKey &&
                !(IsInstanceValid(_loadingScreen) && _loadingScreen.CanDismiss && loadingKey.Keycode is Key.Enter or Key.Space))
                GetViewport().SetInputAsHandled();
            return;
        }
        if (IsInstanceValid(_nuclearStrikeComposer))
        { HandleDecisionInput(_nuclearStrikeComposer, inputEvent, CloseNuclearStrikeReview, war: true); return; }
        if (_mediaLibraryLayer is { Visible: true }) { HandleMediaLibraryInput(inputEvent); return; }
        if (_countryRankingsLayer is { Visible: true }) { HandleCountryRankingsInput(inputEvent); return; }
        if (_releaseModalLayer is { Visible: true })
        {
            if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            { CloseReleaseCountryModal(); GetViewport().SetInputAsHandled(); }
            return;
        }
        if (IsInstanceValid(_warDeclaration))
        {
            HandleDecisionInput(_warDeclaration, inputEvent, CloseWarDeclaration, war: true);
            return;
        }
        if (IsInstanceValid(_allianceProposalReview))
        {
            HandleDecisionInput(_allianceProposalReview, inputEvent, CloseAllianceProposalReview, war: true);
            return;
        }
        // The big trees are read before the map is: a wheel over a graph is a wheel about the
        // graph, and must not also march the camera or scroll the page behind it.
        if (HandleTreeViewportInput(inputEvent)) return;
        if (IsInstanceValid(_regionGroupBuild) && inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        { CloseRegionGroupBuild(); GetViewport().SetInputAsHandled(); return; }
        if (HandleMapControlsInput(inputEvent)) return;
        if (HandleSituationEscape(inputEvent)) return;
        if (IsInstanceValid(_dealComposer))
        {
            HandleDecisionInput(_dealComposer, inputEvent, CloseDealComposer);
            return;
        }
        if (IsInstanceValid(_ultimatumComposer))
        {
            HandleDecisionInput(_ultimatumComposer, inputEvent, CloseUltimatumComposer);
            return;
        }
        if (HandleConstructionPlacementInput(inputEvent)) return;
        if (HandleRecruitmentPlacementInput(inputEvent)) return;
        if (IsInstanceValid(_authorCanvas))
        {
            // Child inspectors and confirmation dialogs own their Escape key.
            if (_authorCanvas.FindChildren("*", "Window", true, false).OfType<Window>().Any(w => w.Visible)) return;
            if (inputEvent is InputEventKey { Pressed: true, Echo: false } authorKey &&
                (authorKey.CtrlPressed || authorKey.MetaPressed) && Letter(authorKey) == Key.S && IsInstanceValid(_authorCode) && IsInstanceValid(_authorTabs) && _authorTabs.IsVisibleInTree())
            { AuthorOperation(true); GetViewport().SetInputAsHandled(); return; }
            // Escape takes the window's OWN way out, which is not always "close".
            //
            // It used to close unconditionally, and opened to choose a country there is no
            // campaign behind the window - so escape took the window away and left a globe
            // with no interface on it, which is exactly the bug it looked like. See
            // `AuthorWindow`: the window is told where its exit leads when it is opened.
            if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                // The workshop's exit can switch scenes immediately. Mark the input
                // first: after that callback this node may no longer have a viewport.
                GetViewport().SetInputAsHandled();
                EscapeAuthorWindow();
            }
            return;
        }
        // Space is a sacred strategy-game time key: it never activates a focused
        // button, confirms a dialog or changes projection. During a campaign it
        // only toggles time, and outside a campaign it is deliberately consumed.
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } &&
            GetViewport().GuiGetFocusOwner() is Control focused &&
            focused is not LineEdit && focused is not TextEdit)
        {
            // Escape is a window command, not a button command. A clicked button keeps
            // keyboard focus in Godot, and a focused control can swallow the event before
            // the campaign's own close chain sees it; release it first so the same Esc works
            // after a click as it does from an empty map.
            focused.ReleaseFocus();
        }

        if (inputEvent is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (HandleDiplomacySearchShortcut(key))
        { GetViewport().SetInputAsHandled(); return; }

        if (HandleRecruitmentWorkbenchShortcut(key))
        { GetViewport().SetInputAsHandled(); return; }

        // The index of everywhere. Ctrl+K because that is where a hand that lives in an editor
        // reaches, and Ctrl+P because that is where a hand that lives in a code editor reaches
        // when it means exactly the same thing. Not F1: that key already opens the handbook, and
        // taking a documented key away from a documented button is how a key map starts lying.
        //
        // While it is up it owns Escape and the arrow keys; everything else is left unhandled so
        // the search field keeps typing. Marking a key handled here does not stop the field from
        // receiving it, it only stops the other handlers below from acting on it.
        if (_navigatorLayer is { Visible: true })
        {
            switch (key.Keycode)
            {
                case Key.Escape:
                    CloseNavigator();
                    GetViewport().SetInputAsHandled();
                    break;
                case Key.Down:
                    MoveNavigator(1);
                    GetViewport().SetInputAsHandled();
                    break;
                case Key.Up:
                    MoveNavigator(-1);
                    GetViewport().SetInputAsHandled();
                    break;
            }
            return;
        }
        if (((key.CtrlPressed || key.MetaPressed) && Letter(key) is Key.K or Key.P))
        {
            ToggleNavigator();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.Keycode == Key.Escape && MovementPanelOwnsEscape())
        {
            // The movement card is a HUD dock, and a focused control inside it may otherwise
            // consume Escape before the unhandled close chain runs. Answer it here, where the
            // card is known to be the topmost thing that owns the key.
            CloseMovementPanel();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_sovereignFinanceLayer is { Visible: true } && _handbookLayer is not { Visible: true } &&
            _developerConsoleLayer is not { Visible: true } &&
            _pauseMenuLayer is not { Visible: true } && _settingsLayer is not { Visible: true } &&
            _saveLoadLayer is not { Visible: true })
        {
            if (key.Keycode == Key.Escape) { CloseSovereignFinance(); GetViewport().SetInputAsHandled(); return; }
            if (key.Keycode is Key.Space or Key.F2 or Key.F3 or Key.F7 or Key.F10)
            { GetViewport().SetInputAsHandled(); return; }
        }

        // Reload is also available while typing in the console or searching a country.
        if (key.CtrlPressed && key.ShiftPressed && Letter(key) == Key.R)
        {
            if (_developerConsoleLayer is not null)
            {
                if (!_developerConsoleLayer.Visible) ToggleDeveloperConsole();
                ExecuteDeveloperCommand("reload");
            }
            GetViewport().SetInputAsHandled();
            return;
        }

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
        if (key.Keycode == Key.Escape && _researchLayer is { Visible: true })
        { CloseResearchScreen(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.Escape && _doctrineLayer is { Visible: true })
        { CloseDoctrineScreen(); GetViewport().SetInputAsHandled(); return; }
        // Escape takes back a rebase that is waiting for its region first, then the panel.
        if (key.Keycode == Key.Escape && _supportLayer is { Visible: true })
        {
            if (_supportRebaseArmed || _supportTargetArmed) CancelSupportRebase(); else CloseSupportScreen();
            GetViewport().SetInputAsHandled();
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
        if (key.Keycode == Key.Escape && (_drawingKind is not null || _erasingPlans))
        {
            EndDrawing();
            SetArmyFeedback(Tr("The line was abandoned."));
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.Keycode == Key.F1 && _campaign is not null && _hudLayer is { Visible: true })
        { ToggleHandbook(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.F5) { QuickSave(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.Home && _campaign is not null && _hudLayer is { Visible: true })
        { FocusOwnCapital(); GetViewport().SetInputAsHandled(); return; }
        if (key.Keycode == Key.F6 && _campaign is not null && _hudLayer is { Visible: true })
        {
            ToggleShowcasePan();
            GetViewport().SetInputAsHandled();
            return;
        }
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
        if (IsInstanceValid(_nuclearStrikeComposer) || IsInstanceValid(_warDeclaration)) { GetViewport().SetInputAsHandled(); return; }
        if (_mediaLibraryLayer is { Visible: true }) { GetViewport().SetInputAsHandled(); return; }
        if (_mapModeDrawerLayer is { Visible: true }) { GetViewport().SetInputAsHandled(); return; }
        // A diplomatic modal must also shield the map's unhandled keyboard shortcuts.
        if (IsInstanceValid(_dealComposer) || _situationLayer is { Visible: true } && !inputEvent.IsActionPressed("ui_cancel"))
        { GetViewport().SetInputAsHandled(); return; }
        if (IsInstanceValid(_ultimatumComposer)) { GetViewport().SetInputAsHandled(); return; }
        // Escape while the workshop is up is answered in `_Input`, which runs first - see
        // there. Nothing else in the campaign hears a key while that window covers it.
        if (IsInstanceValid(_authorCanvas)) return;
        if (_isLoading || !_startupComplete) return;
        if (inputEvent is InputEventKey && GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
        if (_developerConsoleLayer is { Visible: true }) return;
        if (_sovereignFinanceLayer is { Visible: true }) return;
        // A workspace covers the navigation that opened it, so Escape is the way back out of
        // it - the same answer the province card and the diplomacy menu already give.
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } &&
            CloseVisibleCampaignScreen())
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        HandleProjectionHotkey(inputEvent);
        if (GetViewport().IsInputHandled()) return;

        // Halving and joining for what is not a division: the fleets of a box selection and the
        // long-range formations. Divisions take the same keys below.
        if (inputEvent is InputEventKey { Pressed: true, Echo: false } splitKey &&
            _hudLayer is { Visible: true } && _selectedArmies.Count == 0 && Input.IsKeyPressed(Key.Ctrl))
        {
            var letter = Letter(splitKey);
            if (letter == Key.X && _selectedFleet is not null && _fleetPanel is { Visible: true })
            { SplitSelectedFleet(); GetViewport().SetInputAsHandled(); return; }
            if (letter == Key.K && _selectedFleet is not null && _fleetPanel is { Visible: true })
            { MergeSelectedFleets(); GetViewport().SetInputAsHandled(); return; }
            if (letter == Key.X && SplitSupportSelection()) { GetViewport().SetInputAsHandled(); return; }
        }

        // B: building icons on the map, on and off - with nothing selected, so a selection keeps its letters.
        if (inputEvent is InputEventKey { Pressed: true, Echo: false } iconsKey && _hudLayer is { Visible: true } &&
            _selectedArmies.Count == 0 && Letter(iconsKey) == Key.B && !iconsKey.CtrlPressed && !iconsKey.AltPressed &&
            !iconsKey.ShiftPressed && !iconsKey.MetaPressed)
        {
            SettingsCatalog.Store("map_region_icons", !SettingsCatalog.Flag("map_region_icons"));
            ApplySetting("map_region_icons");
            RefreshMapVisualToggles();
            GetViewport().SetInputAsHandled();
            return;
        }

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
                    else if (key.AltPressed || Input.IsKeyPressed(Key.Alt)) CancelSelectedQueuedOrders();
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

                // Halve the selection: half the formations stay selected and take the next
                // order, the other half stay where they are. One formation is cut in two.
                case Key.X when Input.IsKeyPressed(Key.Ctrl):
                    SplitSelectionInHalf();
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

        // The top window first, and back to whatever opened it: the save list and the settings
        // reached from the pause menu return to it. Neither was in this chain, so Escape went
        // past them to the pause menu underneath and left the save list standing on screen.
        if (_settingsLayer is { Visible: true })
            HideSettings();
        else if (_saveLoadLayer is { Visible: true })
            HideSaveLoadInterface();
        else if (_networkLayer is { Visible: true })
            CloseNetworkMultiplayer();
        else if (_handbookLayer is { Visible: true })
            CloseHandbook();
        else if (_peaceLayer is { Visible: true })
            ClosePeaceConference();
        else if (_defconLayer is { Visible: true })
            CloseDefconOverview();
        else if (_pauseMenuLayer is { Visible: true })
            HidePauseMenu();
        else if (_stateAffairsLayer is { Visible: true })
            CloseStateAffairs();
        else if (_sovereignFinanceLayer is { Visible: true })
            CloseSovereignFinance();
        else if (_ledgerLayer is { Visible: true })
            CloseLedger();
        else if (_warLedgerLayer is { Visible: true })
            CloseWarLedger();
        // The full-screen pages the other screens open over the map: Escape closes them the
        // way it closes the ledger, rather than opening the pause menu over the top of them.
        else if (_diplomacyLayer is { Visible: true })
            CloseDiplomacyScreen();
        else if (_conflictsLayer is { Visible: true })
            CloseConflictsScreen();
        else if (_policyLayer is { Visible: true })
            ClosePolicyWorkspace();
        else if (_tradeScreenLayer is { Visible: true })
            CloseTradeScreen();
        else if (_productionLayer is { Visible: true })
            CloseProductionScreen();
        // Context menus and HUD docks on the map (province card, fleet inspector, army
        // roster, resource atlas, movement card, side dossier, country/region selection):
        // Escape puts them away one layer at a time before ever reaching the pause menu.
        else if (_provinceCardLayer is not null && IsInstanceValid(_provinceCardLayer) && _provinceCardLayer.Visible)
            CloseProvinceCard();
        else if (_fleetPanel is { Visible: true })
            CloseFleetPanel();
        else if (_armyPanel is { Visible: true })
            ClearArmySelectionAndClose();
        else if (_resourceAtlasPanel is { Visible: true })
            _globe.SetMapMode(StrategicMapMode.Political);
        else if (_movementPanel is { Visible: true })
            CloseMovementPanel();
        else if (_sidePanel.Visible && _dossierReturnArmyIds is not null)
            ReturnToArmyOrders();
        else if (_sidePanel.Visible)
            CloseSidePanel();
        else if (MapSelectionShowing || _regionCard is { Visible: true })
            CloseMapSelection();
        else if (_pickerLayer is { Visible: true })
            ShowMainMenu();
        else if (_hudLayer.Visible)
            ShowPauseMenu();

        GetViewport().SetInputAsHandled();
    }

    /// <summary>The seed the next new world is built from. The same seed builds the same
    /// world - every war, every event, every crisis - which is the campaign's whole claim to
    /// being a simulation anybody can talk about. The picker can change it before a game
    /// starts; a save carries the seed of the world it came from.</summary>
    private int _campaignSeed = 20260830;

    /// <summary>The player typed this seed into the picker: build that world, roll nothing.</summary>
    private bool _campaignSeedChosen;

    private void StartCampaign()
    {
        // A new world unless one was asked for. The fixed default played the same history in
        // every game - the same coups, the same wars in the same months - which is the opposite
        // of what two hundred and sixty countries are for. The picker shows the seed, and typing
        // it back replays that world exactly; the setting turns the dice off for those who want
        // one world to learn.
        if (!_campaignSeedChosen && SettingsCatalog.Flag("fresh_world_each_game"))
            _campaignSeed = CampaignSeedSelection.Roll();
        _campaignSeedChosen = false;
        if (_pickerSeed is not null) _pickerSeed.Text = _campaignSeed.ToString();
        _pickerScenarioEntry = null;
        if (_campaign is not null)
            _campaign.Changed -= Refresh;

        _campaign = Campaign.CreateBootstrap(_campaignSeed);
        ResetTrends();
        _lastAutosaveDay = -1;
        _campaignInProgress = false;

        // Day-zero proof that the emergent economy still reproduces the scenario's
        // authored 2024 figures. If this ever drifts, the calibration broke, not the map.
        GD.Print("[dof] " + Simulation.EconomySystem.LastCalibrationReport);
        if (IsInstanceValid(_scenarioSummary))
            _scenarioSummary.Text = Simulation.Loc.Format("{0} countries · {1} regions", _campaign.Nations.Count, _campaign.States.Count);
        BindCampaignMods();
        ApplyCustomCursors();

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
        // A flag design saved in the editor, or a mod's designs re-read, changes which flag a
        // country flies exactly as a new picture would.
        Simulation.FlagDesignCatalog.Changed += ForgetFlags;
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
        // The stage that runs this takes ten and a half seconds of a fifteen-second load, and the
        // screens it builds are timed individually into the performance channel: a table beats an
        // opinion about which screen is the slow one. `state_affairs` alone is 1.2 s.
        var interfaceClock = System.Diagnostics.Stopwatch.StartNew();
        void Built(string name, System.Action build)
        {
            interfaceClock.Restart();
            build();
            Simulation.DiagnosticLogs.Write("performance",
                $"INTERFACE_PART part={name} ms={interfaceClock.Elapsed.TotalMilliseconds:F1}");
        }

        Built("modding", InitializeModding);
        var background = new ColorRect
        {
            Color = Ui.Colour("surface.background"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(background);

        Built("globe", () =>
        {
            _globe = new GlobePresentation();
            _modLoader.SetCrossoverPresentation = enabled => _globe.SetCrossoverPresentation(enabled);
            _globe.CountryPicked += OnCountryPicked;
            _globe.UnclaimedPicked += OnUnclaimedPicked;
            _globe.ArmyPicked += OnArmyPicked;
            _globe.FleetPicked += OnFleetPicked;
            _globe.NuclearFlashed += FlashScreen;
            _globe.SupportPicked += OnSupportPicked;
            _globe.SupportBoxPicked += OnSupportBoxPicked;
            _globe.BattlePicked += ShowBattleDetails;
            _globe.ArmiesBoxPicked += OnArmiesBoxPicked;
            _globe.ProvincesBoxSelected += OnRegionsBoxSelected;
            _globe.FleetsBoxPicked += OnFleetsBoxPicked;
            _globe.ProvinceCommanded += OnProvinceCommanded;
            AddChild(_globe);
        });

        Built("menu", BuildMainMenu);
        // The country picker (2 980 ms) is built on first open, in OpenCountryPicker and
        // OpenLocalMultiplayer: a player who starts a campaign from a save never needs it.
        Built("hud", BuildHud);
        Built("movement", BuildMovementPanel);
        Built("hotseat", BuildHotseatInterface);
        Built("network", BuildNetworkMultiplayerInterface);
        // Over the campaign and its interface, under the pause menu and the console.
        Built("world_curse", BuildWorldCurseLayer);
        Built("pause_menu", BuildPauseMenu);
        Built("save_load", BuildSaveLoadInterface);
        Built("events", BuildEventInterface);
        Built("peace_offer", BuildPeaceOfferInterface);
        Built("audio", BuildStrategicAudio);
        Built("console", BuildDeveloperConsole);
        Built("free_camera", BuildFreeCameraOverlay);
        Built("observer_hint", BuildObserverHint);
        Built("settings", BuildSettingsInterface);

        // Last, because it reaches into everything the lines above have just built. From
        // here on the screen and the running game cannot disagree: both go through
        // ApplySetting, and this is the same call the screen makes when a row changes.
        ApplyAllSettings();
    }

    /// <summary>
    /// <summary>
    /// Screens that are built the first time somebody opens them, not at load.
    ///
    /// Every screen used to be constructed on the first frame of a campaign whether or not the
    /// player would ever open it, and the stage that did it took ten and a half seconds - a third
    /// of the whole load. The measurement named the two worst: the state-affairs workspace at
    /// 1 221 ms and production at 404 ms. Both are modal pages with a lot of rows and textures,
    /// both are reached by one button, and neither is needed to draw a map.
    ///
    /// The guard is a set rather than a null check because a layer that failed to build must not
    /// be built again on every open, and because the name says what it is at the call site.
    /// </summary>
    private readonly System.Collections.Generic.HashSet<string> _builtScreens =
        new(StringComparer.Ordinal);

    private void EnsureScreen(string key, System.Action build)
    {
        if (_builtScreens.Add(key)) build();
    }

    /// <summary>
    /// The heavy screens, built while the loading screen still covers the map. Built on first
    /// open, each froze the running game the first time a player reached for it - state affairs
    /// 0.7 s, production 0.4 s, the situation room 0.25 s, measured in the game. Under the
    /// loading screen the same second and a half is part of a wait nobody is playing through.
    /// </summary>
    private void PrebuildScreens()
    {
        if (_campaign?.Player is null || _hudLayer is null) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            EnsureScreen("state_affairs", BuildStateAffairsInterface);
            EnsureScreen("production", BuildProductionScreen);
            if (_situationLayer is null) BuildSituationRoom();
            WarmDiplomacyRows();
        }
        catch (Exception exception)
        {
            // Reported here; EnsureScreen does not retry a screen that failed, opened or not.
            Simulation.DiagnosticLogs.Write("errors", "PREBUILD " + exception.Message);
        }
        Simulation.DiagnosticLogs.Write("performance", $"PREBUILD_SCREENS ms={clock.Elapsed.TotalMilliseconds:F1}");
    }

    private void BuildHud()
    {
        _hudLayer = FullLayer("GameHud");
        _hudLayer.Visible = false;
        AddChild(_hudLayer);
        BuildMapVignette();
        // Every interface picture, from loader threads, before a screen asks for it (TextureWarmup).
        TextureWarmup.Start(this);

        BuildTopBar();
        BuildCampaignNavigation();
        BuildDossierSidePanel();

        BuildArmyPanel();
        BuildFleetPanel(_hudLayer);

        // Every screen below is built here, on the first frame of a campaign, whether or not the
        // player will open it. The stage that does it takes ten and a half seconds - a third of
        // the whole load - and the honest way to cut that is to know which screen costs what
        // before restructuring anything. Each builder is timed into the performance channel, so
        // the table is there on the next run.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Timed(string name, System.Action build)
        {
            watch.Restart();
            build();
            Simulation.DiagnosticLogs.Write("performance",
                $"INTERFACE_BUILD screen={name} ms={watch.Elapsed.TotalMilliseconds:F1}");
        }

        Timed("peace", BuildPeaceConference);
        Timed("ledger", BuildLedgerInterface);
        // State affairs and production are built on first open: 1.6 s of the ten-second stage,
        // spent before the player has asked for either. See EnsureScreen.
        Timed("war_ledger", BuildWarLedger);
        Timed("diplomacy", BuildDiplomacyScreen);
        Timed("policy", BuildPolicyWorkspace);
        Timed("trade", BuildTradeScreen);
        Timed("support", BuildSupportScreen);
        Timed("research", BuildResearchScreen);
        Timed("doctrine", BuildDoctrineScreen);
        Timed("handbook", BuildHandbook);
        Timed("conflicts", BuildConflictsScreen);
        Timed("map_controls", BuildMapControls);
        Timed("strategic_hud", BuildStrategicHud);
        Resized += LayoutCampaignShell;
        LayoutCampaignShell();
    }

    private void BeginSession()
    {
        _globe.HighlightNation(-1);
        _globe.ClearSelection();
        _observerMode = _campaign.HumanSeats.Count == 0 && !_campaign.Nations.Any(n => n.IsHuman);
        if (_pauseMenuLayer is not null) _pauseMenuLayer.Visible = false;
        if (_eventLayer is not null) _eventLayer.Visible = false;
        _menuLayer.Visible = false;
        if (_pickerLayer is not null) _pickerLayer.Visible = false;
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

    private void OpenScreen(string screen)
    {
        CancelConstructionPlacement();
        CancelRecruitmentPlacement();
        if (screen == "Situation") { OpenSituationRoom(); return; }
        if (_situationLayer is not null) _situationLayer.Visible = false;
        _situationReturn = "";
        if (_sovereignFinanceLayer is not null) _sovereignFinanceLayer.Visible = false;
        if (!Simulation.MechanicFeatures.ScreenEnabled(_campaign, screen)) return;
        _sideScreenBuiltKey = $"{screen}|{_dossierTab}|{_campaign?.Player?.Id}|" +
            (_campaign is null ? 0 : _campaign.ElapsedDays / SideScreenInterval(screen));
        if (screen == "Diplomacy") MarkFirstStep("diplomacy");
        if (screen != "Conflicts" && _conflictsLayer is not null) _conflictsLayer.Visible = false;
        if (screen == "Conflicts")
        {
            OpenConflictsScreen();
            return;
        }
        if (screen == "State Affairs") MarkFirstStep("budget");
        if (screen != "Laws" && _policyLayer is not null) _policyLayer.Visible = false;
        if (screen != "Support" && _supportLayer is { Visible: true }) CloseSupportScreen();
        if (_resourceAtlasPanel is not null) _resourceAtlasPanel.Visible = false;
        if (_researchLayer is not null) _researchLayer.Visible = false;
        if (_doctrineLayer is not null) _doctrineLayer.Visible = false;
        if (screen == "Research") { OpenResearchScreen(); return; }
        if (screen == "Doctrines") { OpenDoctrineScreen(); return; }
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
        // Diplomacy, trade and production are full pages rather than side dossiers: they
        // are the three places a player decides something with the whole world in view.
        if (screen == "Diplomacy")
        {
            OpenDiplomacyScreen();
            UpdateScreenButtons();
            return;
        }
        if (screen == "Trade")
        {
            OpenTradeScreen();
            UpdateScreenButtons();
            return;
        }
        if (screen == "Production")
        {
            OpenProductionScreen();
            UpdateScreenButtons();
            return;
        }
        // The long-range forces: the guns, aircraft and drones that reach into the war
        // from bases instead of standing in it. A full page for the same reason the
        // other three are: it is where a player decides something.
        if (screen == "Support")
        {
            OpenSupportScreen();
            UpdateScreenButtons();
            return;
        }
        if (_ledgerLayer is not null) _ledgerLayer.Visible = false;
        if (_warLedgerLayer is not null) _warLedgerLayer.Visible = false;
        if (_diplomacyLayer is not null) _diplomacyLayer.Visible = false;
        if (_tradeScreenLayer is not null) _tradeScreenLayer.Visible = false;
        if (_productionLayer is not null) _productionLayer.Visible = false;
        if (_supportLayer is not null) _supportLayer.Visible = false;
        RefreshSideScreenContents(screen);
    }

    private void RefreshSideScreenContents(string screen)
    {
        _sideScreenBuiltKey = $"{screen}|{_dossierTab}|{_campaign?.Player?.Id}|" +
            (_campaign is null ? 0 : _campaign.ElapsedDays / SideScreenInterval(screen));
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
                if (Factions.Of(nation) is { } faction)
                {
                    // A de facto power says what it is before anything else: the Houthis are not
                    // a country the way Oman is, and a player clicking Sanaa should be told so.
                    _sideBody.Text += "\n\n" + Simulation.Loc.Get(faction.Description);
                    DetailHeading("DE FACTO POWER");
                    DetailRow("Recognition", Tr("not recognised as a state"),
                        Tr("A real organisation that held and governed ground of its own on 1 January 2024 without being a recognised state. It is at war, in a truce or at peace with the state it holds ground of, as it was that morning; that state can take the ground back whole if it wins."),
                        "state.warning");
                    if (_campaign.NationById(nation.SecededFromId) is { } holdsOf)
                        DetailRow("Holds ground of", holdsOf.DisplayName);
                    var patrons = faction.Patrons.Select(tag => _campaign.NationByTag(tag)?.DisplayName)
                        .Where(name => !string.IsNullOrEmpty(name)).ToArray();
                    if (patrons.Length > 0)
                        DetailRow("Backed by", string.Join(", ", patrons),
                            Tr("States that keep it going with money, weapons or soldiers on the ground. A country thinking of attacking it counts them."));
                }
                DetailHeading("STATE");
                DetailRow(CountryNames.CapitalTitle(_campaign, nation), CountryNames.Capital(nation, _campaign));
                DetailRow("States held", _campaign.StatesOf(nation).Length.ToString("N0"));
                DetailRow("Population", Fmt.People(nation.Population), Fmt.PeopleExact(nation.Population));
                // Who the country says it is, where a player looks first. The exact share is
                // on the line rather than behind a tooltip: the number a declaration is
                // judged by is the number the player needs.
                if (_campaign.FeatureEnabled("state_identity"))
                {
                    // A declaration against half the country is the one state of these lines
                    // that is a problem rather than a fact: colour it so, because the strain
                    // is already being paid every week.
                    var declaredFaithShare = StateIdentity.IsSecular(nation.DeclaredReligion)
                        ? 1f
                        : StateIdentity.ReligionShare(_campaign, nation, nation.DeclaredReligion);
                    DetailRow("State religion", StateIdentityShareLine(nation, true),
                        Tr("The faith the state declares. Declaring one needs the faithful to be a share of the country; a secular declaration needs nobody's permission."),
                        declaredFaithShare < 0.5f ? "state.warning" : "text.bright");
                    DetailRow("State culture", StateIdentityShareLine(nation, false),
                        Tr("The people the state declares itself of, by the share of the country that lives it."));
                    var faith = StateIdentity.Headline(_campaign, nation, true);
                    var people = StateIdentity.Headline(_campaign, nation, false);
                    if (faith.Id is not null && !faith.Id.Equals(nation.DeclaredReligion, StringComparison.OrdinalIgnoreCase))
                        DetailRow("Largest faith", Simulation.Loc.Format("{0} · {1:0.0%} of the country",
                            StateIdentity.Name(faith.Id, true), faith.Share),
                            Tr("The faith most people of the country hold, whether or not the state declares it."));
                    if (people.Id is not null && !people.Id.Equals(nation.DeclaredCulture, StringComparison.OrdinalIgnoreCase))
                        DetailRow("Largest people", Simulation.Loc.Format("{0} · {1:0.0%} of the country",
                            StateIdentity.Name(people.Id, false), people.Share),
                            Tr("The people most of the country belongs to, whether or not the state declares it."));
                }
                DetailHeading("ECONOMY");
                DetailRow("GDP", Fmt.Money(nation.Gdp), Fmt.MoneyExact(nation.Gdp));
                DetailRow("GDP per head", Fmt.Money(nation.Gdp / Mathf.Max(1f, nation.Population)),
                    "Annual output divided by everyone living in the country.");
                DetailRow("Treasury", Fmt.Money(nation.Treasury), Fmt.MoneyExact(nation.Treasury),
                    nation.Treasury < 0f ? "state.negative" : "text.bright");
                DetailRow("Tax rate", Fmt.Percent(nation.TaxRate));
                // What a player opening a country first wants to know: is it at war, and with
                // whom is it likeliest to be next.
                DetailHeading("SITUATION");
                var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id)).ToList();
                DetailRow("Wars", wars.Count == 0 ? Tr("at peace")
                        : wars.Count == 1 ? WarNames.Of(_campaign, wars[0])
                        : Simulation.Loc.Format("{0} and {1} more", WarNames.Of(_campaign, wars[0]), wars.Count - 1),
                    string.Join("\n", wars.Select(war => WarNames.Of(_campaign, war))),
                    wars.Count == 0 ? "text.bright" : "state.negative");
                var standoff = Escalation.Hottest(_campaign, 25f)
                    .FirstOrDefault(relation => (relation.A == nation.Id || relation.B == nation.Id) &&
                                                !CampaignDiplomacy.AtWar(_campaign, relation.A, relation.B));
                if (standoff is not null && _campaign.NationById(standoff.A == nation.Id ? standoff.B : standoff.A) is { } rival)
                {
                    var stage = (TensionStage)standoff.TensionStage;
                    DetailRow("Hottest standoff", Simulation.Loc.Format("{0} · {1}", CountryNames.Compact(rival), Tr(StageName(stage))),
                        Tr("The country this one is likeliest to fight next, and how far the tension between them has gone. The Conflicts screen has the rest."),
                        stage >= TensionStage.Clashes ? "state.critical" : stage >= TensionStage.Crisis ? "state.warning" : "text.bright");
                }
                DetailHeading("GOVERNMENT");
                DetailRow("Form of government", Tr(PoliticsCore.GovernmentOf(nation).Name),
                    Tr(PoliticsCore.GovernmentOf(nation).Description));
                DetailRow("Ideology", Tr(PoliticsCore.IdeologyOf(nation).Name),
                    Tr(PoliticsCore.IdeologyOf(nation).Description));
                var coup = Coups.Outlook(_campaign, nation);
                DetailRow("Coup risk",
                    Tr(coup switch { "plot" => "a plot is under way", "high" => "high", "low" => "low", _ => "none" }),
                    Simulation.Loc.Format("Below {0} stability officers may start plotting, the more the lower it falls - most of all against a junta or a provisional government, when a war is being lost, or after a coup next door. A plot ripens in {1} days unless stability is back above the line.",
                        Fmt.Percent(_campaign.Defines.CoupStabilityThreshold), _campaign.Defines.CoupPlotDays),
                    coup switch { "plot" => "state.critical", "high" => "state.warning", _ => "text.bright" });
                // A promise the government has made in public is a fact of this dossier: the
                // date is public, and so is the day it was kept or missed.
                if (nation.Promises.Count > 0)
                {
                    DetailHeading("PROMISES");
                    foreach (var active in nation.Promises.OrderBy(promise => promise.DueDay))
                    {
                        if (PromiseCatalog.Find(active.Id) is not { } definition) continue;
                        var daysLeft = Math.Max(0, active.DueDay - _campaign.ElapsedDays);
                        DetailRow("Promise", Simulation.Loc.Format("{0} · {1}", Tr(definition.Name),
                                Simulation.Loc.Format("Days left: {0}", daysLeft)),
                            Tr("A promise made in public. Keeping it earns trust; missing the deadline costs more than never promising. The result is measured the same way every other condition in the game is."),
                            daysLeft <= 0 ? "state.warning" : "text.bright");
                    }
                }
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
                var seasonThere = Simulation.WaterConditions.SeasonCaption(_campaign, nation.CapitalLatitude);
                if (seasonThere.Length > 0)
                    DetailRow("Season", seasonThere,
                        "The season follows the calendar and this capital's latitude. Winter slows roads away from the equator.");
                var mood = Simulation.PoliticalCohorts.StrainCaption(_campaign, nation);
                if (mood.Length > 0)
                    DetailRow("Public mood", mood,
                        "Political opinion, read from the regions. A liked country is allowed to say so.",
                        mood == "Liked enough to argue in the open." ? "state.positive" : "state.warning");
                var loud = Simulation.PoliticalCohorts.Loudest(_campaign, nation);
                if (loud is not null && (loud.Cause.Length > 0 || loud.Heat >= 0.35f))
                    DetailRow("Loudest region", loud.Name.Length > 0 ? loud.Name : "—",
                        loud.Cause.Length > 0
                            ? loud.Cause
                            : "The region whose politics have left the government furthest. The unrest map uses the same heat.",
                        loud.Heat >= 0.55f ? "state.critical" : loud.Heat >= 0.35f ? "state.warning" : "text.bright");
                var worn = Simulation.MovementRules.WornWelcome(_campaign, nation);
                if (worn is not null)
                    DetailRow("Headquarters here", worn.DisplayName,
                        Simulation.MovementRules.Patience(_campaign, worn),
                        worn.Movement.Heat >= _campaign.Defines.MovementExpelHeat * 0.75f ? "state.negative" : "state.warning");
                var (birthRate, deathRate, _, vitalStatus) = Simulation.HouseholdEconomy.NationalVitalSummary(_campaign, nation);
                DetailRow("Demographic balance",
                    Simulation.Loc.Format("{0} ({1:+0.00%;-0.00%;0.00%}/yr)", Tr(vitalStatus), birthRate - deathRate),
                    Simulation.DemographicCycle.Explain(_campaign, nation),
                    deathRate > birthRate ? "state.warning" : birthRate - deathRate >= 0.012f ? "state.positive" : "text.bright",
                    () =>
                    {
                        var b = new Simulation.Breakdown();
                        Simulation.HouseholdEconomy.NationalVitalSummary(_campaign, nation, b);
                        return b;
                    });
                DetailRow("Age structure",
                    Simulation.Loc.Format("{0} young · {1} work · {2} old",
                        Fmt.Percent(nation.YoungShare), Fmt.Percent(nation.WorkingShare), Fmt.Percent(nation.OldShare)),
                    Simulation.DemographicCycle.Explain(_campaign, nation),
                    nation.OldShare >= _campaign.Defines.DemographyReferenceOldShare + 0.04f ? "state.warning" : "text.bright",
                    () => Simulation.HouseholdEconomy.ExplainNationPopulation(_campaign, nation));
                if (nation.WeeklyNetMigration != 0 || nation.CumulativeNetMigration != 0 || MathF.Abs(nation.BrainDrainRate) >= 0.01f)
                {
                    var destNation = _campaign.NationById(nation.BrainDrainDestinationId);
                    var srcNation = _campaign.NationById(nation.BrainGainSourceId);
                    var migrationTip = destNation is not null
                        ? Simulation.Loc.Format("Weekly migration {0} (cumulative {1}). Skilled specialists are moving primarily to {2}.",
                            Fmt.PeopleDelta(nation.WeeklyNetMigration), Fmt.PeopleDelta(nation.CumulativeNetMigration), destNation.DisplayName)
                        : srcNation is not null
                            ? Simulation.Loc.Format("Weekly migration {0} (cumulative {1}). Skilled specialists are arriving primarily from {2}.",
                                Fmt.PeopleDelta(nation.WeeklyNetMigration), Fmt.PeopleDelta(nation.CumulativeNetMigration), srcNation.DisplayName)
                            : Simulation.Loc.Format("Weekly migration {0} (cumulative {1}).",
                                Fmt.PeopleDelta(nation.WeeklyNetMigration), Fmt.PeopleDelta(nation.CumulativeNetMigration));
                    DetailRow("Net migration", Fmt.PeopleDelta(nation.WeeklyNetMigration), migrationTip,
                        nation.WeeklyNetMigration < 0 ? "state.warning" : nation.WeeklyNetMigration > 0 ? "state.positive" : "text.bright");
                    DetailRow("Brain drain",
                        nation.BrainDrainRate < -0.005f
                            ? Simulation.Loc.Format("−{0}", Fmt.Percent(MathF.Abs(nation.BrainDrainRate)))
                            : nation.BrainDrainRate > 0.005f
                                ? Simulation.Loc.Format("+{0}", Fmt.Percent(nation.BrainDrainRate))
                                : Tr("balanced"),
                        Tr("Human-capital flow: war, low stability and weak living standards push educated specialists abroad; universities, research laboratories and high living standards retain and attract them."),
                        nation.BrainDrainRate < -0.15f ? "state.negative" : nation.BrainDrainRate < -0.04f ? "state.warning" : nation.BrainDrainRate > 0.04f ? "state.positive" : "text.bright",
                        () =>
                        {
                            var b = new Simulation.Breakdown();
                            var rep = _campaign.StatesOf(nation).OrderByDescending(s => s.BrainDrainPressure).FirstOrDefault();
                            if (rep is not null)
                                Simulation.BrainDrain.CalculatePressure(_campaign, nation, rep, Simulation.AiFiscalPolicy.AtWar(_campaign, nation), b);
                            return b;
                        });
                }
                break;

            case "Economy":
                ShowEconomyScreen();
                break;
            case "Laws":
                ShowPoliticalPolicies();
                break;

            case "Military":
                var armies = _campaign.Armies.Where(army => army.NationId == player.Id).ToArray();
                var divisions = armies.Sum(army => army.Divisions);
                var fleets = _campaign.Fleets.Where(fleet => fleet.NationId == player.Id).ToArray();
                var personnel = MilitaryBootstrap.Personnel(player.Tag, player.Population);
                _sideBody.Text = Simulation.Loc.Format("{0} armed forces", player.DisplayName);

                DetailHeading("FORCES");
                _sideDetail.AddChild(ActionButton("Recruit & deploy", () => OpenRecruitment(), 180, 38, true));
                var doctrineTabBtn = ActionButton("Doctrines tab", OpenArmyDoctrinePage, 180, 38,
                    iconPath: "res://game/gfx/interface/icons/icon_military.png");
                doctrineTabBtn.Name = "MilitaryOpenDoctrineTab";
                _sideDetail.AddChild(doctrineTabBtn);
                var warBookBtn = ActionButton(Loc.Get("Open the book of war"), OpenDoctrineScreen, 180, 38,
                    iconPath: "res://game/gfx/interface/icons/icon_laws.png");
                warBookBtn.Name = "MilitaryOpenDoctrineScreen";
                _sideDetail.AddChild(warBookBtn);
                var generalsBtn = ActionButton(Loc.Get("Generals"), OpenGeneralsScreen, 180, 38,
                    iconPath: "res://game/gfx/interface/icons/icon_military.png");
                generalsBtn.Name = "MilitaryOpenGenerals";
                generalsBtn.TooltipText = Ui.Tip(Tr("Every army group's commander, their record, and relieving one."));
                _sideDetail.AddChild(generalsBtn);
                DetailRow("Divisions", divisions.ToString("0.#"),
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
                // Routed to the full diplomacy screen before the switch is reached; the
                // case stays so the screen set names a destination the switch can talk
                // about if the routing above ever changes.
                ShowCountryInteraction(nation, "Diplomatic overview");
                break;


            case "Decisions":
                ShowDecisionsScreen();
                break;
        }
        ApplyDossierTab();
    }

    private void CloseSidePanel() => CloseSidePanel(dropRegionSelection: true);

    private void CloseSidePanel(bool dropRegionSelection)
    {
        // A region dossier is the only side panel that owns map ground. Closing it must take
        // the tint off that ground too; every other dossier leaves the map alone, and a panel
        // making way for the movement card keeps the region its actions are aimed at.
        var closeRegionSelection = dropRegionSelection && _regionControls is { Visible: true };
        ForgetDossierReturn();
        _sidePanel.Visible = false;
        _globe.SetMapModeSubject(null);
        _openScreen = "";
        UpdateScreenButtons();
        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
        if (closeRegionSelection) ClearRegionSelection();
    }

    private void OnCountryPicked(GlobePick pick)
    {
        if (TryConstructionMapPick(pick)) return;
        if (TryRecruitmentMapPick(pick)) return;
        PlayStrategicCue("map_select");
        if (_networkLayer is not null && _networkLayer.Visible)
        {
            SelectNetworkCountry(pick.Nation, false);
            return;
        }
        _selectedNation = pick.Nation;
        _selectedState = pick.State;
        _selectedProvinceId = pick.ProvinceId;
        // Its army is drawn while it is the country being looked at, whatever the map shows otherwise.
        _globe.ShownCountryId = pick.Nation?.Id ?? -1;
        if (_pickerLayer is { Visible: true })
        {
            SetPickerNation(pick.Nation, false);
            UpdatePickerLocation(pick);
            return;
        }
        if (!_hudLayer.Visible) return;
        // The long-range forces panel takes a click on the map for itself: where the selected
        // formations are rebased, or the base a new one is raised into.
        if (TrySupportMapPick(pick)) return;
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
        // A left click on the map always opens the picked country's card on the left and its
        // ground along the bottom. The floating diplomacy popup stays behind the flag.
        ShowMapSelection(pick);

        // Direct inter-country actions from the map: a floating diplomacy menu at the cursor,
        // only for a country that is not ours. It closes on a click outside or on Escape.
        if (_mapLeftClickInteractionEnabled &&
            pick.Nation is not null && pick.Nation != _campaign.Player && _hudLayer.Visible)
            ShowMapDiplomacyMenu(pick.Nation, GetViewport().GetMousePosition());
    }

    private void TogglePause()
    {
        if (IsNetworkClient)
        {
            _networkHudStatus.Text = Tr("Only the host controls campaign time.");
            return;
        }
        _campaign.IsPaused = !_campaign.IsPaused;
        _dayAccumulator = 0;
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
        _dayAccumulator = 0;
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
        RefreshTopBar(player);
        RefreshStrategicAudio();
        RefreshEventInterface();
        RefreshPeaceOfferInterface();
        RefreshNegotiationNotifications();
        RefreshSituationRoom();
        RefreshSituationDesk();
        RefreshStrategicHud();
        RefreshBattleDossier();
        if (_ledgerLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_ledgerLayer)) RefreshLedger();
        RefreshRecruitment();
        if (_stateAffairsLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_stateAffairsLayer)) RefreshStateAffairs();
        // These are retained labels/buttons: updating them does not destroy numeric input.
        if (_sovereignFinanceLayer is { Visible: true }) RefreshSovereignFinanceWorkspace();
        if (_warLedgerLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_warLedgerLayer)) RefreshWarLedger();
        if (_diplomacyLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_diplomacyLayer)) RefreshDiplomacyScreen();
        if (_policyLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_policyLayer)) RefreshPolicyWorkspace();
        if (_doctrineLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_doctrineLayer)) RefreshDoctrineScreen();
        if (_conflictsLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_conflictsLayer)) RefreshConflictsScreen();
        if (_tradeScreenLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_tradeScreenLayer)) RefreshTradeScreen();
        if (_productionLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_productionLayer)) RefreshProductionScreen();
        if (_supportLayer is { Visible: true } && !InterfaceInteractionGuard.Busy(_supportLayer)) RefreshSupportScreen();
        if (_researchLayer is { Visible: true }) RefreshResearchScreen();
        if (_armyPanel is { Visible: true }) RefreshArmyPanel();
        else if (_sidePanel.Visible && _openScreen is "Wars" or "Military" or "Economy" or "Nation" or "Decisions" or "Laws" &&
                 SideScreenDue())
        {
            var scroll = _dossierScroll.ScrollVertical;
            RefreshSideScreenContents(_openScreen);
            Callable.From(() => { if (_dossierScroll is not null) _dossierScroll.ScrollVertical = scroll; }).CallDeferred();
        }
    }

    /// <summary>
    /// A world line in the right rail flies the camera to the same place a map ring would.
    /// It stays silent. The world writes several of these a day, and a cue on each one
    /// is noise, not news.
    /// </summary>
    private void FocusNews(NewsFeed.Line line)
    {
        if (_campaign is null || _globe is null) return;
        if (!NewsPlace(line.ActorId, line.TargetId, line.Kind, out var latitude, out var longitude)) return;
        _globe.FocusOn(latitude, longitude, 5.5f);
        PlayStrategicCue("map_focus");
    }

    private bool NewsPlace(int actorId, int targetId, string kind, out float latitude, out float longitude)
    {
        latitude = longitude = 0f;
        var actor = _campaign.NationById(actorId);
        var target = _campaign.NationById(targetId);
        if (actor?.Movement is not null && target is not null) (actor, target) = (target, null);
        if (actor is null && target is null) return false;
        if (actor is not null && target is not null && target.Movement is null)
        {
            var dLon = target.CapitalLongitude - actor.CapitalLongitude;
            while (dLon > 180f) dLon -= 360f;
            while (dLon < -180f) dLon += 360f;
            if (MathF.Abs(target.CapitalLatitude - actor.CapitalLatitude) + MathF.Abs(dLon) < 18f)
            {
                latitude = (actor.CapitalLatitude + target.CapitalLatitude) * 0.5f;
                longitude = actor.CapitalLongitude + dLon * 0.5f;
                return true;
            }
            if (kind is "war_started" or "border_war" or "world_war_began") actor = target;
        }
        var at = actor ?? target;
        if (at.CapitalLatitude == 0f && at.CapitalLongitude == 0f) return false;
        latitude = at.CapitalLatitude;
        longitude = at.CapitalLongitude;
        return true;
    }

    // What the side panel was last built for. Refresh runs many times a second; the panel
    // used to be torn down and rebuilt on every one of them - sixty formable-nation cards,
    // each counting thousands of provinces, several times a second, which froze the game
    // and threw away whatever the player was typing into the search box.
    private string _sideScreenBuiltKey = "";

    private string SideScreenKey()
    {
        return $"{_openScreen}|{_dossierTab}|{_campaign.Player?.Id}|{_campaign.ElapsedDays / SideScreenInterval(_openScreen)}";
    }

    // Formable nations change slowly and cost the most to count: a month at a time; the
    // opportunity atlas a week; everything else a day.
    private int SideScreenInterval(string screen) => screen != "Decisions" ? 1 :
        _dossierTab == "Formables" ? 30 : _dossierTab == "Opportunities" ? 7 : 1;

    private bool SideScreenDue()
    {
        if (InterfaceInteractionGuard.Busy(_sidePanel)) return false;
        // Never under the player's fingers: a rebuild takes the text field they are typing in.
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox &&
            _sidePanel.IsAncestorOf(GetViewport().GuiGetFocusOwner())) return false;
        return SideScreenKey() != _sideScreenBuiltKey;
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
    /// One restrained frame for campaign windows. Surface roles change depth and padding,
    /// while the shell palette remains moddable and brass marks only an interruption.
    /// </summary>
    private static StyleBoxFlat CampaignPanelStyle(PanelSurface surface)
    {
        var style = ShellPanelStyle();
        var modal = surface == PanelSurface.Modal;
        style.BgColor = Ui.Colour(modal ? "surface.raised" : "shell.background");
        style.BorderColor = Ui.Colour(modal ? "button.primary_border" : "surface.line", modal ? 0.78f : 1f);
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(modal ? 4 : 3);
        style.SetContentMarginAll(modal ? 8 : 4);
        style.ShadowColor = Ui.Colour("surface.background", surface switch
        {
            PanelSurface.Modal => 0.72f,
            PanelSurface.Dossier => 0.50f,
            PanelSurface.Command => 0.42f,
            _ => 0.30f
        });
        style.ShadowSize = surface switch
        {
            PanelSurface.Modal => 12,
            PanelSurface.Dossier => 8,
            PanelSurface.Command => 6,
            _ => 4
        };
        style.ShadowOffset = new Vector2(0, modal ? 4 : 2);
        return style;
    }

    private PanelContainer TexturedPanel(Node parent, PanelSurface surface = PanelSurface.Dossier)
    {
        var panel = new PanelContainer
        {
            ClipContents = true
        };
        var inCampaign = _hudLayer is not null && (parent == _hudLayer || _hudLayer.IsAncestorOf(parent));
        panel.SetMeta("skin_panel_surface", (int)surface);
        panel.SetMeta("skin_campaign_surface", inCampaign);
        panel.AddThemeStyleboxOverride("panel", PanelStyle(surface, inCampaign));
        parent.AddChild(panel);
        AtlasSurfaceSkin.Attach(panel, surface is PanelSurface.Command or PanelSurface.CommandStrip
            ? AtlasSurfaceSkin.Level.Instrument : AtlasSurfaceSkin.Level.Document);
        if (!inCampaign)
        {
            var edge = new Panel
            {
                Name = "ShellEdge",
                MouseFilter = MouseFilterEnum.Ignore,
                FocusMode = FocusModeEnum.None
            };
            edge.AddThemeStyleboxOverride("panel", ShellEdgeStyle());
            panel.AddChild(edge);
            panel.MoveChild(edge, 1);
        }
        panel.Resized += panel.QueueRedraw;
        panel.Draw += () =>
        {
            if (panel.Size.X <= 8f) return;
            var modal = surface == PanelSurface.Modal;
            // Depth rather than a flat fill: a faint light falling from the top edge and fading out
            // by mid-panel, the way every engine's default window reads as a lit surface. One quad,
            // drawn only when the panel is resized or restyled.
            var top = Ui.Colour("text.body", modal ? 0.055f : 0.035f);
            var clear = new Color(top, 0f);
            var fall = MathF.Min(panel.Size.Y * 0.45f, 260f);
            panel.DrawPolygon(
                new[] { new Vector2(1f, 1f), new Vector2(panel.Size.X - 1f, 1f), new Vector2(panel.Size.X - 1f, fall), new Vector2(1f, fall) },
                new[] { top, top, clear, clear });
            var light = Ui.Colour(modal ? "button.primary_border" : "text.body", modal ? 0.16f : 0.08f);
            panel.DrawLine(new Vector2(4f, 1.5f), new Vector2(panel.Size.X - 4f, 1.5f), light, 1f, true);
        };
        // Windows arrive rather than appear: a short fade and a few pixels of lift whenever the
        // panel becomes visible, whichever layer or button showed it.
        if (surface is PanelSurface.Modal or PanelSurface.Dossier)
            panel.VisibilityChanged += () => AnimateArrival(panel);
        return panel;
    }

    /// <summary>The arrival of a window: 140 ms of fade and lift, skipped when motion is turned down.</summary>
    private static void AnimateArrival(Control panel)
    {
        if (!panel.IsVisibleInTree() || !panel.IsInsideTree()) return;
        if (Ui.Metric("ui.motion", 1f) <= 0f) return;
        if (panel.HasMeta("arrival_tween") && panel.GetMeta("arrival_tween").AsGodotObject() is Tween running && running.IsValid())
            running.Kill();
        panel.Modulate = new Color(1f, 1f, 1f, 0f);
        panel.PivotOffset = panel.Size * 0.5f;
        panel.Scale = new Vector2(0.985f, 0.985f);
        var tween = panel.CreateTween().SetParallel().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        tween.TweenProperty(panel, "modulate:a", 1f, 0.14f);
        tween.TweenProperty(panel, "scale", Vector2.One, 0.16f);
        panel.SetMeta("arrival_tween", tween);
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

    public static string TranslateOpinionDriver(string source)
    {
        if (string.IsNullOrEmpty(source)) return "";
        var paren = source.LastIndexOf(" (x", StringComparison.Ordinal);
        if (paren > 0 && source.EndsWith(")"))
            return Tr(source.Substring(0, paren)) + source.Substring(paren);
        return Tr(source);
    }

    private static Label Title(string text, int size, Color color, bool upper = false)
    {
        // One door for type. Every title in the game comes through here, so the scale and the
        // two faces are applied once instead of in six hundred call sites: an arbitrary size
        // is snapped to the nearest step, a heading-sized step is set in the display face, and
        // an upper-case micro-label gets the letter spacing capitals need.
        //
        // Capitals are recognised, not only requested. Half of the interface's section labels
        // were written as "POSTURE" in the source and never passed `upper`, so they were set in
        // crowded capitals with no tracking - the single loudest "made in a hurry" signal a dark
        // interface can send. Asking whether the text is already in capitals costs one pass and
        // fixes every one of them at once.
        var caps = upper || LooksUppercase(text);
        var step = Ui.TypeStep(size);
        var label = new Label
        {
            Text = upper ? Tr(text).ToUpperInvariant() : Tr(text),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.SetMeta(LocSourceMeta, text);
        if (caps) label.SetMeta(LocUpperMeta, true);
        label.SetMeta(LocAppliedMeta, label.Text);
        label.AddThemeFontSizeOverride("font_size", step);
        if (caps) label.AddThemeFontOverride("font", Ui.Tracked());
        else if (step >= Ui.DisplayThreshold)
            // A big heading is set in the serif, and a big figure is not: digits in a serif
            // read as a quotation, digits in the tabular face read as a measurement.
            label.AddThemeFontOverride("font", FigureLike(label.Text) ? Ui.NumberFont : Ui.DisplayFont);
        label.AddThemeColorOverride("font_color", color);
        var colourToken = Ui.ColourToken(color);
        if (colourToken.Length > 0)
        {
            label.SetMeta("skin_label_colour", colourToken);
            label.SetMeta("skin_label_applied", color);
        }
        return label;
    }

    /// <summary>Whether a string is already set in capitals: two letters or more, none of them small.</summary>
    private static bool LooksUppercase(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var letters = 0;
        foreach (var character in text)
        {
            if (!char.IsLetter(character)) continue;
            letters++;
            if (char.IsLower(character)) return false;
        }
        return letters >= 2;
    }

    /// <summary>
    /// Whether a string is a figure rather than a word: digits, signs, separators and a unit.
    /// Used only to choose a face, never to change what is written.
    /// </summary>
    private static bool FigureLike(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var figures = 0;
        var letters = 0;
        foreach (var character in text)
        {
            if (char.IsDigit(character)) figures++;
            else if (char.IsLetter(character)) letters++;
        }
        return figures > 0 && letters <= 2;
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
    /// The declared identity as a dossier line: the name, and the exact share of the country
    /// that stands behind it - the number the declaration is judged by.
    /// </summary>
    private string StateIdentityShareLine(Nation nation, bool religion)
    {
        var caption = Simulation.StateIdentity.Caption(_campaign, nation, religion);
        if (caption == Tr("none declared")) return caption;
        var share = religion
            ? Simulation.StateIdentity.ReligionShare(_campaign, nation, nation.DeclaredReligion)
            : Simulation.StateIdentity.CultureShare(_campaign, nation, nation.DeclaredCulture);
        return Simulation.Loc.Format("{0} · {1:0.0%} of the country", caption, share);
    }

    /// <summary>
    /// The picture the game means by a name. The path the interface was written against is
    /// passed as the fallback, so a mod that removes a declaration loses its own override
    /// rather than leaving the player with an empty frame.
    /// </summary>
    private static Texture2D Art(string sprite, string shipped)
    {
        var path = SpriteCatalog.TextureOf(sprite, shipped);
        // A new mod good may not have artwork yet. Keep its name and controls usable
        // instead of emitting loader errors on every refresh of the production line.
        return !string.IsNullOrWhiteSpace(path) && ResourceLoader.Exists(path)
            ? GD.Load<Texture2D>(path) : null;
    }

    private static Color ArtColour(string sprite, string token)
    {
        var target = Ui.Colour(token);
        var reference = Color.FromString(SpriteCatalog.Get(sprite).SourceColour ?? "", Colors.White);
        return new Color(target.R / reference.R, target.G / reference.G, target.B / reference.B, target.A);
    }

    private Button ActionButton(string text, System.Action callback, float width, float height,
        bool primary = false, string iconPath = "")
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            var role = CampaignUiCatalog.ActionIcon(text);
            if (role.Length > 0)
                iconPath = SpriteCatalog.TextureOf("icon_" + role, $"res://game/gfx/interface/icons/icon_{role}.png");
        }
        var button = new Button
        {
            Text = Tr(text),
            CustomMinimumSize = new Vector2(width, height)
        };
        if (!string.IsNullOrWhiteSpace(iconPath) && ResourceLoader.Exists(iconPath))
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
        button.AddThemeFontSizeOverride("font_size", Ui.TypeStep(height >= 42 ? 15 : 13));
        if (width <= 0 && button.Icon is not null && button.Text.Length > 0)
        {
            button.SetMeta("icon_caption_minimum", true);
            ReserveButtonIconSpace(button);
        }
        button.Pressed += () =>
        {
            PlayStrategicCue("ui_select");
            callback();
        };
        return button;
    }

    // ExpandIcon intentionally excludes the image from Godot's minimum size. A natural-
    // width HBox button otherwise fits only its caption and silently shrinks the icon away.
    private static void ReserveButtonIconSpace(Button button)
    {
        var caption = button.GetThemeFont("font").GetStringSize(button.Text,
            fontSize: button.GetThemeFontSize("font_size")).X;
        var padding = button.GetThemeStylebox("normal").GetMinimumSize().X;
        button.CustomMinimumSize = new Vector2(Mathf.Ceil(caption + padding +
            button.GetThemeConstant("icon_max_width") + button.GetThemeConstant("h_separation")),
            button.CustomMinimumSize.Y);
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
        UpdateMapModeButtons(_activeMapMode);
        LayoutCampaignNavigation();
        _strategicHudFingerprint = int.MinValue;
        RefreshSituationDesk();
        RefreshStrategicHud();
        if (_hudLayer is { Visible: true } && _militarySpending is not null && _campaign?.Player is { } player)
            RefreshTopBar(player);
        if (_mediaButton is not null) _mediaButton.AccessibilityName = Tr("Soundtrack player");

        // The names across the map. Built as 3D text once and rebuilt only when a country
        // renames itself - which is what a change of language is, for every country at once.
        _globe?.RefreshCountryNames();

        // Dropdowns. An OptionButton item is a plain string with nothing attached, so each
        // list is re-filled by whoever owns it.
        RefreshBuildingPicker();
        if (_sovereignFinanceLayer is { Visible: true }) RefreshSovereignFinanceWorkspace();
        if (_policyLayer is { Visible: true }) { BuildPolicyTarget(); RebuildPolicyList(); _policyBuiltDay = -1; RefreshPolicyWorkspace(); }
        if (_diplomacyLayer is { Visible: true })
        {
            RebuildDiplomacyList(force: true);
            ForceDiplomacyRefresh();
        }
        if (_supportLayer is { Visible: true }) RefreshSupportScreen();
        if (_stateAffairsLayer is { Visible: true }) RefreshStateAffairs();
        if (_peaceLayer is { Visible: true })
        {
            RecaptionSubjectTerms();
            RefreshPeaceConference();
        }
        if (_settingsLayer is { Visible: true }) ShowSettingsCategory(_settingsCategory);
        if (_saveLoadLayer is { Visible: true }) RefreshSaveSlots();
        if (_armyPanel is { Visible: true }) RefreshArmyPanel();
        if (MapSelectionShowing) ShowMapSelectionContent();

        if (_campaign is not null && _dossierScreen == "Economy") ShowEconomyScreen();
        if (_campaign is not null && _dossierScreen == "Decisions" && _dossierTab == "Decisions")
            RefreshAgendaLanguage();
    }

    private static void RefreshLocalisedText(Node node)
    {
        if (node is Control page && page.HasMeta("loc_tab") && page.GetParent() is TabContainer tabs)
            tabs.SetTabTitle(tabs.GetTabIdxFromControl(page), Tr(page.GetMeta("loc_tab").AsString()));
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
                    if (button.HasMeta("icon_caption_minimum")) ReserveButtonIconSpace(button);
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
                var help = Tr(node.GetMeta(LocTipMeta).AsString());
                control.TooltipText = Ui.Tip(node.HasMeta("loc_tip_heading")
                    ? Tr(node.GetMeta("loc_tip_heading").AsString()) + "\n" + help : help);
                node.SetMeta(LocTipAppliedMeta, control.TooltipText);
            }
        }

        foreach (var child in node.GetChildren()) RefreshLocalisedText(child);
    }

    /// <summary>
    /// Paint one button as ordinary or accented. Split out of button construction because
    /// accent is not a fixed property of a button any more: the speed the campaign is
    /// running at and the screen that is open are both shown by lighting the control that
    /// represents them, which means re-painting a button that already exists.
    /// </summary>
    internal static void StyleButton(Button button, bool primary)
    {
        button.SetMeta("skin_button_primary", primary);
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
        // A rail button carries its picture above its word (`icon_above`).
        var above = button.HasMeta("icon_above") && button.GetMeta("icon_above").AsBool();
        var rounded = button.HasMeta("atlas_round_button") && button.GetMeta("atlas_round_button").AsBool();
        var navTile = button.HasMeta("atlas_nav_tile") && button.GetMeta("atlas_nav_tile").AsBool();
        var tabPill = button.HasMeta("atlas_tab_button") && button.GetMeta("atlas_tab_button").AsBool();
        var outlinerGroup = button.HasMeta("atlas_outliner_group") && button.GetMeta("atlas_outliner_group").AsBool();
        var outlinerRow = button.HasMeta("atlas_outliner_row") ? button.GetMeta("atlas_outliner_row").AsString() : "";
        button.IconAlignment = iconOnly || above ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        button.VerticalIconAlignment = above ? VerticalAlignment.Top : VerticalAlignment.Center;

        if (navTile)
        {
            button.AddThemeStyleboxOverride("normal", AtlasSurfaceSkin.NavTile(primary, 0));
            button.AddThemeStyleboxOverride("hover", AtlasSurfaceSkin.NavTile(primary, 1));
            button.AddThemeStyleboxOverride("pressed", AtlasSurfaceSkin.NavTile(primary, 2));
            button.AddThemeStyleboxOverride("hover_pressed", AtlasSurfaceSkin.NavTile(primary, 2));
            button.AddThemeStyleboxOverride("disabled", AtlasSurfaceSkin.NavTile(primary, 3));
        }
        else if (tabPill)
        {
            button.AddThemeStyleboxOverride("normal", AtlasSurfaceSkin.TabPill(primary, 0));
            button.AddThemeStyleboxOverride("hover", AtlasSurfaceSkin.TabPill(primary, 1));
            button.AddThemeStyleboxOverride("pressed", AtlasSurfaceSkin.TabPill(primary, 2));
            // A pressed toggle under the pointer is drawn in hover_pressed; left to the theme it had
            // other margins, so the active map layer changed width under the mouse and the whole
            // two-column drawer jumped with it.
            button.AddThemeStyleboxOverride("hover_pressed", AtlasSurfaceSkin.TabPill(primary, 2));
            button.AddThemeStyleboxOverride("disabled", AtlasSurfaceSkin.TabPill(primary, 3));
            // A button with a picture of its own (the layer list's flag) keeps it.
            if (button.ToggleMode && (button.Icon is null || button.HasMeta("toggle_lamp")))
            {
                button.SetMeta("toggle_lamp", true);
                // A switch: a pressed toggle hovers in Godot's own hover_pressed style unless told
                // otherwise, and its state wears a lamp beside the word.
                foreach (var (state, index) in new[] { ("normal", 0), ("hover", 1), ("pressed", 2), ("hover_pressed", 1), ("disabled", 0) })
                    button.AddThemeStyleboxOverride(state, AtlasSurfaceSkin.ToggleFace(primary, index));
                button.Icon = AtlasSurfaceSkin.ToggleLamp(primary);
                // Every switch redraws its box when it flips, whoever flipped it, so no screen can
                // forget to (the map bar's check boxes stayed ticked after being turned off).
                if (!button.HasMeta("lamp_wired"))
                {
                    button.SetMeta("lamp_wired", true);
                    button.Toggled += on => { if (IsInstanceValid(button)) StyleButton(button, on); };
                }
                button.AddThemeConstantOverride("icon_max_width", 16);
                button.AddThemeConstantOverride("h_separation", 6);
                // Room for the lamp, once: a caption sized to its width would otherwise be clipped.
                if (!button.HasMeta("lamp_room") && button.CustomMinimumSize.X > 0f)
                {
                    button.SetMeta("lamp_room", true);
                    button.CustomMinimumSize += new Vector2(20f, 0f);
                }
                foreach (var key in new[] { "icon_normal_color", "icon_hover_color", "icon_pressed_color", "icon_hover_pressed_color", "icon_focus_color" })
                    button.AddThemeColorOverride(key, Colors.White);
            }
        }
        else if (outlinerGroup)
        {
            button.AddThemeStyleboxOverride("normal", AtlasSurfaceSkin.OutlinerGroupBar(0));
            button.AddThemeStyleboxOverride("hover", AtlasSurfaceSkin.OutlinerGroupBar(1));
            button.AddThemeStyleboxOverride("pressed", AtlasSurfaceSkin.OutlinerGroupBar(2));
            button.AddThemeStyleboxOverride("disabled", AtlasSurfaceSkin.OutlinerGroupBar(0));
        }
        else if (outlinerRow.Length > 0)
        {
            button.AddThemeStyleboxOverride("normal", AtlasSurfaceSkin.OutlinerCardRow(outlinerRow, 0));
            button.AddThemeStyleboxOverride("hover", AtlasSurfaceSkin.OutlinerCardRow(outlinerRow, 1));
            button.AddThemeStyleboxOverride("pressed", AtlasSurfaceSkin.OutlinerCardRow(outlinerRow, 2));
            button.AddThemeStyleboxOverride("disabled", AtlasSurfaceSkin.OutlinerCardRow(outlinerRow, 0));
        }
        else
        {
            button.AddThemeStyleboxOverride("normal", ButtonStyle(primary, 0, iconOnly, above, rounded));
            button.AddThemeStyleboxOverride("hover", ButtonStyle(primary, 1, iconOnly, above, rounded));
            button.AddThemeStyleboxOverride("pressed", ButtonStyle(primary, 2, iconOnly, above, rounded));
            button.AddThemeStyleboxOverride("hover_pressed", ButtonStyle(primary, 2, iconOnly, above, rounded));
            // `primary` survives into the disabled look, and that is the whole of telling
            // "this is what we are" apart from "you may not have this". The politics screen
            // disables the form of government and the ideology a country already has - you
            // cannot adopt what you already are - and with a flat grey disabled style that made
            // the country's own ideology look exactly like one the game was refusing.
            button.AddThemeStyleboxOverride("disabled", ButtonStyle(primary, 3, iconOnly, above, rounded));
        }
        ButtonFocusPresentation.Style(button, rounded);
        var normalTextColour = outlinerGroup
            ? Ui.Colour("text.accent_dim")
            : navTile
                ? (primary ? Ui.Colour("text.accent") : Ui.Colour("text.body_soft"))
                : tabPill
                    ? (primary ? Ui.Colour("text.accent") : Ui.Colour("text.muted"))
                    : (primary ? Ui.Colour("button.text_hover") : Ui.Colour("button.text"));
        button.AddThemeColorOverride("font_color", normalTextColour);
        button.AddThemeColorOverride("font_hover_color", Ui.Colour("button.text_hover"));
        button.AddThemeColorOverride("font_pressed_color", Ui.Colour("button.text_pressed"));
        button.AddThemeColorOverride("font_disabled_color",
            primary ? Ui.Colour("text.accent_dim", 0.55f) : Ui.Colour("button.text_disabled"));
        if (navTile)
        {
            button.AddThemeColorOverride("icon_normal_color", primary ? Ui.Colour("text.accent") : Ui.Colour("text.body_soft"));
            button.AddThemeColorOverride("icon_hover_color", Ui.Colour("button.text_hover"));
            button.AddThemeColorOverride("icon_pressed_color", Ui.Colour("text.accent"));
        }
        // Compensate the authored palette so gold PNGs do not receive a second gold tint.
        // Reapply on selection and presentation reload as well as initial construction.
        if (button.HasMeta("army_monochrome_icon") && button.GetMeta("army_monochrome_icon").AsBool())
        {
            var sprite = button.GetMeta("army_icon_sprite").AsString();
            button.AddThemeColorOverride("icon_normal_color", ArtColour(sprite, "text.accent_dim"));
            button.AddThemeColorOverride("icon_hover_color", ArtColour(sprite, "text.accent"));
            button.AddThemeColorOverride("icon_pressed_color", ArtColour(sprite, "button.text_pressed"));
            button.AddThemeColorOverride("icon_disabled_color", ArtColour(sprite, "button.text_disabled"));
        }
        if (button.HasMeta("atlas_music_button"))
        {
            button.AddThemeColorOverride("icon_normal_color", Ui.Colour("text.accent_dim"));
            button.AddThemeColorOverride("icon_hover_color", Ui.Colour("text.accent"));
            button.AddThemeColorOverride("icon_pressed_color", Ui.Colour("button.text_pressed"));
        }
        // A switch's lettering last, over the general colours above: dark on a lit key, muted on an unlit one.
        if (tabPill && button.ToggleMode && button.HasMeta("toggle_lamp"))
        {
            var lettering = primary ? Ui.Colour("button.text_hover") : Ui.Colour("text.muted");
            foreach (var key in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
                button.AddThemeColorOverride(key, key == "font_hover_color" && !primary ? Ui.Colour("button.text_hover") : lettering);
        }
        button.SetMeta("skin_button_applied", button.GetThemeColor("font_color"));
    }

    private static StyleBox ButtonStyle(bool primary, int state, bool iconOnly = false, bool narrow = false, bool rounded = false)
    {
        var fill = Ui.Colour(state switch
        {
            1 => primary ? "button.primary_fill_hover" : "button.fill_hover",
            2 => primary ? "button.primary_fill_pressed" : "button.fill_pressed",
            3 => "button.fill_disabled",
            _ => primary ? "button.primary_fill" : "button.fill"
        });
        var edge = primary && state == 3
            ? Ui.Colour("button.primary_border", 0.38f)
            : Ui.Colour(state switch
            {
                1 => "button.border_hover",
                2 => "button.border_pressed",
                3 => "button.border_disabled",
                _ => primary ? "button.primary_border" : "button.border"
            });
        var style = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = edge,
            ContentMarginLeft = narrow ? 4 : 12,
            ContentMarginRight = narrow ? 4 : 12,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(rounded ? 8 : 3);
        return InstrumentMaterials.Face(style, state == 2, state == 3);
    }

    private StyleBox PanelStyle(PanelSurface surface, bool inCampaign = false)
    {
        var command = surface is PanelSurface.Command or PanelSurface.CommandStrip;
        var strip = surface == PanelSurface.CommandStrip;
        var sprite = strip ? "panel_command_strip" : command ? "panel_command_frame" : "panel_dossier";
        var shipped = $"res://game/gfx/interface/panels/{sprite}.png";
        var path = SpriteCatalog.TextureOf(sprite, shipped);
        if (path.Equals(shipped, StringComparison.OrdinalIgnoreCase))
        {
            var style = CampaignPanelStyle(surface);
            if (!inCampaign)
                style.BgColor = Ui.Colour(surface == PanelSurface.Modal ? "surface.raised" : "surface.overlay");
            return style;
        }
        var textureMargin = strip ? 20f : command ? 48f : 28f;
        var contentHorizontal = strip ? 6f : command ? 28f : surface == PanelSurface.Modal ? 14f : 6f;
        var contentVertical = strip ? 4f : command ? 22f : surface == PanelSurface.Modal ? 12f : 4f;
        return new StyleBoxTexture
        {
            Texture = Art(sprite, shipped),
            TextureMarginLeft = textureMargin,
            TextureMarginTop = textureMargin,
            TextureMarginRight = textureMargin,
            TextureMarginBottom = textureMargin,
            ContentMarginLeft = contentHorizontal,
            ContentMarginTop = contentVertical,
            ContentMarginRight = contentHorizontal,
            ContentMarginBottom = contentVertical
        };
    }
}
