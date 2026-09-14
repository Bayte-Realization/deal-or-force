using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Drawing a battle plan.
///
/// The army panel could already tell a formation to hold the front, push it forward or
/// stand in reserve - but "the front" meant whatever provinces happened to touch the enemy
/// that day, so an order was a role rather than a plan. This is the other half: the player
/// brushes a line onto the map, and the formations they had selected are told to make that
/// line true. Border and objective tools use RMB strokes; rear-area brushes retain
/// LMB painting/RMB erasing. Objectives have a separate departure front and destination.
///
/// The brush itself is the globe's, the same one the peace conference draws borders with.
/// </summary>
public partial class DealOrForceGame
{
    private FrontLineKind? _drawingKind;
    private readonly HashSet<int> _drawnProvinces = new();
    private readonly Dictionary<FrontLineKind, Button> _drawButtons = new();
    private Label _drawFeedback;
    private DrawnFrontLine _borderDraft;
    private int _editingBorderId = -1;
    private bool _editingStart;
    private readonly List<int> _offensiveStroke = new();
    private DrawnFrontLine _offensiveSource;

    private static string DrawName(FrontLineKind kind) => kind switch
    {
        FrontLineKind.Defence => "front line",
        FrontLineKind.Offensive => "offensive line",
        FrontLineKind.AreaDefence => "area to guard",
        _ => "fallback line"
    };

    /// <summary>Start, switch or finish a stroke. The same button twice puts the brush down.</summary>
    private void ToggleDrawing(FrontLineKind kind)
    {
        if (_drawingKind == kind)
        {
            CommitDrawnLine();
            return;
        }

        if (kind == FrontLineKind.Offensive && OffensivePlanning.Source(_campaign, SelectedPlayerArmies()) is null)
        {
            SetArmyFeedback("Assign the selected divisions to ONE border front first, then draw the offensive objective in that country's territory.");
            return;
        }
        var wasDrawing = _drawingKind.HasValue;
        _drawingKind = kind;
        _borderDraft = null;
        _editingBorderId = -1;
        _offensiveStroke.Clear();
        if (!wasDrawing)
        {
            _drawnProvinces.Clear();
            if (kind != FrontLineKind.Offensive) SeedDrawingFromSelection();
            _globe.ProvincePainted -= OnPlanProvincePainted;
            _globe.ProvincePainted += OnPlanProvincePainted;
        }
        _globe.PaintMode = kind is not (FrontLineKind.Defence or FrontLineKind.Offensive);
        _globe.BorderPaintMode = kind is FrontLineKind.Defence or FrontLineKind.Offensive;
        _globe.ObjectivePaintMode = kind == FrontLineKind.Offensive;
        _globe.BorderStrokeStarted -= BeginBorderStroke;
        _globe.BorderStrokeMoved -= MoveBorderStroke;
        _globe.BorderStrokeFinished -= CommitDrawnLine;
        if (kind is FrontLineKind.Defence or FrontLineKind.Offensive)
        {
            _drawnProvinces.Clear();
            _globe.BorderStrokeStarted += BeginBorderStroke;
            _globe.BorderStrokeMoved += MoveBorderStroke;
            _globe.BorderStrokeFinished += CommitDrawnLine;
        }
        _globe.SetPlanPreview(_drawnProvinces, (int)kind);
        RefreshDrawingUi();
    }

    /// <summary>
    /// A line that starts empty is a line the player has to draw from nothing while the
    /// map is moving. Starting it from where the selected formations already stand gives
    /// them something to correct, which is how HOI4's front tool behaves too.
    /// </summary>
    private void SeedDrawingFromSelection()
    {
        foreach (var army in SelectedPlayerArmies())
        {
            var line = BattlePlanning.LineFor(_campaign, army);
            if (line is null) continue;
            foreach (var province in line.Provinces) _drawnProvinces.Add(province);
        }
    }

    private IEnumerable<Army> SelectedPlayerArmies()
    {
        var targets = _selectedArmies.Count > 0 ? _selectedArmies : new List<Army> { _selectedArmy };
        return targets.Where(army => army is not null && _campaign.Player is not null &&
                                     army.NationId == _campaign.Player.Id);
    }

    /// <summary>One stroke over one province: put it on the line, or take it off.</summary>
    private void OnPlanProvincePainted(GlobePick pick, bool adding)
    {
        if (_drawingKind is null || pick.ProvinceId <= 0) return;
        var changed = adding
            ? _drawnProvinces.Add(pick.ProvinceId)
            : _drawnProvinces.Remove(pick.ProvinceId);
        if (!changed) return;
        _globe.SetPlanPreview(_drawnProvinces, (int)_drawingKind.Value);
        RefreshDrawingUi();
    }

    /// <summary>Hand the line to the selected formations and put the brush down.</summary>
    private void CommitDrawnLine()
    {
        if (_drawingKind is null) return;
        var kind = _drawingKind.Value;
        var armies = SelectedPlayerArmies().Select(army => army.Id).ToList();
        var provinces = kind == FrontLineKind.Offensive ? _offensiveStroke.ToList() : _drawnProvinces.ToList();
        var border = _borderDraft;
        var editId = _editingBorderId;
        EndDrawing();

        if (provinces.Count == 0 || armies.Count == 0)
        {
            SetArmyFeedback(provinces.Count == 0
                ? "Nothing was drawn — the line was not changed."
                : "No formation of yours was selected to hold the line.");
            return;
        }

        _campaign.Commands.Enqueue(new DrawFrontLineCommand
        {
            IssuerId = _campaign.Player.Id,
            Kind = kind,
            Provinces = provinces,
            ArmyIds = armies,
            BorderNationId = border?.BorderNationId ?? -1,
            EditLineId = editId,
            WholeBorder = border?.WholeBorder ?? false,
            StartLatitude = border?.StartLatitude ?? 0, StartLongitude = border?.StartLongitude ?? 0,
            EndLatitude = border?.EndLatitude ?? 0, EndLongitude = border?.EndLongitude ?? 0
        });
        _campaign.Commands.ApplyAll(_campaign);
        _globe.RefreshBattlePlans();
        if (kind == FrontLineKind.Offensive && !SelectedPlayerArmies().Any(a =>
            BattlePlanning.LineFor(_campaign, a) is { Kind: FrontLineKind.Offensive } l && l.ObjectiveSequence.SequenceEqual(provinces)))
        {
            SetArmyFeedback("Objective rejected: draw one connected land line in the front's target country. Existing orders were kept.");
            return;
        }
        SetArmyFeedback($"{armies.Count:N0} formation(s) drawn onto a {DrawName(kind)} " +
                               $"of {provinces.Count:N0} province(s)." +
                               (kind == FrontLineKind.Offensive ? " Press Execute when the plan is ready." : "") +
                               (kind == FrontLineKind.AreaDefence
                                   ? " They will spread over the places worth holding rather than along an edge."
                                   : ""));
        ShowSelectedArmies();
    }

    /// <summary>Take the selected formations off whatever line they were on.</summary>
    private void ClearDrawnLine()
    {
        EndDrawing();
        var armies = SelectedPlayerArmies().Select(army => army.Id).ToList();
        if (armies.Count == 0) return;
        _campaign.Commands.Enqueue(new ClearFrontLineCommand
        {
            IssuerId = _campaign.Player.Id,
            ArmyIds = armies
        });
        _campaign.Commands.ApplyAll(_campaign);
        _globe.RefreshBattlePlans();
        SetArmyFeedback("Back on the war's own front line.");
        ShowSelectedArmies();
    }

    /// <summary>Put the brush down without committing. Escape and closing the panel do this.</summary>
    private void EndDrawing()
    {
        if (_drawingKind is null) return;
        _drawingKind = null;
        _drawnProvinces.Clear();
        _globe.PaintMode = false;
        _globe.BorderPaintMode = false;
        _globe.ObjectivePaintMode = false;
        _offensiveStroke.Clear();
        _offensiveSource = null;
        _globe.BorderStrokeStarted -= BeginBorderStroke;
        _globe.BorderStrokeMoved -= MoveBorderStroke;
        _globe.BorderStrokeFinished -= CommitDrawnLine;
        _borderDraft = null;
        _editingBorderId = -1;
        _globe.ProvincePainted -= OnPlanProvincePainted;
        _globe.ClearPlanPreview();
        RefreshDrawingUi();
    }

    private void RefreshDrawingUi()
    {
        foreach (var (kind, button) in _drawButtons)
        {
            if (!IsInstanceValid(button)) continue;
            StyleButton(button, _drawingKind == kind);
        }
        if (!IsInstanceValid(_drawFeedback)) return;
        _drawFeedback.Text = _drawingKind is null
            ? OffensiveStatus()
            : _drawingKind == FrontLineKind.Defence
                ? $"Border front: {_drawnProvinces.Count} posts · release RMB to assign. MMB pans."
            : _drawingKind == FrontLineKind.Offensive
                ? $"Objective: {_offensiveStroke.Count} provinces · RMB drag in target country, release to save · Alt+RMB redraw · Esc cancel."
                : $"Drawing a {DrawName(_drawingKind.Value)} — {_drawnProvinces.Count:N0} province(s). " +
              "Click the same button again to commit.";
        _drawFeedback.AddThemeColorOverride("font_color",
            Ui.Colour(_drawingKind is null ? "text.muted" : "text.bright"));
    }

    private string OffensiveStatus()
    {
        var armies = SelectedPlayerArmies().ToArray();
        var line = OffensivePlanning.Source(_campaign, armies);
        if (line is not { Kind: FrontLineKind.Offensive } || line.Objectives.Count == 0)
            return Tr("Front: LMB whole border / RMB section. Attack: RMB destination / Alt+RMB redraw.");
        var reached = armies.Count(a => line.Objectives.GetValueOrDefault(a.Id, -1) == a.ProvinceId);
        var executing = armies.Count(a => a.PlanActive);
        var phase = reached == armies.Length ? "Objective reached" : executing > 0 ? "Executing" : "Prepared / paused";
        return $"{phase} · {reached}/{armies.Length} divisions at objective · {line.Provinces.Count} goal provinces. " +
            "Execute advances; Prepare stops. Attack + Alt/RMB redraws the objective.";
    }

    private void BeginBorderStroke(GlobePick pick, bool whole, bool edit)
    {
        if (_drawingKind == FrontLineKind.Offensive) { BeginOffensiveStroke(pick, edit); return; }
        _drawnProvinces.Clear();
        _borderDraft = null;
        _editingBorderId = -1;
        var owner = _campaign.Player?.Id ?? -1;
        if (owner < 0 || !SelectedPlayerArmies().Any()) return;
        if (edit)
        {
            var existing = SelectedPlayerArmies().Select(a => BattlePlanning.LineFor(_campaign, a))
                .FirstOrDefault(l => l is { Kind: FrontLineKind.Defence, BorderNationId: >= 0 });
            if (existing is null)
            {
                SetArmyFeedback("Select divisions assigned to a border front before editing it.");
                return;
            }
            _editingBorderId = existing.Id;
            _editingStart = GeoDistance(pick.Latitude, pick.Longitude, existing.StartLatitude, existing.StartLongitude) <
                            GeoDistance(pick.Latitude, pick.Longitude, existing.EndLatitude, existing.EndLongitude);
            _borderDraft = new DrawnFrontLine { NationId = owner, Kind = FrontLineKind.Defence,
                BorderNationId = existing.BorderNationId, StartLatitude = existing.StartLatitude,
                StartLongitude = existing.StartLongitude, EndLatitude = existing.EndLatitude, EndLongitude = existing.EndLongitude };
        }
        else
        {
            var foreign = BorderFronts.ForeignAt(_campaign, owner, pick.ProvinceId);
            if (foreign < 0)
            {
                SetArmyFeedback("Start on a shared land border with another country.");
                _globe.ClearPlanPreview();
                return;
            }
            _editingStart = false;
            _borderDraft = new DrawnFrontLine { NationId = owner, Kind = FrontLineKind.Defence,
                BorderNationId = foreign, WholeBorder = whole, StartLatitude = pick.Latitude,
                StartLongitude = pick.Longitude, EndLatitude = pick.Latitude, EndLongitude = pick.Longitude };
        }
        MoveBorderStroke(pick);
    }

    private static float GeoDistance(float lat, float lon, float otherLat, float otherLon)
    {
        var dx = System.MathF.Abs(lon - otherLon);
        dx = System.MathF.Min(dx, 360 - dx);
        return (lat - otherLat) * (lat - otherLat) + dx * dx;
    }

    private void MoveBorderStroke(GlobePick pick)
    {
        if (_drawingKind == FrontLineKind.Offensive) { MoveOffensiveStroke(pick); return; }
        if (_borderDraft is null) return;
        if (_editingStart) { _borderDraft.StartLatitude = pick.Latitude; _borderDraft.StartLongitude = pick.Longitude; }
        else { _borderDraft.EndLatitude = pick.Latitude; _borderDraft.EndLongitude = pick.Longitude; }
        _drawnProvinces.Clear();
        _drawnProvinces.UnionWith(BorderFronts.Resolve(_campaign, _borderDraft));
        _globe.SetBorderPlanPreview(_drawnProvinces, _borderDraft.BorderNationId);
        RefreshDrawingUi();
    }
}
