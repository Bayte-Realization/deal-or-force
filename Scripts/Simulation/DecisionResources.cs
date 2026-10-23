using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

public readonly record struct DecisionResourceCost(string Currency, float Amount);

/// <summary>Explicit, non-borrowable decision prices. The UI, cabinet and command
/// read the same immutable catalogue vector; balances are the existing saved fields.</summary>
public static class DecisionResources
{
    private static readonly string[] Order = { "army_experience", "air_experience", "naval_experience", "political_points" };
    public static IReadOnlyList<DecisionResourceCost> Of(DecisionDefinition decision) =>
        decision.ResourceCosts ?? Array.Empty<DecisionResourceCost>();

    public static bool Parse(JsonElement body, string origin, List<string> errors,
        out IReadOnlyList<DecisionResourceCost> costs)
    {
        costs = null;
        if (body.ValueKind != JsonValueKind.Object)
        { errors?.Add(origin + ": expected an object of resource prices."); return false; }
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var entry in body.EnumerateObject())
        {
            var key = entry.Name.ToLowerInvariant();
            if (Array.IndexOf(Order, key) < 0 || values.ContainsKey(key) ||
                entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetSingle(out var amount) ||
                !float.IsFinite(amount) || amount < 0 || amount > 100000)
            { errors?.Add(origin + ": invalid, duplicate or unknown resource price '" + entry.Name + "'."); return false; }
            values[key] = amount;
        }
        var ordered = new List<DecisionResourceCost>();
        foreach (var key in Order)
            if (values.TryGetValue(key, out var amount) && amount > 0) ordered.Add(new(key, amount));
        costs = ordered.AsReadOnly();
        return true;
    }

    public static float Balance(Nation nation, string currency) => nation is null ? 0 : currency switch
    {
        "army_experience" => nation.ArmyExperience,
        "air_experience" => nation.AirExperience,
        "naval_experience" => nation.NavalExperience,
        "political_points" => nation.PoliticalPoints,
        _ => float.NaN
    };

    public static string Name(string currency) => Loc.Get(currency switch
    {
        "army_experience" => "army experience",
        "air_experience" => "air experience",
        "naval_experience" => "naval experience",
        "political_points" => "political credit",
        _ => currency
    });

    public static bool CanAfford(Nation nation, DecisionDefinition decision, bool keepAiReserve = false)
    {
        foreach (var cost in Of(decision))
        {
            var balance = Balance(nation, cost.Currency);
            if (!float.IsFinite(balance) || balance < cost.Amount) return false;
        }
        if (keepAiReserve && decision.AiResourceReserve is { } reserves)
            foreach (var item in reserves)
            {
                var payment = 0f;
                foreach (var cost in Of(decision))
                    if (cost.Currency == item.Currency) { payment = cost.Amount; break; }
                var balance = Balance(nation, item.Currency);
                if (!float.IsFinite(balance) || balance < payment + item.Amount) return false;
            }
        return nation is not null;
    }

    public static string Refusal(Nation nation, DecisionDefinition decision)
    {
        foreach (var cost in Of(decision))
        {
            var balance = Balance(nation, cost.Currency);
            if (!float.IsFinite(balance) || balance < cost.Amount)
                return Loc.Format("Insufficient {0}: need {1}, available {2}.", Name(cost.Currency),
                    Points(cost.Amount), Points(balance));
        }
        return "";
    }

    internal static void Spend(Nation nation, DecisionDefinition decision)
    {
        foreach (var cost in Of(decision))
        {
            var after = MathF.Max(0, Balance(nation, cost.Currency) - cost.Amount);
            switch (cost.Currency)
            {
                case "army_experience": nation.ArmyExperience = after; break;
                case "air_experience": nation.AirExperience = after; break;
                case "naval_experience": nation.NavalExperience = after; break;
                case "political_points": nation.PoliticalPoints = after; break;
            }
        }
    }

    public static string PriceText(Nation nation, DecisionDefinition decision)
    {
        var prices = new List<string>();
        foreach (var cost in Of(decision))
            prices.Add(Loc.Format("{0}: {1} (available {2})", Name(cost.Currency),
                Points(cost.Amount), Points(Balance(nation, cost.Currency))));
        return string.Join(" · ", prices);
    }

    private static string Points(float value) => Fmt.Number(value,
        MathF.Abs(value) > 0 && MathF.Abs(value) < .000001f ? "0.######E+0" : "0.######");
}
