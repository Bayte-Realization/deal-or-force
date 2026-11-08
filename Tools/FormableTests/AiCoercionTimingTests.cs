using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>The strategic decision must precede a threat, not only the resulting war.</summary>
internal static class AiCoercionTimingTests
{
    private static readonly MethodInfo Warmaking = typeof(AiStatecraft).GetMethod("Warmaking",
        BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo DailyTables = typeof(AiStatecraft).GetMethod("EnsureDailyTables",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("AI coercion timing: " + text);
        Console.WriteLine("PASS AI coercion timing: " + text);
    }

    internal static void Run()
    {
        // Loads the real culture/government/doctrine catalogues once. The small worlds below
        // exercise the real strategic decision, not a second implementation of its roll.
        _ = Campaign.CreateBootstrap(20261107);
        foreach (var annex in new[] { false, true })
        {
            var committed = 0;
            var labels = annex ? "whole-state" : "regional";
            for (var seed = 1; seed <= 128; seed++)
            {
                var first = World(seed, annex);
                Think(first.C, first.Us);
                Check(!Transferred(first) && first.C.UltimatumPapers.Count == 0 && first.C.Wars.Count == 0,
                    labels + " threat waits for preparation on opening seed " + seed);
                Check(first.Us.WarPlanTargetId == first.Them.Id && first.Us.WarPlanSinceDay == 0,
                    labels + " opening creates a real strategic plan on seed " + seed);

                Day(first.C, 90);
                Think(first.C, first.Us);
                var acted = Transferred(first);
                if (acted) committed++;

                var twin = World(seed, annex);
                Think(twin.C, twin.Us);
                Day(twin.C, 90);
                Think(twin.C, twin.Us);
                Check(Transferred(twin) == acted && CampaignFingerprint.Of(first.C) == CampaignFingerprint.Of(twin.C),
                    labels + " same seed reproduces the decision and state " + seed);
            }
            Check(committed > 0 && committed < 128,
                labels + " prepared threats vary across seeds: " + committed + "/128 commit on day 90");
        }

        // Force the commitment roll on these fixtures. Each refusal must come from the
        // live situation rather than a lucky non-action roll.
        Block("a current truce", world => CampaignDiplomacy.Get(world.C, 0, 1).TruceUntil = 500);
        Block("a non-aggression pact", world => CampaignDiplomacy.Get(world.C, 0, 1).NonAggressionPact = true);
        Block("a valued bilateral alliance", world =>
        {
            var bond = CampaignDiplomacy.Get(world.C, 0, 1);
            bond.Stance = DiplomaticStance.Allied;
            bond.Opinion = 100;
        });
        Block("a shared defence organisation", world => InternationalOrganizations.Seed(world.C,
            "Defence", "collective_defence", new[] { 0, 1 }));
        Block("an ongoing foreign war", world => world.C.Wars.Add(new StrategicWar
            { Id = 75, AttackerId = 0, DefenderId = 2, StartedDay = 90 }));
        Block("no army able to stage the threat", world => world.C.Armies.Clear());
        Block("insufficient domestic stability", world => world.Us.Stability = .1f);
        Block("insufficient war support", world => world.Us.WarSupport = .1f);
        Block("voluntary wars disabled", world => world.C.Defines.AiMayDeclareWar = false);
        Block("a government that cannot afford coercion", world =>
            { world.Us.Treasury = 0; world.Us.DebtCondition = DebtCondition.Default; });

        // The threshold and preparation are independently meaningful even if the act is
        // almost certain once the government is ready. No new magic opening-game lock.
        var cautious = World(4, false);
        cautious.C.Defines.AiWarDesireThreshold = 100;
        cautious.C.Defines.AiWarChance = 100;
        Day(cautious.C, 90);
        Think(cautious.C, cautious.Us);
        Check(!Transferred(cautious), "a merely positive appetite does not bypass the desire threshold");
        var quick = World(4, false);
        quick.C.Defines.AiWarPreparationDays = 0;
        quick.C.Defines.AiWarChance = 100;
        Think(quick.C, quick.Us);
        Check(Transferred(quick), "a mod can explicitly remove preparation without a hidden calendar lock");

        var human = World(4, false);
        human.Them.IsHuman = true;
        human.C.Defines.AiWarChance = 100;
        Think(human.C, human.Us);
        Day(human.C, 90);
        Think(human.C, human.Us);
        Check(!Transferred(human) && human.C.UltimatumPapers.Count(paper => paper.Pending) == 1,
            "a prepared AI still delivers a paper to a human instead of signing for them");
    }

    private static void Block(string label, Action<Fixture> change)
    {
        var world = World(4, false);
        world.C.Defines.AiWarChance = 100;
        world.Us.WarPlanTargetId = world.Them.Id;
        world.Us.WarPlanSinceDay = 0;
        Day(world.C, 90);
        change(world);
        Think(world.C, world.Us);
        Check(!Transferred(world) && world.C.UltimatumPapers.Count == 0,
            label + " blocks the AI ultimatum");
    }

    private static void Think(Campaign campaign, Nation nation)
    {
        // TickStages prepares this shared lookup before invoking the staggered cabinet.
        DailyTables.Invoke(null, new object[] { campaign });
        Warmaking.Invoke(null, new object[] { campaign, nation });
    }

    private static void Day(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    private static bool Transferred(Fixture world) =>
        world.C.ProvinceMap.ProvinceToNation[world.AskedProvince] == world.Us.MapIndex;

    private sealed record Fixture(Campaign C, Nation Us, Nation Them, int AskedProvince);

    private static Fixture World(int seed, bool annex)
    {
        var c = new Campaign(seed, new Defines { AiAnnexesWeakNeighbours = false });
        // This variant has no cultural claim: an openly hostile, aggressive ministry
        // may still contemplate coercion, but must prepare and choose its moment too.
        if (annex) c.Defines.AiHostilityAppetite = 2f;
        var us = new Nation { Id = 0, MapIndex = 1, Tag = "POL", Name = "Demander", Population = 2_000_000,
            Treasury = 100, Gdp = 80, Stability = .9f, WarSupport = .9f, CapitalStateMapIndex = 1,
            Government = "presidential_republic", Ideology = "nationalism" };
        var them = new Nation { Id = 1, MapIndex = 2, Tag = "BLR", Name = "Recipient", Population = 1_000_000,
            Treasury = 20, Gdp = .01f, Stability = .7f, WarSupport = .6f, CapitalStateMapIndex = 9 };
        var third = new Nation { Id = 2, MapIndex = 3, Tag = "LTU", Name = "Unrelated",
            Population = 1_000_000, Treasury = 20, Gdp = 1, Stability = .7f };
        c.Nations.AddRange(new[] { us, them, third });
        const int width = 8, height = 3;
        var regions = new ushort[width * height];
        var owners = new ushort[regions.Length];
        regions[9] = 1; owners[9] = 1;
        regions[10] = 2; owners[10] = 2;
        regions[11] = 9; owners[11] = 2;
        regions[14] = 4; owners[14] = 3;
        var map = ProvinceTopology.Build(regions, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(c, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(c, new float[map.ProvinceCount + 1]);
        CampaignDiplomacy.ForgetBorders();
        var asked = new ScenarioState { Id = "border", MapIndex = 2, NationMapIndex = 2, NationId = "BLR", Population = 400_000 };
        if (!annex) asked.PopulationGroups.Add(new PopulationGroup { Culture = "polish", Size = 400_000 });
        c.States.AddRange(new[]
        {
            new ScenarioState { Id = "home", MapIndex = 1, NationMapIndex = 1, NationId = "POL", Population = 2_000_000 },
            asked,
            new ScenarioState { Id = "capital", MapIndex = 9, NationMapIndex = 2, NationId = "BLR", Population = 600_000 },
            new ScenarioState { Id = "other", MapIndex = 4, NationMapIndex = 3, NationId = "LTU", Population = 1_000_000 }
        });
        var source = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 1);
        var target = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 2);
        us.CapitalProvinceId = source;
        them.CapitalProvinceId = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 9);
        c.Armies.Add(new Army { Id = 10, NationId = 0, ProvinceId = source, Divisions = 12, Personnel = 120_000,
            MaxPersonnel = 120_000, Strength = 1, Organisation = 1, Equipment = 1, SupplyLevel = 1 });
        CampaignDiplomacy.Get(c, 0, 1).Opinion = -80;
        return new(c, us, them, target);
    }
}

