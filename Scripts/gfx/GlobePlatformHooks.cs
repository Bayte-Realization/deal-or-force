using System.Collections.Generic;
using DealOrForce.Platform;
using Godot;

namespace DealOrForce;

/// <summary>
/// Platform graphics tier propagation (DesktopHigh / MobileMedium / MobileLow) and
/// multi-touch camera hookup bridging <see cref="PlatformProfile"/> and <see cref="TouchCameraAdapter"/>
/// into <see cref="GlobePresentation"/> with strictly zero impact on desktop mouse/keyboard controls.
/// </summary>
public sealed partial class GlobePresentation
{
    private TouchCameraAdapter _touchAdapter;
    private float _cachedShaderQualityTier = -1f;

    /// <summary>Live multi-touch camera adapter (consumed by mobile Touch-Mode HUD controls).</summary>
    internal TouchCameraAdapter TouchAdapter => _touchAdapter;

    /// <summary>
    /// Map the active platform and runtime quality profile to the uniform <c>quality_tier</c>
    /// contract shared by <c>globe_map.gdshader</c>, <c>CloudShader</c>, <c>MistShader</c>, and
    /// <c>AtmosphereShader</c>: <c>0 = DesktopHigh</c>, <c>1 = MobileMedium</c>, <c>2 = MobileLow</c>.
    /// </summary>
    private float CurrentShaderQualityTier()
    {
        var active = PlatformProfile.Current ?? PlatformProfile.DesktopDefault;
        var platformTier = active?.GraphicsQualityTier ?? PlatformGraphicsTier.DesktopHigh;
        if (platformTier == PlatformGraphicsTier.MobileLow) return 2f;
        if (platformTier == PlatformGraphicsTier.MobileMedium)
        {
            if (_quality is not null && (_quality.Order >= 2 || !_quality.MapSmoothing || _quality.RenderScale <= 0.65f))
                return 2f;
            return 1f;
        }
        if (_quality is not null)
        {
            if (_quality.Order >= 2 || !_quality.MapSmoothing || _quality.RenderScale <= 0.65f)
                return 2f;
            if (_quality.Order == 1 || _quality.RenderScale < 0.95f || !_quality.SunShadows)
                return 1f;
        }
        return 0f;
    }

    /// <summary>Push <c>quality_tier</c> once to all map/weather/atmosphere materials when it changes.</summary>
    private void ApplyShaderQualityTier(bool force = false)
    {
        var tier = CurrentShaderQualityTier();
        var compat = RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility" ? 1f : 0f;
        if (!force && Mathf.IsEqualApprox(tier, _cachedShaderQualityTier)) return;
        _cachedShaderQualityTier = tier;
        _globeMaterial?.SetShaderParameter("quality_tier", tier);
        _globeMaterial?.SetShaderParameter("compat_renderer", compat);
        if (IsInstanceValid(_atmosphereLayer) && _atmosphereLayer.MaterialOverride is ShaderMaterial atmo)
            atmo.SetShaderParameter("quality_tier", tier);
        ApplyWeatherQualityTier(tier);
        ApplyBattleVfxQualityTier(tier);
        ApplyTradeQualityTier(tier);
        ApplySupplyLineQualityTier(tier);
        ApplyEventPulseQualityTier(tier);
        ApplyCapitalAndUrbanQualityTier(tier);
    }

    private void CreateTouchCameraHooks()
    {
        var logicalH = Size.Y > 1f ? Size.Y : 1080f;
        var profile = GodotPlatformBridge.EnsureInitialized(logicalH);
        _touchAdapter = new TouchCameraAdapter(profile);
        _touchAdapter.PanRequested += delta =>
        {
            _globeZoomAnchorActive = false;
            _flatZoomAnchorActive = false;
            DragCamera(delta);
        };
        _touchAdapter.ZoomRequested += (anchor, direction, _) =>
        {
            _flatGlide = Vector2.Zero;
            _orbitGlide = Vector2.Zero;
            if (_flatMap) ZoomFlatMapAt(anchor, direction, fine: false);
            else ZoomGlobeAt(anchor, direction, fine: false);
        };
        _touchAdapter.TiltRotateRequested += (tiltPx, rotRad) =>
        {
            if (_flatMap || !IsInstanceValid(_camera)) return;
            _globeZoomAnchorActive = false;
            var radians = MapPanScale.OrbitRadiansPerPixel(_distance, GlobeRadius, _camera.Fov, ViewportSize().Y);
            _targetPitch = Mathf.Clamp(_targetPitch - tiltPx * radians, -MaxPitch, MaxPitch);
            _targetYaw += rotRad;
            _pitch = _targetPitch;
            _yaw = _targetYaw;
        };
        _touchAdapter.TapRequested += DispatchPrimaryClick;
        _touchAdapter.LongPressContextRequested += pos =>
        {
            if (PlanEraseMode || CountrySelectionOnly || !TryBeginActionRead(out var campaign, 150)) return;
            GlobePick commanded = default;
            var picked = false;
            try { picked = TryPick(pos, out commanded, updateSelection: false); }
            finally { campaign.ExitRead(); }
            if (picked) ProvinceCommanded?.Invoke(commanded);
        };
        _touchAdapter.BoxSelectUpdated += rect =>
        {
            _boxSelecting = true;
            _boxRegions = false;
            _dragging = false;
            _flatGlide = Vector2.Zero;
            _orbitGlide = Vector2.Zero;
            _boxStart = rect.Position;
            _boxCurrent = rect.End;
            QueueRedraw();
        };
        _touchAdapter.BoxSelectCompleted += rect =>
        {
            _boxSelecting = false;
            _boxRegions = false;
            _dragging = false;
            if (!CountrySelectionOnly) DispatchBoxUnitSelection(rect);
            QueueRedraw();
        };
        _touchAdapter.BoxSelectCanceled += () =>
        {
            _boxSelecting = false;
            _boxRegions = false;
            _dragging = false;
            QueueRedraw();
        };
    }

    private bool HandleTouchInput(InputEvent inputEvent) =>
        _touchAdapter is not null && _touchAdapter.TryHandleInput(inputEvent);

    private void TickTouchCamera(double delta, bool blocked)
    {
        if (_touchAdapter is null) return;
        if (blocked) _touchAdapter.Cancel();
        else _touchAdapter.Tick(delta);
    }

    private void DispatchPrimaryClick(Vector2 position)
    {
        if (!TryBeginActionRead(out var campaign, 150)) return;
        // Materialize pick under read lock and invoke UI signals AFTER releasing the read fence
        // so UI click handlers that acquire write locks for ICampaignCommand never self-deadlock!
        Simulation.Nation movement = null;
        GlobePick? pickedCountry = null;
        Simulation.LandBattle pickedBattle = null;
        IReadOnlyList<Simulation.Army> pickedStack = null;
        Simulation.Fleet pickedFleet = null;
        Simulation.SupportFormation pickedSupport = null;
        Simulation.ScenarioState pickedUnclaimed = null;
        int pickedUnclaimedProvince = -1;
        bool clickedEmpty = false;
        try
        {
            if (!CountrySelectionOnly && _movementLayer is not null && _movementLayer.TryPickAt(position, out movement))
            { }
            else if (CountrySelectionOnly)
            {
                if (TryPick(position, out var country)) pickedCountry = country;
            }
            else if (SupportRebaseArmed)
            {
                // A strike target is being named in the long-range panel: the click is its.
                if (TryPick(position, out var destination)) pickedCountry = destination;
            }
            else if (TryPickBattle(position, out pickedBattle)) { }
            else if (TryPickArmyStack(position, out pickedStack)) { }
            else if (TryPickFleet(position, out pickedFleet)) { }
            else if (TryPickSupport(position, out pickedSupport)) { }
            else if (TryPick(position, out var pick)) pickedCountry = pick;
            else if (TryPickUnclaimed(position, out pickedUnclaimed, out pickedUnclaimedProvince)) { }
            else clickedEmpty = true;
        }
        finally
        {
            campaign.ExitRead();
        }

        if (movement is not null) MovementPicked?.Invoke(movement);
        else if (pickedCountry is { } country) CountryPicked?.Invoke(country);
        else if (pickedBattle is not null) BattlePicked?.Invoke(pickedBattle);
        else if (pickedStack is not null)
        {
            if (pickedStack.Count == 1) ArmyPicked?.Invoke(pickedStack[0]);
            else if (pickedStack.Count > 1) ArmiesBoxPicked?.Invoke(pickedStack);
        }
        else if (pickedFleet is not null) FleetPicked?.Invoke(pickedFleet);
        else if (pickedSupport is not null) SupportPicked?.Invoke(pickedSupport);
        else if (pickedUnclaimed is not null) UnclaimedPicked?.Invoke(pickedUnclaimed, pickedUnclaimedProvince);
        else if (clickedEmpty) EmptyMapClicked?.Invoke();
    }
}
