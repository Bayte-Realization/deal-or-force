using Godot;

namespace DealOrForce;

/// <summary>
/// Watching the world instead of running it.
///
/// The most interesting thing this game can put in front of somebody is fifty years of the
/// planet redrawing itself at speed five, and the one thing that made that impossible to
/// record was the game doing what a strategy game is supposed to do: stopping to ask
/// questions. An event arrives, time halts, a modal window waits, and the recording is over.
///
/// So there is a seat that is never asked. The simulation half of it is one flag on the
/// campaign - no events are raised for an observer and anything already pending is decided
/// - and this is the other half: the panels that exist to be operated get out of the way,
/// and what is left is the map, the date and the speed.
///
/// Deliberately not a different game mode. It is a toggle on a campaign already running, so
/// a player can watch their own world for a decade and then take the wheel back, and the
/// flag rides along in the save so a recording can be resumed.
/// </summary>
public partial class DealOrForceGame
{
    private bool _observerChromeHidden;
    private Label _observerHint;

    private void BuildObserverHint()
    {
        _observerHint = Title("", 13, Ui.Colour("text.hint"));
        _observerHint.Visible = false;
        _observerHint.HorizontalAlignment = HorizontalAlignment.Center;
        _observerHint.MouseFilter = Control.MouseFilterEnum.Ignore;
        _observerHint.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _observerHint.AnchorLeft = 0f;
        _observerHint.AnchorRight = 1f;
        _observerHint.OffsetLeft = 0;
        _observerHint.OffsetRight = 0;
        _observerHint.OffsetTop = -38;
        _observerHint.OffsetBottom = -14;
        _hudLayer.AddChild(_observerHint);
    }

    /// <summary>The country an observer is watching, or null for the whole world.</summary>
    private Simulation.Nation _observerWatched;
    private int _observerReturnNationId = -1;
    private string _observerTransientNote = "";
    private bool _interfaceHidden;
    private bool _simulating;
    private System.Func<Simulation.ICampaignCommand, bool> _observerGate;

    /// <summary>Nobody is playing and no country is singled out: the bar and the report are the world's.</summary>
    private bool WatchingWorld => _observerMode && (_observerWatched is null || _campaign?.NationById(_observerWatched.Id) is null);

    private void ToggleObserverMode()
    {
        if (_campaign is null || !_hudLayer.Visible) return;
        var currentlyObserving = _observerMode || _campaign.ObserverMode;
        if (currentlyObserving)
        {
            // Pure observer campaign (no human seat): F7 takes control of the country being watched.
            if (_campaign.HumanSeats.Count == 0)
            {
                if (_observerWatched is null)
                {
                    ShowObserverHint(Tr("Click a country to watch it, then F7 takes control of it."));
                    if (IsNewShellActive) RefreshNewShell(force: true);
                    return;
                }
                var nation = _observerWatched;
                _observerWatched = null;
                _observerReturnNationId = -1;
                _campaign.ConfigureSinglePlayer(nation.Id);
                SetObserverMode(false);
                return;
            }

            // Player entered observer mode from an active campaign: restore their own seat immediately.
            var seatIndex = System.Math.Clamp(_campaign.ActiveSeat, 0, _campaign.HumanSeats.Count - 1);
            var returnId = _observerReturnNationId >= 0 && _campaign.NationById(_observerReturnNationId) is not null
                ? _observerReturnNationId
                : _campaign.HumanSeats[seatIndex];
            _observerReturnNationId = -1;
            _observerWatched = null;
            _campaign.ViewAs(returnId);
            SetObserverMode(false);
            return;
        }

        _observerReturnNationId = _campaign.HumanSeats.Count > 0 ? (_campaign.Player?.Id ?? _campaign.HumanSeats[0]) : -1;
        _observerWatched = _campaign.Player;
        SetObserverMode(true);
    }

    /// <summary>
    /// An observer clicked a country: the flag, the figures, the alerts and every screen are now
    /// that country's immediately (even while paused). It is still run by the computer; orders are refused.
    /// </summary>
    private void WatchCountry(Simulation.Nation nation)
    {
        if ((!_observerMode && _campaign is not { ObserverMode: true }) || _campaign is null || nation is null) return;
        _observerMode = true;
        if (ReferenceEquals(nation, _observerWatched) && ReferenceEquals(_campaign.Player, nation))
        {
            if (IsNewShellActive) RefreshNewShell(force: true);
            return;
        }
        _observerWatched = nation;
        _campaign.ViewAs(nation.Id);
        RestoreCountryTopBar();
        ResetTrends();
        _strategicHudFingerprint = int.MinValue;
        ShowObserverHint("");
        Refresh();
        if (IsNewShellActive) RefreshNewShell(force: true);
    }

    /// <summary>Back to the faceless view: the world's own figures and news (F4).</summary>
    private void WatchWorld()
    {
        if (!_observerMode || _observerWatched is null) return;
        _observerWatched = null;
        _strategicHudFingerprint = int.MinValue;
        ShowObserverHint("");
        Refresh();
        if (IsNewShellActive) RefreshNewShell(force: true);
    }

    /// <summary>
    /// Ctrl+F9, in any mode: the whole interface steps off the screen and the map is all there is.
    /// Moved, not hidden: everything that asks "is the campaign on screen" still gets yes, the
    /// keys keep working, and nothing invisible is left to catch a click.
    /// </summary>
    private void ToggleInterfaceHidden()
    {
        if (_campaign is null || _hudLayer is null || !_hudLayer.Visible) return;
        _interfaceHidden = !_interfaceHidden;
        _hudLayer.Position = _interfaceHidden ? new Vector2(0, -100000) : Vector2.Zero;
        if (_globe is not null) _globe.InterfaceHidden = _interfaceHidden;
        if (!_interfaceHidden) { LayoutCampaignShell(); Refresh(); }
    }

    private void ShowObserverHint(string once)
    {
        _observerTransientNote = once ?? "";
        if (_observerHint is null) return;
        if (IsNewShellActive)
        {
            _observerHint.Visible = false;
            return;
        }
        _observerHint.Visible = _observerMode || _campaign is { ObserverMode: true };
        if (!_observerHint.Visible) return;
        // Between the rail on the left and the map's switches on the right, never under either.
        _observerHint.OffsetLeft = _hudLayer.FindChild("CampaignNavigation", true, false) is Control rail && rail.Visible
            ? rail.GetRect().End.X + 8f : 0f;
        _observerHint.OffsetRight = _hudLayer.FindChild("MapControls", true, false) is Control switches && switches.Visible
            ? -(GetViewportRect().Size.X - switches.GetRect().Position.X) - 8f : 0f;
        _observerHint.ClipText = true;
        _observerHint.Text = once.Length > 0 ? once
            : _campaign.HumanSeats.Count > 0 ? Tr("Observing · F7 returns the controls · Ctrl+F9 hides the interface")
            : _observerWatched is null ? Tr("Observing the world · click a country to watch it · Ctrl+F9 hides the interface")
            : Simulation.Loc.Format("Watching {0} · F4 back to the world · F7 takes control · Ctrl+F9 hides the interface", _observerWatched.DisplayName);
    }

    /// <summary>
    /// While observing, orders from the interface are not carried out: the country on screen
    /// is read-only. Orders the simulation gives itself during a step pass.
    /// </summary>
    private void SyncObserverCommandGate()
    {
        if (_campaign is null) return;
        // A step an order has to finish first (FinishStep) is still the simulation speaking.
        _observerGate ??= _ => !_simulating && _campaign is { MidStep: false };
        var queue = _campaign.Commands;
        var locked = (_observerMode || _campaign.ObserverMode) && !_networkConnected;
        if (locked) { if (queue.SubmitInterceptor is null) queue.SubmitInterceptor = _observerGate; }
        else if (queue.SubmitInterceptor == _observerGate) queue.SubmitInterceptor = null;
    }

    private void SetObserverMode(bool watching)
    {
        if (_campaign is null) return;
        _observerMode = watching;
        _campaign.ObserverMode = watching;

        // The interface stays (owner, 16 November): an observer reads the same screens a player
        // does. Clearing the screen for a recording is its own key now (Ctrl+F9), in any mode.
        // Only what waits for an answer is put away, because nobody is there to answer.
        if (_eventLayer is not null && watching) _eventLayer.Visible = false;
        _observerChromeHidden = false;
        if (!watching)
        {
            _observerWatched = null;
            _observerReturnNationId = -1;
            RestoreCountryTopBar();
        }
        // The report on the right is the world's or a country's: draw it again for whichever it is now.
        _strategicHudFingerprint = int.MinValue;
        // The clock is left as it is: starting it is the observer's own decision.
        SyncObserverCommandGate();
        ShowObserverHint("");
        Refresh();
        if (IsNewShellActive) RefreshNewShell(force: true);
    }
}
