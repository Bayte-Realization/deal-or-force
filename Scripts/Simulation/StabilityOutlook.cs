using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A read-only explanation of the existing stability rule, not a second cycle model
/// or a forecast that invents future events. Re-evaluate when actual conditions change.</summary>
public sealed record StabilityOutlook(float Current, float Target, float NextReviewChange, Breakdown Drivers)
{
    /// <summary>
    /// What the imperial overstretch is weighed from, so the line can say what the figure is: the
    /// weight of the regions that strain the state, and the capacity it bears for free. Empty when
    /// the country is not overstretched.
    /// </summary>
    public string OverstretchNote { get; init; } = "";

    public string Direction => NextReviewChange > .0005f ? "Conditions favour recovery" :
        NextReviewChange < -.0005f ? "Pressure is building" : "Holding steady";
    public string Tone => NextReviewChange > .0005f ? "state.positive" :
        NextReviewChange < -.0005f ? "state.warning" : "text.accent";
    public Breakdown.Entry? MainDrag => Drivers.Entries
        .Where(e => e.Value < -.005f && e.Source != "already as settled as a country gets" &&
            e.Source != "rounding").OrderBy(e => e.Value).Select(e => (Breakdown.Entry?)e).FirstOrDefault();

    public static StabilityOutlook Read(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !float.IsFinite(nation.Stability) || !campaign.HasOwnedLand(nation)) return null;
        var drivers = new Breakdown();
        var target = StabilityCore.Preview(campaign, nation, drivers);
        if (!float.IsFinite(target)) return null;
        var change = (target - nation.Stability) * M.Clamp01(StabilityCore.Weights.Approach);
        var note = StabilityCore.Overstretch(campaign, nation) > 0f
            ? Loc.Format("Unaccepted regions weigh {0:0.0} against a capacity of {1}; autonomy and acceptance lower the weight.",
                StabilityCore.StrainedWeight(campaign, nation), campaign.Defines.BaseImperialCapacity)
            : "";
        return new(nation.Stability, target, change, drivers) { OverstretchNote = note };
    }
}
