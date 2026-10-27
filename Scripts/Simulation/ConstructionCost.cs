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
    public float ConstructionPriceCeiling { get; set; } = 2.5f;

    /// <summary>
    /// The weekly output (Nation.GrossOutput) one economy's worth of building is sized for. A
    /// country this size runs the catalogue's project limits; one ten times larger runs ten times
    /// as many, up to ConstructionSizeCap.
    /// </summary>
    public float ConstructionReferenceOutput { get; set; } = 12f;
    public float ConstructionSizeCap { get; set; } = 12f;
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
    public static int Limit(Campaign campaign, Nation nation, int perEconomy) =>
        Math.Max(perEconomy, (int)MathF.Round(perEconomy * Size(campaign, nation)));
}
