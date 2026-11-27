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
    private readonly List<(Vector3 Origin, Vector3 Target, float Progress, bool IsFlat)> _battleArrowSpecs = new();
    private int _battleArrowFingerprint = int.MinValue;
    private int _battleArrowZoomBucket = int.MinValue;
    private int _battleArrowFlatBucket = int.MinValue;
    private bool _battleArrowHasSurface;

    private float BattleArrowUnitsPerPixel()
    {
        if (_flatMap) return GlobeLineSanity.SafeClamp(_counterWorldPixel, 0.0001f, 0.05f, 0.0008f);
        if (!IsInstanceValid(_camera)) return 0.0008f;
        var viewportH = Math.Max(1f, GlobeLineSanity.FiniteOr(Size.Y, 720f));
        var camPos = _camera.GlobalPosition;
        var camLen = GlobeLineSanity.IsFinite(ToNum(camPos)) ? camPos.Length() : GlobeRadius + 0.25f;
        var altitude = Math.Max(0.25f, GlobeLineSanity.FiniteOr(camLen, GlobeRadius + 0.25f) - GlobeRadius);
        var halfFovRad = Mathf.DegToRad(GlobeLineSanity.SafeClamp(_camera.Fov, 5f, 160f, 45f) * 0.5f);
        var result = altitude * (2f * MathF.Tan(halfFovRad) / viewportH);
        return GlobeLineSanity.SafeClamp(result, 1e-5f, 0.1f, 0.0008f);
    }

    private int CurrentBattleArrowZoomBucket() =>
        Mathf.RoundToInt(MathF.Log2(Math.Clamp(BattleArrowUnitsPerPixel(), 1e-5f, 0.1f)) * 4f);

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
        _battleArrowFlatBucket = int.MinValue;
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
        if (_campaign?.ProvinceMap is not { } map) return;
        if (!GlobeLineSanity.TryValidateProvinceId(attackingProvinceId, map.ProvinceCount, map.ProvinceCenterCell)) return;
        if (!GlobeLineSanity.IsValidGeo(battleLocation.Latitude, battleLocation.Longitude) ||
            (battleLocation.Latitude == 0f && battleLocation.Longitude == 0f)) return;
        var originGeo = map.CenterOf(attackingProvinceId);
        if (!GlobeLineSanity.IsValidGeo(originGeo.Latitude, originGeo.Longitude)) return;

        var battlePoint = _flatMap
            ? LocalFlatPoint(battleLocation.Latitude, battleLocation.Longitude, FlatLineZ + 0.002f)
            : LocalSurfacePoint(battleLocation.Latitude, battleLocation.Longitude, SurfaceLineRadius + 0.0008f);
        var originPoint = _flatMap
            ? LocalFlatPoint(originGeo.Latitude, originGeo.Longitude, FlatLineZ + 0.0025f)
            : LocalSurfacePoint(originGeo.Latitude, originGeo.Longitude, SurfaceLineRadius + 0.001f);
        if (_flatMap)
            originPoint = originPoint with { X = DateLineSeam.NearestCopy(originPoint.X, battlePoint.X, FlatMapWidth) };
        if (!GlobeLineSanity.IsValidEmittedVertex(ToNum(originPoint), _flatMap, _flatX) ||
            !GlobeLineSanity.IsValidEmittedVertex(ToNum(battlePoint), _flatMap, _flatX))
            return;

        // Skip degenerate, invalid cross-globe, or date-line wrapping arrows.
        var dist = originPoint.DistanceTo(battlePoint);
        var maxChord = _flatMap ? GlobeLineSanity.MaxBattleArrowChordFlat : GlobeLineSanity.MaxBattleArrowChordGlobe;
        if (float.IsFinite(dist) && dist >= 0.006f && dist <= maxChord)
        {
            _battleArrowSpecs.Add((originPoint, battlePoint,
                GlobeLineSanity.SafeClamp(intensity, 0.38f, 0.98f, 0.5f), _flatMap));
        }
    }

    private void RefreshBattleVfxZoomIfNeeded()
    {
        if (!MilitaryMarkersEnabled || _battleArrowSpecs.Count == 0) return;
        var flatBucket = _flatMap ? Mathf.FloorToInt(_flatX * 0.25f) : 0;
        if (CurrentBattleArrowZoomBucket() != _battleArrowZoomBucket || flatBucket != _battleArrowFlatBucket)
            CommitBattleVfxMeshes();
    }

    private void CommitBattleVfxMeshes()
    {
        if (_battleArrowMesh is null) return;
        if (_routeMaterials[1] is null) CreateOrderMaterials();

        var zoomBucket = CurrentBattleArrowZoomBucket();
        var flatBucket = _flatMap ? Mathf.FloorToInt(_flatX * 0.25f) : 0;
        _battleArrowZoomBucket = zoomBucket;
        _battleArrowFlatBucket = flatBucket;
        var arrowHash = HashCode.Combine(_flatMap, flatBucket, _battleArrowSpecs.Count, zoomBucket);
        for (var i = 0; i < _battleArrowSpecs.Count; i++)
        {
            var spec = _battleArrowSpecs[i];
            arrowHash = HashCode.Combine(arrowHash,
                spec.IsFlat,
                BitConverter.SingleToInt32Bits(spec.Origin.X),
                BitConverter.SingleToInt32Bits(spec.Origin.Y),
                BitConverter.SingleToInt32Bits(spec.Origin.Z),
                BitConverter.SingleToInt32Bits(spec.Target.X),
                BitConverter.SingleToInt32Bits(spec.Target.Y));
            arrowHash = HashCode.Combine(arrowHash,
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
                var worldPx = GlobeLineSanity.SafeClamp(rawUnitsPerPixel, 0.00018f, 0.0016f, 0.0008f);
                var zoomScale = GlobeLineSanity.SafeClamp(rawUnitsPerPixel / 0.00058f, 0.48f, 1.0f, 0.8f);
                var maxChord = _flatMap ? GlobeLineSanity.MaxBattleArrowChordFlat : GlobeLineSanity.MaxBattleArrowChordGlobe;
                for (var i = 0; i < _battleArrowSpecs.Count; i++)
                {
                    var spec = _battleArrowSpecs[i];
                    if (spec.IsFlat != _flatMap) continue;
                    if (!GlobeLineSanity.IsValidEmittedEdge(ToNum(spec.Origin), ToNum(spec.Target), _flatMap, _flatX, maxChord))
                        continue;
                    var len = spec.Origin.DistanceTo(spec.Target);
                    if (!float.IsFinite(len) || len < 0.004f) continue;
                    var trimStart = MathF.Min(len * 0.16f, Math.Clamp(worldPx * 9f, 0.004f, 0.015f));
                    var trimEnd = MathF.Min(len * 0.18f, Math.Clamp(worldPx * 14f, 0.006f, 0.022f));
                    var start = spec.Origin;
                    var end = spec.Target;
                    if (len > trimStart + trimEnd + 0.005f)
                    {
                        var startRaw = ToGd(GlobeLineSanity.InterpolateSurfacePoint(
                            ToNum(spec.Origin), ToNum(spec.Target), trimStart / len, _flatMap));
                        var endRaw = ToGd(GlobeLineSanity.InterpolateSurfacePoint(
                            ToNum(spec.Origin), ToNum(spec.Target), 1f - trimEnd / len, _flatMap));
                        if (!TryOnOrderSurface(startRaw, SurfaceLineRadius, FlatLineZ, out start) ||
                            !TryOnOrderSurface(endRaw, SurfaceLineRadius, FlatLineZ, out end))
                            continue;
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
