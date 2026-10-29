using System;
using System.Linq;
using DealOrForce.Simulation;

static class UltimatumPersistenceTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("FAIL ultimatum integration: " + text); Console.WriteLine("PASS ultimatum integration: " + text); }

    static (Campaign C, Nation From, Nation To) World(bool human = false)
    {
        var c = new Campaign(20261026);
        var from = c.AddNation(new Nation { Tag = "AAA", Name = "Demander", MapIndex = 501, Gdp = 100,
            Population = 1_000_000, Treasury = 100, OutputScale = 1, Stability = .8f });
        var to = c.AddNation(new Nation { Tag = "BBB", Name = "Target", MapIndex = 502, Gdp = 10,
            Population = 100_000, Treasury = 10, OutputScale = 1, Stability = .01f });
        c.States.Add(new ScenarioState { Id = "ult_from", Name = "Home", NationId = from.Tag,
            MapIndex = 1001, NationMapIndex = from.MapIndex, Population = from.Population });
        c.States.Add(new ScenarioState { Id = "ult_to", Name = "Target", NationId = to.Tag,
            MapIndex = 1002, NationMapIndex = to.MapIndex, Population = to.Population });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002, 1001, 1002 },
            new ushort[] { 501, 502, 501, 502 }, 2, 2, _ => false));
        c.Armies.Add(new Army { Id = 1, NationId = from.Id, Divisions = 100, Strength = 1, Organisation = 1, Equipment = 1 });
        c.Armies.Add(new Army { Id = 2, NationId = to.Id, Divisions = 1, Strength = 1, Organisation = 1, Equipment = 1 });
        CampaignDiplomacy.Get(c, from.Id, to.Id);
        if (human) c.SetPlayer(to.Id);
        SovereignFinance.NormalizeAccounts(c);
        EconomySystem.Explain(c);
        return (c, from, to);
    }

    public static void Run()
    {
        var fields = typeof(Ultimatum.Demand).GetProperties()
            .Where(property => property.PropertyType == typeof(bool) && property.Name is not ("LiftEmbargo" or "NonAggression"))
            .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var (c, from, to) = World(human: true);
        to.Technologies.Add(TechnologyCatalog.All.First().Id);
        var demand = new Ultimatum.Demand { TechnologyTransfer = true };
        var delivered = Ultimatum.Deliver(c, from, to, demand);
        Check(delivered.Accepted && c.UltimatumPapers.Count == 1,
            "extended demand reaches a human desk, never an automatic surrender: " + delivered.Note);
        var paper = c.UltimatumPapers.Single();
        Check(paper.Terms.TechnologyTransfer, "delivery keeps the technology-transfer term");
        Check(!ModifierSystem.Has(from, "ultimatum_technology"), "delivery alone grants nothing");
        var copy = Ultimatum.Copy(paper);
        Check(fields.All(field => Equals(typeof(Ultimatum.Demand).GetProperty(field).GetValue(copy.Terms),
            typeof(Ultimatum.Demand).GetProperty(field).GetValue(paper.Terms))) && copy.Regions.SequenceEqual(paper.Regions),
            "paper copy preserves extended terms");
        // Every independent bit must affect the hash, not merely the presence of any new term.
        foreach (var field in fields)
        {
            var member = typeof(UltimatumPaper).GetField(field);
            Check(member is not null, field + " is stored on the saved paper");
            var original = (bool)member.GetValue(paper);
            var hash = CampaignFingerprint.Of(c);
            member.SetValue(paper, !original);
            Check(CampaignFingerprint.Of(c) != hash, field + " participates in the network fingerprint");
            Check((bool)typeof(Ultimatum.Demand).GetProperty(field).GetValue(paper.Terms) == !original,
                field + " reaches the resolved package");
            Check((bool)member.GetValue(Ultimatum.Copy(paper)) == !original, field + " survives a non-default paper copy");
            member.SetValue(paper, original);
        }
        var fingerprint = CampaignFingerprint.Of(c);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "ultimatum-terms"), "ultimatum-terms");
        Check(saved.Success, "extended paper survives save and network import: " + saved.Warning);
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(saved.Campaign) == fingerprint && saved.Campaign.UltimatumPapers.Single().Terms.TechnologyTransfer,
            "roundtrip preserves both the pending demand and the authoritative fingerprint");
        var refused = new AnswerUltimatumCommand { IssuerId = to.Id, PaperId = paper.Id, Reply = Ultimatum.Answer.Refuse };
        refused.Apply(c);
        Check(refused.Outcome.Accepted && !ModifierSystem.Has(from, "ultimatum_technology"), "refusing a new term does not apply its reward");
        var paidHash = CampaignFingerprint.Of(c);
        new AnswerUltimatumCommand { IssuerId = to.Id, PaperId = paper.Id, Reply = Ultimatum.Answer.Yield }.Apply(c);
        Check(CampaignFingerprint.Of(c) == paidHash, "a closed paper cannot be replayed as acceptance");
        Annexation();
    }

    static void Annexation()
    {
        var (c, from, to) = World();
        to.PublicDebt = 7;
        to.FoundedCultures.Add("annexed_minority");
        from.StockpilesSeeded = to.StockpilesSeeded = true;
        from.Stockpiles["Arms"] = 2; to.Stockpiles["Arms"] = 3;
        var cash = from.Treasury + to.Treasury;
        var debt = from.PublicDebt + to.PublicDebt;
        var gdp = from.Gdp + to.Gdp;
        var population = c.States.Sum(s => s.Population);
        var order = new IssueUltimatumCommand { IssuerId = from.Id, TargetId = to.Id, Annex = true };
        order.Apply(c);
        Check(order.Result.Accepted && order.Response == Ultimatum.Answer.Yield,
            "actual annex-only command does not silently add a contradictory fallback region: " + order.Result.Note);
        Check(!c.HasOwnedLand(to) && c.Fallen.Contains(to.Id) && to.Population == 0,
            "accepted annexation closes the defeated country, not only its map colour");
        Check(Math.Abs(from.Treasury + to.Treasury - cash) < .0001f &&
            Math.Abs(from.PublicDebt + to.PublicDebt - debt) < .0001f && to.PublicDebt == 0,
            "annexation inherits cash and debt rather than erasing or duplicating the ledgers");
        Check(Math.Abs(from.Gdp + to.Gdp - gdp) < .0001f && from.Population == population,
            "annexation conserves output and population");
        Check(from.Stockpiles["Arms"] == 5 && to.Stockpiles.Count == 0,
            "existing stores pass exactly once to the successor");
        Check(c.Armies.All(a => a.NationId != to.Id) && to.CapitalProvinceId == 0 && to.CapitalStateMapIndex == 0,
            "armies and capital ownership follow the completed annexation");
        Check(!from.FoundedCultures.Contains("annexed_minority"), "coerced annexation does not silently proclaim a cultural union");
        var after = CampaignFingerprint.Of(c);
        order.Apply(c);
        Check(!order.Result.Accepted && CampaignFingerprint.Of(c) == after, "annexation cannot be repeated against an eliminated country");

        (c, from, to) = World();
        var foreign = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).First(p => c.ProvinceMap.ProvinceToNation[p] == to.MapIndex);
        c.SetProvinceController(foreign, (ushort)from.MapIndex);
        after = CampaignFingerprint.Of(c);
        var result = Ultimatum.Resolve(c, from, to, new Ultimatum.Demand { Annex = true }, Ultimatum.Answer.Yield);
        Check(!result.Accepted && CampaignFingerprint.Of(c) == after,
            "an ultimatum cannot steal occupied land or bypass an existing war settlement");

        (c, from, to) = World(human: true);
        var seats = c.HumanSeats.ToArray();
        var player = c.Player?.Id;
        result = Ultimatum.Deliver(c, from, to, new Ultimatum.Demand { Annex = true });
        Check(result.Accepted && c.HasOwnedLand(to), "an annexation addressed to a human waits for explicit consent");
        var answer = new AnswerUltimatumCommand { IssuerId = to.Id,
            PaperId = c.UltimatumPapers.Single().Id, Reply = Ultimatum.Answer.Yield };
        answer.Apply(c);
        Check(answer.Outcome.Accepted && !c.HasOwnedLand(to), "explicit human acceptance applies the same completed annexation");
        Check(c.HumanSeats.SequenceEqual(seats) && c.Player?.Id == player,
            "annexation does not reconfigure human seats or silently switch the active player");
    }
}
