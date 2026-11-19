using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The half of the quality ladder that lives in the game rather than in the map.
///
/// It reads the adapter once, picks a starting tier, and then watches the frame the game is
/// actually getting. A machine that cannot hold thirty frames a second on the authored picture
/// loses pixels first, then city names and weather, then the smoothed raster - in that order,
/// because that is the order in which the losses are least visible. It never writes the
/// player's saved preferences: the tier is a fact about this machine today, not a setting.
/// </summary>
public partial class DealOrForceGame
{
    private static readonly bool _isMobileRuntime = OS.HasFeature("mobile") || OS.GetName() is "Android" or "iOS";
    private FrameRateGovernor _qualityGovernor;

    /// <summary>Read the ladder and the graphics card, and put the result into effect.</summary>
    private void RebuildQualityProfile()
    {
        if (_globe is null) return;
        var chosen = SettingsCatalog.Chosen("graphics_quality");
        var automatic = string.IsNullOrWhiteSpace(chosen) || chosen.Equals("auto", StringComparison.OrdinalIgnoreCase);
        var adapter = RenderingServer.GetVideoAdapterName();
        var method = RenderingServer.GetCurrentRenderingMethod();
        var profile = QualityProfiles.For(chosen, adapter, method);
        if (automatic && _isMobileRuntime && string.Equals(profile.Id, "high", StringComparison.OrdinalIgnoreCase))
        {
            var balanced = QualityProfiles.For("balanced", adapter, method);
            profile = string.Equals(balanced.Id, "high", StringComparison.OrdinalIgnoreCase)
                ? (QualityProfiles.Next(profile) ?? profile)
                : balanced;
        }
        if (_qualityGovernor is null) _qualityGovernor = new FrameRateGovernor(profile, automatic);
        else _qualityGovernor.Reset(profile);
        _globe.ApplyQuality(profile);
        DiagnosticLogs.Write("performance",
            $"QUALITY tier={profile.Id} auto={automatic} mobile={_isMobileRuntime} render_scale={profile.RenderScale} " +
            $"adapter=\"{adapter}\" method={method}");
    }

    /// <summary>One frame's worth of evidence for the governor. Cheap enough to call every frame.</summary>
    private void MaintainQuality(double delta)
    {
        if (_qualityGovernor is null || _globe is null) return;
        // A map still being built is loading, not play: the warm-up starts when it is up.
        if (!_globe.MapReady) { _qualityGovernor.Reset(_qualityGovernor.Profile); return; }
        var next = _qualityGovernor.Sample(delta * 1000.0, _globe.MeasuredGpuMs);
        if (next is null) return;
        _globe.ApplyQuality(next);
        DiagnosticLogs.Write("performance",
            $"QUALITY_STEP tier={next.Id} render_scale={next.RenderScale} shadows={next.SunShadows} " +
            $"city_labels={next.CityLabels} trade={next.TradeTraffic} weather={next.Weather} max_fps={next.MaxFps}");
    }
}
