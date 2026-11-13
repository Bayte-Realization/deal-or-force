using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

// A paused game behind other windows does not need sixty frames a second of the map shader.
public partial class DealOrForceGame
{
    /// <summary>
    /// Every frame: the player's frame limit, except while the game is paused and the window is
    /// not in front (or is minimised) - then "background_fps". A running game is never slowed,
    /// because the simulation's share of each frame is what moves the clock at the top speed.
    /// A strategy game is left paused in the background for hours; drawing the globe at full rate
    /// all that time was the graphics card running hot for a picture nobody was looking at.
    /// </summary>
    private void PaceBackgroundFrames()
    {
        // These are string-valued choices, not sliders with a numeric 0..1 range.
        var limit = Math.Max(0, Whole("max_fps"));
        var background = Math.Max(0, Whole("background_fps"));
        var behind = !DisplayServer.WindowIsFocused() || DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized;
        var paused = _campaign is null || _campaign.IsPaused;
        var wanted = behind && paused && background > 0 ? (limit > 0 ? Math.Min(limit, background) : background) : limit;
        // Focus notifications and settings can also write the engine's cap. Comparing
        // only our previous intent would miss such writes and leave a running clock slow.
        if (Engine.MaxFps != wanted) Engine.MaxFps = wanted;
    }
}
