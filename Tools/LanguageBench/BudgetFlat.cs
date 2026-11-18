using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using DealOrForce.Simulation;

namespace DealOrForce.Tools.LanguageBench;

/// <summary>
/// <see cref="BudgetCore.Evaluate"/> for every country, as a pure function over flat arrays, so
/// every language is handed the same numbers. The game's version reads Nation objects, a
/// dictionary of policy rates and a List of BudgetLine objects; turning those into these arrays
/// is <see cref="Pack"/>, and that cost is reported on its own as part of the crossing - a flat
/// kernel in any language, C# included, needs it unless the game kept the data flat.
///
/// Arithmetic is the game's, operation for operation and in the same order, in 32-bit floats:
/// the outcome of every port is compared bit for bit with the game's own Evaluate.
/// </summary>
internal sealed class BudgetInput
{
    public const int SlotCount = 8;
    public const int LineFloatColumns = 12;   // yield, loss_yield, constant, neutral, growth, living, literacy,
                                              // radicalism, stability, resented_above, resented_living, resented_radicalism
    public const int LineIntColumns = 4;      // side (0 revenue, 1 expense), charge (0 output, 1 debt, 2 tariffs,
                                              // 3 trade balance), player_set, is_debt_interest
    public const int NationColumns = 8;       // weekly_output, debt_basis, tariffs, trade_balance, public_debt,
                                              // debt_spread_annual, tax_efficiency, debt_mechanic_active (0/1)
    public const int OutputColumns = 8;       // revenue, taxation, expenses, growth, living, literacy, radicalism, stability

    public int Nations;
    public int Lines;
    public float TaxTolerance;
    public float TaxEvasion;
    public float[] LineFloats;
    public int[] LineInts;
    public int[] FactorOffsets;
    public int[] FactorSlots;
    public float[] FactorWeights;
    public float[] Slots;
    public float[] NationFloats;
    public float[] Rates;

    private static readonly FieldInfo ToleranceField = typeof(BudgetCore).GetField("TaxTolerance", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("BudgetCore.TaxTolerance");
    private static readonly FieldInfo EvasionField = typeof(BudgetCore).GetField("TaxEvasion", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("BudgetCore.TaxEvasion");

    /// <summary>The catalogue half, done once: it changes when a mod loads, not per week.</summary>
    public static BudgetInput Catalogue(int nations)
    {
        var lines = BudgetCore.All;
        var input = new BudgetInput
        {
            Nations = nations,
            Lines = lines.Count,
            TaxTolerance = (float)ToleranceField.GetValue(null),
            TaxEvasion = (float)EvasionField.GetValue(null),
            LineFloats = new float[lines.Count * LineFloatColumns],
            LineInts = new int[lines.Count * LineIntColumns],
            FactorOffsets = new int[lines.Count + 1],
            Slots = new float[nations * SlotCount],
            NationFloats = new float[nations * NationColumns],
            Rates = new float[nations * lines.Count],
        };
        var slots = new List<int>();
        var weights = new List<float>();
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            var f = l * LineFloatColumns;
            input.LineFloats[f + 0] = line.Yield;
            input.LineFloats[f + 1] = line.LossYield;
            input.LineFloats[f + 2] = line.Constant;
            input.LineFloats[f + 3] = line.Neutral;
            input.LineFloats[f + 4] = line.Growth;
            input.LineFloats[f + 5] = line.Living;
            input.LineFloats[f + 6] = line.Literacy;
            input.LineFloats[f + 7] = line.Radicalism;
            input.LineFloats[f + 8] = line.Stability;
            input.LineFloats[f + 9] = line.ResentedAbove;
            input.LineFloats[f + 10] = line.ResentedLiving;
            input.LineFloats[f + 11] = line.ResentedRadicalism;
            var i = l * LineIntColumns;
            input.LineInts[i + 0] = line.Side == BudgetSide.Revenue ? 0 : 1;
            input.LineInts[i + 1] = (int)line.Charge;
            input.LineInts[i + 2] = line.PlayerSet ? 1 : 0;
            input.LineInts[i + 3] = line.Id == "debt_interest" ? 1 : 0;
            for (var k = 0; k < line.Factors.Length; k++) { slots.Add(line.Factors[k]); weights.Add(line.Weights[k]); }
            input.FactorOffsets[l + 1] = slots.Count;
        }
        input.FactorSlots = slots.ToArray();
        input.FactorWeights = weights.ToArray();
        return input;
    }

    /// <summary>
    /// The per-week half: what <c>FillSlots</c>, <c>RateOf</c>, <c>BasisOf</c> and the modifier
    /// lookup read from each Nation, written into the flat arrays. Timed as part of the crossing.
    /// </summary>
    public void Pack(Nation[] nations, float[] weeklyOutput, bool[] atWar)
    {
        var lines = BudgetCore.All;
        for (var c = 0; c < nations.Length; c++)
        {
            var nation = nations[c];
            var s = c * SlotCount;
            Slots[s + 0] = nation.Stability;
            Slots[s + 1] = nation.Literacy;
            Slots[s + 2] = nation.StandardOfLiving;
            Slots[s + 3] = nation.Urbanisation > 0f ? nation.Urbanisation : 0.55f;
            Slots[s + 4] = nation.EmployedShare;
            Slots[s + 5] = atWar[c] ? 1f : 0f;
            Slots[s + 6] = nation.ShortageIndex;
            Slots[s + 7] = nation.Inflation;
            var n = c * NationColumns;
            NationFloats[n + 0] = weeklyOutput[c];
            NationFloats[n + 1] = SovereignFinance.Outstanding(nation);
            NationFloats[n + 2] = nation.WeeklyTariffIncome;
            NationFloats[n + 3] = nation.WeeklyTradeBalance;
            NationFloats[n + 4] = nation.PublicDebt;
            NationFloats[n + 5] = nation.DebtSpreadAnnual;
            NationFloats[n + 6] = NationalModifiers.Factor(nation, "tax_efficiency");
            NationFloats[n + 7] = nation.DebtMechanicActive ? 1f : 0f;
            var r = c * Lines;
            for (var l = 0; l < lines.Count; l++) Rates[r + l] = BudgetCore.RateOf(nation, lines[l]);
        }
    }
}

internal static class BudgetFlat
{
    private const int S = BudgetInput.SlotCount, LF = BudgetInput.LineFloatColumns, LI = BudgetInput.LineIntColumns,
        NC = BudgetInput.NationColumns, OC = BudgetInput.OutputColumns;

    /// <summary>The game's Evaluate, called as it is, for every country: the reference.</summary>
    public static void AsWritten(Nation[] nations, float[] weeklyOutput, bool[] atWar, float[] output)
    {
        for (var c = 0; c < nations.Length; c++)
        {
            var outcome = BudgetCore.Evaluate(nations[c], weeklyOutput[c], atWar[c]);
            var o = c * OC;
            output[o + 0] = outcome.Revenue; output[o + 1] = outcome.Taxation; output[o + 2] = outcome.Expenses;
            output[o + 3] = outcome.Growth; output[o + 4] = outcome.Living; output[o + 5] = outcome.Literacy;
            output[o + 6] = outcome.Radicalism; output[o + 7] = outcome.Stability;
        }
    }

    private static float ScaleOf(BudgetInput b, int line, int slotBase)
    {
        var scale = b.LineFloats[line * LF + 2];
        for (var k = b.FactorOffsets[line]; k < b.FactorOffsets[line + 1]; k++)
            scale += b.FactorWeights[k] * b.Slots[slotBase + b.FactorSlots[k]];
        return scale;
    }

    /// <summary>The flat port, line by line: what the native and F# ports translate.</summary>
    public static void Evaluate(BudgetInput b, float[] output)
    {
        for (var c = 0; c < b.Nations; c++) EvaluateOne(b, c, output);
    }

    private static void EvaluateOne(BudgetInput b, int c, float[] output)
    {
        var slotBase = c * S;
        var n = c * NC;
        var rateBase = c * b.Lines;
        var weeklyOutput = b.NationFloats[n + 0];

        float compliance;
        if (weeklyOutput <= 0f || b.TaxEvasion <= 0f) compliance = 1f;
        else
        {
            var burden = 0f;
            for (var line = 0; line < b.Lines; line++)
            {
                if (!(b.LineInts[line * LI + 0] == 0 && b.LineInts[line * LI + 1] == 0)) continue;
                var rate = b.Rates[rateBase + line];
                burden += rate * b.LineFloats[line * LF + 0] * ScaleOf(b, line, slotBase);
            }
            compliance = 1f / (1f + b.TaxEvasion * MathF.Max(0f, burden - b.TaxTolerance));
        }

        float revenue = 0f, taxation = 0f, expenses = 0f;
        float growth = 0f, living = 0f, literacy = 0f, radicalism = 0f, stability = 0f;
        for (var line = 0; line < b.Lines; line++)
        {
            var f = line * LF;
            var side = b.LineInts[line * LI + 0];
            var charge = b.LineInts[line * LI + 1];
            var basis = charge switch { 1 => b.NationFloats[n + 1], 2 => b.NationFloats[n + 2], 3 => b.NationFloats[n + 3], _ => weeklyOutput };
            var rate = b.Rates[rateBase + line];
            var lossYield = b.LineFloats[f + 1];
            var yield = !float.IsNaN(lossYield) && basis < 0f ? lossYield : b.LineFloats[f + 0];
            var amount = basis * rate * yield * ScaleOf(b, line, slotBase);
            if (b.NationFloats[n + 7] != 0f && b.LineInts[line * LI + 3] != 0)
                amount += b.NationFloats[n + 4] * b.NationFloats[n + 5] / 52f;
            if (side == 0 && charge == 0) amount *= compliance * b.NationFloats[n + 6];
            amount = charge == 3 ? amount : MathF.Max(0f, amount);

            if (side == 0)
            {
                revenue += amount;
                if (charge is 0 or 1) taxation += amount;
            }
            else expenses += amount;

            if (b.LineInts[line * LI + 2] == 0) continue;
            var above = rate - b.LineFloats[f + 3];
            growth += above * b.LineFloats[f + 4];
            living += above * b.LineFloats[f + 5];
            literacy += above * b.LineFloats[f + 6];
            radicalism += above * b.LineFloats[f + 7];
            stability += above * b.LineFloats[f + 8];

            var resentedAbove = b.LineFloats[f + 9];
            if (resentedAbove <= 0f) continue;
            var resented = MathF.Max(0f, rate - resentedAbove);
            if (resented <= 0f) continue;
            living += resented * b.LineFloats[f + 10];
            radicalism += resented * b.LineFloats[f + 11];
        }
        var o = c * OC;
        output[o + 0] = revenue; output[o + 1] = taxation; output[o + 2] = expenses;
        output[o + 3] = growth; output[o + 4] = living; output[o + 5] = literacy;
        output[o + 6] = radicalism; output[o + 7] = stability;
    }

    /// <summary>
    /// Tuned: spans instead of array fields, each line's scale made once per country rather than
    /// twice (deterministic, so the same bits), and the line table walked as locals.
    /// </summary>
    public static void EvaluateTuned(BudgetInput b, float[] output, float[] scratch)
    {
        ReadOnlySpan<float> lineFloats = b.LineFloats;
        ReadOnlySpan<int> lineInts = b.LineInts, offsets = b.FactorOffsets, factorSlots = b.FactorSlots;
        ReadOnlySpan<float> factorWeights = b.FactorWeights;
        var lines = b.Lines;
        var scale = scratch.AsSpan(0, lines);
        for (var c = 0; c < b.Nations; c++)
        {
            var slots = b.Slots.AsSpan(c * S, S);
            var nation = b.NationFloats.AsSpan(c * NC, NC);
            var rates = b.Rates.AsSpan(c * lines, lines);
            for (var line = 0; line < lines; line++)
            {
                var s = lineFloats[line * LF + 2];
                for (var k = offsets[line]; k < offsets[line + 1]; k++) s += factorWeights[k] * slots[factorSlots[k]];
                scale[line] = s;
            }
            var weeklyOutput = nation[0];
            var compliance = 1f;
            if (weeklyOutput > 0f && b.TaxEvasion > 0f)
            {
                var burden = 0f;
                for (var line = 0; line < lines; line++)
                    if (lineInts[line * LI] == 0 && lineInts[line * LI + 1] == 0)
                        burden += rates[line] * lineFloats[line * LF] * scale[line];
                compliance = 1f / (1f + b.TaxEvasion * MathF.Max(0f, burden - b.TaxTolerance));
            }
            var levyFactor = compliance * nation[6];
            var debtExtra = nation[7] != 0f ? nation[4] * nation[5] / 52f : 0f;
            float revenue = 0f, taxation = 0f, expenses = 0f;
            float growth = 0f, living = 0f, literacy = 0f, radicalism = 0f, stability = 0f;
            for (var line = 0; line < lines; line++)
            {
                var f = lineFloats.Slice(line * LF, LF);
                var side = lineInts[line * LI];
                var charge = lineInts[line * LI + 1];
                var basis = charge == 0 ? weeklyOutput : nation[charge];
                var rate = rates[line];
                var yield = !float.IsNaN(f[1]) && basis < 0f ? f[1] : f[0];
                var amount = basis * rate * yield * scale[line];
                if (nation[7] != 0f && lineInts[line * LI + 3] != 0) amount += debtExtra;
                if (side == 0 && charge == 0) amount *= levyFactor;
                if (charge != 3) amount = MathF.Max(0f, amount);
                if (side == 0) { revenue += amount; if (charge <= 1) taxation += amount; }
                else expenses += amount;
                if (lineInts[line * LI + 2] == 0) continue;
                var above = rate - f[3];
                growth += above * f[4]; living += above * f[5]; literacy += above * f[6];
                radicalism += above * f[7]; stability += above * f[8];
                if (f[9] <= 0f) continue;
                var resented = MathF.Max(0f, rate - f[9]);
                if (resented <= 0f) continue;
                living += resented * f[10]; radicalism += resented * f[11];
            }
            var o = output.AsSpan(c * OC, OC);
            o[0] = revenue; o[1] = taxation; o[2] = expenses; o[3] = growth;
            o[4] = living; o[5] = literacy; o[6] = radicalism; o[7] = stability;
        }
    }

    /// <summary>The flat port with countries spread over threads: a pure kernel, so allowed.</summary>
    public static void EvaluateParallel(BudgetInput b, float[] output) =>
        Parallel.For(0, b.Nations, c => EvaluateOne(b, c, output));
}
