using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class FeatureTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL features: " + name);
        Console.WriteLine("PASS features: " + name);
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261002);
        var rus = c.NationByTag("RUS");
        c.ConfigureSinglePlayer(rus.Id);
        c.Defines.HoursPerTick = 24;
        Check(MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
            DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var map, out var mapError), "campaign map loads: " + mapError);
        c.AttachProvinceTopology(map);
        var wing = c.SupportFormations.First(f => f.NationId == rus.Id && SupportTypeCatalog.Get(f.TypeId).Kind == SupportKind.Air);
        var artillery = c.SupportFormations.First(f => f.NationId == rus.Id && SupportTypeCatalog.Get(f.TypeId).Kind == SupportKind.Ground);
        var count = c.SupportFormations.Count;
        Check(c.FeatureEnabled("aviation") && !c.FeatureEnabled("missing"), "defaults keep 2024; undeclared custom mechanic is off");
        Check(SupportTypeCatalog.Get("sam_brigade").Kind == SupportKind.AirDefence,
            "snake_case air_defence content is classified as air defence, not ground artillery");
        c.SetFeature("air_defence", false);
        Check(!SupportCore.Enabled(c, "sam_brigade") && SupportCore.Enabled(c, artillery.TypeId) && SupportCore.Enabled(c, wing.TypeId),
            "air defence switch is independent of artillery and aviation");
        c.SetFeature("air_defence", true);
        c.SetFeature("AVIATION", false);
        Check(!SupportCore.Enabled(c, wing.TypeId) && SupportCore.Enabled(c, artillery.TypeId), "aviation switch leaves artillery enabled");
        Check(!c.SupportByNation.Values.SelectMany(x => x).Any(f => f.Id == wing.Id), "disabled wing absent from combat reach index");
        Check(!SupportCore.InRange(c, wing, SupportTypeCatalog.Get(wing.TypeId), wing.BaseProvinceId), "dormant wing cannot contribute even with stale reach index");
        var mission = wing.Mission;
        var order = new SetSupportMissionCommand { IssuerId = rus.Id, FormationId = wing.Id, Mission = SupportCore.MissionAirSuperiority };
        order.Apply(c);
        Check(order.Refusal.Length > 0 && wing.Mission == mission, "disabled mission rejected authoritatively");
        var rebase = SupportCore.PreviewRebase(c, rus.Id, new[] { wing.Id }, wing.StateMapIndex);
        Check(rebase.Refusal.Length > 0, "disabled rebase rejected");
        Check(SupportCore.Validate(c, rus.Id, wing.StateMapIndex, wing.TypeId).Contains("disabled"), "disabled recruitment rejected before spending");
        wing.Strength = .6f;
        var exp = wing.Experience;
        var training = new SupportOrder { Id = -700, NationId = rus.Id, TypeId = wing.TypeId, StateMapIndex = wing.StateMapIndex,
            DurationDays = 30, TrainedDays = 5, LastProcessedDay = -1 };
        c.SupportOrders.Add(training);
        c.SupportExposure[wing.Id] = 100;
        SupportCore.Tick(c);
        Check(training.TrainedDays == 5 && wing.Strength == .6f && wing.Experience == exp, "disabled training, attrition, recovery and AI are suspended");
        Check(c.SupportFormations.Count == count, "disable does not delete saved forces");
        var fingerprint = CampaignFingerprint.Of(c);
        var errors = new List<string>();
        using var effectJson = JsonDocument.Parse("{\"feature.aviation_set\":1}");
        var effect = ScriptedEffects.Parse(effectJson.RootElement, "fixture", errors);
        var savedEffect = JsonSerializer.Deserialize<ScriptedEffects.Effect>(JsonSerializer.Serialize(effect));
        savedEffect.Apply(c, rus);
        Check(errors.Count == 0 && c.FeatureEnabled("aviation") && !effect.IsEmpty, "scripted enable effect survives serialization");
        Check(CampaignFingerprint.Of(c) != fingerprint, "feature state affects multiplayer fingerprint");
        c.IsPaused = false;
        c.Tick();
        Check(training.TrainedDays == 6, "training resumes without catching up dormant days");
        c.Defines.Features["experimental_air"] = false;
        using var typeJson = JsonDocument.Parse("{\"fighter_wing\":{\"required_feature\":\"experimental_air\"}}");
        SupportTypeCatalog.Merge(typeJson.RootElement);
        Check(!SupportCore.Enabled(c, "fighter_wing"), "custom required_feature gates modded type");
        c.SetFeature("experimental_air", true);
        Check(SupportCore.Enabled(c, "fighter_wing"), "custom feature can be enabled through campaign API");
        c.SetFeature("long_range_forces", false);
        Check(!SupportCore.Enabled(c, artillery.TypeId) && !SupportCore.Enabled(c, wing.TypeId), "master switch disables all support arms");
        c.SetFeature("long_range_forces", true);
        c.SetFeature("aviation", false);
        using var triggers = JsonDocument.Parse("{\"flight_ready\":{\"min_year\":2024,\"tags\":[\"RUS\"],\"min_technology.flight_fixture\":1},\"broken\":{\"oops\":1}}");
        ScriptLibrary.MergeTriggers(triggers.RootElement);
        c.Defines.FeatureUnlocks["aviation"] = "flight_ready";
        c.Defines.FeatureUnlocks["broken_unlock"] = "broken";
        c.EvaluateFeatureUnlocks();
        Check(!c.FeatureEnabled("aviation") && !c.FeatureEnabled("broken_unlock"), "missing technology and invalid unlock conditions fail closed");
        rus.Technologies.Add("flight_fixture");
        c.IsPaused = false;
        c.Tick();
        c.EvaluateFeatureUnlocks();
        Check(c.FeatureEnabled("aviation"), "deterministic named technology/year trigger unlocks mechanic");
        c.SetFeature("aviation", false);
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, ""), "").Campaign;
        Check(restored is not null && !restored.FeatureEnabled("aviation") && restored.FeatureEnabled("experimental_air") &&
              restored.Defines.FeatureUnlocks["aviation"] == "flight_ready", "save/network restore retains switches, overrides and unlock rules");
        Check(restored.SupportFormations.Count == c.SupportFormations.Count && restored.SupportOrders.Any(o => o.Id == training.Id), "dormant content survives save roundtrip");
        var merged = new Defines();
        MechanicFeatures.Merge(merged.Features, new Dictionary<string, bool> { ["aviation"] = false, ["my_custom"] = true });
        Check(!merged.Features["aviation"] && merged.Features["artillery"] && merged.Features["my_custom"], "defines patch merges per key and preserves unrelated mechanics");
        Check(!c.SetFeature("../bad", true), "invalid feature identifiers rejected");
        foreach (var flag in new[] { "navy", "research", "construction", "recruitment", "decisions", "events" })
        {
            c.SetFeature(flag, false);
            Check(!c.FeatureEnabled(flag), flag + " switch is independently disabled");
        }
        var researchBefore = rus.CurrentResearch;
        var technology = ResearchSystem.AvailableTo(rus).First();
        new SetResearchCommand { IssuerId = rus.Id, TechnologyId = technology.Id }.Apply(c);
        Check(rus.CurrentResearch == researchBefore, "disabled research command cannot change the chosen technology");
        var projects = c.Construction.Count;
        var treasury = rus.Treasury;
        new QueueConstructionCommand { IssuerId = rus.Id, StateMapIndex = c.States.First(s => s.NationMapIndex == rus.MapIndex).MapIndex,
            Building = BuildingKind.CivilianIndustry }.Apply(c);
        Check(c.Construction.Count == projects && rus.Treasury == treasury, "disabled construction creates no order and spends nothing");
        var fleet = c.Fleets.First(f => f.NationId == rus.Id);
        var fleetBefore = JsonSerializer.Serialize(fleet, new JsonSerializerOptions { IncludeFields = true });
        NavalOperations.Tick(c);
        Check(JsonSerializer.Serialize(fleet, new JsonSerializerOptions { IncludeFields = true }) == fleetBefore,
            "disabled naval tick leaves dormant fleets unchanged");
        Check(!MechanicFeatures.ScreenEnabled(c, "Research") && !MechanicFeatures.SystemEnabled(c, "ResearchSystem") &&
              !MechanicFeatures.Allows(c, new SetResearchCommand()), "navigation, system and command share the same feature gate");
        c.Defines.Features["custom_orders"] = false;
        c.Defines.FeatureCommands["SetArmyStanceCommand"] = "custom_orders";
        c.Defines.FeatureSystems["MilitaryOperations"] = "custom_orders";
        c.Defines.FeatureScreens["Military"] = "custom_orders";
        var army = c.Armies.First(a => a.NationId == rus.Id);
        var stance = army.Stance;
        new SetArmyStanceCommand { IssuerId = rus.Id, ArmyId = army.Id, Stance = ArmyStance.Cautious }.Apply(c);
        Check(army.Stance == stance && !MechanicFeatures.SystemEnabled(c, "MilitaryOperations") &&
              !MechanicFeatures.ScreenEnabled(c, "Military"), "custom defines gate existing commands, systems and screens");
        c.SetFeature("custom_orders", true);
        new SetArmyStanceCommand { IssuerId = rus.Id, ArmyId = army.Id, Stance = ArmyStance.Cautious }.Apply(c);
        Check(army.Stance == ArmyStance.Cautious, "custom command binding resumes without recreating divisions");
        var extendedSave = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, ""), "").Campaign;
        Check(extendedSave is not null && !extendedSave.FeatureEnabled("navy") &&
            extendedSave.Defines.FeatureCommands["SetArmyStanceCommand"] == "custom_orders" &&
            extendedSave.Defines.FeatureSystems["MilitaryOperations"] == "custom_orders" &&
            extendedSave.Defines.FeatureScreens["Military"] == "custom_orders", "extended feature bindings survive save/network snapshots");
        c.Defines.Features.Clear();
        c.FeatureOverrides.Clear();
        Check(c.FeatureEnabled("navy") && c.FeatureEnabled("research") && !c.FeatureEnabled("custom_orders"),
            "older feature tables preserve built-in defaults but never invent custom features");
        SupportTypeCatalog.ResetCatalog();
        ScriptLibrary.ResetCatalog();
    }
}
