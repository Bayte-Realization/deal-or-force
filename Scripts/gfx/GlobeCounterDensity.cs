using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private ulong _nextSurfaceDensityMs;

    // Region merge on the globe (23 November 2026, round 2; the rule is GlobeCounterMerge).
    private bool _globeCountersMerged;
    private ulong _nextGlobeMergeReplanMs;
    private readonly Simulation.GlobeCounterMerge.Planner _globeMergePlanner = new();
    private readonly List<ArmyStackMarker> _globeMergeMarkers = new();
    private readonly List<Simulation.GlobeCounterMerge.Counter> _globeMergeCounters = new();
    private readonly HashSet<int> _globeFrontRegions = new();
    private readonly Dictionary<ArmyStackMarker, List<ArmyStackMarker>> _globeMerges = new();
    private readonly Stack<List<ArmyStackMarker>> _globeMergeSpare = new();
    private int[] _globeMergeHosts = Array.Empty<int>();

    private void UpdateSurfaceCounterDensity()
    {
        if (_flatMap || !IsInstanceValid(_armyRoot)) return;
        // Past the strategic distance one country's counters in one region are drawn as one, as
        // the flat map does by screen overlap. A dead band round the threshold keeps a zoom that
        // stops on it from flickering between the two.
        var threshold = Math.Clamp(Ui.Metric("map.globe_counter_merge_distance", Simulation.GlobeCounterMerge.DefaultDistance), 5.5f, 20f);
        var merged = Simulation.GlobeCounterMerge.Strategic(_globeCountersMerged, _distance, threshold);
        var now = Time.GetTicksMsec();
        if (merged != _globeCountersMerged)
        {
            _globeCountersMerged = merged;
            _counterDensityDirty = true;
            _nextSurfaceDensityMs = 0;
        }
        // A battle starts and an order is given without a counter moving: while merged, the
        // plan is looked at again twice a second.
        if (merged && now >= _nextGlobeMergeReplanMs) _counterDensityDirty = true;
        if (!_counterDensityDirty) return;
        if (now < _nextSurfaceDensityMs) return;
        _nextSurfaceDensityMs = now + 100;
        _nextGlobeMergeReplanMs = now + 500;
        _counterDensityDirty = false;
        // The province centre wins over screen-space crowding. Never combine neighbouring
        // provinces or retain a collision-avoidance surface slot; what merges is a country's own
        // counters within one region, and only at the strategic distance.
        _globeMergeMarkers.Clear();
        foreach (var marker in _armyStacks.Values)
        {
            if (marker.Node is null || !IsInstanceValid(marker.Node) || !marker.Node.Visible) continue;
            var pos = ArmyMarkerPosition(marker);
            if (marker.TargetPosition != pos)
            {
                marker.TargetPosition = pos;
                OrientSurfaceArmyCard(marker);
            }
            SizeSurfaceArmyCounter(marker);
            _globeMergeMarkers.Add(marker);
        }
        foreach (var list in _globeMerges.Values) { list.Clear(); _globeMergeSpare.Push(list); }
        _globeMerges.Clear();
        if (merged && !PlanGlobeCounterMerge())
        {
            // The campaign is busy writing: keep what is drawn and try again next pass.
            _counterDensityDirty = true;
            return;
        }
        if (merged)
        {
            for (var i = 0; i < _globeMergeMarkers.Count; i++)
            {
                var host = _globeMergeHosts[i];
                if (host == i) continue;
                var hostMarker = _globeMergeMarkers[host];
                if (!_globeMerges.TryGetValue(hostMarker, out var list))
                    _globeMerges[hostMarker] = list = _globeMergeSpare.Count > 0 ? _globeMergeSpare.Pop() : new List<ArmyStackMarker>();
                list.Add(_globeMergeMarkers[i]);
            }
        }
        for (var i = 0; i < _globeMergeMarkers.Count; i++)
        {
            if (merged && _globeMergeHosts[i] != i) continue;
            var marker = _globeMergeMarkers[i];
            _globeMerges.TryGetValue(marker, out var takenIn);
            RefreshProvinceCounterDisplay(marker, false, takenIn);
            if (takenIn is null) continue;
            // The counters taken in show nothing; a click on where one stood picks the whole group.
            var ids = marker.Node.GetMeta("display_army_ids");
            foreach (var taken in takenIn)
            {
                taken.Plate.Visible = false;
                taken.Count.Visible = false;
                if (taken.ConditionBars is not null) taken.ConditionBars.Visible = false;
                taken.DisplayFingerprint = 0;
                taken.DisplayStack.Clear();
                taken.DisplayStack.AddRange(marker.DisplayStack);
                taken.Node.SetMeta("display_army_ids", ids);
            }
        }
    }

    /// <summary>Which counter each visible counter is drawn in, into <see cref="_globeMergeHosts"/>. False when the campaign could not be read.</summary>
    private bool PlanGlobeCounterMerge()
    {
        var campaign = _campaign;
        if (campaign is null) return false;
        if (!campaign.TryEnterRead(0, true)) return false;
        try
        {
            var regionOf = campaign.ProvinceMap?.ProvinceToState;
            _globeFrontRegions.Clear();
            if (regionOf is not null)
                foreach (var war in campaign.Wars)
                {
                    if (war.Ended) continue;
                    foreach (var province in war.AttackerFront)
                        if (province > 0 && province < regionOf.Length) _globeFrontRegions.Add(regionOf[province]);
                    foreach (var province in war.DefenderFront)
                        if (province > 0 && province < regionOf.Length) _globeFrontRegions.Add(regionOf[province]);
                }
            _globeMergeCounters.Clear();
            foreach (var marker in _globeMergeMarkers)
            {
                var divisions = 0f;
                bool inBattle = false, moving = false, selected = false;
                var region = -1;
                foreach (var army in marker.Stack)
                {
                    divisions += army.Divisions;
                    inBattle |= army.InBattle;
                    moving |= army.IsMoving;
                    selected |= _selectedArmyIds.Contains(army.Id);
                    if (region < 0)
                        region = army.StateMapIndex >= 0 ? army.StateMapIndex
                            : regionOf is not null && army.ProvinceId > 0 && army.ProvinceId < regionOf.Length ? regionOf[army.ProvinceId] : -1;
                }
                _globeMergeCounters.Add(new Simulation.GlobeCounterMerge.Counter(marker.NationId, region, divisions, inBattle, moving, selected));
            }
            if (_globeMergeHosts.Length < _globeMergeCounters.Count)
                _globeMergeHosts = new int[Math.Max(64, _globeMergeCounters.Count * 2)];
            _globeMergePlanner.Plan(_globeMergeCounters, true, _globeFrontRegions,
                (a, b) => Simulation.CampaignDiplomacy.AtWar(campaign, a, b), _globeMergeHosts);
            return true;
        }
        finally { campaign.ExitRead(); }
    }
}
