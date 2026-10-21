using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

// A focused audit of the public conference/command contract as it exists on 2026-10-20.
// LIMIT checks deliberately record missing capabilities, not approval of that design.
// No Godot process, real campaign file, production build, or shared test Program is used.
internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
        Console.WriteLine("PASS " + label);
    }

    private sealed class Fixture
    {
        public readonly Campaign Campaign;
        public readonly StrategicWar War;
        public Nation Player => Campaign.Nations[0];
        public Nation EnemyLeader => Campaign.Nations[1];
        public Nation EnemyAlly => Campaign.Nations[2];
        public Nation OwnAlly => Campaign.Nations[3];
        public Nation Neutral => Campaign.Nations[4];

        public Fixture(bool includeVanishedCountry = false)
        {
            Campaign = new Campaign(20261020) { ScenarioId = "peace_conference_audit" };
            var tags = new[] { "PWA", "PLO", "PEA", "POA", "PNE" };
            var cultures = new[] { "english", "russian", "russian", "german", "spanish" };
            for (var i = 0; i < tags.Length; i++)
            {
                DemographyCatalog.Declare(tags[i], new[] { cultures[i] });
                Campaign.Nations.Add(new Nation
                {
                    Id = i, MapIndex = i + 1, Tag = tags[i], Name = "Audit country " + i,
                    Treasury = 30, Gdp = 100, DebtInitialized = true, PublicDebt = 10,
                    Population = i is 1 or 2 ? 200000 : 100000,
                    Stability = .8f, WarSupport = .8f
                });
            }
            if (includeVanishedCountry)
                Campaign.Nations.Add(new Nation
                {
                    Id = 5, MapIndex = 99, Tag = "RUS", Name = "Vanished historical country",
                    DeclaredCulture = "russian", Treasury = 0, Gdp = 0, Population = 0,
                    DebtInitialized = true
                });

            // One province per region: own 1; enemy leader 2/3; enemy ally 4/5;
            // own ally 6; neutral 7. Both enemies retain territory after one-region release.
            const int width = 10, height = 3;
            var regions = new ushort[width * height];
            var owners = new ushort[regions.Length];
            var ownerByRegion = new[] { 0, 1, 2, 2, 3, 3, 4, 5 };
            for (var region = 1; region <= 7; region++)
            {
                var owner = ownerByRegion[region];
                var state = new ScenarioState
                {
                    MapIndex = region, Id = "peace_audit_" + region, Name = "Audit region " + region,
                    NationMapIndex = owner, Population = 100000
                };
                state.PopulationGroups.Add(new PopulationGroup
                {
                    Culture = cultures[owner - 1], Religion = "orthodox", Size = 100000
                });
                state.CoreTags.Add(tags[owner - 1]);
                Campaign.States.Add(state);
                regions[width + region] = (ushort)region;
                owners[width + region] = (ushort)owner;
            }
            Campaign.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, width, height, _ => false));
            Campaign.ConfigureSinglePlayer(0);
            Campaign.Defines.PeaceWholeRegions = true;
            Campaign.Defines.PeaceCostPerProvince = .01f;
            SovereignFinance.NormalizeAccounts(Campaign);
            War = new StrategicWar
            {
                Id = 1, AttackerId = 0, DefenderId = 1, WarScore = 100,
                DefenderWarSupport = 0, StartedDay = -600
            };
            War.AttackerAllies.Add(3);
            War.DefenderAllies.Add(2);
            Campaign.Wars.Add(War);
        }

        public int Province(int region) => Enumerable.Range(1, Campaign.ProvinceMap.ProvinceCount)
            .Single(p => Campaign.ProvinceMap.ProvinceToState[p] == region);

        public PeaceDemand Demand(params int[] regions)
        {
            var demand = new PeaceDemand { WarId = War.Id, ProposerId = Player.Id };
            foreach (var region in regions) demand.Provinces.Add(Province(region));
            return demand;
        }
    }

    private static void SelectionAndReadOnlyPreview()
    {
        var f = new Fixture();
        var c = f.Campaign;
        var before = CampaignFingerprint.Of(c);
        var randomBefore = c.Random.State;
        var claimable = PeaceConference.Claimable(c, f.War, f.Player.Id).ToHashSet();
        Check(claimable.SetEquals(new[] { 2, 3, 4, 5 }.Select(f.Province)),
            "opposing leader and opposing ally land are selectable; friendly/neutral land is not");
        var demand = f.Demand(2, 4);
        var ledger = new PeaceLedger(c, f.War, f.Player.Id);
        Check(ledger.ProvincesPerState.Keys.OrderBy(x => x).SequenceEqual(new[] { 2, 3, 4, 5 }),
            "ledger surveys all enemy coalition regions");
        Check(ledger.Price(f.Province(4)).Cost > 0 && PeaceConference.BlockReason(c, f.War, demand) == "",
            "an affordable enemy-ally claim has a nonzero price and passes territorial validation");
        var rounded = PeaceConference.Round(c, f.War, demand);
        _ = ledger.Cost(rounded);
        Check(demand.Provinces.SetEquals(new[] { f.Province(2), f.Province(4) }) &&
              CampaignFingerprint.Of(c) == before && c.Random.State == randomBefore,
            "claim/ledger/round/validation preview preserves campaign fingerprint, RNG and draft");
        foreach (var region in new[] { 1, 6, 7 })
            Check(PeaceConference.BlockReason(c, f.War, f.Demand(region)).Length > 0,
                "own, own-ally or neutral region cannot be taken: " + region);
        Check(PeaceConference.Budget(c, f.War, f.OwnAlly.Id) == PeaceConference.Budget(c, f.War, f.Player.Id),
            "a coalition ally has the shared side budget, not an individual contribution budget");
    }

    private static void ActualBeneficiaryAndSignatories()
    {
        var f = new Fixture();
        var c = f.Campaign;
        c.SetProvinceController(f.Province(4), (ushort)f.OwnAlly.MapIndex);
        c.ProvinceOccupation[f.Province(4)] = 1;
        var command = WrittenPeaceCommand.From(f.Demand(2, 4));
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
              NetworkCommandCodec.TryDecode(type, json, out var replay) && replay is WrittenPeaceCommand,
            "actual written-peace command survives the public wire codec");
        NetworkCommandCodec.TryDecode(type, json, out var decoded);
        decoded.Apply(c);
        Check(((WrittenPeaceCommand)decoded).Delivered && f.War.Ended,
            "decoded command signs a legal AI-opponent settlement");
        Check(c.ProvinceMap.ProvinceToNation[f.Province(2)] == f.Player.MapIndex &&
              c.ProvinceMap.ProvinceToNation[f.Province(4)] == f.Player.MapIndex,
            "LIMIT: both enemy countries' ceded provinces go to proposer, including land held by own ally");
        Check(c.StatesOf(f.OwnAlly).Select(s => s.MapIndex).SequenceEqual(new[] { 6 }) &&
              c.ProvinceMap.ProvinceToNation[f.Province(7)] == f.Neutral.MapIndex,
            "own ally is not a reward recipient; neutral border remains untouched");
        Check(new[] { (0, 1), (0, 2), (3, 1), (3, 2) }.All(pair =>
                CampaignDiplomacy.Peek(c, pair.Item1, pair.Item2)?.TruceUntil > c.ElapsedDays),
            "one signed peace creates truces across the entire coalition pair set");

        f = new Fixture(); c = f.Campaign;
        var cash = c.Nations.Select(n => n.Treasury).ToArray();
        var terms = f.Demand(); terms.Reparations = true; terms.TradeConcessions = true;
        command = WrittenPeaceCommand.From(terms); command.Apply(c);
        Check(command.Delivered && c.Nations[0].Treasury > cash[0] && c.Nations[1].Treasury < cash[1] &&
              c.Nations.Skip(2).Select(n => n.Treasury).SequenceEqual(cash.Skip(2)),
            "LIMIT: reparations pay only proposer from enemy leader, not either ally");
        Check(CampaignDiplomacy.Peek(c, 0, 1)?.TradeAgreement == true &&
              CampaignDiplomacy.Peek(c, 3, 1)?.TradeAgreement != true &&
              CampaignDiplomacy.Peek(c, 0, 2)?.TradeAgreement != true,
            "LIMIT: trade concession has leader/proposer signatories, no selected ally beneficiary or payer");

        f = new Fixture();
        terms = f.Demand(); terms.Subjugate = "puppet";
        command = WrittenPeaceCommand.From(terms); command.Apply(f.Campaign);
        Check(command.Delivered && f.EnemyLeader.AnswersTo(f.Player.Id) && !f.EnemyAlly.IsSubject,
            "LIMIT: imposed subject relationship targets enemy leader, not enemy ally");
    }

    private static void HumanConsentGap()
    {
        foreach (var extraHuman in new[] { 2, 3 })
        {
            var f = new Fixture();
            f.Campaign.Nations[extraHuman].IsHuman = true;
            var demand = f.Demand(4);
            Check(PeaceConference.BlockReason(f.Campaign, f.War, demand) == "",
                "UI GAP: territorial verdict is empty with additional human coalition participant " + extraHuman);
            var before = CampaignFingerprint.Of(f.Campaign);
            var command = WrittenPeaceCommand.From(demand); command.Apply(f.Campaign);
            Check(!command.Delivered && !f.War.Ended && command.Note.Length > 0 &&
                  CampaignFingerprint.Of(f.Campaign) == before,
                "real command safely refuses that coalition despite signable preview: " + extraHuman);
        }

        var h = new Fixture(); h.EnemyLeader.IsHuman = true;
        var offer = h.Demand(4);
        Check(PeaceConference.BlockReason(h.Campaign, h.War, offer) == "",
            "UI GAP: a human enemy also receives the unconditional signable territorial verdict");
        var cmd = WrittenPeaceCommand.From(offer); cmd.Apply(h.Campaign);
        Check(cmd.Delivered && !h.War.Ended && h.War.PendingOffer is not null &&
              h.Campaign.ProvinceMap.ProvinceToNation[h.Province(4)] == h.EnemyAlly.MapIndex,
            "actual human-recipient command waits for an answer and transfers nothing early");
        Check(PeaceOffers.Answer(h.Campaign, h.War, h.EnemyLeader.Id, false) && !h.War.Ended,
            "human recipient can decline the supposedly will-sign settlement");
    }

    private static void IndependenceAndDemography()
    {
        var f = new Fixture();
        var c = f.Campaign;
        var enemyAllyRelease = f.Demand(4); enemyAllyRelease.ReleaseRegions = true;
        Check(Dismantlement.Refusal(c, f.EnemyAlly, new[] { 4 }, peace: true) == "",
            "enemy ally itself has a legal independent regional settlement");
        Check(PeaceConference.BlockReason(c, f.War, enemyAllyRelease).Length > 0,
            "LIMIT: conference cannot release that enemy-ally region because release parent is fixed to enemy leader");
        var release = f.Demand(2); release.ReleaseRegions = true;
        Check(PeaceConference.BlockReason(c, f.War, release) == "",
            "enemy leader can be compelled to release a whole connected region while retaining land");
        var inhabitants = c.States.Single(s => s.MapIndex == 2).PopulationGroups
            .Select(g => (g.Culture, g.Religion, g.Size)).ToArray();
        var totalCash = c.Nations.Sum(n => n.Treasury);
        var totalDebt = c.Nations.Sum(n => n.PublicDebt);
        var count = c.Nations.Count;
        var command = WrittenPeaceCommand.From(release); command.Apply(c);
        Check(command.Delivered && f.War.Ended && c.Nations.Count == count + 1 && f.War.AnnexedProvinces == 0,
            "independence creates exactly one new country, not an annexation reward");
        var born = c.Nations.Last();
        Check(born.Id != f.Player.Id && born.Id != f.OwnAlly.Id && born.Id != f.Neutral.Id &&
              c.ProvinceMap.ProvinceToNation[f.Province(2)] == born.MapIndex &&
              born.FoundedCultures.Contains("russian"),
            "LIMIT: recipient is an automatically founded cultural identity, not a chosen existing country");
        Check(c.StatesOf(born).SelectMany(s => s.PopulationGroups)
                  .Select(g => (g.Culture, g.Religion, g.Size)).SequenceEqual(inhabitants),
            "forced release preserves the actual residents' culture, religion and size");
        Check(Math.Abs(c.Nations.Sum(n => n.Treasury) - totalCash) < .001f &&
              Math.Abs(c.Nations.Sum(n => n.PublicDebt) - totalDebt) < .001f,
            "forced release conserves aggregate cash and public debt");
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "peace-audit"), "peace-audit");
        var restoredBorn = loaded.Success ? loaded.Campaign.NationByTag(born.Tag) : null;
        Check(restoredBorn is not null && loaded.Campaign.StatesOf(restoredBorn)
                  .SelectMany(s => s.PopulationGroups).Select(g => (g.Culture, g.Religion, g.Size)).SequenceEqual(inhabitants),
            "real save codec preserves new country and released inhabitants' culture/religion");

        f = new Fixture(); c = f.Campaign;
        var region = c.States.Single(s => s.MapIndex == 4);
        var foreign = PeaceConference.Price(c, f.War, f.Player.Id, f.Province(4)).Cost;
        region.PopulationGroups[0].Religion = "atheist";
        Check(PeaceConference.Price(c, f.War, f.Player.Id, f.Province(4)).Cost == foreign,
            "LIMIT: changing only regional religion does not change conference province price");
        region.PopulationGroups[0].Culture = "english";
        var kindred = PeaceConference.Price(c, f.War, f.Player.Id, f.Province(4));
        Check(kindred.Cost < foreign && kindred.Reason.Contains("national reunification", StringComparison.Ordinal),
            "culture does affect the existing national-reunification price discount");

        f = new Fixture(includeVanishedCountry: true); c = f.Campaign;
        var vanished = c.NationByTag("RUS");
        Check(vanished is not null && !c.HasOwnedLand(vanished),
            "historical-restoration fixture contains an existing but landless country record");
        release = f.Demand(2); release.ReleaseRegions = true;
        command = WrittenPeaceCommand.From(release); command.Apply(c);
        Check(command.Delivered && c.Nations.Count == 7 && !c.HasOwnedLand(vanished) &&
              c.Nations.Last().Tag != vanished.Tag && c.HasOwnedLand(c.Nations.Last()),
            "LIMIT: release creates a fresh identity instead of restoring the existing vanished culture-country tag");

        f = new Fixture(); c = f.Campaign;
        var first = c.States.Single(s => s.MapIndex == 2);
        var second = c.States.Single(s => s.MapIndex == 3);
        first.PopulationGroups.Clear();
        first.PopulationGroups.Add(new PopulationGroup { Culture = "russian", Size = 51000 });
        first.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = 49000 });
        second.Population = 90000; second.PopulationGroups.Clear();
        second.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = 90000 });
        Check(new[] { first, second }.SelectMany(s => s.PopulationGroups).GroupBy(g => g.Culture)
                  .OrderByDescending(g => g.Sum(p => p.Size)).First().Key == "english" &&
              Dismantlement.Culture(c, new[] { 2, 3 }) == "russian",
            "IDENTITY GAP: release culture follows largest region's plurality, not the selected population's aggregate majority");
    }

    public static int Main()
    {
        try
        {
            SelectionAndReadOnlyPreview();
            ActualBeneficiaryAndSignatories();
            HumanConsentGap();
            IndependenceAndDemography();
            Console.WriteLine($"PEACE CONFERENCE AUDIT: {_checks} checks passed; LIMIT/UI GAP labels describe current missing capabilities.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"PEACE CONFERENCE AUDIT failed after {_checks} passes: {error}");
            return 1;
        }
    }
}
