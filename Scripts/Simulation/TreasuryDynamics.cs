using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether treasuries are worked on at all by what follows: erosion, the pressure to spend, the credit cycle, shocks.</summary>
    public bool TreasuryDynamicsEnabled { get; set; } = true;

    /// <summary>What reserves earn a year; inflation above it eats them.</summary>
    public float ReserveYield { get; set; } = 0.02f;

    /// <summary>
    /// Reserves a government can sit on, as a share of its GDP, before the country wants them
    /// spent; the yearly share of the excess that goes - in handouts, tax cuts and public works
    /// where the government answers to voters (war restraint 1), in pockets where it answers to
    /// nobody (restraint 0); and what the spending buys in stability, per unit of GDP spent.
    /// </summary>
    public float TreasuryComfortShare { get; set; } = 0.12f;
    public float TreasurySpendingRate { get; set; } = 0.3f;
    public float TreasuryCorruptionRate { get; set; } = 0.12f;
    public float TreasurySpendingStability { get; set; } = 0.5f;
    public float TreasurySpendingStabilityWeeklyMax { get; set; } = 0.002f;

    /// <summary>
    /// The world credit cycle: how much leverage calm years build each year; the yearly chance of
    /// a financial crisis at full leverage (it rises with the square of it); where it starts at.
    /// </summary>
    public float LeverageGrowthPerYear { get; set; } = 0.11f;
    public float FinancialCrisisChance { get; set; } = 0.35f;
    public float StartingLeverage { get; set; } = 0.45f;

    /// <summary>
    /// A financial crisis at full severity: the bailout, as a share of GDP; the output lost at its
    /// worst; how long the recession lasts; what it costs in stability.
    /// </summary>
    public float CrisisBailoutShare { get; set; } = 0.06f;
    public float CrisisRecessionDepth { get; set; } = 0.08f;
    public int CrisisRecessionDays { get; set; } = 540;
    public float CrisisStability { get; set; } = 0.06f;

    /// <summary>
    /// Contagion: how strongly a country's trade with crisis-struck countries carries the crisis
    /// in, how much weaker each link of the chain is, how many links it runs, and the least
    /// severity worth passing on.
    /// </summary>
    public float ContagionStrength { get; set; } = 1.5f;
    public float ContagionDecay { get; set; } = 0.6f;
    public int ContagionHops { get; set; } = 3;
    public float ContagionFloor { get; set; } = 0.15f;

    /// <summary>A world pandemic: its yearly chance, the output it costs at its worst, its bill as a share of GDP, its length.</summary>
    public float PandemicChance { get; set; } = 0.012f;
    public float PandemicDepth { get; set; } = 0.05f;
    public float PandemicCostShare { get; set; } = 0.02f;
    public int PandemicDays { get; set; } = 365;
}

public sealed partial class Nation
{
    /// <summary>The share of output a recession takes at its worst, and the days it began and ends.</summary>
    public float RecessionDepth;
    public int RecessionStartDay;
    public int RecessionUntilDay;
}

public sealed partial class Campaign
{
    /// <summary>How leveraged the world economy is, 0 to 1: calm builds it, a crisis spends it.</summary>
    public float WorldLeverage = -1f;
    public int LastFinancialCrisisDay = -100000;
}

/// <summary>A kind of unexpected bill (common/economic_shocks): earthquakes, floods, epidemics.</summary>
public sealed class EconomicShockDefinition
{
    public string Id = "";
    public string Name = "";
    public float AnnualChance;
    public float CostMin, CostMax;
    public float Damage;
    public float Deaths;
    public float Stability;
}

/// <summary>
/// Why money does not simply pile up. Capital in history accumulated and was spent as fast:
///
/// - reserves lose to inflation what they do not earn;
/// - a full treasury is a political fact - voters want it back as tax cuts and handouts,
///   and where nobody answers to voters it leaks into pockets;
/// - calm years build leverage until a financial crisis spends it - bailouts, recession, lost
///   stability - and the crisis travels along trade to the countries that sell to and buy from
///   the stricken (1873, 1929, 1997, 2008);
/// - and the bills nobody budgets for: earthquakes, floods, storms, droughts, epidemics, and
///   once in a long while a pandemic that reaches everybody.
///
/// The same rules for every government, played or not.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// МАКРОЭКОНОМИЧЕСКИЙ ЦИКЛ И КАНАЛЫ ЗАРАЖЕНИЯ (MACROECONOMIC CYCLE FLOW):
/// ═══════════════════════════════════════════════════════════════════════════════
///
///    [ Казна / Доходы ]
///           │
///           ├─► Эрозия инфляцией: (Inflation - Yield) / 52
///           ├─► Слив излишков > 12% ВВП: блага (демократия) / коррупция (автократия)
///           ├─► Нагрузка DEFCON: налог готовности (alertDrain)
///           │
///    [ Спокойные годы: WorldLeverage += 0.11 / 52 ]
///           │
///           ▼
///    [ Финансовый шок: P ~ Leverage² * 0.35 / 52 ]
///           │
///           ├─► Эпицентр (Bailout 6% ВВП, рецессия 8% выпуска, падение стабильности)
///           │
///           ▼
///    [ Распространение по торговым артериям: Spread() ]
///    Партнёр 1 (вес торговли) ──► Партнёр 2 (затухание 0.6) ──► Партнёр 3
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// ДЛЯ МОДДЕРОВ (FOR MODDERS) — Архитектура макроэкономики и шоков:
/// ═══════════════════════════════════════════════════════════════════════════════
/// 1. ЕДИНИЦЫ ИЗМЕРЕНИЯ (UNITS):
///    • Treasury, Gdp: миллиарды условных единиц игры (1.0 = 1 млрд).
///    • Inflation, ReserveYield: годовые коэффициенты (0.02 = 2% годовых).
///    • Stability: диапазон от 0.0 до 1.0.
///    • WorldLeverage: кредитное плечо мировой экономики от 0.0 (разгружена) до 1.0 (перегрета).
///
/// 2. МЕХАНИЗМЫ РАСХОДОВАНИЯ И ДИНАМИКИ (DYNAMICS):
///    • Инфляционная эрозия (Erode): еженедельно казна теряет потерю покупательной способности
///      на сумму excess inflation = (Inflation - ReserveYield) / 52.
///    • Политический слив излишков (SpendDown): казна свыше TreasuryComfortShare * GDP (12%)
///      тратится на общественные блага (давая бонус стабильности) при подотчётной власти
///      (AiStatecraft.Restraint) или расхищается при низкой подотчётности.
///    • Мобилизационная нагрузка DEFCON: при тревоге DEFCON 4..1 войска требуют круглосуточного
///      снабжения, списывая еженедельно alertDrain = Gdp * (upkeepMult - 1f) * 0.002f.
///
/// 3. КРЕДИТНЫЙ ЦИКЛ И КРИЗИСЫ (CreditCycle & Spread):
///    • В мирные годы WorldLeverage прирастает на LeverageGrowthPerYear / 52.
///    • Вероятность кризиса пропорциональна квадрату плеча: FinancialCrisisChance * Leverage^2 / 52.
///    • Эпицентр (CrisisOrigin) выбирается среди крупных экономик взвешенно по Gdp^0.7.
///    • Распространение (Spread) передаётся по торговым путям (TradeRoutes) на глубину
///      ContagionHops (по умолчанию 3 звена).
///
/// 4. ЭКОНОМИЧЕСКИЕ КАТАСТРОФЫ (UnexpectedBills):
///    • Катастрофы считываются из common/economic_shocks/*.json (землетрясения, наводнения,
///      засухи, тайфуны). Каждая катастрофа имеет годовую вероятность annual_chance, диапазон
///      стоимости cost [min, max] (доля ВВП), урон инфраструктуре damage, смертность deaths
///      и падение стабильности stability.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class TreasuryDynamics
{
    /// <summary>This session's tallies, for the bench. Not campaign state.</summary>
    public static int Crises, Contagions, Disasters, Pandemics;
    public static float Spent, Leaked, Eroded;

    private static readonly List<EconomicShockDefinition> Shocks = new();
    private static bool _loaded;

    public static IReadOnlyList<EconomicShockDefinition> ShockKinds { get { Load(); return Shocks; } }

    public static void ResetCatalog(List<string> errors = null)
    {
        Shocks.Clear();
        var byId = new Dictionary<string, EconomicShockDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "economic_shocks"))
            using (document)
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) { errors?.Add($"common/economic_shocks: '{entry.Name}' must be an object."); continue; }
                    if (!byId.TryGetValue(entry.Name, out var shock)) byId[entry.Name] = shock = new EconomicShockDefinition { Id = entry.Name, Name = entry.Name };
                    foreach (var field in entry.Value.EnumerateObject())
                    {
                        switch (field.Name)
                        {
                            case "name": shock.Name = field.Value.GetString() ?? shock.Name; break;
                            case "annual_chance": shock.AnnualChance = Math.Max(0f, field.Value.GetSingle()); break;
                            case "cost":
                                if (field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() == 2)
                                {
                                    shock.CostMin = Math.Max(0f, field.Value[0].GetSingle());
                                    shock.CostMax = Math.Max(shock.CostMin, field.Value[1].GetSingle());
                                }
                                break;
                            case "damage": shock.Damage = M.Clamp01(field.Value.GetSingle()); break;
                            case "deaths": shock.Deaths = M.Clamp01(field.Value.GetSingle()); break;
                            case "stability": shock.Stability = M.Clamp01(field.Value.GetSingle()); break;
                            default: errors?.Add($"common/economic_shocks: '{entry.Name}' has no field '{field.Name}'."); break;
                        }
                    }
                }
        Shocks.AddRange(byId.Values.OrderBy(s => s.Id, StringComparer.Ordinal));
        _loaded = true;
    }

    private static void Load()
    {
        if (!_loaded) ResetCatalog();
    }

    /// <summary>What a recession takes off this country's output now: 1 when there is none.</summary>
    public static float OutputFactor(Campaign c, Nation nation)
    {
        if (nation.RecessionDepth <= 0f || c.ElapsedDays >= nation.RecessionUntilDay) return 1f;
        var span = Math.Max(1, nation.RecessionUntilDay - nation.RecessionStartDay);
        var left = M.Clamp01((nation.RecessionUntilDay - c.ElapsedDays) / (float)span);
        // Down fast, back slowly: the worst comes in the first fifth.
        var elapsed = 1f - left;
        var shape = elapsed < 0.2f ? elapsed / 0.2f : left / 0.8f;
        return 1f - M.Clamp01(nation.RecessionDepth) * M.Clamp01(shape);
    }

    public static void Tick(Campaign c)
    {
        if (c.ElapsedDays % 7 != 6 || !c.Defines.TreasuryDynamicsEnabled) return;
        var d = c.Defines;
        if (c.WorldLeverage < 0f) c.WorldLeverage = M.Clamp01(d.StartingLeverage);
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not null || nation.Population <= 0 || nation.Gdp <= 0f) continue;
            Erode(c, nation);
            SpendDown(c, nation);
            // National alert can exceed the world's alert: do not gate this cost on
            // DefconLevel alone. UpkeepMultiplier reads the country's effective posture.
            var upkeep = DefconSystem.UpkeepMultiplier(c, nation);
            if (upkeep > 1f)
            {
                var alertDrain = nation.Gdp * (upkeep - 1f) * 0.002f;
                nation.Treasury = MathF.Max(0f, nation.Treasury - alertDrain);
            }
            if (nation.RecessionDepth > 0f && c.ElapsedDays >= nation.RecessionUntilDay) nation.RecessionDepth = 0f;
        }
        CreditCycle(c);
        UnexpectedBills(c);
    }

    /// <summary>Reserves lose what inflation takes above what they earn.</summary>
    private static void Erode(Campaign c, Nation nation)
    {
        if (nation.Treasury <= 0f) return;
        var rate = M.Clamp(nation.Inflation - c.Defines.ReserveYield, 0f, 2f);
        var loss = nation.Treasury * rate / 52f;
        nation.Treasury -= loss;
        Eroded += loss;
    }

    /// <summary>A treasury above the comfortable is spent down, by voters or by pockets.</summary>
    private static void SpendDown(Campaign c, Nation nation)
    {
        var d = c.Defines;
        var excess = nation.Treasury - nation.Gdp * d.TreasuryComfortShare;
        if (excess <= 0f) return;
        var accountability = M.Clamp01(AiStatecraft.Restraint(c, nation));
        var spent = excess * d.TreasurySpendingRate * (0.4f + 0.6f * accountability) / 52f;
        var leaked = excess * d.TreasuryCorruptionRate * (1f - accountability) / 52f;
        nation.Treasury -= spent + leaked;
        nation.Stability = M.Clamp01(nation.Stability + MathF.Min(d.TreasurySpendingStabilityWeeklyMax,
            spent / MathF.Max(0.01f, nation.Gdp) * d.TreasurySpendingStability));
        Spent += spent;
        Leaked += leaked;
    }

    // ---- the credit cycle ---------------------------------------------------------------------

    private static void CreditCycle(Campaign c)
    {
        var d = c.Defines;
        c.WorldLeverage = M.Clamp01(c.WorldLeverage + d.LeverageGrowthPerYear / 52f);
        var weekly = d.FinancialCrisisChance * c.WorldLeverage * c.WorldLeverage / 52f;
        if (c.Random.NextFloat() >= weekly) return;
        var origin = CrisisOrigin(c);
        if (origin is null) return;
        var severity = 0.5f + 0.5f * c.Random.NextFloat() * (0.5f + 0.5f * c.WorldLeverage);
        Strike(c, origin, severity, true);
        Crises++;
        c.Chronicle.Record(c, "financial_crisis", origin.Id, subject: $"{severity * 100f:0}");
        Spread(c, origin, severity);
        c.WorldLeverage = M.Clamp01(c.WorldLeverage * 0.25f);
        c.LastFinancialCrisisDay = c.ElapsedDays;
        c.MarkStrategicChange();
    }

    /// <summary>Where a crisis starts: a large economy, the larger the likelier, and one already deep in debt likelier still.</summary>
    private static Nation CrisisOrigin(Campaign c)
    {
        var candidates = c.Nations.Where(n => n.Movement is null && n.Population > 0 && n.Gdp > 0f).ToList();
        var total = 0f;
        var weights = new float[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var n = candidates[i];
            weights[i] = MathF.Pow(n.Gdp, 0.7f) * (n.DebtCondition == DebtCondition.Current ? 1f : 2f);
            total += weights[i];
        }
        if (total <= 0f) return null;
        var pick = c.Random.NextFloat() * total;
        for (var i = 0; i < candidates.Count; i++)
        {
            pick -= weights[i];
            if (pick <= 0f) return candidates[i];
        }
        return candidates[^1];
    }

    /// <summary>What a crisis does to one country: the bailout, the recession, the anger.</summary>
    internal static void Strike(Campaign c, Nation nation, float severity, bool origin)
    {
        var d = c.Defines;
        severity = M.Clamp01(severity);
        nation.Treasury -= nation.Gdp * d.CrisisBailoutShare * severity * (origin ? 1f : 0.6f);
        var depth = d.CrisisRecessionDepth * severity;
        // A second blow on a country already down deepens and lengthens it rather than resetting it.
        if (nation.RecessionDepth > 0f && c.ElapsedDays < nation.RecessionUntilDay)
            depth = MathF.Max(nation.RecessionDepth, depth) + MathF.Min(nation.RecessionDepth, depth) * 0.5f;
        nation.RecessionDepth = M.Clamp(depth, 0f, 0.35f);
        nation.RecessionStartDay = c.ElapsedDays;
        nation.RecessionUntilDay = c.ElapsedDays + (int)(d.CrisisRecessionDays * (0.6f + 0.4f * severity));
        nation.Stability = M.Clamp01(nation.Stability - d.CrisisStability * severity);
    }

    /// <summary>The domino: each country's trade with the stricken carries the crisis in, weaker at every link.</summary>
    private static void Spread(Campaign c, Nation origin, float severity)
    {
        var d = c.Defines;
        var struck = new Dictionary<int, float> { [origin.Id] = severity };
        var frontier = new List<int> { origin.Id };
        // Each country's trade, as a share of all it does, with each partner.
        var trade = new Dictionary<int, Dictionary<int, float>>();
        var totals = new Dictionary<int, float>();
        foreach (var route in c.TradeRoutes)
        {
            var value = MathF.Max(0f, route.Value);
            if (value <= 0f) continue;
            Add(route.SellerId, route.BuyerId, value);
            Add(route.BuyerId, route.SellerId, value);
        }
        void Add(int a, int b, float value)
        {
            if (!trade.TryGetValue(a, out var partners)) trade[a] = partners = new Dictionary<int, float>();
            partners[b] = partners.GetValueOrDefault(b) + value;
            totals[a] = totals.GetValueOrDefault(a) + value;
        }
        for (var hop = 0; hop < Math.Max(0, d.ContagionHops) && frontier.Count > 0; hop++)
        {
            var next = new List<int>();
            foreach (var nation in c.Nations.OrderBy(n => n.Id))
            {
                if (struck.ContainsKey(nation.Id) || nation.Movement is not null || nation.Gdp <= 0f) continue;
                if (!trade.TryGetValue(nation.Id, out var partners) || totals.GetValueOrDefault(nation.Id) <= 0f) continue;
                var exposure = 0f;
                var carried = 0f;
                foreach (var source in frontier)
                    if (partners.TryGetValue(source, out var value))
                    {
                        var share = value / totals[nation.Id];
                        exposure += share;
                        carried = MathF.Max(carried, struck[source]);
                    }
                if (exposure <= 0f) continue;
                var chance = M.Clamp01(exposure * d.ContagionStrength * carried);
                if (c.Random.NextFloat() >= chance) continue;
                var felt = carried * d.ContagionDecay * (0.6f + 0.4f * M.Clamp01(exposure * 3f));
                if (felt < d.ContagionFloor) continue;
                struck[nation.Id] = felt;
                next.Add(nation.Id);
                Strike(c, nation, felt, false);
                Contagions++;
                c.Chronicle.Record(c, "crisis_contagion", nation.Id, origin.Id, subject: $"{felt * 100f:0}");
            }
            frontier = next;
        }
    }

    // ---- the bills nobody budgets for -----------------------------------------------------------

    private static void UnexpectedBills(Campaign c)
    {
        var d = c.Defines;
        if (c.Random.NextFloat() < d.PandemicChance / 52f)
        {
            Pandemics++;
            foreach (var nation in c.Nations)
            {
                if (nation.Movement is not null || nation.Gdp <= 0f || nation.Population <= 0) continue;
                nation.Treasury -= nation.Gdp * d.PandemicCostShare * (0.7f + 0.6f * c.Random.NextFloat());
                var depth = d.PandemicDepth * (0.6f + 0.8f * c.Random.NextFloat());
                if (nation.RecessionDepth <= 0f || c.ElapsedDays >= nation.RecessionUntilDay)
                {
                    nation.RecessionDepth = depth;
                    nation.RecessionStartDay = c.ElapsedDays;
                    nation.RecessionUntilDay = c.ElapsedDays + d.PandemicDays;
                }
                else nation.RecessionDepth = M.Clamp(nation.RecessionDepth + depth * 0.5f, 0f, 0.35f);
            }
            var worst = c.Nations.Where(n => n.Movement is null && n.Population > 0).OrderByDescending(n => n.Population).FirstOrDefault();
            if (worst is not null) c.Chronicle.Record(c, "pandemic", worst.Id);
        }

        Load();
        if (Shocks.Count == 0) return;
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is not null || nation.Gdp <= 0f || nation.Population <= 0) continue;
            foreach (var shock in Shocks)
            {
                if (shock.AnnualChance <= 0f || c.Random.NextFloat() >= shock.AnnualChance / 52f) continue;
                var regions = c.StatesOf(nation);
                if (regions.Length == 0) break;
                var region = PickByPopulation(c, regions);
                var cost = nation.Gdp * (shock.CostMin + (shock.CostMax - shock.CostMin) * c.Random.NextFloat());
                nation.Treasury -= cost;
                if (shock.Damage > 0f) region.WarDamage = MathF.Min(0.8f, MathF.Max(region.WarDamage, region.WarDamage + shock.Damage));
                if (shock.Deaths > 0f && region.Population > 0)
                    EconomySystem.AdjustStatePopulation(region, -(long)(region.Population * shock.Deaths));
                nation.Stability = M.Clamp01(nation.Stability - shock.Stability);
                Disasters++;
                c.Chronicle.Record(c, "disaster_" + shock.Id, nation.Id, place: region.DisplayName, subject: Fmt(cost));
                break;
            }
        }
    }

    private static string Fmt(float billions) => billions >= 1f ? $"{billions:0.0} bn" : $"{billions * 1000f:0} m";

    private static ScenarioState PickByPopulation(Campaign c, ScenarioState[] regions)
    {
        var total = 0d;
        foreach (var region in regions) total += Math.Max(1L, region.Population);
        var pick = c.Random.NextFloat() * total;
        foreach (var region in regions)
        {
            pick -= Math.Max(1L, region.Population);
            if (pick <= 0d) return region;
        }
        return regions[^1];
    }
}
