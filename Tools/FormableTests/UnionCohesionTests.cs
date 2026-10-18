using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Whether a state that was proclaimed stays proclaimed.
///
/// Five things are worth a check, and each of them is a sentence the mechanic makes: a union of two
/// peoples starts short of whole; war, instability and radicalism take it out of a country while a
/// quiet year puts it back; below the floor a region actually leaves, through the secession system
/// rather than through a rule of this mechanic's own; and the state of mind reaches the country
/// through the ordinary modifier sources, so its trouble is visible in the numbers a player watches.
/// A country that has proclaimed nothing is not touched at all - that last one matters most, because
/// a mechanic that quietly moves every country in the world is a mechanic nobody can reason about.
/// </summary>
static class UnionCohesionTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS union: " + label);
    }

    static void SetDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    /// <summary>Run the tick forward one day at a time, the way the simulation does.</summary>
    static void Days(Campaign campaign, int from, int count)
    {
        for (var day = from; day < from + count; day++)
        {
            SetDay(campaign, day);
            UnionCohesion.Tick(campaign);
        }
    }

    static void Stir(Campaign campaign, Nation nation, float stability, float radicalism)
    {
        nation.Stability = stability;
        foreach (var state in campaign.StatesOf(nation)) state.Radicalism = radicalism;
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20260928);
        var china = campaign.NationByTag("CHN");
        Check(china is not null, "the scenario holds a country this can be tried on");
        Check(UnionCohesion.Sources(china).Any() == false,
            "a country that has proclaimed nothing has no union to hold together");

        var union = FormableNations.Find(campaign, "congo_reunited");
        Check(union is not null, "the catalogue holds a union of two countries");
        var start = UnionCohesion.Start(campaign, china, union);
        Check(start < 1f, "a union of two peoples starts short of whole: " + start);
        Check(Math.Abs(1f - start - campaign.Defines.UnionCohesionPerPeople) < 0.001f,
            "and it costs exactly one people's worth of what a claim costs");

        // Proclaim it, and watch a calm country put itself back together.
        china.FormedNationId = union.Id;
        china.FormedNationDay = 0;
        china.UnionCohesion = 0.5f;
        Stir(campaign, china, 0.9f, 0f);
        Days(campaign, campaign.Defines.UnionGraceDays + 1, 200);
        var recovered = china.UnionCohesion;
        Check(recovered > 0.5f, "a quiet, stable union recovers: 0.50 -> " + recovered.ToString("0.000"));

        // Then take it apart the way a country is actually taken apart: unrest, and no calm.
        Stir(campaign, china, 0.25f, 0.85f);
        Days(campaign, 400, 120);
        var worn = china.UnionCohesion;
        Check(worn < recovered, "instability and radicalism wear it down: " + worn.ToString("0.000"));

        // And below the floor it loses ground, through the secession machinery that was already
        // there. Which region goes is decision, and checked first: forcing the secession itself
        // needs a province map, and the bootstrap campaign - unlike a real one - has none.
        foreach (var state in campaign.StatesOf(china)) state.Radicalism = 0.1f;
        var angry = campaign.StatesOf(china)[3];
        angry.Radicalism = 0.95f;
        Check(ReferenceEquals(UnionCohesion.AngriestRegion(campaign, china), angry),
            "the region that leaves is the angriest one the union holds");
        if (campaign.ProvinceMap is null)
        {
            Check(SecessionEvents.Force(campaign, angry) is null,
                "and with no map in this campaign the ground change cannot be forced here at all");
        }
        else
        {
            var before = campaign.StatesOf(china).Length;
            china.UnionCohesion = 0.2f;
            china.UnionLastSecessionDay = -9999;
            SetDay(campaign, 700);
            UnionCohesion.Tick(campaign);
            var lost = before - campaign.StatesOf(china).Length;
            Check(lost > 0, "a union below the floor loses a region: held " + before + ", lost " + lost);
            Check(china.UnionCohesion > 0.2f, "and losing it settles the rest for a while: " +
                china.UnionCohesion.ToString("0.000"));
        }

        // The state of mind reaches the country through the ordinary sources, both ways.
        china.UnionCohesion = 0.2f;
        var trouble = UnionCohesion.Sources(china).SelectMany(source => source.Modifiers).ToArray();
        Check(trouble.Length > 0 && trouble.Where(kv => kv.Key == "radicalism_weekly").Any(kv => kv.Value > 0f),
            "a union in trouble feeds radicalism through the modifier sources");
        china.UnionCohesion = 0.95f;
        var held = UnionCohesion.Sources(china).SelectMany(source => source.Modifiers).ToArray();
        Check(held.Length > 0 && held.All(kv => kv.Value > 0f),
            "a union that holds gives something back");

        // An ordinary country is left completely alone.
        var japan = campaign.NationByTag("JPN");
        var cohesion = japan.UnionCohesion;
        Stir(campaign, japan, 0.2f, 0.9f);
        Days(campaign, 800, 60);
        Check(Math.Abs(japan.UnionCohesion - cohesion) < 0.0001f,
            "a country that proclaimed nothing is never touched");
    }

    /// <summary>
    /// The end of the chain, on a campaign that really has a map: a region leaving, ground changing
    /// hands. The bootstrap fixture carries no province map, so every other check in this file stops
    /// at the decision. This one builds the campaign the game itself plays, which is slower than the
    /// rest of the suite put together, and that is why it has a switch of its own.
    /// </summary>
    public static void RunOnMap()
    {
        var manifest = System.IO.Path.Combine(GameDataLoader.DataFolder(), "scenarios", "2024", "scenario.json");
        Check(System.IO.File.Exists(manifest), "the shipped scenario is where this check looks for it");
        var campaign = ScenarioCatalog.CreateBaseCampaign(20260928, manifest);
        if (campaign.ProvinceMap is null)
        {
            // Measured, not assumed: the province map is built from the runtime map data, and a
            // headless harness does not carry it - even the campaign the game itself plays comes
            // back without one here. The ground change is therefore not checkable in this suite at
            // all, and a weaker assertion under the same name would be worse than saying so.
            Console.WriteLine("SKIP union: the ground change needs the runtime province map, " +
                "which this harness does not carry - it is checked on a live campaign instead");
            return;
        }
        Check(true, "a campaign built from the shipped scenario carries its map");
        var china = campaign.NationByTag("CHN");
        Check(china is not null && campaign.StatesOf(china).Length > 4,
            "and the country this check uses holds more than one region");

        // Proclaimed, unhappy, and past its grace: exactly the state the mechanic acts on.
        china.FormedNationId = "congo_reunited";
        china.FormedNationDay = 0;
        china.UnionCohesion = 0.2f;
        china.UnionLastSecessionDay = -9999;
        Stir(campaign, china, 0.2f, 0.9f);
        var before = campaign.StatesOf(china).Length;
        SetDay(campaign, 200);
        UnionCohesion.Tick(campaign);
        var after = campaign.StatesOf(china).Length;
        Check(after < before, "a union below the floor really loses ground: " + before + " -> " + after);
        Check(china.UnionCohesion > 0.2f, "and losing it settles the rest: " + china.UnionCohesion.ToString("0.000"));
        Check(!string.IsNullOrEmpty(china.FormedNationId),
            "and the claim stays with the country: the same state can be proclaimed again");
        Check(campaign.Nations.Any(nation => nation.FormedNationHistory.Contains("congo_reunited")) == false,
            "while nothing pretends the proclamation never happened");
    }
}
