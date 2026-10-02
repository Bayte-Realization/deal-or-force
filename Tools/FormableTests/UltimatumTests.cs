using System;
using System.Linq;
using DealOrForce.Simulation;

static class UltimatumTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        Console.WriteLine("PASS ultimatum: " + message);
    }

    private static (Campaign c, ProvinceTopology map, Nation demander, Nation target, int stateMapIndex, int[] provinces) Setup()
    {
        // A synthetic map, not the shipped one: the real EarthData/raster pair carries a
        // compile stamp that belongs to whoever last ran MapDataCompiler, and this test
        // must not care whose turn it was. The demanded state is one the campaign's own
        // region list says the target holds, so CanDemand's ownership rule is exercised
        // against real state data.
        var campaign = Campaign.CreateBootstrap(7);
        var demander = campaign.NationByTag("KOR");
        campaign.ConfigureSinglePlayer(demander.Id);
        var target = campaign.NationByTag("PRK");
        var demanded = campaign.States.FirstOrDefault(s => s.NationMapIndex == target.MapIndex);
        Check(demanded is not null, "fixture: the target holds a state");
        var home = campaign.States.FirstOrDefault(s => s.NationMapIndex == demander.MapIndex);
        Check(home is not null, "fixture: the demander holds a state");

        var width = 16;
        var height = 4;
        var states = new ushort[width * height];
        var control = new ushort[width * height];
        var stateA = (ushort)home.MapIndex;
        var stateB = (ushort)demanded.MapIndex;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var left = x < width / 2;
            var cell = y * width + x;
            states[cell] = left ? stateA : stateB;
            control[cell] = left ? (ushort)demander.MapIndex : (ushort)target.MapIndex;
        }
        var map = ProvinceTopology.Build(states, control, width, height, _ => false);
        campaign.AttachProvinceTopology(map);
        // The 2024 ownership layer re-assigns provinces from history by id, and a synthetic
        // map's province ids collide with real ones; the attach also reconciles the region
        // list with the ground it found. The fixture, not the history layer, says who holds
        // what here - arrays and the region records both.
        home.NationMapIndex = demander.MapIndex;
        demanded.NationMapIndex = target.MapIndex;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var owner = map.ProvinceToState[province] == stateB ? (ushort)target.MapIndex : (ushort)demander.MapIndex;
            if (map.ProvinceToNation[province] != owner)
            {
                map.ProvinceToNation[province] = owner;
                campaign.SetProvinceController(province, owner);
            }
        }
        var provinces = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => map.ProvinceToState[p] == stateB && map.ProvinceToNation[p] == (ushort)target.MapIndex)
            .ToArray();
        Check(provinces.Length > 0, "fixture: target owns provinces in the demanded state");
        return (campaign, map, demander, target, stateB, provinces);
    }

    public static void Run()
    {
        {
            var (c, map, demander, target, stateIdx, provinces) = Setup();
            var stability = target.Stability;
            var notoriety = demander.Notoriety;
            var wars = c.Wars.Count(w => !w.Ended);
            var outcome = Ultimatum.Accept(c, demander, target, stateIdx);
            Check(outcome.Accepted, "a yielded region changes hands without a shot");
            Check(provinces.All(p => map.ProvinceToNation[p] == (ushort)demander.MapIndex), "the demanded provinces belong to the demander");
            Check(c.Wars.Count(w => !w.Ended) == wars, "no war was opened");
            Check(target.Stability < stability - 0.01f, "the target carries the humiliation");
            Check(demander.Notoriety > notoriety + 0.01f, "the world noticed the demand");
        }
        {
            var (c, map, demander, target, stateIdx, provinces) = Setup();
            Ultimatum.Refuse(c, demander, target, stateIdx);
            Check(Statecraft.CasusBelli(c, demander, target) == "ultimatum_refusal",
                "a refusal is the casus belli it looks like");
            Check(provinces.All(p => map.ProvinceToNation[p] == (ushort)target.MapIndex), "a refusal moved no border");
        }
        {
            var (c, map, demander, target, _, _) = Setup();
            var foreign = c.States.First(s => s.NationMapIndex != target.MapIndex && s.NationMapIndex != demander.MapIndex);
            var outcome = Ultimatum.Accept(c, demander, target, foreign.MapIndex);
            Check(!outcome.Accepted, "the target cannot yield a state it does not own");
            Check(Statecraft.CasusBelli(c, demander, target) != "ultimatum_refusal", "a refused demand over foreign land threatens nobody");
        }
        {
            var (c, map, demander, target, stateIdx, provinces) = Setup();
            // An unplayable dwarf facing a giant yields. The fixture's KOR/PRK are near
            // peers, so the balance is forced instead of hoping bootstrap values agree.
            target.Population = 1;
            target.Gdp = 0.01f;
            var answer = Ultimatum.Consider(c, demander, target, stateIdx);
            Check(answer == Ultimatum.Answer.Yield, "the powerless yield");
        }
        {
            var (c, map, demander, target, stateIdx, _) = Setup();
            // A giant facing a dwarf calls the bluff.
            demander.Population = 1;
            demander.Gdp = 0.01f;
            target.Population = 1_000_000;
            target.Gdp = 500f;
            var answer = Ultimatum.Consider(c, demander, target, stateIdx);
            Check(answer == Ultimatum.Answer.Refuse, "the powerful refuse");
        }
        {
            var (c, map, demander, target, stateIdx, _) = Setup();
            // Whatever it answers, the same demand asked twice gets the same answer.
            var again = Ultimatum.Consider(c, demander, target, stateIdx);
            Check(Ultimatum.Consider(c, demander, target, stateIdx) == again,
                "the answer is deterministic");
        }
    }
}
