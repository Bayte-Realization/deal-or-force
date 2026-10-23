using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DealOrForce.Simulation;

static class CivicCampaignTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS civic: " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(803);
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        var movement = c.NationByTag("SCV"); var parent = c.NationByTag("GBR");
        Check(movement?.Movement is not null && c.NationByTag("WCV") is not null && c.NationByTag("QCV") is not null,
            "three civic campaigns found from data");
        var definition = Movements.DefinitionOf(movement);
        var region = Movements.StateById(c, "gb_abd");
        Check(!definition.CanStageUprising && !MovementActions.For(movement).Any(k => k.Id == "rise"),
            "civic independence is not armed uprising");
        Check(CivilIndependence.AwarenessTarget(c, definition, movement.Movement, region, parent) > 0,
            "civic aspiration does not require poverty or exclude the parent's primary cultures");
        foreach (var id in definition.Homeland)
            if (Movements.StateById(c, id) is { } home) movement.Movement.Support[home.MapIndex] = .92f;
        Check(CivilIndependence.StandingTarget(c, movement) >= c.Defines.CivilReferendumLegitimacy,
            "a strong civic mandate can sustain referendum legitimacy without hundreds of foreign recognitions");
        c.ConfigureSinglePlayer(parent.Id);
        movement.Movement.Support[region.MapIndex] = .92f; movement.Movement.Legitimacy = .9f;
        Check(CivilIndependence.Refusal(c, movement, region).Length > 0, "a human parent must agree explicitly");
        Check(MovementDealings.Refusal(c, parent, movement, "authorise_referendum").Length == 0, "parent can permit a vote");
        new MovementDealingCommand { IssuerId = parent.Id, MovementId = movement.Id, Action = "authorise_referendum" }.Apply(c);
        Check(CivilIndependence.ParentConsents(c, movement, parent), "authorisation is an actual campaign command");
        var votes = CivilIndependence.ResidentSupport(movement, region);
        Check(votes > .5f, "majority uses all residents, not just a selected people's fraction");
        var secedingProvince = PeaceConference.ProvincesOfState(c.ProvinceMap, region.MapIndex).First();
        var garrison = new Army
        {
            Id = c.Armies.Select(a => a.Id).DefaultIfEmpty().Max() + 1,
            NationId = parent.Id, ProvinceId = secedingProvince, Divisions = 1f,
            Strength = .8f, Cohesion = .8f, SupplyLevel = .8f, Personnel = 12_000,
            MaxPersonnel = 15_000, TemplateId = "line_infantry"
        };
        c.Armies.Add(garrison);
        c.Defines.PeacefulSecessionDefectionShare = 1f;
        var armies = c.Armies.Count; var wars = c.Wars.Count;
        Check(CivilIndependence.Ratify(c, movement, region), "qualified agreed referendum transfers territory");
        Check(region.NationMapIndex == movement.MapIndex && c.StatesOf(parent).Length > 0 &&
            c.Armies.Count == armies && c.Wars.Count == wars, "peaceful transfer creates neither militia nor war and preserves parent");
        Check(garrison.NationId == movement.Id,
            "an agreed referendum carries the local garrison with the new country");
        // The question is Scotland's, not Aberdeen's: the whole authored homeland whose own
        // residents voted for it comes over in the same act.
        var homelandRegions = definition.Homeland.Select(id => Movements.StateById(c, id)).Where(r => r is not null).ToArray();
        var homelandTaken = homelandRegions.Count(r => r.NationMapIndex == movement.MapIndex);
        Check(homelandTaken == homelandRegions.Length && homelandTaken > 1,
            $"the agreed vote carries the whole Scottish homeland, not one region ({homelandTaken} of {homelandRegions.Length})");
        Check(CampaignDiplomacy.Get(c, parent.Id, movement.Id).TruceUntil > c.ElapsedDays,
            "settlement protects against immediate attack");
        var options = new JsonSerializerOptions { IncludeFields = true, PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate };
        var saved = JsonSerializer.Deserialize<MovementState>(JsonSerializer.Serialize(movement.Movement, options), options);
        Check(saved.ReadyOnDay.ContainsKey(CivilIndependence.ConsentKey(parent.Id)), "referendum consent survives serialization");
        // A diaspora is named for where it is, not for the homeland its people came from:
        // Scots in California found California. And a secession seated in one region is that
        // region, not the whole land its box sits in - the Highlands are Highland, and
        // calling the state founded there Scotland would be the second thing with that name.
        var diaspora = SecessionEvents.NameFor(c, "scottish", "us_ca", "California", parent);
        Check(diaspora.Contains("California", StringComparison.OrdinalIgnoreCase) &&
              !diaspora.Contains("Scotland", StringComparison.OrdinalIgnoreCase),
            "diaspora state uses a local name, not a remote homeland: " + diaspora);
        var highlands = SecessionEvents.NameFor(c, "scottish", "gb_hld", "Highlands", parent);
        Check(highlands.Contains("Highland", StringComparison.OrdinalIgnoreCase) &&
              !highlands.Contains("Scotland", StringComparison.OrdinalIgnoreCase),
            "existing state name is not duplicated: " + highlands);
        var war = new StrategicWar { CivilConflict = true, AiNegotiationUnlockDay = 540, WarScore = 95 };
        Check(CivilWarPersistence.NegotiationLocked(c, war), "civil conflict cannot bargain away its future on an early score spike");
        war.Id = c.Wars.Select(w => w.Id).DefaultIfEmpty().Max() + 1;
        war.AttackerId = movement.Id; war.DefenderId = parent.Id;
        war.AttackerLosses = 100;
        war.AttackerFront.Add(MilitiaCore.Seat(c.ProvinceMap, c.ProvinceController,
            (ushort)movement.MapIndex, (ushort)region.MapIndex).Province);
        c.Wars.Add(war);
        movement.ManpowerReserve = 100_000; movement.Treasury = 1;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 90L * 24);
        var reserveMen = movement.ManpowerReserve; var reserveMoney = movement.Treasury;
        CivilWarPersistence.Tick(c);
        Check(war.EscalationStage == 1 && c.Armies.Count == armies + 1 &&
            movement.ManpowerReserve == reserveMen - MilitiaCore.Weights.Formation &&
            Math.Abs(movement.Treasury - (reserveMoney - c.Defines.CivilWarReserveCost)) < .0001,
            "active civil war mobilises one paid AI reserve on owned ground");
        CivilWarPersistence.Tick(c);
        Check(c.Armies.Count == armies + 1, "same escalation stage cannot mint repeated reserves");
        Check(!c.Armies.Any(a => a.NationId == parent.Id && a.TemplateId == MilitiaCore.TemplateId),
            "reserve automation does not spend a human player's resources");
        var savedWar = JsonSerializer.Deserialize<StrategicWar>(JsonSerializer.Serialize(war, options), options);
        Check(savedWar.CivilConflict && savedWar.EscalationStage == 1 && savedWar.AiNegotiationUnlockDay == 540,
            "civil war duration and paid mobilisation stage survive serialization");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 540L * 24);
        Check(!CivilWarPersistence.NegotiationLocked(c, war) && CivilWarPersistence.StageDue(c, war) == 3,
            "negotiation lock expires and escalation is capped");
        c.SetFeature("militia", false);
        Check(!MilitiaCore.Found(c, movement, region), "militia switch also gates external founding calls");

        // Verify multi-region armed uprising liberates the movement's whole homeland zone of interest:
        c.SetFeature("militia", true);
        var cameroon = c.NationByTag("CMR");
        var ambazonia = c.NationByTag("ABZ");
        if (cameroon is not null && ambazonia?.Movement is not null)
        {
            var abzDef = Movements.DefinitionOf(ambazonia);
            var spark = Movements.StateById(c, "cm_nw");
            var second = Movements.StateById(c, "cm_sw");
            if (spark is not null && second is not null && spark.NationMapIndex == cameroon.MapIndex && second.NationMapIndex == cameroon.MapIndex)
            {
                ambazonia.Treasury = 1f;
                ambazonia.Movement.Cadres = 15000f;
                ambazonia.Movement.Support[spark.MapIndex] = 0.85f;
                var takenArmed = MovementRules.TakeZone(c, ambazonia, spark, cameroon);
                Check(takenArmed == 2, $"armed uprising liberated whole homeland zone of interest ({takenArmed} regions)");
                Check(spark.NationMapIndex == ambazonia.MapIndex && second.NationMapIndex == ambazonia.MapIndex,
                    "both Ambazonian homeland regions transferred to Ambazonia upon uprising");
                Check(c.Homelands.ContainsKey(ambazonia.Id) && c.Homelands[ambazonia.Id].Contains(spark.MapIndex) &&
                      c.Homelands[ambazonia.Id].Contains(second.MapIndex), "liberated regions recorded in nation homelands");
            }
        }
    }
}
