using static DealOrForce.TerritoryLabelLayout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using DealOrForce.Simulation;
using SourceEarthData = DealOrForce.Simulation.WorldMapSource;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private bool _dragging;
    private bool _boxSelecting;
    /// <summary>The box was begun with Ctrl held: it picks regions, not forces (ProvincesBoxSelected).</summary>
    private bool _boxRegions;
    private bool _paintBoxing;
    private bool _paintBoxClaims;
    private Vector2 _boxStart;
    private Vector2 _boxCurrent;
    private float _dragDistance;
    private Vector2 _previousPointer;
    private readonly Simulation.Controls.MapGestureSession _gesture = new();
    /// <summary>
    /// The brush. A press starts a stroke, motion continues it, and a release ends it;
    /// each province is reported once per stroke so dragging slowly does not send the same
    /// province forty times. Returns true when the event was a stroke, so ordinary picking
    /// and camera dragging stay out of the way while a peace is being drawn.
    /// </summary>
    private bool HandlePaintInput(InputEvent inputEvent)
    {
        // Both mouse buttons are the brush here, which leaves nothing to move the map with -
        // and the ground being argued over can be on the other side of the planet from
        // whatever the camera happens to be pointed at. The middle button drags instead.
        // WASD keeps working throughout, because it is polled rather than routed, so there
        // is never a settlement the player can price but not look at.
        if (inputEvent is InputEventMouseButton pan && pan.ButtonIndex == MouseButton.Middle)
        {
            if (pan.Pressed) BeginCameraDrag(pan.Position, pan.ButtonIndex);
            else _dragging = false;
            AcceptEvent();
            return true;
        }
        if (inputEvent is InputEventMouseMotion panning && _dragging && !_painting)
        {
            DragCamera(panning.Position - _previousPointer);
            _previousPointer = panning.Position;
            AcceptEvent();
            return true;
        }

        // Shift, or the frame button, turns the brush into a rectangle. Left takes what is
        // inside it, right gives it back - the same two hands as the brush, so nothing new has
        // to be learned.
        var framing = PaintBoxMode;
        if (inputEvent is InputEventMouseButton boxButton &&
            boxButton.ButtonIndex is MouseButton.Left or MouseButton.Right &&
            (boxButton.ShiftPressed || framing || _paintBoxing))
        {
            if (boxButton.Pressed && (boxButton.ShiftPressed || framing))
            {
                _paintBoxing = true;
                _paintBoxClaims = boxButton.ButtonIndex == MouseButton.Left;
                _boxSelecting = true;
                _boxStart = boxButton.Position;
                _boxCurrent = boxButton.Position;
                QueueRedraw();
            }
            else if (_paintBoxing && !boxButton.Pressed)
            {
                _boxCurrent = boxButton.Position;
                _paintBoxing = false;
                _boxSelecting = false;
                List<int> claimed = null;
                if (TryBeginActionRead(out var campaign, 150))
                {
                    try { claimed = ProvincesInScreenRect(RectFromPoints(_boxStart, _boxCurrent)); }
                    finally { campaign.ExitRead(); }
                }
                if (claimed is { Count: > 0 }) ProvincesBoxPainted?.Invoke(claimed, _paintBoxClaims);
                QueueRedraw();
            }
            AcceptEvent();
            return true;
        }
        if (inputEvent is InputEventMouseMotion boxDrag && _paintBoxing)
        {
            _boxCurrent = boxDrag.Position;
            QueueRedraw();
            AcceptEvent();
            return true;
        }

        if (inputEvent is InputEventMouseButton button &&
            button.ButtonIndex is MouseButton.Left or MouseButton.Right)
        {
            if (button.Pressed)
            {
                _painting = true;
                _paintingClaims = button.ButtonIndex == MouseButton.Left;
                _lastPaintedProvince = -1;
                Paint(button.Position);
            }
            else
            {
                _painting = false;
                _lastPaintedProvince = -1;
            }
            AcceptEvent();
            return true;
        }

        if (inputEvent is InputEventMouseMotion motion && _painting)
        {
            Paint(motion.Position);
            AcceptEvent();
            return true;
        }

        return false;
    }

    private void Paint(Vector2 pixel)
    {
        if (!TryBeginActionRead(out var campaign, 150)) return;
        GlobePick pick = default;
        var picked = false;
        try { picked = TryPick(pixel, out pick, updateSelection: false); }
        finally { campaign.ExitRead(); }
        if (!picked) return;
        if (pick.ProvinceId <= 0 || pick.ProvinceId == _lastPaintedProvince) return;
        _lastPaintedProvince = pick.ProvinceId;
        ProvincePainted?.Invoke(pick, _paintingClaims);
    }

    private bool TryPick(Vector2 pixel, out GlobePick pick, bool updateSelection = true)
    {
        pick = default;
        if (!TryLocate(pixel, out var latitude, out var longitude, out var cell, out var provinceId)) return false;
        // A country owning a province and an army occupying it are not the same thing.
        // Picking the controller here also suppressed the tooltip's occupation line, since
        // it then compared that controller to itself and called the occupier the owner.
        var nationMapIndex = _campaign?.ProvinceMap is { } map
            ? (provinceId > 0 && provinceId < map.ProvinceToNation.Length ? map.ProvinceToNation[provinceId] : 0)
            : (provinceId > 0 ? 0 : (SourceEarthData.PeekControlIfLoaded() is { } ctrl ? ctrl[cell] : SourceEarthData.Control[cell]));
        if (nationMapIndex == 0 || !_nationByMapIndex.TryGetValue(nationMapIndex, out var nation))
            return false;
        return FinishPick(nation, latitude, longitude, cell, provinceId, updateSelection, out pick);
    }

    internal bool VerificationPickAt(Vector2 pixel, out GlobePick pick, bool updateSelection = false)
    {
        if (_campaign is { } campaign)
        {
            if (!campaign.TryEnterRead(250, true))
            {
                pick = default;
                return false;
            }
            try { return TryPick(pixel, out pick, updateSelection); }
            finally { campaign.ExitRead(); }
        }
        return TryPick(pixel, out pick, updateSelection);
    }

    /// <summary>
    /// Where on the Earth a pixel of the screen is: latitude and longitude, the raster cell,
    /// and the province under it (0 or less when the map has no provinces yet). False off the
    /// edge of the globe or the flat map.
    /// </summary>
    private bool TryLocate(Vector2 pixel, out float latitude, out float longitude, out int cell, out int provinceId)
    {
        latitude = longitude = 0f;
        cell = 0;
        provinceId = -1;
        if (!IsInstanceValid(_camera)) return false;

        var origin = _camera.ProjectRayOrigin(pixel);
        var direction = _camera.ProjectRayNormal(pixel).Normalized();
        if (_flatMap)
        {
            if (Mathf.Abs(direction.Z) < 0.0001f) return false;
            var rayDistance = -origin.Z / direction.Z;
            if (rayDistance < 0f) return false;
            var surface = origin + direction * rayDistance;
            if (surface.Y < -FlatMapHeight * 0.5f || surface.Y > FlatMapHeight * 0.5f) return false;
            longitude = surface.X / FlatMapWidth * 360f;
            latitude = surface.Y / FlatMapHeight * 180f;
        }
        else
        {
            var b = origin.Dot(direction);
            var c = origin.LengthSquared() - GlobeRadius * GlobeRadius;
            var discriminant = b * b - c;
            if (discriminant < 0f) return false;
            var rayDistance = -b - Mathf.Sqrt(discriminant);
            if (rayDistance < 0f) return false;
            var surface = (origin + direction * rayDistance).Normalized();
            latitude = Mathf.RadToDeg(Mathf.Asin(surface.Y));
            longitude = Mathf.RadToDeg(Mathf.Atan2(surface.X, surface.Z)) - 180f;
        }
        while (longitude <= -180f) longitude += 360f;
        while (longitude > 180f) longitude -= 360f;

        var x = Mathf.Clamp((int)((longitude + 180f) / 360f * SourceEarthData.Width), 0, SourceEarthData.Width - 1);
        var y = Mathf.Clamp((int)((90f - latitude) / 180f * SourceEarthData.Height), 0, SourceEarthData.Height - 1);
        cell = y * SourceEarthData.Width + x;
        provinceId = _provinceTopology is null ? -1 : _provinceTopology.CellToProvince[cell];
        return true;
    }

    private bool FinishPick(Simulation.Nation nation, float latitude, float longitude, int cell, int provinceId,
        bool updateSelection, out GlobePick pick)
    {
        // The live region, not the authored one. `SourceEarthData.States` is the raster as
        // it was drawn: it still says the whole of Luhansk is one region long after a border
        // has cut it in two, so reading the panel off it answers about a region the player
        // is not standing in - which is how clicking on ground Russia holds produced
        // Ukraine's fog of war.
        var regionMapIndex = _campaign?.ProvinceMap is { } liveRegions
            ? (provinceId > 0 && provinceId < liveRegions.ProvinceToState.Length ? liveRegions.ProvinceToState[provinceId] : 0)
            : (SourceEarthData.PeekStatesIfLoaded() is { } loadedStates ? loadedStates[cell] : SourceEarthData.States[cell]);
        EnsureStateIndex();
        _stateByMapIndex.TryGetValue(regionMapIndex, out var state);
        if (updateSelection)
        {
            var rawRegion = _provinceTopology is not null
                ? (provinceId > 0 && provinceId < _provinceTopology.ProvinceToState.Length ? _provinceTopology.ProvinceToState[provinceId] : 0)
                : (SourceEarthData.PeekStatesIfLoaded() is { } loadedRawStates ? loadedRawStates[cell] : SourceEarthData.States[cell]);
            SetSelectionShaderParams(active: true, regionId: rawRegion, provinceId: provinceId);
            SelectedRegionMembers(provinceId > 0 && _campaign?.ProvinceMap is not null ? regionMapIndex : -1);
            SetTradeFocusNation(nation is { Movement: null } ? nation.Id : -1);
        }
        pick = new GlobePick(nation, state, provinceId, latitude, longitude);
        return true;
    }

    private ulong _lastTouchInputUsec;

    private bool IsTouchEmulatedMouse(InputEventMouse mouseEvent)
    {
        if (mouseEvent.Device == -1) return true;
        if (_touchAdapter is { IsTouchActive: true }) return true;
        if (_lastTouchInputUsec > 0 && (Time.GetTicksUsec() - _lastTouchInputUsec) < 350_000UL) return true;
        return false;
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        if (LoadingInputBlocked || MediaLibraryInputBlocked || MapLayerDrawerInputBlocked) { AcceptEvent(); return; }

        // Route plan-drawing touch first, then TouchCameraAdapter, swallowing touch-emulated mouse.
        if (inputEvent is InputEventScreenTouch or InputEventScreenDrag)
        {
            _lastTouchInputUsec = Time.GetTicksUsec();
            if (HandleAnyTouchInput(inputEvent))
            {
                AcceptEvent();
                return;
            }
        }
        if (inputEvent is InputEventMouse mouseEvent && IsTouchEmulatedMouse(mouseEvent))
        {
            AcceptEvent();
            return;
        }

        if (PlanEraseMode && inputEvent is InputEventMouseButton erase && erase.ButtonIndex == MouseButton.Left)
        {
            if (erase.Pressed && TryBeginActionRead(out var eraseCampaign, 150))
            {
                int lineId = -1;
                bool objectiveOnly = false;
                bool hit;
                try { hit = TryPickPlan(erase.Position, out lineId, out objectiveOnly); }
                finally { eraseCampaign.ExitRead(); }
                if (hit) PlanEraseRequested?.Invoke(lineId, objectiveOnly);
            }
            AcceptEvent(); return;
        }
        if (_freeCamera && HandleFreeCameraInput(inputEvent)) { AcceptEvent(); return; }
        if (BorderPaintMode && HandleBorderInput(inputEvent)) return;
        if (PaintMode && HandlePaintInput(inputEvent)) return;

        if (inputEvent is InputEventMouseButton button)
        {
            var scheme = Simulation.Controls.ControlSchemeRules.ActiveMouseScheme;
            var threshPx = Simulation.Controls.ControlSchemeRules.DragThresholdPx;
            var threshMs = Simulation.Controls.ControlSchemeRules.DragThresholdMs;
            var nowMs = Time.GetTicksMsec();

            if (button.ButtonIndex == MouseButton.Left)
            {
                if (button.Pressed)
                {
                    _gesture.BeginPress(scheme, Simulation.Controls.MapPointerButton.Left,
                        button.Position.X, button.Position.Y, nowMs,
                        button.CtrlPressed, button.AltPressed, button.ShiftPressed,
                        allowRegionBox: !CountrySelectionOnly && ProvincesBoxSelected is not null);

                    if (_gesture.BoxStarted)
                    {
                        _boxSelecting = true;
                        _boxRegions = _gesture.BoxRegions;
                        _boxStart = button.Position;
                        _boxCurrent = button.Position;
                        _dragging = false;
                    }
                    else
                    {
                        _boxSelecting = false;
                        _boxRegions = false;
                        _cameraDragButton = MouseButton.Left;
                        _previousPointer = button.Position;
                        _dragDistance = 0f;
                        _flatGlide = Vector2.Zero;
                        _orbitGlide = Vector2.Zero;
                        _lastFlatDragUsec = Time.GetTicksUsec();
                        _globeZoomAnchorActive = false;
                        _flatZoomAnchorActive = false;
                        _dragging = false;
                    }
                    QueueRedraw();
                }
                else if (_gesture.IsPressed && _gesture.Button == Simulation.Controls.MapPointerButton.Left)
                {
                    var outcome = _gesture.ReleaseLeft(button.Position.X, button.Position.Y, threshPx);
                    _boxSelecting = false;
                    _boxRegions = false;
                    _dragging = false;
                    _boxCurrent = button.Position;

                    if (outcome == Simulation.Controls.LmbReleaseOutcome.BoxRegionSelect)
                    {
                        List<int> provinces = null;
                        if (TryBeginActionRead(out var boxCampaign, 150))
                        {
                            try
                            {
                                var area = RectFromPoints(_boxStart, _boxCurrent);
                                provinces = area.Size.Length() >= threshPx ? ProvincesInScreenRect(area) : new List<int>();
                                if (provinces.Count == 0 && TryPick(button.Position, out var single, updateSelection: false) && single.ProvinceId > 0)
                                    provinces.Add(single.ProvinceId);
                            }
                            finally { boxCampaign.ExitRead(); }
                        }
                        if (provinces is { Count: > 0 }) ProvincesBoxSelected?.Invoke(provinces);
                    }
                    else if (outcome == Simulation.Controls.LmbReleaseOutcome.BoxArmySelect)
                    {
                        DispatchBoxUnitSelection(RectFromPoints(_boxStart, _boxCurrent));
                    }
                    else if (outcome == Simulation.Controls.LmbReleaseOutcome.ClickSelect)
                    {
                        DispatchPrimaryClick(button.Position);
                    }
                    QueueRedraw();
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex is MouseButton.Right or MouseButton.Middle)
            {
                var ptrBtn = button.ButtonIndex == MouseButton.Right
                    ? Simulation.Controls.MapPointerButton.Right
                    : Simulation.Controls.MapPointerButton.Middle;

                if (button.Pressed)
                {
                    _gesture.BeginPress(scheme, ptrBtn, button.Position.X, button.Position.Y, nowMs,
                        button.CtrlPressed, button.AltPressed, button.ShiftPressed, allowRegionBox: false);
                    if (_gesture.BoxStarted)
                    {
                        _boxSelecting = true;
                        _boxRegions = false;
                        _boxStart = button.Position;
                        _boxCurrent = button.Position;
                        _dragging = false;
                    }
                    else
                    {
                        BeginCameraDrag(button.Position, button.ButtonIndex);
                        _dragging = _gesture.PanStarted;
                        _boxSelecting = false;
                    }
                    QueueRedraw();
                }
                else
                {
                    if (button.ButtonIndex == MouseButton.Middle && _gesture.IsPressed &&
                        _gesture.Button == Simulation.Controls.MapPointerButton.Middle)
                    {
                        var outcome = _gesture.ReleaseMiddle(button.Position.X, button.Position.Y, threshPx);
                        _boxSelecting = false;
                        _dragging = false;
                        _boxCurrent = button.Position;
                        if (outcome == Simulation.Controls.MmbReleaseOutcome.BoxArmySelect && !CountrySelectionOnly)
                        {
                            DispatchBoxUnitSelection(RectFromPoints(_boxStart, _boxCurrent));
                        }
                        else if (outcome == Simulation.Controls.MmbReleaseOutcome.ClickArmySelect && !CountrySelectionOnly &&
                                 TryBeginActionRead(out var mmbCampaign, 150))
                        {
                            IReadOnlyList<Simulation.Army> mmbStack = null;
                            var picked = false;
                            try { picked = TryPickArmyStack(button.Position, out mmbStack); }
                            finally { mmbCampaign.ExitRead(); }
                            if (picked)
                            {
                                if (mmbStack.Count == 1) ArmyPicked?.Invoke(mmbStack[0]);
                                else if (mmbStack.Count > 1) ArmiesBoxPicked?.Invoke(mmbStack);
                            }
                        }
                        QueueRedraw();
                    }
                    else if (button.ButtonIndex == MouseButton.Right && _gesture.IsPressed &&
                             _gesture.Button == Simulation.Controls.MapPointerButton.Right)
                    {
                        var outcome = _gesture.ReleaseRight(button.Position.X, button.Position.Y, nowMs,
                            threshPx, threshMs, button.AltPressed);
                        _dragging = false;
                        if (!PlanEraseMode && !CountrySelectionOnly &&
                            outcome is Simulation.Controls.RmbReleaseOutcome.AltSplitMove or Simulation.Controls.RmbReleaseOutcome.CommandOrContextMenu &&
                            TryBeginActionRead(out var rmbCampaign, 150))
                        {
                            GlobePick pick = default;
                            var picked = false;
                            try { picked = TryPick(button.Position, out pick, updateSelection: false); }
                            finally { rmbCampaign.ExitRead(); }
                            if (picked)
                            {
                                if (outcome == Simulation.Controls.RmbReleaseOutcome.AltSplitMove)
                                    AltProvinceSplitCommanded?.Invoke(pick);
                                else
                                    ProvinceCommanded?.Invoke(pick);
                            }
                        }
                    }
                    else if (button.ButtonIndex == _cameraDragButton)
                    {
                        _dragging = false;
                    }
                }
                AcceptEvent();
                return;
            }

            if (button.ButtonIndex == MouseButton.WheelUp || button.ButtonIndex == MouseButton.WheelDown)
            {
                _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero;
                var sign = button.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
                if (_flatMap)
                    ZoomFlatMapAt(button.Position, sign, button.ShiftPressed);
                else
                    ZoomGlobeAt(button.Position, sign, button.ShiftPressed);
                AcceptEvent();
                return;
            }
        }

        if (inputEvent is InputEventMouseMotion gestureMotion && _gesture.IsPressed)
        {
            var threshPx = Simulation.Controls.ControlSchemeRules.DragThresholdPx;
            var threshMs = Simulation.Controls.ControlSchemeRules.DragThresholdMs;
            var nowMs = Time.GetTicksMsec();
            var shouldPan = _gesture.UpdateMotion(gestureMotion.Position.X, gestureMotion.Position.Y,
                nowMs, threshPx, threshMs, out var panDx, out var panDy);

            if (_gesture.BoxStarted)
            {
                _boxCurrent = gestureMotion.Position;
                QueueRedraw();
                AcceptEvent();
                return;
            }

            if (shouldPan)
            {
                if (!_dragging)
                {
                    _dragging = true;
                    _previousPointer = gestureMotion.Position - new Vector2(panDx, panDy);
                    _lastFlatDragUsec = Time.GetTicksUsec();
                    _globeZoomAnchorActive = false;
                    _flatZoomAnchorActive = false;
                    _targetFlatX = _flatX;
                    _targetFlatY = _flatY;
                    _targetFlatZoom = _flatZoom;
                    _targetYaw = _yaw;
                    _targetPitch = _pitch;
                    _targetDistance = _distance;
                }
                DragCamera(new Vector2(panDx, panDy));
                _previousPointer = gestureMotion.Position;
            }
            AcceptEvent();
            return;
        }

        if (inputEvent is InputEventMouseMotion boxMotion && _boxSelecting)
        {
            _boxCurrent = boxMotion.Position;
            QueueRedraw();
            AcceptEvent();
            return;
        }

        if (inputEvent is InputEventMouseMotion motion && _dragging && !_boxSelecting)
        {
            DragCamera(motion.Position - _previousPointer);
            _previousPointer = motion.Position;
            AcceptEvent();
        }
    }

    /// <summary>
    /// Move the world under the pointer by a mouse delta - a yaw and a pitch on the globe,
    /// a pan on the flat map. Its own method because the paint modes need it too: while a
    /// peace is being drawn both mouse buttons are the brush, and a camera the player cannot
    /// move is a peace conference they cannot finish when the war is on the far side of the
    /// world.
    /// </summary>
    private void DragCamera(Vector2 delta, bool recordGlide = true)
    {
        _dragDistance += delta.Length();
        if (_flatMap)
        {
            _flatZoomAnchorActive = false;
            // The current zoom, not the one being lerped towards: what a pixel is worth is
            // decided by what is on the screen now, so dragging while the wheel is still
            // settling moves the ground by the distance the hand moved rather than by the
            // distance it will be worth in a moment.
            var unitsPerPixel = MapPanScale.FlatUnitsPerPixel(_flatZoom, ViewportSize().Y);
            _targetFlatX -= delta.X * unitsPerPixel;
            _targetFlatY += delta.Y * unitsPerPixel;
            ClampFlatCamera();
            if (recordGlide)
            {
                // How fast the hand is moving, smoothed over the last few motion events, for the glide.
                var now = Time.GetTicksUsec();
                var seconds = Mathf.Clamp((now - _lastFlatDragUsec) / 1_000_000f, 0.004f, 0.1f);
                _lastFlatDragUsec = now;
                var speed = new Vector2(-delta.X, delta.Y) * unitsPerPixel / seconds;
                _flatGlide = _flatGlide.Lerp(speed, 0.45f).LimitLength(_flatZoom * 3.5f);
            }
            else
            {
                // Touch gestures carry their own smooth exponential inertia in TouchGestureMachine.
                _flatGlide = Vector2.Zero;
            }

            // And the camera goes there, rather than being asked to.
            //
            // A drag is direct manipulation: the ground you took hold of has to stay under
            // the pointer. Moving only the target left the camera closing eleven per cent of
            // the gap per frame - a hundred and forty milliseconds of lag, measured, with the
            // whole of a drag's distance still outstanding at the end of it - so the map
            // trailed the hand and kept sliding after the button came up. Which is exactly
            // what a map that "moves more slowly" feels like, even though the arithmetic
            // moving the target was right to six decimal places.
            //
            // The wheel and the keyboard keep their smoothing: nobody expects a zoom to
            // snap, and WASD adds a little every frame, which is what the lerp is for.
            _flatX = _targetFlatX;
            _flatY = _targetFlatY;
            return;
        }
        // One pixel of hand movement should move approximately one pixel of ground at
        // the view centre. Use the displayed altitude, never a pending wheel/fly-to target.
        var radians = MapPanScale.OrbitRadiansPerPixel(_distance, GlobeRadius, _camera.Fov, ViewportSize().Y);
        var longitudeScale = Math.Max(.15f, Mathf.Cos(_pitch));
        var movement = new Vector2(-delta.X * radians / longitudeScale, -delta.Y * radians);
        _targetYaw += movement.X;
        _targetPitch = Mathf.Clamp(_targetPitch + movement.Y, -MaxPitch, MaxPitch);
        _yaw = _targetYaw;
        _pitch = _targetPitch;
        if (recordGlide)
        {
            var dragNow = Time.GetTicksUsec();
            var dragSeconds = Mathf.Clamp((dragNow - _lastFlatDragUsec) / 1_000_000f, .004f, .1f);
            _lastFlatDragUsec = dragNow;
            _orbitGlide = _orbitGlide.Lerp(movement / dragSeconds, 1f - Mathf.Exp(-dragSeconds * 24f))
                .LimitLength(radians * ViewportSize().Y * .9f / longitudeScale);
        }
        else
        {
            _orbitGlide = Vector2.Zero;
        }
    }

    /// <summary>
    /// Outline one country on the map, or none.
    ///
    /// Takes a map index rather than a nation so that clearing it is a number rather than a
    /// null, and so the globe does not have to know what a Nation is to draw one.
    /// </summary>
    public void HighlightNation(int mapIndex)
    {
        if (_highlightedNation == mapIndex) return;
        _highlightedNation = mapIndex;
        WakeGlobeRendering();
        _globeMaterial?.SetShaderParameter("highlighted_nation", (float)mapIndex);
        InvalidateCities();
    }

    private int _highlightedNation = -1;
    private ShaderMaterial _cachedSelectionMaterial;
    private float _cachedSelectionActive = float.NaN;
    private float _cachedSelectionRegionId = float.NaN;
    private float _cachedSelectionProvinceId = float.NaN;
    private float _cachedSelectionRegionLive = float.NaN;

    private void SetSelectionShaderParams(bool active, float regionId, float provinceId, float? regionLive = null)
    {
        if (_globeMaterial is null) return;
        if (!ReferenceEquals(_cachedSelectionMaterial, _globeMaterial))
        {
            _cachedSelectionMaterial = _globeMaterial;
            _cachedSelectionActive = float.NaN;
            _cachedSelectionRegionId = float.NaN;
            _cachedSelectionProvinceId = float.NaN;
            _cachedSelectionRegionLive = float.NaN;
        }
        var activeVal = active ? 1f : 0f;
        if (_cachedSelectionActive != activeVal)
        {
            _cachedSelectionActive = activeVal;
            WakeGlobeRendering();
            _globeMaterial.SetShaderParameter("selection_active", activeVal);
        }
        if (_cachedSelectionRegionId != regionId)
        {
            _cachedSelectionRegionId = regionId;
            WakeGlobeRendering();
            _globeMaterial.SetShaderParameter("selected_region_id", regionId);
        }
        if (_cachedSelectionProvinceId != provinceId)
        {
            _cachedSelectionProvinceId = provinceId;
            WakeGlobeRendering();
            _globeMaterial.SetShaderParameter("selected_province_id", provinceId);
        }
        if (regionLive is { } live && _cachedSelectionRegionLive != live)
        {
            _cachedSelectionRegionLive = live;
            _globeMaterial.SetShaderParameter("selected_region_live", live);
        }
    }

    /// <summary>
    /// Take the picked region and province off the map. The tint lives in shader uniforms
    /// rather than in a node, so hiding the panel that described it left the ground lit as
    /// if it were still selected. Closing that panel now calls this.
    /// </summary>
    public void ClearSelection()
    {
        SetSelectionShaderParams(active: false, regionId: -1f, provinceId: -1f, regionLive: 0f);
        SetTradeFocusNation(-1);
    }

    private Image _selectedRegionImage;
    private ImageTexture _selectedRegionTexture;
    private readonly byte[] _selectedRegionPixels = new byte[256 * 256];
    // What the uploaded membership texture currently shows: tapping the same live region again
    // (on the same map, same revision) re-lights it without walking every province or re-uploading.
    private int _lastSelectedRegionLive = int.MinValue;
    private Simulation.Campaign _lastSelectedRegionCampaign;
    private object _lastSelectedRegionMap;
    private int _lastSelectedRegionRevision = int.MinValue;

    /// <summary>
    /// Light the live region the player picked, every province of it. The tint used to follow
    /// the authored raster, so a region a border had cut lit up as the whole region it had been
    /// in 2024 - and a region stitched together from pieces lit up as only the piece clicked.
    /// One byte per province, read by the shader the way the front membership is.
    /// </summary>
    private void SelectedRegionMembers(int liveRegion)
    {
        if (_globeMaterial is null) return;
        var map = _campaign?.ProvinceMap;
        if (liveRegion < 0 || map is null)
        {
            if (!ReferenceEquals(_cachedSelectionMaterial, _globeMaterial) || _cachedSelectionRegionLive != 0f)
            {
                _cachedSelectionMaterial = _globeMaterial;
                _cachedSelectionRegionLive = 0f;
                WakeGlobeRendering();
                _globeMaterial.SetShaderParameter("selected_region_live", 0f);
            }
            return;
        }
        var revision = _campaign.MapRevision;
        if (liveRegion == _lastSelectedRegionLive && ReferenceEquals(_campaign, _lastSelectedRegionCampaign) &&
            ReferenceEquals(map, _lastSelectedRegionMap) && revision == _lastSelectedRegionRevision &&
            _selectedRegionTexture is not null)
        {
            if (!ReferenceEquals(_cachedSelectionMaterial, _globeMaterial) || _cachedSelectionRegionLive != 1f)
            {
                _cachedSelectionMaterial = _globeMaterial;
                _cachedSelectionRegionLive = 1f;
                _globeMaterial.SetShaderParameter("selected_region_live", 1f);
            }
            return;
        }
        Array.Clear(_selectedRegionPixels);
        var states = map.ProvinceToState;
        for (var province = 1; province < states.Length && province < _selectedRegionPixels.Length; province++)
            if (states[province] == liveRegion) _selectedRegionPixels[province] = 255;
        if (_selectedRegionImage is null) _selectedRegionImage = Image.CreateFromData(256, 256, false, Image.Format.R8, _selectedRegionPixels);
        else _selectedRegionImage.SetData(256, 256, false, Image.Format.R8, _selectedRegionPixels);
        UpdateMapImage("selected_region_members", _selectedRegionImage, ref _selectedRegionTexture);
        _cachedSelectionMaterial = _globeMaterial;
        _cachedSelectionRegionLive = 1f;
        WakeGlobeRendering();
        _globeMaterial.SetShaderParameter("selected_region_live", 1f);
        _lastSelectedRegionLive = liveRegion;
        _lastSelectedRegionCampaign = _campaign;
        _lastSelectedRegionMap = map;
        _lastSelectedRegionRevision = revision;
    }

    private Vector2 _lastHoverTooltipPos = new(float.NaN, float.NaN);
    private ulong _lastHoverTooltipMsec;

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (LoadingInputBlocked || MediaLibraryInputBlocked || MapLayerDrawerInputBlocked || InterfaceHidden ||
            Platform.PlatformProfile.Current?.IsMobile == true ||
            _dragging || _gesture.IsPressed || GlobeViewInMotion || FlatViewInMotion ||
            _touchAdapter is { IsTouchActive: true } ||
            (_lastTouchInputUsec > 0 && (Time.GetTicksUsec() - _lastTouchInputUsec) < 350_000UL))
        {
            _cachedHoverTooltip = "";
            _lastHoverTooltipPos = new Vector2(float.NaN, float.NaN);
            return "";
        }
        var nowMs = Time.GetTicksMsec();
        if (float.IsFinite(_lastHoverTooltipPos.X) &&
            atPosition.DistanceSquaredTo(_lastHoverTooltipPos) < 9f &&
            nowMs - _lastHoverTooltipMsec < 60UL)
        {
            return _cachedHoverTooltip;
        }
        var campaign = _campaign;
        if (campaign is not null && !campaign.TryEnterRead(0, true))
            return _cachedHoverTooltip;
        try
        {
            _cachedHoverTooltip = BuildHoverTooltip(atPosition);
            _lastHoverTooltipPos = atPosition;
            _lastHoverTooltipMsec = nowMs;
            return _cachedHoverTooltip;
        }
        finally { campaign?.ExitRead(); }
    }

    private string BuildHoverTooltip(Vector2 atPosition)
    {
        var badgeTip = RegionBadgeTooltip(atPosition);
        if (badgeTip.Length > 0) return badgeTip;
        var supportTip = SupportMapTooltip(atPosition);
        if (supportTip.Length > 0) return supportTip;
        var eventTip = EventPulseTooltip(atPosition);
        if (eventTip.Length > 0) return eventTip;
        if (TryPickBattle(atPosition, out var battle))
        {
            var attacker = _campaign?.NationById(battle.AttackerId);
            var defender = _campaign?.NationById(battle.DefenderId);
            var latest = battle.Rounds.LastOrDefault();
            var unknown = Simulation.Loc.Get("Unknown");
            if (FogOfWarActive && _campaign?.Player is { IsHuman: true } player &&
                battle.AttackerId != player.Id && battle.DefenderId != player.Id)
                return Simulation.Loc.Format(
                    "Observed battle · province #{0:N0}\n{1} vs {2}\nDetailed foreign combat reports unavailable.",
                    battle.ProvinceId, attacker?.DisplayName ?? unknown, defender?.DisplayName ?? unknown);
            var battleText = Simulation.Loc.Format("BATTLE · province #{0:N0}\n{1} vs {2}\nLosses {3:N0} / {4:N0}",
                       battle.ProvinceId, attacker?.DisplayName ?? unknown, defender?.DisplayName ?? unknown,
                       battle.AttackerLosses, battle.DefenderLosses) + "\n" + BattleMapTooltip(battle) +
                   (latest is null ? "" : "\n" + Simulation.Loc.Format(
                       "Power {0:0.0} / {1:0.0} · plan {2:P0}",
                       latest.AttackerPower, latest.DefenderPower, latest.AttackerPlanning));
            if (_campaign is not null)
                foreach (var unit in _campaign.Armies)
                    if (unit.BattleId == battle.Id)
                    {
                        battleText += "\n" + Simulation.StaffReasons.Explain(_campaign, unit);
                        break;
                    }
            return battleText;
        }
        if (TryPickArmyStack(atPosition, out var stack, selectableOnly: false))
        {
            if (stack.Count == 0) return "";
            var lead = stack[0];
            var owner = _campaign?.NationById(lead.NationId);
            var name = owner?.DisplayName ?? Simulation.Loc.Get("Unknown");
            var personnel = stack.Sum(DivisionPersonnel);
            var condition = Simulation.MilitaryConditionReadout.ForPlayer(_campaign, stack, !FogOfWarActive);
            var readiness = condition.Known ?
                Simulation.Loc.Format("Organisation: {0:P0}", condition.Organisation) + "\n" +
                Simulation.Loc.Format("Equipment: {0:P0}", condition.Equipment) : "";
            if (!Simulation.CampaignIntel.PlayerCanInspect(_campaign, lead.NationId))
                return Simulation.Loc.Format("{0} · {1:N0} observed divisions", name, stack.Count) + "\n" +
                       Simulation.Loc.Format("Observed personnel: {0:N0}", personnel) + "\n" +
                       (readiness.Length == 0 ? "" : Simulation.Loc.Get("Observed combat readiness") + "\n" + readiness + "\n") +
                       (condition.Formations > 1 ? Simulation.Loc.Get("Division-weighted average of observed formations.") + "\n" : "") +
                       Simulation.Loc.Get("Foreign formation — orders and route are not available.");
            // Russian counts in three - 1 дивизия, 2 дивизии, 5 дивизий - so the count and
            // its noun have to travel together through the table rather than have an "s"
            // stitched onto the end of one language's plural.
            return Simulation.Loc.Format(Simulation.Plural.Key("{0} · {1:N0} division", stack.Count),
                       name, stack.Count) + "\n" +
                   Simulation.Loc.Format("Personnel {0:N0} · organisation {1:P0}",
                       personnel, stack.Average(army => army.Organisation)) + "\n" +
                   Simulation.Loc.Format("Equipment {0:P0} · experience {1:P0} · supply {2:P0}",
                       stack.Average(army => army.Equipment), stack.Average(army => army.Experience),
                       stack.Average(army => army.SupplyLevel)) + "\n" +
                   (stack.Count == 1
                       ? Simulation.Loc.Format("{0} · {1} posture · planning {2:P0}",
                           Simulation.ArmyVocabulary.OrderName(lead.OrderKind),
                           Simulation.ArmyVocabulary.StanceName(lead.Stance), lead.Planning)
                       : Simulation.Loc.Get("Click to select every division represented by this counter. A + marks nearby provinces grouped at this zoom.")) +
                   "\n" + Simulation.ArmyVocabulary.Situation(_campaign, lead) +
                   "\n" + Simulation.StaffReasons.Explain(_campaign, lead);
        }
        if (!TryPick(atPosition, out var pick, updateSelection: false)) return UnclaimedTooltip(atPosition);
        var state = pick.State is null
            ? Simulation.Loc.Get("Unmapped administrative area")
            : pick.State.DisplayName + " · " + Simulation.Loc.Get(pick.State.Kind);
        var controller = pick.ProvinceId > 0 && _campaign?.ProvinceController is { } controllers
            ? _nationByMapIndex.GetValueOrDefault(controllers[pick.ProvinceId])
            : pick.Nation;
        var occupation = pick.ProvinceId > 0 && _campaign?.ProvinceOccupation is { } occupations
            ? occupations[pick.ProvinceId]
            : 0f;
        var roads = Simulation.WaterConditions.RoadThroughput(_campaign, pick.Latitude);
        var hydrology = pick.ProvinceId > 0 && _provinceTopology is not null
            ? (_provinceTopology.HasRiver(pick.ProvinceId)
                ? "\n" + Simulation.Loc.Get("River terrain · crossings affect march and attack") +
                    "\n" + Simulation.Loc.Format("Seasonal crossing difficulty: {0:P0}", Simulation.WaterConditions.CrossingDifficulty(_campaign, pick.Latitude)) : "") +
              (_provinceTopology.HasLakeAccess(pick.ProvinceId)
                ? "\n" + Simulation.Loc.Get("Lake shore · local supply access") +
                    "\n" + Simulation.Loc.Format("Lake logistics availability: {0:P0}", Simulation.WaterConditions.LakeAvailability(_campaign, pick.Latitude)) : "") +
              (roads < 0.995f
                ? "\n" + Simulation.Loc.Format("Winter roads carry {0:P0} of fair-weather traffic.", roads) : "")
            : "";
        return pick.Nation.DisplayName + " (" + pick.Nation.Tag + ")\n" + state + "\n" +
               Simulation.StateIdentity.Brief(_campaign, pick.Nation) + "\n" +
               Simulation.Loc.Format("Province #{0:N0}", pick.ProvinceId) +
               (controller is null || controller == pick.Nation ? ""
                   // Held by arrangement - a lease, troops without a war - says so, not "occupied".
                   : pick.State is not null && Simulation.LandArrangementSystem.Covering(_campaign, pick.State.MapIndex, controller.Id) is not null
                       ? "\n" + Simulation.LandArrangementSystem.Describe(_campaign, pick.State)
                       : "\n" + Simulation.Loc.Format(
                   "Controlled by {0} · {1:P0} secured\nStill owned by {2}: control is not an annexation. Only a signed peace moves the border.",
                   controller.DisplayName, occupation, pick.Nation.DisplayName)) +
               (_mapMode == StrategicMapMode.Resources && pick.State is not null ? "\n" + Simulation.Loc.Format(
                   "{0} · resource potential {1:0.##}", Simulation.Loc.Get(Simulation.CommodityCatalog.Get(_resourceGood).Name),
                   Simulation.ResourceAtlas.Potential(pick.State, _resourceGood)) : "") +
               hydrology + (pick.ProvinceId > 0 && _provinceTopology is not null
                   ? TerrainDescription(_provinceTopology.TerrainAt(pick.ProvinceId)) : "");
    }

    private List<Simulation.Army> ArmiesInScreenRect(Rect2 rect)
    {
        var selected = new List<Simulation.Army>();
        if (!IsInstanceValid(_camera) || (!_flatMap && _distance > 13.5f)) return selected;
        foreach (var marker in _armyStacks.Values)
        {
            var node = marker.Node;
            // A counter standing behind another country's in the same province is hidden, not
            // gone: a box drawn over the province takes its divisions too (CoverSharedProvinces).
            if (!IsInstanceValid(node) || (!node.IsVisibleInTree() && !_coveredCounters.Contains(node)) ||
                !marker.Plate.Visible || _camera.IsPositionBehind(node.GlobalPosition)) continue;
            if (rect.HasPoint(_camera.UnprojectPosition(node.GlobalPosition)))
                selected.AddRange((marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack)
                    .Where(army => Simulation.ArmyInteraction.CanSelect(_campaign, army)));
        }
        return selected;
    }

    public IReadOnlyList<Simulation.Army> VisiblePlayerArmiesOnScreen(int playerId)
    {
        var result = new List<Simulation.Army>();
        if (!TryBeginActionRead(out var campaign, 150)) return result;
        try
        {
            if (!IsInstanceValid(_camera)) return result;
            var vp = ViewportSize();
            var rect = new Rect2(Vector2.Zero, new Vector2(vp.X, vp.Y));
            var viewNormal = _camera.GlobalPosition.Normalized();
            var seen = new HashSet<int>();
            foreach (var marker in _armyStacks.Values)
            {
                var node = marker.Node;
                if (!IsInstanceValid(node) || !marker.Plate.Visible || _camera.IsPositionBehind(node.GlobalPosition))
                    continue;
                if (!_flatMap && node.GlobalPosition.Normalized().Dot(viewNormal) <= 0.05f)
                    continue;
                if (!rect.HasPoint(_camera.UnprojectPosition(node.GlobalPosition)))
                    continue;
                var stack = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
                foreach (var army in stack)
                    if (army.NationId == playerId && Simulation.ArmyInteraction.CanSelect(campaign, army) && seen.Add(army.Id))
                        result.Add(army);
            }
        }
        finally { campaign.ExitRead(); }
        return result;
    }

    /// <summary>
    /// Every province the screen rectangle covers.
    ///
    /// By sampling rather than by projecting each province, because the map has thirty-eight
    /// thousand of them and the rectangle has a few hundred thousand pixels: walking the
    /// smaller of the two is the cheaper question, and the same ray-pick the brush already
    /// uses answers it. The step widens with the rectangle so that selecting a continent
    /// costs about what selecting a county does; three pixels is finer than any province is
    /// small at a zoom where a player would draw a box at all.
    /// </summary>
    private List<int> ProvincesInScreenRect(Rect2 rect)
    {
        var found = new List<int>();
        if (rect.Size.X < 2f || rect.Size.Y < 2f) return found;

        var seen = new HashSet<int>();
        var step = Mathf.Max(3f, rect.Size.Length() / 420f);
        for (var y = rect.Position.Y; y <= rect.End.Y; y += step)
        for (var x = rect.Position.X; x <= rect.End.X; x += step)
        {
            if (!TryPick(new Vector2(x, y), out var pick, updateSelection: false)) continue;
            if (pick.ProvinceId <= 0 || !seen.Add(pick.ProvinceId)) continue;
            found.Add(pick.ProvinceId);
        }
        return found;
    }

    private bool TryDispatchBoxUnitSelection(Rect2 rect, int timeoutMs = 0, bool priority = false)
    {
        if (CountrySelectionOnly) return true;
        Simulation.Campaign campaign;
        if (priority ? !TryBeginPriorityActionRead(out campaign, timeoutMs) : !TryBeginActionRead(out campaign, timeoutMs))
            return false;
        // Divisions and long-range formations in one box, as one selection -
        // the panel no longer has to be open for a box to take formations.
        List<Simulation.Army> selected;
        List<Simulation.SupportFormation> formations;
        List<Simulation.Fleet> fleets = null;
        try
        {
            selected = ArmiesInScreenRect(rect);
            formations = SupportInScreenRect(rect);
            if (selected.Count == 0 && formations.Count == 0)
                fleets = FleetsInScreenRect(rect);
        }
        finally { campaign.ExitRead(); }

        if (selected.Count > 0 || formations.Count > 0)
        {
            if (SelectionBoxPicked is not null) SelectionBoxPicked.Invoke(selected, formations);
            else if (selected.Count > 0) ArmiesBoxPicked?.Invoke(selected);
        }
        else if (fleets is { Count: > 0 })
            FleetsBoxPicked?.Invoke(fleets);
        return true;
    }

    private static Rect2 RectFromPoints(Vector2 a, Vector2 b)
    {
        var min = new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y));
        var max = new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y));
        return new Rect2(min, max - min);
    }

    private Vector2I ViewportSize()
    {
        var size = GetViewport().GetVisibleRect().Size;
        return new Vector2I(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y));
    }

}
