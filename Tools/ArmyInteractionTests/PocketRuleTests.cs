using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// The pocket and partisan rules of 16 November 2026, pinned to the sentences they were written for.
///
/// The owner: "Divisions in pockets should weaken, but not die by themselves - they should die
/// exactly when their morale is driven down to 0%. And they should still have some supply, which
/// runs out." And: "Partisans work strangely - they die right where they rose."
///
/// --pocket-rules-only runs both halves; --pocket-hunger-only and --partisans-only run one.
///
/// The world is eight provinces in a strip, each a region of a million people, and four
/// countries: a besieger, a state, a rising against the besieger, and a bystander. Days are
/// driven pass by pass - supply, rest, battle, wasting, surrender, desertion, the hidden - so
/// each check says which rule it is about and nothing of the economy runs underneath it.
/// </summary>
static class PocketRuleTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL pocket rules: " + label);
        Console.WriteLine("PASS pocket rules: " + label);
    }

    const ushort Besieger = 100, State = 101, Rising = 102;
    const int WarId = 991;

    public static void Run()
    {
        RunHunger();
        RunPartisans();
    }

    public static void RunHunger()
    {
        HungerStopsAtTheFloor();
        UntouchedPocketNeverSurrenders();
        PocketFallsOnlyToAnAssault();
        PocketRecoversToItsCeiling();
        StoresAreFiniteAndShown();
        PeaceStopsTheHungerClock();
        ForgottenPocketDesertsOverYears();
    }

    public static void RunPartisans()
    {
        RisingOnItsOwnLandIsNotCutOff();
        LandFeedsOnlySoMany();
        MilitiaRefillsFromItsRegion();
        BrokenAmongItsOwnSlipsAway();
        BeatenWithNoRetreatGoesToGround();
        TheWeakLoseTheirWarInTheField();
        TheWeakFightOnWithoutGround();
        UndergroundMeltsAway();
        HiddenFightersKeepTheWarAlive();
        SaveLoadAndOldSaves();
        TwoWorldsFromOneSeedAgree();
    }

    // ================================================================== the world

    /// <summary>
    /// Provinces 1 to 8 top to bottom, region 1000 + province. <paramref name="owners"/> is who
    /// owns (and at first controls) each, index 1 to 8. Each country's capital is the first
    /// province it owns, unless it is given one.
    /// </summary>
    static Campaign World(ushort[] owners, int seed = 4116)
    {
        const int width = 32, height = 256;
        var states = new ushort[width * height];
        var raster = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = y * width + x;
            var p = y / 32 + 1;
            states[at] = (ushort)(1000 + p);
            raster[at] = owners[p];
            rgb[at * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, raster, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 8).ToDictionary(p => p, p => p));
        var c = new Campaign(seed) { ScenarioId = "pocket_rules" };
        c.Defines.HoursPerTick = 24;
        c.Defines.RetreatSearchDepth = 1;
        c.Defines.FrontStaffCallsUpIdle = false;
        string[] names = { "Besieger", "Pocket state", "Rising", "Bystander" };
        for (var n = 0; n < 4; n++)
        {
            var home = Array.IndexOf(owners, (ushort)(100 + n), 1);
            c.Nations.Add(new Nation
            {
                Id = n, MapIndex = 100 + n, Tag = "PR" + n, Name = names[n], Population = 4_000_000, Gdp = 100,
                Treasury = 20, ManpowerReserve = 0, WarSupport = 0.6f, Stability = 0.6f, MilitarySpending = .06f,
                CapitalProvinceId = Math.Max(0, home), CapitalStateMapIndex = home > 0 ? 1000 + home : 0
            });
        }
        for (var p = 1; p <= 8; p++)
            c.States.Add(new ScenarioState
            {
                Id = "rule_" + p, MapIndex = 1000 + p, NationMapIndex = owners[p], NationId = "PR" + (owners[p] - 100),
                Name = "Region " + p, Population = 1_000_000, Stability = 1f, Radicalism = 0f
            });
        c.AttachProvinceTopology(map);
        c.Armies.Clear(); c.Wars.Clear(); c.Battles.Clear();
        c.SetPlayer(3);
        return c;
    }

    static StrategicWar War(Campaign c, int attacker, int defender)
    {
        var war = new StrategicWar { Id = WarId, AttackerId = attacker, DefenderId = defender, StartedDay = 0, LastFrontDay = 0 };
        c.Wars.Add(war);
        c.MarkWarsChanged();
        return war;
    }

    static Army Unit(Campaign c, int nation, int province, float divisions = 1f)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var army = new Army
        {
            Id = (c.Armies.Count == 0 ? 0 : c.Armies.Max(a => a.Id)) + 1, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], AssignedWarId = WarId, OrderProvinceId = -1,
            OrderKind = ArmyOrder.Manual, DirectOrder = true, Divisions = divisions, Strength = 1f, Organisation = 1f,
            SupplyLevel = 1f, Cohesion = .8f, Personnel = (int)(15_000 * divisions), MaxPersonnel = (int)(15_000 * divisions),
            Name = "Formation " + nation + "/" + province, Latitude = centre.Latitude, Longitude = centre.Longitude
        };
        c.Armies.Add(army);
        return army;
    }

    static LandBattle Battle(Campaign c, Army attacker, Army defender)
    {
        var battle = new LandBattle
        {
            Id = (c.Battles.Count == 0 ? 0 : c.Battles.Max(b => b.Id)) + 1, WarId = WarId,
            ProvinceId = defender.ProvinceId, AttackerId = attacker.NationId, DefenderId = defender.NationId
        };
        battle.Attackers.Add(attacker.Id); battle.Defenders.Add(defender.Id);
        attacker.BattleId = defender.BattleId = battle.Id;
        c.Battles.Add(battle);
        return battle;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(c, day * 24L);

    /// <summary>
    /// One day of the rules a pocket lives under, in the order the campaign runs them: the
    /// supply trace and the hunger clock, rest and its ceiling, battles, wasting and burial,
    /// the surrender pass, and - weekly - desertion and the hidden.
    /// </summary>
    static void Day(Campaign c, int day, bool battles = false, bool desertion = true)
    {
        SetDay(c, day);
        LogisticsSystem.Tick(c);
        MilitaryOperations.Tick(c);
        if (battles) Warfare.Tick(c);
        Attrition.Tick(c);
        StrandedGarrisons.Tick(c);
        if (desertion) Desertion.Tick(c);
        Partisans.Tick(c);
    }

    /// <summary>
    /// A formation of the state in an exclave of its own (province 4) with the besieger's ground
    /// on both sides of it and its capital beyond: cut off, at war, nobody near it.
    /// </summary>
    static (Campaign World, Army Pocket, StrategicWar War) Pocket(int seed = 4116)
    {
        var c = World(new ushort[] { 0, State, State, Besieger, State, Besieger, Besieger, Besieger, Besieger }, seed);
        var war = War(c, 0, 1);
        return (c, Unit(c, 1, 4), war);
    }

    /// <summary>Run days until the stores are gone, and say which day that was.</summary>
    static int UntilHungry(Campaign c, Army pocket, int day, bool desertion)
    {
        while (pocket.PocketStores != 0f && day < 200) Day(c, day++, desertion: desertion);
        Check(pocket.PocketStores == 0f, "the stores of a pocket nobody supplies run out (on day " + day + ")");
        return day;
    }

    // ================================================================== A: hunger

    static void HungerStopsAtTheFloor()
    {
        var (c, pocket, _) = Pocket();
        var d = c.Defines;
        Day(c, 0, desertion: false);
        Check(pocket.CutOff && pocket.PocketStores > 0f, "a formation in an exclave of a country at war is in a pocket, with stores counted");
        // Hunger alone: the clock, the ceiling and the wasting, without the men who walk away.
        var day = UntilHungry(c, pocket, 1, desertion: false);
        for (var hungry = 0; hungry < 120; hungry++) Day(c, day++, desertion: false);
        Console.WriteLine($"  120 hungry days, untouched: strength {pocket.Strength:0.000}, morale {pocket.Organisation:0.000}, " +
                          $"supply {pocket.SupplyLevel:0.000}, {pocket.Personnel:N0} men, hungry {pocket.DaysCutOff} days");
        Check(c.Armies.Contains(pocket), "after 120 days with no stores and no attack the formation is still there");
        Check(MathF.Abs(pocket.Strength - d.PocketHungerStrengthFloor) < 0.001f,
            "hunger takes its strength down to the floor and stops there");
        Check(MathF.Abs(pocket.Organisation - d.PocketHungerOrganisationFloor) < 0.001f,
            "its morale settles on the hunger floor, far above the rout threshold");
        Check(MathF.Abs(pocket.SupplyLevel - d.PocketHungerSupplyFloor) < 0.001f &&
              pocket.SupplyLevel < AssaultReadiness.MinimumSupply,
            "its supply settles on a trickle: enough to defend with, too little to attack");
        Check(pocket.Power > 0f && pocket.Power < 0.05f * 1f,
            "what is left is a few hundredths of the division it was - weak, not gone");
        var before = (pocket.Strength, pocket.Organisation, pocket.SupplyLevel);
        for (var more = 0; more < 60; more++) Day(c, day++, desertion: false);
        Check(before == (pocket.Strength, pocket.Organisation, pocket.SupplyLevel),
            "and sixty more days of hunger change nothing: the floor is a floor");
    }

    static void UntouchedPocketNeverSurrenders()
    {
        var (c, pocket, war) = Pocket();
        var surrendered = WarLosses.SurrenderedFormations;
        var starved = Attrition.StarvedInPockets;
        string said = null;
        Attrition.Witness = (_, army, reason, _) => { if (ReferenceEquals(army, pocket)) said = reason; };
        try
        {
            // Every pass a pocket lives under, the surrender roll and desertion included.
            for (var day = 0; day < 200; day++) Day(c, day);
        }
        finally { Attrition.Witness = null; }
        Console.WriteLine($"  200 days untouched, every pass: strength {pocket.Strength:0.000}, morale {pocket.Organisation:0.000}, hungry {pocket.DaysCutOff} days");
        Check(c.Armies.Contains(pocket) && said is null, "200 days cut off and never attacked: the formation was not removed for any reason");
        Check(WarLosses.SurrenderedFormations == surrendered && Attrition.StarvedInPockets == starved &&
              c.Nations[1].FormationsLost == 0,
            "nobody surrendered, nobody starved to nothing, and the country lost no formation");
        // Under the hunger floor by now: the floor is hunger's, and a few men have walked away
        // each week since (PocketHungerDesertionWeekly). Still three times a buried formation.
        Check(pocket.Organisation >= c.Defines.PocketHungerOrganisationFloor - 0.001f &&
              pocket.Strength > 3f * c.Defines.DivisionBrokenStrength &&
              pocket.Strength < c.Defines.PocketHungerStrengthFloor,
            "it is weak, a little weaker each week by desertion, and a long way from gone");
        Check((war.IsAttacking(1) ? war.AttackerLosses : war.DefenderLosses) > 0f,
            "the men hunger cost it are on the war's books");
    }

    static void PocketFallsOnlyToAnAssault()
    {
        // A starved pocket, then somebody comes for it.
        var (c, pocket, war) = Pocket();
        var day = UntilHungry(c, pocket, 0, desertion: false);
        for (var hungry = 0; hungry < 60; hungry++) Day(c, day++, desertion: false);
        Check(c.Armies.Contains(pocket) && pocket.Strength <= c.Defines.PocketHungerStrengthFloor + 0.001f,
            "sixty hungry days leave a pocket on its floor and on the map");
        var assault = Unit(c, 0, 3, 3f);
        Battle(c, assault, pocket);
        string said = null;
        var moraleAtTheEnd = 1f;
        Attrition.Witness = (_, army, reason, _) =>
        {
            if (!ReferenceEquals(army, pocket)) return;
            said = reason; moraleAtTheEnd = army.Organisation;
        };
        var captured = war.DefenderCaptured;
        var battleDays = 0;
        try
        {
            while (c.Armies.Contains(pocket) && battleDays < 20)
            {
                var alive = pocket.Organisation;
                Day(c, day++, battles: true, desertion: false);
                battleDays++;
                if (c.Armies.Contains(pocket))
                    Check(alive > 0.005f, "while it has any morale left the pocket is still on the map (battle day " + battleDays + ")");
            }
        }
        finally { Attrition.Witness = null; }
        Console.WriteLine($"  assault on a starved pocket: gone after {battleDays} battle day(s), \"{said}\", morale {moraleAtTheEnd:0.000} at the end");
        // The design reckoned one to three days; dug in for two months on its own ground it
        // takes a three-division assault about a week. Days, either way, where hunger took none.
        Check(!c.Armies.Contains(pocket) && battleDays <= 10, "a starved pocket falls to a real assault in days");
        Check(said is "destroyed: zero morale in pocket" or "destroyed: cut off with no line of retreat",
            "what destroyed it was the battle - beaten with no road out - and not hunger");
        Check(moraleAtTheEnd <= c.Defines.BattleBreakOrganisation + 0.001f,
            "and it was destroyed with its morale spent by the attack");
        Check(war.DefenderCaptured > captured && c.ProvinceController[4] == Besieger,
            "its survivors are prisoners and the ground is the attacker's");

        // The same with its strength whole: a pocket fights at any morale above nothing, hungry
        // or fed, and dies on the day an attack takes the last of it.
        foreach (var hungry in new[] { false, true })
        {
            (c, pocket, war) = Pocket();
            Day(c, 0, desertion: false);
            if (hungry) { pocket.PocketStores = 0f; pocket.DaysCutOff = 30; pocket.SupplyLevel = c.Defines.PocketHungerSupplyFloor; }
            assault = Unit(c, 0, 3);
            var battle = Battle(c, assault, pocket);
            pocket.Organisation = .08f;
            SetDay(c, 1); Warfare.Tick(c);
            Check(!battle.Ended && c.Armies.Contains(pocket) && c.ProvinceController[4] == State,
                (hungry ? "hungry" : "fed") + ": with morale above zero the encircled formation fights on where it stands");
            pocket.Organisation = .001f;
            SetDay(c, 2); Warfare.Tick(c);
            Check(!c.Armies.Contains(pocket) && war.DefenderCaptured > 0f,
                (hungry ? "hungry" : "fed") + ": at zero morale under attack, with no retreat, it is destroyed and its men taken");
        }
    }

    static void PocketRecoversToItsCeiling()
    {
        var (c, pocket, _) = Pocket();
        Day(c, 0, desertion: false);
        // With stores: an assault was beaten off and left it shaken. It comes round, at half pace.
        pocket.Organisation = .30f;
        var twin = Unit(c, 1, 1);   // at home, supplied, the same shaking
        twin.Organisation = .30f;
        Day(c, 1, desertion: false);
        var gained = pocket.Organisation - .30f;
        var ordinary = twin.Organisation - .30f;
        Console.WriteLine($"  a day's rest: in a pocket with stores +{gained:0.0000}, supplied at home +{ordinary:0.0000}");
        Check(gained > 0f, "a pocket with stores recovers its morale after an assault (it used to recover nothing)");
        Check(gained < ordinary, "more slowly than a formation the road reaches");
        c.Armies.Remove(twin);

        // Hungry and on its floor, knocked under it by an assault that then gave up.
        var day = UntilHungry(c, pocket, 2, desertion: false);
        for (var hungry = 0; hungry < 30; hungry++) Day(c, day++, desertion: false);
        var ceiling = c.Defines.PocketHungerOrganisationFloor;
        pocket.Organisation = .04f;
        Day(c, day++, desertion: false);
        Check(pocket.Organisation > .04f && pocket.Organisation < ceiling, "a starved pocket comes round too, slowly");
        for (var rest = 0; rest < 120; rest++) Day(c, day++, desertion: false);
        Check(MathF.Abs(pocket.Organisation - ceiling) < 0.001f, "up to the ceiling its hunger has set, and no higher");
    }

    static void StoresAreFiniteAndShown()
    {
        var (c, pocket, _) = Pocket();
        Check(StrandedGarrisons.StoresBadge(c, pocket).Text == "" && StrandedGarrisons.SelectionLine(c, new[] { pocket }) == "",
            "a formation that is not cut off shows no stores badge and no pocket line");
        Day(c, 0, desertion: false);
        var counted = pocket.PocketStores;
        Day(c, 1, desertion: false);
        var quiet = counted - pocket.PocketStores;
        var badge = StrandedGarrisons.StoresBadge(c, pocket);
        Console.WriteLine($"  stores: {counted:0.0} days counted; a quiet day draws {quiet:0.00}; badge \"{badge.Text}\"; " +
                          $"panel: {StrandedGarrisons.SelectionLine(c, new[] { pocket })}");
        // The strip's edge is a coast, so this pocket holds a port: a held port brings a share
        // in (PocketPortDepotShare) and a quiet day eats that much less than one.
        var draw = StrandedGarrisons.DailyDraw(c, pocket);
        Check(counted > 1f && quiet > 0.1f && quiet <= 1f && MathF.Abs(quiet - draw) < 0.01f,
            "the stores are days of supply, and every quiet day eats its share of one");
        Check(badge.Severity == StrandedGarrisons.BadgeWarning &&
              badge.Text == ((int)MathF.Ceiling(pocket.PocketStores / draw)).ToString(),
            "the badge beside a cut-off division is the days its stores will last at that draw");
        Check(StrandedGarrisons.SelectionLine(c, new[] { pocket }).Contains(badge.Text),
            "and the army panel says the same number in one line");

        // A day of fighting eats nearly two, and the badge says what that leaves.
        var before = pocket.PocketStores;
        pocket.BattleId = 5;
        var inBattle = StrandedGarrisons.StoresBadge(c, pocket);
        Day(c, 2, desertion: false);
        pocket.BattleId = -1;
        var fought = before - pocket.PocketStores;
        Console.WriteLine($"  a day of fighting draws {fought:0.00}; the badge then read \"{inBattle.Text}\"");
        Check(MathF.Abs(fought - quiet * c.Defines.PocketStoresBattleDraw) < 0.01f && fought > quiet,
            "a day of battle draws the stores faster");
        Check(int.Parse(inBattle.Text) < int.Parse(badge.Text), "and the badge counts the days left at that day's draw");

        // What the tooltip says will happen, and what would end it.
        var tooltip = LogisticsSystem.DescribeSupply(c, pocket);
        Console.WriteLine("  tooltip with stores: " + tooltip);
        Check(tooltip.Contains(StrandedGarrisons.FateLine(c, pocket)) && StrandedGarrisons.FateLine(c, pocket).Length > 0,
            "the tooltip says what happens when the stores run out and what destroys the formation");

        var day = UntilHungry(c, pocket, 3, desertion: false);
        Check(day <= (int)(counted / quiet) + 4, "stores counted at " + counted.ToString("0.0") + " days are gone when they have been eaten, on day " + day);
        badge = StrandedGarrisons.StoresBadge(c, pocket);
        Check(badge.Text == "0" && badge.Severity == StrandedGarrisons.BadgeNegative, "with the stores gone the badge is a nought in the colour of bad news");
        Check(StrandedGarrisons.SelectionLine(c, new[] { pocket }) == Loc.Get("In a pocket: the stores are gone"),
            "and the panel line says they are gone");
        var hungryTip = LogisticsSystem.DescribeSupply(c, pocket);
        Console.WriteLine("  tooltip hungry: " + hungryTip);
        Check(hungryTip.Contains(StrandedGarrisons.FateLine(c, pocket)), "the hungry tooltip still says what would destroy it");
        Check(Loc.Has("chronicle.fighters_went_to_ground") && Loc.Has("chronicle.enemy_came_back") &&
              Loc.Has("In a pocket: stores for {0} days"),
            "the new sentences are in the localisation tables");
    }

    static void PeaceStopsTheHungerClock()
    {
        var (c, pocket, war) = Pocket();
        Day(c, 0, desertion: false);
        Day(c, 1, desertion: false);
        var stores = pocket.PocketStores;
        war.Ended = true; c.MarkWarsChanged();
        for (var day = 2; day < 40; day++) Day(c, day);
        Console.WriteLine($"  38 days of peace in an enclave: stores {stores:0.0} -> {pocket.PocketStores:0.0}, hungry {pocket.DaysCutOff} days, " +
                          $"supply {pocket.SupplyLevel:0.00}, strength {pocket.Strength:0.00}; {StrandedGarrisons.PocketLine(c, pocket)}");
        Check(pocket.CutOff && pocket.PocketStores == stores, "a country at peace does not draw the stores of its enclave");
        Check(pocket.DaysCutOff == 0 && pocket.Strength == 1f, "and nobody in it goes hungry or wastes away");
        Check(pocket.SupplyLevel > 0.7f, "it is kept where rationing would keep it");
        Check(StrandedGarrisons.SelectionLine(c, new[] { pocket }) == "", "an enclave at peace raises no pocket line in the army panel");

        // A pocket already starving when the peace is signed stops starving and comes back from it.
        (c, pocket, war) = Pocket();
        var hungryDay = UntilHungry(c, pocket, 0, desertion: false);
        for (var hungry = 0; hungry < 20; hungry++) Day(c, hungryDay++, desertion: false);
        var strength = pocket.Strength;
        var clock = pocket.DaysCutOff;
        war.Ended = true; c.MarkWarsChanged();
        Day(c, hungryDay++);
        Check(pocket.Strength == strength && pocket.DaysCutOff < clock, "the peace stops the wasting and turns the hunger clock back");
        for (var rest = 0; rest < 40; rest++) Day(c, hungryDay++);
        Check(pocket.DaysCutOff == 0 && pocket.Strength == strength && pocket.SupplyLevel > 0.7f,
            "a month of peace later it is fed again, with the men it had left");
    }

    static void ForgottenPocketDesertsOverYears()
    {
        var (c, pocket, _) = Pocket();
        var surrendered = WarLosses.SurrenderedFormations;
        string said = null;
        Attrition.Witness = (_, army, reason, _) => { if (ReferenceEquals(army, pocket)) said = reason; };
        var day = 0;
        var atHalfAYear = -1f;
        try
        {
            while (c.Armies.Contains(pocket) && day < 1500)
            {
                Day(c, day++);
                if (day == 200) atHalfAYear = pocket.Strength;
            }
        }
        finally { Attrition.Witness = null; }
        Console.WriteLine($"  a pocket nobody ever attacks or relieves: strength {atHalfAYear:0.000} on day 200, gone on day {day}, \"{said}\"");
        Check(atHalfAYear > c.Defines.DivisionBrokenStrength && atHalfAYear < c.Defines.PocketHungerStrengthFloor,
            "under the hunger floor it loses men only to desertion, slowly");
        Check(day > 365 && day < 1500, "it takes more than a year, and it does not stand for ever");
        Check(said == Attrition.DesertedInAPocket && WarLosses.SurrenderedFormations == surrendered,
            "when it goes, it has melted away - nobody starved it out and nobody took it prisoner");
    }

    // ================================================================== B: among their own people

    /// <summary>
    /// A rising against the besieger: it owns provinces 4 and 6 (its seat), and the besieger's
    /// province 5 lies between them. Its formation at 4 is on its own ground, among its own
    /// people, with no road to its seat.
    /// </summary>
    static (Campaign World, Army Fighters, StrategicWar War) RisingWorld(int seed = 4116, bool rebel = true)
    {
        var c = World(new ushort[] { 0, Besieger, Besieger, Besieger, Rising, Besieger, Rising, Besieger, Besieger }, seed);
        var rising = c.Nations[2];
        rising.CapitalProvinceId = 6; rising.CapitalStateMapIndex = 1006;
        if (rebel) { rising.SecededFromId = 0; rising.RevoltParentId = 0; }
        c.Homelands[rising.Id] = new List<int> { 1004, 1006 };
        var war = War(c, 0, 2);
        return (c, Unit(c, 2, 4), war);
    }

    static void RisingOnItsOwnLandIsNotCutOff()
    {
        var (c, fighters, _) = RisingWorld();
        var everCutOff = false;
        for (var day = 0; day < 61; day++)
        {
            Day(c, day);
            everCutOff |= fighters.CutOff;
        }
        Console.WriteLine($"  60 days on its own ground with no road to its seat: cut off ever {everCutOff}, supply {fighters.SupplyLevel:0.00}, " +
                          $"strength {fighters.Strength:0.00}, morale {fighters.Organisation:0.00}; {LogisticsSystem.DescribeSupply(c, fighters)}");
        Check(!everCutOff && fighters.LivingOffLand, "a rising's formation among its own people is fed by them, not cut off");
        Check(c.Armies.Contains(fighters) && fighters.Strength > 0.9f && fighters.Organisation > 0.9f,
            "sixty days later it is alive and whole where it rose");
        Check(MathF.Abs(fighters.SupplyLevel - c.Defines.PartisanForageSupply) < 0.02f && fighters.PocketStores < 0f,
            "living on what the land gives: less than a depot, and no pocket stores are being eaten");
        Check(EquipmentLogistics.DailyLimit(c, fighters) == 0f, "the villages feed it; they do not replace its equipment");
        Check(LogisticsSystem.DescribeSupply(c, fighters).StartsWith(Partisans.ForageLine(c, fighters), StringComparison.Ordinal) &&
              Partisans.ForageLine(c, fighters).Length > 0,
            "its tooltip says who feeds it and how many they can feed");

        // The road comes back: it is an ordinary supplied formation again.
        c.SetProvinceController(5, Rising);
        Day(c, 63);
        Check(!fighters.LivingOffLand && !fighters.CutOff && fighters.SupplyLevel > c.Defines.PartisanForageSupply,
            "when a road reaches it again the depots feed it and the mark goes");
        c.SetProvinceController(5, Besieger);

        // Without the people: the same formation in a region that is not its homeland and is not angry.
        var foreign = Unit(c, 2, 3);
        Day(c, 66);
        Check(foreign.CutOff && !foreign.LivingOffLand && foreign.PocketStores >= 0f,
            "among people who are not its own it is not fed: an ordinary pocket, on what it carries");
        // And among strangers who are in revolt themselves it is fed, half as well as at home under occupation.
        var angry = c.States.First(s => s.MapIndex == 1003);
        angry.Radicalism = 1f; angry.Stability = 0f;
        Check(Partisans.Support(c, c.Nations[2], 3) >= c.Defines.PartisanSupportFloor, "an angry region outside the homeland stands behind it enough");
        for (var day = 69; day < 100; day += 3) Day(c, day);
        Check(!foreign.CutOff && foreign.LivingOffLand &&
              MathF.Abs(foreign.SupplyLevel - c.Defines.PartisanForageSupply * c.Defines.PartisanOccupiedGroundShare) < 0.03f,
            "there it lives off the land at the occupied-ground share");

        // Ground it governed when the war began is its own people's too, homeland list or not:
        // the 2024 factions hold much of theirs as occupied ground (HTS in Aleppo). Ground it
        // took in the war is not.
        var (held, heldFighters, heldWar) = RisingWorld();
        held.Homelands[2] = new List<int> { 1006 };
        Check(Partisans.HeldWhenTheWarBegan(held, held.Nations[2], 4) &&
              Partisans.Support(held, held.Nations[2], 4) >= held.Defines.PartisanHomelandSupport,
            "a province the rising governed on the morning its war began counts as among its people");
        Day(held, 0);
        Check(!heldFighters.CutOff && heldFighters.LivingOffLand, "and a formation standing there is fed by them");
        heldWar.OriginalController[3] = Besieger;
        held.SetProvinceController(3, Rising);
        Check(!Partisans.HeldWhenTheWarBegan(held, held.Nations[2], 3) &&
              Partisans.Support(held, held.Nations[2], 3) < held.Defines.PartisanSupportFloor,
            "a province it took in the war is not: there it is a stranger with a rifle");
        // A second war, against the bystander, knows nothing of that capture: the record is the
        // first war's alone. The town is still taken ground, read through either war.
        var second = new StrategicWar { Id = WarId + 1, AttackerId = 2, DefenderId = 3, StartedDay = 0, LastFrontDay = 0 };
        held.Wars.Add(second); held.MarkWarsChanged();
        Check(!Partisans.HeldWhenTheWarBegan(held, held.Nations[2], 3) &&
              Partisans.Support(held, held.Nations[2], 3) < held.Defines.PartisanSupportFloor,
            "fighting two wars, ground taken in one is not its own through the other, which never recorded it");
        Check(Partisans.HeldWhenTheWarBegan(held, held.Nations[2], 4), "and ground it held before both still is");
        var shadow = Unit(held, 2, 3);
        Day(held, 3);
        Check(shadow.CutOff && !shadow.LivingOffLand, "a formation in that conquered town is cut off, not fed as if among its own");
        held.Armies.Remove(shadow);
        second.Ended = true;
        heldWar.Ended = true; held.MarkWarsChanged();
        Check(!Partisans.HeldWhenTheWarBegan(held, held.Nations[2], 4), "and with no war on, the question is not asked");

        // Not for a state's army, and not with the rule switched off.
        var (plain, regular, _) = RisingWorld(rebel: false);
        Day(plain, 0);
        Check(regular.CutOff && !regular.LivingOffLand, "a state's own formation in the same exclave is cut off: the rule is for risings");
        var (off, same, _) = RisingWorld();
        off.Defines.PartisanSupplyEnabled = false;
        Day(off, 0);
        Check(same.CutOff && !same.LivingOffLand, "with the rule switched off a rising's exclave is a pocket again");
    }

    static void LandFeedsOnlySoMany()
    {
        var (c, first, _) = RisingWorld();
        // Sixty thousand people at homeland support feed 1.2 divisions; three stand there.
        c.States.First(s => s.MapIndex == 1004).Population = 60_000;
        var second = Unit(c, 2, 4);
        var third = Unit(c, 2, 4);
        for (var day = 0; day < 45; day += 3) { SetDay(c, day); LogisticsSystem.Tick(c); }
        var fed = Partisans.Feeds(c, 1004, Partisans.Support(c, c.Nations[2], 4));
        var expected = c.Defines.PartisanForageSupply * fed / 3f;
        Console.WriteLine($"  a region that feeds {fed:0.0} divisions with 3 standing: supply {first.SupplyLevel:0.00} each (alone it would be {c.Defines.PartisanForageSupply:0.00})");
        Check(MathF.Abs(fed - 1.2f) < 0.01f, "people, support and the people-per-division define give what a region can feed");
        Check(new[] { first, second, third }.All(a => a.LivingOffLand && MathF.Abs(a.SupplyLevel - expected) < 0.02f),
            "more divisions than the land feeds share what there is, equally");
        Check(first.SupplyLevel >= c.Defines.PocketHungerSupplyFloor, "never worse than a starved pocket eats");
        // Nobody lives there: nobody feeds anybody, and it is a pocket.
        c.States.First(s => s.MapIndex == 1004).Population = 0;
        SetDay(c, 45); LogisticsSystem.Tick(c);
        Check(first.CutOff && !first.LivingOffLand, "an empty region feeds nobody");
    }

    static void MilitiaRefillsFromItsRegion()
    {
        var (c, _, _) = RisingWorld();
        var state = c.States.First(s => s.MapIndex == 1006);
        var home = MilitiaCore.FoundAt(c, c.Nations[2], state, 6, 9000, 0.45f);
        var away = MilitiaCore.FoundAt(c, c.Nations[2], c.States.First(s => s.MapIndex == 1005), 5, 9000, 0.45f);
        Check(home is not null && away is not null && home.TemplateId == MilitiaCore.TemplateId && home.Personnel == 9000,
            "a militia formation can be stood up in a named province");
        // Beaten: 4,000 men off the roll and off its strength, 1,000 of them wounded and coming back.
        foreach (var militia in new[] { home, away })
        {
            militia.Personnel = 5000;
            militia.Strength = 5000f / militia.MaxPersonnel;
            militia.Recovering = 1000f;
        }
        SetDay(c, 1);
        MilitiaCore.Tick(c);
        var share = (int)(home.MaxPersonnel * c.Defines.MilitiaVolunteerSharePerDay);
        Console.WriteLine($"  a drained militia after a day: on its own ground {home.Personnel} men at strength {home.Strength:0.000}, " +
                          $"{home.Recovering:N0} wounded; on the enemy's {away.Personnel}");
        Check(home.Personnel == 5000 + share, "a militia on ground its country holds refills from its region (it never did: the flag was compared with a list position)");
        Check(MathF.Abs(home.Strength - (5000f + share) / home.MaxPersonnel) < 0.0001f,
            "a volunteer is fighting strength as well as a name on the roll: strength rises with the roll");
        Check(home.Recovering == 1000f, "the volunteers are new men: the wounded are still the wounded, coming back by their own road");
        Check(away.Personnel == 5000 && away.Strength == 5000f / away.MaxPersonnel, "one standing on the enemy's ground does not");
        // Full but for its wounded: there is no place for a volunteer that a wounded man is coming back to.
        home.Personnel = 8000; home.Strength = 8000f / home.MaxPersonnel; home.Recovering = 1000f;
        SetDay(c, 2);
        MilitiaCore.Tick(c);
        Check(home.Personnel == 8000 && home.Strength == 8000f / home.MaxPersonnel,
            "a militia whose only gap is its wounded takes no volunteers: the roll never outgrows the establishment");
    }

    /// <summary>
    /// "Partisans die right where they rose." A rising's formation broken in its own province,
    /// with the enemy's flag over every neighbour, is not taken off the map when one neighbour
    /// is its own people's valley and no enemy stands in it: it slips there and lives.
    /// </summary>
    static void BrokenAmongItsOwnSlipsAway()
    {
        var (c, fighters, war) = RisingWorld();
        var rising = c.Nations[2];
        // Region 5, between its two, is its people's too: the besieger holds the town, nobody
        // stands in it.
        c.Homelands[rising.Id] = new List<int> { 1004, 1005, 1006 };
        Day(c, 0);
        var assault = Unit(c, 0, 3, 3f);
        Battle(c, assault, fighters);
        fighters.Organisation = .001f;
        var slipped = Partisans.Tally.GetValueOrDefault("slipped_away");
        var gone = Partisans.Tally.GetValueOrDefault("went_to_ground");
        var men = fighters.Personnel;
        string said = null;
        Attrition.Witness = (_, army, reason, _) => { if (ReferenceEquals(army, fighters)) said = reason; };
        var day = 1;
        try
        {
            for (; day < 6 && fighters.ProvinceId == 4 && c.Armies.Contains(fighters); day++) { SetDay(c, day); Warfare.Tick(c); }
        }
        finally { Attrition.Witness = null; }
        Console.WriteLine($"  broken in its own province with the enemy's flag on both sides: in province {fighters.ProvinceId} on day {day - 1}, " +
                          $"{fighters.Personnel:N0} of {men:N0} men, morale {fighters.Organisation:0.000}; \"{said ?? "still on the map"}\"");
        Check(c.Armies.Contains(fighters) && said is null, "a rising's formation broken among its own people is not taken off the map");
        Check(fighters.ProvinceId == 5 && Partisans.Tally.GetValueOrDefault("slipped_away") == slipped + 1,
            "it slipped past the enemy into the neighbouring valley of its people, where nobody was standing");
        Check(Partisans.Tally.GetValueOrDefault("went_to_ground") == gone && rising.FightersGoneToGround.Count == 0 &&
              war.DefenderCaptured < men * 0.05f,
            "nobody went to ground and nobody walked into captivity");
        Check(c.ProvinceController[4] == Besieger && c.ProvinceController[5] == Besieger,
            "the battle is lost and the ground is the victor's; the valley it slipped into is not taken by slipping into it");
        Check(fighters.ReadyDay > c.ElapsedDays, "it arrives as any beaten formation does, needing rest");
        for (var rest = day; rest < day + 4; rest++) { SetDay(c, rest); LogisticsSystem.Tick(c); }
        Check(fighters.LivingOffLand && !fighters.CutOff && fighters.SupplySourceStateMapIndex == 1005,
            "under the enemy's flag among its own people it is not a pocket: the valley feeds it");

        // The enemy follows it there and stands in every way out: now there is nowhere to slip
        // to, and it goes to ground. Province 6 is held by an enemy formation as well.
        Unit(c, 0, 6);
        assault.ProvinceId = 4; assault.StateMapIndex = 1004;
        var again = Unit(c, 0, 4, 3f);
        c.Battles.Clear();
        fighters.BattleId = -1; assault.BattleId = -1;
        Battle(c, again, fighters);
        fighters.Organisation = .001f;
        Attrition.Witness = (_, army, reason, _) => { if (ReferenceEquals(army, fighters)) said = reason; };
        try { SetDay(c, day + 5); Warfare.Tick(c); }
        finally { Attrition.Witness = null; }
        Check(!c.Armies.Contains(fighters) && said == Partisans.WentToGround,
            "with an enemy formation in every province it could slip to, it scatters into the villages");

        // A state's formation has no such people and no such paths.
        var (plain, regular, plainWar) = RisingWorld(rebel: false);
        plain.Homelands[2] = new List<int> { 1004, 1005, 1006 };
        Day(plain, 0);
        Battle(plain, Unit(plain, 0, 3, 3f), regular);
        regular.Organisation = .001f;
        SetDay(plain, 1); Warfare.Tick(plain);
        Check(!plain.Armies.Contains(regular) && plainWar.DefenderCaptured > 10_000f,
            "a state's formation broken in the same place surrenders, as it always did");
        // Nor with the rule switched off.
        var (off, same, _) = RisingWorld();
        off.Homelands[2] = new List<int> { 1004, 1005, 1006 };
        off.Defines.GuerrillaSlipAwayEnabled = false;
        Day(off, 0);
        Battle(off, Unit(off, 0, 3, 3f), same);
        same.Organisation = .001f;
        SetDay(off, 1); Warfare.Tick(off);
        Check(!off.Armies.Contains(same) && off.Nations[2].FightersGoneToGround.Count > 0,
            "with slipping away switched off it goes to ground where it was beaten, as in the first version");

        // The only way out leads among strangers (region 5 is not its people's): it takes it
        // all the same, and there it is an ordinary pocket on what it carries - weakening, and
        // not dying of it - instead of vanishing where it was beaten.
        var (far, wanderer, _) = RisingWorld();
        Day(far, 0);
        Battle(far, Unit(far, 0, 3, 3f), wanderer);
        wanderer.Organisation = .001f;
        for (var at = 1; at < 6 && wanderer.ProvinceId == 4 && far.Armies.Contains(wanderer); at++) { SetDay(far, at); Warfare.Tick(far); }
        Console.WriteLine($"  broken with only strangers' ground unwatched: on the map {far.Armies.Contains(wanderer)}, in province {wanderer.ProvinceId}, " +
                          $"hidden {Partisans.Hidden(far.Nations[2]):N0}, morale {wanderer.Organisation:0.000}");
        Check(far.Armies.Contains(wanderer) && wanderer.ProvinceId is 3 or 5 && far.Nations[2].FightersGoneToGround.Count == 0,
            "with only strangers' ground unwatched, it slips there rather than vanish - even behind the assault, emptied by it");
        for (var at = 6; at < 10; at++) { SetDay(far, at); LogisticsSystem.Tick(far); }
        Check(wanderer.CutOff && !wanderer.LivingOffLand && wanderer.PocketStores >= 0f,
            "among strangers nobody feeds it: it is a pocket, on its stores");
    }

    /// <summary>
    /// "Small pockets in civil wars should stand up to a great power." A rising still under
    /// arms loses its war at home when its fighters are gone, not when the strong hold its
    /// towns: its home front reads its formations, a state's reads the score and its ground.
    /// </summary>
    static void TheWeakLoseTheirWarInTheField()
    {
        var (c, fighters, war) = RisingWorld();
        var rising = c.Nations[2];
        var besieger = c.Nations[0];
        // Both of its provinces taken: a state in that position is collapsing.
        c.SetProvinceController(4, Besieger); c.SetProvinceController(6, Besieger);
        war.DefenderOccupiedValue = 1f; war.WarScore = 90f;
        Check(WarWeariness.HomeFrontMood(c.Defines, -90f, 1f) == "front_collapsing",
            "a state with all its ground under the enemy and the score against it reads its front as collapsing");
        Unit(c, 2, 4);
        Check(Factions.HoldsOnByArms(c, rising) && WarWeariness.MoodOf(c, war, rising) is null,
            "a rising with the formations it fights on with feels no collapse for the towns it lost");
        c.Armies.RemoveAll(a => a.NationId == rising.Id && !ReferenceEquals(a, fighters));
        Check(!Factions.HoldsOnByArms(c, rising) && WarWeariness.MoodOf(c, war, rising) == "front_collapsing",
            "down to its last formation it is read as anybody is");
        rising.FightersGoneToGround[1004] = MilitiaCore.Weights.Formation;
        Check(WarWeariness.MoodOf(c, war, rising) is null, "a detachment in hiding beside it counts as one under arms");
        rising.FightersGoneToGround.Clear();
        Unit(c, 2, 4);
        c.Defines.RisingHomeFrontByArms = false;
        Check(WarWeariness.MoodOf(c, war, rising) == "front_collapsing", "with the rule off it is read as a state");
        c.Defines.RisingHomeFrontByArms = true;
        // Not a state's army: the besieger, attacking and losing, is read by its score.
        war.WarScore = -90f;
        Unit(c, 0, 1); Unit(c, 0, 1);
        Check(WarWeariness.MoodOf(c, war, besieger) == "front_collapsing", "a state under arms is still read by its score and its ground");
        // Weekly, through the pass that grants it.
        war.WarScore = 90f;
        SetDay(c, c.Defines.EconomyTickDays);
        WarWeariness.Tick(c);
        Check(!rising.Modifiers.Any(m => m.Id is "front_collapsing" or "war_going_badly"),
            "and the weekly pass grants the rising under arms no collapsing front");
    }

    static void BeatenWithNoRetreatGoesToGround()
    {
        var (c, fighters, war) = RisingWorld();
        var rising = c.Nations[2];
        Day(c, 0);
        // Every way out watched: the besieger stands in province 5 as well as attacking from 3.
        var watch = Unit(c, 0, 5);
        var assault = Unit(c, 0, 3, 3f);
        Battle(c, assault, fighters);
        fighters.Organisation = .001f;
        var men = fighters.Personnel;
        string said = null;
        var lost = true;
        Attrition.Witness = (_, army, reason, counted) => { if (ReferenceEquals(army, fighters)) { said = reason; lost = counted; } };
        try { SetDay(c, 1); Warfare.Tick(c); }
        finally { Attrition.Witness = null; }
        var hidden = rising.FightersGoneToGround.GetValueOrDefault(1004);
        Console.WriteLine($"  beaten with no retreat on its own ground: {men:N0} men -> {hidden:N0} in hiding, {war.DefenderCaptured:N0} captured, " +
                          $"{war.DefenderKilled:N0} killed; \"{said}\"");
        Check(!c.Armies.Contains(fighters) && said == Partisans.WentToGround && !lost,
            "a rising's formation beaten with nowhere to go, among its own people, scatters instead of surrendering");
        Check(MathF.Abs(hidden - men * c.Defines.GuerrillaGoToGroundShare) < 1f, "six in ten of its men are in hiding in that region");
        Check(war.DefenderCaptured > 0f && war.DefenderCaptured + war.DefenderKilled < men * 0.45f && rising.FormationsLost == 0,
            "the rest are the dead and the prisoners of a lost battle - fewer prisoners than it had men");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "fighters_went_to_ground" && e.ActorId == rising.Id && !e.Public) &&
              c.Chronicle.Entries.Any(e => e.Kind == "enemy_went_to_ground" && e.ActorId == 0 && !e.Public),
            "both sides read of it, each in its own chronicle");
        Check(c.ProvinceController[4] == Besieger, "the ground is the victor's");

        // They come back where nobody is standing, and not where somebody is.
        c.Defines.GuerrillaReformWeeklyChance = 1f;
        Check(assault.ProvinceId == 4, "the victor's formation is standing in the province it took");
        for (var day = 2; day < 30; day++) { SetDay(c, day); Partisans.Tick(c); }
        Check(!c.Armies.Any(a => a.NationId == rising.Id) && rising.FightersGoneToGround.GetValueOrDefault(1004) > 0f,
            "with a garrison in every province of the region nobody takes the field");
        var waiting = rising.FightersGoneToGround[1004];
        Check(waiting < hidden, "and the hidden drift home while they wait");
        assault.ProvinceId = 3; assault.StateMapIndex = 1003;
        for (var day = 30; day < 37; day++) { SetDay(c, day); Partisans.Tick(c); }
        var back = c.Armies.FirstOrDefault(a => a.NationId == rising.Id);
        Console.WriteLine($"  the garrison left: {(back is null ? "nobody" : back.Personnel.ToString("N0") + " men")} took the field in province {back?.ProvinceId}, " +
                          $"{rising.FightersGoneToGround.GetValueOrDefault(1004):N0} still hidden");
        Check(back is not null && back.ProvinceId == 4 && back.Personnel >= MilitiaCore.Weights.Formation * c.Defines.GuerrillaReformMinShare,
            "once no enemy formation stands there a detachment comes out of hiding");
        Check(c.ProvinceController[4] == Rising && war.OriginalController.ContainsKey(4),
            "and takes the province it came out in");
        Check(MathF.Abs(back.Organisation - c.Defines.GuerrillaReformOrganisation) < 0.001f && back.Strength < 1f,
            "thin, and mustered in a hurry");
        Check(rising.FightersGoneToGround.GetValueOrDefault(1004) < waiting - back.Personnel + 300f,
            "the men it fielded are no longer in hiding");
        SetDay(c, 39); LogisticsSystem.Tick(c);
        Check(!back.CutOff && back.LivingOffLand, "among its own people again, it is fed by them");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "fighters_came_back" && e.ActorId == rising.Id),
            "its side reads that they are back");

        // Not among strangers, and not a state's army: those surrender as they always did.
        var (plain, regular, plainWar) = RisingWorld(rebel: false);
        Day(plain, 0);
        Battle(plain, Unit(plain, 0, 3, 3f), regular);
        regular.Organisation = .001f;
        SetDay(plain, 1); Warfare.Tick(plain);
        Check(!plain.Armies.Contains(regular) && plain.Nations[2].FightersGoneToGround.Count == 0 &&
              plainWar.DefenderCaptured > 10_000f,
            "a state's formation in the same place surrenders whole");
    }

    /// <summary>
    /// "Small pockets in civil wars should stand up to a great power." A rising that has lost
    /// the last region it owned, with formations in the field and the will to use them, has lost
    /// its towns and not its war: it does not fall that week, its war is not ended for it, and
    /// its army is not laid down with a state that is not there. It falls when the fighters go.
    /// </summary>
    static void TheWeakFightOnWithoutGround()
    {
        // The besieger owns everything: the rising's two regions have been taken off it.
        var landless = new ushort[] { 0, Besieger, Besieger, Besieger, Besieger, Besieger, Besieger, Besieger, Besieger };
        Campaign Landless(bool rule)
        {
            var c = World(landless);
            var rising = c.Nations[2];
            rising.SecededFromId = 0; rising.RevoltParentId = 0;
            rising.CapitalProvinceId = 4; rising.CapitalStateMapIndex = 1004;
            c.Homelands[rising.Id] = new List<int> { 1004, 1006 };
            c.Defines.RisingsFightOnWithoutGround = rule;
            War(c, 0, 2);
            Unit(c, 2, 4); Unit(c, 2, 6); Unit(c, 2, 6);
            return c;
        }
        void Week(Campaign c, int day)
        {
            SetDay(c, day);
            CapitalCore.Tick(c);
            MovementFall.Decide(c);
            MovementFall.EndLostWars(c);
        }

        var held = Landless(rule: true);
        var side = held.Nations[2];
        var war = held.Wars[0];
        Check(held.StatesOf(side).Length == 0 && Factions.FightsOnWithoutGround(held, side),
            "a rising with no region, three formations and its people for the war fights on without ground");
        Week(held, 7);
        Check(!held.Fallen.Contains(side.Id) && !war.Ended && held.Armies.Count(a => a.NationId == side.Id) == 3,
            "it has not fallen, its war goes on and its formations are still in the field");

        // Nor is it signed out of existence: the line as the border leaves it nothing, and it
        // will not take that peace. A white peace gives ground back; that one it may weigh.
        SetDay(held, 600);
        Check(Factions.KeepsNothingByTheLine(held, war, side) &&
              PeaceNegotiation.Acceptance(held, war, 0, PeaceTerms.StatusQuo) == 0f &&
              PeaceNegotiation.BlockReason(held, war, 0, PeaceTerms.StatusQuo).Length > 0,
            "keeping nothing by the line, it refuses the peace of the line, however long the war");
        Check(PeaceNegotiation.Acceptance(held, war, 0, PeaceTerms.WhitePeace) > 0f,
            "a white peace is still a peace it weighs");
        SetDay(held, held.Defines.PeaceForcedEndDays + 1);
        Check(PeaceNegotiation.Acceptance(held, war, 0, PeaceTerms.StatusQuo) > 0f,
            "past the five years at which the world ends a war nobody plays, the line is drawn all the same");
        SetDay(held, 600);

        // Owning ground it no longer holds, it does not sign the last of it away either. The
        // rising owns provinces 4 and 6; a treaty taking both is refused, one taking 4 is not -
        // not for that reason - and with its army gone it is no longer refused.
        var (owned, _, ownedWar) = RisingWorld();
        Unit(owned, 2, 6); Unit(owned, 2, 6);
        const string lastGround = "They will not sign away the last of their ground while they still have an army in the field";
        var unconditional = false;
        string Asked(params int[] provinces)
        {
            var demand = new PeaceDemand { WarId = ownedWar.Id, ProposerId = 0, Unconditional = unconditional };
            foreach (var province in provinces) demand.Provinces.Add(province);
            return PeaceConference.BlockReason(owned, ownedWar, demand);
        }
        Check(Asked(4, 6) == lastGround, "a rising in the field will not sign away the last of the ground it owns");
        Check(Asked(4) != lastGround, "a treaty that leaves it some of its ground is weighed as any other");
        unconditional = true;
        Check(Asked(4, 6) == lastGround, "nor as the unconditional settlement of a war decided by the ground the other side holds");
        unconditional = false;
        owned.Armies.RemoveAll(a => a.NationId == 2 && a.ProvinceId == 6);
        Check(Asked(4, 6) != lastGround, "with its last formation alone in the field, that refusal is gone");

        // Holding a province is not keeping one. A rising that owns and holds its two provinces
        // keeps them by the line; once the besieger holds both, the line would give them to it.
        var (line, _, lineWar) = RisingWorld();
        Unit(line, 2, 6); Unit(line, 2, 6);
        Check(!Factions.KeepsNothingByTheLine(line, lineWar, line.Nations[2]), "owning and holding its own ground, the line leaves it that");
        foreach (var province in new[] { 4, 6 })
        {
            lineWar.OriginalController[province] = Rising;
            line.SetProvinceController(province, Besieger);
        }
        Check(PeaceNegotiation.StatusQuoLeavesNothing(line, lineWar, line.Nations[2]) &&
              Factions.KeepsNothingByTheLine(line, lineWar, line.Nations[2]),
            "with both its provinces held by the besieger, the line would leave it nothing, and it refuses it");
        // In a coalition the leader signs for all: a leader with nothing at stake does not sign
        // the line that ends its member.
        lineWar.DefenderId = 1;
        lineWar.DefenderAllies.Add(2);
        line.MarkWarsChanged();
        Check(Factions.SideKeepsNothingByTheLine(line, lineWar, attackers: false) &&
              PeaceNegotiation.Acceptance(line, lineWar, 0, PeaceTerms.StatusQuo) == 0f,
            "a coalition will not sign the line that leaves one of its fighting members nothing");
        // Out of a peace it lost, with its two regions and three formations, it is a statelet,
        // not a rump to fold back into the state it fought.
        var (kept, _, keptWar) = RisingWorld();
        Unit(kept, 2, 6); Unit(kept, 2, 6);
        keptWar.Ended = true; keptWar.WinnerId = 0;
        Factions.SettleCivilWarSurvivors(kept, keptWar);
        Check(kept.HasOwnedLand(kept.Nations[2]) && kept.Nations[2].Population > 0 && kept.StatesOf(kept.Nations[2]).Length == 2,
            "a rising that came out of a lost war with its ground and its formations keeps both");
        var (folded, _, foldedWar) = RisingWorld();
        Unit(folded, 2, 6); Unit(folded, 2, 6);
        folded.Defines.RisingsFightOnWithoutGround = false;
        foldedWar.Ended = true; foldedWar.WinnerId = 0;
        Factions.SettleCivilWarSurvivors(folded, foldedWar);
        Check(!folded.HasOwnedLand(folded.Nations[2]) && folded.Nations[2].Population == 0,
            "with the rule off, two regions are a rump and go back to the state, army or no army");

        line.Defines.RisingsFightOnWithoutGround = false;
        Check(!Factions.KeepsNothingByTheLine(line, lineWar, line.Nations[2]), "with the rule off it may sign that line, as before");

        // Down to one formation: a side holding nothing needs two. That week it is over.
        held.Armies.RemoveAll(a => a.NationId == side.Id && a.ProvinceId == 6);
        Check(!Factions.FightsOnWithoutGround(held, side), "down to its last formation it no longer fights on without ground");
        Week(held, 14);
        Check(held.Fallen.Contains(side.Id) && war.Ended && held.Armies.All(a => a.NationId != side.Id),
            "and that week it falls, its war ends and its last formation lays down its arms");

        // Nor with nobody left believing in the war.
        var tired = Landless(rule: true);
        tired.Nations[2].WarSupport = tired.Defines.FactionFightOnSupport * 0.5f;
        Week(tired, 7);
        Check(tired.Fallen.Contains(2) && tired.Wars[0].Ended,
            "with its people tired of the war it falls with all its formations standing");

        // A state's army is not this: the old rule for a country overrun.
        var state = Landless(rule: true);
        state.Nations[2].SecededFromId = -1; state.Nations[2].RevoltParentId = -1;
        Week(state, 7);
        Check(state.Fallen.Contains(2) && state.Wars[0].Ended && state.Armies.All(a => a.NationId != 2),
            "a state that has lost all its ground falls with its army, as it always did");

        // And with the rule switched off, the rising goes the same way.
        var off = Landless(rule: false);
        Week(off, 7);
        Check(off.Fallen.Contains(2) && off.Wars[0].Ended && off.Armies.All(a => a.NationId != 2),
            "with the rule off, the last town lost is the war lost");
    }

    static void UndergroundMeltsAway()
    {
        var (c, fighters, _) = RisingWorld();
        var rising = c.Nations[2];
        c.Armies.Remove(fighters);
        // An enemy formation in the one province of each region, so nobody comes back.
        Unit(c, 0, 4); Unit(c, 0, 6);
        c.SetProvinceController(4, Besieger); c.SetProvinceController(6, Besieger);
        c.Defines.GuerrillaReformWeeklyChance = 1f;
        rising.FightersGoneToGround[1004] = 27_000f;
        rising.FightersGoneToGround[1006] = 9_000f;
        var weeks = 0;
        var small = -1;
        while (Partisans.DetachmentsInHiding(c, rising) > 0 && weeks < 200)
        {
            SetDay(c, weeks * 7 + 2);
            Partisans.Tick(c);
            weeks++;
            if (small < 0 && rising.FightersGoneToGround.GetValueOrDefault(1006) < 4_500f) small = weeks;
        }
        Console.WriteLine($"  27,000 + 9,000 hidden under a full garrison: the 9,000 are under a detachment in {small} weeks, all of them in {weeks}");
        Check(small is > 15 and < 30, "nine thousand hidden are too few to field after about half a year");
        Check(weeks is > 50 and < 110 && !c.Armies.Any(a => a.NationId == rising.Id),
            "and a large underground held down everywhere is spent in a year or two: there is no immortality in it");
    }

    static void HiddenFightersKeepTheWarAlive()
    {
        var (c, fighters, war) = RisingWorld();
        var rising = c.Nations[2];
        c.Armies.Remove(fighters);
        Unit(c, 0, 4); Unit(c, 0, 6);
        c.SetProvinceController(4, Besieger); c.SetProvinceController(6, Besieger);
        war.OriginalController[4] = Rising; war.OriginalController[6] = Rising;
        Check(AiStatecraft.SideOverrun(c, war, attackers: false) && !Factions.FightsOn(c, rising, 1f),
            "a rising with no ground, no formation and nobody in hiding is overrun and gives up");
        rising.FightersGoneToGround[1004] = 2f * MilitiaCore.Weights.Formation;
        Check(!AiStatecraft.SideOverrun(c, war, attackers: false), "with fighters in hiding it is not overrun");
        Check(Factions.FightsOn(c, rising, 0.9f), "and it fights on: the hidden count beside its formations");
        rising.FightersGoneToGround[1004] = 0.4f * MilitiaCore.Weights.Formation;
        Check(AiStatecraft.SideOverrun(c, war, attackers: false) && !Factions.FightsOn(c, rising, 0.9f),
            "too few to field a detachment, and the war is lost");
        // Holding none of its own ground, a side down to its last two or three formations needs
        // twice the conviction to go on; with twice that many in the field or in hiding the
        // ordinary bar is enough - that is how a war of the weak goes on when it holds nothing.
        rising.WarSupport = 0.3f;
        Check(!Factions.FightsOn(c, rising, 1f), "holding nothing, with nobody hidden, a lukewarm war is given up");
        var detachment = MilitiaCore.Weights.Formation * c.Defines.GuerrillaReformMinShare;
        // What fields n formations out of one region: a full militia formation at a time, and
        // the last one when it is at least a detachment.
        float Worth(int n) => (n - 1) * MilitiaCore.Weights.Formation + detachment;
        rising.FightersGoneToGround[1004] = Worth(2);
        Check(Partisans.DetachmentsInHiding(c, rising) == 2, "the hidden are counted as the formations they would field");
        Check(!Factions.FightsOn(c, rising, 1f), "holding nothing and down to two detachments, a lukewarm war is still given up");
        Unit(c, 2, 6); Unit(c, 2, 6);
        Check(Factions.FightsOn(c, rising, 1f), "holding nothing, with two formations in the field and two hidden, it is not");
        c.Armies.RemoveAll(a => a.NationId == rising.Id);
        rising.FightersGoneToGround[1004] = Worth(4);
        Check(Factions.FightsOn(c, rising, 1f), "nor with four detachments' worth hidden and nobody in the field");
        rising.WarSupport = 0.15f;
        Check(!Factions.FightsOn(c, rising, 1f), "and with nobody left believing in the war, it is given up all the same");

        // Hidden men do not walk between regions to make up a detachment. Three regions of 2,700
        // each are 8,100 in hiding and not one formation that could ever take the field: the
        // side is overrun, and the remnant pass may settle it.
        rising.WarSupport = 0.6f;
        rising.FightersGoneToGround.Clear();
        rising.FightersGoneToGround[1004] = 2_700f;
        rising.FightersGoneToGround[1005] = 2_700f;
        rising.FightersGoneToGround[1006] = 2_700f;
        Check(Partisans.Hidden(rising) > detachment && Partisans.DetachmentsInHiding(c, rising) == 0,
            "scattered under a detachment in every region, the hidden add up to nobody who can take the field");
        Check(AiStatecraft.SideOverrun(c, war, attackers: false) && !Factions.FightsOn(c, rising, 0.9f),
            "so a side with nothing else is overrun, not kept alive by a phantom");
        // Nor do men count in a region where their war is over: a region of the bystander's, at
        // peace with the rising, has no ground for them to come out on.
        c.SetProvinceController(7, 103);
        rising.FightersGoneToGround.Clear();
        rising.FightersGoneToGround[1007] = Worth(2);
        Check(Partisans.DetachmentsInHiding(c, rising) == 0 && AiStatecraft.SideOverrun(c, war, attackers: false),
            "hidden in a region held by a country it is not at war with, they are nobody's detachments");
        c.SetProvinceController(7, Besieger);
        Check(Partisans.DetachmentsInHiding(c, rising) == 2, "in the same region under the enemy's flag they are two");

        // They come out when the side needs them, not only when it is confident: a rising at the
        // line where a faction gives up still fields what it has hidden.
        rising.WarSupport = c.Defines.FactionFightOnSupport * 0.5f;
        c.Defines.GuerrillaReformWeeklyChance = 1f;
        c.Armies.RemoveAll(a => a.NationId == 0 && a.ProvinceId == 7);
        SetDay(c, 2); Partisans.Tick(c);
        Check(c.Armies.Any(a => a.NationId == rising.Id && a.StateMapIndex == 1007),
            "with war support at the giving-up line, the hidden still take the field");
        c.Armies.RemoveAll(a => a.NationId == rising.Id);
        rising.WarSupport = 0.6f;
        // The war over, they are simply home.
        rising.FightersGoneToGround[1004] = 20_000f;
        war.Ended = true; c.MarkWarsChanged();
        SetDay(c, 9); Partisans.Tick(c);
        Check(rising.FightersGoneToGround.Count == 0, "when the war ends nobody is in hiding any more");
    }

    static void SaveLoadAndOldSaves()
    {
        var (c, fighters, war) = RisingWorld();
        // The state fights beside the rising and holds no ground: its formation on the besieger's
        // land is stranded, an ordinary pocket living on what it carries.
        war.DefenderAllies.Add(1); c.MarkWarsChanged();
        var pocketState = Unit(c, 1, 1);
        Day(c, 0);
        c.Nations[2].FightersGoneToGround[1004] = 5_400.5f;
        c.Nations[2].FightersGoneToGround[1006] = 120f;
        Check(fighters.LivingOffLand && pocketState.CutOff, "the world to be saved has a formation living off the land and one in a pocket");
        var baseline = CampaignFingerprint.Of(c);
        fighters.LivingOffLand = false;
        Check(CampaignFingerprint.Of(c) != baseline, "living off the land is in the campaign fingerprint");
        fighters.LivingOffLand = true;
        c.Nations[2].FightersGoneToGround[1004] += 1f;
        Check(CampaignFingerprint.Of(c) != baseline, "so are the fighters in hiding");
        c.Nations[2].FightersGoneToGround[1004] -= 1f;

        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "pocket-rules"), "pocket-rules");
        Check(saved.Success, "the world saves and loads");
        var restored = saved.Campaign;
        var twin = restored.Armies.First(a => a.Id == fighters.Id);
        var twinPocket = restored.Armies.First(a => a.Id == pocketState.Id);
        Check(twin.LivingOffLand && !twin.CutOff && twin.SupplyLevel == fighters.SupplyLevel, "living off the land survives the save");
        Check(twinPocket.CutOff && twinPocket.PocketStores == pocketState.PocketStores && twinPocket.DaysCutOff == pocketState.DaysCutOff,
            "a pocket's stores and hunger clock survive it");
        var hidden = restored.Nations[2].FightersGoneToGround;
        Check(hidden.Count == 2 && hidden[1004] == 5_400.5f && hidden[1006] == 120f, "the fighters in hiding survive it, region by region");

        // A save from before these rules: the fields are simply absent.
        var options = new JsonSerializerOptions { IncludeFields = true };
        var oldArmy = JsonSerializer.Deserialize<Army>("{\"Id\":7,\"CutOff\":true,\"DaysCutOff\":9}", options);
        var oldNation = JsonSerializer.Deserialize<Nation>("{\"Id\":3,\"Tag\":\"OLD\"}", options);
        Check(oldArmy is { LivingOffLand: false, CutOff: true, DaysCutOff: 9 } && oldArmy.PocketStores == -1f,
            "an old save's formation loads as not living off the land, its pocket intact");
        Check(oldNation?.FightersGoneToGround is { Count: 0 }, "an old save's country loads with nobody in hiding");
        Check(new Defines().PocketSurrenderDailyChance == 0f && new Defines().PocketHungerStrengthFloor == 0.35f &&
              new Defines().PocketResistanceMinSupply == 0f && new Defines().EncircledStrengthLoss == 0.010f,
            "the compiled defaults are the rule as shipped");
    }

    /// <summary>The whole story once: a rising fed by its land, beaten, hidden, back; and a state's pocket starving beside it.</summary>
    static string Story(int seed)
    {
        var c = World(new ushort[] { 0, State, Besieger, Besieger, Rising, Besieger, Rising, Besieger, State }, seed);
        var rising = c.Nations[2];
        rising.CapitalProvinceId = 6; rising.CapitalStateMapIndex = 1006;
        rising.SecededFromId = 0; rising.RevoltParentId = 0;
        c.Homelands[rising.Id] = new List<int> { 1004, 1006 };
        c.Nations[1].CapitalProvinceId = 1; c.Nations[1].CapitalStateMapIndex = 1001;
        var war = new StrategicWar { Id = WarId, AttackerId = 0, DefenderId = 2, StartedDay = 0, LastFrontDay = 0 };
        war.DefenderAllies.Add(1);
        c.Wars.Add(war); c.MarkWarsChanged();
        var fighters = Unit(c, 2, 4);
        Unit(c, 2, 6);
        Unit(c, 1, 8);                            // the state's exclave: an ordinary pocket
        var assault = Unit(c, 0, 3, 3f);
        for (var day = 0; day < 20; day++) Day(c, day);
        Battle(c, assault, fighters);
        fighters.Organisation = .01f;
        for (var day = 20; day < 90; day++)
        {
            Day(c, day, battles: day < 24);
            if (day == 30) { assault.ProvinceId = 3; assault.StateMapIndex = 1003; }
        }
        return CampaignFingerprint.Of(c) + " rng " + c.Random.State + " armies " + c.Armies.Count +
               " hidden " + Partisans.Hidden(rising).ToString("R") + " day " + c.ElapsedDays;
    }

    static void TwoWorldsFromOneSeedAgree()
    {
        var first = Story(20261116);
        var second = Story(20261116);
        Console.WriteLine("  the story, twice from one seed: " + first);
        Check(first == second, "two worlds from one seed tell the same story to the last bit: pockets, the land and the hidden draw no dice of their own");
    }
}
