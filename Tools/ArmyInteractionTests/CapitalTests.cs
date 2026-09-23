using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Where a government sits (items 64 and 40): moved by choice, moved by necessity, and gone
/// with the country.
/// </summary>
static class CapitalTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260908);
        c.AttachProvinceTopology(map);
        var rus = c.NationByTag("RUS");
        var kaz = c.NationByTag("KAZ");
        c.ConfigureSinglePlayer(rus.Id);

        // --- A: the first day moves nobody -------------------------------------------------
        // Every capital was placed by the map a moment ago; if the daily check disagreed with
        // that about anybody, it would rename and move them on the first morning.
        var before = c.Nations.ToDictionary(n => n.Id, n => (n.CapitalStateMapIndex, n.CapitalName));
        CapitalCore.Tick(c);
        var moved = c.Nations.Where(n => before[n.Id] != (n.CapitalStateMapIndex, n.CapitalName)).ToList();
        foreach (var nation in moved.Take(6))
            Console.WriteLine($"    moved on day one: {nation.Tag} {before[nation.Id].CapitalName} -> {nation.CapitalName}");
        Check(moved.Count == 0, "the daily check moves no capital the map has just placed");

        // --- B: by choice --------------------------------------------------------------------
        var urals = c.States.First(s => s.Id == "ru_sve");
        var moscow = rus.CapitalStateMapIndex;
        rus.Treasury = 1_000f;
        var treasury = rus.Treasury;
        var governance = rus.StabilityGovernance;
        var cost = CapitalCore.Cost(c, rus);
        var order = new MoveCapitalCommand { IssuerId = rus.Id, StateMapIndex = urals.MapIndex };
        order.Apply(c);
        Console.WriteLine($"  RUS moved to {rus.CapitalName} ({rus.CapitalLatitude:0.0}, {rus.CapitalLongitude:0.0}) for {cost:0.00}");
        Check(order.Moved && rus.CapitalStateMapIndex == urals.MapIndex, "a government can move to a region of its own");
        Check(rus.CapitalProvinceId > 0 && map.ProvinceToState[rus.CapitalProvinceId] == urals.MapIndex,
            "the new capital stands inside the region it moved to");
        Check(MathF.Abs(treasury - cost - rus.Treasury) < 0.001f && rus.StabilityGovernance < governance,
            "moving costs the treasury and a little of the country's stability");
        Check(CapitalCore.Refusal(c, rus, c.States.First(s => s.MapIndex == moscow)).Length > 0,
            "it cannot move again within the year");

        // --- C: where it may not go ------------------------------------------------------------
        rus.CapitalMovedDay = -100_000;
        Check(CapitalCore.Refusal(c, rus, c.StatesOf(kaz).First()).Length > 0, "a capital cannot move to somebody else's region");
        Check(CapitalCore.Refusal(c, rus, urals).Length > 0, "nor to where it already is");

        // --- D: by necessity ---------------------------------------------------------------------
        // The Urals are ceded. The government does not go on sitting in Yekaterinburg. A cession
        // moves both the region and the owner of every province in it, so the test does both.
        void Cede(ScenarioState state, Nation to)
        {
            state.NationMapIndex = to.MapIndex;
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToState[province] == state.MapIndex) map.ProvinceToNation[province] = (ushort)to.MapIndex;
            c.InvalidateStateIndex();
        }
        Cede(urals, kaz);
        CapitalCore.Tick(c);
        var largest = c.StatesOf(rus).OrderByDescending(s => s.Population).First();
        Console.WriteLine($"  the Urals ceded: RUS now governed from {rus.CapitalName}");
        Check(rus.CapitalStateMapIndex == largest.MapIndex && CapitalCore.HasCapital(c, rus),
            "a country that loses its capital region moves to the most populous one it has left");
        Cede(urals, rus);

        // --- E: with the country ---------------------------------------------------------------
        var gone = c.NationByTag("EST");
        var standing = c.Armies.Count(a => a.NationId == gone.Id);
        foreach (var state in c.StatesOf(gone).ToList()) Cede(state, rus);
        CapitalCore.Tick(c);
        Console.WriteLine($"  Estonia annexed whole: {standing} formation(s) in the field before, {c.Armies.Count(a => a.NationId == gone.Id)} after");
        Check(gone.CapitalStateMapIndex == 0 && !CapitalCore.HasCapital(c, gone),
            "a country with no regions left has no capital");
        Check(standing > 0 && c.Armies.All(a => a.NationId != gone.Id),
            "and no army: what it had in the field surrenders with it");
    }
}
