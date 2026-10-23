using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A country can be counted as dying without a world around it: the rule is a pure function of
/// a nation, the defines and a headcount, so the test holds one in its hands instead of
/// simulating a century of decline and hoping the counter moves.
/// </summary>
static class DemographicExtinctionTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("demographic extinction: " + label); Console.WriteLine("PASS demographic extinction: " + label); }

    public static void Run()
    {
        var defines = new Defines
        {
            DemographicExtinctionEnabled = true,
            DemographicExtinctionFloor = 20000f,
            DemographicExtinctionReviews = 3,
        };
        var nation = new Nation();

        Check(DemographicExtinction.Review(nation, defines, 50000) == 0,
            "a country above the floor is not collapsing");
        Check(!DemographicExtinction.IsCollapsing(nation),
            "a country above the floor carries no counted weeks");

        nation.CollapseReviews = DemographicExtinction.Review(nation, defines, 12000);
        Check(nation.CollapseReviews == 1 && DemographicExtinction.IsCollapsing(nation),
            "the first review under the floor starts the count");
        Check(!DemographicExtinction.IsFinal(nation, defines),
            "one week under the floor is not a final collapse");

        nation.CollapseReviews = DemographicExtinction.Review(nation, defines, 11000);
        nation.CollapseReviews = DemographicExtinction.Review(nation, defines, 9000);
        Check(DemographicExtinction.IsFinal(nation, defines),
            "the collapse is final once it has lasted the configured number of reviews");

        // Recovery wipes the record: a famine that ends is not a country that is dying.
        Check(DemographicExtinction.Review(nation, defines, 90000) == 0,
            "climbing back above the floor leaves no residue");

        // Off means off, whatever the headcount says - the default a save written before this
        // existed must keep.
        var off = new Defines();
        var doomed = new Nation { CollapseReviews = 99 };
        Check(!off.DemographicExtinctionEnabled, "the rule is off in a fresh campaign");
        Check(DemographicExtinction.Review(doomed, off, 1) == 0, "with the rule off nothing collapses");
        Check(!DemographicExtinction.IsFinal(doomed, off), "with the rule off nothing is called final");

        Land();
        Persistence();
    }

    /// <summary>
    /// The ground a dying country lets go of. The world here is two regions under one flag -
    /// a seat with people in it and a region with none - which is the smallest world the
    /// capital rule can be asked about: who leaves, who stays, and what true zero takes.
    /// </summary>
    static void Land()
    {
        var defines = new Defines
        {
            DemographicExtinctionEnabled = true,
            DemographicExtinctionFloor = 20000f,
            DemographicExtinctionReviews = 2,
        };

        var c = World(defines);
        var nation = c.Nations[0];
        nation.CollapseReviews = 1;
        DemographicExtinction.Tick(c);
        var capital = c.States.Find(s => s.MapIndex == 3001);
        var empty = c.States.Find(s => s.MapIndex == 3002);
        Check(nation.CollapseReviews == 2 && nation.CollapseAnnounced, "the final review announces the collapse");
        Check(RegionOwner(c, 3002) == 0, "the emptied region's provinces lose their owner");
        Check(empty.NationMapIndex == 0, "the emptied region's record follows its ground");
        Check(empty.Unclaimed, "the emptied region is drawn as unclaimed");
        Check(!DemographicExtinction.Abandon(c, empty), "a region already relinquished is not relinquished twice");
        Check(RegionOwner(c, 3001) == 501 && capital.NationMapIndex == 501,
            "the capital region stays the country's own while anybody is left");

        // A famine that ends keeps the country, but not the ground it already let go: what
        // was relinquished is unclaimed now, and becoming somebody again is conquest's job.
        nation.Population = 40000; capital.Population = 40000;
        SetDay(c, 18);
        DemographicExtinction.Tick(c);
        Check(nation.CollapseReviews == 0, "climbing back above the floor stops the count");
        Check(RegionOwner(c, 3002) == 0 && empty.Unclaimed, "recovered ground stays relinquished");
        Check(RegionOwner(c, 3001) == 501, "the capital never left");

        // True zero: the last inhabitant gone takes the seat with them, and the country
        // stands landless. Whatever army it still had is then the capital rule's business,
        // not this mechanic's - a country must not die twice, by two causes.
        var doomed = World(defines);
        var dying = doomed.Nations[0];
        dying.Population = 0;
        doomed.States[0].Population = 0;
        doomed.States[1].Population = 0;
        dying.CollapseReviews = 1;
        DemographicExtinction.Tick(doomed);
        Check(RegionOwner(doomed, 3001) == 0 && doomed.States[0].Unclaimed,
            "the last inhabitant gone, the capital region goes too");
        Check(!doomed.StatesOf(dying).Any(), "an extinguished country holds no ground at all");

        // A region emptied while its country lives is war's business, not demography's.
        var healthy = World(defines);
        healthy.Nations[0].Population = 40000; healthy.States[0].Population = 40000;
        DemographicExtinction.Tick(healthy);
        Check(RegionOwner(healthy, 3002) == 501 && !healthy.States[1].Unclaimed,
            "a healthy country keeps its empty region");

        // Off means off, even at true zero with a full count of weeks behind it.
        var off = World(new Defines());
        off.Nations[0].Population = 0; off.States[0].Population = 0;
        off.Nations[0].CollapseReviews = 99;
        DemographicExtinction.Tick(off);
        Check(RegionOwner(off, 3001) == 501 && RegionOwner(off, 3002) == 501,
            "with the rule off nothing is relinquished");
    }

    static void Persistence()
    {
        var defines = new Defines { DemographicExtinctionEnabled = true };
        var c = World(defines, seated: true);
        c.Nations[0].CollapseReviews = 4;
        c.Nations[0].CollapseAnnounced = true;
        var before = CampaignFingerprint.Of(c);
        var plain = World(defines, seated: true);
        Check(CampaignFingerprint.Of(plain) != before, "the counted weeks are authority, not presentation");

        var imported = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "extinction"), "extinction");
        Check(imported.Success, "the collapse count rides the existing real snapshot codec");
        Check(imported.Campaign.Nations[0].CollapseReviews == 4 && imported.Campaign.Nations[0].CollapseAnnounced,
            "the counted weeks and the announcement survive a save/load round trip");
        imported.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(imported.Campaign) == before,
            "the collapse count keeps the same fingerprint after save/load");
    }

    /// <summary>
    /// Two regions under one flag: provinces 1-15 are the seat, 16-30 the empty east.
    /// Unseated by default, because a country a person plays is skipped by the weekly pass -
    /// the persistence round trip is the one question that needs a player in the chair,
    /// since a snapshot without one has nobody to call its own.
    /// </summary>
    static Campaign World(Defines rules, bool seated = false)
    {
        var c = new Campaign(824, rules) { ScenarioId = "extinction_fixture" };
        c.AddNation(new Nation { Tag = "EX0", MapIndex = 501, Population = 12000, Gdp = 10, Treasury = 5, OutputScale = 1 });
        c.States.Add(new ScenarioState { Id = "extinction_capital", Name = "Extinction capital", MapIndex = 3001,
            NationMapIndex = 501, NationId = "EX0", Population = 12000 });
        c.States.Add(new ScenarioState { Id = "extinction_empty", Name = "Extinction empty", MapIndex = 3002,
            NationMapIndex = 501, NationId = "EX0", Population = 0 });
        // The ownership index is lazy and caches whatever States held when it was first
        // asked. A fixture that adds regions by hand must drop it afterwards, exactly as
        // the scenario loader and the save importer do.
        c.InvalidateStateIndex();
        SetDay(c, 11);
        // Build grows whole provinces out of seed cells - eighteen cells and up apiece - so
        // a raster has to be large enough for each region to hold several provinces, or the
        // world comes back with one province in it and nothing to ask about.
        var cells = Enumerable.Range(0, 800).Select(i => (ushort)(i < 400 ? 3001 : 3002)).ToArray();
        var owners = Enumerable.Range(0, 800).Select(_ => (ushort)501).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 40, 20, _ => false));
        // Attaching the map re-locates every capital from the country's capital coordinates
        // (LocateCapitals), which in a synthetic world that authored none means "wherever
        // (0,0) happens to be nearest". A fixture that wants a particular seat names it
        // after the attach, the way a scenario names it after the map.
        c.Nations[0].CapitalStateMapIndex = 3001;
        c.Nations[0].CapitalProvinceId = 1;
        if (seated) c.ConfigureSinglePlayer(0);
        return c;
    }

    /// <summary>
    /// Whose ground a region's provinces are, read the way the mechanic writes them: through
    /// the province-to-state ledger, never through a hardcoded province id, because which
    /// cell became which province is the raster walk's business, not the test's.
    /// </summary>
    static ushort RegionOwner(Campaign c, int stateMapIndex)
    {
        var topology = c.ProvinceMap;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToState[province] == (ushort)stateMapIndex)
                return topology.ProvinceToNation[province];
        return ushort.MaxValue;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
}
