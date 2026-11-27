using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Synchronous Frame-0 campaign-entry coordination between <see cref="DealOrForceGame"/>,
/// <see cref="BackgroundSimRunner"/>, and <see cref="GlobePresentation"/>.
/// Guarantees fresh <see cref="ProvinceVision"/>, <see cref="CampaignRenderSnapshot"/>,
/// <see cref="CampaignHudSnapshot"/>, <see cref="WorldVisualState"/>, visibility flags, and
/// complete globe visual sync BEFORE the very first campaign frame renders.
/// </summary>
public partial class DealOrForceGame
{
    private bool _wasCampaignVisualsActive;
    private Campaign _lastCampaignEntryCampaign;
    private int _lastCampaignEntryPlayerId = int.MinValue;
    private bool _lastCampaignEntryObserver;
    private bool _lastCampaignEntryWatchingWorld;

    private bool IsCampaignViewportActive() =>
        _campaign is not null &&
        _menuLayer is { Visible: false } &&
        (_pickerLayer is null || !_pickerLayer.Visible);

    private void SyncGlobeVisibilityFlags(bool allowAutoFlush = true)
    {
        if (_globe is null) return;
        var inCampaign = IsCampaignViewportActive();
        _globe.FogOfWarActive = inCampaign && !WatchingWorld;
        _globe.MilitaryMarkersEnabled = inCampaign;
        _globe.RegionIconsSuppressed = _pickerLayer is { Visible: true } || IsNewShellSuppressingRegionIcons();

        if (!inCampaign)
        {
            _wasCampaignVisualsActive = false;
            return;
        }
        if (!allowAutoFlush || !_startupComplete || _isLoading || _campaign is null || !_globe.IsMapPresentationReady)
            return;

        var playerId = _campaign.Player?.Id ?? -1;
        var needsFlush = !_wasCampaignVisualsActive ||
                         !ReferenceEquals(_lastCampaignEntryCampaign, _campaign) ||
                         _lastCampaignEntryPlayerId != playerId ||
                         _lastCampaignEntryObserver != _observerMode ||
                         _lastCampaignEntryWatchingWorld != WatchingWorld;
        if (needsFlush)
            PrepareCampaignEntryVisuals(focusPlayerCapital: false, snapCamera: false);
    }

    private void PrepareCampaignEntryVisuals(bool focusPlayerCapital = false, bool snapCamera = true)
    {
        if (_campaign is null) return;
        _lastFastHudRefreshMsec = 0;
        _observerMode = _campaign.ObserverMode || (_campaign.HumanSeats.Count == 0 && !_campaign.Nations.Any(n => n.IsHuman));

        if (WantsBackgroundSimulation)
        {
            var alreadyAttached = _simRunner.IsActive && ReferenceEquals(_simRunner.Campaign, _campaign);
            EnsureBackgroundSimSynced();
            if (alreadyAttached)
                _simRunner.RefreshSnapshotNow();
        }
        else
        {
            if (_simRunner.Campaign is not null || _simRunner.IsActive)
                _simRunner.Stop();
            RefreshCampaignSnapshotSynchronously();
        }

        if (_globe is null) return;
        var inCampaign = IsCampaignViewportActive();
        _globe.FogOfWarActive = inCampaign && !WatchingWorld;
        _globe.MilitaryMarkersEnabled = inCampaign;
        _globe.RegionIconsSuppressed = _pickerLayer is { Visible: true } || IsNewShellSuppressingRegionIcons();

        if (focusPlayerCapital && !_observerMode && _campaign.Player is { } player)
            FocusNationCapital(player);

        if (_globe.IsMapPresentationReady)
            _globe.FlushCampaignEntryVisuals(_simRunner, snapCamera: snapCamera);

        _wasCampaignVisualsActive = inCampaign;
        _lastCampaignEntryCampaign = _campaign;
        _lastCampaignEntryPlayerId = _campaign.Player?.Id ?? -1;
        _lastCampaignEntryObserver = _observerMode;
        _lastCampaignEntryWatchingWorld = WatchingWorld;
    }

    private void RefreshCampaignSnapshotSynchronously()
    {
        var campaign = _campaign;
        if (campaign is null) return;
        if (campaign.ProvinceMap is not null)
        {
            CampaignIntel.InvalidateActors(campaign);
            if (campaign.Player is { IsHuman: true } p)
                CampaignIntel.Vision(campaign, p.Id);
        }
        var nextVersion = Math.Max(1L, campaign.RenderSnapshot.Version + 1L);
        var snap = CampaignRenderSnapshot.Capture(campaign, campaign.RenderSnapshot, CountryRankings.Weights.Default, nextVersion);
        campaign.PublishRenderSnapshot(snap);
    }
}
