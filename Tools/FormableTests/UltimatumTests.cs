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

        var width = 20;
        var height = 4;
        var states = new ushort[width * height];
        var control = new ushort[width * height];
        var stateA = (ushort)home.MapIndex;
        var stateB = (ushort)demanded.MapIndex;
        var retained = campaign.States.First(s => s.NationMapIndex == target.MapIndex && s.MapIndex != stateB);
        var stateC = (ushort)retained.MapIndex;
        var last = campaign.States.First(s => s.MapIndex != stateA && s.MapIndex != stateB && s.MapIndex != stateC);
        var stateD = (ushort)last.MapIndex;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var left = x < 8;
            var cell = y * width + x;
            states[cell] = left ? stateA : x < 12 ? stateB : x < 16 ? stateC : stateD;
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
        retained.NationMapIndex = target.MapIndex;
        last.NationMapIndex = target.MapIndex;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var owner = map.ProvinceToState[province] != stateA ? (ushort)target.MapIndex : (ushort)demander.MapIndex;
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
        campaign.MarkMapChanged();
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
            Check(!CampaignDiplomacy.CanDeclareWar(c, demander.Id, target.Id, out _) &&
                !CampaignDiplomacy.CanDeclareWar(c, target.Id, demander.Id, out _),
                "accepted settlement protects both sides with a truce");
            var retainedState = c.States.First(s => s.NationMapIndex == target.MapIndex);
            Check(!Ultimatum.Accept(c, demander, target, retainedState.MapIndex).Accepted,
                "an ultimatum cannot annex the last remaining territory");
            Check(target.Stability < stability - 0.01f, "the target carries the humiliation");
            Check(demander.Notoriety > notoriety + 0.01f, "the world noticed the demand");
            var news = NewsFeed.Lines(c).First(l => l.Kind == "ultimatum_accepted");
            Check(c.Chronicle.Entries.Last().Subject.Length > 0 &&
                news.Text.StartsWith(target.Name, StringComparison.OrdinalIgnoreCase),
                "acceptance news names the yielding country and demanded region");
        }
        {
            var (c, map, demander, target, stateIdx, provinces) = Setup();
            Ultimatum.Refuse(c, demander, target, stateIdx);
            Check(Statecraft.CasusBelli(c, demander, target) == "ultimatum_refusal",
                "a refusal is the casus belli it looks like");
            Check(provinces.All(p => map.ProvinceToNation[p] == (ushort)target.MapIndex), "a refusal moved no border");
            Check(!CampaignDiplomacy.CanDeclareWar(c, demander.Id, target.Id, out _) &&
                !CampaignDiplomacy.CanDeclareWar(c, target.Id, demander.Id, out _), "refusal also blocks immediate war in both directions");
            c.IsPaused = false;
            c.Defines.HoursPerTick = 24;
            for (var day = 0; day < c.Defines.UltimatumRefusalTruceDays; day++) c.Tick();
            Check(CampaignDiplomacy.CanDeclareWar(c, demander.Id, target.Id, out _), "war becomes available when refusal cooldown expires");
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
            c.Armies.RemoveAll(a => a.NationId == target.Id);
            var answer = Ultimatum.Consider(c, demander, target, stateIdx);
            Check(answer == Ultimatum.Answer.Yield, "the powerless yield");
        }
        {
            var (c, map, demander, target, stateIdx, _) = Setup();
            // A giant facing a dwarf calls the bluff.
            demander.Population = 1;
            demander.Gdp = 0.01f;
            c.Armies.RemoveAll(a => a.NationId == demander.Id);
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
        {
            var (c, map, demander, target, stateIdx, provinces) = Setup();
            var relation = CampaignDiplomacy.Get(c, demander.Id, target.Id);
            relation.NonAggressionPact = true;
            var before = c.Chronicle.Entries.Count;
            new IssueUltimatumCommand { IssuerId = demander.Id, TargetId = target.Id, StateMapIndex = stateIdx }.Apply(c);
            Check(c.Chronicle.Entries.Count == before && provinces.All(p => map.ProvinceToNation[p] == target.MapIndex),
                "command cannot bypass a non-aggression pact");
            relation.NonAggressionPact = false;
            relation.TruceUntil = c.ElapsedDays + 30;
            new IssueUltimatumCommand { IssuerId = demander.Id, TargetId = target.Id, StateMapIndex = stateIdx }.Apply(c);
            Check(c.Chronicle.Entries.Count == before, "command respects a truce");
            Check(!Ultimatum.Accept(c, target, target, stateIdx).Accepted &&
                !Ultimatum.Refuse(c, target, target, stateIdx).Accepted,
                "self-demand creates neither a transfer nor a casus belli");
        }
        PackageTests();
    }

    private static void PackageTests()
    {
        {
            var (c, map, demander, target, first, _) = Setup();
            var regions = Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToNation[p] == target.MapIndex)
                .Select(p => (int)map.ProvinceToState[p]).Distinct().OrderBy(id => id).ToArray();
            Check(regions.Length == 3, "fixture leaves a third region after a two-region package");
            target.Treasury = 10;
            var relation = CampaignDiplomacy.Get(c, demander.Id, target.Id);
            if (relation.A == target.Id) relation.EmbargoByA = true; else relation.EmbargoByB = true;
            var cash = demander.Treasury;
            var demand = new Ultimatum.Demand { Regions = regions.Take(2).ToArray(), Reparations = 2,
                LiftEmbargo = true, NonAggression = true };
            var transfer = Enumerable.Range(1, map.ProvinceCount).Where(p => demand.Regions.Contains(map.ProvinceToState[p])).ToArray();
            var outcome = Ultimatum.Resolve(c, demander, target, demand, Ultimatum.Answer.Yield);
            Check(outcome.Accepted && transfer.All(p => map.ProvinceToNation[p] == demander.MapIndex),
                "accepted package transfers multiple regions together");
            Check(Math.Abs(target.Treasury - 8) < .001 && Math.Abs(demander.Treasury - cash - 2) < .001,
                "cash is conserved across a one-time payment");
            Check(!relation.EmbargoBy(target.Id) && relation.NonAggressionPact,
                "accepted package lifts their embargo and establishes a pact");
            Check(c.HasOwnedLand(target) && relation.TruceUntil == c.ElapsedDays + c.Defines.UltimatumSettlementTruceDays,
                "target survives the settlement with protected peace");
            var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, ""), "").Campaign;
            Check(restored is not null && CampaignDiplomacy.Get(restored, demander.Id, target.Id).TruceUntil == relation.TruceUntil &&
                CampaignDiplomacy.Get(restored, demander.Id, target.Id).NonAggressionPact,
                "save/network snapshot preserves the settlement and its truce");
        }
        {
            var (c, map, demander, target, first, _) = Setup();
            target.Treasury = 10;
            var cash = demander.Treasury;
            var relation = CampaignDiplomacy.Get(c, demander.Id, target.Id);
            if (relation.A == target.Id) relation.EmbargoByA = true; else relation.EmbargoByB = true;
            var regions = Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToNation[p] == target.MapIndex)
                .Select(p => (int)map.ProvinceToState[p]).Distinct().ToArray();
            var before = map.ProvinceToNation.ToArray();
            var all = new Ultimatum.Demand { Regions = regions, Reparations = 2, LiftEmbargo = true };
            Check(!Ultimatum.Resolve(c, demander, target, all, Ultimatum.Answer.Yield).Accepted &&
                before.SequenceEqual(map.ProvinceToNation) && target.Treasury == 10 && demander.Treasury == cash && relation.EmbargoBy(target.Id),
                "whole-country demand is rejected before ANY settlement mutation");
            var demand = new Ultimatum.Demand { Regions = regions.Take(2).ToArray(), Reparations = 2, LiftEmbargo = true, NonAggression = true };
            Check(Ultimatum.Resolve(c, demander, target, demand, Ultimatum.Answer.Refuse).Accepted &&
                before.SequenceEqual(map.ProvinceToNation) && target.Treasury == 10 && demander.Treasury == cash &&
                relation.EmbargoBy(target.Id) && !relation.NonAggressionPact, "refusal transfers none of the package terms");
        }
        {
            var (c, map, demander, target, first, provinces) = Setup();
            target.Treasury = 10;
            var before = map.ProvinceToNation.ToArray();
            Check(!Ultimatum.Validate(c, demander, target, new Ultimatum.Demand { Reparations = float.NaN }, out _) &&
                !Ultimatum.Validate(c, demander, target, new Ultimatum.Demand { Reparations = 6 }, out _),
                "invalid or excessive cash demands are rejected");
            c.SetProvinceController(provinces[0], (ushort)demander.MapIndex);
            Check(!Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { Regions = new[] { first }, Reparations = 1 },
                Ultimatum.Answer.Yield).Accepted && before.SequenceEqual(map.ProvinceToNation) && target.Treasury == 10,
                "occupied region rejects the entire package atomically");
            var command = new IssueUltimatumCommand { IssuerId = demander.Id, TargetId = target.Id,
                StateMapIndices = new[] { first, first + 1 }, Reparations = 2, LiftEmbargo = true, NonAggression = true };
            Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
                NetworkCommandCodec.TryDecode(type, json, out var decoded) && decoded is IssueUltimatumCommand copy &&
                copy.StateMapIndices.SequenceEqual(command.StateMapIndices) && copy.Reparations == 2 && copy.LiftEmbargo && copy.NonAggression,
                "replay/network command preserves all package fields");
        }
        {
            var (c, map, demander, target, first, _) = Setup();
            Check(c.HasOwnedLand(target), "land cache initially sees the target");
            StatePartition.Annex(c, Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToNation[p] == target.MapIndex).ToArray(), demander, false);
            Check(!c.HasOwnedLand(target) && !CampaignDiplomacy.CanDeclareWar(c, demander.Id, target.Id, out _) &&
                CampaignDiplomacy.DeclareWar(c, demander.Id, target.Id, first) is null,
                "a disappeared country cannot become a ghost-war target even without a truce");
        }
    }
}
