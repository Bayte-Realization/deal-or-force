using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static partial class BenchChecks
{

    /// <summary>
    /// Reefs and cays smaller than the source raster's 0.0625-degree cell. The Coral Sea
    /// Islands (four residents) and the Spratlys (a hundred, on scattered outposts) have
    /// no land pixel anywhere to carve a province out of, so they are rows in the country
    /// table with nothing under them and no way to fix that short of a finer raster.
    /// </summary>
    internal static readonly string[] SubPixelTerritories = { "CSI", "PGA" };

    /// <summary>
    /// A country with a seat in the table and no cell on the map cannot be picked, fought
    /// over or negotiated with - it is a row, not a state. Vatican City, Northern Cyprus
    /// and Gibraltar were exactly that until they were given ground, so the condition is
    /// worth holding onto.
    /// </summary>
    internal static string EveryNationHasTerritory(Campaign campaign,
        IReadOnlyDictionary<string, string> mergeTable = null)
    {
        var invalid = OpeningTerritoryChecks.Administrations(campaign, mergeTable ?? CountryMerges.All, out var administrations);
        if (invalid.Length > 0) return invalid;
        var landless = campaign.Nations
            // Uninhabited claims - Antarctica, Bir Tawil, Clipperton, the banks and reefs -
            // have nobody to simulate, so ground for them would be ground nothing stands on.
            .Where(nation => nation.Population > 0)
            .Where(nation => !SubPixelTerritories.Contains(nation.Tag, StringComparer.OrdinalIgnoreCase))
            // A missing target or cycle is an error above, never a blanket exemption.
            .Where(nation => !administrations.ContainsKey(nation.MapIndex))
            .Where(nation => campaign.StatesOf(nation).Length == 0)
            .Select(nation => nation.Tag)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();
        return landless.Count == 0
            ? ""
            : $"{landless.Count} nation(s) hold no state: {string.Join(", ", landless.Take(12))}";
    }

    /// <summary>
    /// Redraw a border through the middle of a region and check the region actually
    /// becomes two. This is the invariant the whole partition rests on: a region cut in
    /// half is two regions with two populations and two sets of buildings, not one region
    /// with a note attached - and the arithmetic has to add back up to what it started as.
    /// </summary>
    /// <summary>
    /// The state the shipped map was actually in: nine regions whose provinces belonged to
    /// two countries at once, because the only thing that ever split a region was a peace
    /// treaty and no treaty had been signed yet. Kherson, Kharkiv, Donetsk, Zaporizhzhia
    /// and Luhansk were the obvious ones - the region record said Ukraine, the ground under
    /// half of it said Russia, and the interface had to pick one of them to believe.
    /// </summary>
    /// <summary>
    /// Whether every country is named by its tag.
    ///
    /// `CountryNames.Resolve` asks for `country.&lt;TAG&gt;.name` first and falls back to the
    /// English name only when there is no such row. The fallback works, and it is what every
    /// country used until now - which means a translation was keyed by an English sentence,
    /// in a flat namespace shared with cultures, religions, buildings and goods. That is how
    /// the language picker came to offer "Англичане": English is a language and also a
    /// people, and the table could not tell them apart.
    ///
    /// Three letters mean one country. This check is what keeps it that way: a country added
    /// without a tag row still WORKS, and says so here rather than being found by a player.
    /// </summary>
    internal static string CountriesAreNamedByTag(Campaign campaign)
    {
        var english = ReadLocalizationTable("english");
        if (english is null) return "";

        var byFallback = new List<string>();
        var checkedCount = 0;
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrEmpty(nation.Tag)) continue;
            checkedCount++;
            // The same question Resolve asks, in the same order: anything more specific
            // first, then the plain tag. A country named by any of them is named by its tag.
            var named = english.ContainsKey($"country.{nation.Tag}.name") ||
                        english.ContainsKey($"country.{nation.Tag}.map");
            if (!named && byFallback.Count < 8) byFallback.Add($"{nation.Tag} ({nation.Name})");
            if (!named && byFallback.Count >= 8) byFallback.Add("...");
        }
        if (checkedCount == 0) return "";
        if (byFallback.Count > 0)
            return $"{byFallback.Count:N0} country(ies) are named only by their English name and " +
                   $"would lose their translation if that name were edited: {string.Join(", ", byFallback)}";

        // And the tag route has to actually be the one taken, not merely present.
        var sample = campaign.Nations.Find(nation => nation.Tag == "RUS") ?? campaign.Nations.FirstOrDefault();
        if (sample is not null && string.IsNullOrEmpty(CountryNames.Resolve(sample, false)))
            return $"{sample.Tag} resolves to nothing at all";

        Console.WriteLine($"  countries: {checkedCount:N0} named by tag (country.<TAG>.name), " +
                          "so renaming one in common/countries keeps its translation.");
        return "";
    }

    /// <summary>
    /// A country whose name is an argument answers to the option that settles it.
    ///
    /// `country_name_&lt;tag&gt;` is a settings value used as a suffix on a localisation key, so
    /// the whole mechanism is two pieces of data that have to agree with each other and
    /// nothing in between to raise an error when they do not. Three ways for it to be wrong,
    /// all silent:
    ///
    ///   - the option names a country that is not on the map, so it does nothing for ever;
    ///   - a variant has a row in one language and not another, so the same save reads
    ///     Belarus in English and Белоруссия in Russian and nobody can see why;
    ///   - the resolver stops asking, and every option on the screen quietly becomes decor.
    ///
    /// A variant with no row anywhere is fine and is checked as fine: Moldova's option has
    /// exactly one value, no row behind it, and changes nothing on purpose.
    ///
    /// Settings are written to memory here - the bench never calls `UseDirectory`, so there
    /// is no file to write to - and cleared again at the end, so nothing this check does is
    /// visible to the checks after it.
    /// </summary>
    internal static string ANamingOptionRenamesTheCountry(Campaign campaign)
    {
        var options = SettingsCatalog.All
            .Where(definition => definition.Id.StartsWith(CountryNames.VariantPrefix,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.Count == 0) return "";

        var scripts = FindScripts();
        if (scripts is null) return "";
        var tables = new List<(string Language, Dictionary<string, string> Rows)>();
        foreach (var folder in Directory.GetDirectories(
                     Path.Combine(Path.GetDirectoryName(scripts)!, "game", "localization")))
        {
            var name = Path.GetFileName(folder);
            var rows = ReadLocalizationTable(name);
            if (rows is not null) tables.Add((name, rows));
        }
        if (tables.Count == 0) return "";

        var renamed = 0;
        var inert = new List<string>();
        try
        {
            foreach (var option in options)
            {
                var tag = option.Id[CountryNames.VariantPrefix.Length..].ToUpperInvariant();
                var nation = campaign.NationByTag(tag);
                if (nation is null)
                    return $"'{option.Id}' offers to rename {tag}, and there is no such country";
                if (option.Values.Count == 0)
                    return $"'{option.Id}' offers no values, so there is nothing to choose";

                foreach (var value in option.Values)
                {
                    var key = $"country.{tag}.name.{value}";
                    var has = tables.Where(entry => entry.Rows.ContainsKey(key))
                        .Select(entry => entry.Language).ToList();
                    if (has.Count > 0 && has.Count != tables.Count)
                    {
                        var missing = tables.Select(entry => entry.Language).Except(has);
                        return $"'{key}' is written in {string.Join(", ", has)} and missing from " +
                               $"{string.Join(", ", missing)}, so what {tag} is called depends on " +
                               "which language the screen is in";
                    }

                    // And the resolver has to actually reach it. Asked through the same path
                    // the map uses, not by reading the table back.
                    PlayerSettings.Set(option.Id, value);
                    CountryNames.Refresh();
                    var shown = CountryNames.Resolve(nation, false);
                    if (has.Count == 0)
                    {
                        inert.Add($"{option.Id}={value}");
                        continue;
                    }
                    var wanted = tables.First(entry =>
                        string.Equals(entry.Language, Loc.Language, StringComparison.OrdinalIgnoreCase)
                        || entry.Language == "english").Rows[key];
                    if (!string.Equals(shown, wanted, StringComparison.Ordinal))
                        return $"with '{option.Id}' set to '{value}', {tag} is still shown as " +
                               $"'{shown}' rather than '{wanted}'";
                    renamed++;
                }
            }
        }
        finally
        {
            foreach (var option in options) PlayerSettings.Clear(option.Id);
            CountryNames.Refresh();
        }

        // And at least one of them has to do something, or the mechanism is unreachable and
        // will rot without anybody noticing.
        if (renamed == 0)
            return $"{options.Count:N0} naming option(s) are on the screen and not one of them " +
                   "changes a name in any language";

        Console.WriteLine($"  naming: {options.Count:N0} option(s) over what a country is called, " +
                          $"{renamed:N0} spelling(s) reached through the resolver in " +
                          $"{tables.Count:N0} language(s)" +
                          (inert.Count == 0
                              ? "."
                              : $"; {inert.Count:N0} deliberately change(s) nothing: {string.Join(", ", inert)}."));
        return "";
    }

    internal static string EveryRegionHasOneOwner(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        var owner = new Dictionary<ushort, ushort>();
        var divided = new Dictionary<ushort, HashSet<ushort>>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var region = topology.ProvinceToState[province];
            if (region == 0) continue;
            var flag = topology.ProvinceToNation[province];
            if (!owner.TryGetValue(region, out var first)) { owner[region] = flag; continue; }
            if (first == flag) continue;
            if (!divided.TryGetValue(region, out var flags))
                divided[region] = flags = new HashSet<ushort> { first };
            flags.Add(flag);
        }

        if (divided.Count > 0)
        {
            var examples = divided.Take(3).Select(entry =>
            {
                var state = campaign.States.Find(candidate => candidate.MapIndex == entry.Key);
                var tags = entry.Value.Select(flag =>
                    campaign.Nations.Find(nation => nation.MapIndex == flag)?.Tag ?? flag.ToString());
                return $"{state?.Name ?? entry.Key.ToString()} ({string.Join("/", tags)})";
            });
            return $"{divided.Count:N0} region(s) belong to more than one country: {string.Join(", ", examples)}";
        }

        // And the region record agrees with the ground, rather than naming an owner that
        // lost the last province of it some time ago.
        var wrong = new List<string>();
        foreach (var state in campaign.States)
        {
            if (!owner.TryGetValue((ushort)state.MapIndex, out var flag)) continue;
            if (state.NationMapIndex != flag)
                wrong.Add($"{state.Name} says {state.NationMapIndex}, its ground says {flag}");
        }
        if (wrong.Count > 0)
            return $"{wrong.Count:N0} region(s) name an owner their provinces do not: {string.Join("; ", wrong.Take(3))}";

        // The case that started this: the occupied Ukrainian oblasts and Crimea, where the
        // region record named Ukraine and the ground named Russia. Named here so a run says
        // out loud that the ground and the ledger agree about them.
        var watched = new[] { "Luhansk", "Donetsk", "Kherson", "Zaporizhzhia", "Kharkiv",
                              "Autonomous Republic of Crimea" };
        var report = new List<string>();
        foreach (var state in campaign.States)
        {
            var stem = state.Name;
            if (!watched.Any(name => stem.EndsWith(name, StringComparison.Ordinal))) continue;
            var tag = campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex)?.Tag ?? "?";
            report.Add($"{stem} {tag}");
        }
        report.Sort(StringComparer.Ordinal);

        Console.WriteLine($"  borders: all {owner.Count:N0} region(s) on the map have exactly one owner" +
                          (report.Count == 0 ? "." : $"; the contested ones are {string.Join(", ", report)}."));
        return "";
    }

    /// <summary>
    /// The other direction. A border that goes away should leave the map as it found it:
    /// without this, a region cut in half by a war stayed two regions for the rest of the
    /// campaign even after one country held every province of it again - two rows in the
    /// ledger, two sets of buildings and two populations for one place.
    /// </summary>
    internal static string RegionsHealWhenTheBorderGoesAway(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        // A region held whole, big enough to cut and put back.
        ScenarioState subject = null;
        List<int> provinces = null;
        foreach (var candidate in campaign.States.OrderByDescending(state => state.Population))
        {
            if (candidate.ParentMapIndex != 0) continue;
            var owned = new List<int>();
            for (var province = 1; province <= topology.ProvinceCount; province++)
                if (topology.ProvinceToState[province] == (ushort)candidate.MapIndex)
                    owned.Add(province);
            if (owned.Count < 6) continue;
            if (owned.Any(province => topology.ProvinceToNation[province] != (ushort)candidate.NationMapIndex)) continue;
            subject = candidate;
            provinces = owned;
            break;
        }
        if (subject is null) return "no region held whole is large enough to cut and heal";

        var originalOwner = campaign.Nations.Find(nation => nation.MapIndex == subject.NationMapIndex);
        var taker = campaign.Nations.FirstOrDefault(nation =>
            nation.MapIndex != subject.NationMapIndex && nation.Population > 0);
        if (originalOwner is null || taker is null) return "no pair of countries to move the ground between";

        var name = subject.Name;
        var peopleBefore = subject.Population;
        var industryBefore = subject.Buildings.Values.Sum();
        var regionsBefore = campaign.States.Count;
        var literacyBefore = subject.Literacy;

        var half = provinces.Take(provinces.Count / 2).ToList();
        var created = StatePartition.Annex(campaign, half, taker);
        if (created.Count == 0) return $"cutting {name} in half created no new region";
        if (created.Any(piece => piece.ParentMapIndex != subject.MapIndex))
            return "a piece does not remember the region it was cut from, so nothing can put it back";

        // Hand every province of the original region back, including the ones that never
        // moved: annexing to the owner that already holds them is a no-op for those.
        StatePartition.Annex(campaign, provinces, originalOwner);

        if (campaign.States.Count != regionsBefore)
            return $"the region list did not return to its size: {regionsBefore:N0} before, " +
                   $"{campaign.States.Count:N0} after";
        var healed = campaign.States.Find(state => state.MapIndex == subject.MapIndex);
        if (healed is null) return $"'{name}' no longer exists after being put back together";
        if (campaign.States.Any(state => state.ParentMapIndex == subject.MapIndex))
            return $"'{name}' still has pieces of itself listed separately";
        if (healed.NationMapIndex != originalOwner.MapIndex)
            return $"'{name}' came back under the wrong flag";

        if (Math.Abs(healed.Population - peopleBefore) > Math.Max(4L, peopleBefore / 1000L))
            return $"people went missing in the round trip: {peopleBefore:N0} before, {healed.Population:N0} after";
        if (Math.Abs(healed.Buildings.Values.Sum() - industryBefore) > 2)
            return $"buildings went missing in the round trip: {industryBefore:N0} before, " +
                   $"{healed.Buildings.Values.Sum():N0} after";
        if (Math.Abs(healed.Literacy - literacyBefore) > 0.01f)
            return $"literacy drifted through the round trip: {literacyBefore:P1} before, {healed.Literacy:P1} after";

        var known = campaign.States.Select(state => (ushort)state.MapIndex).ToHashSet();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (!known.Contains(topology.ProvinceToState[province]))
                return $"province {province} belongs to region {topology.ProvinceToState[province]}, which does not exist";
        if (provinces.Any(province => topology.ProvinceToState[province] != (ushort)subject.MapIndex))
            return $"'{name}' did not take all of its ground back";

        Console.WriteLine($"  healing: {name} was cut into {created.Count + 1} by {taker.Tag}, handed back, " +
                          $"and is one region of {healed.Population:N0} again.");
        return "";
    }

    internal static string RegionsSplitAlongNewBorders(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        // A region with enough provinces to be worth cutting, held whole by one owner.
        ScenarioState subject = null;
        List<int> provinces = null;
        foreach (var candidate in campaign.States.OrderByDescending(state => state.Population))
        {
            var owned = new List<int>();
            for (var province = 1; province <= topology.ProvinceCount; province++)
                if (topology.ProvinceToState[province] == (ushort)candidate.MapIndex)
                    owned.Add(province);
            if (owned.Count < 6) continue;
            if (owned.Any(province => topology.ProvinceToNation[province] != (ushort)candidate.NationMapIndex)) continue;
            subject = candidate;
            provinces = owned;
            break;
        }
        if (subject is null) return "no region large enough to test a split against";

        var taker = campaign.Nations.FirstOrDefault(nation =>
            nation.MapIndex != subject.NationMapIndex && nation.Population > 0);
        if (taker is null) return "no nation to hand the ground to";

        var populationBefore = subject.Population;
        var industryBefore = subject.Buildings.Values.Sum();
        var statesBefore = campaign.States.Count;
        var half = provinces.Take(provinces.Count / 2).ToList();

        var created = StatePartition.Annex(campaign, half, taker);

        if (created.Count == 0)
            return $"annexing {half.Count} of {subject.Name}'s {provinces.Count} provinces created no new region";
        if (campaign.States.Count != statesBefore + created.Count)
            return "the region list does not account for the regions the split created";

        var pieces = created.Append(subject).ToList();
        var populationAfter = pieces.Sum(state => state.Population);
        var industryAfter = pieces.Sum(state => state.Buildings.Values.Sum());
        if (Math.Abs(populationAfter - populationBefore) > Math.Max(4L, populationBefore / 1000L))
            return $"population does not add up: {populationBefore:N0} before, {populationAfter:N0} after";
        if (Math.Abs(industryAfter - industryBefore) > pieces.Count)
            return $"buildings do not add up: {industryBefore:N0} before, {industryAfter:N0} after";

        var originalOwner = subject.NationMapIndex;
        foreach (var piece in created)
        {
            if (piece.MapIndex < StatePartition.FirstDynamicMapIndex)
                return $"'{piece.Name}' took an authored map index ({piece.MapIndex})";
            // A new region belongs either to whoever took the ground, or to the country it
            // was cut away from - taking a slice out of the middle can leave the remainder
            // in two pockets that cannot reach each other, and those are regions too.
            if (piece.NationMapIndex != taker.MapIndex && piece.NationMapIndex != originalOwner)
                return $"'{piece.Name}' ended up under neither the taker nor the original owner";
            if (string.Equals(piece.Name, subject.Name, StringComparison.Ordinal))
                return "the new region reuses its parent's name, so a player cannot tell them apart";
        }
        if (created.All(piece => piece.NationMapIndex != taker.MapIndex))
            return $"{taker.Tag} took ground from {subject.Name} but owns none of the regions it became";
        if (subject.NationMapIndex != originalOwner)
            return $"'{subject.Name}' kept its name but changed owner; the seceding half should carry the new name";

        // Every province still has to belong to exactly one region, and every region the
        // split produced has to be one the campaign knows about.
        var known = campaign.States.Select(state => (ushort)state.MapIndex).ToHashSet();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (!known.Contains(topology.ProvinceToState[province]))
                return $"province {province} belongs to region {topology.ProvinceToState[province]}, which does not exist";

        var names = pieces.Select(piece => piece.Name).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            return "two of the regions the split produced share a name: " + string.Join(", ", names);

        Console.WriteLine($"  partition: {subject.Name} lost {half.Count} of {provinces.Count} provinces to " +
                          $"{taker.Tag} and became {created.Count + 1} regions - " +
                          string.Join(", ", pieces.Select(piece =>
                          {
                              var owner = campaign.Nations.Find(nation => nation.MapIndex == piece.NationMapIndex);
                              return $"{piece.Name} ({owner?.Tag ?? "?"}, {piece.Population:N0} people)";
                          })) + ".");
        return "";
    }

    /// <summary>
    /// A redrawn border has to survive being saved. The province raster on disk still shows
    /// the world of 2024; if a save does not carry its own political layer, loading hands
    /// every conquest back and leaves the region list describing a map that no longer
    /// exists. Run after the partition test, on a campaign whose borders have moved.
    /// </summary>
    /// <summary>
    /// A save's history comes back week for week. The history is written by the save's worker
    /// and spliced into the file the frame wrote, so a file can be valid JSON and load and
    /// still have lost it - or have put it somewhere the reader never looks.
    /// </summary>
    internal static string AHistoryComesBackWhole(Campaign campaign)
    {
        if (campaign.Player is null || campaign.ProvinceMap is null) return "";
        var folder = Path.Combine(Path.GetTempPath(), "dof_history_check");
        Directory.CreateDirectory(folder);
        SaveSystem.Save(folder, "history", campaign, "", "history", true);
        var loaded = SaveSystem.Load(folder, "history", "");
        if (loaded.Campaign is null) return $"the save did not load back: {loaded.Warning}";
        var weeks = 0;
        foreach (var nation in campaign.Nations)
        {
            var saved = campaign.History.ForNation(nation.Id);
            var back = loaded.Campaign.History.ForNation(nation.Id);
            if (saved.Count != back.Count)
                return $"{nation.Tag} saved {saved.Count} week(s) of history and {back.Count} came back";
            for (var week = 0; week < saved.Count; week++)
                if (!saved[week].Equals(back[week]))
                    return $"{nation.Tag}'s week on day {saved[week].Day} came back different";
            weeks += saved.Count;
        }
        if (weeks == 0) return "there was no history to write";
        Console.WriteLine($"  history: {weeks:N0} week(s) of {campaign.Nations.Count} countries written by the save's " +
                          "worker and read back unchanged.");
        return "";
    }

    /// <summary>
    /// The world tally - one walk of the map a day, read by militia and stability - agrees
    /// with the per-country count it replaced, region by region, including the regions a
    /// border has cut between two owners. After two hundred days of war there are some of
    /// those, and they are where a tally kept by region alone would hand one owner the other's.
    /// </summary>
    internal static string TheWorldTallyIsEachCountrysTally(Campaign campaign)
    {
        if (campaign.ProvinceMap is null) return "";
        var own = new Dictionary<int, RegionControl>();
        var enemies = new HashSet<ushort>();
        var regions = 0;
        var shared = new Dictionary<int, int>();
        foreach (var nation in campaign.Nations)
        {
            // Hostile by side, the rule the world tally uses: an ally in the same war is not.
            enemies.Clear();
            foreach (var war in campaign.Wars)
            {
                if (war.Ended || !war.Involves(nation.Id)) continue;
                foreach (var other in campaign.Nations)
                    if (other.Id != nation.Id && war.Opposes(nation.Id, other.Id)) enemies.Add((ushort)other.MapIndex);
            }
            ControlTally.Of(campaign, nation, enemies, own);
            foreach (var (region, expected) in own)
            {
                var world = ControlTally.World(campaign, nation.MapIndex, region);
                if (world.Provinces != expected.Provinces || world.Held != expected.Held ||
                    world.Bordered != expected.Bordered)
                    return $"{nation.Tag} in region {region}: the world tally says {world.Provinces}/{world.Held}/" +
                           $"{world.Bordered} (provinces/held/bordered), its own count {expected.Provinces}/" +
                           $"{expected.Held}/{expected.Bordered}";
                shared[region] = shared.GetValueOrDefault(region) + 1;
                regions++;
            }
        }
        if (regions == 0) return "nobody owns a region";
        Console.WriteLine($"  control tally: {regions:N0} country-region count(s) agree, " +
                          $"{shared.Count(entry => entry.Value > 1)} of them in a region two owners share.");
        return "";
    }

    internal static string BordersSurviveASave(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        var states = campaign.States.Count;
        var dynamic = campaign.States.Count(state => state.MapIndex >= StatePartition.FirstDynamicMapIndex);
        if (dynamic == 0) return "nothing was split, so there is nothing to round-trip";

        var owners = (ushort[])topology.ProvinceToNation.Clone();
        var regions = (ushort[])topology.ProvinceToState.Clone();

        var bytes = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
        if (bytes.Length == 0) return "the campaign refused to serialise";
        var result = SaveSystem.ImportNetworkSnapshot(bytes, "bench");
        var restored = result.Campaign;
        if (restored is null) return $"the save did not load back: {result.Warning}";

        // The restored campaign has no province graph until one is attached, exactly as in
        // the game. Attaching it is what replays the saved political layer.
        if (!MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
                DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var freshTopology, out var message))
            return $"could not rebuild the province graph: {message}";
        restored.AttachProvinceTopology(freshTopology);

        if (restored.States.Count != states)
            return $"{states} regions were saved and {restored.States.Count} came back";
        var restoredDynamic = restored.States.Count(state => state.MapIndex >= StatePartition.FirstDynamicMapIndex);
        if (restoredDynamic != dynamic)
            return $"{dynamic} split regions were saved and {restoredDynamic} came back";

        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (restored.ProvinceMap.ProvinceToNation[province] != owners[province])
                return $"province {province} came back owned by {restored.ProvinceMap.ProvinceToNation[province]} " +
                       $"instead of {owners[province]} - the save handed a conquest back";
            if (restored.ProvinceMap.ProvinceToState[province] != regions[province])
                return $"province {province} came back in region {restored.ProvinceMap.ProvinceToState[province]} " +
                       $"instead of {regions[province]} - the split did not survive";
        }

        Console.WriteLine($"  save round trip: {states:N0} regions including {dynamic:N0} created by a border, " +
                          $"and every province came back where the peace left it.");
        return "";
    }

    internal static string APromiseIsAnsweredAndAConquestIsCounted()
    {
        var campaign = Campaign.CreateBootstrap(20240101);
        if (!AttachMap(campaign)) return "no province map, so nobody borders anybody";

        // Three countries that touch each other, so that a war between two of them is a war
        // the third can physically join.
        Nation attacker = null;
        Nation victim = null;
        Nation friend = null;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Population < 5_000_000) continue;
            foreach (var victimMap in CampaignDiplomacy.Neighbours(campaign, candidate.MapIndex))
            {
                var possibleVictim = NationByMap(campaign, victimMap);
                if (possibleVictim is null || possibleVictim.Population < 5_000_000) continue;
                foreach (var friendMap in CampaignDiplomacy.Neighbours(campaign, possibleVictim.MapIndex))
                {
                    var possibleFriend = NationByMap(campaign, friendMap);
                    if (possibleFriend is null || possibleFriend.Id == candidate.Id) continue;
                    if (possibleFriend.Population < 5_000_000 || possibleFriend.IsHuman) continue;
                    if (!CampaignDiplomacy.SharesLandBorder(campaign, possibleFriend.MapIndex, candidate.MapIndex))
                        continue;
                    attacker = candidate;
                    victim = possibleVictim;
                    friend = possibleFriend;
                    break;
                }
                if (friend is not null) break;
            }
            if (friend is not null) break;
        }
        if (attacker is null || victim is null || friend is null)
        {
            var sample = 0;
            foreach (var nation in campaign.Nations)
            {
                if (nation.Population < 5_000_000) continue;
                sample = CampaignDiplomacy.Neighbours(campaign, nation.MapIndex).Count;
                if (sample > 0) break;
            }
            return "could not find three countries that all touch each other " +
                   $"(a large country has {sample:N0} neighbour(s), so the border table is " +
                   (sample == 0 ? "empty" : "fine and the triangle is what is missing") + ")";
        }

        // A real alliance, and a friend able and willing to honour it.
        var bond = CampaignDiplomacy.Get(campaign, friend.Id, victim.Id);
        bond.Stance = DiplomaticStance.Allied;
        bond.Opinion = 90f;
        friend.WarSupport = 1f;
        friend.Stability = 0.9f;

        var warsBefore = campaign.Wars.Count;
        var notorietyBefore = attacker.Notoriety;
        var war = CampaignDiplomacy.DeclareWar(campaign, attacker.Id, victim.Id);
        if (war is null) return $"{attacker.Tag} could not declare on its own neighbour {victim.Tag}";

        // Into the same war, beside the country it promised to stand by. It used to be a
        // second war of the friend's own, which is what made a NATO war thirty wars.
        if (!war.IsDefending(friend.Id))
            return $"{friend.Tag} was allied to {victim.Tag} at {bond.Opinion:0} opinion, watched " +
                   $"{attacker.Tag} attack it, and did nothing";
        if (campaign.Wars.Count != warsBefore + 1)
            return $"one declaration opened {campaign.Wars.Count - warsBefore} wars; a call to arms " +
                   "brings an ally into the war that is happening, not into one of its own";
        if (CampaignDiplomacy.Get(campaign, friend.Id, attacker.Id).Stance != DiplomaticStance.AtWar)
            return $"{friend.Tag} joined the war against {attacker.Tag} without being at war with them";

        // The declaration itself is counted, and so is what it takes afterwards.
        if (attacker.Notoriety <= notorietyBefore && string.IsNullOrEmpty(attacker.LastWarJustification))
            return $"{attacker.Tag} declared a war it could give no reason for and the world did not mind";

        var beforeAnnexation = attacker.Notoriety;
        var takeable = new List<int>();
        var topology = campaign.ProvinceMap;
        for (var province = 1; province <= topology.ProvinceCount && takeable.Count < 40; province++)
            if (topology.ProvinceToNation[province] == (ushort)victim.MapIndex) takeable.Add(province);
        if (takeable.Count > 0)
        {
            StatePartition.Annex(campaign, takeable, attacker);
            if (attacker.Notoriety <= beforeAnnexation)
                return $"{attacker.Tag} annexed {takeable.Count:N0} province(s) of {victim.Tag} for free";
        }

        // And enough of it brings the neighbourhood together.
        attacker.Notoriety = Statecraft.CoalitionThreshold + 5f;
        foreach (var neighbourMap in CampaignDiplomacy.Neighbours(campaign, attacker.MapIndex))
        {
            var neighbour = NationByMap(campaign, neighbourMap);
            if (neighbour is null || neighbour.Id == attacker.Id) continue;
            CampaignDiplomacy.Get(campaign, neighbour.Id, attacker.Id).Opinion = -40f;
        }
        for (var step = 0; step < 8 && Statecraft.CoalitionAgainst(campaign, attacker.Id) is null; step++)
        {
            campaign.IsPaused = false;
            campaign.TickDay();
        }
        var coalition = Statecraft.CoalitionAgainst(campaign, attacker.Id);
        if (coalition is null)
            return $"{attacker.Tag} reached {attacker.Notoriety:0} notoriety among hostile neighbours " +
                   "and nobody organised against it";

        Console.WriteLine($"  statecraft: {attacker.Tag} attacked {victim.Tag}; {friend.Tag} honoured its " +
                          $"alliance and joined the same war ({war.BelligerentCount} belligerents). " +
                          $"{attacker.Tag} carries {attacker.Notoriety:0} notoriety " +
                          $"and {coalition.MemberIds.Count:N0} countries have formed against it.");
        return "";
    }

    internal static Nation NationByMap(Campaign campaign, int mapIndex)
    {
        foreach (var nation in campaign.Nations)
            if (nation.MapIndex == mapIndex) return nation;
        return null;
    }

    internal static string TheWorldGovernsItself(Campaign campaign, int days)
    {
        var years = MathF.Max(0.01f, days / 365f);
        var wars = campaign.Wars.Count;
        var settled = campaign.Wars.Count(war => war.Ended);
        var annexations = campaign.Wars.Sum(war => war.AnnexedProvinces);
        var trades = campaign.Relations.Values.Count(relation => relation.TradeAgreement);
        var pacts = campaign.Relations.Values.Count(relation => relation.NonAggressionPact);
        var alliances = campaign.Relations.Values.Count(relation => relation.Stance == DiplomaticStance.Allied);
        var rivalries = campaign.Relations.Values.Count(relation => relation.Stance == DiplomaticStance.Rival);
        var projects = campaign.Construction.Count;
        var taxes = campaign.Nations.Where(nation => nation.Population > 0).Select(nation => nation.TaxRate).ToList();
        var taxSpread = taxes.Count == 0 ? 0f : taxes.Max() - taxes.Min();

        Console.WriteLine($"  the world after {days} day(s) of nobody playing: {wars:N0} war(s) declared " +
                          $"({settled:N0} settled, {annexations:N0} province(s) annexed), " +
                          $"{trades:N0} trade agreement(s), {pacts:N0} pact(s), {alliances:N0} alliance(s), " +
                          $"{rivalries:N0} rivalr(ies), {projects:N0} project(s) under construction.");
        Console.WriteLine($"  tax rates range {taxes.Min():P0} to {taxes.Max():P0}; " +
                          $"{campaign.Nations.Count(nation => nation.MilitarySpending > 0.10f):N0} " +
                          "countries are on a war footing.");

        // Whether the sea is contested or decorative. Every navy but the player's used to
        // sit in port for the whole campaign, which is easy to miss when nothing checks it.
        var byMission = campaign.Fleets
            .Where(fleet => fleet.Hulls > 0)
            .GroupBy(fleet => fleet.Mission)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} x{group.Count()}");
        var atSea = campaign.Fleets.Count(fleet => fleet.Mission != FleetMission.Anchored);
        var carrying = campaign.Fleets.Count(fleet => fleet.Carried.Count > 0);
        var worn = campaign.Fleets.Where(fleet => fleet.Hulls > 0).ToList();
        // Whether anybody with a navy is fighting anybody else with one. Zero naval actions
        // is the right answer when no two navies are at war, and the wrong one otherwise -
        // the number that tells them apart belongs in the report.
        var navied = new HashSet<int>();
        foreach (var fleet in campaign.Fleets) if (fleet.Hulls > 0) navied.Add(fleet.NationId);
        var navalWars = campaign.Wars.Count(war => !war.Ended &&
            navied.Contains(war.AttackerId) && navied.Contains(war.DefenderId));
        var overseasWars = campaign.Wars.Count(war => !war.Ended &&
            campaign.NationById(war.AttackerId) is { } a && campaign.NationById(war.DefenderId) is { } b &&
            !CampaignDiplomacy.SharesLandBorder(campaign, a.MapIndex, b.MapIndex));
        Console.WriteLine($"  wars at sea: {navalWars:N0} live war(s) have a navy on both sides, " +
                          $"{overseasWars:N0} are between countries that do not touch by land.");
        Console.WriteLine($"  the sea: {atSea:N0} of {worn.Count:N0} fleet(s) are out, {carrying:N0} carrying " +
                          $"troops - {string.Join(", ", byMission)}. Average strength " +
                          $"{(worn.Count == 0 ? 0f : worn.Average(fleet => fleet.Strength)):P0}, " +
                          $"experience {(worn.Count == 0 ? 0f : worn.Average(fleet => fleet.Experience)):P0}.");

        var tally = string.Join(", ", AiStatecraft.Tally.OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key} {entry.Value:N0}"));
        Console.WriteLine($"  war decisions: {(tally.Length == 0 ? "none reached" : tally)}");

        if (wars <= 1 && years >= 0.9f)
            return "not one country declared a war in a year; the world is a diorama";
        if (trades + pacts + alliances + rivalries == 0)
            return "no country formed or soured a single relationship in a year";
        if (projects == 0 && taxSpread < 0.01f)
            return "nobody built anything and every country still has the same tax rate";

        // And the other failure: one country eating the planet because nothing pushes back.
        var largest = campaign.Nations
            .Where(nation => nation.Population > 0)
            .OrderByDescending(nation => campaign.StatesOf(nation).Length)
            .First();
        var share = campaign.StatesOf(largest).Length / (float)campaign.States.Count;
        if (share > 0.25f)
            return $"{largest.Tag} holds {share:P0} of the world's regions after {days} day(s); " +
                   "the AI is not being pushed back by anything";
        return "";
    }

    /// <summary>
    /// A province must belong to the country whose ground its own pixels are on.
    ///
    /// This is the most basic thing that can be true of a political map, and it was quietly
    /// false for half the world. The map compiler builds the provinces from the source
    /// raster and then exports the political map from the result - but attaching a topology
    /// applies whatever ownership file is already on disk, and that file was written against
    /// the *previous* province numbering. So each recompile that changed the province count
    /// laid the old world over the new provinces and wrote the wreckage back out as truth.
    /// Kentucky was Chinese. Ashgabat was American. Every screen in the game reported it
    /// faithfully, every existing check passed, and the front lines were drawn across three
    /// continents because half the planet was suddenly at war with itself.
    ///
    /// Nothing caught it because nothing compared the map against the ground it is drawn on.
    /// </summary>
    internal static string ProvincesSitWhereTheirOwnerIs(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "the province map is unavailable";
        var invalid = OpeningTerritoryChecks.Administrations(campaign, CountryMerges.All, out var administrations);
        if (invalid.Length > 0) return invalid;

        // Land history says belongs to nobody (ColonisationCore) is the one place the ground and
        // the owner are meant to differ: the raster still paints Antarctica as a country. There
        // the answer is nobody - and it has to be nobody, in every province of the region.
        var unclaimed = campaign.States.Where(ColonisationCore.IsUnclaimed).Select(state => state.MapIndex).ToHashSet();
        // And regions the 2024 bookmark hands to the country holding them rather than the one
        // whose ground the raster paints them as - written by region id, because province ids
        // move at every recut. The Gaza Strip is Israel's (history/provinces/ownership.csv).
        var handedOver = new Dictionary<string, string> { ["ps_gza"] = "ISR" };
        var deliberate = campaign.States.Where(state => handedOver.ContainsKey(state.Id))
            .ToDictionary(state => state.MapIndex, state => campaign.NationByTag(handedOver[state.Id])?.MapIndex ?? -1);
        // Narrow, reviewed 2024 coastal repairs; MapTerritoryTests additionally pins
        // these IDs to geographic coordinates, neighbours and the compiled-map stamp.
        // Do not whitelist all ownership.csv rows: a stale recut must still fail here.
        var coastalRepairs = new Dictionary<int, (string Ground, string Owner)>
        {
            [18115] = ("GEO", "ABK"), [18133] = ("GEO", "ABK"), [21648] = ("PSE", "ISR")
        };
        // Corrections written by a point on the map (history/provinces, "@longitude,latitude"):
        // reviewed one by one and kept through recuts, unlike a province id - the Abkhazian
        // coastal pockets, Siachen, Bir Tawil.
        var points = ProvinceOwnership.PointCorrections(topology);
        var nobodys = 0;
        var checkedProvinces = 0;
        var wrong = new List<string>();
        string Tag(int mapIndex) =>
            campaign.Nations.Find(nation => nation.MapIndex == mapIndex)?.Tag ?? $"#{mapIndex}";
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var cell = topology.ProvinceCenterCell[province];
            if (cell < 0 || cell >= DealOrForce.Simulation.WorldMapSource.Control.Length) continue;
            var ground = DealOrForce.Simulation.WorldMapSource.Control[cell];
            var owner = topology.ProvinceToNation[province];
            if (unclaimed.Contains(topology.ProvinceToState[province]))
            {
                checkedProvinces++;
                if (owner == 0) { nobodys++; continue; }
                wrong.Add(wrong.Count < 4 ? $"province {province:N0} is in land nobody owns and belongs to {Tag(owner)}" : "");
                continue;
            }
            if (ground == 0) continue;
            checkedProvinces++;
            // A declared administration is the expected owner, not an arbitrary csv
            // override. Keeping the obsolete raw source tag is wrong too.
            var expected = administrations.GetValueOrDefault(ground, ground);
            if (owner == expected) continue;
            if (deliberate.TryGetValue(topology.ProvinceToState[province], out var handed) && owner == handed) continue;
            if (coastalRepairs.TryGetValue(province, out var repair) &&
                Tag(ground) == repair.Ground && Tag(owner) == repair.Owner) continue;
            if (points.TryGetValue(province, out var pointOwner) &&
                (owner == 0 ? pointOwner == "none" : Tag(owner) == pointOwner)) continue;
            // A de facto power of 2024 stands on the ground of the state it holds it of (Factions).
            if (campaign.Nations.Find(nation => nation.MapIndex == owner) is { FactionId.Length: > 0 } faction &&
                campaign.NationById(faction.SecededFromId) is { } parent && parent.MapIndex == ground) continue;
            wrong.Add(wrong.Count < 4 ? $"province {province:N0} stands on {Tag(ground)} ground and belongs to {Tag(owner)} " +
                $"({OpeningTerritoryChecks.SourceSummary(campaign, province)})" : "");
        }

        if (wrong.Count > 0)
            return $"{wrong.Count:N0} of {checkedProvinces:N0} provinces belong to somebody other than the " +
                   $"country they are standing on: {string.Join("; ", wrong.Where(entry => entry.Length > 0))}";

        Console.WriteLine($"  geography: all {checkedProvinces:N0} provinces match their ground, authored " +
                          $"administration or explicit correction; unclaimed {nobodys:N0}.");
        return "";
    }

    /// <summary>
    /// Names, map colours and capitals are the most-modded thing in any game of this kind,
    /// and they were compiled constants until they were exported into
    /// `common/countries/`. Exported data has one failure mode, and it is the one the
    /// shipped defines already had once: the file and the game drift apart, and a modder
    /// edits one world while playing another. So every country the scenario creates must
    /// have a definition, and the definition must say what the game says.
    /// </summary>
    internal static string ShippedCountriesMatchTheScenario(Campaign campaign, HashSet<string> bookmarkTags)
    {
        var shipped = new Dictionary<string, (string Name, string Color, string Capital)>(StringComparer.Ordinal);
        foreach (var document in GameDataLoader.DataDocuments("common", "countries"))
            using (document)
            {
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    shipped[entry.Name] = (
                        Text(entry.Value, "name"), Text(entry.Value, "color"), Text(entry.Value, "capital_name"));
                }
            }
        if (shipped.Count == 0)
            return "no country definitions ship at all; common/countries is missing or empty";

        var missing = new List<string>();
        var drifted = new List<string>();
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrWhiteSpace(nation.Tag)) continue;
            // Movements are declared in common/movements, not common/countries; the de facto
            // powers of 2024 in history/factions. A country the simulation founded answers to
            // a tag the bookmark never shipped, and is nobody's drift.
            if (nation.Movement is not null || nation.FactionId.Length > 0) continue;
            if (!bookmarkTags.Contains(nation.Tag)) continue;
            if (!shipped.TryGetValue(nation.Tag, out var entry)) { missing.Add(nation.Tag); continue; }
            // A country that renamed itself in play (a proclamation, a formable, a rising, a
            // faction settling back into its parent) answers a different name than the roster,
            // and the proclamation repaints the colour with it - the living world working,
            // not drift. Its having a definition at all is still required.
            if (nation.NameProclaimed) continue;
            if (!string.Equals(entry.Name, nation.Name, StringComparison.Ordinal))
                drifted.Add($"{nation.Tag} name: file '{entry.Name}', game '{nation.Name}'");
            else if (!string.Equals(entry.Color, nation.Color, StringComparison.OrdinalIgnoreCase))
                drifted.Add($"{nation.Tag} colour: file '{entry.Color}', game '{nation.Color}'");
        }
        if (missing.Count > 0)
            return $"{missing.Count:N0} country(ies) the scenario creates have no definition: " +
                   string.Join(", ", missing.Take(6));
        if (drifted.Count > 0)
            return $"{drifted.Count:N0} definition(s) have drifted from the game: " +
                   string.Join("; ", drifted.Take(3));

        Console.WriteLine($"  countries: {shipped.Count:N0} definitions in common/countries describe every " +
                          "nation the scenario creates, name and colour for name and colour.");
        return "";
    }

    internal static string Text(System.Text.Json.JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == System.Text.Json.JsonValueKind.String
            ? field.GetString() ?? ""
            : "";

    /// <summary>
    /// The moments belong to the code; what they sound like belongs to the data.
    ///
    /// Every part of a cue used to be a constant in the interface: the file, which of the
    /// three players it went to, how loud, and how long before it could sound again - that
    /// last one a `switch` on the cue's own name, which is the least reachable place a
    /// modder could look. The check holds the two halves apart: the six moments the code
    /// raises must all be declared, and the declaration has to be the thing that decides.
    /// </summary>
    /// <summary>
    /// Which flag a country flies, now that the answer can change.
    ///
    /// Every country had exactly one flag for the length of a campaign, which is a strange
    /// rule for a game whose subject is countries changing: Russia going communist flew the
    /// tricolour, a restored Irish monarchy flew the tricolour, and a country founded that
    /// morning by a secession flew nothing at all because no file was named after it.
    ///
    /// The binding is entirely by file name, which is what makes it easy to add to and also
    /// what makes it impossible for a compiler to check: nothing anywhere says that
    /// `flag_rus_communism.png` is looked for under exactly that name. This does.
    /// </summary>
    /// <summary>
    /// Whether the political layer is alive for anybody but the player.
    ///
    /// A player could rearrange their state at any time and no other country in the world
    /// ever did - two hundred and sixty governments holding the same form and the same
    /// ideology from 2024 onwards, through defeat, collapse and revolution. Everything
    /// needed to change was already written; nothing called it.
    ///
    /// Four ways to be wrong, and the check looks for each: nothing ever changes, everything
    /// changes constantly, a country changes into something it already was, or the player's
    /// own government is rearranged out from under them.
    /// </summary>
    /// <summary>
    /// Going back to the menu and playing again.
    ///
    /// The province map costs seconds and a great deal of memory to build, so it is built
    /// once a session and handed to whichever campaign is running. Two things follow that
    /// nothing tested, because this bench builds a fresh topology for every check and the
    /// game does not:
    ///
    /// - the second campaign has to be GIVEN the map at all. It was not: the map was applied
    ///   once, to the first campaign, and everything that happens in that apply - the
    ///   topology reaching the campaign, and with it the deployment of every army in the
    ///   world - happened to that campaign and to nothing else. A second campaign had no
    ///   province map: no fronts, no movement, no combat, and every division in the world
    ///   still standing on the capital it was created at.
    /// - and it has to be given the AUTHORED map. The simulation writes borders into the
    ///   topology's own arrays, so a second campaign handed it unchanged would open with the
    ///   first campaign's conquests already made.
    /// </summary>
    /// <summary>
    /// The states a country can proclaim itself.
    ///
    /// Each is a few lines of data naming country tags, ideology ids, government ids and a
    /// colour, and not one of those is checked by anything. A tag that does not exist does
    /// not throw - it silently contributes no territory, which makes the state either
    /// impossible or far too easy to proclaim, and nothing would ever say which.
    /// </summary>
    internal static string FormableStatesNameRealThings(Campaign campaign)
    {
        FormableNations.ResetCatalog();
        var all = FormableNations.All.ToList();
        if (all.Count == 0) return "common/formable_nations declares nothing at all";

        var tags = new HashSet<string>(campaign.Nations.Select(nation => nation.Tag),
            StringComparer.OrdinalIgnoreCase);

        var conditioned = 0;
        var coloured = 0;
        long largest = 0;
        FormableNation biggest = null;
        foreach (var formable in all)
        {
            foreach (var tag in formable.EligibleTags)
                if (!tags.Contains(tag))
                    return $"'{formable.Id}' may be proclaimed by {tag}, which is not a country";
            foreach (var tag in formable.RequiredTags)
                if (!tags.Contains(tag))
                    return $"'{formable.Id}' wants the territory of {tag}, which is not a country";
            foreach (var id in formable.RequiredIdeology)
                if (!IdeologyCatalog.Knows(id))
                    return $"'{formable.Id}' requires the ideology '{id}', which nothing declares";
            foreach (var id in formable.RequiredGovernment)
                if (!GovernmentCatalog.Knows(id))
                    return $"'{formable.Id}' requires the government '{id}', which nothing declares";

            // Whoever may proclaim it must be among those whose ground it wants, or it is a
            // claim on somebody else rather than a union - which this mechanic does not do,
            // because it grants no territory.
            if (formable.RequiredTags.Length > 0 &&
                !formable.EligibleTags.Any(tag => formable.RequiredTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
                return $"nobody who may proclaim '{formable.Id}' is part of it, so it could only " +
                       "ever be declared over other people's ground";

            if (formable.Colour.Length > 0)
            {
                coloured++;
                if (!formable.Colour.StartsWith("#", StringComparison.Ordinal) || formable.Colour.Length != 7)
                    return $"'{formable.Id}' paints itself '{formable.Colour}', which is not a colour";
            }
            if (formable.RequiredIdeology.Length > 0 || formable.RequiredGovernment.Length > 0)
                conditioned++;

            long people = 0;
            foreach (var tag in formable.RequiredTags)
                people += campaign.NationByTag(tag)?.Population ?? 0;
            if (people > largest) { largest = people; biggest = formable; }
        }

        // And one of them, evaluated for real, has to produce requirements a player could
        // read - the whole path from data to panel, rather than the data alone.
        var korea = FormableNations.Find("united_korea");
        var founder = campaign.NationByTag("KOR");
        if (korea is not null && founder is not null)
        {
            var status = FormableNations.Evaluate(campaign, founder, korea);
            if (status.Requirements.Length == 0)
                return "united_korea evaluated to no requirements at all";
            if (status.CanForm)
                return "Korea could proclaim a united Korea on day one without holding the North";
            foreach (var requirement in status.Requirements)
                if (string.IsNullOrWhiteSpace(requirement.Label))
                    return "a requirement was shown to the player with no words in it";
        }

        Console.WriteLine($"  formable states: {all.Count:N0} declared, {conditioned:N0} of them " +
                          $"asking for a particular politics and {coloured:N0} painting themselves; " +
                          $"the largest is {biggest?.Name} at {largest:N0} people.");
        return "";
    }

    /// <summary>How many different provinces a country's formations are actually standing in.</summary>
    internal static int Spread(Campaign campaign, string tag)
    {
        var nation = campaign.NationByTag(tag);
        if (nation is null) return 0;
        var provinces = new HashSet<int>();
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id) provinces.Add(army.ProvinceId);
        return provinces.Count;
    }

    internal static string GovernmentsChangeWithoutBeingTold()
    {
        GovernanceEvents.ResetCatalog();
        if (GovernanceEvents.All.Count == 0)
            return "common/governance declares no transitions, so no country can ever change";

        var campaign = Campaign.CreateBootstrap(20240606);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var player = campaign.Player;
        if (player is null) return "there is no player, so there is nobody this must not happen to";
        var playerGovernment = player.Government;
        var playerIdeology = player.Ideology;

        var before = new Dictionary<int, (string Government, string Ideology)>();
        foreach (var nation in campaign.Nations)
            before[nation.Id] = (nation.Government, nation.Ideology);

        var changes = new List<GovernanceReport>();
        for (var day = 0; day < 365 * 15; day++)
        {
            campaign.TickDay();
            changes.AddRange(GovernanceEvents.Recent);
        }

        if (changes.Count == 0)
            return "fifteen years of wars, collapses and secessions and not one government " +
                   "anywhere became anything else";

        // The player's country is theirs. A cabinet that rearranged the state out from under
        // the person playing it would be the most annoying thing this game could do.
        if (player.Government != playerGovernment || player.Ideology != playerIdeology)
            return $"the player's own country was rearranged for them: {playerGovernment}/" +
                   $"{playerIdeology} became {player.Government}/{player.Ideology}";

        foreach (var report in changes)
        {
            if (report.Nation is null) return "a change was reported with no country attached";
            // Against what it became, not against what it is now: a country that changed
            // twice has a current state that says nothing about either change.
            if (report.WasGovernment == report.NowGovernment &&
                report.WasIdeology == report.NowIdeology)
                return $"{report.Nation.Tag} was reported as changing into what it already was";
            if (!GovernmentCatalog.Knows(report.NowGovernment))
                return $"{report.Nation.Tag} ended up under '{report.NowGovernment}', " +
                       "which nothing declares";
            if (!IdeologyCatalog.Knows(report.NowIdeology))
                return $"{report.Nation.Tag} ended up believing '{report.NowIdeology}', " +
                       "which nothing declares";
        }

        // Nobody may do it twice inside the cooling-off period, because the cost of doing it
        // is the only thing that makes it a decision rather than a twitch.
        var byNation = new Dictionary<int, int>();
        foreach (var report in changes)
            byNation[report.Nation.Id] = byNation.GetValueOrDefault(report.Nation.Id) + 1;
        var mostChanges = 0;
        foreach (var count in byNation.Values) mostChanges = Math.Max(mostChanges, count);
        var ceiling = 365 * 15 / PoliticsCore.ChangeCooldownDays + 1;
        if (mostChanges > ceiling)
            return $"one country changed {mostChanges} times in fifteen years, past the " +
                   $"{ceiling} the cooling-off period allows";

        // And it has to be rare. A world where half the governments turn over in a decade is
        // not a simulation of politics, it is noise.
        if (byNation.Count > campaign.Nations.Count / 3)
            return $"{byNation.Count:N0} of {campaign.Nations.Count:N0} countries changed " +
                   "regime in fifteen years, which is not politics, it is weather";

        var kinds = new HashSet<string>();
        foreach (var report in changes) kinds.Add(report.TransitionId);

        Console.WriteLine($"  governance: {changes.Count:N0} change(s) of regime in fifteen years " +
                          $"across {byNation.Count:N0} countr(ies), by {kinds.Count:N0} of " +
                          $"{GovernanceEvents.All.Count:N0} declared route(s): " +
                          string.Join(", ", kinds.Take(5)) + ". The player's own was left alone.");
        return "";
    }

    internal static string EveryCountryHasACapital(Campaign campaign)
    {
        var nameless = new List<string>();
        var lost = new List<string>();
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrWhiteSpace(nation.CapitalName)) nameless.Add(nation.Tag);
            // Nobody's capital is on the equator at the prime meridian. That point is what
            // a country gets when nothing was written down for it.
            else if (MathF.Abs(nation.CapitalLatitude) < 0.001f &&
                     MathF.Abs(nation.CapitalLongitude) < 0.001f) lost.Add(nation.Tag);
        }

        if (nameless.Count > 0)
            return $"{nameless.Count:N0} countr(ies) have no capital at all: " +
                   string.Join(", ", nameless.Take(8));
        if (lost.Count > 0)
            return $"{lost.Count:N0} countr(ies) keep their capital at zero north, zero east, " +
                   $"which is the Gulf of Guinea: {string.Join(", ", lost.Take(8))}";

        // And the capital has to be somewhere on the country, not merely somewhere.
        var stranded = 0;
        var worst = "";
        var furthest = 0f;
        foreach (var nation in campaign.Nations)
        {
            var states = campaign.StatesOf(nation.MapIndex);
            if (states.Length == 0) continue;
            if (nation.CapitalStateMapIndex > 0) continue;
            stranded++;
            if (furthest <= 0f) worst = nation.Tag;
        }
        if (stranded > 0)
            return $"{stranded:N0} countr(ies) hold ground but their capital is in none of it, " +
                   $"starting with {worst}";

        Console.WriteLine($"  capitals: all {campaign.Nations.Count:N0} countries know where " +
                          "their capital is, and every one of them stands on their own ground.");
        return "";
    }

    internal static string FlagsFollowWhatACountryIs()
    {
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        if (FlagCatalog.All.Count == 0)
            return "common/flags declares no qualifiers, so a flag can only ever be the country's";

        var campaign = Campaign.CreateBootstrap(20240404);
        var russia = campaign.NationByTag("RUS");
        if (russia is null) return "RUS is missing, so there is nothing to change the politics of";

        // What it flies now, and what it flies after a revolution. These have to differ, or
        // the whole mechanism is decoration.
        var before = FlagCatalog.Resolve(campaign, russia);
        if (before != "flag_rus")
            return $"RUS as it stands resolves to '{before}' rather than its own flag";

        var wasIdeology = russia.Ideology;
        var wasGovernment = russia.Government;
        russia.Ideology = "communism";
        russia.Government = "one_party_state";
        var after = FlagCatalog.Resolve(campaign, russia);
        if (after == before)
            return "RUS went communist and went on flying exactly the same flag";
        if (after != "flag_rus_communism" && after != "flag_rus_communism_one_party_state")
            return $"RUS went communist and resolved to '{after}', which is not its communist flag";
        russia.Ideology = wasIdeology;
        russia.Government = wasGovernment;

        // One file, many conditions - and the two lines that must not be crossed.
        //
        // The first: a country's OWN flag beats a borrowed one. Russia is nationalist in
        // 2024 and flies a tricolour, and the moment a traditionalist flag existed for it to
        // borrow, it flew the Imperial black-yellow-white instead. Exact matches are taken
        // before the country's own flag; anything reached along an alias comes after it.
        russia.Ideology = "socialism";
        russia.Government = "presidential_republic";
        var borrowedTooEagerly = FlagCatalog.Resolve(campaign, russia);
        if (borrowedTooEagerly != "flag_rus")
            return $"a socialist Russia resolved to '{borrowedTooEagerly}' rather than its own " +
                   "flag, so something borrowed is beating something owned";

        // The second: an alias may not cross from a form of government into an ideology. A
        // one-party state may be communist and may equally be fascist, so the arrangement is
        // not evidence about the banner.
        russia.Ideology = "fascism";
        russia.Government = "one_party_state";
        foreach (var candidate in FlagCatalog.Candidates(campaign, russia))
            if (candidate.Contains("communism", StringComparison.Ordinal) ||
                candidate.Contains("socialism", StringComparison.Ordinal))
                return $"a fascist one-party Russia asked for '{candidate}', which is a banner " +
                       "it has no claim on";
        russia.Ideology = wasIdeology;
        russia.Government = wasGovernment;

        // And the complaint this whole mechanism exists for. China's flag is the flag of the
        // communist state - five stars, the large one the Party - so a China that has stopped
        // being communist must not go on flying it. It used to, because the country's own
        // flag was the bottom of the chain and the bottom of the chain always answers.
        var china = campaign.NationByTag("CHN");
        if (china is not null)
        {
            var wasChineseIdeology = china.Ideology;
            var wasChineseGovernment = china.Government;
            if (FlagCatalog.Resolve(campaign, china) != "flag_chn")
                return "China as it stands does not fly its own flag";

            china.Ideology = "conservatism";
            var afterwards = FlagCatalog.Resolve(campaign, china);
            if (afterwards == "flag_chn")
                return "a conservative China went on flying the flag of the communist state";
            if (afterwards.Length == 0)
                return "a conservative China was left with no flag at all";

            // And a communist China arranged some other way still flies it: the five stars
            // are about what the state believes, not about where the power sits.
            china.Ideology = wasChineseIdeology;
            china.Government = "presidential_republic";
            if (FlagCatalog.Resolve(campaign, china) != "flag_chn")
                return "a communist China that rearranged itself stopped flying its own flag";
            china.Government = wasChineseGovernment;
        }

        // A country the scenario never declared - which is what a secession produces - has
        // no file of its own and must still come out with something.
        var invented = new Nation
        {
            Tag = "ZZZ", Iso = "ZZZ", Name = "Nowhere",
            Ideology = "communism", Government = "provisional_government"
        };
        DemographyCatalog.Declare("ZZZ", new[] { "chechen" });
        var borrowed = FlagCatalog.Resolve(campaign, invented);
        if (borrowed != "flag_culture_chechen_communism")
            return $"a country founded by Chechens resolved to '{borrowed}' rather than their flag";

        // And the same people, having decided something, may fly something else again.
        DemographyCatalog.Declare("ZZZ", new[] { "kurdish" });
        invented.Ideology = "socialism";
        var variant = FlagCatalog.Resolve(campaign, invented);
        if (variant != "flag_culture_kurdish_socialism")
            return $"a socialist Kurdish state resolved to '{variant}' rather than its own banner";

        // Who a country answers to, which outranks everything else a flag can know. The
        // order in common/flags says so - subject at 7, ideology at 10 - and the reason is
        // Vichy France, which flew neither the Republic's flag nor a fascist one but what an
        // occupied France was allowed to fly. Worth checking now that twenty-five files hang
        // off this qualifier.
        var ceylon = campaign.NationByTag("LKA");
        var britain = campaign.NationByTag("GBR");
        if (ceylon is not null && britain is not null)
        {
            var heldOverlord = ceylon.OverlordId;
            var heldKind = ceylon.SubjectType;
            var heldPolitics = ceylon.Ideology;

            ceylon.OverlordId = britain.Id;
            ceylon.SubjectType = "dominion";
            var asDominion = FlagCatalog.Resolve(campaign, ceylon);
            if (asDominion != "flag_lka_dominion")
                return $"Ceylon as a dominion resolved to '{asDominion}' rather than its dominion flag";

            // A kind of dependency nothing has drawn a flag for falls back to the national
            // flag rather than to nothing - a satellite flew its own flag, which is right.
            ceylon.SubjectType = "satellite";
            var asSatellite = FlagCatalog.Resolve(campaign, ceylon);
            if (asSatellite != "flag_lka")
                return $"a satellite with no flag of its own resolved to '{asSatellite}'";

            ceylon.OverlordId = heldOverlord;
            ceylon.SubjectType = heldKind;
            ceylon.Ideology = heldPolitics;
            if (FlagCatalog.Resolve(campaign, ceylon) != "flag_lka")
                return "Ceylon stopped answering to anybody and did not go back to its own flag";
        }

        // And no colonial ensign or wartime puppet banner comes back. The base game ships none
        // (they live in mods/_templates/historical_flags): Palestine under Russian protection
        // used to fly the ensign of the British Mandate. A fascist puppet China flies China's
        // flag, because nothing else on disk is China's.
        if (china is not null && britain is not null)
        {
            var heldOverlord = china.OverlordId;
            var heldKind = china.SubjectType;
            var heldPolitics = china.Ideology;

            china.OverlordId = britain.Id;
            china.SubjectType = "puppet";
            china.Ideology = "fascism";
            var installed = FlagCatalog.Resolve(campaign, china);
            // One of China's own flags - the national one or a variant of it, such as the
            // Republic's, which a puppet answering to a liberal overlord reaches for - and never
            // a puppet banner or a fascist one, which the base game no longer ships.
            if (!installed.StartsWith("flag_chn", StringComparison.Ordinal) ||
                installed.Contains("puppet") || installed.Contains("protectorate") || installed.Contains("fascism"))
                return $"a fascist puppet China flew '{installed}' rather than one of its own flags";
            var palestine = campaign.NationByTag("PSE");
            if (palestine is not null)
            {
                var heldPalestine = (palestine.OverlordId, palestine.SubjectType);
                palestine.OverlordId = britain.Id;
                palestine.SubjectType = "protectorate";
                var mandate = FlagCatalog.Resolve(campaign, palestine);
                (palestine.OverlordId, palestine.SubjectType) = heldPalestine;
                if (mandate != "flag_pse")
                    return $"a protected Palestine flew '{mandate}' rather than the Palestinian flag";
            }

            china.OverlordId = heldOverlord;
            china.SubjectType = heldKind;
            china.Ideology = heldPolitics;
        }

        // And the one condition that is not politics at all. The Philippines is the whole
        // reason the `at_war` qualifier exists: its flag is flown red-field-up, by law and
        // only, while the country is at war. Three other countries were filed under it once
        // - Bolivia's and Peru's state flags and Japan's naval ensign - and none of them was
        // a war flag; this is what the condition is for, so this is what is checked.
        var philippines = campaign.NationByTag("PHL");
        if (philippines is not null)
        {
            if (FlagCatalog.Resolve(campaign, philippines) != "flag_phl")
                return "the Philippines at peace does not fly its own flag";

            var enemy = campaign.Nations.FirstOrDefault(
                other => other.Id != philippines.Id && !other.IsSubject &&
                         CampaignDiplomacy.CanDeclareWar(campaign, other.Id, philippines.Id, out _));
            if (enemy is null)
                return "nobody in the world was able to declare war on the Philippines";

            CampaignDiplomacy.DeclareWar(campaign, enemy.Id, philippines.Id);
            var wartime = FlagCatalog.Resolve(campaign, philippines);
            if (wartime != "flag_phl_at_war")
                return $"the Philippines went to war and flew '{wartime}' rather than its war flag";

            // And the flag comes back down again. `at_peace` is declared silent, so this is
            // also the check that a silent value drops out of the name rather than becoming
            // a file nobody has.
            foreach (var war in campaign.Wars) war.Ended = true;
            var afterwards = FlagCatalog.Resolve(campaign, philippines);
            if (afterwards != "flag_phl")
                return $"the war ended and the Philippines went on flying '{afterwards}'";
        }

        // Every flag on disk has to be reachable by the name it is filed under: a picture
        // nothing can ask for is a picture that is not in the game.
        var unreachable = 0;
        var counted = 0;
        var folder = GameDataLoader.DataFolder("gfx", "flags");
        if (folder is not null)
            foreach (var file in Directory.EnumerateFiles(folder, "flag_*.png"))
            {
                counted++;
                if (!SpriteCatalog.Has(Path.GetFileNameWithoutExtension(file))) unreachable++;
            }
        if (unreachable > 0)
            return $"{unreachable:N0} of {counted:N0} flag file(s) cannot be asked for by name";

        var ideological = 0;
        var peoples = 0;
        if (folder is not null)
            foreach (var file in Directory.EnumerateFiles(folder, "flag_*.png"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.StartsWith("flag_culture_", StringComparison.Ordinal)) peoples++;
                else if (name.Count(character => character == '_') > 1) ideological++;
            }

        // What the files actually cover, which is the number that matters. Every country
        // against every ideology and every form of government is a few tens of thousands of
        // conditions; the question is how many of them find something other than the plain
        // national flag, and aliases are what make that number far larger than the file
        // count. Measured rather than asserted - a coverage that falls is worth seeing.
        var combinations = 0;
        var answered = 0;
        foreach (var nation in campaign.Nations)
        {
            var heldIdeology = nation.Ideology;
            var heldGovernment = nation.Government;
            foreach (var ideology in IdeologyCatalog.All)
            foreach (var government in GovernmentCatalog.All)
            {
                nation.Ideology = ideology.Id;
                nation.Government = government.Id;
                combinations++;
                var resolved = FlagCatalog.Resolve(campaign, nation);
                if (resolved.Length > 0 && resolved != $"flag_{nation.Tag.ToLowerInvariant()}")
                    answered++;
            }
            nation.Ideology = heldIdeology;
            nation.Government = heldGovernment;
        }
        Console.WriteLine($"  flag coverage: {answered:N0} of {combinations:N0} country-ideology-" +
                          $"government conditions find a flag of their own rather than the plain " +
                          $"national one - {answered / (float)Math.Max(1, combinations):P1}, from " +
                          $"{counted:N0} files.");

        Console.WriteLine($"  flags: {counted:N0} on disk - {peoples:N0} for peoples and " +
                          $"{ideological:N0} for a country in a particular condition; " +
                          $"{FlagCatalog.All.Count} qualifier(s) and " +
                          $"{FlagCatalog.FallbackSources.Count} fallback famil(ies) declared, " +
                          "and a country with no flag of its own still flies one.");
        return "";
    }

    /// <summary>
    /// A total conversion may redraw the world; it must not be fought by the armies of 2024.
    ///
    /// `history/units` was the one history folder the mod loader never read. A mod could
    /// move every border, rename every country and rewrite every economy, and the
    /// standing armies and the navies would still be the base game's - which is exactly
    /// the kind of hole that only shows up once somebody tries to build the mod.
    ///
    /// `fonts/` is checked alongside it because it is the same class of hole: a mod can
    /// translate every string in the game and render a screen of empty boxes, since the
    /// face the interface is set in was the engine's and nothing could say otherwise.
    /// </summary>
    /// <summary>
    /// The world's 1,826 regions were a C# array until the day this check was written, and
    /// the thing worth guarding is not that they moved but that they stayed moved: a
    /// generated file nothing verifies is a file that drifts from the game the first time
    /// somebody edits the generator.
    /// </summary>
    /// <summary>
    /// A region holds the people who live in it, and a country's farms and factories do not
    /// depend on how finely the map happens to cut it.
    ///
    /// A country's people used to be dealt almost evenly across its regions - Magadan as big
    /// as Moscow, Iowa as Florida - and each region rounded its own farms and factories down,
    /// so a country cut into many small regions opened with a farm apiece and no industry at
    /// all: Russia, France, Britain, Turkey and Poland among them. The people are now where
    /// history/states/2024/ writes for each region, and the buildings are counted for the
    /// country and dealt to its regions.
    /// </summary>
    internal static string RegionsHoldTheirPeople()
    {
        // Its own world: the sandbox's regions have been fought over by the time this runs.
        var campaign = Campaign.CreateBootstrap(20240302);
        var byId = campaign.States.ToDictionary(state => state.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (big, small, factor) in new[] { ("ru_mow", "ru_mag", 20.0), ("ru_len", "ru_chu", 20.0), ("us_fl", "us_ia", 3.0) })
        {
            if (!byId.TryGetValue(big, out var many) || !byId.TryGetValue(small, out var few))
                return $"the map has no region {(byId.ContainsKey(big) ? small : big)}";
            if (many.Population < few.Population * factor)
                return $"{many.Name} holds {many.Population:N0} people and {few.Name} {few.Population:N0}: " +
                       "the deal is still spreading a country's people evenly";
        }

        // What the file writes is what the region has, and a country it lists is the sum of it.
        var written = HistoryDataLoader.StatedPopulations(campaign);
        if (written.Count < 1000)
            return $"only {written.Count:N0} region(s) have a population written down";
        var byIndex = campaign.States.ToDictionary(state => state.MapIndex);
        var wrong = written.Where(pair => byIndex.TryGetValue(pair.Key, out var state) && state.Population != pair.Value)
            .Select(pair => $"{byIndex[pair.Key].Id} has {byIndex[pair.Key].Population:N0}, the file says {pair.Value:N0}")
            .Take(3).ToList();
        if (wrong.Count > 0) return string.Join("; ", wrong);

        // A city region is a city: more urban than its country, and holding fewer of its farms
        // than its people alone would give it.
        var moscow = byId["ru_mow"];
        var russia = campaign.NationByTag("RUS");
        if (moscow.Urbanization <= EconomySystem.NationalUrbanisation(russia))
            return $"{moscow.Name} is {moscow.Urbanization:P0} urban, no more than Russia as a whole";
        var russianFarms = campaign.StatesOf(russia).Sum(state => state.Buildings.GetValueOrDefault(BuildingKind.Agriculture));
        var moscowFarms = moscow.Buildings.GetValueOrDefault(BuildingKind.Agriculture);
        if (moscowFarms > 0 && moscowFarms >= russianFarms * (double)moscow.Population / Math.Max(1, russia.Population))
            return $"{moscow.Name} holds {moscowFarms} of Russia's {russianFarms} farms - a city's share of the countryside";

        // Farms and factories counted for the country.
        var defines = campaign.Defines;
        foreach (var tag in new[] { "RUS", "FRA", "GBR", "TUR", "POL" })
        {
            var nation = campaign.NationByTag(tag);
            var regions = campaign.StatesOf(nation);
            var industry = regions.Sum(state => state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry));
            var farms = regions.Sum(state => state.Buildings.GetValueOrDefault(BuildingKind.Agriculture));
            if (industry == 0)
                return $"{nation.Name} opens with no civilian industry across {regions.Length} region(s)";
            var expected = Math.Max(1, (int)Math.Round(regions.Sum(state => (double)state.Population) / defines.SeedPeoplePerFarm));
            if (Math.Abs(farms - expected) > 1)
                return $"{nation.Name} opens with {farms} farm(s) for {nation.Population:N0} people, where the country's " +
                       $"figure is {expected}: the regions are rounding for themselves again";
        }

        // The markets were tuned against the world's old total, which the recount keeps.
        var world = campaign.States.Sum(state => state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry));
        if (world < 380 || world > 480)
            return $"the world opens with {world:N0} civilian industries, outside the 380-480 the markets are tuned for";

        Console.WriteLine($"  regions: {written.Count:N0} with their people written down; {byId["ru_mow"].Name} " +
                          $"{byId["ru_mow"].Population:N0}, {byId["ru_mag"].Name} {byId["ru_mag"].Population:N0}; " +
                          $"{world:N0} civilian industries in the world, none of the five countries above without them.");
        return "";
    }

    /// <summary>
    /// Decisions are content, and content the AI acts on.
    ///
    /// Two holes, one check. A decision used to need a `kind` - a consequence compiled
    /// into the executable - so a mod could rename the five the game shipped and write no
    /// sixth; and no AI had ever taken one, so two hundred and sixty governments sat on
    /// the same five levers for a whole campaign while only the player pulled them.
    ///
    /// The check refuses to accept either half on its own: the libraries have to load and
    /// compose, a shipped decision has to be nothing but data, and somebody who is not the
    /// player has to have actually done something over two years.
    /// </summary>
    internal static string GovernmentsTakeScriptedDecisions(Campaign campaign, int days)
    {
        if (ScriptLibrary.TriggerCount == 0) return "common/scripted_triggers declares nothing";
        if (ScriptLibrary.EffectCount == 0) return "common/scripted_effects declares nothing";
        if (ScriptLibrary.ScoreCount == 0) return "common/scripted_modifiers declares nothing";

        // A decision that needs no compiled case is the one a mod could write.
        var scripted = DecisionCatalog.All
            .Where(decision => decision.Kind == DecisionKind.Scripted)
            .ToList();
        if (scripted.Count == 0)
            return "every shipped decision still needs a compiled kind, so a mod cannot write one";
        var toothless = scripted.FirstOrDefault(decision => decision.Effect is null || decision.Effect.IsEmpty);
        if (toothless.Id is not null)
            return $"scripted decision '{toothless.Id}' does nothing: its effects did not parse";
        var ungated = scripted.FirstOrDefault(decision => decision.Trigger is null || decision.Trigger.IsEmpty);
        if (ungated.Id is not null)
            return $"scripted decision '{ungated.Id}' has no condition, so it is always on offer";

        // `use` has to actually fold a named block in, or the libraries are decoration.
        using (var document = JsonDocument.Parse("{ \"use\": [ \"restive\" ] }"))
        {
            var borrowed = ScriptedTrigger.Parse(document.RootElement, "bench", null);
            if (borrowed.IsEmpty) return "a trigger that uses a named one came out empty";

            var calm = campaign.Nations.Find(nation => nation.Stability > 0.60f);
            var troubled = campaign.Nations.Find(nation => nation.Stability < 0.40f);
            if (calm is not null && borrowed.Matches(campaign, calm))
                return $"'restive' matched {calm.Tag}, whose stability is {calm.Stability:P0}";
            if (troubled is not null && !borrowed.Matches(campaign, troubled))
                return $"'restive' refused {troubled.Tag}, whose stability is {troubled.Stability:P0}";
        }

        var acted = campaign.Nations.Count(nation => !nation.IsHuman && nation.DecisionAvailableDay.Count > 0);
        if (acted == 0)
            return $"no government took a decision in {days:N0} day(s); the AI cabinet is not sitting";

        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var nation in campaign.Nations)
            foreach (var decision in nation.DecisionAvailableDay.Keys)
                taken[decision] = taken.GetValueOrDefault(decision) + 1;
        var popular = string.Join(", ", taken
            .OrderByDescending(entry => entry.Value)
            .Take(4)
            .Select(entry => $"{entry.Key} x{entry.Value:N0}"));

        Console.WriteLine($"  cabinets: {acted:N0} of {campaign.Nations.Count:N0} governments took a decision " +
                          $"in {days:N0} day(s) - {popular}.");
        Console.WriteLine($"  script libraries: {ScriptLibrary.TriggerCount:N0} trigger(s), " +
                          $"{ScriptLibrary.EffectCount:N0} effect(s), {ScriptLibrary.ScoreCount:N0} score(s); " +
                          $"{scripted.Count:N0} decision(s) are data alone.");
        return "";
    }

    /// <summary>
    /// An opinion has to be made of reasons, and the reasons have to fade.
    ///
    /// Opinion was one number that two buttons moved and a war slammed to minus sixty. It
    /// remembered nothing, so nothing could explain it to the player, and it forgot
    /// nothing either: a war fought in 2024 held a country at minus sixty in 2124, while a
    /// decade of patient diplomacy read exactly like a bribe paid last Tuesday.
    ///
    /// The check holds both halves. The parts have to add up to the number - an opinion
    /// that does not equal its own breakdown is a screen that lies - and time has to
    /// actually work on them.
    /// </summary>
    internal static string OpinionIsRememberedAndForgotten(Campaign campaign)
    {
        if (OpinionCatalog.Count == 0) return "common/opinion_modifiers declares nothing";

        var first = campaign.Nations.Find(nation => nation.Population > 0);
        var second = campaign.Nations.Find(nation =>
            nation != first && nation.Population > 0 &&
            !campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)));
        if (first is null || second is null) return "not enough countries to test with";

        var relation = CampaignDiplomacy.Get(campaign, first.Id, second.Id);
        relation.Reasons.Clear();
        OpinionSystem.Settle(campaign, relation);
        // Two countries with no history of each other are not strangers any more: they can
        // see what the other is. So what is left when the reasons are cleared has to be
        // politics, declared identity and public reputation. Identity is not a historical
        // opinion reason either: clearing reasons does not abolish either state church.
        var politics = PoliticsCore.Affinity(campaign, first.Id, second.Id);
        var identity = StateIdentity.Affinity(campaign, first.Id, second.Id);
        var reputation = SocialMemory.OpinionEffect(campaign, relation);
        var baseline = Math.Clamp(politics + identity + reputation, -100f, 100f);
        if (Math.Abs(relation.Opinion - baseline) > 0.01f)
            return $"a country with no history of another has an opinion of {relation.Opinion:0.0}, " +
                   $"where politics account for {politics:0.0}, identity for {identity:0.0} and public reputation for {reputation:0.0}";

        OpinionSystem.Remember(campaign, relation, "declared_war_on_us");
        var remembered = relation.Opinion;
        if (remembered > Math.Max(-100f, baseline - 50f))
            return $"a declaration of war moved opinion to {remembered:0.0}, which is barely a grudge";

        // What the screen shows has to equal what it is made of.
        var parts = 0f;
        foreach (var entry in relation.OpinionDrivers.Entries) parts += entry.Value;
        if (Math.Abs(parts - relation.Opinion) > 0.5f)
            return $"the opinion reads {relation.Opinion:0.0} and its parts add to {parts:0.0}";

        // And it fades. Nine years on, the same grudge has to be worth less.
        var reason = relation.Reasons.Find(item =>
            string.Equals(item.Id, "declared_war_on_us", StringComparison.OrdinalIgnoreCase));
        if (reason is null) return "the declaration of war was not recorded as a reason";
        reason.GrantedDay = campaign.ElapsedDays - 3285;
        reason.ExpiresDay = campaign.ElapsedDays + 365;
        OpinionSystem.Settle(campaign, relation);
        var faded = relation.Opinion;
        if (faded <= remembered + 5f)
            return $"nine years on the grudge is still {faded:0.0}, having started at {remembered:0.0}";

        // Expired reasons are gone rather than lingering at zero weight.
        reason.ExpiresDay = campaign.ElapsedDays - 1;
        OpinionSystem.Settle(campaign, relation);
        if (relation.Reasons.Any(item =>
                string.Equals(item.Id, "declared_war_on_us", StringComparison.OrdinalIgnoreCase)))
            return "a reason that ran out is still on the books";

        relation.Reasons.Clear();
        OpinionSystem.Settle(campaign, relation);

        var held = 0;
        foreach (var entry in campaign.Relations.Values) held += entry.Reasons.Count;
        Console.WriteLine($"  opinion: {OpinionCatalog.Count:N0} kind(s) of reason; the world is holding " +
                          $"{held:N0} of them across {campaign.Relations.Count:N0} relation(s).");
        return "";
    }

    /// <summary>
    /// Stability is a formula now rather than a stored number, and two things have to hold.
    /// The scenario still says what it said - 261 countries did not quietly become more or
    /// less settled on the day the formula was switched on - and the capital weighs more
    /// than its size, which is the claim the whole model rests on.
    /// </summary>
    /// <summary>
    /// The militia has to do three things or it is a cheat rather than a mechanic: rise
    /// where a country is actually invaded, rise far less where the people do not think the
    /// state is theirs, and cost nothing out of the pool the regular army reinforces from -
    /// which it did draw on at first, with the result that the country it was meant to help
    /// lost a quarter of its ground.
    /// </summary>
    /// <summary>
    /// The mechanic that lets a border move without a war.
    ///
    /// A country holding a region that is wholly somebody else's people, furious and
    /// ungoverned, used to be able to do so for a century with nothing ever coming of it -
    /// every border in the world that was not drawn by a war between two states had no
    /// mechanism at all. This is that mechanism, and it has four ways to be wrong: nothing
    /// leaves, what leaves is not a country, the parent loses more than it should, or two
    /// peers disagree about any of it.
    /// </summary>
    internal static string RegionsThatAreNotTheirsCanLeave()
    {
        var campaign = Campaign.CreateBootstrap(20240707);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var before = campaign.Nations.Count;
        var tags = new HashSet<string>(campaign.Nations.Select(nation => nation.Tag), StringComparer.OrdinalIgnoreCase);
        var indices = new HashSet<int>(campaign.Nations.Select(nation => nation.MapIndex));

        // Fifteen years. The calibration says the worst region in the world goes in about
        // ten under a state that governs itself, so this is that with room for the state to
        // govern itself unusually well.
        var born = new List<SecessionReport>();
        var bornOn = new Dictionary<SecessionReport, int>();
        for (var day = 0; day < 365 * 15; day++)
        {
            campaign.TickDay();
            foreach (var report in SecessionEvents.Recent) bornOn.TryAdd(report, campaign.ElapsedDays);
            born.AddRange(SecessionEvents.Recent);
        }

        if (born.Count == 0)
            return "fifteen years of the 2024 world and not one region left the country holding it, " +
                   "so the mechanic is calibrated out of existence";

        foreach (var report in born)
        {
            var country = report.Seceded;
            if (country is null) return "a secession reported no country";
            if (!tags.Add(country.Tag))
                return $"{country.Tag} was handed a tag somebody was already using";
            if (!indices.Add(country.MapIndex))
                return $"{country.Tag} was handed map index {country.MapIndex}, which is already painted";
            if (string.IsNullOrWhiteSpace(country.Name))
                return $"{country.Tag} has no name";
            if (string.IsNullOrWhiteSpace(country.Government) || string.IsNullOrWhiteSpace(country.Ideology))
                return $"{country.Tag} is governed by nothing";
            if (report.Population < SecessionCore.Weights.MinimumPopulation)
                return $"{country.Tag} was founded with {report.Population:N0} people, under the minimum";

            // It cannot be a dissolution. The cap exists because a country losing four
            // fifths of itself in one afternoon is not a secession, it is a bug with a flag.
            //
            // One region is the exception, and a real one: a single region that happens to be
            // most of the country cannot be cut in half to fit a ceiling, and a country that
            // is one big region and several small ones is entitled to lose the big one.
            var share = report.ParentPopulation <= 0
                ? 0f
                : report.Population / (float)report.ParentPopulation;
            if (report.Regions > 1 && share > SecessionCore.Weights.MaximumShare + 0.05f)
                return $"{report.Parent.Tag} lost {share:P0} of itself across {report.Regions} " +
                       $"region(s) at once, past the cap of {SecessionCore.Weights.MaximumShare:P0}";
        }

        // Whether anybody ever fights to keep a province.
        //
        // A refused secession declares a war, and if that declaration is itself refused -
        // a truce, an alliance, no way to reach them - the code downgrades the refusal to
        // an acceptance and says so. Which of those actually happens has never been
        // measured, and "I have never seen a secession put down by force" is a report
        // about the outcome, not about the branch that produced it.
        var contested = born.Count(report => report.Outcome == SecessionOutcome.Contested);
        var foughtOver = 0;
        var stillFighting = 0;
        foreach (var report in born)
        {
            if (report.Seceded is null || report.Parent is null) continue;
            foreach (var war in campaign.Wars)
            {
                if (!war.Involves(report.Seceded.Id) || !war.Involves(report.Parent.Id)) continue;
                foughtOver++;
                if (!war.Ended) stillFighting++;
                break;
            }
        }
        Console.WriteLine($"  secession: {born.Count} founded, {contested} contested, " +
                          $"{foughtOver} met a war of independence, {stillFighting} of those " +
                          $"were still being fought at the end.");
        foreach (var report in born)
            Console.WriteLine($"    day {bornOn.GetValueOrDefault(report)}: {report.Seceded?.Tag} out of {report.Parent?.Tag} " +
                              $"({report.Pieces.Count} region(s), {report.Population:N0} people) - {report.Reason}");
        if (contested == 0)
            return $"{born.Count} countries were born and every single parent let them go, " +
                   "so nothing is ever put down by force";
        if (foughtOver == 0)
            return $"{contested} secession(s) were refused and not one of them produced a war " +
                   "between the parent and the country that left";

        // And the ground actually moved. A country in the nation list that owns nothing is
        // the failure mode this whole path is most likely to produce.
        foreach (var report in born)
        {
            var country = report.Seceded;
            var held = campaign.StatesOf(country.MapIndex).Length;
            var conquered = campaign.Wars.Any(war => war.Involves(country.Id));
            if (held == 0 && !conquered)
                return $"{country.Tag} ({country.Name}, map index {country.MapIndex}) exists, is at " +
                       $"peace, and owns no ground: it was founded on {report.Regions} region(s) and " +
                       $"{report.Population:N0} people from {report.Parent.Tag}, {report.Outcome}. " +
                       $"Those regions are now held by " +
                       string.Join(", ", report.Pieces.Select(piece =>
                           campaign.States.FirstOrDefault(state => state.MapIndex == piece.MapIndex) is { } found
                               ? $"{piece.Name}={campaign.Nations.FirstOrDefault(n => n.MapIndex == found.NationMapIndex)?.Tag ?? "?"}"
                               : $"{piece.Name}=gone"));
            if (held == 0) continue;

            if (campaign.ProvinceController is { } control)
            {
                // Held by itself or by an enemy it is fighting: a country whose one province is
                // occupied is being overrun, not lost track of - Macau, seceded from Vietnam and
                // taken by Taiwan in the war that followed. Held by anybody else, the region was
                // handed over and the ground never was.
                var provinces = 0;
                var elsewhere = "";
                var regions = new HashSet<int>(campaign.StatesOf(country.MapIndex).Select(region => region.MapIndex));
                for (var province = 1; province < control.Length; province++)
                {
                    if (control[province] == (ushort)country.MapIndex) { provinces++; continue; }
                    if (!regions.Contains(campaign.ProvinceMap.ProvinceToState[province])) continue;
                    var holder = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == control[province]);
                    if (holder is null || !CampaignDiplomacy.AtWar(campaign, holder.Id, country.Id))
                        elsewhere = holder?.Tag ?? "nobody";
                }
                if (provinces == 0 && elsewhere.Length > 0)
                    return $"{country.Tag} owns {held} region(s) and holds not one province of them: " +
                           $"{elsewhere} does, and is not at war with it";
            }
        }

        // What the user asked for, and the thing that makes it look like a secession rather
        // than a language map: at least some of what left went for its own reasons rather
        // than its nationality. Reported rather than required, because a world where every
        // secession happened to be one clean people is a legitimate world.
        var joined = born.Sum(report => report.Pieces.Count(piece => piece.Cause == SecessionCause.Discontent));
        var core = born.Sum(report => report.Pieces.Count(piece => piece.Cause == SecessionCause.Nationality));

        // Two worlds, the same seed, the same answer. A mechanic that adds countries is the
        // worst possible thing to be non-deterministic about: the peers agree for years and
        // then one of them has a country the other has never heard of.
        //
        // Stepped side by side rather than one after the other, and deliberately. Side by
        // side is the real case - a multiplayer client rebuilding from a snapshot beside the
        // world it is replacing - and it is also the only one this can answer cleanly: two
        // campaigns run one after the other in a single process already diverge slightly
        // after three years with no secession anywhere in them, so something global outlives
        // a campaign. That is a real bug and an older one, and testing against it here would
        // only mean this check failed for somebody else's reason.
        var left = Campaign.CreateBootstrap(20240808);
        var right = Campaign.CreateBootstrap(20240808);
        if (!AttachMap(left) || !AttachMap(right)) return "";
        left.IsPaused = false;
        right.IsPaused = false;
        var founded = left.Nations.Count;
        for (var day = 0; day < 365 * 15; day++) { left.TickDay(); right.TickDay(); }

        if (left.Nations.Count == founded)
            return "the world used to check that two peers come apart alike never came apart, " +
                   "so the check proves nothing";
        if (left.Nations.Count != right.Nations.Count)
            return $"two peers stepping together produced {left.Nations.Count:N0} countries and " +
                   $"{right.Nations.Count:N0}";
        if (CampaignFingerprint.Of(left) != CampaignFingerprint.Of(right))
            return "two peers stepping together came apart differently: " + WhatDiffers(left, right);

        var accepted = born.Count(report => report.Outcome == SecessionOutcome.Accepted);
        Console.WriteLine($"  secession: {born.Count:N0} country(ies) declared themselves in fifteen years " +
                          $"({campaign.Nations.Count - before:N0} still on the list), {accepted:N0} let go and " +
                          $"{born.Count - accepted:N0} fought over; {core:N0} region(s) left for their people " +
                          $"and {joined:N0} went along for their own reasons.");
        // What kind of revolt each one was, which is the thing a newborn country knows about
        // itself and the only basis it has for a flag. A world where every secession comes
        // back "discontent" means the core-finding is not distinguishing anything.
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var report in born)
        {
            var kind = string.IsNullOrEmpty(report.Revolt) ? "(none)" : report.Revolt;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            if (report.Seceded.FoundedInRevolt != report.Revolt)
                return $"{report.Seceded.Tag} was founded in a '{report.Revolt}' revolt and " +
                       $"remembers '{report.Seceded.FoundedInRevolt}'";
        }
        if (kinds.ContainsKey("(none)"))
            return $"{kinds["(none)"]} of {born.Count} new countries do not know what founded them";
        Console.WriteLine("    revolts: " + string.Join(", ",
            kinds.OrderByDescending(entry => entry.Value).Select(entry => $"{entry.Key} {entry.Value}")));

        foreach (var report in born.Take(6))
            Console.WriteLine($"    {report.Parent.Tag} -> {report.Seceded.Tag} ({report.Seceded.Name}): " +
                              $"{report.Regions} region(s), {report.Population:N0} people, " +
                              $"{report.Revolt}, {report.Reason}");
        return "";
    }

    internal static string MilitiaRisesWhereItIsWanted()
    {
        var campaign = Campaign.CreateBootstrap(20240505);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var ukraine = campaign.NationByTag("UKR");
        if (ukraine is null) return "UKR is missing, so there is no invasion to rise against";
        var reserveBefore = ukraine.ManpowerReserve;

        for (var day = 0; day < 200; day++) campaign.TickDay();

        var risen = campaign.Armies
            .Where(army => army.TemplateId == MilitiaCore.TemplateId)
            .ToList();
        if (risen.Count == 0)
            return "two hundred days of a live invasion and not one person picked up a rifle";

        var defending = risen.Count(army => army.NationId == ukraine.Id);
        if (defending == 0)
            return $"{risen.Count:N0} militia formations stood up in the world and none of them " +
                   "in the country being invaded";

        // Militia is the region's own people, not the ministry's reservists. The reserve may
        // move for every other reason - recruitment, casualties - but not because of this.
        foreach (var army in risen)
            if (army.Personnel <= 0)
                return "a militia formation stood up with nobody in it";

        // Who rises. A region the state is for fills a formation; a region held down by a
        // state it does not recognise does not, however many people live in it.
        var willing = 0f;
        var unwilling = 0f;
        foreach (var state in campaign.StatesOf(ukraine.MapIndex))
        {
            var own = 1f - EconomySystem.ForeignShare(state, ukraine.Tag);
            var here = campaign.Armies.Count(army => army.TemplateId == MilitiaCore.TemplateId &&
                                                     army.StateMapIndex == state.MapIndex);
            if (own >= 0.8f) willing += here;
            else unwilling += here;
        }
        if (willing <= 0f)
            return "nobody rose in any region that is mostly the country's own people";

        // And it goes home. Nothing has threatened a country at peace, so its militia
        // should not be standing a year later - though it may for the month it takes to stand
        // down (StandDownDays) after a war that has only just ended. Asked of any country at
        // peace, that month used to fail the check whenever a war happened to end in the
        // last weeks of the run.
        int LastWarEnded(Nation nation)
        {
            var ended = -1_000_000; // never: long before any stand-down
            foreach (var war in campaign.Wars)
                if (war.Ended && war.Involves(nation.Id)) ended = Math.Max(ended, war.EndedDay);
            return ended;
        }
        var standingDown = MilitiaCore.Weights.StandDownDays + 7;
        var peaceful = campaign.Nations.FirstOrDefault(nation =>
            nation.Id != ukraine.Id &&
            campaign.Armies.Any(army => army.TemplateId == MilitiaCore.TemplateId &&
                                        army.NationId == nation.Id) &&
            !campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)) &&
            campaign.ElapsedDays - LastWarEnded(nation) > standingDown);
        if (peaceful is not null)
            return $"{peaceful.Tag} is at peace and still has militia standing " +
                   $"{campaign.ElapsedDays - LastWarEnded(peaceful):N0} day(s) after its last war";

        Console.WriteLine($"  militia: {risen.Count:N0} formation(s) stood up in 200 days, " +
                          $"{defending:N0} of them in UKR; {willing:N0} in regions that are mostly " +
                          $"its own people against {unwilling:N0} elsewhere; the reserve went from " +
                          $"{reserveBefore:N0} to {ukraine.ManpowerReserve:N0} for every other reason.");
        return "";
    }

    internal static string StabilityIsWorkedOutRatherThanStored()
    {
        var campaign = Campaign.CreateBootstrap(20240202);
        if (!AttachMap(campaign)) return "";

        // What the scenario authored is what the formula returns, for everybody.
        var worst = 0f;
        Nation worstNation = null;
        foreach (var nation in campaign.Nations)
        {
            // A movement without ground has no regions for its stability to be worked out from.
            if (nation.Landless) continue;
            var drift = MathF.Abs(StabilityCore.Target(campaign, nation) - nation.Stability);
            if (drift <= worst) continue;
            worst = drift;
            worstNation = nation;
        }
        if (worst > 0.005f)
            return $"the formula disagrees with the scenario by {worst:P1} for " +
                   $"{worstNation?.Tag}: it is authored at {worstNation?.Stability:P1}";

        // A country big enough to have a capital and somewhere else worth comparing it to.
        var subject = campaign.Nations
            .Where(nation => nation.CapitalStateMapIndex > 0 && nation.Population > 20_000_000)
            .OrderByDescending(nation => campaign.StatesOf(nation.MapIndex).Length)
            .FirstOrDefault();
        if (subject is null) return "no country large enough to tell a capital from a province";

        var states = campaign.StatesOf(subject.MapIndex);
        var capital = states.FirstOrDefault(state => state.MapIndex == subject.CapitalStateMapIndex);
        if (capital is null) return $"{subject.Tag}'s capital is not in one of its own regions";
        var elsewhere = states
            .Where(state => state.MapIndex != capital.MapIndex && state.Population > 0)
            .OrderBy(state => MathF.Abs(state.Population - capital.Population))
            .FirstOrDefault();
        if (elsewhere is null) return $"{subject.Tag} has nowhere to compare its capital with";

        float Upset(ScenarioState state)
        {
            var wasRadical = state.Radicalism;
            var wasLiving = state.StandardOfLiving;
            try
            {
                state.Radicalism = 1f;
                state.StandardOfLiving = 0f;
                return StabilityCore.Target(campaign, subject);
            }
            finally
            {
                state.Radicalism = wasRadical;
                state.StandardOfLiving = wasLiving;
            }
        }

        var settled = StabilityCore.Target(campaign, subject);
        var withoutCapital = Upset(capital);
        var withoutProvince = Upset(elsewhere);

        if (withoutCapital >= settled)
            return $"setting fire to {subject.Tag}'s capital region did not cost it any stability";
        if (withoutCapital >= withoutProvince)
            return $"{subject.Tag}'s capital ({capital.Name}, {capital.Population:N0}) is worth no more " +
                   $"than {elsewhere.Name} ({elsewhere.Population:N0}): {withoutCapital:P1} against " +
                   $"{withoutProvince:P1}";

        Console.WriteLine($"  stability: all {campaign.Nations.Count:N0} countries agree with the scenario " +
                          $"(worst {worst:P2}); for {subject.Tag}, losing {capital.Name} costs " +
                          $"{settled - withoutCapital:P1} against {settled - withoutProvince:P1} for " +
                          $"{elsewhere.Name} of the same size.");
        return "";
    }

    /// <summary>
    /// Who governs, by what right, and what everyone else makes of it.
    ///
    /// Three things, and the third is the one worth having a check for. A government and an
    /// ideology that only affect the country holding them are a label; what makes them a
    /// system is that the rest of the world can see them, so this proves that two countries
    /// with nothing else between them still have an opinion, that it is warm when their
    /// politics are close and cold when they are opposite, and that the whole of it can be
    /// switched off from `common/defines` by a mod that wants ideology to be scenery.
    /// </summary>
    internal static string ThePoliticalWorldHangsTogether(Campaign world)
    {
        if (GovernmentCatalog.All.Count < 2) return "common/governments describes fewer than two governments";
        if (IdeologyCatalog.All.Count < 2) return "common/ideologies describes fewer than two ideologies";

        // Nobody is governed by something the catalogue has never heard of.
        foreach (var nation in world.Nations)
        {
            if (!GovernmentCatalog.Knows(nation.Government))
                return $"{nation.Tag} is a `{nation.Government}`, which no file describes";
            if (!IdeologyCatalog.Knows(nation.Ideology))
                return $"{nation.Tag} believes in `{nation.Ideology}`, which no file describes";
        }

        // And the scenario said something, rather than leaving the whole world identical.
        var governments = world.Nations.Select(nation => nation.Government).Distinct().Count();
        var ideologies = world.Nations.Select(nation => nation.Ideology).Distinct().Count();
        if (governments < 5 || ideologies < 5)
            return $"the world holds {governments} form(s) of government and {ideologies} ideolog(ies), " +
                   "which is a scenario that has not been written";

        // Two countries with the same politics think better of each other than two with
        // opposite politics. This is the whole diplomatic consequence in one line.
        var left = world.Nations.Find(nation => string.Equals(nation.Ideology, "liberalism", StringComparison.OrdinalIgnoreCase));
        var right = world.Nations.Find(nation => nation != left &&
            string.Equals(nation.Ideology, "liberalism", StringComparison.OrdinalIgnoreCase));
        var opposite = world.Nations.Find(nation =>
            string.Equals(nation.Ideology, "communism", StringComparison.OrdinalIgnoreCase));
        if (left is null || right is null || opposite is null)
            return "the scenario has no pair of like-minded countries and an opposite to compare them with";

        var alike = PoliticsCore.Affinity(world, left.Id, right.Id);
        var unalike = PoliticsCore.Affinity(world, left.Id, opposite.Id);
        if (alike <= 0f) return $"two countries of the same politics regard each other at {alike:0.0}";
        if (unalike >= 0f) return $"{left.Tag} and {opposite.Tag} are opposites and regard each other at {unalike:0.0}";
        if (alike - unalike < 10f)
            return $"politics move opinion by only {alike - unalike:0.0} between identical and opposite";

        // A mod may turn it off entirely.
        var kept = world.Defines.IdeologyOpinionWeight;
        world.Defines.IdeologyOpinionWeight = 0f;
        var silent = PoliticsCore.Affinity(world, left.Id, opposite.Id);
        world.Defines.IdeologyOpinionWeight = kept;
        if (Math.Abs(silent) > 0.0001f) return "ideology still counts after its weight was set to zero";

        // Changing what a country is costs it, and cannot be done twice in a fortnight.
        var subject = world.Nations.Find(nation =>
            nation.Population > 0 && !world.Wars.Any(war => !war.Ended && war.Involves(nation.Id)));
        if (subject is null) return "no country at peace to reorganise";
        var wasGovernment = subject.Government;
        var wasIdeology = subject.Ideology;
        var wasStability = subject.Stability;
        var wasChangedDay = subject.PoliticsChangedDay;

        var target = GovernmentCatalog.All.First(entry =>
            !string.Equals(entry.Id, subject.Government, StringComparison.OrdinalIgnoreCase)).Id;
        if (!world.ChangePolitics(subject.Id, target, "", out var refusal))
            return $"a country at peace could not change its government: {refusal}";
        if (subject.Stability >= wasStability)
            return $"{subject.Tag} rearranged itself and its stability went from {wasStability:P0} to {subject.Stability:P0}";
        if (world.ChangePolitics(subject.Id, wasGovernment, "", out _))
            return "a country changed its government twice on the same day";

        subject.Government = wasGovernment;
        subject.Ideology = wasIdeology;
        subject.Stability = wasStability;
        subject.PoliticsChangedDay = wasChangedDay;

        // And a mod may add to both catalogues.
        var governmentCount = GovernmentCatalog.All.Count;
        var ideologyCount = IdeologyCatalog.All.Count;
        var errors = new List<string>();
        GovernmentCatalog.Merge("""
        { "bench_stratocracy": { "name": "Bench stratocracy", "centralisation": 0.9,
          "effects": { "war_support": 0.0002 } } }
        """, errors);
        IdeologyCatalog.Merge("""
        { "bench_syndicalism": { "name": "Bench syndicalism", "economic": -0.8, "authority": -0.6,
          "prefers_governments": ["bench_stratocracy"] } }
        """, errors);
        if (errors.Count > 0) return $"a sound entry was rejected: {errors[0]}";
        if (!GovernmentCatalog.Knows("bench_stratocracy") || !IdeologyCatalog.Knows("bench_syndicalism"))
            return "a mod added a government and an ideology and the catalogues did not keep them";

        GovernmentCatalog.ResetCatalog();
        IdeologyCatalog.ResetCatalog();
        if (GovernmentCatalog.All.Count != governmentCount || IdeologyCatalog.All.Count != ideologyCount)
            return "resetting the catalogues left the bench's politics behind";

        Console.WriteLine($"  politics: {GovernmentCatalog.All.Count} form(s) of government and " +
                          $"{IdeologyCatalog.All.Count} ideolog(ies); the world runs {governments} and " +
                          $"{ideologies} of them. Like minds are worth {alike:0.0}, opposites {unalike:0.0}.");
        return "";
    }

    /// <summary>
    /// A border that moves leaves people behind. The piece a conqueror takes must be full
    /// of the previous owner's nationality, that must make it restive, and time must
    /// eventually settle it - otherwise conquest is either free or permanently poisoned,
    /// and neither is a game.
    /// </summary>
    internal static string ConquestLeavesMinorities(Campaign campaign)
    {
        var taken = campaign.States
            .Where(state => state.MapIndex >= StatePartition.FirstDynamicMapIndex)
            .Select(state => new
            {
                State = state,
                Owner = campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex)
            })
            .Where(entry => entry.Owner is not null)
            .Select(entry => new
            {
                entry.State,
                entry.Owner,
                Foreign = EconomySystem.ForeignShare(entry.State, entry.Owner.Tag)
            })
            .OrderByDescending(entry => entry.Foreign)
            .FirstOrDefault();

        if (taken is null) return "nothing was annexed, so there is no minority to check";
        if (taken.Foreign < 0.5f)
            return $"'{taken.State.Name}' changed hands and only {taken.Foreign:P0} of it is foreign; " +
                   "people are apparently changing nationality when a treaty is signed";

        var peoples = EconomySystem.Cultures(taken.State);
        if (peoples.Count == 0) return "the annexed region has nobody in it";

        var radicalismBefore = taken.State.Radicalism;
        var foreignBefore = taken.Foreign;
        campaign.IsPaused = false;
        for (var day = 0; day < 400; day++) campaign.TickDay();
        var foreignAfter = EconomySystem.ForeignShare(taken.State, taken.Owner.Tag);

        Console.WriteLine($"  nationality: {taken.Owner.Tag} took {taken.State.Name}, " +
                          $"{foreignBefore:P0} foreign at the treaty and {foreignAfter:P0} a year later " +
                          $"({string.Join(", ", peoples.Take(3).Select(entry => $"{DemographyCatalog.CultureName(entry.Culture)} {entry.Size:N0}"))}); " +
                          $"radicalism {radicalismBefore:P0} -> {taken.State.Radicalism:P0}.");

        if (foreignAfter >= foreignBefore)
            return $"a year of rule assimilated nobody in '{taken.State.Name}'; " +
                   "a conquest that can never settle is a conquest nobody would ever make";
        if (taken.State.Radicalism <= radicalismBefore)
            return $"'{taken.State.Name}' is full of foreign nationals and no angrier than before";
        return "";
    }

    /// <summary>
    /// The political map is data now, so two things have to be true of it: the file the
    /// game ships must describe the world the game actually starts in, and a mod that
    /// changes one line must move exactly that one province.
    ///
    /// The first matters because the file is generated from the compiled map: if the two
    /// ever drift, a modder edits one world and plays another - which is the same failure
    /// the shipped defines and the shipped skin have each already had once.
    /// </summary>
    internal static string ThePoliticalMapIsData(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        if (!ProvinceOwnership.HasData)
            return "no political map was loaded; game/history/provinces is missing or empty";

        // Every owned province is one a row names. Counted by province rather than by row: a
        // province named twice, or named and given to nobody, is not two provinces decided.
        var decided = ProvinceOwnership.Decided(topology);
        var owned = 0;
        var strays = new Dictionary<string, int>();
        var firstStray = new Dictionary<string, int>();
        var ownedWater = 0;
        var colonised = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToNation[province] == 0) continue;
            // Water is nobody's whatever the raster painted under it, and the file says
            // nothing about it (ProvinceOwnership.Apply skips a row that names water).
            if (topology.IsWater(province)) { ownedWater++; continue; }
            owned++;
            if (decided.Contains(province)) continue;
            var holder = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == topology.ProvinceToNation[province]);
            var region = campaign.States.FirstOrDefault(state => state.MapIndex == topology.ProvinceToState[province]);
            // Land nobody owned that somebody has since settled is the colonisation working,
            // not the file drifting (ColonisationCore).
            if (region is { ColonisedDay: >= 0 }) { colonised++; continue; }
            var key = $"{holder?.Tag ?? "?"} in {region?.Name ?? "?"}";
            strays[key] = strays.GetValueOrDefault(key) + 1;
            firstStray.TryAdd(key, province);
        }
        string Where(string key)
        {
            var province = firstStray[key];
            var (latitude, longitude) = topology.CenterOf(province);
            var index = topology.ProvinceToNation[province];
            var sharing = string.Join("/", campaign.Nations.Where(nation => nation.MapIndex == index).Select(nation => nation.Tag));
            var region = campaign.States.FirstOrDefault(state => state.MapIndex == topology.ProvinceToState[province]);
            return $"{key} ×{strays[key]} (province {province} at {latitude:0.0}, {longitude:0.0}, water {topology.IsWater(province)}, " +
                   $"map index {index} held by {sharing}, region {region?.Id} owned by index {region?.NationMapIndex} unclaimed {region?.Unclaimed})";
        }
        if (strays.Count > 0)
            return $"{strays.Values.Sum():N0} of {owned:N0} owned provinces are named by no row of the political map, " +
                   "so the file and the map have drifted apart: " +
                   string.Join(", ", strays.OrderByDescending(pair => pair.Value).Take(8).Select(pair => Where(pair.Key)));

        // A mod's line has to win, and only over the province it names.
        var subject = 1;
        while (subject <= topology.ProvinceCount && topology.ProvinceToNation[subject] == 0) subject++;
        if (subject > topology.ProvinceCount) return "no owned province to test an override against";

        // What the shipped file alone makes of the map. The comparison has to be against
        // this rather than against the live map: by the time the checks run, the AI has
        // spent two years annexing provinces, and applying a day-zero file over that
        // undoes every one of them. That is the file working exactly as designed, but it
        // was being counted as ninety provinces of collateral from a one-line override.
        var baseline = ProvinceTopologyClone(topology);
        ProvinceOwnership.Apply(campaign, baseline, (ushort[])campaign.ProvinceController.Clone());

        var before = baseline.ProvinceToNation[subject];
        var taker = campaign.Nations.FirstOrDefault(nation =>
            nation.MapIndex != before && nation.Population > 0);
        if (taker is null) return "no nation to hand the province to";

        var errors = new List<string>();
        ProvinceOwnership.Merge($"{subject};{taker.Tag}", "bench", errors);
        if (errors.Count > 0) return $"a one-line political override was rejected: {errors[0]}";

        var scratch = ProvinceTopologyClone(topology);
        ProvinceOwnership.Apply(campaign, scratch, (ushort[])campaign.ProvinceController.Clone());

        if (scratch.ProvinceToNation[subject] != (ushort)taker.MapIndex)
            return $"the override did not move province {subject} to {taker.Tag}";
        var collateral = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (province != subject && scratch.ProvinceToNation[province] != baseline.ProvinceToNation[province])
                collateral++;
        if (collateral > 0)
            return $"a one-province override also moved {collateral:N0} other province(s)";

        // Put it back, so nothing downstream inherits a border the bench drew.
        ProvinceOwnership.Merge($"{subject};{campaign.Nations.First(nation => nation.MapIndex == before).Tag}",
            "bench", null);

        Console.WriteLine($"  political map: {ProvinceOwnership.Count:N0} provinces are decided by " +
                          $"game/history/provinces, and a one-line mod override moved exactly one of them" +
                          (ownedWater > 0 ? $"; {ownedWater:N0} water province(s) still carry an owner from the raster" : "") +
                          (colonised > 0 ? $"; {colonised:N0} province(s) were settled since." : "."));
        return "";
    }

    /// <summary>A throwaway copy, so the override test never touches the live map.</summary>
    internal static ProvinceTopology ProvinceTopologyClone(ProvinceTopology source) => source.CloneForOverrides();

    /// <summary>
    /// Every authored region correction has to have produced a region with ground under it
    /// and people on it. A rule whose bounding box missed, or whose donor was wrong, leaves
    /// a region that exists in the list and nowhere on the map - which is exactly the state
    /// Nakhchivan was in before it had a rule at all, only harder to notice.
    /// </summary>
    internal static string AuthoredRegionsExist(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        var counts = new Dictionary<int, int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            counts[topology.ProvinceToState[province]] = counts.GetValueOrDefault(topology.ProvinceToState[province]) + 1;

        var authored = campaign.States
            .Where(state => state.MapIndex >= 1711 && state.MapIndex < StatePartition.FirstDynamicMapIndex)
            .Where(state => !LakeRegions.IsWaterState(state))
            .OrderBy(state => state.MapIndex)
            .ToList();
        if (authored.Count == 0) return "no authored region corrections were applied at all";

        var broken = new List<string>();
        foreach (var state in authored)
        {
            var provinces = counts.GetValueOrDefault(state.MapIndex);
            if (provinces == 0) broken.Add($"{state.Name} holds no province");
            // Land that belongs to nobody - Antarctica's lands - has nobody on it by design.
            else if (state.Population <= 0 && !ColonisationCore.IsUnclaimed(state)) broken.Add($"{state.Name} has no people");
        }
        if (broken.Count > 0) return string.Join("; ", broken);

        Console.WriteLine("  authored regions: " + string.Join(", ", authored.Select(state =>
            $"{state.Name} {counts.GetValueOrDefault(state.MapIndex)}p/{Fmt.People(state.Population)}")));
        return "";
    }

    /// <summary>
    /// The compiled map still matches the region layer this build draws.
    ///
    /// Provinces are cut inside regions, so the raster on disk is an output of the code that
    /// draws them. Change one without rebuilding the other and the game loses its whole map
    /// at boot - which is what it looks like from the outside, rather than what it is, and it
    /// cost an afternoon once already. Cheaper to be told here.
    /// </summary>
    internal static string TheCompiledMapMatchesTheCodeThatDrewIt()
    {
        if (string.IsNullOrWhiteSpace(MapRasterCatalog.StampPath))
            return "game/map_data/compiled.json is missing - recompile with Tools/MapDataCompiler";
        var fingerprint = MapRasterCatalog.RegionFingerprint(DealOrForce.Simulation.WorldMapSource.States);
        var stamped = System.Text.Json.JsonDocument.Parse(File.ReadAllText(MapRasterCatalog.StampPath))
            .RootElement.GetProperty("regions").GetUInt64();
        if (stamped != fingerprint)
            return $"provinces.png was cut from region layer {stamped}, this build draws {fingerprint}";
        Console.WriteLine($"  map stamp: provinces.png and this build agree on region layer {fingerprint}.");
        return "";
    }

    /// <summary>
    /// Every region knows who lives in it, and the minorities are where they actually live.
    ///
    /// Three things have to hold at once and only the first is obvious. The head-count has
    /// to add up, or the population screen and the economy disagree about the same region.
    /// Every people, faith and trade has to be one the catalogues define, or the screen
    /// renders an id. And the peoples have to be in the right provinces: a census that is
    /// right nationally and smeared evenly over the country puts Tibetans in Shanghai and
    /// Chechens in Kaliningrad, which is worse than no census at all, and it is the failure
    /// that no total will ever catch.
    /// </summary>
    /// <summary>
    /// The bookmark's regions are written down, each in one place. A region with people in it
    /// and no record is one the game deals out from its country, which is how Magadan came to
    /// hold as many people as Moscow; a region written in two base files is one a modder edits
    /// in the first and sees the second overrule. Pieces cut off a region when the map is
    /// attached - occupied Donetsk, northern Cyprus - are the map's doing and need no record.
    /// </summary>
    internal static string EveryRegionIsWrittenDown(Campaign campaign)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var twice = new List<string>();
        var badNames = new List<string>();
        var resourceOverlays = new List<string>();
        foreach (var file in GameDataLoader.DataFiles("history", "states", "2024"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            using var document = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (name.StartsWith("zz_", StringComparison.OrdinalIgnoreCase))
            {
                // Additive resource-only patches are not duplicate base records. They
                // must reference a base region and cannot replace its census/identity.
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object ||
                        entry.Value.EnumerateObject().Count() != 1 ||
                        !entry.Value.TryGetProperty("resources", out var resources) ||
                        resources.ValueKind != JsonValueKind.Object)
                        return $"invalid resource overlay {name}: {entry.Name}";
                    foreach (var resource in resources.EnumerateObject())
                        if (resource.Value.ValueKind != JsonValueKind.Number ||
                            !resource.Value.TryGetDouble(out var amount) || !double.IsFinite(amount) || amount < 0)
                            return $"invalid resource amount {name}: {entry.Name}/{resource.Name}";
                    resourceOverlays.Add(entry.Name);
                }
                continue;
            }
            // A titles overlay (TerritorialClaims): homeland cores and claims laid over regions
            // their own files describe. It writes no region, so it cannot write one twice.
            if (document.RootElement.EnumerateObject().All(entry => entry.Value.ValueKind == JsonValueKind.Object &&
                    entry.Value.EnumerateObject().All(field => field.Name is "cores" or "claims")))
                continue;
            if (!string.Equals(name, "unclaimed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "lakes", StringComparison.OrdinalIgnoreCase) && campaign.NationByTag(name) is null)
                badNames.Add(name);
            foreach (var entry in document.RootElement.EnumerateObject())
                if (!seen.TryAdd(entry.Name, name)) twice.Add($"{entry.Name} in {seen[entry.Name]} and {name}");
        }
        if (badNames.Count > 0) return "file(s) named for no country: " + string.Join(", ", badNames.Take(5));
        if (twice.Count > 0) return $"{twice.Count} region(s) written twice, starting with " + string.Join("; ", twice.Take(3));
        var unknownOverlays = resourceOverlays.Where(id => !seen.ContainsKey(id)).Distinct().ToArray();
        if (unknownOverlays.Length > 0) return "resource overlays name no base region: " + string.Join(", ", unknownOverlays.Take(5));

        var stated = HistoryDataLoader.StatedRegions(campaign);
        var missing = new List<string>();
        var partial = new List<string>();
        foreach (var state in campaign.States)
        {
            if (state.MapIndex >= StatePartition.FirstDynamicMapIndex) continue;
            if (!stated.TryGetValue(state.MapIndex, out var figures))
            {
                missing.Add(state.Id);
                continue;
            }
            if (state.Population <= 0 || LakeRegions.IsWaterState(state)) continue;
            var lacks = new List<string>();
            if (figures.Population is null) lacks.Add("population");
            if (figures.Urbanisation is null) lacks.Add("urbanization");
            if (figures.Cultures is not { Count: > 0 }) lacks.Add("cultures");
            if (figures.Buildings is null) lacks.Add("buildings");
            if (figures.Resources is null) lacks.Add("resources");
            if (lacks.Count > 0) partial.Add($"{state.Id} ({string.Join(", ", lacks)})");
        }
        if (missing.Count > 0) return $"{missing.Count} region(s) have no record, starting with " + string.Join(", ", missing.Take(5));
        if (partial.Count > 0) return $"{partial.Count} inhabited region(s) leave out a field, starting with " + string.Join("; ", partial.Take(4));
        Console.WriteLine($"  regions: {seen.Count:N0} written in {GameDataLoader.DataFiles("history", "states", "2024").Count} file(s), " +
                          "every inhabited one in full.");
        return "";
    }

    internal static string EveryRegionKnowsWhoLivesInIt(Campaign campaign)
    {
        if (DemographyCatalog.Cultures.Count == 0) return "no peoples are defined at all";
        if (DemographyCatalog.Professions.Count == 0) return "no trades are defined at all";

        var faults = new List<string>();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var empty = 0;
        long counted = 0;
        long stated = 0;
        foreach (var state in campaign.States)
        {
            if (LakeRegions.IsWaterState(state)) continue;
            var total = state.PopulationGroups.Sum(group => group.Size);
            stated += state.Population;
            counted += total;
            if (state.Population > 0 && total == 0) empty++;
            else if (state.Population > 0 && Math.Abs(total - state.Population) > Math.Max(4, state.Population / 1000))
                faults.Add($"{state.Name} holds {total:N0} people and says it holds {state.Population:N0}");
            foreach (var group in state.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                if (DemographyCatalog.Culture(group.Culture) is null) unknown.Add($"culture {group.Culture}");
                if (DemographyCatalog.Religion(group.Religion) is null) unknown.Add($"faith {group.Religion}");
                if (DemographyCatalog.Profession(group.Profession) is null) unknown.Add($"trade {group.Profession}");
            }
            if (faults.Count > 4) break;
        }
        if (empty > 0) faults.Add($"{empty:N0} inhabited region(s) have nobody in them");
        if (unknown.Count > 0) faults.Add("undefined: " + string.Join(", ", unknown.Take(4)));
        if (faults.Count > 0) return string.Join("; ", faults.Take(5));

        // The peoples who named the regions they live in are actually in them. Each of these
        // is a place where a national average is a lie, and the whole point of the "where"
        // line is that the map shows it.
        var expected = new (string Region, string Culture, float AtLeast)[]
        {
            ("Tatarstan", "tatar", 0.40f),
            ("Chechen", "chechen", 0.70f),
            ("Xinjiang", "uyghur", 0.30f),
            ("Tibet", "tibetan", 0.60f),
            ("Quebec", "quebecois", 0.55f),
            ("Kurdistan", "kurdish", 0.60f)
        };
        var reported = new List<string>();
        foreach (var (name, culture, atLeast) in expected)
        {
            var state = campaign.States.FirstOrDefault(candidate =>
                candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (state is null) continue;
            var here = EconomySystem.Cultures(state);
            var share = here.Where(entry => string.Equals(entry.Culture, culture, StringComparison.OrdinalIgnoreCase))
                .Sum(entry => entry.Size) / (double)Math.Max(1, state.Population);
            if (share < atLeast)
                faults.Add($"{state.Name} is {share:P0} {culture} and should be at least {atLeast:P0}");
            reported.Add($"{state.Name} {share:P0} {culture}");
        }
        if (faults.Count > 0) return string.Join("; ", faults.Take(4));

        var sample = campaign.States.FirstOrDefault(state => state.Name.Contains("Bavaria", StringComparison.OrdinalIgnoreCase))
                     ?? campaign.States.OrderByDescending(state => state.Population).First();
        var trades = EconomySystem.Professions(sample).Take(3)
            .Select(entry => $"{DemographyCatalog.ProfessionName(entry.Profession)} {entry.Size / (double)Math.Max(1, sample.Population):P0}");
        var written = HistoryDataLoader.StatedRegions(campaign).Count(pair => pair.Value.Cultures is { Count: > 0 });
        Console.WriteLine($"  people: {DemographyCatalog.Cultures.Count:N0} peoples, {DemographyCatalog.Religions.Count} faiths, " +
                          $"{DemographyCatalog.Professions.Count} trades; {written:N0} region(s) write who lives in them; " +
                          $"{counted:N0} of {stated:N0} placed.");
        Console.WriteLine($"    minorities where they live: {string.Join(", ", reported)}.");
        Console.WriteLine($"    {sample.Name} works as: {string.Join(", ", trades)}.");
        return "";
    }

    /// <summary>
    /// A country that states a figure about itself ends up with that figure.
    ///
    /// The population screen is only worth reading if its numbers are the real ones. Two
    /// of them are looked up rather than guessed - how urban a country is and how much of
    /// it works the land - and both are consumed a long way from where they are written:
    /// they set the weights of ten trades, in every region separately, and the regions are
    /// then normalised. There are several honest-looking ways for a figure to be quietly
    /// halved on that journey, and none of them would look like anything on screen.
    ///
    /// Regions vary around the national average on purpose, so this checks the country as a
    /// whole rather than any one region.
    /// </summary>
    /// <summary>
    /// The population screen lists a region by trade, then by class, then by sector, and
    /// tells the player the last two are the first added up. This is that promise, checked.
    /// </summary>
    /// <summary>
    /// A dependency pays, turns up, drifts, and can be let go.
    ///
    /// Four claims, each checked against a number rather than asserted. The last one is the
    /// one worth having: a subject's wish to leave has to answer to circumstance, so the
    /// same country held by a steady overlord and by a collapsing one must not want the
    /// same thing. If it does, the whole model is a timer with extra steps.
    /// </summary>
    /// <summary>
    /// A war can end with the loser still on the map and no longer deciding.
    ///
    /// Four things, because a demand nobody can price, nobody can refuse and no AI will
    /// write is not a demand. The last of them is the one that decides whether the mechanic
    /// is in the game: a dependency the player has to type into a console is a feature of
    /// the console.
    /// </summary>
    internal static string AWarCanEndInObedienceRatherThanAnnexation()
    {
        if (SubjectCatalog.All.Count == 0) return "common/subjects declared no kinds at all";

        var campaign = Campaign.CreateBootstrap(20240501);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var winner = campaign.NationByTag("FRA");
        var loser = campaign.NationByTag("LUX");
        var large = campaign.NationByTag("DEU");
        if (winner is null || loser is null || large is null) return "";

        // Priced, and priced by who is being asked. Obedience is not a commodity: the same
        // arrangement over a neighbour your own size is a different demand from the one
        // over a country the size of a city.
        var small = PeaceConference.SubjugationCost(campaign, winner, loser, "puppet");
        var big = PeaceConference.SubjugationCost(campaign, winner, large, "puppet");
        if (small <= 0f) return "a puppet costs nothing to impose";
        if (big <= small)
            return $"a puppet costs {small:0.0} over LUX and {big:0.0} over DEU - size is not counted";

        Estrange(campaign, winner, loser);
        var war = CampaignDiplomacy.DeclareWar(campaign, winner.Id, loser.Id);
        if (war is null) return "FRA could not declare on LUX";
        war.WarScore = 100f;
        war.DefenderWarSupport = 0.1f;

        // Refused for the reasons that matter, and refused with the reason said out loud.
        var whole = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id, Subjugate = "puppet" };
        foreach (var province in PeaceConference.Claimable(campaign, war, winner.Id))
            whole.Provinces.Add(province);
        var wholeBlock = PeaceConference.BlockReason(campaign, war, whole);
        if (!wholeBlock.Contains("cannot also answer", StringComparison.OrdinalIgnoreCase))
            return $"annexing LUX whole AND making it answer was refused as '{wholeBlock}'";

        var nonsense = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id, Subjugate = "not_a_kind" };
        if (!PeaceConference.BlockReason(campaign, war, nonsense).Contains("no such arrangement",
                StringComparison.OrdinalIgnoreCase))
            return "a kind that is not in common/subjects was accepted at the table";

        // What the AI would write, given room for it and given none.
        var ledger = new PeaceLedger(campaign, war, winner.Id);
        var rich = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.ConsiderSubjugation(campaign, winner, war, ledger, rich, 400f);
        if (rich.Subjugate.Length == 0)
            return "the AI had four hundred points of war score and asked for nothing";
        var poor = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.ConsiderSubjugation(campaign, winner, war, ledger, poor, 1f);
        if (poor.Subjugate.Length != 0)
            return $"the AI demanded a {poor.Subjugate} with one point of war score";

        // And the settlement itself.
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id, Subjugate = "puppet" };
        var block = PeaceConference.BlockReason(campaign, war, demand);
        if (block.Length > 0) return $"a puppet at a hundred war score was refused: {block}";
        if (!PeaceConference.Resolve(campaign, war, demand)) return "the settlement would not resolve";
        if (!loser.AnswersTo(winner.Id)) return "LUX signed and does not answer to FRA";
        if (!war.Ended) return "the war went on after the settlement that ended it";
        if (FlagCatalog.ValueOf("subject", campaign, loser) != "puppet")
            return "the flag search cannot see that LUX is a puppet";

        Console.WriteLine($"  a peace that takes no ground: LUX is a {SubjectCatalog.Of(loser.SubjectType)?.Name} " +
                          $"of FRA for {small:0.0} war score ({big:0.0} had it been DEU), " +
                          $"and the AI with room for one asked for a {rich.Subjugate}.");
        return "";
    }

    /// <summary>
    /// The obligations, both ways, and the end of the arrangement.
    ///
    /// A dependency that only takes is a tax with a flag on it. What makes it a bargain is
    /// the half running the other way - the master answering for what it holds - and what
    /// makes it a story is that it can end with the subject inside the overlord rather than
    /// only by slipping out of its hands.
    /// </summary>
    internal static string AnsweringToSomebodyMeansSomething()
    {
        var campaign = Campaign.CreateBootstrap(20240501);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var overlord = campaign.NationByTag("FRA");
        var subject = campaign.NationByTag("LUX");
        var stranger = campaign.NationByTag("DEU");
        if (overlord is null || subject is null || stranger is null) return "";
        Estrange(campaign, overlord, subject);
        Estrange(campaign, overlord, stranger);
        Estrange(campaign, subject, stranger);
        if (!SubjectSystem.Subjugate(campaign, overlord, subject, "puppet", out var refusal))
            return $"could not make LUX a puppet of FRA: {refusal}";

        // No foreign policy of its own - and no rebellion by declaration either.
        if (CampaignDiplomacy.CanDeclareWar(campaign, subject.Id, stranger.Id, out _))
            return "a puppet declared a war of its own";
        if (CampaignDiplomacy.CanDeclareWar(campaign, subject.Id, overlord.Id, out _, onOwnInitiative: false))
            return "a puppet was brought into a war against its own overlord";
        if (!CampaignDiplomacy.CanDeclareWar(campaign, subject.Id, stranger.Id, out var why,
                onOwnInitiative: false))
            return $"a puppet could not be brought into somebody else's war: {why}";

        // The half running the other way. DEU attacks the puppet; FRA is in the war.
        CampaignDiplomacy.Get(campaign, stranger.Id, subject.Id).TruceUntil = 0;
        if (CampaignDiplomacy.DeclareWar(campaign, stranger.Id, subject.Id) is null)
            return "DEU could not attack LUX at all";
        if (!CampaignDiplomacy.AtWar(campaign, overlord.Id, stranger.Id))
            return "DEU attacked FRA's puppet and FRA was not in the war";

        // And the end of it. Too soon, and then not too soon.
        if (SubjectSystem.MayAnnex(campaign, overlord, subject, out var tooSoon))
            return "a puppet made this week was absorbed this week";
        if (!tooSoon.Contains("year", StringComparison.OrdinalIgnoreCase))
            return $"the refusal does not say what is missing: {tooSoon}";

        var kind = SubjectCatalog.Of("puppet");
        subject.SubjectSinceDay = campaign.ElapsedDays - kind.AnnexAfterDays - 1;
        subject.LibertyDesire = kind.AnnexBelowLiberty + 0.1f;
        if (SubjectSystem.MayAnnex(campaign, overlord, subject, out var wantsOut))
            return "a puppet that wants out was absorbed anyway";
        if (!wantsOut.Contains("wants out", StringComparison.OrdinalIgnoreCase))
            return $"the refusal does not say what is wrong: {wantsOut}";

        subject.LibertyDesire = 0f;
        var wasFrance = campaign.States.Count(state => state.NationMapIndex == overlord.MapIndex);
        var wasLuxembourg = campaign.States.Count(state => state.NationMapIndex == subject.MapIndex);
        if (wasLuxembourg == 0) return "LUX held no ground to be absorbed";
        if (!SubjectSystem.Annex(campaign, overlord, subject, out var blocked))
            return $"a puppet held long enough and content could not be absorbed: {blocked}";
        if (subject.OverlordId != 0 || subject.SubjectType.Length != 0)
            return "LUX was absorbed and still thinks it answers to somebody";
        var nowFrance = campaign.States.Count(state => state.NationMapIndex == overlord.MapIndex);
        if (nowFrance <= wasFrance)
            return $"LUX was absorbed and FRA is no larger ({wasFrance} region(s) before and after)";

        // A dominion is the kind that never ends this way, and that is data rather than code.
        var dominion = SubjectCatalog.Of("dominion");
        if (dominion is null || dominion.AnnexAfterDays >= 0)
            return "common/subjects no longer has a kind that cannot be absorbed at all";

        Console.WriteLine($"  obligations: a puppet declares no wars of its own, its overlord " +
                          $"answered for it, and after {kind.AnnexAfterDays / 365.0:0.0} content year(s) " +
                          $"it became {nowFrance - wasFrance} more region(s) of France.");
        return "";
    }

    /// <summary>
    /// Every dependency in a world nobody steered, and whether any of them is nonsense.
    ///
    /// Run against the campaign the bench has just played rather than a staged one, because
    /// the failure this is looking for only happens in a world with a history: a country
    /// annexed while somebody answered to it, a subject that outlived its overlord, a chain
    /// that became a circle. The report is the other half - a mechanic that never fires in
    /// an unsteered world is one the player will never meet either.
    /// </summary>
    internal static string DependenciesInAWorldNobodySteered(Campaign campaign)
    {
        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var subject in campaign.Nations)
        {
            if (!subject.IsSubject) continue;
            var overlord = campaign.NationById(subject.OverlordId);
            if (overlord is null)
                return $"{subject.Tag} answers to country {subject.OverlordId}, which is not on the list";
            if (overlord.Id == subject.Id) return $"{subject.Tag} answers to itself";
            if (!SubjectCatalog.Knows(subject.SubjectType))
                return $"{subject.Tag} answers as a '{subject.SubjectType}', which is not a kind";
            if (overlord.AnswersTo(subject.Id))
                return $"{subject.Tag} and {overlord.Tag} answer to each other";
            if (subject.Population <= 0)
                return $"{subject.Tag} answers to {overlord.Tag} and has nobody living in it";
            counted[subject.SubjectType] = counted.GetValueOrDefault(subject.SubjectType) + 1;
        }

        var total = counted.Values.Sum();
        Console.WriteLine(total == 0
            ? "  dependencies: none were imposed in this run - every war that was won was won for ground."
            : $"  dependencies: {total} country(ies) answer to another, as " +
              string.Join(", ", counted.OrderByDescending(pair => pair.Value)
                  .Select(pair => $"{pair.Value} {pair.Key}")) + ".");

        // And the other thing two countries can sign. Reported rather than required: a world
        // with no alliances in it is a legitimate world, and a world where nobody could ever
        // sign one is a bug this line would show as a nought.
        var allied = 0;
        var guaranteed = 0;
        var pacts = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.Stance == DiplomaticStance.Allied) allied++;
            if (relation.GuaranteeByA) guaranteed++;
            if (relation.GuaranteeByB) guaranteed++;
            if (relation.NonAggressionPact) pacts++;
        }
        Console.WriteLine($"  alliances: {allied:N0} alliance(s), {guaranteed:N0} guarantee(s) and " +
                          $"{pacts:N0} non-aggression pact(s) stand in this world.");

        // Who armed and who went home, which is the thing the establishment was added for.
        var armed = 0;
        var shrank = 0;
        var biggest = 0f;
        Nation armer = null;
        foreach (var nation in campaign.Nations)
        {
            if (nation.FoundingDivisions <= 0f) continue;
            var ratio = nation.EstablishedDivisions / nation.FoundingDivisions;
            if (ratio > 1.05f) armed++;
            if (ratio < 0.95f) shrank++;
            if (ratio > biggest) { biggest = ratio; armer = nation; }
        }
        Console.WriteLine(armed + shrank == 0
            ? "  establishments: nobody's army moved from what they started with."
            : $"  establishments: {armed:N0} country(ies) armed and {shrank:N0} stood down; " +
              $"the furthest is {armer?.Tag} at {biggest:P0} of what it began with.");
        return "";
    }

    /// <summary>
    /// Being afraid is not a foreign policy on its own.
    ///
    /// Widening the AI's circle gave every small country an opinion about the great powers,
    /// and the first measurement said what that was worth by itself: rivalries went from 41
    /// to 70 in two years and alliances did not move. A country that has learned to be
    /// frightened and has not learned to do anything about it is a worse model than one that
    /// never looked past its own border.
    ///
    /// What it does about it is ask somebody larger to stand behind it - and the country
    /// asked decides, on what it thinks of the applicant, whether it is already fighting
    /// somebody, and whether it is wary of the same neighbour. Checked here as a
    /// before-and-after on the same world rather than as a number out of a campaign, because
    /// the question is whether the ASKING happens at all.
    /// </summary>
    internal static string BeingAfraidIsNotAPolicy()
    {
        var campaign = Campaign.CreateBootstrap(20240501);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        // Non-aligned, and afraid of somebody who is NOT the strongest country in the
        // world - two earlier versions of this check failed on their own staging rather
        // than on the mechanism. Lithuania is in NATO, so it always had a warmer
        // relationship to work on than its fear; and nobody can shelter a country from
        // China, because there is nobody heavier to ask.
        var small = campaign.NationByTag("NPL");
        var bully = campaign.NationByTag("IND");
        var power = campaign.NationByTag("CHN");
        if (small is null || bully is null || power is null) return "";
        Estrange(campaign, small, bully);
        Estrange(campaign, small, power);

        var fear = CampaignDiplomacy.Get(campaign, small.Id, bully.Id);
        fear.Stance = DiplomaticStance.Rival;
        // Several different grievances rather than one repeated: every opinion modifier has
        // a stack limit, so eight denunciations are worth two.
        foreach (var grievance in new[]
                 {
                     "denounced", "denounced", "took_our_land", "took_our_land",
                     "fought_a_war", "fought_a_war", "embargoed_us", "declared_war_on_us"
                 })
            OpinionSystem.Remember(campaign, fear, grievance);
        OpinionSystem.Settle(campaign, fear);
        if (fear.Opinion > campaign.Defines.AiRivalryThreshold)
            return $"the check could not make NPL afraid of IND: opinion {fear.Opinion:0} against " +
                   $"a rivalry threshold of {campaign.Defines.AiRivalryThreshold:0}";

        var friend = CampaignDiplomacy.Get(campaign, small.Id, power.Id);
        for (var repeat = 0; repeat < 4; repeat++)
            OpinionSystem.Remember(campaign, friend, "improved_relations");
        OpinionSystem.Settle(campaign, friend);
        if (friend.Opinion <= 0f)
            return $"the check could not put NPL and CHN on speaking terms: opinion {friend.Opinion:0}";

        // And a protector with a reason. A great power does not shelter a country from a
        // country it gets on with - measured: China refused Nepal because China's view of
        // India was +33, which is the right answer and the thing this staging was missing.
        var theirQuarrel = CampaignDiplomacy.Get(campaign, power.Id, bully.Id);
        foreach (var grievance in new[]
                 {
                     "denounced", "denounced", "took_our_land", "took_our_land",
                     "fought_a_war", "fought_a_war", "embargoed_us"
                 })
            OpinionSystem.Remember(campaign, theirQuarrel, grievance);
        OpinionSystem.Settle(campaign, theirQuarrel);
        if (theirQuarrel.Opinion > campaign.Defines.AiRivalryThreshold)
            return $"the check could not give CHN a reason to mind IND: opinion {theirQuarrel.Opinion:0}";

        // A bully worth being afraid of, and a protector heavier than the bully. Both are
        // staged: the estimate is the real order of battle now (CampaignIntel), so an India
        // with three times its army is heavier than China unless China grows with it.
        foreach (var army in campaign.Armies)
            if (army.NationId == bully.Id) army.Divisions *= 3f;
            else if (army.NationId == power.Id) army.Divisions *= 4f;
        var bullyStrength = CampaignIntel.EstimatedStrength(campaign, small.Id, bully);
        var powerStrength = CampaignIntel.EstimatedStrength(campaign, small.Id, power);
        if (bullyStrength <= AiStatecraft.StrengthOf(campaign, small) * campaign.Defines.AiCounterweightFear)
            return $"IND is not frightening enough to ask about: {bullyStrength:0} against NPL's " +
                   $"{AiStatecraft.StrengthOf(campaign, small):0}";
        if (powerStrength <= bullyStrength)
            return $"CHN is no heavier than IND ({powerStrength:0} against {bullyStrength:0}), " +
                   "so there is nobody worth asking";

        // The decision itself, rather than four months of a world in which a hundred other
        // things also happen. Three earlier versions of this check ran the campaign and
        // failed on their own staging every time; what is being checked is whether a
        // frightened country asks and whether a power with a reason says yes.
        if (friend.GuaranteedBy(power.Id)) return "CHN already guaranteed NPL before the check began";
        campaign.TickDay();
        if (!AiStatecraft.AskForShelter(campaign, small, bully))
            return $"NPL, worth {AiStatecraft.StrengthOf(campaign, small):0} beside a neighbour worth " +
                   $"{bullyStrength:0}, did not ask a country worth {powerStrength:0} that has its own " +
                   "quarrel with that neighbour";
        if (!friend.GuaranteedBy(power.Id))
            return "NPL asked, somebody said yes, and no guarantee came of it";

        // And the point of it: a guarantor counts when somebody weighs up attacking.
        var friends = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != small.Id && relation.B != small.Id) continue;
            var otherId = relation.Other(small.Id);
            if (relation.Stance == DiplomaticStance.Allied || relation.GuaranteedBy(otherId)) friends++;
        }
        if (friends == 0) return "NPL is guaranteed and counts nobody on its side";

        Console.WriteLine($"  shelter: NPL, afraid of a neighbour three times its weight, was " +
                          $"guaranteed by CHN within {campaign.ElapsedDays:N0} day(s), and now counts " +
                          $"{friends:N0} country(ies) standing with it.");
        return "";
    }

    internal static string AnAllianceIsAskedForAndAnswered()
    {
        var campaign = Campaign.CreateBootstrap(20240501);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var small = campaign.NationByTag("LUX");
        var large = campaign.NationByTag("FRA");
        var third = campaign.NationByTag("DEU");
        if (small is null || large is null || third is null) return "";

        Estrange(campaign, small, large);
        Estrange(campaign, small, third);
        Estrange(campaign, large, third);

        // Cold, they have no reason to sign anything.
        var cold = AllianceSystem.Willingness(campaign, large, small);
        if (AllianceSystem.Propose(campaign, small, large, out var refusedAnswer))
            return $"FRA allied with LUX out of nowhere ({refusedAnswer})";

        // Warm, the small country asking the large one is the commonest alliance there is.
        var bond = CampaignDiplomacy.Get(campaign, small.Id, large.Id);
        bond.AllianceRefusedDay = -9999;
        for (var repeat = 0; repeat < 6; repeat++)
            OpinionSystem.Remember(campaign, bond, "improved_relations");
        OpinionSystem.Remember(campaign, bond, "trade_agreement");
        OpinionSystem.Remember(campaign, bond, "non_aggression_pact");
        OpinionSystem.Settle(campaign, bond);

        var why = new Breakdown();
        var warm = AllianceSystem.Willingness(campaign, large, small, why);
        if (warm <= cold)
            return $"courting LUX changed nothing: {cold:0.00} before and {warm:0.00} after";
        if (!why.HasEntries) return "the answer was worked out from nothing the screen could show";
        if (!AllianceSystem.Propose(campaign, small, large, out var answer))
            return $"FRA would not sign at willingness {warm:0.00}: {answer}";
        if (CampaignDiplomacy.Get(campaign, small.Id, large.Id).Stance != DiplomaticStance.Allied)
            return "FRA accepted and the two are not allied";

        // The refusals that are facts rather than failures.
        if (AllianceSystem.CanPropose(campaign, small, large, out var already) || !already.Contains("already"))
            return $"asking an ally to ally said '{already}'";
        var puppet = campaign.NationByTag("BEL");
        Estrange(campaign, small, puppet);
        Estrange(campaign, third, puppet);
        if (!SubjectSystem.Subjugate(campaign, third, puppet, "puppet", out _))
            return "could not stage a dependency for the alliance check";
        if (AllianceSystem.CanPropose(campaign, small, puppet, out var answersTo) ||
            !answersTo.Contains("answers to"))
            return $"a puppet was free to sign its own alliance: '{answersTo}'";

        // A country somebody is playing is offered, never signed for.
        var human = campaign.NationByTag("ITA");
        Estrange(campaign, human, third);
        var humanBond = CampaignDiplomacy.Get(campaign, human.Id, third.Id);
        for (var repeat = 0; repeat < 8; repeat++)
            OpinionSystem.Remember(campaign, humanBond, "improved_relations");
        OpinionSystem.Settle(campaign, humanBond);
        human.IsHuman = true;
        if (AllianceSystem.Propose(campaign, third, human, out var waiting))
            return "an alliance was signed on a player's behalf";
        if (AllianceSystem.OfferedTo(campaign, human)?.Id != third.Id)
            return $"DEU asked ITA and nothing reached ITA's table ({waiting})";
        if (!AllianceSystem.Answer(campaign, human, true, out _))
            return "ITA accepted an offer and no alliance came of it";
        if (humanBond.Stance != DiplomaticStance.Allied) return "ITA accepted and is not allied";
        human.IsHuman = false;

        // And leaving, which is remembered.
        var before = bond.Opinion;
        var reasonsBefore = bond.Reasons.Sum(reason => OpinionSystem.Weight(campaign, reason));
        if (!AllianceSystem.Break(campaign, small, large, out var blocked))
            return $"LUX could not leave its own alliance: {blocked}";
        if (bond.Stance == DiplomaticStance.Allied) return "LUX left and is still allied";
        OpinionSystem.Settle(campaign, bond);
        // Opinion is clamped at 100. Strongly friendly countries can lose the alliance
        // bonus AND acquire the betrayal penalty while still displaying 100. Check the
        // actual memory and its weight, not an assumed movement of the saturated gauge.
        var broken = bond.Reasons.Find(reason => reason.Id == "broke_our_alliance");
        var reasonsAfter = bond.Reasons.Sum(reason => OpinionSystem.Weight(campaign, reason));
        if (OpinionSystem.Has(bond, "alliance") || broken is null ||
            broken.GrantedDay != campaign.ElapsedDays || broken.ExpiresDay <= campaign.ElapsedDays ||
            OpinionSystem.Weight(campaign, broken) >= 0 || reasonsAfter >= reasonsBefore || bond.Opinion > before)
            return $"leaving did not remove the alliance bonus and retain a negative memory: " +
                   $"opinion {before:0}->{bond.Opinion:0}, reasons {reasonsBefore:0}->{reasonsAfter:0}";

        Console.WriteLine($"  alliances: FRA would not sign with LUX at {cold:0.00} and did at {warm:0.00}; " +
                          $"a puppet may not sign at all; a player is asked rather than signed for; " +
                          $"leaving removed {reasonsBefore - reasonsAfter:0} points of remembered reasons " +
                          $"(displayed opinion {before:0}->{bond.Opinion:0}).");
        return "";
    }

    /// <summary>
    /// Two countries with nothing between them, whatever the scenario says they have.
    ///
    /// The 2024 world opens with NATO, the CSTO and a dozen guarantees in it, which is right
    /// and which quietly broke three checks that had picked France and Luxembourg as two
    /// countries that could fight. A check about tribute is not a check about whether its
    /// two examples are in the same alliance, so it says which relationship it needs.
    /// </summary>
    internal static void Estrange(Campaign campaign, Nation one, Nation two)
    {
        if (campaign is null || one is null || two is null) return;
        var relation = CampaignDiplomacy.Get(campaign, one.Id, two.Id);
        relation.Stance = DiplomaticStance.Neutral;
        relation.AllianceSinceDay = -9999;
        relation.AllianceOfferedBy = 0;
        relation.AllianceRefusedDay = -9999;
        relation.TruceUntil = 0;
        relation.NonAggressionPact = false;
        relation.GuaranteeByA = false;
        relation.GuaranteeByB = false;
        OpinionSystem.Forget(campaign, relation, "alliance");
        OpinionSystem.Forget(campaign, relation, "non_aggression_pact");
        OpinionSystem.Forget(campaign, relation, "guaranteed_us");
        OpinionSystem.Settle(campaign, relation);
        // And no shared defence organization: NATO's members cannot go to war with each
        // other (InternationalOrganizations), which is the tie these checks mean to cut.
        foreach (var organization in campaign.Organizations)
            if (organization.Members.Contains(one.Id) && organization.Members.Contains(two.Id))
                organization.Members.Remove(two.Id);
    }

    internal static string ADependencyIsABargainAboutPower()
    {
        if (SubjectCatalog.All.Count == 0) return "common/subjects declared no kinds at all";

        var campaign = Campaign.CreateBootstrap(20240501);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var overlord = campaign.NationByTag("FRA");
        var subject = campaign.NationByTag("LUX");
        var other = campaign.NationByTag("DEU");
        if (overlord is null || subject is null || other is null) return "";

        Estrange(campaign, overlord, subject);
        Estrange(campaign, overlord, other);
        Estrange(campaign, subject, other);
        if (!SubjectSystem.Subjugate(campaign, overlord, subject, "puppet", out var refusal))
            return $"could not make LUX a puppet of FRA: {refusal}";
        if (!subject.AnswersTo(overlord.Id)) return "LUX does not think it answers to FRA";

        // Refusals that matter.
        if (SubjectSystem.Subjugate(campaign, other, subject, "puppet", out _))
            return "LUX was handed to DEU while it already answered to FRA";
        if (SubjectSystem.Subjugate(campaign, overlord, overlord, "puppet", out _))
            return "FRA was made to answer to itself";
        if (SubjectSystem.Subjugate(campaign, overlord, other, "no_such_kind", out _))
            return "a kind that is not in common/subjects was accepted";

        // Tribute. Run a month and see whether anything moved from one treasury to the other.
        // Measured from what the tribute actually moved, not from the treasuries it moved
        // between. The first version of this check compared France's balance before and
        // after and failed: France runs a deficit of several billion a week, and sixty
        // million of Luxembourgish tribute inside it is not something a subtraction can
        // see. The mechanic was right and the measurement was wrong.
        for (var day = 0; day < 35; day++) campaign.TickDay();
        var taken = overlord.TributeTakenWeekly;
        var paid = subject.TributePaidWeekly;
        if (subject.WeeklyTaxIncome > 0f && taken <= 0f)
            return $"LUX raised {subject.WeeklyTaxIncome:0.00}B a week and FRA collected nothing";
        if (Math.Abs(taken - paid) > 0.0001f)
            return $"FRA took {taken:0.000}B and LUX paid {paid:0.000}B - the money did not balance";

        // Turning up. FRA attacked; the puppet is dragged in, because that is what its kind says.
        var kind = SubjectCatalog.Of("puppet");
        if (kind is null || !kind.DraggedIntoWars) return "the shipped 'puppet' no longer joins its overlord's wars";

        // Drift. The same subject, under two different overlords, must not want the same thing.
        var steady = SubjectSystem.LibertyDesireOf(campaign, subject);
        var wasStability = overlord.Stability;
        overlord.Stability = 0.05f;
        var shaken = SubjectSystem.LibertyDesireOf(campaign, subject);
        overlord.Stability = wasStability;
        if (shaken <= steady)
            return $"LUX wants out {steady:P0} under a steady FRA and {shaken:P0} under a collapsing one";

        // And letting go.
        SubjectSystem.Release(campaign, subject);
        if (subject.OverlordId != 0 || subject.SubjectType.Length != 0)
            return "LUX was released and still thinks it answers to somebody";

        Console.WriteLine($"  dependencies: {SubjectCatalog.All.Count} kind(s) in common/subjects; " +
                          $"FRA took {taken:0.000}B from LUX last week and LUX paid the same; " +
                          $"wanting out {steady:P0} under a steady overlord, {shaken:P0} under a shaken one.");
        return "";
    }

    /// <summary>
    /// A movement without land (Movements) is a country with a headquarters instead of
    /// a map: founded with the world, standing nowhere and at war with nobody; it lives off its
    /// following; its headquarters moves; an uprising that takes a region makes it a country
    /// with a capital and militia; and losing that region makes it landless again, somewhere
    /// that will have it. On its own world, stepped by hand.
    /// </summary>
    internal static string MovementsLiveWithoutLand()
    {
        var campaign = Campaign.CreateBootstrap(20240905);
        if (!AttachMap(campaign)) return "no province map to stand on";
        var all = Movements.Of(campaign).ToList();
        var declared = MovementCatalog.All.Where(definition => !definition.Template && definition.StartActive).ToArray();
        var representedAsFactions = 0;
        foreach (var definition in declared)
        {
            if (all.Any(nation => nation.Movement?.Id == definition.Id)) continue;
            // Exact tags or an explicit territorial_faction identity link may represent a
            // movement already. Never substitute an unrelated country with a colliding name.
            var actor = Movements.TerritorialActor(campaign, definition) ?? campaign.NationByTag(definition.Tag);
            var faction = Factions.Of(actor);
            if (actor is not null && actor.Movement is null && faction is not null &&
                (string.Equals(faction.Tag, definition.Tag, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(faction.Id, definition.TerritorialFaction, StringComparison.Ordinal)) &&
                campaign.StatesOf(actor).Length > 0)
            { representedAsFactions++; continue; }
            return $"movement {definition.Id}/{definition.Tag} has neither its movement nor an authored territorial faction";
        }
        Console.WriteLine($"  movement representation: {declared.Length} active definitions; " +
                          $"{all.Count} movement actors and {representedAsFactions} already-territorial factions.");
        var houthis = campaign.NationByTag("HTH");
        if (campaign.NationByTag("HOU") is not null || houthis?.FactionId != "ansar_allah" ||
            campaign.StatesOf(houthis).Length == 0 || !campaign.Armies.Any(a => a.NationId == houthis.Id))
            return "Ansar Allah must have one territorial administration with its land and army, not a duplicate HOU headquarters";
        Console.WriteLine($"  Yemen: HTH owns {campaign.StatesOf(houthis).Length} regions; " +
            $"{campaign.Armies.Count(a => a.NationId == houthis.Id)} formations; no duplicate HOU actor.");
        foreach (var movement in all)
        {
            if (!movement.Landless) return $"{movement.Tag} starts with land";
            if (campaign.StatesOf(movement).Length > 0) return $"{movement.Tag} starts owning a region";
            if (Movements.HostOf(campaign, movement) is null) return $"{movement.Tag} has no host";
            if (movement.Movement.HeadquartersLatitude == 0f && movement.Movement.HeadquartersLongitude == 0f)
                return $"{movement.Tag}'s headquarters is not on the map";
        }

        var hamas = campaign.NationByTag("HMS");
        var palestine = campaign.NationByTag("PSE");
        var israel = campaign.NationByTag("ISR");
        if (hamas is null || palestine is null || israel is null) return "no Hamas, Palestine or Israel to test with";
        if (CampaignDiplomacy.CanDeclareWar(campaign, israel.Id, hamas.Id, out _))
            return "a country may declare war on a headquarters";

        // Eight weeks of its following.
        var treasury = hamas.Treasury;
        var cadres = hamas.Movement.Cadres;
        for (var week = 0; week < 8; week++) MovementRules.Week(campaign, hamas);
        if (hamas.Movement.IncomeWeekly <= 0f || hamas.Treasury <= treasury)
            return $"eight weeks brought Hamas {hamas.Treasury - treasury:0.000} bn ({hamas.Movement.IncomeWeekly:0.0000} a week)";
        if (hamas.Movement.Cadres <= cadres) return $"Hamas's cadres went from {cadres:0} to {hamas.Movement.Cadres:0}";
        var eightWeekDonations = hamas.Treasury - treasury;

        // The headquarters moves to a sponsor.
        var iran = campaign.NationByTag("IRN");
        var tehran = campaign.StatesOf(iran).OrderByDescending(state => state.Population).First();
        var refusal = MovementActions.Refusal(campaign, hamas, "move", tehran.MapIndex, -1);
        if (refusal.Length > 0) return $"Hamas cannot move to {tehran.Id}: {refusal}";
        new MovementActionCommand { IssuerId = hamas.Id, Action = "move", StateMapIndex = tehran.MapIndex }.Apply(campaign);
        hamas.Movement.ArrivesOnDay = campaign.ElapsedDays;
        MovementRules.Week(campaign, hamas);
        if (Movements.HostOf(campaign, hamas) != iran) return $"Hamas's headquarters ended up in {Movements.HostOf(campaign, hamas)?.Tag}";

        // An uprising that takes the West Bank.
        var westBank = campaign.States.Find(state => state.Id == "ps_wbk");
        if (westBank is null) return "no West Bank";
        hamas.Movement.Cadres = 40000f;
        MovementRules.TakeRegion(campaign, hamas, westBank, palestine);
        if (hamas.Landless || campaign.StatesOf(hamas).Length == 0) return "Hamas rose and holds nothing";
        if (hamas.CapitalStateMapIndex != westBank.MapIndex) return "Hamas holds the West Bank and is not governed from it";
        if (hamas.Movement is not null) return "a movement holding a country did not graduate to sovereign statehood";
        var militia = campaign.Armies.Count(army => army.NationId == hamas.Id);
        if (militia == 0) return "Hamas took the West Bank with no militia to hold it";

        // And loses it again.
        // Statehood now graduates a movement to a regular country. Its fall belongs to
        // the weekly country lifecycle, not Week(), which only updates existing movements.
        // Choose the exile branch explicitly in this fixture; real campaigns retain their roll.
        campaign.Defines.MovementExileBase = 1f;
        var topology = campaign.ProvinceMap;
        var provinces = Enumerable.Range(1, topology.ProvinceCount).Where(province => topology.ProvinceToNation[province] == hamas.MapIndex).ToList();
        StatePartition.Annex(campaign, provinces, israel, asConquest: true);
        MovementFall.Decide(campaign);
        MovementRules.Week(campaign, hamas);
        if (!hamas.Landless) return "Hamas lost all its ground and is not landless again";
        var host = Movements.HostOf(campaign, hamas);
        if (host is null || host == israel) return $"Hamas, landless again, went to {host?.Tag ?? "nowhere"}";

        Console.WriteLine($"  movements: {all.Count} founded ({string.Join(", ", all.Select(nation => nation.Tag))}); " +
                          $"Hamas made {eightWeekDonations * 1000:0} M in eight weeks, moved to {tehran.Id}, rose in the West Bank " +
                          $"with {militia} militia, lost it and went on from {host.Tag}.");
        return "";
    }

    /// <summary>
    /// A country that loses the last of its ground can carry on as a government in exile - its
    /// own peoples, a homeland of the regions it held, a headquarters with a friend that did not
    /// take its ground, the respect a government has - and a movement ground down to nothing is
    /// no more. On its own world, stepped by hand.
    /// </summary>
    internal static string AFallenCountryGoesIntoExile()
    {
        var campaign = Campaign.CreateBootstrap(20240921);
        if (!AttachMap(campaign)) return "no province map to stand on";
        var ukraine = campaign.NationByTag("UKR");
        var russia = campaign.NationByTag("RUS");
        if (ukraine is null || russia is null) return "no Ukraine or Russia to test with";
        MovementFall.RememberHomelands(campaign);
        if (!campaign.Homelands.TryGetValue(ukraine.Id, out var home) || home.Count < 10)
            return $"Ukraine's homeland is {home?.Count ?? 0} region(s)";

        var topology = campaign.ProvinceMap;
        var provinces = Enumerable.Range(1, topology.ProvinceCount)
            .Where(province => topology.ProvinceToNation[province] == ukraine.MapIndex).ToList();
        StatePartition.Annex(campaign, provinces, russia, asConquest: true);
        if (campaign.StatesOf(ukraine).Length > 0) return "Ukraine still holds ground after losing all of it";
        var chance = MovementFall.ExileChance(campaign, ukraine, out var refuge);
        if (refuge is null) return "nowhere would take Ukraine's government in";
        MovementFall.GoIntoExile(campaign, ukraine, refuge);
        var state = ukraine.Movement;
        if (state is null || !state.Exile || !ukraine.Landless) return "Ukraine did not become a government in exile";
        var definition = Movements.DefinitionOf(ukraine);
        if (definition is null || !definition.Has("government_in_exile")) return "the government in exile has no definition of its own";
        if (definition.Homeland.Count < 10) return $"the government in exile claims {definition.Homeland.Count} region(s)";
        var host = Movements.HostOf(campaign, ukraine);
        if (host is null || host == russia) return $"the government in exile sits in {host?.Tag ?? "nowhere"}";
        if (state.Support.Count == 0) return "nobody at home follows the government in exile";
        if (campaign.Armies.Any(army => army.NationId == ukraine.Id)) return "the army stayed on the map without a country";
        var recognitions = state.RecognisedBy.Count;

        // And a movement with nothing left is no more.
        var hamas = campaign.NationByTag("HMS");
        if (hamas?.Movement is null) return "no Hamas to wear down";
        hamas.Movement.Cadres = 0f;
        hamas.Treasury = 0f;
        hamas.Movement.Support.Clear();
        MovementFall.Endure(campaign, hamas);
        if (!hamas.Movement.Dissolved) return "a movement with no cadres, no money and no following carries on";
        if (MovementActions.Refusal(campaign, hamas, "fundraise", -1, -1).Length == 0) return "a dissolved movement can still act";

        Console.WriteLine($"  exile: Ukraine's government went to {host.Tag} ({refuge.DisplayName}) at a {chance:P0} chance, " +
                          $"claiming {definition.Homeland.Count} regions, recognised by {recognitions}, with {Fmt.People(state.Cadres)} cadres; " +
                          $"a Hamas worn to nothing dissolved.");
        return "";
    }

    /// <summary>
    /// The countries' side of a movement: a listing closes the door to recognition from the
    /// country that listed it, a host that throws the headquarters out sees it go elsewhere, and
    /// a country that funds it pays it by the week.
    /// </summary>
    internal static string TheWorldDealsWithMovements()
    {
        var campaign = Campaign.CreateBootstrap(20240922);
        if (!AttachMap(campaign)) return "no province map to stand on";
        var hamas = campaign.NationByTag("HMS");
        var usa = campaign.NationByTag("USA");
        var qatar = campaign.NationByTag("QAT");
        var turkey = campaign.NationByTag("TUR");
        if (hamas?.Movement is null || usa is null || qatar is null || turkey is null) return "no Hamas, America, Qatar or Turkey";
        var state = hamas.Movement;

        new MovementDealingCommand { IssuerId = usa.Id, MovementId = hamas.Id, Action = "designate" }.Apply(campaign);
        if (!state.DesignatedBy.Contains(usa.Id)) return "America's listing did not stick";
        if (MovementDealings.Refusal(campaign, usa, hamas, "recognise").Length == 0) return "a country recognises what it lists as terrorist";

        if (Movements.HostOf(campaign, hamas) != qatar) return $"Hamas's headquarters starts in {Movements.HostOf(campaign, hamas)?.Tag}";
        new MovementDealingCommand { IssuerId = qatar.Id, MovementId = hamas.Id, Action = "expel" }.Apply(campaign);
        var host = Movements.HostOf(campaign, hamas);
        if (host == qatar) return "Qatar expelled the headquarters and it stayed";

        var treasury = turkey.Treasury;
        new MovementDealingCommand { IssuerId = turkey.Id, MovementId = hamas.Id, Action = "sponsor" }.Apply(campaign);
        if (!state.SponsoredBy.ContainsKey(turkey.Id)) return "Turkey's funding did not stick";
        MovementRules.Week(campaign, hamas);
        if (turkey.Treasury >= treasury) return "Turkey funds Hamas and pays nothing";

        Console.WriteLine($"  dealings: America lists Hamas; Qatar throws it out and it goes to {host?.Tag ?? "nowhere"}; " +
                          $"Turkey pays {Fmt.Money(treasury - turkey.Treasury)} a week.");
        return "";
    }

    /// <summary>
    /// On the first day every country that has a flag of its own flies it, or a variant of it.
    ///
    /// A regime's flag is ruled out when the country stops being that regime (common/flags,
    /// own_flag_is_for) - and a rule written against the wrong word rules it out on day one.
    /// Saudi Arabia's shahada was declared the flag of a theocracy while the scenario calls the
    /// kingdom traditionalist, so Riyadh opened under the generic traditionalist banner: the
    /// Carlist cross of Burgundy. Nothing looked at the flags a world actually starts with.
    /// </summary>
    internal static string EveryCountryOpensUnderItsOwnFlag()
    {
        if (SpriteCatalog.Count == 0) SpriteCatalog.Load();
        var world = Campaign.CreateBootstrap(20240101);
        var owned = 0;
        var wrong = new List<string>();
        foreach (var nation in world.Nations)
        {
            var own = "flag_" + FlagCatalog.Slug(nation.Tag);
            if (!SpriteCatalog.Has(own)) continue;
            owned++;
            var flies = FlagCatalog.Resolve(world, nation);
            if (flies.StartsWith(own, StringComparison.OrdinalIgnoreCase)) continue;
            wrong.Add($"{nation.Tag} ({nation.Government}, {nation.Ideology}) flies {(flies.Length == 0 ? "nothing" : flies)}");
        }
        if (owned < 150) return $"only {owned} countries have a flag of their own drawn";
        if (wrong.Count > 0)
            return $"{wrong.Count} of {owned} countries open under somebody else's flag: " + string.Join("; ", wrong.Take(6));
        Console.WriteLine($"  flags: all {owned} countries with a flag of their own open under it (or a variant of it).");
        return "";
    }

    internal static string AProclaimedStateFliesItsOwnFlag(Campaign campaign)
    {
        // The sprite table is loaded by the flags check further down, which runs long after
        // this one. Asking an empty table whether a flag exists is how this first reported
        // that none of the forty-four had been drawn.
        if (SpriteCatalog.Count == 0) SpriteCatalog.Load();

        var formables = FormableNations.All.ToList();
        if (formables.Count == 0) return "no formable nations are declared";

        var drawn = 0;
        var undrawn = new List<string>();
        var wrong = new List<string>();

        foreach (var definition in formables)
        {
            var own = "flag_formable_" + FlagCatalog.Slug(definition.Id);
            if (!SpriteCatalog.Has(own)) { undrawn.Add(definition.Id); continue; }
            drawn++;

            // Every country that could proclaim it has to arrive at the same flag. If the
            // answer depends on the founder, the founder's flag is still winning.
            foreach (var tag in definition.EligibleTags)
            {
                var nation = campaign.NationByTag(tag);
                if (nation is null) continue;
                var preview = FlagCatalog.ResolveFormationPreview(campaign, nation, definition.Id);
                // The stem, or anything built on it: `flag_formable_china_reunified_liberalism`
                // is the proclaimed state's flag for a proclaimed state that is also liberal,
                // which is a better answer than the plain one rather than a wrong one. A
                // founder's own `flag_<tag>_<id>` family answers first when it exists - the
                // authored reunified-China set draws one per founder - and is the same
                // answer a mod would rebind.
                var ownStem = "flag_" + FlagCatalog.Slug(tag) + "_" + FlagCatalog.Slug(definition.Id);
                if (preview.StartsWith(own, StringComparison.OrdinalIgnoreCase) ||
                    preview.StartsWith(ownStem, StringComparison.OrdinalIgnoreCase)) continue;
                wrong.Add($"{tag} proclaiming {definition.Id} would fly " +
                          $"'{(preview.Length == 0 ? "nothing" : preview)}' rather than '{own}'");
                break;
            }
        }

        if (wrong.Count > 0) return string.Join("; ", wrong.Take(4));

        // And nothing has been broken underneath: a country that has proclaimed nothing
        // still flies what it flew.
        foreach (var tag in new[] { "RUS", "FRA", "USA", "SRB", "KOR" })
        {
            var nation = campaign.NationByTag(tag);
            if (nation is null) continue;
            var resolved = FlagCatalog.Resolve(campaign, nation);
            if (resolved.StartsWith("flag_formable_", StringComparison.OrdinalIgnoreCase))
                return $"{tag} has proclaimed nothing and is flying '{resolved}'";
        }

        Console.WriteLine($"  flags: {drawn:N0} of {formables.Count:N0} formable nation(s) have a flag " +
                          $"of their own and every eligible founder resolves to it" +
                          (undrawn.Count == 0
                              ? "."
                              : $"; {undrawn.Count:N0} still to be drawn: {string.Join(", ", undrawn.Take(8))}" +
                                (undrawn.Count > 8 ? ", …" : "") + "."));
        return "";
    }

}
