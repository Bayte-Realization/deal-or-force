using System;
using System.Linq;
using DealOrForce.Simulation;

static class RegionReunionTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("region reunion: " + note); Console.WriteLine("PASS region reunion: " + note); }

    static Campaign Fixture()
    {
        var c = new Campaign(57) { ScenarioId = "region_reunion_fixture" };
        c.AddNation(new Nation { Tag = "RUA", MapIndex = 501, Population = 1000 });
        c.AddNation(new Nation { Tag = "RUB", MapIndex = 502, Population = 1000 });
        c.States.Add(new ScenarioState { Id = "original", Name = "Original", MapIndex = 3001,
            NationMapIndex = 501, NationId = "RUA", Population = 700 });
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)3001, 8192).ToArray(),
            Enumerable.Repeat((ushort)501, 8192).ToArray(), 128, 64, _ => false));
        Check(c.ProvinceMap.ProvinceCount > 4, "fixture has four separate pieces of ground");
        for (var i = 0; i < 3; i++)
        {
            var piece = new ScenarioState { Id = "shard" + i, Name = "Shard " + i, MapIndex = 4000 + i,
                ParentMapIndex = 3001, Population = 100, NationMapIndex = i == 0 ? 501 : 502,
                NationId = i == 0 ? "RUA" : "RUB" };
            c.States.Add(piece); c.ProvinceMap.ProvinceToState[i + 1] = (ushort)piece.MapIndex;
            c.ProvinceMap.ProvinceToNation[i + 1] = (ushort)piece.NationMapIndex;
            c.SetProvinceController(i + 1, (ushort)piece.NationMapIndex);
        }
        c.InvalidateStateIndex(); return c;
    }

    public static void Run()
    {
        var c = Fixture(); var root = c.States[0]; var child = c.States[1];
        root.Buildings[BuildingKind.Infrastructure] = 2; child.Buildings[BuildingKind.Infrastructure] = 1;
        child.WarDamage = .8f; child.Displaced["english"] = 17;
        child.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = 100 });
        c.Construction.Add(new ConstructionProject { StateMapIndex = 4000, ProgressDays = 3, Paid = 2 });
        c.Recruitment.Add(new RecruitmentOrder { StateMapIndex = 4000 });
        c.SupportFormations.Add(new SupportFormation { StateMapIndex = 4000, TargetStateMapIndex = 4000 });
        c.Armies.Add(new Army { Id = 1, NationId = c.Nations[0].Id, StateMapIndex = 4000, ProvinceId = 1, ObjectiveStateMapIndex = 4000 });
        var movement = new MovementState { HeadquartersStateMapIndex = 4000 };
        movement.Support[3001] = .2f; movement.Support[4000] = 1;
        movement.Infiltrated[3001] = 10; movement.Infiltrated[4000] = 20;
        c.Nations[0].Movement = movement;
        var owners = c.ProvinceMap.ProvinceToNation.ToArray(); var controllers = c.ProvinceController.ToArray();
        var rng = c.Random.State;
        Check(StatePartition.Reunify(c) == 2, "recovered shard joins home while same-owner foreign shards join each other");
        Check(c.States.Count == 2 && c.ProvinceMap.ProvinceToState[1] == 3001 && c.ProvinceMap.ProvinceToState[2] == 4001 &&
            c.ProvinceMap.ProvinceToState[3] == 4001, "each flag keeps its own surviving region identity");
        Check(c.States.Sum(s => s.Population) == 1000 && root.Population == 800 && root.Buildings[BuildingKind.Infrastructure] == 3 &&
            root.Displaced["english"] == 17 && Math.Abs(root.WarDamage - .1f) < .00001f, "people, buildings, displacement and weighted damage survive");
        Check(owners.SequenceEqual(c.ProvinceMap.ProvinceToNation) && controllers.SequenceEqual(c.ProvinceController) && rng == c.Random.State,
            "reunion changes no ownership, control or random state");
        Check(c.Construction[0].StateMapIndex == 3001 && c.Construction[0].ProgressDays == 3 && c.Construction[0].Paid == 2 &&
            c.Recruitment[0].StateMapIndex == 3001 && c.SupportFormations[0].StateMapIndex == 3001 &&
            c.SupportFormations[0].TargetStateMapIndex == 3001 && c.Armies[0].ObjectiveStateMapIndex == 3001,
            "ongoing construction, recruitment, bases and army objectives retain their progress and destination");
        Check(movement.HeadquartersStateMapIndex == 3001 && Math.Abs(movement.Support[3001] - .3f) < .00001f &&
            movement.Infiltrated[3001] == 30 && !movement.Support.ContainsKey(4000), "movement support is population-weighted, fighters are added, HQ is rebound");
        var hash = CampaignFingerprint.Of(c);
        Check(StatePartition.Reunify(c) == 0 && hash == CampaignFingerprint.Of(c), "second reconciliation is a strict no-op");
        c.ConfigureSinglePlayer(c.Nations[0].Id);
        var copy = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "reunion"), "reunion");
        Check(copy.Success && copy.Campaign.States.Count == 2 && copy.Campaign.Construction[0].StateMapIndex == 3001,
            "surviving identities and queues round-trip through real persistence: " + copy.Warning);
        StatePartition.Annex(c, new[] { 2, 3 }, c.Nations[0], false);
        Check(c.States.Count == 1 && c.States[0].MapIndex == 3001 && c.States[0].Population == 1000,
            "returning the remaining foreign piece restores the complete original region");

        c = Fixture(); c.States[0].AdministeredById = c.Nations[0].Id; c.States[1].AdministeredById = c.Nations[1].Id;
        StatePartition.Reunify(c);
        Check(c.States.Any(s => s.MapIndex == 4000), "two competing parallel administrations are not silently overwritten");
        c = Fixture(); child = c.States[1]; root = c.States[0];
        child.AdministeredById = c.Nations[1].Id; child.AdministeredSinceDay = 12;
        child.AdministrationTaken = 3; child.AdministrationExposed = true;
        var ownerBefore = c.ProvinceMap.ProvinceToNation.ToArray(); var controlBefore = c.ProvinceController.ToArray();
        Check(StatePartition.Reunify(c) == 2 && !c.States.Any(s => s.MapIndex == 4000),
            "a covert office no longer leaves a second official same-owner region on the map");
        Check(root.AdministeredById == c.Nations[1].Id && root.AdministrationProvinces.SequenceEqual(new[] { 1 }) &&
            Math.Abs(root.AdministrationPopulationShare - .125f) < .000001f && root.AdministrationTaken == 3 &&
            root.AdministeredSinceDay == 12 && root.AdministrationExposed,
            "offices retain only their former province, resident coverage, age, exposure and receipts");
        Check(ownerBefore.SequenceEqual(c.ProvinceMap.ProvinceToNation) && controlBefore.SequenceEqual(c.ProvinceController),
            "local office coverage transfers no sovereign land or occupation");
        c.ConfigureSinglePlayer(c.Nations[0].Id);
        copy = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "office-footprint"), "office-footprint");
        Check(copy.Success && copy.Campaign.States.Single(s => s.MapIndex == 3001).AdministrationProvinces.SequenceEqual(new[] { 1 }) &&
            copy.Campaign.States.Single(s => s.MapIndex == 3001).AdministrationPopulationShare == root.AdministrationPopulationShare,
            "partial office footprint and resident coverage survive real save/reload");
        hash = CampaignFingerprint.Of(c); root.AdministrationPopulationShare = .2f;
        Check(hash != CampaignFingerprint.Of(c), "partial office coverage participates in the campaign fingerprint");
        root.AdministrationPopulationShare = .125f;
        root.AdministrationProvinces.Add(2);
        Check(hash != CampaignFingerprint.Of(c), "local office province IDs participate in the campaign fingerprint");
        root.AdministrationProvinces.Remove(2);
        Check(StatePartition.Reunify(c) == 0 && CampaignFingerprint.Of(c) == hash,
            "office-aware reunion remains idempotent");
        var third = new Nation { Tag = "RUC", MapIndex = 503, Population = 1000 }; c.AddNation(third);
        StatePartition.Annex(c, new[] { 1 }, third, false);
        var officePiece = c.States.Single(s => s.MapIndex == c.ProvinceMap.ProvinceToState[1]);
        Check(officePiece.AdministeredById == c.Nations[1].Id && officePiece.AdministrationProvinces.SequenceEqual(new[] { 1 }) &&
            c.States.Where(s => s != officePiece).All(s => !s.AdministrationProvinces.Contains(1) && s.AdministeredById < 0),
            "a later political split follows the office's actual footprint instead of copying it into every piece");
        c = Fixture(); c.LandArrangements.Add(new LandArrangement { Regions = new() { 4000 } });
        StatePartition.Reunify(c);
        Check(c.States.Any(s => s.MapIndex == 4000), "a lease of one shard does not become a lease of the whole region");
        c = Fixture(); c.DealOffers.Add(new DealOffer { From = new Ultimatum.Side { Regions = new[] { 4000 } } });
        StatePartition.Reunify(c);
        Check(c.States.Any(s => s.MapIndex == 4000), "a pending paper retains the exact territory the recipient saw");
    }
}
