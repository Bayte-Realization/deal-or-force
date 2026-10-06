using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MapAndPeaceRegressionTests
{
    private static void Check(bool pass, string label)
    { if (!pass) throw new Exception(label); Console.WriteLine("PASS map/peace: " + label); }

    public static void Run(ProvinceTopology source)
    {
        Anchors(source);
        RomanSuccession(source);
        DecisivePeace(source);
        Reach(source);
    }

    private static Campaign World(ProvinceTopology source)
    {
        var campaign = Campaign.CreateBootstrap(20261006);
        campaign.AttachProvinceTopology(source.CloneForOverrides());
        return campaign;
    }

    private static void Anchors(ProvinceTopology map)
    {
        var anchors = new ProvinceBoundaryAnchor(map);
        var tested = 0;
        foreach (var target in Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p)).Take(150))
        foreach (var origin in map.Neighbors[target].Where(p => !map.IsWater(p)).Take(2))
        {
            if (!anchors.TryFind(target, origin, out var point)) throw new Exception("No shared edge for " + target + ":" + origin);
            var x = (point.Longitude + 180) / 360 * map.Width;
            var y = (90 - point.Latitude) / 180 * map.Height;
            int Under(float dx, float dy) => map.CellToProvince[Math.Clamp((int)MathF.Floor(y + dy), 0, map.Height - 1) * map.Width +
                (((int)MathF.Floor(x + dx) % map.Width) + map.Width) % map.Width];
            var touching = new HashSet<int> { Under(-.04f, 0), Under(.04f, 0), Under(0, -.04f), Under(0, .04f) };
            if (!touching.Contains(target) || !touching.Contains(origin)) throw new Exception("Anchor is not on its shared border");
            if (!anchors.TryFind(target, origin, out var repeated) || repeated != point) throw new Exception("Unstable anchor");
            tested++;
        }
        Check(tested >= 100, $"{tested} real adjacent province anchors lie on both provinces and remain identical on reuse");
        Check(!anchors.TryFind(0, 1, out _) && !anchors.TryFind(1, 1, out _) && !anchors.TryFind(map.ProvinceCount + 1, 1, out _),
            "invalid and internal battle origins fail safely");
        var minsk = CityCatalog.All.First(c => c.Name == "Minsk");
        var province = CityCatalog.ProvinceUnder(map, minsk.Latitude, minsk.Longitude);
        Check(province > 0 && !map.IsWater(province), "Minsk is bound to land geometry, not a camera-dependent position");
        var adjusted = 0;
        foreach (var city in CityCatalog.All)
        {
            var point = CityCatalog.AnchorOnLand(map, city.Latitude, city.Longitude);
            if (point.ProvinceId == 0) continue; // tiny islands missing from a particular map stay at their real coordinates
            if (CityCatalog.ProvinceUnder(map, point.Latitude, point.Longitude) != point.ProvinceId)
                throw new Exception("City anchor disagrees with its raster province: " + city.Id);
            if (point.Latitude != city.Latitude || point.Longitude != city.Longitude) adjusted++;
        }
        Check(adjusted > 0, $"{adjusted} coastal raster positions corrected; every resolved city stays on its province");
    }

    private static void RomanSuccession(ProvinceTopology source)
    {
        var c = World(source);
        var east = FormableNations.Find("byzantium");
        var west = FormableNations.Find("western_roman_empire");
        var united = FormableNations.Find("roman_restoration");
        Check(new[] { east.Name, west.Name, united.Name }.Distinct().Count() == 3, "Roman successors have distinct names");
        foreach (var tag in new[] { "TUR", "ROU", "ESP" })
            Check(new[] { east, west, united }.All(d => FormableNations.Eligible(c.NationByTag(tag), d)), tag + " can pursue all three Roman paths");
        Check(east.RequiredGovernment.Length == 0 && west.RequiredTags.Except(east.RequiredTags).Any() &&
            united.RequiredTags.Except(west.RequiredTags).Any(), "republics are allowed and foundations have genuinely different territory");
        SpriteCatalog.Load();
        foreach (var (id, path) in new[] { ("roman_restoration", "flag_roman_empire_v2.png"),
                     ("byzantium", "flag_eastern_roman_empire_v2.png"), ("western_roman_empire", "flag_western_roman_empire.png") })
            Check(SpriteCatalog.TextureOf("flag_formable_" + id, "").EndsWith(path), "dedicated flag resolves for " + id);
        foreach (var w in c.Wars) w.Ended = true;
        var founder = c.NationByTag("TUR"); var partner = c.NationByTag("ESP");
        founder.Stability = partner.Stability = .95f; founder.Treasury = 10000;
        founder.FormedNationId = east.Id; founder.FormedNationHistory.Add(east.Id);
        CampaignDiplomacy.Get(c, founder.Id, partner.Id).Opinion = 100;
        Check(FormableNations.CompatibleProclamation(founder, united) &&
            PeacefulFormation.Refusal(c, founder, partner, united).Length == 0,
            "Eastern Rome may negotiate accession towards unified Rome");
        Check(PeacefulFormation.Accept(c, founder, partner, united) && PeacefulFormation.Consented(c, founder, partner, united),
            "compatible accession treaty is actually saved");
        founder.FormedNationHistory.Add(united.Id);
        Check(!PeacefulFormation.Accept(c, founder, partner, united), "a saved invitation cannot repeat a previous proclamation");
    }

    private static void DecisivePeace(ProvinceTopology source)
    {
        var c = World(source); var map = c.ProvinceMap;
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));
        var territory = Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToNation[p] == ukr.MapIndex).ToArray();
        foreach (var p in territory) { c.ProvinceController[p] = (ushort)rus.MapIndex; c.ProvinceOccupation[p] = 1; war.OriginalController[p] = (ushort)ukr.MapIndex; }
        war.WarScore = 100;
        var ledger = new PeaceLedger(c, war, rus.Id);
        c.Defines.PeaceCostPerProvince = .001f;
        var complete = new PeaceDemand { WarId = war.Id, ProposerId = rus.Id };
        Check(AiStatecraft.WriteDecisiveSettlement(c, rus, war, ledger, complete) && complete.Provinces.SetEquals(territory),
            "overwhelming victory chooses an affordable complete annexation, not a thin partial strip");
        Check(ledger.Cost(complete) <= ledger.Budget && PeaceConference.BlockReason(c, war, complete).Length == 0,
            "full settlement obeys real cost and ownership validation");
        c.Defines.PeaceCostPerProvince = 100;
        var dependent = new PeaceDemand { WarId = war.Id, ProposerId = rus.Id };
        Check(AiStatecraft.WriteDecisiveSettlement(c, rus, war, new PeaceLedger(c, war, rus.Id), dependent) &&
            dependent.Provinces.Count == 0 && SubjectCatalog.Knows(dependent.Subjugate),
            "unaffordable annexation is compared with a dependent-state settlement before extras");
        Check(PeaceConference.Resolve(c, war, dependent) && ukr.AnswersTo(rus.Id) && territory.All(p => map.ProvinceToNation[p] == ukr.MapIndex),
            "dependency is created through the real peace resolver without changing the subject's borders");
        war.Ended = false; war.WarScore = 20;
        Check(!AiStatecraft.WriteDecisiveSettlement(c, rus, war, new PeaceLedger(c, war, rus.Id), new()),
            "modest progress does not fabricate a decisive victory");
    }

    private static void Reach(ProvinceTopology source)
    {
        var c = World(source); Movements.Found(c);
        var hamas = c.NationByTag("HMS"); var malta = c.NationByTag("SMO");
        var h = Movements.DefinitionOf(hamas); var m = Movements.DefinitionOf(malta);
        var ukraine = c.States.First(s => s.Id == "ua_14");
        var gaza = c.States.First(s => s.Id == "ps_wbk");
        Check(!MovementReach.CanOperate(c, hamas, ukraine, h) && MovementReach.CanOperate(c, hamas, gaza, h),
            "Hamas has a homeland network, not automatic global operational reach");
        var faithOnly = c.States.First(s => MovementRules.PeopleOf(s, h, out var faithful) == 0 && faithful > 1000000 &&
            MovementReach.CanOperate(c, hamas, s, h));
        Check(MovementReach.SupportCeiling(c, hamas, faithOnly, h, 0, 1000000) == .04f,
            "shared religion alone cannot become a mass political mandate");
        hamas.Movement.Support[faithOnly.MapIndex] = .92f;
        Check(MovementReach.ResidentSupport(c, hamas, faithOnly) <= .004f,
            "regional influence is weighted by actual residents, not minority approval alone");
        var authorised = c.States.First(s => c.Nations.Any(n => n.MapIndex == s.NationMapIndex && malta.Movement.RecognisedBy.Contains(n.Id)));
        Check(MovementReach.CanOperate(c, malta, authorised, m), "recognised humanitarian partners can receive missions abroad");
        authorised.StandardOfLiving = 1; authorised.WarDamage = 0;
        Check(MovementActions.Refusal(c, malta, "hospital", authorised.MapIndex, -1).Length > 0,
            "healthy regions cannot be farmed for field-hospital prestige");
        var before = c.Random.State;
        for (var day = 1; day <= 28; day++)
        { typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24); MovementRules.Tick(c); }
        Check(!hamas.Movement.Support.ContainsKey(ukraine.MapIndex) &&
            hamas.Movement.Support.GetValueOrDefault(faithOnly.MapIndex) <= .04f,
            "weekly simulation removes impossible reach and repairs inflated legacy support");
        Check(c.Random.State == before, "geographical support does not consume the shared random stream");
    }
}
