using System;
using Godot;
using SourceEarthData = Globality.EarthData;

namespace DealOrForce;

/// <summary>
/// Clicking and hovering land that belongs to nobody (ColonisationCore).
///
/// A pick used to be a country: ground with no owner under it was not an answer, so the click
/// fell through to nothing and the tooltip was empty. Antarctica is nobody's now, and it has
/// to be something a player can point at - to read what settling it would cost, and to do it.
/// </summary>
public sealed partial class GlobePresentation
{
    /// <summary>A region nobody owns was clicked, and the province under the cursor.</summary>
    public event Action<Simulation.ScenarioState, int> UnclaimedPicked;

    private bool TryPickUnclaimed(Vector2 pixel, out Simulation.ScenarioState state, out int provinceId,
        bool updateSelection = true)
    {
        // Assigned before anything that can return: the guard chain below short-circuits,
        // so a caller that never reaches TryLocate still has to get a defined answer.
        provinceId = -1;
        state = null;
        if (_campaign?.ProvinceMap is not { } map ||
            !TryLocate(pixel, out _, out _, out var cell, out provinceId) ||
            provinceId <= 0 || provinceId >= map.ProvinceToState.Length ||
            _campaign.ProvinceController is not { } controllers || controllers[provinceId] != 0)
            return false;
        EnsureStateIndex();
        if (!_stateByMapIndex.TryGetValue(map.ProvinceToState[provinceId], out state) ||
            !Simulation.ColonisationCore.IsUnclaimed(state))
        {
            state = null;
            return false;
        }
        if (updateSelection)
        {
            _globeMaterial.SetShaderParameter("selected_region_id", (float)SourceEarthData.States[cell]);
            _globeMaterial.SetShaderParameter("selected_province_id", (float)provinceId);
            _globeMaterial.SetShaderParameter("selection_active", 1f);
        }
        return true;
    }

    private string UnclaimedTooltip(Vector2 atPosition)
    {
        if (!TryPickUnclaimed(atPosition, out var state, out var provinceId, updateSelection: false)) return "";
        var colonist = Simulation.ColonisationCore.Colonist(_campaign, state);
        return state.DisplayName + "\n" + Simulation.Loc.Get("Land that belongs to nobody") + "\n" +
               Simulation.Loc.Format("Province #{0:N0}", provinceId) +
               (colonist is null
                   ? "\n" + Simulation.Loc.Get("Click to see what settling it would take.")
                   : "\n" + Simulation.Loc.Format("{0} is settling it · {1:P0}", colonist.DisplayName,
                       state.ColonisationProgress)) +
               (_provinceTopology is not null ? TerrainDescription(_provinceTopology.TerrainAt(provinceId)) : "");
    }
}
