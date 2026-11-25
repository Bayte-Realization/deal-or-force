using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Batched active-battle assault arrows sharing the textured chevron-animated <see cref="RibbonBatch"/>
/// and <c>order_attack_ink_v2.png</c> shader material with <c>GlobeOrderRibbons</c>.
/// Zero per-frame CPU allocations and zero extra textures on <c>globe_map.gdshader</c>.
/// </summary>
public sealed partial class GlobePresentation
{
    private ArrayMesh _battleArrowMesh;
    private MeshInstance3D _battleArrowNode;

    private readonly RibbonBatch _battleRibbonBatch = new();
    private readonly List<(Vector3 Origin, Vector3 Target, float Progress)> _battleArrowSpecs = new();
    private int _battleArrowFingerprint = int.MinValue;
    private int _battleArrowZoomBucket = int.MinValue;
    private bool _battleArrowHasSurface;

    private float BattleArrowUnitsPerPixel()
    {
        if (_flatMap) return _counterWorldPixel;
        if (!IsInstanceValid(_camera)) return 0.0008f;
        var viewportH = Math.Max(1f, Size.Y);
        var altitude = Math.Max(0.25f, _camera.GlobalPosition.Length() - GlobeRadius);
        return altitude * (2f * MathF.Tan(Mathf.DegToRad(_camera.Fov * 0.5f)) / viewportH);
    }

    private int CurrentBattleArrowZoomBucket() =>
        Mathf.RoundToInt(MathF.Log2(Math.Max(BattleArrowUnitsPerPixel(), 1e-5f)) * 4f);

    private void ClearBattleVfx()
    {
        _battleArrowSpecs.Clear();
        _battleRibbonBatch.Clear();
        if (_battleArrowHasSurface && _battleArrowMesh is not null)
        {
            _battleArrowMesh.ClearSurfaces();
            _battleArrowHasSurface = false;
        }
        _battleArrowFingerprint = int.MinValue;
        _battleArrowZoomBucket = int.MinValue;
        if (IsInstanceValid(_battleArrowNode)) _battleArrowNode.Visible = false;
    }

    private void CreateBattleVfx(Node3D world)
    {
        if (_routeMaterials[1] is null) CreateOrderMaterials();
        _battleArrowMesh = new ArrayMesh();
        _battleArrowNode = new MeshInstance3D
        {
            Name = "ActiveBattleAssaultArrows",
            Mesh = _battleArrowMesh,
            MaterialOverride = null,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false
        };
        world.AddChild(_battleArrowNode);
    }

    private void ApplyBattleVfxQualityTier(float tier)
    {
        foreach (var marker in _battleMarkers.Values)
            if (IsInstanceValid(marker.Node))
                marker.Node.GetNodeOrNull<SurfaceBattleBadge>("SurfaceBadge")?.SetQualityTier(tier);
    }

    private void BeginBattleVfxFrame()
    {
        _battleArrowSpecs.Clear();
    }

    private void RecordBattleAssaultArrow(
        (float Latitude, float Longitude) battleLocation,
        int attackingProvinceId,
        float intensity)
    {
        if (attackingProvinceId <= 0 || _campaign?.ProvinceMap is not { } map) return;
        var battlePoint = _flatMap
            ? LocalFlatPoint(battleLocation.Latitude, battleLocation.Longitude, FlatLineZ + 0.002f)
            : LocalSurfacePoint(battleLocation.Latitude, battleLocation.Longitude, SurfaceLineRadius + 0.0008f);
        var originGeo = map.CenterOf(attackingProvinceId);
        var originPoint = _flatMap
            ? LocalFlatPoint(originGeo.Latitude, originGeo.Longitude, FlatLineZ + 0.0025f)
            : LocalSurfacePoint(originGeo.Latitude, originGeo.Longitude, SurfaceLineRadius + 0.001f);
        // Skip degenerate or date-line wrapping arrows on flat projection.
        var dist = originPoint.DistanceTo(battlePoint);
        if (dist >= 0.006f && (!_flatMap || dist < FlatMapWidth * 0.25f))
            _battleArrowSpecs.Add((originPoint, battlePoint, Math.Clamp(intensity, 0.38f, 0.98f)));
    }

    private void RefreshBattleVfxZoomIfNeeded()
    {
        if (!MilitaryMarkersEnabled || _battleArrowSpecs.Count == 0) return;
        if (CurrentBattleArrowZoomBucket() != _battleArrowZoomBucket)
            CommitBattleVfxMeshes();
    }

    private void CommitBattleVfxMeshes()
    {
        if (_battleArrowMesh is null) return;
        if (_routeMaterials[1] is null) CreateOrderMaterials();

        var zoomBucket = CurrentBattleArrowZoomBucket();
        _battleArrowZoomBucket = zoomBucket;
        var arrowHash = HashCode.Combine(_flatMap, _battleArrowSpecs.Count, zoomBucket);
        for (var i = 0; i < _battleArrowSpecs.Count; i++)
        {
            var spec = _battleArrowSpecs[i];
            arrowHash = HashCode.Combine(arrowHash,
                BitConverter.SingleToInt32Bits(spec.Origin.X),
                BitConverter.SingleToInt32Bits(spec.Origin.Y),
                BitConverter.SingleToInt32Bits(spec.Origin.Z),
                BitConverter.SingleToInt32Bits(spec.Target.X),
                BitConverter.SingleToInt32Bits(spec.Target.Y),
                BitConverter.SingleToInt32Bits(spec.Target.Z),
                BitConverter.SingleToInt32Bits(spec.Progress));
        }
        if (arrowHash != _battleArrowFingerprint)
        {
            if (_battleArrowSpecs.Count > 0)
            {
                _battleArrowFingerprint = arrowHash;
                _battleRibbonBatch.Clear();
                var rawUnitsPerPixel = BattleArrowUnitsPerPixel();
                var worldPx = Math.Clamp(rawUnitsPerPixel, 0.00018f, 0.0016f);
                var zoomScale = Math.Clamp(rawUnitsPerPixel / 0.00058f, 0.48f, 1.0f);
                for (var i = 0; i < _battleArrowSpecs.Count; i++)
                {
                    var spec = _battleArrowSpecs[i];
                    var len = spec.Origin.DistanceTo(spec.Target);
                    if (len < 0.004f) continue;
                    var trimStart = MathF.Min(len * 0.16f, Math.Clamp(worldPx * 9f, 0.004f, 0.015f));
                    var trimEnd = MathF.Min(len * 0.18f, Math.Clamp(worldPx * 14f, 0.006f, 0.022f));
                    var start = spec.Origin;
                    var end = spec.Target;
                    if (len > trimStart + trimEnd + 0.005f)
                    {
                        start = OnOrderSurface(spec.Origin.Lerp(spec.Target, trimStart / len));
                        end = OnOrderSurface(spec.Origin.Lerp(spec.Target, 1f - trimEnd / len));
                    }
                    DrawStraightRibbon(_battleRibbonBatch, 1, start, end, spec.Progress, zoomScale);
                }
                _battleArrowHasSurface = _battleRibbonBatch.Commit(
                    _battleArrowMesh, _routeMaterials[0], _routeMaterials[1], _routeMaterials[2]);
            }
            else
            {
                ClearBattleVfx();
                _battleArrowFingerprint = arrowHash;
            }
        }
    }

    private void FinishBattleVfxVisibility()
    {
        var show = MilitaryMarkersEnabled && ViewDistance <= 14f;
        if (IsInstanceValid(_battleArrowNode))
            _battleArrowNode.Visible = show && _battleArrowHasSurface;
    }
}
