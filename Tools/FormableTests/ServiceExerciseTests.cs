using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class ServiceExerciseTests
{
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS exercises: " + label); }

    public static void Run()
    {
        var c = new Campaign(20261018);
        var nation = c.AddNation(new Nation { Tag = "AAA", MapIndex = 501, Gdp = 100, Treasury = 10, Population = 1_000_000 });
        var other = c.AddNation(new Nation { Tag = "BBB", MapIndex = 502, Gdp = 100, Treasury = 10, Population = 1_000_000 });
        c.States.Add(new ScenarioState { Id = "exercise_a", MapIndex = 1001, NationId = "AAA", NationMapIndex = 501, Population = 1_000_000 });
        c.States.Add(new ScenarioState { Id = "exercise_b", MapIndex = 1002, NationId = "BBB", NationMapIndex = 502, Population = 1_000_000 });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002, 1001, 1002 },
            new ushort[] { 501, 502, 501, 502 }, 2, 2, _ => false));
        c.SupportFormations.Clear(); c.Fleets.Clear();
        var flight = DecisionCatalog.Find("flight_school").Value;
        var cruise = DecisionCatalog.Find("fleet_review").Value;
        Check(flight.Trigger is { IsEmpty: false } && cruise.Trigger is { IsEmpty: false },
            "both shipped service exercises declare actual eligibility");
        Check(DecisionCatalog.IsBlocked(c, nation, flight) && DecisionCatalog.IsBlocked(c, nation, cruise),
            "cash alone cannot buy air or naval experience");
        var groundType = SupportTypeCatalog.All.First(type => type.Kind == SupportKind.Ground);
        var airType = SupportTypeCatalog.All.First(type => type.Kind == SupportKind.Air);
        var wing = new SupportFormation { Id = 1, NationId = nation.Id, StateMapIndex = 1001, BaseProvinceId = 1, TypeId = groundType.Id };
        c.SupportFormations.Add(wing);
        Check(DecisionCatalog.IsBlocked(c, nation, flight), "ground artillery is not a flight school");
        wing.TypeId = airType.Id; wing.NationId = other.Id;
        Check(DecisionCatalog.IsBlocked(c, nation, flight), "another country's wing cannot qualify the issuer");
        wing.NationId = nation.Id; wing.Strength = .01f;
        Check(DecisionCatalog.IsBlocked(c, nation, flight), "a shattered wing must recover before exercising");
        wing.Strength = 1;
        Check(!DecisionCatalog.IsBlocked(c, nation, flight) && DecisionCatalog.BlockReason(c, nation, flight).Length == 0,
            "operational wing at its controlled base unlocks both eligibility APIs");
        c.ProvinceController[1] = (ushort)other.MapIndex;
        Check(DecisionCatalog.IsBlocked(c, nation, flight), "occupation of the base blocks the flight school immediately");
        c.ProvinceController[1] = (ushort)nation.MapIndex;
        wing.BaseProvinceId = 2;
        Check(DecisionCatalog.IsBlocked(c, nation, flight), "a stale base from another region cannot qualify");
        wing.BaseProvinceId = 1;
        c.SetFeature("aviation", false);
        Check(DecisionCatalog.IsBlocked(c, nation, flight), "disabled aviation cannot earn exercise experience");
        c.SetFeature("aviation", true);
        var fleet = new Fleet { Id = 1, NationId = nation.Id, Hulls = 3 };
        c.Fleets.Add(fleet);
        Check(!DecisionCatalog.IsBlocked(c, nation, cruise), "serviceable owned ships qualify a review");
        fleet.InAction = true;
        Check(DecisionCatalog.IsBlocked(c, nation, cruise), "ships fighting a battle cannot conduct a review");
        fleet.InAction = false; fleet.Carried.Add(123);
        Check(DecisionCatalog.IsBlocked(c, nation, cruise), "troop transports are not diverted into a review");
        fleet.Carried.Clear(); fleet.ReadyDay = 1;
        Check(DecisionCatalog.IsBlocked(c, nation, cruise), "a fleet still recovering cannot review early");
        fleet.ReadyDay = 0; fleet.Supply = 0;
        Check(DecisionCatalog.IsBlocked(c, nation, cruise), "an unsupplied fleet cannot earn cruise experience");
        fleet.Supply = 1; c.SetFeature("navy", false);
        Check(DecisionCatalog.IsBlocked(c, nation, cruise), "disabled navy cannot earn review experience");
        c.SetFeature("navy", true);
        var before = CampaignFingerprint.Of(c);
        Check(ScriptedValues.TryRead(c, nation, "available_air_formations", out var air) && air == 1 &&
              ScriptedValues.TryRead(c, nation, "available_fleet_hulls", out var hulls) && hulls == 3,
            "mod triggers read formations and hulls in documented units");
        Check(CampaignFingerprint.Of(c) == before, "reading service availability changes neither state nor RNG");
        var errors = new List<string>();
        using (var body = JsonDocument.Parse("""{"available_air_formations_set":100,"available_fleet_hulls":100}"""))
            ScriptedEffects.Parse(body.RootElement, "exercise fixture", errors);
        Check(errors.Count == 2 && ScriptedValues.IsReadOnly("available_air_formations") &&
              ScriptedValues.IsReadOnly("available_fleet_hulls"), "derived military counts cannot be minted by effects");
        var cash = nation.Treasury;
        new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = flight.Id }.Apply(c);
        Check(nation.AirExperience == 6 && MathF.Abs(nation.Treasury - (cash - DecisionCatalog.Cost(nation, flight))) < .0001f,
            "eligible flight exercise pays the ordinary price and grants only its declared experience");
        new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = flight.Id }.Apply(c);
        Check(nation.AirExperience == 6, "cooldown prevents repeated instant experience");
        new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = cruise.Id }.Apply(c);
        Check(nation.NavalExperience == 6 && DecisionCatalog.IsBlocked(c, nation, cruise),
            "eligible review goes through the same paid command and cooldown");
        nation.Treasury = 0;
        nation.DecisionAvailableDay.Clear();
        Check(DecisionCatalog.IsBlocked(c, nation, flight) && DecisionCatalog.IsBlocked(c, nation, cruise),
            "real forces do not waive treasury requirements");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            Check(Loc.Get(flight.Unavailable) != flight.Unavailable && Loc.Get(cruise.Unavailable) != cruise.Unavailable,
                "both specific refusal explanations have Russian translations");
        }
        finally { Loc.Language = language; }
    }
}
