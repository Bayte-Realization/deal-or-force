using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    public bool PlanEraseMode { get; set; }
    public event Action<int, bool> PlanEraseRequested;
    private readonly List<(int Id, bool Objective, Vector3 A, Vector3 B)> _planHitSegments = new();
    private int _drawingPlanId = -1;
    private bool _drawingPlanObjective;

    private bool TryPickPlan(Vector2 pixel, out int id, out bool objective)
    {
        id = -1; objective = false;
        if (_campaign?.Player is null || !IsInstanceValid(_camera)) return false;
        var best = 14f;
        // The same sampled geometry that was drawn; no second approximation of arrows.
        foreach (var segment in _planHitSegments)
        {
            if (_camera.IsPositionBehind(segment.A) || _camera.IsPositionBehind(segment.B)) continue;
            if (!_flatMap && segment.A.Normalized().Dot(_camera.GlobalPosition.Normalized()) <= 0f) continue;
            var a = _camera.UnprojectPosition(segment.A); var b = _camera.UnprojectPosition(segment.B);
            var edge = b - a;
            var t = edge.LengthSquared() < .001f ? 0f : Mathf.Clamp((pixel - a).Dot(edge) / edge.LengthSquared(), 0f, 1f);
            var distance = pixel.DistanceTo(a + edge * t);
            if (distance >= best) continue;
            best = distance; id = segment.Id; objective = segment.Objective;
        }
        if (id >= 0) return true;
        // Border/area ink lives in the province shader rather than a ribbon mesh.
        if (!TryPick(pixel, out var pick, updateSelection: false)) return false;
        var line = _campaign.FrontLines.Where(l => l.NationId == _campaign.Player.Id &&
            (l.Kind == Simulation.FrontLineKind.Offensive ? l.SourceProvinces : l.Provinces).Contains(pick.ProvinceId))
            .OrderBy(l => l.Id).FirstOrDefault();
        if (line is null) return false;
        id = line.Id; return true;
    }
}
