using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Read-only audit of successful decision_taken facts, not UI intentions.
/// Ambiguous modded names are not attributed. Gaps in bounded history are reported.</summary>
sealed class DecisionResourceTrace
{
    readonly Dictionary<string, DecisionDefinition> _byName = DecisionCatalog.All
        .GroupBy(d => d.Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    readonly Dictionary<string, double> _spent = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _actions = new(StringComparer.Ordinal);
    readonly HashSet<int> _countries = new();
    long _seen;
    long _missing;
    int _invalid;

    public DecisionResourceTrace(Campaign c) => _seen = c.Chronicle.Entries.LastOrDefault()?.Sequence ?? 0;

    public void Observe(Campaign c)
    {
        var rows = c.Chronicle.Entries;
        if (rows.Count > 0 && rows[0].Sequence > _seen + 1) _missing += rows[0].Sequence - _seen - 1;
        foreach (var row in rows)
        {
            if (row.Sequence <= _seen) continue;
            _seen = row.Sequence;
            if (row.Kind != "decision_taken" || !_byName.TryGetValue(row.Subject, out var decision) ||
                c.NationById(row.ActorId)?.IsHuman == true) continue;
            var costs = DecisionResources.Of(decision);
            if (costs.Count == 0) continue;
            _countries.Add(row.ActorId);
            _actions[decision.Id] = _actions.GetValueOrDefault(decision.Id) + 1;
            foreach (var cost in costs) _spent[cost.Currency] = _spent.GetValueOrDefault(cost.Currency) + cost.Amount;
        }
        foreach (var nation in c.Nations)
            if (!float.IsFinite(nation.ArmyExperience) || nation.ArmyExperience < 0 ||
                !float.IsFinite(nation.AirExperience) || nation.AirExperience < 0 ||
                !float.IsFinite(nation.NavalExperience) || nation.NavalExperience < 0 ||
                !float.IsFinite(nation.PoliticalPoints) || nation.PoliticalPoints < 0) _invalid++;
    }

    public int Print(Campaign c)
    {
        Console.WriteLine("RESOURCE_TRACE successful paid decisions by AI: countries=" + _countries.Count +
            " purchases=" + _actions.Values.Sum() + " history-gap=" + _missing + " invalid-wallet-samples=" + _invalid);
        foreach (var row in _spent.OrderBy(p => p.Key)) Console.WriteLine("RESOURCE_TRACE spent " + row.Key + "=" + row.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        foreach (var row in _actions.OrderByDescending(p => p.Value)) Console.WriteLine("RESOURCE_TRACE action " + row.Key + "=" + row.Value);
        foreach (var id in new[] { "logistics_staff_programme", "joint_air_staff_course", "naval_rotation_programme", "civil_service_compact" })
            Console.WriteLine("RESOURCE_TRACE programme " + id + "=" + _actions.GetValueOrDefault(id));
        var naval = DecisionCatalog.Find("naval_rotation_programme");
        if (naval is { } programme)
        {
            var ready = c.Nations.Where(n => !n.IsHuman &&
                ScriptedValues.TryRead(c, n, "available_fleet_hulls", out var hulls) && hulls > 0).ToArray();
            Console.WriteLine("RESOURCE_TRACE final ready navies=" + ready.Length +
                " max-naval-wallet=" + (ready.Length == 0 ? "none" : ready.Max(n => n.NavalExperience).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)) +
                " affordable-with-reserve=" + ready.Count(n => DecisionResources.CanAfford(n, programme, true)));
        }
        Console.WriteLine("RESOURCE_TRACE fingerprint=" + CampaignFingerprint.Of(c).ToString("X16"));
        return _invalid > 0 || _missing > 0 ? 1 : 0;
    }
}
