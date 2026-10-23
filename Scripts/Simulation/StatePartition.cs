using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// What happens to a region when a border is redrawn through the middle of it.
///
/// Until now nothing did. A peace treaty moved province *control* and left ownership
/// alone, so a country could hold half of an enemy region for a decade while the enemy's
/// economy went on counting every person in it. Annexation is the missing half: provinces
/// change owner permanently, and any region the new border cuts through stops being one
/// region and becomes as many as the border left behind.
///
/// A split region is not a label. Each piece is a full region with its own population,
/// its own buildings, its own social classes and its own deposits, divided by how much of
/// the old region it holds - so half of an industrial region is half its industry, and the
/// two halves develop apart from that day on. The largest piece keeps the original
/// identity, which is what lets saves, history and war goals written before the split
/// still mean something afterwards.
///
/// Pieces are also split by connectivity, not only by owner: a region cut into two
/// separate pockets under the same flag becomes two regions, because two pockets that
/// cannot reach each other are not one place.
/// </summary>
public static class StatePartition
{
    /// <summary>
    /// Map indices for regions the campaign creates. The authored table ends below this,
    /// so a dynamic region can never collide with one a mod or the scenario declared.
    /// </summary>
    public const int FirstDynamicMapIndex = 4000;

    /// <summary>
    /// Move permanent ownership of provinces to a nation, then repartition every region
    /// the new border touched. Returns the regions that now exist because of it.
    /// </summary>
    /// <param name="asConquest">
    /// Whether the world should read this as land taken. True for every peace and every
    /// annexation, and false for exactly one caller: a country seceding does not take the
    /// ground it is standing on, and charging it notoriety for the land it already lived on
    /// would have every new state born an international pariah.
    /// </param>
    public static IReadOnlyList<ScenarioState> Annex(Campaign campaign, IEnumerable<int> provinces,
        Nation newOwner, bool asConquest = true)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null || newOwner is null || provinces is null) return Array.Empty<ScenarioState>();

        var touched = new HashSet<int>();
        var formerOwnerIndices = asConquest ? new HashSet<ushort>() : null;
        var moved = 0;
        var peopleTaken = 0L;
        var takenFrom = asConquest ? new Dictionary<ushort, long>() : null;
        // Every road a region comes back by - conquest or a treaty - ends its breakaway's rising.
        var previousHolder = new Dictionary<int, ushort>();
        foreach (var province in provinces)
        {
            if (province <= 0 || province > topology.ProvinceCount) continue;
            var prevOwner = topology.ProvinceToNation[province];
            if (prevOwner == (ushort)newOwner.MapIndex) continue;
            if (prevOwner > 0) formerOwnerIndices?.Add(prevOwner);
            var stateIndex = topology.ProvinceToState[province];
            if (touched.Add(stateIndex))
            {
                foreach (var state in campaign.States)
                {
                    if (state.MapIndex != stateIndex) continue;
                    peopleTaken += state.Population;
                    if (takenFrom is not null && prevOwner > 0)
                        takenFrom[prevOwner] = takenFrom.GetValueOrDefault(prevOwner) + Math.Max(0L, state.Population);
                    if (previousHolder is not null && prevOwner > 0) previousHolder[stateIndex] = prevOwner;
                    break;
                }
            }
            topology.ProvinceToNation[province] = (ushort)newOwner.MapIndex;
            campaign.SetProvinceController(province, (ushort)newOwner.MapIndex);
            moved++;
        }
        if (moved == 0) return Array.Empty<ScenarioState>();

        // What the neighbours make of it. Scaled by people rather than provinces, because a
        // hundred square kilometres of tundra is not what makes a neighbourhood nervous.
        if (asConquest) Statecraft.RecordAnnexation(campaign, newOwner, peopleTaken);
        // A breakaway crushed: the region taken back from a state that seceded from its conqueror
        // goes quiet for years rather than rising again the moment the cooling-off period ends.
        if (previousHolder is { Count: > 0 })
            foreach (var (stateIndex, holder) in previousHolder)
            {
                var breakaway = campaign.Nations.Find(n => n.MapIndex == holder);
                if (breakaway is null || breakaway.SecededFromId != newOwner.Id) continue;
                var region = campaign.States.Find(s => s.MapIndex == stateIndex);
                if (region is not null) region.SecessionPressure = MathF.Min(region.SecessionPressure, SecessionCore.Weights.CrushedPressure);
            }
        // Conquered ground keeps what its work was worth. Its output was priced at the
        // conqueror's own 2024 multiplier (Nation.OutputScale), so taking a poor neighbour made
        // its people as productive as the conqueror's overnight and conquest paid the rich in
        // GDP. The multiplier is blended by people, the way a peaceful transfer already blends it.
        if (takenFrom is { Count: > 0 } && newOwner.OutputScale > 0f && newOwner.Population > 0)
        {
            double weight = newOwner.Population, sum = newOwner.OutputScale * (double)newOwner.Population;
            foreach (var (index, people) in takenFrom)
            {
                var loser = campaign.Nations.Find(n => n.MapIndex == index);
                if (loser is null || loser.OutputScale <= 0f || people <= 0) continue;
                weight += people;
                sum += loser.OutputScale * (double)people;
            }
            if (weight > 0 && sum > 0) newOwner.OutputScale = (float)(sum / weight);
        }

        // One piece per owner, and nothing said about geography - the same rule the map is
        // built under, and for the same reason it gives there.
        //
        // This used to separate pockets as well, so that a region arriving in two disconnected
        // halves became two regions. It reads like tidiness and it was the last source of
        // border gore in the game: a region's provinces are connected through LAND, so every
        // region with an island in it, a lake through it or a strait across it is already in
        // pieces before any war touches it, and handing such a region over WHOLE - the thing
        // the peace code works hardest to do - shredded it on arrival. Twelve of them in a
        // decade, every one from a treaty that had taken the entire region exactly as it was
        // asked to.
        //
        // A region under one owner is one region, in two pockets or in twenty. Countries are
        // shaped like that; so are provinces.
        var created = new List<ScenarioState>();
        foreach (var stateMapIndex in touched)
        {
            var state = campaign.States.Find(candidate => candidate.MapIndex == stateMapIndex);
            if (state is not null) created.AddRange(Repartition(campaign, state, separatePockets: false));
        }

        // A border can put a region back together as easily as it can cut one: taking the
        // last enemy province out of a region that was split years ago makes it one place
        // again, and the ledger should say so.
        Reunify(campaign);

        campaign.InvalidateStateIndex();
        if (formerOwnerIndices is { Count: > 0 })
        {
            foreach (var ownerMapIndex in formerOwnerIndices)
            {
                var formerOwner = campaign.Nations.Find(n => n.MapIndex == ownerMapIndex);
                if (formerOwner is null || formerOwner.Movement is not null || campaign.HasOwnedLand(formerOwner)) continue;
                formerOwner.Gdp = 0f;
                formerOwner.GrossOutput = 0f;
                formerOwner.WeeklyTaxIncome = 0f;
                formerOwner.WeeklyExpenses = 0f;
                formerOwner.WeeklyBalance = 0f;
            }
        }
        campaign.MarkMapChanged();
        return created;
    }

    /// <summary>
    /// Split one region so that every connected group of its provinces under a single
    /// owner is its own region. Returns the regions this created; an empty list means the
    /// region was already whole and only its owner may have changed.
    /// </summary>
    public static IReadOnlyList<ScenarioState> Repartition(Campaign campaign, ScenarioState state,
        bool separatePockets = true)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null || state is null) return Array.Empty<ScenarioState>();

        var provinces = new List<int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToState[province] == (ushort)state.MapIndex)
                provinces.Add(province);

        // A region with no ground left keeps its identity and its zero. Deleting it would
        // break every save, war goal and history entry that names it.
        if (provinces.Count == 0) return Array.Empty<ScenarioState>();

        var pieces = Components(topology, provinces, separatePockets);
        if (pieces.Count <= 1)
        {
            // Whole, but possibly under a new flag.
            var owner = topology.ProvinceToNation[provinces[0]];
            state.Unclaimed = owner == 0 && !LakeRegions.IsWaterState(state);
            if (state.NationMapIndex != owner)
            {
                state.NationMapIndex = owner;
                state.NationId = campaign.Nations.Find(nation => nation.MapIndex == owner)?.Tag ?? "";
            }
            return Array.Empty<ScenarioState>();
        }

        // Which piece keeps the region's name matters. The one that stayed with the country
        // that owned it keeps it - the way a seceding half gets the new name and the rump
        // keeps the old one. If the whole region changed hands in pieces, the largest one
        // inherits instead, so the identity is never simply lost.
        pieces.Sort((left, right) => right.Count.CompareTo(left.Count));
        var originalOwner = (ushort)state.NationMapIndex;
        var parentIndex = pieces.FindIndex(piece => topology.ProvinceToNation[piece[0]] == originalOwner);
        if (parentIndex < 0) parentIndex = 0;

        var total = provinces.Count;
        var parentCentre = Centre(topology, provinces);
        var created = new List<ScenarioState>();

        for (var index = 0; index < pieces.Count; index++)
        {
            if (index == parentIndex) continue;
            var piece = pieces[index];
            var share = piece.Count / (float)total;
            var owner = topology.ProvinceToNation[piece[0]];
            var carved = Carve(campaign, state, piece, share, owner,
                Unique(campaign, Name(state.Name, Centre(topology, piece), parentCentre, index)));
            campaign.States.Add(carved);
            created.Add(carved);
        }

        // The parent keeps its piece and whatever flag flies over it.
        var parentOwner = topology.ProvinceToNation[pieces[parentIndex][0]];
        state.Unclaimed = parentOwner == 0 && !LakeRegions.IsWaterState(state);
        if (state.NationMapIndex != parentOwner)
        {
            state.NationMapIndex = parentOwner;
            state.NationId = campaign.Nations.Find(nation => nation.MapIndex == parentOwner)?.Tag ?? state.NationId;
        }

        RebindArmies(campaign, topology);
        campaign.InvalidateStateIndex();
        return created;
    }

    /// <summary>
    /// Cut every region the political map already divides, once, when the map is attached.
    ///
    /// The 2024 map ships nine of them - Kherson, Kharkiv, Donetsk, Zaporizhzhia and
    /// Luhansk across the Russian front, Santa Cruz around the Patagonian ice field, the
    /// Red Sea around Bir Tawil, and the two sides of the Cyprus buffer zone. Each one was
    /// a region whose record named one owner while the ground under it named another.
    ///
    /// Owner only, without splitting pockets: this is fixing who owns what, not re-deciding
    /// where places are.
    /// </summary>
    public static int RepartitionWorld(Campaign campaign)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null) return 0;

        var owners = new Dictionary<int, ushort>();
        var wrong = new HashSet<int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var region = topology.ProvinceToState[province];
            if (region == 0) continue;
            var owner = topology.ProvinceToNation[province];
            if (!owners.TryGetValue(region, out var first)) owners[region] = owner;
            else if (first != owner) wrong.Add(region);
        }

        // A region can disagree with its ground without being divided at all: Crimea's
        // provinces are Russian in the political map while the region record still said
        // Ukraine, so nothing was there to split and nothing ever corrected it either.
        // Repartitioning a whole region does not cut it - it just puts the right flag on.
        foreach (var state in campaign.States)
            if (owners.TryGetValue(state.MapIndex, out var ground) && state.NationMapIndex != ground)
                wrong.Add(state.MapIndex);

        if (wrong.Count == 0) return 0;

        var created = 0;
        foreach (var region in wrong)
        {
            var state = campaign.States.Find(candidate => candidate.MapIndex == region);
            if (state is null) continue;
            created += Repartition(campaign, state, separatePockets: false).Count;
        }
        // Invalidate whether or not a region was carved: a region that only changed flag
        // changed nothing about the index, but a region that did is now in the list twice
        // as far as anything holding the old index is concerned.
        campaign.InvalidateStateIndex();
        campaign.MarkMapChanged();
        return created;
    }

    /// <summary>
    /// Put back together every region whose pieces are all under one flag again.
    ///
    /// A border that goes away should leave the map as it found it. Without this a region
    /// cut in half by a war stayed two regions for the rest of the campaign even after one
    /// country held every province of it again - two entries in the ledger, two sets of
    /// buildings, two populations, for one place.
    ///
    /// Everything countable is added back up; everything that is a rate is averaged by how
    /// many people each piece brought, because a region that is nine tenths literate and a
    /// region that is half literate do not make a region that is either.
    /// </summary>
    public static int Reunify(Campaign campaign)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null) return 0;

        var families = new Dictionary<int, List<ScenarioState>>();
        foreach (var state in campaign.States)
        {
            if (state.ParentMapIndex == 0) continue;
            if (!families.TryGetValue(state.ParentMapIndex, out var children))
                families[state.ParentMapIndex] = children = new List<ScenarioState>();
            children.Add(state);
        }
        if (families.Count == 0) return 0;

        var provincesOf = new Dictionary<int, List<int>>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var region = topology.ProvinceToState[province];
            if (region == 0) continue;
            if (!provincesOf.TryGetValue(region, out var list)) provincesOf[region] = list = new List<int>();
            list.Add(province);
        }

        var merged = 0;
        foreach (var (parentIndex, children) in families)
        {
            var parent = campaign.States.Find(candidate => candidate.MapIndex == parentIndex);
            if (parent is null) continue;

            // One flag over every piece, the parent's own ground included. A piece with no
            // ground left has no opinion: it is an empty shell the merge will absorb.
            ushort? flag = null;
            var agreed = true;
            foreach (var piece in children.Prepend(parent))
            {
                if (!provincesOf.TryGetValue(piece.MapIndex, out var owned) || owned.Count == 0) continue;
                var owner = topology.ProvinceToNation[owned[0]];
                foreach (var province in owned)
                    if (topology.ProvinceToNation[province] != owner) { agreed = false; break; }
                if (!agreed) break;
                if (flag is null) flag = owner;
                else if (flag != owner) { agreed = false; break; }
            }
            if (!agreed || flag is null) continue;

            foreach (var child in children) Absorb(campaign, topology, parent, child, provincesOf);
            parent.NationMapIndex = flag.Value;
            parent.NationId = campaign.Nations.Find(nation => nation.MapIndex == flag.Value)?.Tag ?? parent.NationId;
            merged += children.Count;
        }

        if (merged > 0)
        {
            RebindArmies(campaign, topology);
            campaign.InvalidateStateIndex();
            campaign.MarkMapChanged();
        }
        return merged;
    }

    /// <summary>One piece back into the region it was cut from.</summary>
    private static void Absorb(Campaign campaign, ProvinceTopology topology, ScenarioState parent,
        ScenarioState child, Dictionary<int, List<int>> provincesOf)
    {
        if (provincesOf.TryGetValue(child.MapIndex, out var owned))
            foreach (var province in owned) topology.ProvinceToState[province] = (ushort)parent.MapIndex;

        var here = parent.Population;
        var there = child.Population;
        var total = here + there;
        float Blend(float left, float right) =>
            total <= 0 ? MathF.Max(left, right) : (left * here + right * there) / total;

        if (parent.ResidentAwareness >= 0f || child.ResidentAwareness >= 0f)
            parent.ResidentAwareness = Blend(ResidentAwareness.Of(campaign, parent), ResidentAwareness.Of(campaign, child));
        parent.Literacy = Blend(parent.Literacy, child.Literacy);
        parent.StandardOfLiving = Blend(parent.StandardOfLiving, child.StandardOfLiving);
        parent.Radicalism = Blend(parent.Radicalism, child.Radicalism);
        parent.Urbanization = Blend(parent.Urbanization, child.Urbanization);
        parent.NonBuildingJobs = parent.NonBuildingJobs < 0 || child.NonBuildingJobs < 0 ? -1 : parent.NonBuildingJobs + child.NonBuildingJobs;
        parent.Population = total;
        foreach (var tag in child.CoreTags)
            if (!parent.CoreTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) parent.CoreTags.Add(tag);
        foreach (var tag in child.Claims)
            if (!parent.Claims.Contains(tag, StringComparer.OrdinalIgnoreCase)) parent.Claims.Add(tag);

        foreach (var group in child.PopulationGroups)
        {
            var match = parent.PopulationGroups.Find(candidate =>
                candidate.Class == group.Class && candidate.Sector == group.Sector &&
                candidate.Culture == group.Culture &&
                candidate.Religion == group.Religion && candidate.Profession == group.Profession);
            if (match is null) { parent.PopulationGroups.Add(group); continue; }
            var mine = match.Size;
            var theirs = group.Size;
            var both = mine + theirs;
            float Mix(float left, float right) =>
                both <= 0 ? MathF.Max(left, right) : (left * mine + right * theirs) / both;
            match.Employment = Mix(match.Employment, group.Employment);
            match.Wealth = Mix(match.Wealth, group.Wealth);
            match.StandardOfLiving = Mix(match.StandardOfLiving, group.StandardOfLiving);
            match.Radicalism = Mix(match.Radicalism, group.Radicalism);
            match.Size = both;
        }

        foreach (var (building, count) in child.Buildings)
            parent.Buildings[building] = parent.Buildings.TryGetValue(building, out var have) ? have + count : count;
        foreach (var (deposit, amount) in child.ResourceDeposits)
            parent.ResourceDeposits[deposit] = parent.ResourceDeposits.TryGetValue(deposit, out var had)
                ? had + amount : amount;

        campaign.States.Remove(child);
    }

    /// <summary>
    /// Connected groups of provinces that share an owner. Two pockets of the same country
    /// that cannot walk to each other are two places, not one.
    /// </summary>
    private static List<List<int>> Components(ProvinceTopology topology, List<int> provinces,
        bool separatePockets)
    {
        // One piece per owner, and nothing said about geography. This is what the 2024 map
        // wants: the question there is "who owns this region", not "is this region one
        // place" - and answering the second at load would cut every archipelago in the
        // world into one region per island, because islands are not neighbours in a graph
        // built out of land borders.
        if (!separatePockets)
        {
            var byOwner = new Dictionary<ushort, List<int>>();
            foreach (var province in provinces)
            {
                var owner = topology.ProvinceToNation[province];
                if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<int>();
                list.Add(province);
            }
            return byOwner.Values.ToList();
        }

        var members = new HashSet<int>(provinces);
        var seen = new HashSet<int>();
        var components = new List<List<int>>();
        var queue = new Queue<int>();

        foreach (var start in provinces)
        {
            if (!seen.Add(start)) continue;
            var owner = topology.ProvinceToNation[start];
            var component = new List<int> { start };
            queue.Clear();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var province = queue.Dequeue();
                foreach (var neighbour in topology.Neighbors[province])
                {
                    if (!members.Contains(neighbour) || topology.ProvinceToNation[neighbour] != owner) continue;
                    if (!seen.Add(neighbour)) continue;
                    component.Add(neighbour);
                    queue.Enqueue(neighbour);
                }
            }
            components.Add(component);
        }
        return components;
    }

    /// <summary>
    /// Cut one piece out of a region. Everything countable moves across in proportion to
    /// how much of the region the piece is; everything that is a rate rather than a count -
    /// literacy, living standards, radicalism - is carried over unchanged, because a border
    /// does not make the people on one side of it suddenly better read.
    /// </summary>
    private static ScenarioState Carve(Campaign campaign, ScenarioState parent, List<int> piece,
        float share, ushort owner, string name)
    {
        var mapIndex = NextMapIndex(campaign);
        var nation = campaign.Nations.Find(candidate => candidate.MapIndex == owner);
        var carved = new ScenarioState
        {
            MapIndex = mapIndex,
            NationMapIndex = owner,
            NationId = nation?.Tag ?? "",
            Unclaimed = owner == 0 && !LakeRegions.IsWaterState(parent),
            Id = $"{parent.Id}_{mapIndex}",
            Name = name,
            Iso = $"{parent.Iso}-{mapIndex}",
            Kind = parent.Kind,
            // The original, not the immediate parent: a region cut, cut again and then
            // reunited should become one region rather than a chain of them.
            ParentMapIndex = parent.ParentMapIndex != 0 ? parent.ParentMapIndex : parent.MapIndex,
            Literacy = parent.Literacy,
            StandardOfLiving = parent.StandardOfLiving,
            Radicalism = parent.Radicalism,
            ResidentAwareness = parent.ResidentAwareness,
            Urbanization = parent.Urbanization
        };

        carved.CoreTags.AddRange(parent.CoreTags);
        if (parent.NonBuildingJobs >= 0)
        {
            carved.NonBuildingJobs = parent.NonBuildingJobs * share;
            parent.NonBuildingJobs -= carved.NonBuildingJobs;
        }
        carved.Claims.AddRange(parent.Claims);
        var population = (long)MathF.Round(parent.Population * share);
        carved.Population = Math.Min(population, parent.Population);
        parent.Population -= carved.Population;

        foreach (var group in parent.PopulationGroups)
        {
            var size = (long)MathF.Round(group.Size * share);
            if (size <= 0) continue;
            size = Math.Min(size, group.Size);
            group.Size -= size;
            carved.PopulationGroups.Add(new PopulationGroup
            {
                Class = group.Class,
                Sector = group.Sector,
                Culture = group.Culture,
                Religion = group.Religion,
                Profession = group.Profession,
                Size = size,
                Employment = group.Employment,
                Wealth = group.Wealth,
                StandardOfLiving = group.StandardOfLiving,
                Radicalism = group.Radicalism
            });
        }

        foreach (var building in parent.Buildings.Keys.ToArray())
        {
            var count = parent.Buildings[building];
            if (count <= 0) continue;
            var moved = (int)MathF.Round(count * share);
            if (moved <= 0) continue;
            moved = Math.Min(moved, count);
            parent.Buildings[building] = count - moved;
            carved.Buildings[building] = moved;
        }

        foreach (var deposit in parent.ResourceDeposits.Keys.ToArray())
        {
            var amount = parent.ResourceDeposits[deposit];
            if (amount <= 0f) continue;
            var moved = amount * share;
            parent.ResourceDeposits[deposit] = amount - moved;
            carved.ResourceDeposits[deposit] = moved;
        }

        var topology = campaign.ProvinceMap;
        foreach (var province in piece) topology.ProvinceToState[province] = (ushort)mapIndex;
        return carved;
    }

    /// <summary>
    /// A name a player can find on a map. The piece is named for where it lies against the
    /// region it came from, which is how these places tend to get named for real -
    /// Northern Ireland, West Bengal, South Ossetia.
    /// </summary>
    private static string Name(string parentName, (float Latitude, float Longitude) centre,
        (float Latitude, float Longitude) parentCentre, int ordinal)
    {
        var north = centre.Latitude - parentCentre.Latitude;
        var east = centre.Longitude - parentCentre.Longitude;
        var stem = StripDirection(parentName);

        if (MathF.Abs(north) < 0.05f && MathF.Abs(east) < 0.05f)
            return $"{stem} ({ordinal + 1})";

        var direction = MathF.Abs(north) >= MathF.Abs(east)
            ? north > 0f ? "Northern" : "Southern"
            : east > 0f ? "Eastern" : "Western";
        return $"{direction} {stem}";
    }

    /// <summary>
    /// Two pockets on the same side of a region get the same compass direction, and a
    /// player cannot act on two regions with one name. The first keeps the plain name and
    /// the rest are numbered, the way a map legend would do it.
    /// </summary>
    private static string Unique(Campaign campaign, string name)
    {
        if (campaign.States.All(state => !string.Equals(state.Name, name, StringComparison.Ordinal)))
            return name;
        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{name} {suffix}";
            if (campaign.States.All(state => !string.Equals(state.Name, candidate, StringComparison.Ordinal)))
                return candidate;
        }
        return name;
    }

    /// <summary>Keep a region split twice from becoming "Northern Northern Kyiv".</summary>
    private static string StripDirection(string name)
    {
        foreach (var prefix in new[] { "Northern ", "Southern ", "Eastern ", "Western " })
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return name[prefix.Length..];
        return name;
    }

    private static (float Latitude, float Longitude) Centre(ProvinceTopology topology, List<int> provinces)
    {
        if (provinces.Count == 0) return (0f, 0f);
        var latitude = 0f;
        var longitude = 0f;
        foreach (var province in provinces)
        {
            var centre = topology.CenterOf(province);
            latitude += centre.Latitude;
            longitude += centre.Longitude;
        }
        return (latitude / provinces.Count, longitude / provinces.Count);
    }

    private static int NextMapIndex(Campaign campaign)
    {
        var highest = FirstDynamicMapIndex - 1;
        foreach (var state in campaign.States)
            if (state.MapIndex > highest) highest = state.MapIndex;
        return highest + 1;
    }

    /// <summary>
    /// Divisions remember which region they are standing in. After a split, half of them
    /// are standing somewhere that has a different name than it did this morning.
    /// </summary>
    private static void RebindArmies(Campaign campaign, ProvinceTopology topology)
    {
        foreach (var army in campaign.Armies)
            if (army.ProvinceId > 0 && army.ProvinceId <= topology.ProvinceCount)
                army.StateMapIndex = topology.ProvinceToState[army.ProvinceId];
    }
}
