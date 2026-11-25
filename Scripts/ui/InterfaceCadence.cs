using System.Linq;

namespace DealOrForce;

/// <summary>
/// How often the interface catches up with the world.
///
/// While the clock is stopped, every frame: a click is answered on the next one. While it
/// runs, ten times a second. At the top speed a frame runs several simulation steps, and
/// redrawing the top bar, the side panel and every open screen for each of them - which is
/// what a redraw per step meant - spent most of a frame's budget on pictures of states the
/// player saw for no frame at all. A tenth of a second is below what a moving number needs to
/// read as moving, and what stops the clock (an event, an offer) is still answered the same
/// step: the campaign raises that at once rather than waiting to be asked (Campaign.Tick).
/// </summary>
public partial class DealOrForceGame
{
    private const double RunningInterfaceInterval = 0.1;
    private double _interfaceSinceRefresh;
    private int _cadenceFeatureDay = int.MinValue;
    private int _cadenceFeatureMapRev = int.MinValue;
    private bool _cadenceFeatureIsNewShell;
    private bool _cachedSupportEnabled;

    private void FlushInterfaceIfDue(double delta)
    {
        KeepMapSelection(delta);
        KeepFleetPanel(delta);
        KeepHeadlinePopups(delta);
        KeepResourceAtlas(delta);
        _interfaceSinceRefresh += delta;
        if (_interfaceSinceRefresh < RunningInterfaceInterval)
        {
            if (_campaign.IsPaused) _campaign.FlushInterfaceChanges();
            return;
        }
        _interfaceSinceRefresh = 0d;
        var isNewShell = IsNewShellActive;
        var featuresDirty = !isNewShell ||
            _cadenceFeatureDay != _campaign.ElapsedDays ||
            _cadenceFeatureMapRev != _campaign.MapRevision ||
            _cadenceFeatureIsNewShell != isNewShell;

        if (!isNewShell)
        {
            foreach (var (screen, button) in _screenButtons)
            {
                var enabled = NavigationDestinationEnabled(screen);
                var wantVis = enabled && NavigationDestinationVisible(screen);
                if (button.Visible != wantVis) button.Visible = wantVis;
                if (!enabled && _openScreen == screen) ToggleCampaignScreen(screen);
            }
        }
        if (featuresDirty)
        {
            _cadenceFeatureDay = _campaign.ElapsedDays;
            _cadenceFeatureMapRev = _campaign.MapRevision;
            _cadenceFeatureIsNewShell = isNewShell;
            if (!_campaign.FeatureEnabled("research") && _researchLayer is { Visible: true }) CloseResearchScreen();
            if (!_campaign.FeatureEnabled("politics") && _stateAffairsLayer is { Visible: true }) CloseStateAffairs();
            if (!_campaign.FeatureEnabled("navy") && _fleetPanel is { Visible: true }) SetFleetPanelVisible(false);
            var supportEnabled = false;
            foreach (var t in Simulation.SupportTypeCatalog.All)
            {
                if (Simulation.SupportCore.Enabled(_campaign, t)) { supportEnabled = true; break; }
            }
            _cachedSupportEnabled = supportEnabled;
            if (!isNewShell && _screenButtons.TryGetValue("Support", out var supportButton))
            {
                var wantSupport = supportEnabled &&
                    Simulation.MechanicFeatures.ScreenEnabled(_campaign, "Support") && NavigationDestinationVisible("Support");
                if (supportButton.Visible != wantSupport) supportButton.Visible = wantSupport;
            }
            if (!supportEnabled && _supportLayer is { Visible: true }) CloseSupportScreen();
        }
        else if (!isNewShell && !_cachedSupportEnabled && _supportLayer is { Visible: true })
        {
            CloseSupportScreen();
        }
        _campaign.FlushInterfaceChanges();
    }
}
