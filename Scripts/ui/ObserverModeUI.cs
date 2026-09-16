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

    private void ToggleObserverMode()
    {
        if (_campaign is null || !_hudLayer.Visible) return;
        SetObserverMode(!_campaign.ObserverMode);
    }

    private void SetObserverMode(bool watching)
    {
        if (_campaign is null) return;
        _campaign.ObserverMode = watching;

        // Everything whose only purpose is to be operated. The top bar stays, because a
        // recording with no date on it is a recording of nothing, and so does the clock -
        // which is also how somebody gets back out of here.
        HideForObserver("CampaignNavigation", watching);
        HideForObserver("MapControls", watching);
        if (_sidePanel is not null && watching) _sidePanel.Visible = false;
        if (_outliner is not null) _outliner.Visible = !watching;
        if (_armyPanel is not null && watching) _armyPanel.Visible = false;
        if (_fleetPanel is not null && watching) _fleetPanel.Visible = false;
        if (_ledgerLayer is not null && watching) _ledgerLayer.Visible = false;
        if (_stateAffairsLayer is not null && watching) _stateAffairsLayer.Visible = false;
        if (_eventLayer is not null && watching) _eventLayer.Visible = false;
        _observerChromeHidden = watching;

        if (_observerHint is not null)
        {
            _observerHint.Visible = watching;
            _observerHint.Text = Tr("Observing · F7 returns the controls · nothing will stop the clock");
        }

        // A world nobody is steering should be moving. Turning the mode on while paused is
        // almost always somebody about to record something.
        if (watching && _campaign.IsPaused) TogglePause();
        Refresh();
    }

    private void HideForObserver(string nodeName, bool watching)
    {
        if (_hudLayer is null) return;
        if (_hudLayer.FindChild(nodeName, true, false) is Control panel) panel.Visible = !watching;
    }
}
