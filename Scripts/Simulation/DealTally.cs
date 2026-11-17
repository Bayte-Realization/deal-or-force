using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A running count of the deals and agreements the world asked for and how each was answered.
///
/// Not campaign state and not saved, the same as <see cref="AiStatecraft.Tally"/>: it exists so
/// "trade agreements are one-sided" can be answered with how often each kind of offer is put,
/// by whom, and how often it is signed - which the chronicle, a 2048-line ring buffer, cannot
/// answer over five years. Nothing in the simulation reads it, so it moves no fingerprint.
/// Concurrent because agreements are asked from more than one pass, and a count that can lose
/// increments under load would make a measurement tool lie quietly.
/// </summary>
public static class DealTally
{
    private static readonly ConcurrentDictionary<string, int> Counts = new();

    public static void Count(string key) => Counts.AddOrUpdate(key, 1, static (_, n) => n + 1);

    public static int Get(string key) => Counts.GetValueOrDefault(key);

    public static IReadOnlyList<KeyValuePair<string, int>> Snapshot() =>
        Counts.OrderBy(entry => entry.Key, System.StringComparer.Ordinal).ToList();

    public static void Reset() => Counts.Clear();

    /// <summary>"human" or "ai": which kind of seat a country answers from, for the parity columns.</summary>
    public static string Seat(Nation nation) => nation?.IsHuman == true ? "human" : "ai";

    /// <summary>The kind a deal table belongs to, by its terms (common/deals), or "other".</summary>
    public static string KindOf(Ultimatum.Deal deal) => DealAnswerKinds.Classify(deal)?.Id ?? "other";
}
