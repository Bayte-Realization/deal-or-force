using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The share of a country's productive buildings that wear out in a year (CapitalDepreciation):
    /// the mill that closes, the field that goes back to scrub. 0 is the old world, where nothing
    /// built ever stopped working and capacity could only compound.
    /// </summary>
    public float BuildingDepreciationPerYear { get; set; } = 0.02f;
}

/// <summary>
/// Plant wears out.
///
/// Nothing built in the world ever stopped producing, and the workforce a building asks for is
/// small enough that labour never ran short, so every building put up was capacity for ever: let
/// every country build as fast as its economy could and the world grew ten per cent a year. Real
/// capital depreciates - a few per cent of the stock a year - and growth is what a country builds
/// beyond what it loses.
///
/// Once a week each region rolls, for each kind of building that makes goods, whether one of them
/// goes out of service: the chance is the stock times the yearly rate over 52, so a region with
/// fifty mills loses about one a year at two per cent. It is the same removal as pulling one down,
/// without the scrap money. The roll is a hash of the campaign seed, the region, the kind and the
/// week, so it draws nothing from the campaign's random stream and needs nothing saved. Schools,
/// hospitals and the like are not worn here: what they gave a country stays with its people.
/// </summary>
public static class CapitalDepreciation
{
    private static readonly List<Commodity> Makes = new();
    private static readonly List<Commodity> Uses = new();
    private static readonly Dictionary<BuildingKind, bool> Productive = new();

    private static bool IsProductive(BuildingKind kind)
    {
        if (Productive.TryGetValue(kind, out var known)) return known;
        ProductionChains.RolesOf(kind, Makes, Uses);
        return Productive[kind] = Makes.Count > 0;
    }

    public static void Week(Campaign campaign)
    {
        var rate = campaign?.Defines.BuildingDepreciationPerYear ?? 0f;
        if (rate <= 0f || !float.IsFinite(rate) || campaign.ElapsedDays % 7 != 0) return;
        var week = campaign.ElapsedDays / 7;
        var worn = new List<BuildingKind>();
        foreach (var state in campaign.States)
        {
            if (state.Buildings.Count == 0) continue;
            var nation = campaign.Nations.Find(n => n.MapIndex == state.NationMapIndex);
            if (nation is null) continue;
            worn.Clear();
            foreach (var (kind, count) in state.Buildings)
            {
                if (count <= 0 || !IsProductive(kind)) continue;
                var chance = count * rate / 52f;
                if (Escalation.Roll(campaign.Seed, state.MapIndex, (int)kind, week, 0xDE9) < chance) worn.Add(kind);
            }
            foreach (var kind in worn) Wear(campaign, nation, state, kind);
        }
    }

    private static void Wear(Campaign campaign, Nation nation, ScenarioState state, BuildingKind kind)
    {
        var standing = state.Buildings.GetValueOrDefault(kind);
        if (standing <= 0) return;
        var definition = BuildingCatalog.Get(kind);
        var yield = Waterfront.Yield(campaign, state.MapIndex, kind);
        if (standing == 1) state.Buildings.Remove(kind);
        else state.Buildings[kind] = standing - 1;
        nation.Gdp = MathF.Max(0.01f, nation.Gdp - definition.GdpGain * yield);
        // Never below the base it started from: a mill standing since 2024 never added to the
        // capacity, and wearing it out must not take away what it never gave.
        nation.ConstructionCapacity = MathF.Max(MathF.Min(1f, nation.ConstructionCapacity),
            nation.ConstructionCapacity - definition.CapacityGain * yield);
        campaign.Chronicle.Record(campaign, "building_worn_out", nation.Id, subject: definition.Name, place: state.Name, isPublic: false);
        campaign.MarkStrategicChange();
    }
}
