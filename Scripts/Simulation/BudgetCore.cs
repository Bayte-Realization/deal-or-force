using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>Whether a budget line brings money in or takes it out.</summary>
public enum BudgetSide
{
    Revenue,
    Expense
}

/// <summary>What a line is charged against.</summary>
public enum BudgetBasis
{
    Output,
    Debt,
    Tariffs,
    TradeBalance
}

/// <summary>
/// Where a country keeps its rate for a line.
///
/// The four with a field of their own are not a special case anybody wanted; they are four
/// names that three sliders, the AI cabinet, four campaign commands, every scripted effect
/// and every save ever written already use. Everything else lives in the dictionary.
/// </summary>
public enum BudgetStore
{
    Policy,
    TaxRate,
    MilitarySpending,
    WelfareSpending,
    EducationSpending
}

/// <summary>
/// One line of a national budget, as data.
///
/// Every line is the same arithmetic:
///
///     amount = basis · rate · yield · (constant + Σ weight·factor)
///
/// `basis` is what the line is charged against - a week of output for almost everything,
/// the outstanding debt for interest, and for tariffs and the trade balance the figure the
/// market already worked out. `rate` is the policy: the number on a slider for the lines a
/// government chooses, and a fixed 1 for the ones it does not get to choose. `yield` is how
/// much of the basis one whole point of rate is worth, and the bracket is what the country
/// is like - a tax on incomes brings in more from a settled country, policing costs more in
/// an unsettled one.
///
/// That one expression covers everything the game shipped with, which is the point: the
/// five expenses and three revenues that used to be eight hard-coded multiplications in the
/// weekly tick are now eight rows in a file, and reproduce the same budget to the last
/// decimal. What a mod adds is a ninth row rather than a fork of the tick.
/// </summary>
public sealed class BudgetLine
{
    public string Id = "";

    /// <summary>What the player is shown. A localisation key, like every other caption.</summary>
    public string Name = "";

    /// <summary>One sentence under the slider. Also a key.</summary>
    public string Description = "";

    public BudgetSide Side;

    /// <summary>`output`, `debt`, `tariffs` or `trade_balance`.</summary>
    public string Basis = "output";

    /// <summary>
    /// The same two facts as `Basis` and `Id`, decided once when the file is read.
    ///
    /// The weekly tick asks both of these seventeen times for each of two hundred and sixty
    /// countries, and asking them as strings cost forty kilobytes of garbage a simulated
    /// day - `Id.ToLowerInvariant()` alone was four hundred and sixty thousand throwaway
    /// strings over a two-year run. Nothing about the data changed; only when the question
    /// is answered.
    /// </summary>
    public BudgetBasis Charge = BudgetBasis.Output;

    public BudgetStore Store = BudgetStore.Policy;

    public float Yield = 1f;
    public float Constant = 1f;
    public int[] Factors = Array.Empty<int>();
    public float[] Weights = Array.Empty<float>();

    public float Default;
    public float Minimum;
    public float Maximum = 1f;

    /// <summary>
    /// The rate at which this line is neither a stimulus nor a burden. Every effect below
    /// is measured from here, so a line whose rate sits at its neutral point changes
    /// nothing about the country and only moves money.
    /// </summary>
    public float Neutral;

    /// <summary>Whether a government sets this, or it simply happens to it.</summary>
    public bool PlayerSet;

    /// <summary>Where a slider appears among the others. Ties break on id.</summary>
    public int Order;

    // Per whole point of (rate - neutral). Annual for growth, weekly for the rest, which
    // is the cadence each of those quantities already moves at.
    public float Growth;
    public float Living;
    public float Literacy;
    public float Radicalism;
    public float Stability;

    /// <summary>
    /// Above this rate the public stops treating the line as ordinary. Zero disables it.
    /// A tax can be raised a long way before anybody minds and then minded a great deal,
    /// and a straight line through the origin cannot say that.
    /// </summary>
    public float ResentedAbove;
    public float ResentedLiving;
    public float ResentedRadicalism;
}

/// <summary>What a week of budget did, without any of it being written down as prose.</summary>
public readonly struct BudgetOutcome
{
    public BudgetOutcome(float revenue, float taxation, float expenses,
        float growth, float living, float literacy, float radicalism, float stability)
    {
        Revenue = revenue;
        Taxation = taxation;
        Expenses = expenses;
        Growth = growth;
        Living = living;
        Literacy = literacy;
        Radicalism = radicalism;
        Stability = stability;
    }

    /// <summary>Everything that came in, tariffs and the trade balance included.</summary>
    public readonly float Revenue;

    /// <summary>Only what was actually levied - what a finance minister would call taxation.</summary>
    public readonly float Taxation;

    public readonly float Expenses;
    public readonly float Balance => Revenue - Expenses;

    public readonly float Growth;
    public readonly float Living;
    public readonly float Literacy;
    public readonly float Radicalism;
    public readonly float Stability;
}

/// <summary>
/// The budget the game runs on, read from `common/budget` rather than compiled in.
/// </summary>
public static class BudgetCore
{
    private const int SlotStability = 0;
    private const int SlotLiteracy = 1;
    private const int SlotStandardOfLiving = 2;
    private const int SlotUrbanisation = 3;
    private const int SlotEmployment = 4;
    private const int SlotAtWar = 5;
    private const int SlotShortage = 6;
    private const int SlotCount = 7;

    /// <summary>
    /// Filled once per country per week and read by every line. Static and reused for the
    /// same reason the production slot table is: the weekly tick walks two hundred and
    /// sixty countries and must not leave two hundred and sixty arrays behind it.
    /// </summary>
    private static readonly float[] Slots = new float[SlotCount];

    private static readonly List<BudgetLine> Lines = new();

    public static IReadOnlyList<BudgetLine> All => Lines;

    /// <summary>The lines a government actually sets, in the order a panel should show them.</summary>
    public static IEnumerable<BudgetLine> PlayerLines
    {
        get
        {
            foreach (var line in Lines)
                if (line.PlayerSet) yield return line;
        }
    }

    public static BudgetLine Find(string id)
    {
        foreach (var line in Lines)
            if (string.Equals(line.Id, id, StringComparison.OrdinalIgnoreCase)) return line;
        return null;
    }

    static BudgetCore() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Lines.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "budget"))
            using (document)
                Merge(document.RootElement, errors);
        Sort();
        if (Lines.Count == 0)
            errors?.Add("common/budget describes no lines, so no country can raise or spend anything.");
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Sort();
    }

    /// <summary>
    /// Add or replace lines from one file. Later wins by id, so a mod that only wants
    /// income tax to yield less names `income_tax` and inherits the other sixteen.
    /// </summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var group in root.EnumerateObject())
        {
            var side = group.Name.ToLowerInvariant() switch
            {
                "revenues" or "revenue" => BudgetSide.Revenue,
                "expenses" or "expense" => BudgetSide.Expense,
                _ => (BudgetSide?)null
            };
            if (side is null || group.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in group.Value.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var line = Read(entry.Name, side.Value, entry.Value, errors);
                if (line is null) continue;
                Lines.RemoveAll(existing => string.Equals(existing.Id, line.Id, StringComparison.OrdinalIgnoreCase));
                Lines.Add(line);
            }
        }
    }

    /// <summary>Read a file and say what is wrong with it, keeping nothing.</summary>
    public static void Validate(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var group in root.EnumerateObject())
        {
            var side = group.Name.ToLowerInvariant() switch
            {
                "revenues" or "revenue" => BudgetSide.Revenue,
                "expenses" or "expense" => BudgetSide.Expense,
                _ => (BudgetSide?)null
            };
            if (side is null)
            {
                problems?.Add($"budget: `{group.Name}` is neither `revenues` nor `expenses`.");
                continue;
            }
            if (group.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in group.Value.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Object)
                    Read(entry.Name, side.Value, entry.Value, problems);
        }
    }

    /// <summary>
    /// Read one line and say what is wrong with it, keeping nothing.
    ///
    /// For whatever is holding a single line rather than a whole file - the bench walking
    /// a mod template, for one. Which side it claims to be on makes no difference to
    /// whether it is well formed, so the caller does not have to know.
    /// </summary>
    public static void ValidateLine(string id, JsonElement element, List<string> problems) =>
        Read(id, BudgetSide.Revenue, element, problems);

    private static void Sort() =>
        Lines.Sort((left, right) =>
        {
            if (left.Side != right.Side) return left.Side.CompareTo(right.Side);
            if (left.Order != right.Order) return left.Order.CompareTo(right.Order);
            return string.CompareOrdinal(left.Id, right.Id);
        });

    private static BudgetLine Read(string id, BudgetSide side, JsonElement element, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            errors?.Add("budget: a line with no id.");
            return null;
        }

        var line = new BudgetLine { Id = id.Trim(), Side = side, Name = id.Trim() };
        var factors = new List<int>();
        var weights = new List<float>();

        foreach (var field in element.EnumerateObject())
        {
            // A row documents itself with `//name` keys, the way the rest of the data does.
            if (field.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            switch (field.Name.ToLowerInvariant())
            {
                case "name" when field.Value.ValueKind == JsonValueKind.String:
                    line.Name = field.Value.GetString() ?? line.Name;
                    break;
                case "description" when field.Value.ValueKind == JsonValueKind.String:
                    line.Description = field.Value.GetString() ?? "";
                    break;
                case "basis" when field.Value.ValueKind == JsonValueKind.String:
                    line.Basis = (field.Value.GetString() ?? "output").Trim().ToLowerInvariant();
                    break;
                case "yield":
                    line.Yield = Number(field.Value, line.Yield);
                    break;
                case "order":
                    line.Order = (int)Number(field.Value, line.Order);
                    break;
                case "scale" when field.Value.ValueKind == JsonValueKind.Object:
                    ReadScale(id, field.Value, line, factors, weights, errors);
                    break;
                case "rate" when field.Value.ValueKind == JsonValueKind.Object:
                    ReadRate(field.Value, line);
                    break;
                case "effects" when field.Value.ValueKind == JsonValueKind.Object:
                    ReadEffects(id, field.Value, line, errors);
                    break;
                case "resented_above" when field.Value.ValueKind == JsonValueKind.Object:
                    ReadResentment(field.Value, line);
                    break;
                default:
                    errors?.Add($"budget line `{id}`: `{field.Name}` is not a field a line has.");
                    break;
            }
        }

        switch (line.Basis)
        {
            case "output": line.Charge = BudgetBasis.Output; break;
            case "debt": line.Charge = BudgetBasis.Debt; break;
            case "tariffs": line.Charge = BudgetBasis.Tariffs; break;
            case "trade_balance": line.Charge = BudgetBasis.TradeBalance; break;
            default:
                errors?.Add($"budget line `{id}`: `{line.Basis}` is not a basis. " +
                            "Use output, debt, tariffs or trade_balance.");
                return null;
        }
        line.Store = line.Id.ToLowerInvariant() switch
        {
            "income_tax" => BudgetStore.TaxRate,
            "armed_forces" => BudgetStore.MilitarySpending,
            "welfare" => BudgetStore.WelfareSpending,
            "education" => BudgetStore.EducationSpending,
            _ => BudgetStore.Policy
        };
        if (line.Maximum < line.Minimum)
        {
            errors?.Add($"budget line `{id}`: maximum {line.Maximum} is below minimum {line.Minimum}.");
            return null;
        }

        line.Factors = factors.ToArray();
        line.Weights = weights.ToArray();
        line.Default = Math.Clamp(line.Default, line.Minimum, line.Maximum);
        return line;
    }

    private static void ReadScale(string id, JsonElement element, BudgetLine line,
        List<int> factors, List<float> weights, List<string> errors)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            var name = entry.Name.ToLowerInvariant();
            if (name == "constant")
            {
                line.Constant = Number(entry.Value, line.Constant);
                continue;
            }
            var slot = SlotOf(name);
            if (slot < 0)
            {
                errors?.Add($"budget line `{id}`: `{entry.Name}` is not a factor. Use one of " +
                            "stability, literacy, standard_of_living, urbanisation, employment, " +
                            "at_war, shortage - or constant.");
                continue;
            }
            factors.Add(slot);
            weights.Add(Number(entry.Value, 0f));
        }
    }

    private static void ReadRate(JsonElement element, BudgetLine line)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            switch (entry.Name.ToLowerInvariant())
            {
                case "default": line.Default = Number(entry.Value, line.Default); break;
                case "minimum" or "min": line.Minimum = Number(entry.Value, line.Minimum); break;
                case "maximum" or "max": line.Maximum = Number(entry.Value, line.Maximum); break;
                case "neutral": line.Neutral = Number(entry.Value, line.Neutral); break;
                case "player":
                    line.PlayerSet = entry.Value.ValueKind == JsonValueKind.True;
                    break;
            }
        }
    }

    private static void ReadEffects(string id, JsonElement element, BudgetLine line, List<string> errors)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            switch (entry.Name.ToLowerInvariant())
            {
                case "growth": line.Growth = Number(entry.Value, 0f); break;
                case "living": line.Living = Number(entry.Value, 0f); break;
                case "literacy": line.Literacy = Number(entry.Value, 0f); break;
                case "radicalism": line.Radicalism = Number(entry.Value, 0f); break;
                case "stability": line.Stability = Number(entry.Value, 0f); break;
                default:
                    errors?.Add($"budget line `{id}`: `{entry.Name}` is not an effect. Use " +
                                "growth, living, literacy, radicalism or stability.");
                    break;
            }
        }
    }

    private static void ReadResentment(JsonElement element, BudgetLine line)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            switch (entry.Name.ToLowerInvariant())
            {
                case "threshold": line.ResentedAbove = Number(entry.Value, 0f); break;
                case "living": line.ResentedLiving = Number(entry.Value, 0f); break;
                case "radicalism": line.ResentedRadicalism = Number(entry.Value, 0f); break;
            }
        }
    }

    private static int SlotOf(string name) => name switch
    {
        "stability" => SlotStability,
        "literacy" => SlotLiteracy,
        "standard_of_living" => SlotStandardOfLiving,
        "urbanisation" or "urbanization" => SlotUrbanisation,
        "employment" => SlotEmployment,
        "at_war" => SlotAtWar,
        "shortage" => SlotShortage,
        _ => -1
    };

    private static float Number(JsonElement element, float fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var value) ? value : fallback;

    /// <summary>
    /// What one country sets one line to.
    ///
    /// Four of these have a field of their own on the nation rather than a row in the
    /// dictionary, and that is deliberate rather than left over: three sliders, the AI's
    /// cabinet, every scripted effect and four campaign commands all name `TaxRate`,
    /// `MilitarySpending`, `WelfareSpending` and `EducationSpending` directly, and a save
    /// written before any of this existed has them at the top level. A line a mod adds has
    /// no such history and lives in the dictionary, where it costs nothing.
    /// </summary>
    public static float RateOf(Nation nation, BudgetLine line)
    {
        if (nation is null || line is null) return 0f;
        if (!line.PlayerSet) return 1f;
        return line.Store switch
        {
            BudgetStore.TaxRate => nation.TaxRate,
            BudgetStore.MilitarySpending => nation.MilitarySpending,
            BudgetStore.WelfareSpending => nation.WelfareSpending,
            BudgetStore.EducationSpending => nation.EducationSpending,
            _ => nation.PolicyRates.TryGetValue(line.Id, out var rate) ? rate : line.Default
        };
    }

    public static void SetRate(Nation nation, BudgetLine line, float value)
    {
        if (nation is null || line is null || !line.PlayerSet) return;
        value = Math.Clamp(value, line.Minimum, line.Maximum);
        switch (line.Store)
        {
            case BudgetStore.TaxRate: nation.TaxRate = value; return;
            case BudgetStore.MilitarySpending: nation.MilitarySpending = value; return;
            case BudgetStore.WelfareSpending: nation.WelfareSpending = value; return;
            case BudgetStore.EducationSpending: nation.EducationSpending = value; return;
            default: nation.PolicyRates[line.Id] = value; return;
        }
    }

    private static void FillSlots(Nation nation, bool atWar)
    {
        Slots[SlotStability] = nation.Stability;
        Slots[SlotLiteracy] = nation.Literacy;
        Slots[SlotStandardOfLiving] = nation.StandardOfLiving;
        Slots[SlotUrbanisation] = nation.Urbanisation > 0f ? nation.Urbanisation : 0.55f;
        Slots[SlotEmployment] = nation.EmployedShare;
        Slots[SlotAtWar] = atWar ? 1f : 0f;
        Slots[SlotShortage] = nation.ShortageIndex;
    }

    private static float BasisOf(Nation nation, BudgetLine line, float weeklyOutput) => line.Charge switch
    {
        BudgetBasis.Debt => MathF.Max(0f, -nation.Treasury),
        BudgetBasis.Tariffs => nation.WeeklyTariffIncome,
        BudgetBasis.TradeBalance => nation.WeeklyTradeBalance,
        _ => weeklyOutput
    };

    /// <summary>What one line is worth to one country this week.</summary>
    public static float Amount(Nation nation, BudgetLine line, float weeklyOutput, bool atWar)
    {
        if (nation is null || line is null) return 0f;
        FillSlots(nation, atWar);
        return AmountFromSlots(nation, line, weeklyOutput);
    }

    /// <summary>
    /// What a line would be worth at a rate the country has not set.
    ///
    /// For the panels, which have to say what moving a slider would do before it is moved.
    /// It goes through the same expression the weekly tick uses rather than a second copy
    /// of it: two copies of a formula are two formulas, and the second one is right until
    /// somebody edits the first, after which the panel quietly lies about the finances.
    /// </summary>
    public static float AmountAtRate(Nation nation, BudgetLine line, float weeklyOutput,
        bool atWar, float rate)
    {
        if (nation is null || line is null) return 0f;
        FillSlots(nation, atWar);
        var scale = line.Constant;
        for (var index = 0; index < line.Factors.Length; index++)
            scale += line.Weights[index] * Slots[line.Factors[index]];
        var amount = BasisOf(nation, line, weeklyOutput) * rate * line.Yield * scale;
        return line.Charge == BudgetBasis.TradeBalance ? amount : MathF.Max(0f, amount);
    }

    private static float AmountFromSlots(Nation nation, BudgetLine line, float weeklyOutput)
    {
        var scale = line.Constant;
        for (var index = 0; index < line.Factors.Length; index++)
            scale += line.Weights[index] * Slots[line.Factors[index]];

        var amount = BasisOf(nation, line, weeklyOutput) * RateOf(nation, line) * line.Yield * scale;
        // A trade balance is allowed to be a loss; nothing else is allowed to be negative,
        // because a tax that pays money out because the country is miserable is a bug
        // wearing a formula's clothes.
        return line.Charge == BudgetBasis.TradeBalance ? amount : MathF.Max(0f, amount);
    }

    /// <summary>
    /// One country's week: what it raised, what it spent, and what all of that is doing to
    /// the country beyond the money.
    /// </summary>
    public static BudgetOutcome Evaluate(Nation nation, float weeklyOutput, bool atWar)
    {
        if (nation is null) return default;
        FillSlots(nation, atWar);

        float revenue = 0f, taxation = 0f, expenses = 0f;
        float growth = 0f, living = 0f, literacy = 0f, radicalism = 0f, stability = 0f;

        foreach (var line in Lines)
        {
            var amount = AmountFromSlots(nation, line, weeklyOutput);
            if (line.Side == BudgetSide.Revenue)
            {
                revenue += amount;
                if (line.Charge is BudgetBasis.Output or BudgetBasis.Debt) taxation += amount;
            }
            else
            {
                expenses += amount;
            }

            if (!line.PlayerSet) continue;
            var rate = RateOf(nation, line);
            var above = rate - line.Neutral;
            growth += above * line.Growth;
            living += above * line.Living;
            literacy += above * line.Literacy;
            radicalism += above * line.Radicalism;
            stability += above * line.Stability;

            if (line.ResentedAbove <= 0f) continue;
            var resented = MathF.Max(0f, rate - line.ResentedAbove);
            if (resented <= 0f) continue;
            living += resented * line.ResentedLiving;
            radicalism += resented * line.ResentedRadicalism;
        }

        return new BudgetOutcome(revenue, taxation, expenses,
            growth, living, literacy, radicalism, stability);
    }

    /// <summary>
    /// Write the week out line by line, for the one country somebody is looking at.
    ///
    /// Only ever called for a human's country. Composing seventeen labels for two hundred
    /// and sixty cabinets nobody is reading was, when the budget had eight lines, fifty
    /// thousand sentences a simulated year written for an empty room.
    /// </summary>
    public static void Itemise(Nation nation, float weeklyOutput, bool atWar)
    {
        if (nation is null) return;
        FillSlots(nation, atWar);
        nation.Budget.Begin(Loc.Get("Weekly balance"), "money");
        foreach (var line in Lines)
        {
            var amount = AmountFromSlots(nation, line, weeklyOutput);
            var sign = line.Side == BudgetSide.Expense ? -1f : 1f;
            nation.Budget.Add(Label(nation, line), sign * amount);
        }
    }

    /// <summary>
    /// What a line is called on the screen, with its rate in it when it has one a
    /// government chose. Both halves come out of the localisation table, so translating
    /// the budget is translating rows rather than editing the tick.
    /// </summary>
    public static string Label(Nation nation, BudgetLine line)
    {
        var name = Loc.Get(line.Name);
        if (!line.PlayerSet) return name;
        return Loc.Format("{0} at {1}", name, RateOf(nation, line).ToString("P0",
            System.Globalization.CultureInfo.CurrentCulture));
    }
}
