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
/// line true. Left button paints, right button rubs out, and the map shows the line while
/// it is being drawn rather than after it is committed.
///
/// The brush itself is the globe's, the same one the peace conference draws borders with.
/// </summary>
public partial class DealOrForceGame
{
    private FrontLineKind? _drawingKind;
    private readonly HashSet<int> _drawnProvinces = new();
    private readonly Dictionary<FrontLineKind, Button> _drawButtons = new();
    private Label _drawFeedback;

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

        // Switching kind mid-stroke keeps what is already painted: a player who started a
        // defensive line and decided it was an offensive one means the same provinces.
        var wasDrawing = _drawingKind.HasValue;
        _drawingKind = kind;
        if (!wasDrawing)
        {
            _drawnProvinces.Clear();
            SeedDrawingFromSelection();
            _globe.PaintMode = true;
            _globe.ProvincePainted -= OnPlanProvincePainted;
            _globe.ProvincePainted += OnPlanProvincePainted;
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
        var provinces = _drawnProvinces.ToList();
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
            ArmyIds = armies
        });
        if (kind == FrontLineKind.Offensive)
            foreach (var id in armies)
                _campaign.Commands.Enqueue(new SetArmyPlanExecutionCommand
                {
                    IssuerId = _campaign.Player.Id,
                    ArmyId = id,
                    Active = false
                });
        _campaign.Commands.ApplyAll(_campaign);
        _globe.RefreshBattlePlans();
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
            ? "Pick a line, then drag along the map. Right button rubs out."
            : $"Drawing a {DrawName(_drawingKind.Value)} — {_drawnProvinces.Count:N0} province(s). " +
              "Click the same button again to commit.";
        _drawFeedback.AddThemeColorOverride("font_color",
            Ui.Colour(_drawingKind is null ? "text.muted" : "text.bright"));
    }
}
