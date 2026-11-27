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
    private bool _appendOffensivePhase;
    private readonly List<int> _defensiveStroke = new();
    private bool _defensiveStrokeValid;
    private Button _planEraserButton;
    private bool _erasingPlans;

    private void TogglePlanEraser()
    {
        var next = !_erasingPlans;
        EndDrawing();
        _planTransientHint = "";
        _erasingPlans = next;
        _globe.PlanEraseMode = next;
        _globe.SetBattlePlanToolActive(next);
        _globe.MouseDefaultCursorShape = next ? Control.CursorShape.Cross : Control.CursorShape.Arrow;
        _globe.PlanEraseRequested -= ErasePickedPlan;
        if (next) _globe.PlanEraseRequested += ErasePickedPlan;
        RefreshDrawingUi();
    }

    private void ErasePickedPlan(int lineId, bool objectiveOnly)
    {
        if (!_erasingPlans || _campaign?.Player is null) return;
        ApplyBattlePlanCommands(c => c.Commands.Enqueue(new DeleteBattlePlanCommand
        { IssuerId = c.Player.Id, FrontLineId = lineId, ObjectiveOnly = objectiveOnly }));
        SetArmyFeedback(Tr(objectiveOnly ? "Objective erased; departure front retained." : "Plan erased; planned movement stopped."));
        ShowSelectedArmies();
    }

    private static string DrawName(FrontLineKind kind) => Simulation.Loc.Get(kind switch
    {
        FrontLineKind.Defence => "front line",
        FrontLineKind.Offensive => "offensive line",
        FrontLineKind.AreaDefence => "area to guard",
        _ => "defensive line"
    });

    /// <summary>Start, switch or finish a stroke. The same button twice puts the brush down.</summary>
    private void ToggleDrawing(FrontLineKind kind)
    {
        if (_erasingPlans) EndDrawing();
        if (_drawingKind == kind)
        {
            if (kind == FrontLineKind.AreaDefence) CommitDrawnLine();
            else EndDrawing();
            return;
        }

        if (kind == FrontLineKind.Offensive && OffensivePlanning.Source(_campaign, SelectedPlayerArmies()) is null)
        {
            _planTransientHint = Tr("Assign the selected divisions to ONE border front first, then draw the offensive objective in that country's territory.");
            SetArmyFeedback(_planTransientHint);
            RefreshDrawingUi();
            return;
        }
        _planTransientHint = "";
        var wasDrawing = _drawingKind.HasValue;
        _drawingKind = kind;
        _borderDraft = null;
        _editingBorderId = -1;
        _offensiveStroke.Clear();
        _defensiveStroke.Clear();
        _defensiveStrokeValid = false;
        if (!wasDrawing)
        {
            _drawnProvinces.Clear();
            if (kind != FrontLineKind.Offensive) SeedDrawingFromSelection();
            _globe.ProvincePainted -= OnPlanProvincePainted;
            _globe.ProvincePainted += OnPlanProvincePainted;
        }
        _globe.PaintMode = kind == FrontLineKind.AreaDefence;
        _globe.BorderPaintMode = kind != FrontLineKind.AreaDefence;
        _globe.ObjectivePaintMode = kind is FrontLineKind.Offensive or FrontLineKind.Fallback;
        _globe.SetBattlePlanToolActive(true);
        _globe.BorderStrokeStarted -= BeginBorderStroke;
        _globe.BorderStrokeMoved -= MoveBorderStroke;
        _globe.BorderStrokeFinished -= CommitDrawnLine;
        _globe.BorderStrokeCanceled -= OnBorderStrokeCanceled;
        _globe.BorderHovered -= PreviewBorderUnderPointer;
        _borderHoverCountry = -1;
        if (kind != FrontLineKind.AreaDefence)
        {
            _drawnProvinces.Clear();
            _globe.BorderStrokeStarted += BeginBorderStroke;
            _globe.BorderStrokeMoved += MoveBorderStroke;
            _globe.BorderStrokeFinished += CommitDrawnLine;
            _globe.BorderStrokeCanceled += OnBorderStrokeCanceled;
            if (kind == FrontLineKind.Defence) _globe.BorderHovered += PreviewBorderUnderPointer;
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
        var provinces = kind == FrontLineKind.Offensive ? _offensiveStroke.ToList()
            : kind == FrontLineKind.Fallback ? _defensiveStroke.ToList() : _drawnProvinces.ToList();
        var border = _borderDraft;
        var editId = _editingBorderId;
        var appendPhase = _appendOffensivePhase;
        if (provinces.Count == 0 && armies.Count > 0 && HandleEmptyStrokeRetry(kind)) return;
        EndDrawing();

        if (provinces.Count == 0 || armies.Count == 0)
        {
            SetArmyFeedback(provinces.Count == 0
                ? "Nothing was drawn — the line was not changed."
                : "No formation of yours was selected to hold the line.");
            return;
        }

        ApplyBattlePlanCommands(c => c.Commands.Enqueue(new DrawFrontLineCommand
        {
            IssuerId = c.Player.Id, Kind = kind, Provinces = provinces, ArmyIds = armies,
            BorderNationId = border?.BorderNationId ?? -1, EditLineId = editId,
            AppendOffensivePhase = appendPhase, WholeBorder = border?.WholeBorder ?? false,
            StartLatitude = border?.StartLatitude ?? 0, StartLongitude = border?.StartLongitude ?? 0,
            EndLatitude = border?.EndLatitude ?? 0, EndLongitude = border?.EndLongitude ?? 0
        }));
        if (kind == FrontLineKind.Fallback && !SelectedPlayerArmies().Any(a =>
            BattlePlanning.LineFor(_campaign, a) is { Kind: FrontLineKind.Fallback } l && l.DefensiveSequence.SequenceEqual(provinces)))
        {
            SetArmyFeedback(Tr("Defensive line rejected: use connected controlled land. Existing orders were kept."));
            return;
        }
        if (kind == FrontLineKind.Offensive && !SelectedPlayerArmies().Any(a =>
            BattlePlanning.LineFor(_campaign, a) is { Kind: FrontLineKind.Offensive } l &&
            OffensivePlanning.PhaseGoals(l, appendPhase ? OffensivePlanning.PhaseCount(l) - 1 : l.ActiveOffensivePhase).SequenceEqual(provinces)))
        {
            SetArmyFeedback(Tr("Objective rejected: draw one connected land line in the front's target country. Existing orders were kept."));
            return;
        }
        if (kind == FrontLineKind.Defence && border is { WholeBorder: true, BorderNationId: >= 0 } &&
            _campaign.NationById(border.BorderNationId) is { } across)
        {
            // The standing front on one border: say whose, and that it will keep to it.
            SetArmyFeedback(Simulation.Loc.Format(
                "Standing front on the whole border with {0}: {1:N0} divisions on {2:N0} provinces. It follows that border as it moves and takes no other.",
                across.DisplayName, armies.Count, provinces.Count));
            ShowSelectedArmies();
            return;
        }
        SetArmyFeedback(Simulation.Loc.Format(
                                   Simulation.Plural.Key("{0:N0} formation drawn onto a {1} of {2:N0} provinces.",
                                       armies.Count),
                                   armies.Count, DrawName(kind), provinces.Count) +
                               (kind == FrontLineKind.Offensive
                                   ? " " + Tr("Press Execute when the plan is ready.") : "") +
                               (kind == FrontLineKind.AreaDefence
                                   ? " " + Tr("They will spread over the places worth holding rather than along an edge.")
                                   : ""));
        ShowSelectedArmies();
    }

    /// <summary>
    /// Put the selection on the whole front against every enemy - the "Whole front" button.
    ///
    /// No stroke to draw: the line is everywhere the country touches ground held by anybody
    /// it is at war with, and it keeps itself up to date (DrawnFrontLine.AllEnemies). A second
    /// army group pressed onto it joins the same front instead of opening another.
    /// </summary>
    private void AssignWholeFront()
    {
        EndDrawing();
        if (_campaign?.Player is null) return;
        var armies = SelectedPlayerArmies().Select(army => army.Id).Distinct().ToList();
        if (armies.Count == 0)
        {
            SetArmyFeedback(Tr("Select the divisions or the army group to put on the front first."));
            return;
        }
        ApplyBattlePlanCommands(c => c.Commands.Enqueue(new DrawFrontLineCommand
        { IssuerId = c.Player.Id, Kind = FrontLineKind.Defence, AllEnemies = true, ArmyIds = armies }));
        var line = _campaign.FrontLines.Find(candidate => candidate.NationId == _campaign.Player.Id &&
            candidate.AllEnemies && candidate.Kind == FrontLineKind.Defence);
        if (line is null || !armies.Any(line.ArmyIds.Contains))
        {
            SetArmyFeedback(Tr("No country at war with you shares a land border with you. Existing orders were kept."));
            return;
        }
        var facing = line.Provinces.Select(province => BorderFronts.EnemyAt(_campaign, _campaign.Player.Id, province))
            .Where(id => id >= 0).Distinct().Count();
        SetArmyFeedback(Simulation.Loc.Format(
            "Whole front: {0:N0} divisions on {1:N0} provinces facing {2:N0} enemy countries.",
            line.ArmyIds.Count, line.Provinces.Count, facing));
        ShowSelectedArmies();
    }

    /// <summary>Remove entire selected plans, not just the selected entries of their rosters.</summary>
    private void ClearDrawnLine()
    {
        EndDrawing();
        var armies = SelectedPlayerArmies().Select(army => army.Id).ToList();
        if (armies.Count == 0) return;
        var selected = armies.ToHashSet();
        var lineIds = _campaign.FrontLines.Where(l => l.NationId == _campaign.Player.Id &&
            (l.ArmyIds.Any(selected.Contains) || SelectedPlayerArmies().Any(a => a.FrontLineId == l.Id)))
            .Select(l => l.Id).OrderBy(id => id).ToArray();
        ApplyBattlePlanCommands(c =>
        {
            foreach (var id in lineIds) c.Commands.Enqueue(new DeleteBattlePlanCommand { IssuerId = c.Player.Id, FrontLineId = id });
            c.Commands.Enqueue(new ClearFrontLineCommand { IssuerId = c.Player.Id, ArmyIds = armies });
            foreach (var id in armies)
            {
                c.Commands.Enqueue(new DetachArmyFromPlanCommand { IssuerId = c.Player.Id, ArmyId = id });
                c.Commands.Enqueue(new HaltArmyCommand { IssuerId = c.Player.Id, ArmyId = id });
            }
        });
        SetArmyFeedback(Tr("Selected plans cleared; planned movement stopped."));
        ShowSelectedArmies();
    }

    /// <summary>Put the brush down without committing. Escape and closing the panel do this.</summary>
    private void EndDrawing()
    {
        _planTransientHint = "";
        if (_erasingPlans)
        {
            _erasingPlans = false;
            _globe.PlanEraseMode = false;
            _globe.SetBattlePlanToolActive(false);
            _globe.MouseDefaultCursorShape = Control.CursorShape.Arrow;
            _globe.PlanEraseRequested -= ErasePickedPlan;
            RefreshDrawingUi();
        }
        if (_drawingKind is null) return;
        _drawingKind = null;
        _drawnProvinces.Clear();
        _globe.PaintMode = false;
        _globe.BorderPaintMode = false;
        _globe.ObjectivePaintMode = false;
        _globe.SetBattlePlanToolActive(false);
        _offensiveStroke.Clear();
        _offensiveSource = null;
        _appendOffensivePhase = false;
        _defensiveStroke.Clear();
        _globe.BorderHovered -= PreviewBorderUnderPointer;
        _borderHoverCountry = -1;
        _globe.BorderStrokeStarted -= BeginBorderStroke;
        _globe.BorderStrokeMoved -= MoveBorderStroke;
        _globe.BorderStrokeFinished -= CommitDrawnLine;
        _globe.BorderStrokeCanceled -= OnBorderStrokeCanceled;
        _borderDraft = null;
        _editingBorderId = -1;
        _globe.ProvincePainted -= OnPlanProvincePainted;
        _globe.ClearPlanPreview();
        RefreshDrawingUi();
    }

    private void RefreshDrawingUi()
    {
        if (IsInstanceValid(_planEraserButton)) StyleButton(_planEraserButton, _erasingPlans);
        foreach (var (kind, button) in _drawButtons)
        {
            if (!IsInstanceValid(button)) continue;
            StyleButton(button, _drawingKind == kind);
        }
        FormatDrawingFeedback();
    }

    private string OffensiveStatus()
    {
        var armies = SelectedPlayerArmies().ToArray();
        var defensive = armies.Length > 0 ? BattlePlanning.LineFor(_campaign, armies[0]) : null;
        if (defensive is { Kind: FrontLineKind.Fallback } && armies.All(a => a.FrontLineId == defensive.Id))
            return Simulation.Loc.Format(IsTouchPrimaryUi ? "mobile.plan.defensive_status_fmt" : "Defence: {0:N0}/{1:N0} divisions at their posts. Defend + Alt/RMB redraws the line.",
                armies.Count(a => a.OrderProvinceId > 0 && a.ProvinceId == a.OrderProvinceId), armies.Length);
        var line = OffensivePlanning.Source(_campaign, armies);
        // On a border front and no offensive drawn from it: say which border, so the order just
        // given can be read back where the tools are and not guessed from arrows on the map.
        if ((line is not { Kind: FrontLineKind.Offensive } || line.Objectives.Count == 0) &&
            defensive is { Kind: FrontLineKind.Defence } && armies.All(a => a.FrontLineId == defensive.Id))
        {
            if (defensive.AllEnemies)
                return Simulation.Loc.Format("Whole front: every border with an enemy · {0:N0} posts.", defensive.Provinces.Count);
            if (defensive.BorderNationId >= 0 && _campaign.NationById(defensive.BorderNationId) is { } across)
                return Simulation.Loc.Format(defensive.WholeBorder
                        ? "Standing front: the whole border with {0} · {1:N0} posts · follows that border, takes no other."
                        : "Front: a stretch of the border with {0} · {1:N0} posts.",
                    across.DisplayName, defensive.Provinces.Count);
        }
        if (line is not { Kind: FrontLineKind.Offensive } || line.Objectives.Count == 0)
            return Tr(IsTouchPrimaryUi ? "mobile.plan.idle_hint" : "plan.pc.idle_hint");
        var reached = armies.Count(a => line.Objectives.GetValueOrDefault(a.Id, -1) == a.ProvinceId);
        var executing = armies.Count(a => a.PlanActive);
        var state = Tr(line.OffensiveRegroupDay >= 0 ? "Regrouping"
            : reached == armies.Length && OffensivePlanning.HasNextPhase(line) ? "Awaiting readiness"
            : reached == armies.Length ? "Objective reached"
            : executing > 0 ? "Executing" : "Prepared / paused");
        return Simulation.Loc.Format(
                   "Phase {0:N0}/{1:N0} · {2} · {3:N0}/{4:N0} divisions at objective · {5:N0} goal provinces.",
                   line.ActiveOffensivePhase + 1, OffensivePlanning.PhaseCount(line), state, reached, armies.Length, line.Provinces.Count) + " " +
               Tr(IsTouchPrimaryUi ? "mobile.plan.offensive_status_tip" : "Execute advances; Prepare stops. Attack + Shift/RMB adds a phase; Alt/RMB redraws the active one.");
    }

    private int _borderHoverCountry = -1;

    /// <summary>
    /// The front tool in hand, nothing pressed yet: light the whole border with the country under
    /// the pointer, because that is what a click will take. The order existed and nobody could
    /// find it - the bar spoke only of dragging - so the map now answers before the click.
    /// </summary>
    private void PreviewBorderUnderPointer(GlobePick pick)
    {
        if (_drawingKind != FrontLineKind.Defence || _borderDraft is not null || _campaign?.Player is null) return;
        var owner = _campaign.Player.Id;
        var foreign = BorderFronts.ForeignAt(_campaign, owner, pick.ProvinceId);
        _drawnProvinces.Clear();
        if (foreign >= 0)
            _drawnProvinces.UnionWith(BorderFronts.Resolve(_campaign, new DrawnFrontLine
            {
                NationId = owner, Kind = FrontLineKind.Defence, BorderNationId = foreign, WholeBorder = true,
                StartLatitude = pick.Latitude, StartLongitude = pick.Longitude, EndLatitude = pick.Latitude, EndLongitude = pick.Longitude
            }));
        _borderHoverCountry = _drawnProvinces.Count > 0 ? foreign : -1;
        if (_borderHoverCountry >= 0) _globe.SetBorderPlanPreview(_drawnProvinces, foreign);
        else _globe.ClearPlanPreview();
        RefreshDrawingUi();
    }

    private void BeginBorderStroke(GlobePick pick, bool whole, bool edit, bool append)
    {
        if (_drawingKind == FrontLineKind.Fallback)
        {
            _defensiveStroke.Clear(); _drawnProvinces.Clear(); _editingBorderId = -1;
            _defensiveStrokeValid = false;
            if (!DefensivePlanning.IsPost(_campaign, _campaign.Player?.Id ?? -1, pick.ProvinceId)) return;
            if (edit)
            {
                var lines = SelectedPlayerArmies().Select(a => BattlePlanning.LineFor(_campaign, a)).ToArray();
                if (lines.Length == 0 || lines.Any(l => l is null || l.Kind != FrontLineKind.Fallback || l.Id != lines[0].Id))
                {
                    SetArmyFeedback(Tr("Select divisions on one defensive line before using Alt+RMB to redraw it."));
                    return;
                }
                _editingBorderId = lines[0].Id;
            }
            _defensiveStrokeValid = true;
            MoveBorderStroke(pick);
            return;
        }
        if (_drawingKind == FrontLineKind.Offensive) { BeginOffensiveStroke(pick, edit, append); return; }
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
                SetArmyFeedback(Tr("Select divisions assigned to a border front before editing it."));
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
                SetArmyFeedback(Tr("Start on a shared land border with another country."));
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
        if (!TryLazyAnchorBorderStroke(pick)) return;
        if (_drawingKind == FrontLineKind.Fallback)
        {
            if (!_defensiveStrokeValid || !DefensivePlanning.Extend(_campaign, _campaign.Player?.Id ?? -1, _defensiveStroke, pick.ProvinceId)) return;
            _drawnProvinces.Clear(); _drawnProvinces.UnionWith(_defensiveStroke);
            _globe.SetDefensivePlanPreview(_defensiveStroke);
            RefreshDrawingUi();
            return;
        }
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
