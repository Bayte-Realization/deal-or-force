using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// What a pocket costs, and what becomes of the men it takes.
///
/// Items 52 and 56 on the list: losses split into killed, wounded and captured, and closing a
/// pocket raising them. Each case below follows the men rather than the formation, because
/// the defect this replaces was exactly men leaving the world uncounted - a starving pocket's
/// daily loss and a collapsed pocket's survivors appeared in no war's losses at all.
/// </summary>
static class LossTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260907);
        var rus = c.NationByTag("RUS");
        var ukr = c.NationByTag("UKR");
        c.ConfigureSinglePlayer(rus.Id);
        c.AttachProvinceTopology(map);
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));
        c.Wars.RemoveAll(w => w != war);
        c.Battles.Clear();
        var army = c.Armies.First(a => a.NationId == rus.Id);
        c.Armies.Clear();
        c.Armies.Add(army);

        var ctrl = c.ProvinceController;
        var pocket = Enumerable.Range(1, map.ProvinceCount).First(p =>
            ctrl[p] == ukr.MapIndex && map.Neighbors[p].Length >= 3 &&
            map.Neighbors[p].All(n => ctrl[n] == ukr.MapIndex));
        army.ProvinceId = pocket;
        army.StateMapIndex = map.ProvinceToState[pocket];
        army.Divisions = 1; army.Strength = 1; army.Organisation = 1; army.Recovering = 0;
        army.Personnel = (int)MilitaryBootstrap.DivisionSize;
        army.CutOff = true;
        army.AssignedWarId = -1;
        var russiaAttacks = war.IsAttacking(rus.Id);
        float Killed() => russiaAttacks ? war.AttackerKilled : war.DefenderKilled;
        float Captured() => russiaAttacks ? war.AttackerCaptured : war.DefenderCaptured;
        float Total() => russiaAttacks ? war.AttackerLosses : war.DefenderLosses;

        // --- A: a day in a pocket at the hourly step costs a day, not twenty-four ----------
        c.Defines.HoursPerTick = 1;
        var totalBefore = Total();
        for (var hour = 0; hour < 24; hour++) Attrition.Tick(c);
        var lostInADay = 1f - army.Strength;
        Console.WriteLine($"  a day cut off at 1 h steps: strength 100 % -> {army.Strength:P1}, " +
                          $"{Killed():N0} killed and {Captured():N0} captured on the war's books");
        Check(MathF.Abs(lostInADay - c.Defines.EncircledStrengthLoss) < 0.002f,
            "a pocket loses a day's strength in a day at the hourly step (it lost a day's worth every hour)");
        var men = lostInADay * MilitaryBootstrap.DivisionSize;
        Check(MathF.Abs(Total() - totalBefore - men) < men * 0.05f + 1f,
            "the men a starving pocket loses are on the war's losses");
        Check(Captured() > 0f && Killed() > 0f && WarLosses.HeldFrom(war, rus.Id) > 0f,
            "a starving pocket's losses are split into killed and captured, and the captured are held");

        // --- B: the pocket collapses, and what is left of it surrenders --------------------
        army.Strength = c.Defines.DivisionBrokenStrength + 0.0005f;
        army.Recovering = 1500f;
        if (russiaAttacks) war.AttackerWounded += 1500f; else war.DefenderWounded += 1500f;
        var heldBefore = WarLosses.HeldFrom(war, rus.Id);
        for (var hour = 0; hour < 48 && c.Armies.Contains(army); hour++) Attrition.Tick(c);
        var surrendered = WarLosses.HeldFrom(war, rus.Id) - heldBefore;
        Console.WriteLine($"  the pocket collapsed: {surrendered:N0} more prisoners (its wounded among them)");
        Check(!c.Armies.Contains(army), "a pocket that starves down to nothing is removed");
        Check(surrendered >= 1500f, "a collapsed pocket's survivors and its wounded are taken prisoner, not lost from the count");

        // --- C: an exchange, one for one ---------------------------------------------------
        war.Prisoners[rus.Id] = 10_000f;
        war.Prisoners[ukr.Id] = 4_000f;
        var rusReserve = rus.ManpowerReserve;
        var ukrReserve = ukr.ManpowerReserve;
        var exchange = new ExchangePrisonersCommand { IssuerId = rus.Id, WarId = war.Id, CounterpartId = ukr.Id };
        exchange.Apply(c);
        Console.WriteLine($"  exchange: {war.LastNegotiation}");
        Check(MathF.Abs(WarLosses.HeldFrom(war, rus.Id) - 6_000f) < 1f && WarLosses.HeldFrom(war, ukr.Id) < 1f,
            "an exchange swaps as many as the smaller side holds, one for one");
        var home = 4_000f * c.Defines.PrisonersRepatriatedShare;
        Check(MathF.Abs(rus.ManpowerReserve - rusReserve - home) < 1f &&
              MathF.Abs(ukr.ManpowerReserve - ukrReserve - home) < 1f,
            "exchanged prisoners go back into their own country's reserve");
        war.Prisoners[ukr.Id] = 4_000f;
        Check(WarLosses.ExchangeRefusal(c, war, rus.Id, ukr.Id).Length > 0,
            "a second exchange in the same month is refused");

        // --- D: the peace sends everybody home ---------------------------------------------
        war.Ended = true;
        rusReserve = rus.ManpowerReserve;
        WarLosses.Repatriate(c);
        Check(war.Prisoners.Count == 0 &&
              MathF.Abs(rus.ManpowerReserve - rusReserve - 6_000f * c.Defines.PrisonersRepatriatedShare) < 1f,
            "when a war ends its prisoners go home, less those who died in captivity");
        // And nobody has to remember to ask: the day's pass finds an ended war with prisoners
        // still on it, whichever of the several ways of ending a war it was.
        war.Prisoners[ukr.Id] = 2_000f;
        c.Defines.HoursPerTick = 24;
        c.IsPaused = false;
        c.TickDay();
        Check(war.Prisoners.Count == 0, "the daily pass sends home the prisoners of a war that has ended");
    }
}
