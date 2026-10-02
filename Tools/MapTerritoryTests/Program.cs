using System;
using System.Linq;
using DealOrForce.Simulation;
using Map = Globality.EarthData;

var campaign = Campaign.CreateBootstrap(20260830);
TerritorialTopology.Apply(campaign, Map.Control, Map.States, Map.Width, Map.Height);
if (!MapRasterCatalog.TryBuildProvinceTopology(Map.States, Map.Control, Map.Width, Map.Height, out var topology, out var message))
    throw new Exception(message);
Console.WriteLine(message);
var georgia = campaign.NationByTag("GEO").MapIndex;
var abkhazia = campaign.NationByTag("ABK").MapIndex;
var previousOwners = (ushort[])topology.ProvinceToNation.Clone();
var ossetia = campaign.NationByTag("OST").MapIndex;
var russia = campaign.NationByTag("RUS").MapIndex;
int ProvinceAt(double longitude, double latitude) => topology.CellToProvince[
    (int)((90 - latitude) / 180 * Map.Height) * Map.Width + (int)((longitude + 180) / 360 * Map.Width)];
Check(topology.ProvinceToNation[ProvinceAt(43.96, 42.25)] == ossetia, "Tskhinvali lies inside the corrected South Ossetian contour");
Check(topology.ProvinceToNation[ProvinceAt(44.48, 42.13)] == ossetia, "Akhalgori southeastern lobe is present rather than a circular outline");
Check(topology.ProvinceToNation[ProvinceAt(44.11, 41.98)] == georgia, "Gori stays outside the corrected contour");
var ossetianProvinces = Enumerable.Range(1, topology.ProvinceCount).Where(p => topology.ProvinceToNation[p] == ossetia).ToHashSet();
Check(ossetianProvinces.Count > 0 && ossetianProvinces.Any(p => topology.Neighbors[p].Any(n => topology.ProvinceToNation[n] == russia)), "South Ossetia touches Russia instead of becoming a detached enclave");
var frontier = new System.Collections.Generic.Queue<int>(); var visited = new System.Collections.Generic.HashSet<int>();
frontier.Enqueue(ossetianProvinces.First());
while (frontier.Count > 0)
{
    var p = frontier.Dequeue(); if (!visited.Add(p)) continue;
    foreach (var n in topology.Neighbors[p]) if (ossetianProvinces.Contains(n) && !visited.Contains(n)) frontier.Enqueue(n);
}
Check(visited.Count == ossetianProvinces.Count, "all South Ossetian provinces form one connected territory");
var shapeBefore = (ushort[])Map.States.Clone(); var ownersBefore = (ushort[])Map.Control.Clone();
TerritorialTopology.Apply(campaign, Map.Control, Map.States, Map.Width, Map.Height);
Check(shapeBefore.AsSpan().SequenceEqual(Map.States) && ownersBefore.AsSpan().SequenceEqual(Map.Control), "reapplying the contour repair is idempotent");
Check(topology.ProvinceCount == 38018, "local border repair preserves every shipped province ID and total count");
var coastalPockets = new[] { 18115, 18133 };
foreach (var p in coastalPockets)
{
    var centre = topology.CenterOf(p);
    Check(centre.Longitude > 40.5 && centre.Longitude < 41 && centre.Latitude > 43 && centre.Latitude < 43.2,
        $"province {p} is still the identified coastal pocket, not a renumbered province elsewhere");
    Check(topology.Neighbors[p].Length > 0 && topology.Neighbors[p].All(n => topology.ProvinceToNation[n] == abkhazia),
        $"province {p} has only Abkhazian land neighbours");
}
campaign.AttachProvinceTopology(topology);
var israel = campaign.NationByTag("ISR").MapIndex;
var palestine = campaign.NationByTag("PSE").MapIndex;
var enclave = topology.CenterOf(21648);
Check(enclave.Latitude > 31.1 && enclave.Latitude < 31.3 && enclave.Longitude > 34.1 && enclave.Longitude < 34.4,
    "Gaza correction targets the verified coastal pocket, not West Bank province 21320");
Check(topology.ProvinceToNation[21648] == israel && campaign.ProvinceController[21648] == israel,
    "Gaza coastal pocket follows ISR ownership and control in the 2024 bookmark");
Check(topology.ProvinceToNation[21320] == palestine, "West Bank remains PSE");
var preservedStates = Globality.EarthData.States;
MapRasterCatalog.ResetCatalog();
Check(ReferenceEquals(preservedStates, Globality.EarthData.States), "unchanged source refresh preserves the repaired region grid");
Check(MapRasterCatalog.TryBuildProvinceTopology(Map.States, Map.Control, Map.Width, Map.Height, out _, out _),
    "province compile stamp still matches after a catalog refresh");
foreach (var p in coastalPockets)
{
    Check(topology.ProvinceToNation[p] == abkhazia && campaign.ProvinceController[p] == abkhazia,
        $"province {p} belongs to and is controlled by Abkhazia at campaign start");
    Check(campaign.States.First(s => s.MapIndex == topology.ProvinceToState[p]).NationMapIndex == abkhazia,
        $"province {p} region and country agree after reconciliation");
}
var otherGeorgia = Enumerable.Range(1, topology.ProvinceCount).Where(p => previousOwners[p] == georgia && !coastalPockets.Contains(p)).ToArray();
Check(otherGeorgia.Length > 0 && otherGeorgia.All(p => topology.ProvinceToNation[p] == georgia), "other Georgian territory remains Georgian");
Check(topology.ProvinceToNation[18086] == georgia && topology.ProvinceToNation[18333] == georgia,
    "nearby genuine Georgian coastal and eastern regions are not taken");
var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(campaign, "abkhazia-pockets"), "abkhazia-pockets");
Check(saved.Success, "corrected campaign snapshot loads");
saved.Campaign.AttachProvinceTopology(topology);
Check(coastalPockets.All(p => topology.ProvinceToNation[p] == abkhazia && saved.Campaign.ProvinceController[p] == abkhazia),
    "corrected ownership survives save/load");
// The new bookmark must not overwrite a played save or an independent authored scenario.
StatePartition.Annex(saved.Campaign, new[] { 18115 }, saved.Campaign.NationByTag("GEO"), false);
var edited = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(saved.Campaign, "edited"), "edited");
Check(edited.Success, "player-edited campaign snapshot loads");
edited.Campaign.AttachProvinceTopology(topology);
Check(topology.ProvinceToNation[18115] == georgia, "a player's later conquest is not overwritten by the bookmark correction");
var independent = Campaign.CreateBootstrap(20260830);
independent.ScenarioId = "independent-territory-test";
if (!MapRasterCatalog.TryBuildProvinceTopology(Map.States, Map.Control, Map.Width, Map.Height, out var ownMap, out message))
    throw new Exception(message);
var authoredOwners = coastalPockets.Select(p => ownMap.ProvinceToNation[p]).ToArray();
independent.AttachProvinceTopology(ownMap);
Check(coastalPockets.Select((p, i) => ownMap.ProvinceToNation[p] == authoredOwners[i]).All(x => x),
    "independent scenarios keep their authored ownership rather than the 2024 coastal correction");
void Check(bool ok, string label)
{
    if (!ok) throw new Exception("FAIL " + label);
    Console.WriteLine("PASS " + label);
}
