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
    private float _yaw = -0.35f;
    // How far north or south the camera may look: 86 degrees, so Greenland, Svalbard and
    // Antarctica can be brought to the middle of the screen. It stopped at 69 degrees,
    // where the far north sat squashed against the top edge; short of 90 so the orbit
    // never flips over the pole.
    internal const float MaxPitch = 1.50f;
    private float _pitch = 0.30f;
    private float _targetYaw = -0.35f;
    private float _targetPitch = 0.30f;
    private float _distance = 12.5f;
    private float _targetDistance = 12.5f;
    private bool _globeZoomAnchorActive;
    private (double X, double Y, double Z) _globeZoomAnchorWorld;
    private Vector2 _globeZoomAnchorPixel;
    private bool _cameraTransformDirty = true;
    private float _lastAppliedYaw = float.NaN;
    private float _lastAppliedPitch = float.NaN;
    private float _lastAppliedDistance = float.NaN;
    private float _lastAppliedFlatX = float.NaN;
    private float _lastAppliedFlatY = float.NaN;
    private float _lastAppliedFlatZoom = float.NaN;
    private bool _lastAppliedFlatMap;
    private float _motionFrameYaw = float.NaN;
    private float _motionFramePitch = float.NaN;
    private float _motionFrameDistance = float.NaN;
    private float _motionFrameFlatX = float.NaN;
    private float _motionFrameFlatY = float.NaN;
    private float _motionFrameFlatZoom = float.NaN;
    private bool _motionFrameFlatMap;
    private bool _isActivelyPanning;

    internal bool IsActivelyPanning => _isActivelyPanning;

    /// <summary>Frames a geographic point exactly as the source globe's country focus does.</summary>
    public void FocusOn(float latitude, float longitude, float distance = 8f)
    {
        _flatGlide = Vector2.Zero; _orbitGlide = Vector2.Zero;
        _flatZoomAnchorActive = false;
        _cameraTransformDirty = true;
        if (_flatMap)
        {
            _targetFlatX = longitude / 18f;
            _targetFlatY = latitude / 18f;
            _targetFlatZoom = FlatZoomForDistance(distance);
            ClampFlatCamera();
            return;
        }
        _targetPitch = Mathf.Clamp(-Mathf.DegToRad(latitude), -MaxPitch, MaxPitch);
        _targetYaw = Mathf.DegToRad(longitude + 180f);
        _targetDistance = Mathf.Clamp(distance, MinimumDistance, MaximumDistance);
    }

    /// <summary>Unified camera distance across orbital and flat projections.</summary>
    private float ViewDistance => _flatMap ? _flatZoom * 1.6f - 1.04f : _distance;
    internal float ShellViewDistance => ViewDistance;
    internal float ShellFlatZoom => _flatZoom;
    internal IReadOnlySet<int> NewShellVisibleArmyIds { get; set; }
    internal Vector2? ProjectMapPointToScreen(float latitude, float longitude) => BadgeScreenPoint(latitude, longitude);
    internal (float Latitude, float Longitude, float SpanDegrees) CameraMinimapView =>
        _flatMap
            ? (Math.Clamp(_flatY * 18f, -85f, 85f), Math.Clamp(_flatX * 18f, -180f, 180f), Math.Clamp(_flatZoom * 14f, 12f, 180f))
            : (Math.Clamp(-Mathf.RadToDeg(_pitch), -85f, 85f), Mathf.PosMod(Mathf.RadToDeg(_yaw), 360f) - 180f, Math.Clamp(_distance * 6.5f, 14f, 160f));

    private bool _showcasePan;
    public bool ShowcasePan => _showcasePan;

    public void SetShowcasePan(bool enabled) => _showcasePan = enabled;

    /// <summary>
    /// Carry a released drag on and let it die away. While the hand still holds the map the
    /// glide waits; if the hand has rested more than a moment, there is nothing left to carry.
    /// </summary>
    private void UpdateFlatGlide(float delta)
    {
        if (!_cameraInertia) { _flatGlide = Vector2.Zero; return; }
        if (_dragging)
        {
            if (Time.GetTicksUsec() - _lastFlatDragUsec > 90_000) _flatGlide = Vector2.Zero;
            return;
        }
        if (_flatGlide.LengthSquared() < _flatZoom * _flatZoom * 1e-6f) { _flatGlide = Vector2.Zero; return; }
        _flatZoomAnchorActive = false;
        _targetFlatX += CameraInertia.Travel(_flatGlide.X, delta);
        _targetFlatY += CameraInertia.Travel(_flatGlide.Y, delta);
        ClampFlatCamera();
        _flatX = _targetFlatX;
        _flatY = _targetFlatY;
        _flatGlide *= Mathf.Exp(-CameraInertia.Damping * Math.Clamp(delta, 0, .1f));
    }

    private void UpdateOrbitGlide(float delta)
    {
        if (!_cameraInertia) { _orbitGlide = Vector2.Zero; return; }
        if (_dragging)
        {
            if (Time.GetTicksUsec() - _lastFlatDragUsec > 90_000) _orbitGlide = Vector2.Zero;
            return;
        }
        if (_orbitGlide.LengthSquared() < 1e-9f) { _orbitGlide = Vector2.Zero; return; }
        _targetYaw += CameraInertia.Travel(_orbitGlide.X, delta);
        _targetPitch = Mathf.Clamp(_targetPitch + CameraInertia.Travel(_orbitGlide.Y, delta), -MaxPitch, MaxPitch);
        _yaw = _targetYaw; _pitch = _targetPitch;
        _orbitGlide *= Mathf.Exp(-CameraInertia.Damping * Math.Clamp(delta, 0, .1f));
    }

    private void ZoomGlobeAt(Vector2 pixel, float direction, bool fine)
    {
        var vp = ViewportSize();
        var fov = IsInstanceValid(_camera) ? _camera.Fov : RigFieldOfView;
        var baseAltitude = Mathf.Max(0.10f, _targetDistance - GlobeRadius);
        var baseDelta = direction * baseAltitude * 0.22f;
        var delta = Simulation.Controls.ControlSchemeRules.ComputeZoomStep(baseDelta, fine, 0.35f);
        var nextDistance = Mathf.Clamp(_targetDistance + delta, MinimumDistance, MaximumDistance);

        if (vp.X >= 2 && vp.Y >= 2 &&
            Simulation.Controls.ControlSchemeRules.TryGlobeRayHitLocal(pixel.X, pixel.Y, vp.X, vp.Y, fov, GlobeRadius, _distance, out var hitLocal))
        {
            _globeZoomAnchorWorld = Simulation.Controls.ControlSchemeRules.OrbitLocalToWorld(hitLocal, _pitch, _yaw);
            _globeZoomAnchorPixel = pixel;
            _globeZoomAnchorActive = true;
            _targetDistance = nextDistance;
            if (Simulation.Controls.ControlSchemeRules.SolveGlobeZoomAnchor(
                pixel.X, pixel.Y, vp.X, vp.Y, fov, GlobeRadius, MaxPitch,
                _globeZoomAnchorWorld.X, _globeZoomAnchorWorld.Y, _globeZoomAnchorWorld.Z,
                _pitch, _yaw, nextDistance, out var nextPitch, out var nextYaw))
            {
                _targetPitch = nextPitch;
                _targetYaw = nextYaw;
            }
            return;
        }
        _globeZoomAnchorActive = false;
        _targetDistance = nextDistance;
    }

    /// <summary>
    /// Direct 1:1 globe pinch zoom anchored at the 2-finger midpoint when well inside the
    /// sphere limb (<c>hitLocal.Z &gt;= 0.25</c>). Skips anchor re-aiming near the grazing horizon
    /// and updates distance directly so continuous touch frames never drift sideways.
    /// </summary>
    private void PinchZoomGlobeAt(Vector2 pixel, float scaleFactor)
    {
        var vp = ViewportSize();
        var fov = IsInstanceValid(_camera) ? _camera.Fov : RigFieldOfView;
        var oldAlt = Mathf.Max(0.05f, _distance - GlobeRadius);
        var newDist = Mathf.Clamp(GlobeRadius + oldAlt / Math.Max(0.1f, scaleFactor), MinimumDistance, MaximumDistance);

        if (vp.X >= 2 && vp.Y >= 2 &&
            Simulation.Controls.ControlSchemeRules.TryGlobeRayHitLocal(
                pixel.X, pixel.Y, vp.X, vp.Y, fov, GlobeRadius, _distance, out var hitLocal) &&
            hitLocal.Z >= 0.25)
        {
            var anchorWorld = Simulation.Controls.ControlSchemeRules.OrbitLocalToWorld(hitLocal, _pitch, _yaw);
            if (Simulation.Controls.ControlSchemeRules.SolveGlobeZoomAnchor(
                    pixel.X, pixel.Y, vp.X, vp.Y, fov, GlobeRadius, MaxPitch,
                    anchorWorld.X, anchorWorld.Y, anchorWorld.Z,
                    _pitch, _yaw, newDist, out var nextPitch, out var nextYaw) &&
                float.IsFinite(nextPitch) && float.IsFinite(nextYaw) &&
                MathF.Abs(nextPitch - _pitch) < 0.35f &&
                MathF.Abs(Mathf.AngleDifference(_yaw, nextYaw)) < 0.35f)
            {
                _pitch = _targetPitch = nextPitch;
                _yaw = _targetYaw = nextYaw;
            }
        }
        _distance = _targetDistance = newDist;
        _globeZoomAnchorActive = false;
    }

    private void UpdateGlobeZoomAnchor()
    {
        if (_flatMap || !_globeZoomAnchorActive) return;
        var vp = ViewportSize();
        var fov = IsInstanceValid(_camera) ? _camera.Fov : RigFieldOfView;
        if (vp.X >= 2 && vp.Y >= 2 &&
            Simulation.Controls.ControlSchemeRules.SolveGlobeZoomAnchor(
                _globeZoomAnchorPixel.X, _globeZoomAnchorPixel.Y, vp.X, vp.Y, fov, GlobeRadius, MaxPitch,
                _globeZoomAnchorWorld.X, _globeZoomAnchorWorld.Y, _globeZoomAnchorWorld.Z,
                _pitch, _yaw, _distance, out var p, out var y))
        {
            _pitch = p;
            _yaw = y;
        }
        if (MathF.Abs(_distance - _targetDistance) < 0.0005f)
            _globeZoomAnchorActive = false;
    }

    /// <summary>The ordinary camera: an orbit rig the player turns, tilts and zooms.</summary>
    private void UpdateOrbitCamera(double delta)
    {
        var dt = (float)delta;
        const float showcaseRadPerSec = 0.07f;
        var showcaseFlatDelta = 0f;
        var showcaseYawDelta = 0f;
        if (_showcasePan)
        {
            // A complete, level orbit takes roughly 90 seconds in either projection.
            // This moves the camera's target, never the world's geographic coordinates;
            // country labels, unit counters and borders continue to agree with the map.
            if (_flatMap)
            {
                showcaseFlatDelta = FlatMapWidth * showcaseRadPerSec / Mathf.Tau * dt;
                _targetFlatX += showcaseFlatDelta;
            }
            else
            {
                showcaseYawDelta = showcaseRadPerSec * dt;
                _targetYaw += showcaseYawDelta;
            }
        }
        var weight = 1f - Mathf.Pow(0.0008f, dt);
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, weight);
        _pitch = Mathf.Lerp(_pitch, _targetPitch, weight);
        _distance = Mathf.Lerp(_distance, _targetDistance, weight);
        _flatX = Mathf.Lerp(_flatX, _targetFlatX, weight);
        _flatY = Mathf.Lerp(_flatY, _targetFlatY, weight);
        _flatZoom = Mathf.Lerp(_flatZoom, _targetFlatZoom, weight);
        UpdateFlatZoomAnchor();
        UpdateGlobeZoomAnchor();
        if (_flatMap) UpdateFlatGlide(dt);
        else UpdateOrbitGlide(dt);

        var keyOrEdgeMoved = ApplyKeyboardAndEdgePan(dt);
        SnapCameraLerpTails();
        ApplyCamera();
        UpdateCameraMotionState(keyOrEdgeMoved, showcaseFlatDelta, showcaseYawDelta);
    }

    private bool ApplyKeyboardAndEdgePan(float delta)
    {
        var moved = false;
        var typing = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        var cameraKeysBlocked = typing || DealOrForceGame.CameraKeyboardBlocked;
        if (_flatMap && !cameraKeysBlocked)
        {
            var up = DealOrForceGame.Held(Key.W);
            var down = DealOrForceGame.Held(Key.S);
            var left = DealOrForceGame.Held(Key.A);
            var right = DealOrForceGame.Held(Key.D);
            if (up || down || left || right)
            {
                _flatZoomAnchorActive = false;
                var flatSpeed = _flatZoom * 0.62f * delta;
                if (up) _targetFlatY += flatSpeed;
                if (down) _targetFlatY -= flatSpeed;
                if (left) _targetFlatX -= flatSpeed;
                if (right) _targetFlatX += flatSpeed;
                ClampFlatCamera();
                moved = true;
            }
        }
        else if (!_flatMap && !cameraKeysBlocked)
        {
            // The keys move the camera, not the globe.
            var up = DealOrForceGame.Held(Key.W);
            var down = DealOrForceGame.Held(Key.S);
            var left = DealOrForceGame.Held(Key.A);
            var right = DealOrForceGame.Held(Key.D);
            if (up || down || left || right)
            {
                _globeZoomAnchorActive = false;
                if (up) _targetPitch = Mathf.Clamp(_targetPitch - delta * 0.7f, -MaxPitch, MaxPitch);
                if (down) _targetPitch = Mathf.Clamp(_targetPitch + delta * 0.7f, -MaxPitch, MaxPitch);
                if (left) _targetYaw -= delta * 0.7f;
                if (right) _targetYaw += delta * 0.7f;
                moved = true;
            }
        }

        if (!LoadingInputBlocked && !_dragging && !_boxSelecting && !cameraKeysBlocked &&
            Simulation.Controls.ControlSchemeRules.EdgePanEnabled && GetWindow() is { } win && win.HasFocus())
        {
            var vp = ViewportSize();
            var localMouse = GetLocalMousePosition();
            if (localMouse.X >= 0f && localMouse.Y >= 0f && localMouse.X <= vp.X && localMouse.Y <= vp.Y)
            {
                var (edx, edy) = Simulation.Controls.ControlSchemeRules.ComputeEdgePanDelta(
                    localMouse.X, localMouse.Y, vp.X, vp.Y, deltaSec: delta);
                if (edx != 0f || edy != 0f)
                {
                    _flatZoomAnchorActive = false;
                    _globeZoomAnchorActive = false;
                    if (_flatMap)
                    {
                        var upp = MapPanScale.FlatUnitsPerPixel(_flatZoom, vp.Y);
                        _targetFlatX -= edx * upp;
                        _targetFlatY += edy * upp;
                        ClampFlatCamera();
                    }
                    else
                    {
                        var rad = MapPanScale.OrbitRadiansPerPixel(_distance, GlobeRadius, _camera.Fov, vp.Y);
                        var lonScale = Mathf.Max(0.28f, Mathf.Cos(_pitch));
                        _targetYaw -= edx * rad / lonScale;
                        _targetPitch = Mathf.Clamp(_targetPitch - edy * rad, -MaxPitch, MaxPitch);
                    }
                    moved = true;
                }
            }
        }
        return moved;
    }

    private void SnapCameraLerpTails()
    {
        if (_flatMap)
        {
            if (!_flatZoomAnchorActive)
            {
                if (MathF.Abs(_targetFlatZoom - _flatZoom) <= 1e-6f) _flatZoom = _targetFlatZoom;
                if (!_showcasePan && MathF.Abs(_targetFlatX - _flatX) <= 1e-6f) _flatX = _targetFlatX;
                if (MathF.Abs(_targetFlatY - _flatY) <= 1e-6f) _flatY = _targetFlatY;
            }
            return;
        }
        if (!_globeZoomAnchorActive)
        {
            if (MathF.Abs(_targetDistance - _distance) <= 1e-6f) _distance = _targetDistance;
            if (MathF.Abs(_targetPitch - _pitch) <= 1e-6f) _pitch = _targetPitch;
            if (!_showcasePan && MathF.Abs(Mathf.AngleDifference(_yaw, _targetYaw)) <= 1e-6f) _yaw = _targetYaw;
        }
    }

    private void UpdateCameraMotionState(bool keyOrEdgeMoved, float showcaseFlatDelta, float showcaseYawDelta)
    {
        var touchActiveMotion = _touchAdapter is
        {
            IsTouchActive: true,
            CurrentState: Platform.TouchGestureState.Panning or Platform.TouchGestureState.Pinching or Platform.TouchGestureState.PostPinchLockout
        };
        var glideActive = _flatMap
            ? _flatGlide.LengthSquared() > _flatZoom * _flatZoom * 1e-6f
            : _orbitGlide.LengthSquared() > 1e-9f;
        var anchorActive = _flatMap ? _flatZoomAnchorActive : _globeZoomAnchorActive;

        var cameraMovedMeaningfully = false;
        if (_motionFrameFlatMap == _flatMap)
        {
            if (_flatMap && float.IsFinite(_motionFrameFlatX))
            {
                var dX = MathF.Abs(_flatX - _motionFrameFlatX);
                if (dX > FlatMapWidth * 0.5f) dX = MathF.Abs(dX - FlatMapWidth);
                var dY = MathF.Abs(_flatY - _motionFrameFlatY);
                var dZoom = MathF.Abs(_flatZoom - _motionFrameFlatZoom);
                var nonShowcaseDx = _showcasePan ? MathF.Max(0f, dX - MathF.Abs(showcaseFlatDelta) - 1e-4f) : dX;
                var panFloor = MathF.Max(1e-4f, _flatZoom * 0.00025f);
                cameraMovedMeaningfully = nonShowcaseDx > panFloor || dY > panFloor || dZoom > panFloor;
            }
            else if (!_flatMap && float.IsFinite(_motionFrameYaw))
            {
                var dYaw = MathF.Abs(Mathf.AngleDifference(_motionFrameYaw, _yaw));
                var dPitch = MathF.Abs(_pitch - _motionFramePitch);
                var dDist = MathF.Abs(_distance - _motionFrameDistance);
                var nonShowcaseYaw = _showcasePan ? MathF.Max(0f, dYaw - MathF.Abs(showcaseYawDelta) - 1e-4f) : dYaw;
                cameraMovedMeaningfully = nonShowcaseYaw > 0.00025f || dPitch > 0.00025f || dDist > 0.002f;
            }
        }

        _motionFrameFlatMap = _flatMap;
        _motionFrameYaw = _yaw;
        _motionFramePitch = _pitch;
        _motionFrameDistance = _distance;
        _motionFrameFlatX = _flatX;
        _motionFrameFlatY = _flatY;
        _motionFrameFlatZoom = _flatZoom;

        _isActivelyPanning = _dragging || touchActiveMotion || glideActive || anchorActive || keyOrEdgeMoved || cameraMovedMeaningfully;
    }

    // ---- Free camera -----------------------------------------------------------------
    //
    // The console's freecam used to hide the HUD and leave the orbit rig exactly where it
    // was, so "free" meant a clean screenshot of the same locked-down view. This is the
    // real thing: the camera comes off the rig entirely and flies. Mouse looks, WASD moves
    // along the direction it is looking, Space and Ctrl climb and descend, Shift is faster
    // and Alt is slower. Nothing clamps it - it can sit on a province, fly the length of a
    // front, or pull back until the planet is a marble, which is what makes it useful for
    // showing the game to somebody rather than playing it.

    private bool _freeCamera;
    private Vector3 _freePosition;
    private float _freeYaw;
    private float _freePitch;
    private float _freeSpeed = 4f;

    // ---- what the camera is being asked to do, as opposed to what it is doing -----------
    //
    // Everything above and below is a *target*. What the viewer sees is `_shown*`, which
    // chases it. That split is the whole reason this camera can shoot usable footage: a
    // hand on a mouse produces a jittery signal, and a camera that follows it exactly
    // produces jittery film. With the chase switched off the two are the same thing and it
    // behaves as it always did.
    private float _freeRoll;
    private float _freeFov = RigFieldOfView;
    private bool _freeSmoothing = true;
    private Vector3 _shownPosition;
    private Quaternion _shownRotation = Quaternion.Identity;
    private float _shownFov = RigFieldOfView;

    /// <summary>
    /// Turntable mode: the camera hangs at a fixed distance from a point on the globe and
    /// swings around it, always looking at it.
    ///
    /// This is the shot that a flying camera cannot make. Circling something by hand means
    /// steering position and aim at once and getting both wrong; here the aim is not a thing
    /// anyone has to hold, so the only inputs left are which way round and how far out.
    /// </summary>
    private bool _freeOrbiting;
    private Vector3 _freeOrbitCentre;
    private float _freeOrbitRadius = 12f;
    private float _freeOrbitYaw;
    private float _freeOrbitPitch;

    /// <summary>Radians a second the turntable turns on its own. Zero is a still camera.</summary>
    private float _freeAutoOrbit;

    public bool FreeOrbiting => _freeOrbiting;
    public float FreeFieldOfView => _freeFov;
    public float FreeRoll => _freeRoll;
    public bool FreeSmoothing => _freeSmoothing;
    public float FreeAutoOrbit => _freeAutoOrbit;

    /// <summary>Whether the camera has come off its rig and is flying.</summary>
    public bool FreeCamera => _freeCamera;

    /// <summary>Metres per second at the current setting, for a status line.</summary>
    public float FreeCameraSpeed => _freeSpeed;

    public void SetFreeCamera(bool enabled)
    {
        if (_freeCamera == enabled || !IsInstanceValid(_camera)) return;
        _freeCamera = enabled;

        if (enabled)
        {
            // Start exactly where the player was looking, so turning it on does not
            // teleport them somewhere else.
            var transform = _camera.GlobalTransform;
            _freePosition = transform.Origin;
            var euler = transform.Basis.GetEuler();
            _freeYaw = euler.Y;
            _freePitch = Mathf.Clamp(euler.X, -1.45f, 1.45f);
            // A fresh flight every time. A roll, a lens or a turntable left over from the
            // last one is a camera that starts somewhere nobody asked it to be.
            _freeRoll = 0f;
            _freeFov = RigFieldOfView;
            _freeOrbiting = false;
            _freeAutoOrbit = 0f;
            _camera.TopLevel = true;
            Input.MouseMode = Input.MouseModeEnum.Captured;
            _isActivelyPanning = false;
            SnapFreeCamera();
        }
        else
        {
            // Land where it was flying. Without this the orbit rig snaps back to whatever
            // angle it held before take-off, which throws the player across the planet at
            // exactly the moment they wanted to look at what they had just flown to.
            var direction = _freePosition.LengthSquared() > 0.0001f
                ? _freePosition.Normalized()
                : Vector3.Back;
            _targetPitch = Mathf.Clamp(Mathf.Asin(direction.Y), -MaxPitch, MaxPitch);
            _targetYaw = Mathf.Atan2(direction.X, direction.Z);
            _pitch = _targetPitch;
            _yaw = _targetYaw;
            _targetDistance = Mathf.Clamp(_freePosition.Length(), MinimumDistance, MaximumDistance);
            _distance = _targetDistance;

            _camera.TopLevel = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            // The lens goes back with it. A camera landed at a 12-degree field of view would
            // leave the whole game zoomed into a telephoto shot with nothing to explain it.
            _camera.Fov = RigFieldOfView;
            _freeOrbiting = false;
            _freeAutoOrbit = 0f;
            _cameraTransformDirty = true;
            ApplyCamera();
        }
    }

    /// <summary>
    /// One frame of flight. Speed scales with height above the surface, so the same stick
    /// deflection crawls along a front line and crosses an ocean, which is the difference
    /// between a camera that is usable at both scales and one that is usable at neither.
    /// </summary>
    private void UpdateFreeCamera(double delta)
    {
        var step = (float)delta;
        var fast = Input.IsKeyPressed(Key.Shift) ? 4.5f : 1f;
        var slow = Input.IsKeyPressed(Key.Alt) ? 0.22f : 1f;

        // ---- the two rotations a flying camera never had -------------------------------
        // Roll, because a horizon that is allowed to tilt is the difference between footage
        // and a map screenshot; and R to put it back, because finding level again by hand
        // is the part nobody enjoys.
        if (DealOrForceGame.Held(Key.Q)) _freeRoll -= 1.1f * step * fast * slow;
        if (DealOrForceGame.Held(Key.E)) _freeRoll += 1.1f * step * fast * slow;
        if (DealOrForceGame.Held(Key.R)) _freeRoll = Mathf.MoveToward(_freeRoll, 0f, 2.2f * step);

        // A lens rather than a throttle: moving closer and zooming in look nothing alike,
        // and only one of them can be done from orbit.
        if (DealOrForceGame.Held(Key.Z)) _freeFov = Mathf.Clamp(_freeFov - 26f * step, 12f, 110f);
        if (DealOrForceGame.Held(Key.X)) _freeFov = Mathf.Clamp(_freeFov + 26f * step, 12f, 110f);
        if (DealOrForceGame.Held(Key.F)) _freeFov = Mathf.MoveToward(_freeFov, RigFieldOfView, 60f * step);

        if (_freeOrbiting) UpdateFreeOrbit(step, fast * slow);
        else UpdateFreeFlight(step, fast * slow);

        // Everything downstream sizes labels, markers and province detail by _distance.
        // Feeding it the flying camera's own height keeps the map behaving the way it
        // does under the orbit rig instead of freezing at whatever zoom it left.
        _distance = _freePosition.Length();
        _targetDistance = _distance;
        ApplyFreeCamera(step);
    }

    private void UpdateFreeFlight(float step, float scale)
    {
        var forward = -new Basis(Quaternion.FromEuler(new Vector3(_freePitch, _freeYaw, 0f))).Z;
        var right = new Basis(Quaternion.FromEuler(new Vector3(0f, _freeYaw, 0f))).X;

        var move = Vector3.Zero;
        if (DealOrForceGame.Held(Key.W)) move += forward;
        if (DealOrForceGame.Held(Key.S)) move -= forward;
        if (DealOrForceGame.Held(Key.D)) move += right;
        if (DealOrForceGame.Held(Key.A)) move -= right;
        if (Input.IsKeyPressed(Key.Space)) move += Vector3.Up;
        if (Input.IsKeyPressed(Key.Ctrl)) move -= Vector3.Up;

        var altitude = Mathf.Max(0.35f, _freePosition.Length() - GlobeRadius);
        var speed = _freeSpeed * Mathf.Clamp(altitude / 6f, 0.10f, 6f) * scale;

        if (move.LengthSquared() > 0.0001f)
            _freePosition += move.Normalized() * speed * step;
    }

    /// <summary>
    /// One frame of turntable. The keys keep their meanings as far as they can: W and S
    /// close on the subject and back away from it, A and D go round it, Space and Ctrl
    /// raise and lower the shot.
    /// </summary>
    private void UpdateFreeOrbit(float step, float scale)
    {
        _freeOrbitYaw += _freeAutoOrbit * step;

        var turn = 0.85f * step * scale;
        if (DealOrForceGame.Held(Key.A)) _freeOrbitYaw -= turn;
        if (DealOrForceGame.Held(Key.D)) _freeOrbitYaw += turn;
        if (Input.IsKeyPressed(Key.Space)) _freeOrbitPitch += turn;
        if (Input.IsKeyPressed(Key.Ctrl)) _freeOrbitPitch -= turn;
        _freeOrbitPitch = Mathf.Clamp(_freeOrbitPitch, -1.45f, 1.45f);

        var closing = _freeOrbitRadius * 0.9f * step * scale;
        if (DealOrForceGame.Held(Key.W)) _freeOrbitRadius -= closing;
        if (DealOrForceGame.Held(Key.S)) _freeOrbitRadius += closing;
        _freeOrbitRadius = Mathf.Clamp(_freeOrbitRadius, GlobeRadius * 0.06f, MaximumDistance * 2.5f);

        _freePosition = _freeOrbitCentre + OrbitOffset();
    }

    private Vector3 OrbitOffset() => new(
        _freeOrbitRadius * Mathf.Cos(_freeOrbitPitch) * Mathf.Sin(_freeOrbitYaw),
        _freeOrbitRadius * Mathf.Sin(_freeOrbitPitch),
        _freeOrbitRadius * Mathf.Cos(_freeOrbitPitch) * Mathf.Cos(_freeOrbitYaw));

    /// <summary>Where the camera is pointing, on the globe, or the nearest point to it.</summary>
    private Vector3 AimedSurfacePoint()
    {
        var forward = -new Basis(Quaternion.FromEuler(new Vector3(_freePitch, _freeYaw, 0f))).Z;
        // Ray against the sphere. The near root is the visible face; a ray that misses
        // entirely still has a closest approach, and the point of the globe beside it is a
        // far better thing to circle than nothing at all.
        var toCentre = -_freePosition;
        var along = toCentre.Dot(forward);
        var closest = _freePosition + forward * along;
        var missBy = closest.Length();
        if (missBy < GlobeRadius && along > 0f)
        {
            var half = Mathf.Sqrt(GlobeRadius * GlobeRadius - missBy * missBy);
            return _freePosition + forward * (along - half);
        }
        return (closest.LengthSquared() > 0.0001f ? closest.Normalized() : Vector3.Back) * GlobeRadius;
    }

    /// <summary>Hang the camera off the point it is looking at, or let it go again.</summary>
    public void SetFreeOrbit(bool orbiting)
    {
        if (!_freeCamera || _freeOrbiting == orbiting) return;
        _freeOrbiting = orbiting;
        if (orbiting)
        {
            _freeOrbitCentre = AimedSurfacePoint();
            var offset = _freePosition - _freeOrbitCentre;
            _freeOrbitRadius = Mathf.Max(GlobeRadius * 0.06f, offset.Length());
            _freeOrbitYaw = Mathf.Atan2(offset.X, offset.Z);
            _freeOrbitPitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(offset.Y / Mathf.Max(0.0001f, offset.Length()), -1f, 1f)),
                -1.45f, 1.45f);
            return;
        }
        // Carry the aim back into free flight, so letting go does not snap the view round.
        var direction = (_freeOrbitCentre - _freePosition).Normalized();
        _freePitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(direction.Y, -1f, 1f)), -1.45f, 1.45f);
        _freeYaw = Mathf.Atan2(-direction.X, -direction.Z);
        _freeAutoOrbit = 0f;
    }

    public void SetFreeAutoOrbit(float radiansPerSecond) => _freeAutoOrbit = radiansPerSecond;

    public void SetFreeSmoothing(bool smoothing) => _freeSmoothing = smoothing;

    /// <summary>What the camera should look like this instant, before any chasing.</summary>
    private (Vector3 Position, Quaternion Rotation, float Fov) FreeCameraTarget()
    {
        var roll = Quaternion.FromEuler(new Vector3(0f, 0f, _freeRoll));
        if (!_freeOrbiting)
            return (_freePosition, Quaternion.FromEuler(new Vector3(_freePitch, _freeYaw, 0f)) * roll, _freeFov);

        var direction = _freeOrbitCentre - _freePosition;
        if (direction.LengthSquared() < 0.0001f) direction = Vector3.Back;
        direction = direction.Normalized();
        // LookingAt is undefined when the direction and the up vector are the same line,
        // which is exactly what a shot from directly overhead is.
        var up = Mathf.Abs(direction.Dot(Vector3.Up)) > 0.999f ? Vector3.Back : Vector3.Up;
        var aim = Basis.LookingAt(direction, up).GetRotationQuaternion();
        return (_freePosition, aim * roll, _freeFov);
    }

    private void ApplyFreeCamera(float step)
    {
        if (!IsInstanceValid(_camera)) return;
        var (position, rotation, fov) = FreeCameraTarget();

        // A time constant rather than a per-frame fraction, so the feel of it does not
        // change with the frame rate - which for a camera whose whole job is footage would
        // mean the shot looking different on the machine that records it.
        var weight = _freeSmoothing ? 1f - Mathf.Pow(0.0016f, step) : 1f;
        _shownPosition = _shownPosition.Lerp(position, weight);
        _shownRotation = _shownRotation.Slerp(rotation, weight).Normalized();
        _shownFov = Mathf.Lerp(_shownFov, fov, weight);

        _camera.Projection = Camera3D.ProjectionType.Perspective;
        _camera.Fov = _shownFov;
        _camera.GlobalTransform = new Transform3D(new Basis(_shownRotation), _shownPosition);
    }

    /// <summary>Put the camera exactly where it is being asked to be, with no chase.</summary>
    private void SnapFreeCamera()
    {
        var (position, rotation, fov) = FreeCameraTarget();
        _shownPosition = position;
        _shownRotation = rotation;
        _shownFov = fov;
        ApplyFreeCamera(0f);
    }

    /// <summary>
    /// Look and throttle. Returns true when the event belonged to the free camera, so
    /// picking, box-select and orbit dragging stay out of the way while it is flying.
    /// </summary>
    private bool HandleFreeCameraInput(InputEvent inputEvent)
    {
        // Input only ever moves the target. What the viewer sees chases it a frame at a
        // time in ApplyFreeCamera, which is what turns a hand on a mouse into a shot.
        if (inputEvent is InputEventMouseMotion look && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (_freeOrbiting)
            {
                // On the turntable the mouse swings the camera around the subject rather
                // than turning its head, because the head is already pointed at it.
                _freeOrbitYaw -= look.Relative.X * FreeLookSensitivity;
                _freeOrbitPitch = Mathf.Clamp(
                    _freeOrbitPitch + look.Relative.Y * FreeLookSensitivity, -1.45f, 1.45f);
                return true;
            }
            _freeYaw -= look.Relative.X * FreeLookSensitivity;
            _freePitch = Mathf.Clamp(_freePitch - look.Relative.Y * FreeLookSensitivity, -1.45f, 1.45f);
            return true;
        }

        if (inputEvent is InputEventMouseButton wheel && wheel.Pressed &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            var step = wheel.ButtonIndex == MouseButton.WheelUp ? 1.22f : 1f / 1.22f;
            // The wheel means the same thing in both modes - how fast the shot changes -
            // and on a turntable that is the distance it is orbiting at.
            if (_freeOrbiting)
                _freeOrbitRadius = Mathf.Clamp(_freeOrbitRadius / step, GlobeRadius * 0.06f,
                    MaximumDistance * 2.5f);
            else
                _freeSpeed = Mathf.Clamp(_freeSpeed * step, 0.4f, 90f);
            return true;
        }

        return false;
    }

    private const float FreeLookSensitivity = 0.0032f;

    /// <summary>
    /// The lens the orbit rig looks through, and what a flying camera starts with.
    ///
    /// One number rather than two: a free camera that began at a different focal length
    /// would reframe the shot the instant it was switched on, which is the one thing
    /// taking the camera off its rig is supposed not to do.
    /// </summary>
    private const float RigFieldOfView = 38f;

    private void ApplyCamera()
    {
        if (!IsInstanceValid(_orbit) || !IsInstanceValid(_camera)) return;
        if (_flatMap) RebaseFlatCamera();
        var dirty = _cameraTransformDirty || _lastAppliedFlatMap != _flatMap;
        if (!dirty)
        {
            dirty = _flatMap
                ? !float.IsFinite(_lastAppliedFlatX) ||
                  MathF.Abs(_flatX - _lastAppliedFlatX) > 1e-6f ||
                  MathF.Abs(_flatY - _lastAppliedFlatY) > 1e-6f ||
                  MathF.Abs(_flatZoom - _lastAppliedFlatZoom) > 1e-6f
                : !float.IsFinite(_lastAppliedYaw) ||
                  MathF.Abs(_yaw - _lastAppliedYaw) > 1e-6f ||
                  MathF.Abs(_pitch - _lastAppliedPitch) > 1e-6f ||
                  MathF.Abs(_distance - _lastAppliedDistance) > 1e-6f;
        }
        if (!dirty) return;
        _cameraTransformDirty = false;
        _lastAppliedFlatMap = _flatMap;
        if (_flatMap)
        {
            _lastAppliedFlatX = _flatX;
            _lastAppliedFlatY = _flatY;
            _lastAppliedFlatZoom = _flatZoom;
            ApplyFlatCamera();
            return;
        }
        _lastAppliedYaw = _yaw;
        _lastAppliedPitch = _pitch;
        _lastAppliedDistance = _distance;
        _camera.Projection = Camera3D.ProjectionType.Perspective;
        _orbit.Rotation = new Vector3(_pitch, _yaw, 0f);
        _camera.Position = new Vector3(0f, 0f, _distance);
        _camera.LookAt(Vector3.Zero, Vector3.Up);
    }


}
