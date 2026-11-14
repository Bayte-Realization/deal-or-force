using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The movement people index knows a culture or a faith by the string object that carries
/// its name, and hands a group that arrives with an equal string of its own the object the
/// name is already known by. That must be invisible: the same people counted, the same
/// population stamp, the same fingerprint, the same order of rows - whether every group
/// shares its names (a binary save's string table) or owns a copy of them (a text save),
/// and whether a region's rows are put in order by comparing names or by their ranks.
/// The counts are checked against the plain walk of a region's groups, which knows nothing
/// of the index.
/// </summary>
static class PeopleIndexNamesTests
{
    static int _passed;

    static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("people index names: " + note);
        _passed++;
        Console.WriteLine("PASS people index names: " + note);
    }

    // Names that differ only in case, names of one length with the same first, middle and last
    // letters (they start their search at the same slot), the empty name and no name at all.
    static readonly string[] Cultures =
    {
        "kurd", "Kurd", "KURD", "arab", "armenian", "assyrian", "azeri", "persian", "pashtun", "turkmen",
        "ab1cd", "ab2cd", "ab3cd", "a_c_e", "a-c-e", "zaza", "", null, "yazidi", "circassian",
    };

    static readonly string[] Faiths = { "sunni", "Sunni", "shia", "catholic", "orthodox", "yazidism", "", null };

    public static void Run()
    {
        var shared = World(own: false);
        var owned = World(own: true);
        var texts = Texts(owned);
        var before = CampaignFingerprint.Of(owned);
        Check(DistinctNames(owned) > DistinctTexts(owned) * 4, "the text-save world starts with a string of its own in every group");

        Drain(shared);
        Drain(owned);
        Check(MovementRules.PeopleIndexBuilds(shared) == 1 && MovementRules.PeopleIndexBuilds(owned) == 1, "each world builds once");
        Check(CampaignFingerprint.Of(owned) == before, "building the index leaves the fingerprint alone");
        Check(Texts(owned).SequenceEqual(texts), "every group reads the name it read before, and no name stays no name");
        Check(DistinctNames(owned) <= DistinctTexts(owned), "equal names are one string object after the build");
        Check(MovementRules.PopulationStamp(shared) == MovementRules.PopulationStamp(owned) && MovementRules.PopulationStamp(owned) != 0UL,
            "shared names and owned names give the same population stamp");
        CountsAgree(shared, "shared names");
        CountsAgree(owned, "owned names");
        RowsAgree(shared, "shared names, first build");
        RowsAgree(owned, "owned names, first build");

        // Everybody grows: every region is stale, the whole index is built again, and by now
        // every name has its rank, so the rows are ordered by rank.
        foreach (var world in new[] { shared, owned })
        {
            foreach (var state in world.States)
                foreach (var group in state.PopulationGroups) { group.Size += 7; state.Population += 7; }
            Advance(world);
            Drain(world);
        }
        Check(MovementRules.PeopleIndexBuilds(shared) == 2 && MovementRules.PeopleIndexBuilds(owned) == 2, "a week of births builds again");
        CountsAgree(shared, "shared names, after growth");
        CountsAgree(owned, "owned names, after growth");
        RowsAgree(shared, "shared names, ordered by rank");
        RowsAgree(owned, "owned names, ordered by rank");
        Check(MovementRules.PopulationStamp(shared) == MovementRules.PopulationStamp(owned), "the stamps still agree after growth");

        // A handful of regions change shape: a group emptied, a name nobody has met, a group
        // renamed to another spelling, a copy of a known name. Few enough to be patched in.
        foreach (var world in new[] { shared, owned })
        {
            var first = world.States[3];
            first.Population -= first.PopulationGroups[0].Size;
            first.PopulationGroups[0].Size = 0;
            var second = world.States[5];
            second.PopulationGroups.Add(new PopulationGroup { Culture = "newcomer", Religion = "sunni", Size = 900, Profession = "labourers" });
            second.PopulationGroups.Add(new PopulationGroup { Culture = "aaa_first", Religion = "new_faith", Size = 40, Profession = "labourers" });
            second.Population += 940;
            var third = world.States[8];
            third.PopulationGroups[1].Culture = "ARAB";
            third.PopulationGroups[2].Culture = new string("persian".AsSpan());
            third.PopulationGroups[3].Religion = null;
            third.PopulationGroups[3].Size += 1;
            third.Population += 1;
            Advance(world);
            Drain(world);
        }
        Check(MovementRules.PeopleIndexBuilds(shared) == 3 && MovementRules.PeopleIndexBuilds(owned) == 3, "three changed regions build again");
        CountsAgree(shared, "shared names, after the regions changed");
        CountsAgree(owned, "owned names, after the regions changed");
        RowsAgree(shared, "shared names, patched");
        RowsAgree(owned, "owned names, patched");
        Check(MovementRules.PopulationStamp(shared) == MovementRules.PopulationStamp(owned), "the stamps agree after the regions changed");

        // The same day asked again builds nothing and changes no stamp.
        var stamp = MovementRules.PopulationStamp(owned);
        Advance(owned);
        Drain(owned);
        Check(MovementRules.PeopleIndexBuilds(owned) == 3 && MovementRules.PopulationStamp(owned) == stamp, "a day in which nobody changed builds nothing");

        Console.WriteLine($"PEOPLE_INDEX_NAMES {_passed} PASS");
    }

    static void CountsAgree(Campaign campaign, string label)
    {
        foreach (var definition in Definitions())
            foreach (var state in campaign.States)
            {
                var own = MovementRules.PeopleOf(state, definition, out var faithful);
                var indexedOwn = MovementRules.PeopleOf(campaign, state, definition, out var indexedFaithful);
                if (own != indexedOwn || faithful != indexedFaithful)
                    throw new Exception($"people index names: {label}: {definition.Id} in {state.Id} is {indexedOwn}/{indexedFaithful} by the index and {own}/{faithful} by the walk");
            }
        Check(true, label + ": the index counts what a walk of the groups counts");
    }

    /// <summary>Every faith's rows, as the index keeps them, against the rows worked out here from the groups.</summary>
    static void RowsAgree(Campaign campaign, string label)
    {
        var cache = campaign.MovementPeople;
        var states = campaign.States.OrderBy(state => state.MapIndex).ToList();
        var faiths = new List<string>();
        foreach (var state in states)
            foreach (var group in state.PopulationGroups)
                if (group.Size > 0 && !faiths.Contains(group.Religion ?? "", StringComparer.OrdinalIgnoreCase)) faiths.Add(group.Religion ?? "");
        var rows = 0;
        foreach (var faith in faiths)
        {
            var expected = new List<(ScenarioState Region, string Culture, long Size)>();
            foreach (var state in states)
            {
                // One row a culture, whatever its spelling; the row carries the first spelling met.
                var here = new List<(string Culture, long Size)>();
                foreach (var group in state.PopulationGroups)
                {
                    if (group.Size <= 0 || !string.Equals(group.Religion ?? "", faith, StringComparison.OrdinalIgnoreCase)) continue;
                    var culture = group.Culture ?? "";
                    var at = here.FindIndex(row => string.Equals(row.Culture, culture, StringComparison.OrdinalIgnoreCase));
                    if (at < 0) here.Add((culture, group.Size));
                    else here[at] = (here[at].Culture, here[at].Size + group.Size);
                }
                foreach (var row in here.OrderBy(row => row.Culture, StringComparer.Ordinal)) expected.Add((state, row.Culture, row.Size));
            }
            var kept = cache.ByReligionBuckets.TryGetValue(faith, out var list) ? list : new List<ReligionCultureBucket>();
            if (kept.Count != expected.Count)
                throw new Exception($"people index names: {label}: faith '{faith}' keeps {kept.Count} rows, {expected.Count} expected");
            for (var i = 0; i < kept.Count; i++)
                if (!ReferenceEquals(kept[i].Region, expected[i].Region) || kept[i].Size != expected[i].Size ||
                    !string.Equals(kept[i].Culture, expected[i].Culture, StringComparison.Ordinal))
                    throw new Exception($"people index names: {label}: faith '{faith}' row {i} is {kept[i].Region.Id}/{kept[i].Culture}/{kept[i].Size}, expected {expected[i].Region.Id}/{expected[i].Culture}/{expected[i].Size}");
            rows += kept.Count;
        }
        Check(rows > 0, $"{label}: {rows} rows of {faiths.Count} faiths stand region by region, names in ordinal order");
    }

    static List<string> Texts(Campaign campaign)
    {
        var texts = new List<string>();
        foreach (var state in campaign.States)
            foreach (var group in state.PopulationGroups)
            {
                texts.Add(group.Culture is null ? "<none>" : "c:" + group.Culture);
                texts.Add(group.Religion is null ? "<none>" : "r:" + group.Religion);
            }
        return texts;
    }

    static int DistinctNames(Campaign campaign)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var state in campaign.States)
            foreach (var group in state.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                if (group.Culture is not null) seen.Add(group.Culture);
                if (group.Religion is not null) seen.Add(group.Religion);
            }
        return seen.Count;
    }

    static int DistinctTexts(Campaign campaign)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in campaign.States)
            foreach (var group in state.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                if (group.Culture is not null) seen.Add(group.Culture);
                if (group.Religion is not null) seen.Add(group.Religion);
            }
        return seen.Count;
    }

    static Campaign World(bool own)
    {
        var campaign = new Campaign(21);
        campaign.Nations.Add(new Nation { Id = 0, Tag = "AAA", Name = "One", MapIndex = 10, Population = 1 });
        var roll = 2463534242u;
        uint Next() { roll ^= roll << 13; roll ^= roll >> 17; roll ^= roll << 5; return roll; }
        // Enough regions that the build runs to several batches and a week of births is a full
        // rebuild; listed out of map order, which is the order the index keeps.
        for (var r = 0; r < 48; r++)
        {
            var state = new ScenarioState
            {
                Id = "region_" + r, Name = "Region " + r, MapIndex = 400 - r * 3, NationId = "AAA", NationMapIndex = 10
            };
            var groups = 6 + (int)(Next() % 30);
            for (var g = 0; g < groups; g++)
            {
                var culture = Cultures[Next() % (uint)Cultures.Length];
                var faith = Faiths[Next() % (uint)Faiths.Length];
                var size = 50 + (long)(Next() % 5000);
                state.PopulationGroups.Add(new PopulationGroup
                {
                    Culture = own && culture is not null ? new string(culture.AsSpan()) : culture,
                    Religion = own && faith is not null ? new string(faith.AsSpan()) : faith,
                    Size = size, Profession = "labourers", Class = SocialClass.Lower
                });
                state.Population += size;
            }
            campaign.States.Add(state);
        }
        return campaign;
    }

    static MovementDefinition[] Definitions()
    {
        var kurds = new MovementDefinition { Id = "names_kurds" };
        kurds.Peoples.Add("kurd");
        kurds.Peoples.Add("zaza");
        kurds.Faiths.Add("yazidism");
        var faithful = new MovementDefinition { Id = "names_faithful" };
        faithful.Peoples.Add("ARAB");
        faithful.Faiths.Add("sunni");
        faithful.Faiths.Add("shia");
        var newcomers = new MovementDefinition { Id = "names_newcomers" };
        newcomers.Peoples.Add("newcomer");
        newcomers.Peoples.Add("ab2cd");
        newcomers.Peoples.Add("a-c-e");
        newcomers.Faiths.Add("new_faith");
        newcomers.Faiths.Add("orthodox");
        return new[] { kurds, faithful, newcomers };
    }

    static void Drain(Campaign campaign)
    {
        foreach (var _ in MovementRules.EnsurePeopleIndexedStages(campaign, false)) { }
    }

    static void Advance(Campaign campaign)
    {
        var hours = campaign.ElapsedHours + 24;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, hours);
    }
}
