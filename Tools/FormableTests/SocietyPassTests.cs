using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The society passes keep four tables so as not to ask the same question again: who a people's
/// living state is (DemographicCycle), which groups of a region an arrival could join
/// (InternalMigration), how far a region sits outside what its state declares (PoliticalCohorts),
/// and what a book of doctrine adds (TacticalDoctrines). Each is held here to the plain way of
/// asking, which knows nothing of the table: the answers must be the same to the bit, and a
/// table must stop answering the moment what it was read from is no longer what is there.
/// The weekly review of public images and the weekly settling of opinion are then run on the
/// thread pool and on one thread, and must leave the same world.
/// </summary>
static class SocietyPassTests
{
    static int _passed;

    static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("society passes: " + note);
        _passed++;
        Console.WriteLine("PASS society passes: " + note);
    }

    static void Advance(Campaign campaign) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, campaign.ElapsedHours + 24);

    static uint _roll = 2463534242u;
    static uint Next() { _roll ^= _roll << 13; _roll ^= _roll >> 17; _roll ^= _roll << 5; return _roll; }

    // Names that differ only in case, the empty name and no name at all.
    static readonly string[] Cultures = { "kurd", "Kurd", "arab", "armenian", "azeri", "persian", "", null, "turkmen" };
    static readonly string[] Faiths = { "sunni", "Sunni", "shia", "catholic", "", null };
    static readonly string[] Trades = { "labourers", "Labourers", "farmers", "clerks", "officers" };

    public static void Run()
    {
        ArrivalsJoinTheSameGroups();
        KinStatesAreTheSameStates();
        AlienationIsTheSameWalk();
        ABookIsSummedAgainWhenItChanges();
        ImagesAndOpinionsDoNotDependOnThreads();
        Console.WriteLine($"society passes: {_passed} checks");
    }

    // --- InternalMigration: an arrival across a border joins the group the list scan would join ---

    static void ArrivalsJoinTheSameGroups()
    {
        var trials = 0;
        var newGroups = 0;
        for (var trial = 0; trial < 300; trial++)
        {
            var fromLinked = Region(4 + (int)(Next() % 40), out var seed);
            var toLinked = Region(1 + (int)(Next() % 60), out var seedTo);
            var fromScanned = Copy(fromLinked);
            var toScanned = Copy(toLinked);
            // Small moves take the "join the largest of your own faith" road, large ones do not.
            var count = trial % 3 == 0 ? 1 + Next() % 40 : 1 + Next() % (uint)Math.Max(1, fromLinked.Population / 2);
            var before = toLinked.PopulationGroups.Count;
            var movedLinked = InternalMigration.Move(fromLinked, toLinked, count, withinCountry: false);
            var movedScanned = InternalMigration.Move(fromScanned, toScanned, count, withinCountry: true);
            if (movedLinked != movedScanned || !Same(fromLinked, fromScanned) || !Same(toLinked, toScanned))
                throw new Exception($"society passes: arrivals differ on trial {trial} (seeds {seed}/{seedTo}, count {count})");
            newGroups += toLinked.PopulationGroups.Count - before;
            trials++;
        }
        Check(trials == 300, "three hundred moves across a border end as the same moves inside a country, group for group");
        Check(newGroups > 100, "and they were not all easy: arrivals who found nobody of their kind founded a group");
    }

    static ScenarioState Region(int groups, out uint seed)
    {
        seed = _roll;
        var state = new ScenarioState { Id = "r" + seed, Name = "R", MapIndex = (int)(seed % 9000) + 1 };
        for (var g = 0; g < groups; g++)
        {
            var culture = Cultures[Next() % (uint)Cultures.Length];
            var faith = Faiths[Next() % (uint)Faiths.Length];
            state.PopulationGroups.Add(new PopulationGroup
            {
                // Half the groups own their names, half share them: equal names, different objects.
                Culture = culture is not null && Next() % 2 == 0 ? new string(culture.AsSpan()) : culture,
                Religion = faith is not null && Next() % 2 == 0 ? new string(faith.AsSpan()) : faith,
                Profession = Trades[Next() % (uint)Trades.Length],
                Class = (SocialClass)(Next() % 3),
                Size = Next() % 7 == 0 ? 0 : 1 + Next() % 4000,
            });
        }
        state.Population = Math.Max(2, state.PopulationGroups.Sum(group => group.Size));
        // Whatever the sum left over is matched by Move itself (EconomySystem.MatchGroups).
        return state;
    }

    static ScenarioState Copy(ScenarioState source)
    {
        var copy = new ScenarioState { Id = source.Id, Name = source.Name, MapIndex = source.MapIndex, Population = source.Population };
        foreach (var group in source.PopulationGroups)
            copy.PopulationGroups.Add(new PopulationGroup
            {
                Culture = group.Culture, Religion = group.Religion, Profession = group.Profession,
                Class = group.Class, Sector = group.Sector, Size = group.Size,
            });
        return copy;
    }

    static bool Same(ScenarioState left, ScenarioState right)
    {
        if (left.Population != right.Population || left.PopulationGroups.Count != right.PopulationGroups.Count) return false;
        for (var i = 0; i < left.PopulationGroups.Count; i++)
        {
            var a = left.PopulationGroups[i];
            var b = right.PopulationGroups[i];
            if (a.Size != b.Size || a.Class != b.Class || a.Sector != b.Sector ||
                !string.Equals(a.Culture, b.Culture, StringComparison.Ordinal) ||
                !string.Equals(a.Religion, b.Religion, StringComparison.Ordinal) ||
                !string.Equals(a.Profession, b.Profession, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // --- DemographicCycle: the day's table of kin states answers as the walk of every country ---

    static void KinStatesAreTheSameStates()
    {
        var c = new Campaign(31);
        // A tag nobody declared is for the people of its own name, lower case (DemographyCatalog).
        string[] tags = { "KRD", "krd", "ARB", "AZR", "PRS", "TKM", "ARB", "XXA", "XXB" };
        for (var id = 0; id < tags.Length; id++)
            c.Nations.Add(new Nation { Id = id, Tag = tags[id], Name = tags[id] + id, MapIndex = 10 + id, Population = 1000 });
        c.Nations[2].Population = 0;                              // the first Arab state has nobody left
        c.Fallen.Add(3);                                          // the Azeri state has fallen
        c.Nations[4].Movement = new MovementState();              // the Persian "state" is a movement
        string[] peoples = { "krd", "KRD", "Krd", "arb", "azr", "prs", "tkm", "nobody", "" };
        var asked = 0;
        var yes = 0;
        foreach (var owner in c.Nations)
            foreach (var people in peoples)
            {
                var walked = SecessionEvents.KinState(c, owner, people) is not null;
                if (DemographicCycle.HasLivingKinState(c, owner, people) != walked)
                    throw new Exception($"society passes: kin state of '{people}' seen from {owner.Name} differs from the walk");
                asked++;
                if (walked) yes++;
            }
        Check(asked == tags.Length * peoples.Length && yes > 0 && yes < asked,
            "every people has the kin state the walk of all countries finds, from every owner's side");
        Check(!DemographicCycle.HasLivingKinState(c, c.Nations[0], "azr") && !DemographicCycle.HasLivingKinState(c, c.Nations[0], "prs"),
            "a fallen state and a movement are nobody's kin state");
        Check(DemographicCycle.HasLivingKinState(c, c.Nations[0], "arb") && DemographicCycle.HasLivingKinState(c, c.Nations[0], "krd") &&
              DemographicCycle.HasLivingKinState(c, c.Nations[1], "krd"),
            "the second state of a people stands in when the first is empty, or is the one asking");

        // The table is the day's: tomorrow a country that has lost its people is no longer one.
        c.Nations[5].Population = 0;
        Check(DemographicCycle.HasLivingKinState(c, c.Nations[0], "tkm") == (SecessionEvents.KinState(c, c.Nations[0], "tkm") is not null),
            "what the day's world decides - people, land, a fall - is asked when the question is, not remembered");
    }

    // --- PoliticalCohorts: the table read side by side is the walk asked one region at a time ---

    static void AlienationIsTheSameWalk()
    {
        var c = World(nations: 40, regionsEach: 6);
        var table = PoliticalCohorts.ReadAlienation(c);
        Check(table is not null, "the table is read when the mechanic is on");
        var regions = 0;
        var alienated = 0;
        for (var place = 0; place < c.Nations.Count; place++)
        {
            var nation = c.Nations[place];
            var states = c.StatesOf(nation);
            if (!table.Holds(place, nation, states)) throw new Exception("society passes: a fresh table refused a row");
            for (var i = 0; i < states.Length; i++)
            {
                var walked = StateIdentity.RegionAlienation(c, nation, states[i]);
                if (BitConverter.SingleToInt32Bits(table.Values[table.RowOf(place) + i]) != BitConverter.SingleToInt32Bits(walked))
                    throw new Exception($"society passes: alienation of {states[i].Id} differs from the walk");
                regions++;
                if (walked > 0f) alienated++;
            }
        }
        Check(regions == 240 && alienated > 20 && alienated < regions, "every region's number is the walk's, to the bit");

        var was = DeterministicParallel.ForceSerial;
        DeterministicParallel.ForceSerial = true;
        float[] alone;
        try { alone = PoliticalCohorts.ReadAlienation(c).Values.Take(regions).ToArray(); }
        finally { DeterministicParallel.ForceSerial = was; }
        var together = PoliticalCohorts.ReadAlienation(c).Values.Take(regions).ToArray();
        Check(alone.Select(BitConverter.SingleToInt32Bits).SequenceEqual(together.Select(BitConverter.SingleToInt32Bits)),
            "read on one thread or on several, it is the same table");

        // A country that has declared a people: its declaration is a string of its own.
        var first = c.Nations[1];
        var other = c.Nations[2];
        Check(first.DeclaredCulture.Length > 0 && table.Holds(1, first, c.StatesOf(first)) &&
              !table.Holds(2, first, c.StatesOf(first)) && !table.Holds(-1, first, c.StatesOf(first)) &&
              !table.Holds(c.Nations.Count, first, c.StatesOf(first)),
            "a row answers only for the country it was read for");
        first.DeclaredCulture = new string(first.DeclaredCulture.AsSpan());
        Check(!table.Holds(1, first, c.StatesOf(first)), "a new declaration, even of the same words, is asked afresh");
        Check(table.Holds(2, other, c.StatesOf(other)), "and the other rows still stand");
        c.InvalidateStateIndex();
        Check(!table.Holds(2, other, c.StatesOf(other)), "a new list of regions is asked afresh");
        table = PoliticalCohorts.ReadAlienation(c);
        Check(table.Holds(2, other, c.StatesOf(other)) && table.Holds(1, first, c.StatesOf(first)), "reading again answers again");
        table.Spoil();
        Check(!table.Holds(2, other, c.StatesOf(other)), "a table put aside after a coup answers nothing");

        // And the review that takes its numbers from the table leaves the regions it would
        // have left by asking: two equal worlds, one reviewed through the daily pass (the
        // table), one country by country through the public door (the walk).
        var tabled = World(nations: 40, regionsEach: 6);
        var walkedWorld = World(nations: 40, regionsEach: 6);
        for (var day = 0; day < 3; day++)
        {
            Advance(tabled);
            Advance(walkedWorld);
            var known = PoliticalCohorts.ReadAlienation(tabled);
            for (var place = 0; place < tabled.Nations.Count; place++)
            {
                PoliticalCohorts.Review(tabled, tabled.Nations[place], 1, known, place);
                PoliticalCohorts.Review(walkedWorld, walkedWorld.Nations[place], 1);
            }
        }
        var agree = true;
        for (var i = 0; i < tabled.States.Count && agree; i++)
        {
            var a = tabled.States[i];
            var b = walkedWorld.States[i];
            agree = BitConverter.SingleToInt32Bits(a.GovernmentApproval) == BitConverter.SingleToInt32Bits(b.GovernmentApproval) &&
                    BitConverter.SingleToInt32Bits(a.LawApproval) == BitConverter.SingleToInt32Bits(b.LawApproval) &&
                    BitConverter.SingleToInt32Bits(a.PoliticalDivergence) == BitConverter.SingleToInt32Bits(b.PoliticalDivergence);
        }
        Check(tabled.States.Any(state => state.PoliticalDivergence != 0.08f && state.PoliticalDivergence != 0.04f),
            "three days of review moved the regions off where they were seeded");
        Check(agree, "and through the table they are left exactly where the walk leaves them");
    }

    /// <summary>Countries that have declared a faith and a people, over regions that mostly are something else.</summary>
    static Campaign World(int nations, int regionsEach)
    {
        _roll = 88172645u;
        var c = new Campaign(77);
        for (var id = 0; id < nations; id++)
        {
            c.Nations.Add(new Nation
            {
                Id = id, Tag = "N" + id.ToString("00"), Name = "Nation " + id, MapIndex = 100 + id, Population = 1,
                Stability = 0.3f + id % 5 * 0.1f, StandardOfLiving = 0.4f + id % 3 * 0.1f, ShortageIndex = id % 4 * 0.1f,
                CapitalStateMapIndex = 1000 + id * 10,
                DeclaredReligion = id % 7 == 0 ? "" : Faiths[id % 4] ?? "",
                DeclaredCulture = id % 5 == 0 ? "" : Cultures[id % 6] ?? "",
            });
            long people = 0;
            for (var r = 0; r < regionsEach; r++)
            {
                var state = new ScenarioState
                {
                    Id = $"s{id}_{r}", Name = $"S {id} {r}", MapIndex = 1000 + id * 10 + r, NationId = "N" + id.ToString("00"),
                    NationMapIndex = 100 + id, Stability = 0.5f, StandardOfLiving = 0.3f + r * 0.08f, Radicalism = r * 0.07f,
                    Literacy = 0.6f, Urbanization = 0.5f,
                };
                var groups = 3 + (int)(Next() % 25);
                for (var g = 0; g < groups; g++)
                {
                    var size = 100 + (long)(Next() % 9000);
                    state.PopulationGroups.Add(new PopulationGroup
                    {
                        Culture = Cultures[Next() % (uint)Cultures.Length], Religion = Faiths[Next() % (uint)Faiths.Length],
                        Profession = Trades[Next() % (uint)Trades.Length], Class = SocialClass.Lower, Size = size,
                    });
                    state.Population += size;
                }
                people += state.Population;
                c.States.Add(state);
            }
            c.Nations[id].Population = people;
        }
        c.InvalidateStateIndex();
        return c;
    }

    // --- TacticalDoctrines: a book's sum is kept while the book and the learned list are the same ---

    static void ABookIsSummedAgainWhenItChanges()
    {
        var book = TacticalDoctrineCatalog.All
            .Where(branch => branch.Nodes.Count(node => node.Modifiers.Count > 0) >= 3)
            .OrderBy(branch => branch.Id, StringComparer.Ordinal).FirstOrDefault();
        Check(book is not null, "the catalogue has a book with three articles that add something");
        var articles = book.Nodes.Where(node => node.Modifiers.Count > 0).Take(3).ToArray();

        float Sum(Nation nation, string key) { NationalModifiers.Invalidate(nation); return NationalModifiers.Of(nation, key); }
        bool SameAsFresh(Nation kept)
        {
            // A country that has never been asked, holding the same book and the same articles.
            var fresh = Holder(book, kept.TacticalDoctrine.Track(book.Slot).Known.ToArray());
            foreach (var key in NationalModifiers.Keys)
                if (BitConverter.SingleToInt32Bits(Sum(kept, key.Id)) != BitConverter.SingleToInt32Bits(Sum(fresh, key.Id))) return false;
            return true;
        }
        float Carried(Nation nation) => articles.SelectMany(node => node.Modifiers.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Sum(key => MathF.Abs(Sum(nation, key)));

        var country = Holder(book, articles[0].Id);
        var known = country.TacticalDoctrine.Track(book.Slot).Known;
        var one = Carried(country);
        Check(one > 0f && SameAsFresh(country), "one article adds what it says");
        Check(SameAsFresh(country) && Carried(country) == one, "asked again, the kept sum is the same sum");
        known.Add(articles[1].Id);
        Check(SameAsFresh(country), "an article learned is in the sum the next time it is asked for");
        known[0] = articles[2].Id;
        Check(SameAsFresh(country), "an article swapped for another, the list no longer or shorter, is seen");
        known.Clear();
        known.Add(articles[0].Id);
        known.Add(articles[1].Id);
        Check(SameAsFresh(country), "a list emptied and filled again to the same length is seen");
        country.TacticalDoctrine.Track(book.Slot).Known = new List<string> { articles[2].Id };
        Check(SameAsFresh(country), "a new list is seen");
        country.TacticalDoctrine.Track(book.Slot).Known.Clear();
        Check(SameAsFresh(country) && Carried(country) == Carried(Holder(book)), "a book with nothing learned adds nothing");
    }

    static Nation Holder(DoctrineBranch book, params string[] known)
    {
        var nation = new Nation { Id = 0, Tag = "DOC", Name = "Doctrine", MapIndex = 1, Population = 1000 };
        var track = nation.TacticalDoctrine.Track(book.Slot);
        track.Branch = book.Id;
        track.Known = new List<string>(known);
        nation.TacticalDoctrine.ShelvesOpened = true;
        return nation;
    }

    // --- SocialMemory and OpinionSystem: the pool and one thread leave the same world ---

    static void ImagesAndOpinionsDoNotDependOnThreads()
    {
        var together = Neighbours(forceSerial: false, out var imagesTogether, out var parallelRuns);
        var alone = Neighbours(forceSerial: true, out var imagesAlone, out _);
        Check(DeterministicParallel.Workers <= 1 || parallelRuns > 0, "the reviews were in fact handed to the pool");
        Check(imagesTogether > 500 && imagesTogether == imagesAlone, "both worlds came to hold the same number of images");
        Check(together == alone, "four weeks of images and opinions leave the same fingerprint on one thread and on several");
    }

    static ulong Neighbours(bool forceSerial, out int images, out int parallelRuns)
    {
        var was = DeterministicParallel.ForceSerial;
        DeterministicParallel.ForceSerial = forceSerial;
        try
        {
            _roll = 362436069u;
            var c = new Campaign(5);
            const int nations = 60;
            for (var id = 0; id < nations; id++)
                c.Nations.Add(new Nation
                {
                    // Two pairs of countries stand under one tag, so a third holds one image of both.
                    Id = id, Tag = id is 7 or 8 ? "TWN" : id is 20 or 41 ? "DBL" : "C" + id.ToString("00"),
                    Name = "Country " + id, MapIndex = 10 + id, Population = 1_000_000 + id,
                    Stability = 0.3f + id % 6 * 0.1f, StandardOfLiving = 0.2f + id % 7 * 0.1f, Notoriety = id % 9 * 6f,
                });
            for (var a = 0; a < nations; a++)
                for (var b = a + 1; b < nations; b++)
                {
                    if (Next() % 4 == 0) continue;
                    var relation = CampaignDiplomacy.Get(c, a, b);
                    relation.Stance = (Next() % 9) switch { 0 => DiplomaticStance.Allied, 1 => DiplomaticStance.AtWar, _ => DiplomaticStance.Neutral };
                    relation.TradeAgreement = Next() % 3 == 0;
                    foreach (var reason in OpinionCatalog.All.Take(1 + (int)(Next() % 4)))
                        OpinionSystem.Remember(c, relation, reason.Id, 1f, settle: false);
                }
            DeterministicParallel.ResetDiagnostics();
            for (var day = 0; day < 28; day++)
            {
                Advance(c);
                SocialMemory.Tick(c);
                OpinionSystem.Tick(c);
            }
            parallelRuns = DeterministicParallel.ParallelRuns;
            images = c.Nations.Sum(nation => nation.PublicImages.Count);
            return CampaignFingerprint.Of(c);
        }
        finally { DeterministicParallel.ForceSerial = was; }
    }
}
