using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Regressions from WatafakDonbassy: mediation lock bypass and armyless client states erased at peace.</summary>
static class ConflictIntegrityTests
{
    static void Check(bool ok, string why)
    {
        if (!ok) throw new Exception("conflict integrity: " + why);
        Console.WriteLine("PASS conflict integrity: " + why);
    }

    static void Day(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);

    public static void Run()
    {
        ArmylessRepublic();
        Mediation();
        UndergroundGeography();
        LivingNames();
    }

    static void ArmylessRepublic()
    {
        var c = LandArrangementTests.Fixture();
        var holder = c.Nations[0];
        var parent = c.Nations[1];
        var region = c.States.First(s => s.MapIndex == 3003);
        Check(LandArrangementSystem.SendTroops(c, holder, region, out _), "presence is established");
        Day(c, c.Defines.PresenceReferendumDays + 20);
        var presence = c.LandArrangements.Single();
        Check(LandArrangementSystem.ProclaimRepublic(c, holder, presence, out _), "client republic is actually founded");
        var born = c.Nations[^1];
        var provinces = Reintegration.Everything(c, born).ToArray();
        Check(provinces.Length > 1 && !c.Armies.Any(a => a.NationId == born.Id), "new republic has territory, not yet a standing army");
        var before = CampaignFingerprint.Of(c);
        Factions.SettleDefenselessRebelRemnants(c);
        Check(c.HasOwnedLand(born) && born.Population > 0 && provinces.All(p => c.ProvinceMap.ProvinceToNation[p] == born.MapIndex),
            "zero divisions at peace do not give its territory to its former parent");
        Check(before == CampaignFingerprint.Of(c), "the peacetime remnant review does not mutate a legitimate client state");

        var war = new StrategicWar { Id = 77, AttackerId = parent.Id, DefenderId = born.Id, StartedDay = c.ElapsedDays };
        c.Wars.Add(war);
        Factions.SettleDefenselessRebelRemnants(c);
        Check(c.HasOwnedLand(born), "declaring war is not a substitute for capturing the republic");
        foreach (var p in provinces)
        {
            war.OriginalController[p] = (ushort)born.MapIndex;
            c.SetProvinceController(p, (ushort)holder.MapIndex);
        }
        Factions.SettleDefenselessRebelRemnants(c);
        Check(c.HasOwnedLand(born), "foreign protector control cannot be painted over by the parent");
        foreach (var p in provinces.Take(provinces.Length - 1)) c.SetProvinceController(p, (ushort)parent.MapIndex);
        Factions.SettleDefenselessRebelRemnants(c);
        Check(c.HasOwnedLand(born), "a surviving protected province prevents automatic total annexation");
        foreach (var p in provinces) c.SetProvinceController(p, (ushort)parent.MapIndex);
        war.Ended = true;
        Factions.SettleDefenselessRebelRemnants(c);
        Check(c.HasOwnedLand(born), "a ceasefire is not permission for a new annexation");
        war.Ended = false;
        Factions.SettleDefenselessRebelRemnants(c);
        Check(!c.HasOwnedLand(born) && provinces.All(p => c.ProvinceMap.ProvinceToNation[p] == parent.MapIndex),
            "an armyless rump actually captured in the active war can still be reintegrated");
    }

    static void Mediation()
    {
        var c = LandArrangementTests.Fixture();
        var war = new StrategicWar { Id = 8, AttackerId = 0, DefenderId = 1, StartedDay = 0,
            AiNegotiationUnlockDay = 1095, AttackerWarSupport = 1f, DefenderWarSupport = 1f,
            ClaimedStateMapIndex = -1 };
        c.Wars.Add(war);
        Day(c, 500);
        var before = CampaignFingerprint.Of(c);
        Check(!DiplomaticGestures.CanMediateArmistice(c, war), "third-party mediation honours the scenario's negotiation lock");
        Check(before == CampaignFingerprint.Of(c), "mediation preview is read-only");
        var mediator = new Nation { Id = c.Nations.Count, MapIndex = 800, Tag = "MED", Name = "Mediator",
            Population = 1_000_000, PoliticalPoints = 100f, Treasury = 50f };
        c.Nations.Add(mediator);
        CampaignDiplomacy.Get(c, mediator.Id, 0).Opinion = 50f;
        CampaignDiplomacy.Get(c, mediator.Id, 1).Opinion = 50f;
        Check(!DiplomaticGestures.Perform(c, mediator.Id, 1, "mediate") && !war.Ended,
            "the real mediation action cannot bypass the lock either");
        war.AiNegotiationUnlockDay = 0;
        Day(c, 20);
        Check(!DiplomaticGestures.CanMediateArmistice(c, war), "mediator cannot end a young war before the normal minimum");
        Day(c, 500);
        Check(DiplomaticGestures.CanMediateArmistice(c, war), "unlocked mature war with willing AI governments can still be mediated");
        war.PendingOfferFromId = 0;
        Check(!DiplomaticGestures.CanMediateArmistice(c, war), "mediation cannot overwrite pending terms");
        war.PendingOfferFromId = -1;
        c.Nations[1].IsHuman = true;
        Check(!DiplomaticGestures.CanMediateArmistice(c, war), "a human belligerent must decide, never be answered for");
        c.Nations[1].IsHuman = false;
        war.WarScore = 90f;
        Check(!DiplomaticGestures.CanMediateArmistice(c, war), "both governments must accept, not just the losing side");
        war.WarScore = 0f;
        mediator.DecisionAvailableDay.Clear();
        Check(DiplomaticGestures.Perform(c, mediator.Id, 1, "mediate") && war.Ended &&
            c.Chronicle.Entries.Any(e => e.Kind == "peace_mediated"), "genuinely accepted mediation still signs and records an armistice");
    }

    static void UndergroundGeography()
    {
        var c = LandArrangementTests.Fixture();
        var home = c.States.First(s => s.MapIndex == 3003);
        var abroad = c.States.First(s => s.MapIndex == 3001);
        var movement = new Nation { Id = c.Nations.Count, Tag = "UND", Name = "Local underground", MapIndex = 900,
            Movement = new MovementState { Id = MovementCatalog.UndergroundPrefix + "local", Exile = true, Landless = true,
                HeadquartersStateMapIndex = home.MapIndex } };
        c.Nations.Add(movement);
        var definition = new MovementDefinition { Id = movement.Movement.Id, OutreachKm = 40_000 };
        definition.Homeland.Add(home.Id);
        Check(MovementReach.SupportCeiling(c, movement, home, definition, 1000, 0) > 0f, "regional underground keeps its homeland audience");
        Check(MovementReach.SupportCeiling(c, movement, abroad, definition, 1000, 0) == 0f,
            "co-ethnic residents abroad do not become that region's separatist following");
        abroad.ParentMapIndex = home.MapIndex;
        Check(MovementReach.SupportCeiling(c, movement, abroad, definition, 1000, 0) > 0f,
            "a partition of the real homeland still counts as homeland");
    }

    static void LivingNames()
    {
        var c = LandArrangementTests.Fixture();
        var exile = new Nation { Id = c.Nations.Count, Tag = "EXL", MapIndex = 901, Name = "Donbas",
            FoundNameKind = "land", FoundNameId = "donbas", Population = 0,
            Movement = new MovementState { Id = MovementCatalog.UndergroundPrefix + "exl", Landless = true, Exile = true } };
        c.Nations.Add(exile);
        c.Fallen.Add(exile.Id);
        var before = CampaignFingerprint.Of(c);
        Check(NationNaming.NameAlreadyTaken(c, "Donbas"), "a living underground with zero census population reserves its regional identity");
        Check(NationNaming.NameAlreadyTaken(c, CountryNames.Resolve(exile, false)), "its full name is reserved too");
        Check(CampaignFingerprint.Of(c) == before, "checking a surviving actor's name is read-only");
        exile.Movement.Dissolved = true;
        Check(!NationNaming.NameAlreadyTaken(c, "Donbas"), "a dissolved actor with no land does not reserve a name forever");
    }
}
