using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class UltimatumTermTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("ultimatum terms: " + label);
        System.Console.WriteLine("PASS ultimatum terms: " + label);
    }

    public static void Run()
    {
        Check(SaveSystem.FormatVersion == 2, "save format version stays 2");

        {
            var (c, map, demander, target, third) = World();
            var outcome = Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { Annex = true }, Ultimatum.Answer.Yield);
            Check(outcome.Accepted, "annexation yield is accepted");
            Check(OwnedBy(map, 2) == demander.MapIndex && OwnedBy(map, 3) == demander.MapIndex,
                "annexation yield moves every province the target held");
            Check(OwnedBy(map, 1) == demander.MapIndex && OwnedBy(map, 4) == third.MapIndex,
                "annexation leaves the demander and the third country");
            Check(target.CapitalStateMapIndex == 0 && target.CapitalProvinceId == 0 && target.GovernmentSeatProvinceId == 0,
                "the annexed country does not keep a capital");
            Check(c.Fallen.Contains(target.Id) && !c.HasOwnedLand(target), "the annexed country is no longer on the map");
        }
        {
            var (c, map, demander, target, _) = World();
            var before = Owners(map);
            var outcome = Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { Annex = true }, Ultimatum.Answer.Refuse);
            Check(outcome.Accepted && Owners(map).SequenceEqual(before), "annexation refusal moves no land");
            Check(!c.Fallen.Contains(target.Id) && target.CapitalStateMapIndex == 2, "refusal leaves the capital where it was");
            Check(Statecraft.CasusBelli(c, demander, target) == "ultimatum_refusal", "annexation refusal is a reason for war");
        }
        {
            var (c, map, demander, target, _) = World();
            target.IsHuman = true;
            var before = Owners(map);
            var delivered = Ultimatum.Deliver(c, demander, target, new Ultimatum.Demand { Annex = true });
            Check(delivered.Accepted && Owners(map).SequenceEqual(before) && c.UltimatumPapers.Count == 1,
                "an annexation paper arrives without moving land");
            c.UltimatumPapers.Single().ExpiresDay = c.ElapsedDays;
            Ultimatum.Tick(c);
            Check(Owners(map).SequenceEqual(before) && Statecraft.CasusBelli(c, demander, target) == "ultimatum_refusal",
                "silence on an annexation keeps the land and refuses");
        }
        {
            var (c, map, demander, target, _) = World();
            target.IsHuman = true;
            var before = Owners(map);
            var outcome = Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { Annex = true }, Ultimatum.Answer.Yield);
            Check(!outcome.Accepted && outcome.Note.Contains("Human-controlled") && Owners(map).SequenceEqual(before),
                "automatic resolve still refuses to annex a player");
        }
        {
            var (c, _, demander, target, _) = World();
            var foreign = new Ultimatum.Demand { Regions = new[] { 1 } };
            Check(!Ultimatum.ValidateTerms(c, demander, target, foreign, out var why) && why.Contains("do not hold"),
                "a region the target does not hold is refused before delivery: " + why);
            var delivered = Ultimatum.Deliver(c, demander, target, foreign);
            Check(!delivered.Accepted && c.UltimatumPapers.Count == 0, "that demand is not put on the desk");
        }
        {
            var (c, map, demander, target, _) = World();
            target.IsHuman = true;
            target.Gdp = 0f;
            target.Population = 1;
            var before = Owners(map);
            var asked = AiStatecraft.TryUltimatumFirst(c, demander, target);
            Check(!asked && Owners(map).SequenceEqual(before) && !c.Fallen.Contains(target.Id),
                "the AI does not annex a human");
        }
        {
            var (c, map, demander, target, third) = World();
            target.Gdp = 0f;
            target.Population = 1;
            Check(AiStatecraft.TryUltimatumFirst(c, demander, target), "a negligible government can be annexed");
            Check(OwnedBy(map, 2) == demander.MapIndex && OwnedBy(map, 4) == third.MapIndex && target.CapitalProvinceId == 0,
                "the AI annexes only that country");
        }
        {
            var (c, map, demander, target, third) = World();
            var bond = CampaignDiplomacy.Get(c, target.Id, third.Id);
            bond.Stance = DiplomaticStance.Allied;
            var before = Owners(map);
            var outcome = Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { BreakNamed = true, BreakWithId = third.Id }, Ultimatum.Answer.Yield);
            Check(outcome.Accepted && bond.Stance != DiplomaticStance.Allied, "breaking one alliance ends that alliance");
            Check(Owners(map).SequenceEqual(before) && !CampaignDiplomacy.Get(c, demander.Id, target.Id).NonAggressionPact,
                "the alliance term does not move land or sign a pact");
            Check(c.HasOwnedLand(target) && OwnedBy(map, 4) == third.MapIndex, "the third country keeps its land");
        }
        {
            var (c, map, demander, target, _) = World();
            var before = Owners(map);
            var outcome = Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { Vassalise = true, SubjectKind = "satellite" }, Ultimatum.Answer.Yield);
            Check(outcome.Accepted && target.AnswersTo(demander.Id) && target.SubjectType == "satellite",
                "a subject demand makes the named kind of subject");
            Check(Owners(map).SequenceEqual(before) && !c.Fallen.Contains(target.Id), "subjection does not annex the land");
        }
        {
            var (c, map, demander, target, third) = World();
            var war = new StrategicWar
            {
                Id = 7, AttackerId = demander.Id, DefenderId = target.Id, StartedDay = -400,
                AttackerWarSupport = 0.2f, DefenderWarSupport = 0.2f
            };
            for (var province = 1; province <= map.ProvinceCount; province++)
                war.OriginalController[province] = map.ProvinceToNation[province];
            var occupied = System.Linq.Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 2);
            c.SetProvinceController(occupied, (ushort)demander.MapIndex);
            c.Wars.Add(war);
            Check(Ultimatum.ValidateTerms(c, demander, target, new Ultimatum.Demand { WhitePeace = true }, out var peaceWhy),
                "white peace is a demand the peace writer will sign: " + peaceWhy);
            var outcome = Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { WhitePeace = true }, Ultimatum.Answer.Yield);
            Check(outcome.Accepted && war.Ended, "yielding white peace ends that war");
            Check(c.ProvinceController[occupied] == (ushort)target.MapIndex && OwnedBy(map, 4) == third.MapIndex,
                "white peace restores the pre-war line and does not take a third country's land");
            Check(OwnedBy(map, 2) == target.MapIndex, "white peace does not annex");
        }
        {
            var (c, map, demander, target, _) = World();
            target.WarSupport = 0.8f;
            var before = Owners(map);
            Ultimatum.Resolve(c, demander, target, new Ultimatum.Demand { Humiliate = true }, Ultimatum.Answer.Yield);
            Check(target.WarSupport < 0.8f && Owners(map).SequenceEqual(before), "humiliation hits war support and not the border");
        }
        {
            var options = new JsonSerializerOptions { IncludeFields = true };
            var paper = JsonSerializer.Deserialize<UltimatumPaper>(
                "{\"Id\":4,\"FromId\":0,\"ToId\":1,\"Regions\":[2],\"Reparations\":1.5,\"LiftEmbargo\":true}", options);
            Check(paper is { Annex: false, TradeAgreement: false, BreakNamed: false, WhitePeace: false, Liberate: false } &&
                paper.Regions.SequenceEqual(new[] { 2 }) && paper.Reparations == 1.5f && paper.LiftEmbargo,
                "an old paper without the new fields loads as the old ultimatum");
        }
        {
            var bare = World().c;
            var first = CampaignFingerprint.Of(bare);
            Check(bare.UltimatumPapers.Count == 0 && bare.NextUltimatumId == 1 && CampaignFingerprint.Of(bare) == first,
                "an empty ultimatum desk does not change the fingerprint by being read");
        }
    }

    static int OwnedBy(ProvinceTopology map, ushort region)
    {
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToState[province] == region) return map.ProvinceToNation[province];
        return -1;
    }

    static ushort[] Owners(ProvinceTopology map) => map.ProvinceToNation.ToArray();

    static (Campaign c, ProvinceTopology map, Nation demander, Nation target, Nation third) World()
    {
        var campaign = new Campaign(20261026);
        var demander = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "POL", Name = "Poland", Population = 100_000,
            Treasury = 40f, Gdp = 80f, IsHuman = false, CapitalStateMapIndex = 1, CapitalProvinceId = 0
        };
        var target = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "BLR", Name = "Belarus", Population = 80_000,
            Treasury = 20f, Gdp = 40f, IsHuman = false, CapitalStateMapIndex = 2,
            Stability = 0.7f, WarSupport = 0.6f
        };
        var third = new Nation
        {
            Id = 2, MapIndex = 3, Tag = "LTU", Name = "Lithuania", Population = 40_000,
            Treasury = 10f, Gdp = 20f, IsHuman = false, CapitalStateMapIndex = 4
        };
        campaign.Nations.Add(demander);
        campaign.Nations.Add(target);
        campaign.Nations.Add(third);
        const int width = 8, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        void Cell(int x, int y, ushort region, ushort owner)
        {
            states[y * width + x] = region;
            owners[y * width + x] = owner;
        }
        Cell(1, 1, 1, 1);
        Cell(2, 1, 2, 2);
        Cell(3, 1, 3, 2);
        Cell(5, 1, 4, 3);
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])map.ProvinceToNation.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[map.ProvinceCount + 1]);
        CampaignDiplomacy.ForgetBorders();
        campaign.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Id = "warsaw", Name = "Warsaw", Population = 40 });
        campaign.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 2, Id = "minsk", Name = "Minsk", Population = 30 });
        campaign.States.Add(new ScenarioState { MapIndex = 3, NationMapIndex = 2, Id = "brest", Name = "Brest", Population = 20 });
        campaign.States.Add(new ScenarioState { MapIndex = 4, NationMapIndex = 3, Id = "vilnius", Name = "Vilnius", Population = 20 });
        var capital = Enumerable.Range(1, map.ProvinceCount).First(p => map.ProvinceToState[p] == 2);
        target.CapitalProvinceId = capital;
        return (campaign, map, demander, target, third);
    }
}
