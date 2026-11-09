using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// A paused game behind other windows does not need sixty frames a second of the map shader.
public partial class DealOrForceGame
{
    private int _pacedFps = -1;

    /// <summary>
    /// Every frame: the player's frame limit, except while the game is paused and the window is
    /// not in front (or is minimised) - then "background_fps". A running game is never slowed,
    /// because the simulation's share of each frame is what moves the clock at the top speed.
    /// A strategy game is left paused in the background for hours; drawing the globe at full rate
    /// all that time was the graphics card running hot for a picture nobody was looking at.
    /// </summary>
    private void PaceBackgroundFrames()
    {
        var limit = Math.Max(0, (int)MathF.Round(SettingsCatalog.Number("max_fps")));
        var background = Math.Max(0, (int)MathF.Round(SettingsCatalog.Number("background_fps")));
        var behind = !DisplayServer.WindowIsFocused() || DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized;
        var paused = _campaign is null || _campaign.IsPaused;
        var wanted = behind && paused && background > 0 ? (limit > 0 ? Math.Min(limit, background) : background) : limit;
        if (wanted == _pacedFps) return;
        _pacedFps = wanted;
        Engine.MaxFps = wanted;
    }
}
