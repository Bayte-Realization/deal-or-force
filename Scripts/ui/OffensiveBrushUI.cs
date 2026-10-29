using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void BeginOffensiveStroke(GlobePick pick, bool edit, bool append)
    {
        _offensiveStroke.Clear(); _drawnProvinces.Clear(); _editingBorderId = -1;
        _appendOffensivePhase = append;
        _offensiveSource = OffensivePlanning.Source(_campaign, SelectedPlayerArmies());
        if (_offensiveSource is null) return;
        if (append && (_offensiveSource.Kind != FrontLineKind.Offensive || OffensivePlanning.PhaseCount(_offensiveSource) >= OffensivePlanning.MaxPhases))
        {
            _offensiveSource = null;
            SetArmyFeedback(Tr("Shift+RMB adds a phase to an existing offensive with fewer than 24 phases."));
            return;
        }
        if (edit || append)
        {
            if (_offensiveSource.Kind != FrontLineKind.Offensive)
            {
                _offensiveSource = null;
                SetArmyFeedback(Tr("Alt+RMB edits an existing offensive objective. Use RMB to create one."));
                return;
            }
            _editingBorderId = _offensiveSource.Id;
        }
        MoveOffensiveStroke(pick);
    }

    private void MoveOffensiveStroke(GlobePick pick)
    {
        var map = _campaign.ProvinceMap;
        var enemy = _campaign.NationById(_offensiveSource?.BorderNationId ?? -1);
        var control = _campaign.ProvinceController ?? map?.ProvinceToNation;
        var p = pick.ProvinceId;
        if (map is null || enemy is null || p <= 0 || p > map.ProvinceCount || map.IsWater(p) || control[p] != enemy.MapIndex) return;
        if (_offensiveStroke.Count == 0) _offensiveStroke.Add(p);
        else
        {
            var index = _offensiveStroke.IndexOf(p);
            if (index >= 0) _offensiveStroke.RemoveRange(index + 1, _offensiveStroke.Count - index - 1);
            else
            {
                // Fill skipped mouse samples through neighbouring land, never a straight sea shortcut.
                var segment = map.FindRoute(_offensiveStroke[^1], p, (ushort)enemy.MapIndex, control);
                if (segment.Length == 0 || segment.Length > 16 || _offensiveStroke.Count + segment.Length > 128) return;
                foreach (var next in segment)
                {
                    var previous = _offensiveStroke.IndexOf(next);
                    if (previous >= 0) _offensiveStroke.RemoveRange(previous + 1, _offensiveStroke.Count - previous - 1);
                    else _offensiveStroke.Add(next);
                }
            }
        }
        _drawnProvinces.Clear(); _drawnProvinces.UnionWith(_offensiveStroke);
        System.Collections.Generic.IReadOnlyCollection<int> departure = _appendOffensivePhase
            ? OffensivePlanning.PhaseGoals(_offensiveSource, OffensivePlanning.PhaseCount(_offensiveSource) - 1).ToArray()
            : _offensiveSource.Kind == FrontLineKind.Defence ? _offensiveSource.Provinces : _offensiveSource.SourceProvinces;
        _globe.SetOffensivePlanPreview(_offensiveStroke, departure);
        RefreshDrawingUi();
    }
}
