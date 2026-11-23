using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// A paused game behind other windows does not need sixty frames a second of the map shader.
public partial class DealOrForceGame
{
    /// <summary>True whenever the window is unfocused/minimised or intentionally background-throttled.</summary>
    private bool _isBackgroundFramePaced;
    private ulong _fpsSettingsCheckedAtMsec;
    private int _cachedMaxFps = -1;
    private int _cachedBackgroundFps = -1;

    /// <summary>
    /// Every frame: the player's frame limit, except while the game is paused and the window is
    /// not in front (or is minimised) - then "background_fps". A running game is never slowed,
    /// because the simulation's share of each frame is what moves the clock at the top speed.
    /// A strategy game is left paused in the background for hours; drawing the globe at full rate
    /// all that time was the graphics card running hot for a picture nobody was looking at.
    /// </summary>
    private void PaceBackgroundFrames()
    {
        var now = Time.GetTicksMsec();
        if (_cachedMaxFps < 0 || now - _fpsSettingsCheckedAtMsec >= 500)
        {
            _fpsSettingsCheckedAtMsec = now;
            // These are string-valued choices, not sliders with a numeric 0..1 range.
            var maxFps = Math.Max(0, Whole("max_fps"));
            _cachedMaxFps = _isMobileRuntime && maxFps == 0 ? 60 : maxFps;
            _cachedBackgroundFps = Math.Max(0, Whole("background_fps"));
        }
        var limit = _cachedMaxFps;
        var background = _cachedBackgroundFps;
        var behind = !DisplayServer.WindowIsFocused() || DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized;
        var paused = _campaign is null || _campaign.IsPaused;
        _isBackgroundFramePaced = behind;
        var wanted = behind && paused && background > 0 ? (limit > 0 ? Math.Min(limit, background) : background) : limit;
        // Focus notifications and settings can also write the engine's cap. Comparing
        // only our previous intent would miss such writes and leave a running clock slow.
        if (Engine.MaxFps != wanted) Engine.MaxFps = wanted;
    }
}
