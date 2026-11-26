using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private const ulong MapSelectionLifetimeMs = 1500;
    private const float CameraOriginToleranceSq = 1e-4f;
    private const float CameraBasisMinDot = 0.99995f;
    private const float CameraProjectionTolerance = 1e-4f;
    private PendingMapSelection? _pendingMapSelection;

    // A queued tap holds one priority read request on its campaign's gate, so the background
    // worker yields at the next stage boundary and stays off until the tap has been served.
    // Paired with _pendingMapSelection; released on cancel, replace, dispatch and _ExitTree.
    private bool _priorityReadHeld;
    private Campaign _priorityWorld;
    private int _priorityReadToken;

    private readonly record struct PendingMapSelection(
        Campaign World, int PlayerId, Rect2 ScreenArea, bool IsBox, bool IsFlat, bool CountryOnly,
        Transform3D CameraTransform, Projection CameraProjection, Vector2I Viewport, ulong QueuedAt);

    internal Func<bool> HasMapPlacementTool { get; set; }
    internal Func<bool> IsMapCovered { get; set; }

    internal void CancelPendingMapSelection()
    {
        _pendingMapSelection = null;
        ReleasePendingPriorityRead();
    }

    private void ReleasePendingPriorityRead()
    {
        if (!_priorityReadHeld) return;
        _priorityReadHeld = false;
        var world = _priorityWorld;
        _priorityWorld = null;
        world?.AccessGate.ClearPriorityRead();
    }

    private void DispatchPrimaryClick(Vector2 position)
    {
        CancelPendingMapSelection();
        // Placement uses each click to issue an order. A selection can instead be
        // superseded by a newer gesture while the simulation is still stepping.
        var placing = SupportRebaseArmed || HasMapPlacementTool?.Invoke() == true;
        if (!TryDispatchPrimaryClick(position, placing ? 150 : 0, priority: placing) && !placing)
            RememberMapSelection(new Rect2(position, Vector2.Zero), isBox: false);
    }

    private void DispatchBoxUnitSelection(Rect2 rect)
    {
        CancelPendingMapSelection();
        if (!TryDispatchBoxUnitSelection(rect)) RememberMapSelection(rect, isBox: true);
    }

    private bool MayRetryMapSelection() =>
        _mapApplied && IsInstanceValid(_camera) && !_freeCamera && !InterfaceHidden &&
        !PaintMode && !BorderPaintMode && !PlanEraseMode && !SupportRebaseArmed &&
        HasMapPlacementTool?.Invoke() != true && IsMapCovered?.Invoke() != true;

    private void RememberMapSelection(Rect2 rect, bool isBox)
    {
        if (_campaign is not { } world || !MayRetryMapSelection()) return;
        _pendingMapSelection = new PendingMapSelection(world, world.Player?.Id ?? -1,
            rect, isBox, _flatMap, CountrySelectionOnly,
            _camera.GetCameraTransform(), _camera.GetCameraProjection(), ViewportSize(), Time.GetTicksMsec());
        if (_priorityReadHeld && ReferenceEquals(_priorityWorld, world)) return;
        ReleasePendingPriorityRead();
        world.AccessGate.RequestPriorityRead();
        _priorityReadHeld = true;
        _priorityWorld = world;
        _priorityReadToken++;
    }

    /// <summary>
    /// Whether the camera moved enough that a queued screen coordinate no longer names the same
    /// ground. A glide of a hundredth of a unit keeps the tap; a real pan, turn or zoom drops it.
    /// </summary>
    private static bool CameraMovedMaterially(Transform3D now, Transform3D then, Projection nowProjection, Projection thenProjection)
    {
        if ((now.Origin - then.Origin).LengthSquared() > CameraOriginToleranceSq) return true;
        if (now.Basis.Column0.Normalized().Dot(then.Basis.Column0.Normalized()) < CameraBasisMinDot ||
            now.Basis.Column1.Normalized().Dot(then.Basis.Column1.Normalized()) < CameraBasisMinDot ||
            now.Basis.Column2.Normalized().Dot(then.Basis.Column2.Normalized()) < CameraBasisMinDot)
            return true;
        return ProjectionColumnMoved(nowProjection.X, thenProjection.X) ||
               ProjectionColumnMoved(nowProjection.Y, thenProjection.Y) ||
               ProjectionColumnMoved(nowProjection.Z, thenProjection.Z) ||
               ProjectionColumnMoved(nowProjection.W, thenProjection.W);
    }

    private static bool ProjectionColumnMoved(Vector4 a, Vector4 b) =>
        MathF.Abs(a.X - b.X) > CameraProjectionTolerance || MathF.Abs(a.Y - b.Y) > CameraProjectionTolerance ||
        MathF.Abs(a.Z - b.Z) > CameraProjectionTolerance || MathF.Abs(a.W - b.W) > CameraProjectionTolerance;

    private void RetryMapSelection(bool inputBlocked)
    {
        if (_pendingMapSelection is not { } pending) return;
        if (inputBlocked || Time.GetTicksMsec() - pending.QueuedAt >= MapSelectionLifetimeMs ||
            !ReferenceEquals(_campaign, pending.World) || (_campaign.Player?.Id ?? -1) != pending.PlayerId ||
            !MayRetryMapSelection() || _flatMap != pending.IsFlat || CountrySelectionOnly != pending.CountryOnly ||
            ViewportSize() != pending.Viewport ||
            CameraMovedMaterially(_camera.GetCameraTransform(), pending.CameraTransform,
                _camera.GetCameraProjection(), pending.CameraProjection))
        {
            CancelPendingMapSelection();
            return;
        }

        // The priority request stays held through the dispatch, so the panel the tap opens
        // (CountryPicked -> OpenRegion -> Refresh) reads the world before the worker resumes.
        _pendingMapSelection = null;
        var token = _priorityReadToken;
        var dispatched = pending.IsBox
            ? TryDispatchBoxUnitSelection(pending.ScreenArea, timeoutMs: 25, priority: true)
            : TryDispatchPrimaryClick(pending.ScreenArea.Position, 25, priority: true);
        if (!dispatched)
        {
            // Still queued: keep the same request held for the next frame, unless something
            // in between replaced or cancelled it.
            if (_pendingMapSelection is null && _priorityReadHeld && token == _priorityReadToken)
                _pendingMapSelection = pending;
            return;
        }
        // A handler may have queued a newer selection; release only this tap's request.
        if (token == _priorityReadToken && _pendingMapSelection is null) ReleasePendingPriorityRead();
    }
}
