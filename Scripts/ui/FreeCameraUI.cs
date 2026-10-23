using System;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
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
}
