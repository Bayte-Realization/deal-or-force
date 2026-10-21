using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // View state belongs to a movement in this campaign, not to a rebuilt Control,
    // translated heading, save or network fingerprint. Discard it on world adoption.
    private void RememberMovementViewport()
    {
        if (!ReferenceEquals(_movementViewCampaign, _campaign))
        {
            _movementViewCampaign = _campaign;
            _movementExpanded.Clear();
            _movementScrollPositions.Clear();
            _movementViewNation = -1;
            _movementBuiltFrom = "";
            _movementViewRevision++;
            _movementScrollRestorePending = false;
        }
        if (_movementViewNation >= 0 && _movementScroll is not null && !_movementScrollRestorePending)
            _movementScrollPositions[_movementViewNation] = _movementScroll.ScrollVertical;
    }

    private HashSet<string> MovementExpandedSections(int nationId)
    {
        if (!_movementExpanded.TryGetValue(nationId, out var expanded))
            _movementExpanded[nationId] = expanded = new HashSet<string>();
        return expanded;
    }

    private void RestoreMovementViewport(int nationId)
    {
        _movementViewNation = nationId;
        var revision = ++_movementViewRevision;
        var position = _movementScrollPositions.GetValueOrDefault(nationId);
        _movementScrollRestorePending = true;
        // Container layout runs deferred too. Never apply an older rebuild's scroll
        // restoration to a different movement or to a newly adopted campaign.
        Callable.From(() =>
        {
            if (revision != _movementViewRevision || !GodotObject.IsInstanceValid(_movementScroll)) return;
            _movementScroll.ScrollVertical = position;
            _movementScrollRestorePending = false;
        }).CallDeferred();
    }
}

