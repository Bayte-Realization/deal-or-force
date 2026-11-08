using System;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Whether a building costs what building costs in that country (ConstructionCost). Off,
    /// every catalogue price is the same number of billions everywhere, as it was before.
    /// </summary>
    public bool ConstructionPriceLevelEnabled { get; set; } = true;
    /// <summary>The cheapest and dearest a country's price level can make a building, against the world's.</summary>
    public float ConstructionPriceFloor { get; set; } = 0.25f;
    public float ConstructionPriceCeiling { get; set; } = 4f;

    /// <summary>
    /// The weekly output (Nation.GrossOutput) one economy's worth of building is sized for. A
    /// country this size runs the catalogue's project limits; one ten times larger runs ten times
    /// as many, up to ConstructionSizeCap.
    /// </summary>
    public float ConstructionReferenceOutput { get; set; } = 12f;
    public float ConstructionSizeCap { get; set; } = 12f;

    /// <summary>
    /// Whether every open site works each day (true) or only a country's oldest one, the queue
    /// the construction system ran until 28 October 2026.
    /// </summary>
    public bool ConstructionSitesInParallel { get; set; } = true;

    /// <summary>
    /// How many sites work at once, per economy of the reference size (ConstructionCost.Size); at
    /// least one. All of them at once made the world grow ten per cent a year - nothing wears
    /// out and labour never runs short, so capacity compounded without limit.
    /// </summary>
    public float ConstructionParallelPerEconomy { get; set; } = 0.7f;

    /// <summary>
    /// The fastest a country's construction capacity can make its sites work, as days of work
    /// done per calendar day (ConstructionCost.Pace). 1.5 means a country with all the capacity in
    /// the world finishes a 240-day factory in 160 days, and never sooner. 1 turns the bonus off
    /// and gives back the rule before 1 November 2026, when capacity above one did nothing.
    /// </summary>
    public float ConstructionPaceCeiling { get; set; } = 1.5f;

    /// <summary>
    /// How much capacity above one buys half of the way to ConstructionPaceCeiling. At 1, a country
    /// with capacity 2 builds at 1.25 days a day, one with capacity 3 at about 1.33: every point of
    /// capacity is worth less than the one before it, so the stat stays worth having without ever
    /// letting a country with a long building history pour concrete at three times the speed.
    /// </summary>
    public float ConstructionPaceHalfway { get; set; } = 1f;
}

/// <summary>
/// What a building costs in a country, and how much building a country can run at once.
///
/// A catalogue price was the same number of billions in Delhi as in Milan, and every country,
/// China or Italy, ran the same four projects at a time. Together those made a small rich country
/// the fastest-growing economy on earth: a factory makes the same goods in both, but Italy pays a
/// smaller share of its GDP for it and, with a twentieth of China's industry, its four projects a
/// year are a fifth of everything it has, where China's are a rounding error. Over ten years Italy's
/// real output grew by half and China's by a twentieth.
///
/// Now a building is priced at the country's price level - its GDP per unit of real output against
/// the world's, so labour and land cost what they cost there - and a country runs projects in
/// proportion to the size of its economy. A share of GDP spent on building buys the same share of
/// new capacity anywhere; how fast a country grows is decided by how much it chooses to spend.
/// </summary>
public static class ConstructionCost
{
    private sealed class Frame
    {
        internal int Day = int.MinValue;
        internal float WorldScale = 1f;
    }
    private static readonly ConditionalWeakTable<Campaign, Frame> Frames = new();

    /// <summary>The world's GDP per unit of weekly output, annualised, read once a day.</summary>
    private static float WorldScale(Campaign campaign)
    {
        var frame = Frames.GetOrCreateValue(campaign);
        if (frame.Day == campaign.ElapsedDays) return frame.WorldScale;
        frame.Day = campaign.ElapsedDays;
        double gdp = 0, output = 0;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0 || nation.GrossOutput <= 0f || nation.Gdp <= 0f) continue;
            gdp += nation.Gdp;
            output += nation.GrossOutput * 52d;
        }
        frame.WorldScale = output > 0 ? (float)(gdp / output) : 1f;
        return frame.WorldScale;
    }

    /// <summary>How dear building is in this country against the world: 1 is the world's average.</summary>
    public static float PriceLevel(Campaign campaign, Nation nation)
    {
        var defines = campaign?.Defines;
        if (defines is null || nation is null || !defines.ConstructionPriceLevelEnabled) return 1f;
        if (nation.GrossOutput <= 0f || nation.Gdp <= 0f) return 1f;
        var world = WorldScale(campaign);
        if (world <= 0f || !float.IsFinite(world)) return 1f;
        var own = nation.Gdp / (nation.GrossOutput * 52f);
        var level = own / world;
        if (!float.IsFinite(level)) return 1f;
        return M.Clamp(level, defines.ConstructionPriceFloor, MathF.Max(defines.ConstructionPriceFloor, defines.ConstructionPriceCeiling));
    }

    /// <summary>What this building costs this country now: catalogue price, price level, modifiers.</summary>
    public static float Of(Campaign campaign, Nation nation, BuildingKind kind) =>
        BuildingCatalog.Get(kind).Cost * PriceLevel(campaign, nation) *
        (nation is null ? 1f : NationalModifiers.Factor(nation, "construction_cost"));

    /// <summary>How many economies of the reference size this one is, at least 1.</summary>
    public static float Size(Campaign campaign, Nation nation)
    {
        var defines = campaign?.Defines;
        if (defines is null || nation is null || !defines.ConstructionPriceLevelEnabled) return 1f;
        var size = nation.GrossOutput / MathF.Max(0.01f, defines.ConstructionReferenceOutput);
        return float.IsFinite(size) ? M.Clamp(size, 1f, MathF.Max(1f, defines.ConstructionSizeCap)) : 1f;
    }

    /// <summary>A per-country project limit, scaled to the size of the economy.</summary>
    /// <summary>How many of a country's open sites work each day.</summary>
    public static int WorkingSites(Campaign campaign, Nation nation)
    {
        var defines = campaign?.Defines;
        if (defines is null || !defines.ConstructionSitesInParallel) return 1;
        return Math.Max(1, (int)MathF.Round(Size(campaign, nation) * MathF.Max(0f, defines.ConstructionParallelPerEconomy)));
    }

    public static int Limit(Campaign campaign, Nation nation, int perEconomy) =>
        Math.Max(perEconomy, (int)MathF.Round(perEconomy * Size(campaign, nation)));

    /// <summary>
    /// How many days of work each of a country's working sites does in one calendar day, and so
    /// also how many days' worth of its price the treasury pays for each site that day.
    ///
    /// Construction capacity is the country's builders: the cranes, contractors and crews every
    /// finished building adds a little to (BuildingDefinition.CapacityGain) and wear, war and
    /// demolition take away. Until 1 November 2026 it only scaled the daily payment, and a site's
    /// work clock still counted one day a day - so above one it paid the bill sooner and finished
    /// on the same date, which made the stat every building raised do nothing for most of the
    /// world. Now it is the speed of the work itself:
    ///
    ///   - below one, a site works that fraction of a day each day, never less than a quarter
    ///     (a country that has lost its builders still builds, slowly);
    ///   - at one, a day a day - the catalogue's build time;
    ///   - above one, faster, with diminishing returns towards ConstructionPaceCeiling: the first
    ///     point above one buys ConstructionPaceHalfway's half of the way there, the next a third
    ///     of what is left, and so on.
    ///
    /// The money follows the work: a site that works 1.25 days a day is paid 1.25 days of its price
    /// a day, so faster building costs exactly the same in total, only sooner. Every country runs on
    /// the same curve; nothing here asks who is playing it.
    /// </summary>
    public static float Pace(Defines defines, float capacity)
    {
        if (!float.IsFinite(capacity)) return 1f;
        if (capacity <= 1f) return MathF.Max(0.25f, capacity);
        var ceiling = MathF.Max(1f, defines?.ConstructionPaceCeiling ?? 1f);
        var halfway = MathF.Max(0.01f, defines?.ConstructionPaceHalfway ?? 1f);
        var above = capacity - 1f;
        return 1f + (ceiling - 1f) * above / (above + halfway);
    }

    /// <summary>The pace this country's sites work at today (Pace of its construction capacity).</summary>
    public static float Pace(Campaign campaign, Nation nation) =>
        nation is null ? 1f : Pace(campaign?.Defines, nation.ConstructionCapacity);

    /// <summary>
    /// The calendar days a new building of this kind would take this country at today's pace: the
    /// catalogue time, shortened by the construction_speed modifier as a queued site is
    /// (QueueConstructionCommand), then divided by Pace. What the build list says, so the number a
    /// player reads is the number the site will actually take while capacity stays where it is.
    /// </summary>
    public static int ExpectedDays(Campaign campaign, Nation nation, BuildingKind kind)
    {
        var definition = BuildingCatalog.Get(kind);
        var speed = nation is null ? 1f : NationalModifiers.Factor(nation, "construction_speed");
        if (!float.IsFinite(speed) || speed <= 0f) speed = 1f;
        var work = Math.Max(1, (int)MathF.Round(definition.Days / speed));
        return Math.Max(1, (int)MathF.Ceiling(work / Pace(campaign, nation) - 0.001f));
    }
}
