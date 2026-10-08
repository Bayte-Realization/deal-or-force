using System;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    // Physical reference for residual industry not represented by individual factories.
    // Never rebase from today's market-valued GDP on every week: that is positive feedback.
    public bool ProductionReferenceInitialized;
    public float ProductionReferenceGdp;
    public long ProductionReferencePopulation;
}

public static class ProductionCapacity
{
    public static void Initialize(Nation n)
    {
        if (n is null || n.ProductionReferenceInitialized) return;
        n.ProductionReferenceInitialized = true;
        n.ProductionReferencePopulation = Math.Max(0, n.Population);
        n.ProductionReferenceGdp = float.IsFinite(n.Gdp) && n.Population > 0 ? Math.Max(0, n.Gdp) : 0;
    }

    /// <summary>Scenario-scaled productive capacity, independent of current prices and nominal GDP.</summary>
    public static float Of(Nation n)
    {
        Initialize(n);
        if (n.Population <= 0 || n.ProductionReferencePopulation <= 0 || n.Landless) return 0;
        var capacity = n.ProductionReferenceGdp * (n.Population / (double)n.ProductionReferencePopulation);
        if (!double.IsFinite(capacity) || capacity > float.MaxValue)
            throw new InvalidOperationException("Non-finite production capacity: " + n.Tag);
        return (float)Math.Max(0, capacity);
    }
}
