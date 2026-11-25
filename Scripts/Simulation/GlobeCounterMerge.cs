using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Which army counters on the globe are drawn as one (23 November 2026). Presentation only: it
/// never touches an army, a stack, a province or a save, and the same counters give the same
/// answer every time.
///
/// At the strategic distance one country's counters in one region become one counter with all
/// their divisions - "Gyeonggi, 34 divisions" - instead of a dozen plates piled round Seoul.
/// The key is the region, not the screen: on a sphere the projection slides as the globe turns,
/// a region stays where it is and the player knows what it is. What the player needs to see on
/// its own never merges: a counter in battle, one with a move order, one the player picked, and
/// any counter in a region on an active front or with an enemy at war in it, so a held line
/// stays a line of counters.
/// </summary>
public static class GlobeCounterMerge
{
    /// <summary>The camera distance past which counters merge (V1, the campaign's opening camera, is 8.4).</summary>
    public const float DefaultDistance = 7.0f;
    /// <summary>Half the dead band round it, so a zoom that stops on the threshold does not flicker.</summary>
    public const float DefaultBand = 0.3f;

    /// <summary>One counter as the rule sees it. <see cref="Region"/> is the region's map index, -1 for none.</summary>
    public readonly record struct Counter(int NationId, int Region, float Divisions, bool InBattle, bool Moving, bool Selected);

    /// <summary>Whether counters are merged at this distance, given whether they were a moment ago.</summary>
    public static bool Strategic(bool wasStrategic, float distance, float threshold = DefaultDistance, float band = DefaultBand) =>
        wasStrategic ? distance > threshold - band : distance > threshold + band;

    /// <summary>Reusable working sets, so a pass allocates nothing once it has run.</summary>
    public sealed class Planner
    {
        private readonly HashSet<(int Nation, int Region)> _held = new();
        private readonly Dictionary<(int Nation, int Region), int> _hosts = new();
        private readonly Dictionary<int, List<int>> _byRegion = new();
        private readonly Stack<List<int>> _spare = new();

        /// <summary>The (country, region) pairs whose counters stay apart after the last <see cref="Plan"/>.</summary>
        public IReadOnlyCollection<(int Nation, int Region)> Held => _held;

        /// <summary>
        /// Fills <paramref name="hostOf"/>: for every counter, the index of the counter it is drawn
        /// in - itself when it stands alone or takes others in. The host of a merged group is its
        /// largest counter (the first on a tie), so the group stands where most of it is.
        /// </summary>
        /// <param name="frontRegions">Regions holding a province of an active front, of any war.</param>
        /// <param name="atWar">Whether two countries are at war with each other.</param>
        public void Plan(IReadOnlyList<Counter> counters, bool strategic, IReadOnlySet<int> frontRegions,
            Func<int, int, bool> atWar, int[] hostOf)
        {
            if (hostOf.Length < counters.Count) throw new ArgumentException("hostOf is shorter than the counters", nameof(hostOf));
            for (var i = 0; i < counters.Count; i++) hostOf[i] = i;
            _held.Clear();
            _hosts.Clear();
            if (!strategic) return;
            HoldContestedRegions(counters, frontRegions, atWar);
            for (var i = 0; i < counters.Count; i++)
            {
                var counter = counters[i];
                if (!Free(counter)) continue;
                var key = (counter.NationId, counter.Region);
                if (!_hosts.TryGetValue(key, out var host) || counters[host].Divisions < counter.Divisions) _hosts[key] = i;
            }
            for (var i = 0; i < counters.Count; i++)
            {
                var counter = counters[i];
                if (Free(counter)) hostOf[i] = _hosts[(counter.NationId, counter.Region)];
            }
        }

        private bool Free(Counter counter) =>
            !counter.InBattle && !counter.Moving && !counter.Selected && counter.Region >= 0 &&
            !_held.Contains((counter.NationId, counter.Region));

        /// <summary>A front runs through the region, or an enemy at war stands in it: everyone there stays apart.</summary>
        private void HoldContestedRegions(IReadOnlyList<Counter> counters, IReadOnlySet<int> frontRegions, Func<int, int, bool> atWar)
        {
            foreach (var list in _byRegion.Values) { list.Clear(); _spare.Push(list); }
            _byRegion.Clear();
            for (var i = 0; i < counters.Count; i++)
            {
                var counter = counters[i];
                if (counter.Region < 0) continue;
                if (frontRegions is not null && frontRegions.Contains(counter.Region))
                    _held.Add((counter.NationId, counter.Region));
                if (!_byRegion.TryGetValue(counter.Region, out var members))
                    _byRegion[counter.Region] = members = _spare.Count > 0 ? _spare.Pop() : new List<int>();
                if (!members.Contains(counter.NationId)) members.Add(counter.NationId);
            }
            if (atWar is null) return;
            foreach (var (region, nations) in _byRegion)
            {
                if (nations.Count < 2) continue;
                for (var a = 0; a < nations.Count; a++)
                for (var b = a + 1; b < nations.Count; b++)
                {
                    if (!atWar(nations[a], nations[b])) continue;
                    _held.Add((nations[a], region));
                    _held.Add((nations[b], region));
                }
            }
        }
    }
}
