using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

static class DomesticCrisesTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("domestic crises: " + name);
        Console.WriteLine("PASS domestic crises: " + name);
    }

    internal static void Day(Campaign c, int day) => typeof(Campaign)
        .GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(731);
        var nation = c.NationByTag("GBR");
        var region = c.StatesOf(nation).OrderByDescending(r => r.Population).First();
        foreach (var state in c.StatesOf(nation))
        { state.Literacy = .98f; state.Urbanization = .85f; state.Radicalism = .01f; }
        nation.Stability = .9f;
        c.Wars.Clear();
        var before = CampaignFingerprint.Of(c);
        var view = DomesticCrises.Read(c, nation);
        Check(view.Awareness > .8f && view.Radicalism < .02f,
            "aware and peaceful people are not classified as radical");
        Check(before == CampaignFingerprint.Of(c) && region.ResidentAwareness < 0f,
            "opening crisis gauges does not mutate old-save state");
        var baselinePressure = view.CoupTarget;
        foreach (var state in c.StatesOf(nation)) { state.Literacy = .1f; state.Urbanization = .1f; }
        Check(DomesticCrises.Read(c, nation).CoupTarget == baselinePressure,
            "education and civic consciousness do not feed violent institutional pressure");
        foreach (var state in c.StatesOf(nation)) { state.Literacy = .98f; state.Urbanization = .85f; }
        DomesticCrises.Tick(c);
        var awareness = region.ResidentAwareness;
        region.Literacy = 0f; region.Urbanization = 0f; region.Radicalism = 1f;
        Day(c, 1); DomesticCrises.Tick(c);
        Check(region.ResidentAwareness >= awareness - c.Defines.ResidentAwarenessChangePerDay - .00001f,
            "awareness is remembered and does not vanish after one literacy shock");
        Check(region.ResidentAwareness > .8f, "anger does not erase civic awareness");
        var atOne = region.ResidentAwareness;
        DomesticCrises.Tick(c);
        Check(region.ResidentAwareness == atOne, "a repeated daily call cannot double-apply awareness drift");
        Day(c, 2);
        c.SetFeature("resident_awareness", false);
        DomesticCrises.Tick(c);
        Check(region.ResidentAwareness == atOne, "disabled awareness freezes its persisted value");
        c.SetFeature("resident_awareness", true);
        var options = new JsonSerializerOptions { IncludeFields = true };
        nation.DomesticCrisis.CoupPressure = .61f;
        nation.DomesticCrisis.DaysAboveThreshold = 13;
        var saved = JsonSerializer.Deserialize<Nation>(JsonSerializer.Serialize(nation, options), options);
        Check(saved.DomesticCrisis.CoupPressure == .61f && saved.DomesticCrisis.DaysAboveThreshold == 13,
            "crisis memory is saved with the country");
        var old = JsonSerializer.Deserialize<ScenarioState>("{\"MapIndex\":23}", options);
        Check(old.ResidentAwareness == -1f && ResidentAwareness.Of(c, old) >= 0f,
            "old saves derive initial awareness without pretending residents are unaware");
        var fingerprint = CampaignFingerprint.Of(c);
        region.ResidentAwareness = .45f;
        Check(CampaignFingerprint.Of(c) != fingerprint, "civic memory participates in multiplayer desync detection");
        CoalitionCoupConditions();
        CoupSystemsAgree();
        PoliticsCache();
        CoupProgression();
        Responses();
        Profile();
    }

    static void CoalitionCoupConditions()
    {
        var c = Campaign.CreateBootstrap(887);
        c.Wars.Clear();
        var n = c.NationByTag("GBR");
        n.Government = "parliamentary_republic"; n.Stability = .3f;
        var war = new StrategicWar { AttackerId = c.NationByTag("FRA").Id, DefenderId = c.NationByTag("RUS").Id,
            WarScore = 60f };
        war.AttackerAllies.Add(n.Id);
        c.Wars.Add(war);
        var coup = GovernanceEvents.All.Single(t => t.Id == "military_coup");
        Check(!GovernanceEvents.Eligible(c, n, coup) && !DomesticCrises.Read(c, n).Drivers.Contains("War defeat"),
            "an attacking coalition ally does not mistake victory for defeat");
        war.WarScore = -60f;
        Check(GovernanceEvents.Eligible(c, n, coup) && DomesticCrises.Read(c, n).Drivers.Contains("War defeat"),
            "the same coalition ally sees genuine defeat as political pressure");
    }

    static void CoupSystemsAgree()
    {
        var c = Campaign.CreateBootstrap(889);
        c.Wars.Clear();
        var n = c.NationByTag("GBR");
        n.Government = "parliamentary_republic"; n.Stability = .05f;
        foreach (var r in c.StatesOf(n)) r.Radicalism = 1f;
        n.DomesticCrisis.CoupPressure = .9f;
        n.DomesticCrisis.DaysAboveThreshold = c.Defines.CoupRequiredPressureDays;
        n.CoupPlotDay = 0;
        Coups.GovernmentRisk(n);
        Coups.Seize(c, n);
        Check(n.DomesticCrisis.DaysAboveThreshold == 0 && n.DomesticCrisis.LastOutcomeDay == c.ElapsedDays &&
            n.DomesticCrisis.CooldownUntilDay > c.ElapsedDays,
            "military and institutional coups share aftermath and clear the other pending threat");
        n.Government = "parliamentary_republic"; n.PoliticsChangedDay = -9999; n.Stability = .05f;
        n.DomesticCrisis.CoupPressure = .9f;
        n.DomesticCrisis.DaysAboveThreshold = c.Defines.CoupRequiredPressureDays;
        Day(c, 1); DomesticCrises.Tick(c);
        Check(n.Government == "parliamentary_republic", "a second coup route cannot bypass the same aftermath");
        n.LastCoupDay = -99999;
        Check(Coups.Chance(c, n) == 0f, "institutional aftermath also blocks a new military plot");
        n.DomesticCrisis.CooldownUntilDay = -1;
        c.SetFeature("coups", false);
        Check(!DomesticCrises.Read(c, n).CoupEligible, "the coup feature disables both routes to a coup");
    }

    static void CoupProgression()
    {
        var c = Campaign.CreateBootstrap(883);
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        c.Wars.Clear();
        var nation = c.NationByTag("GBR");
        c.ConfigureSinglePlayer(nation.Id);
        nation.Government = "parliamentary_republic";
        nation.Stability = .05f;
        foreach (var region in c.StatesOf(nation)) region.Radicalism = 1f;
        var capital = c.StatesOf(nation).First();
        nation.CapitalStateMapIndex = capital.MapIndex;
        var read = DomesticCrises.Read(c, nation);
        Check(read.CoupTarget >= c.Defines.CoupPressureThreshold && read.CoupEligible,
            "institutional collapse exposes a real eligible coup, including a human country");
        DomesticCrises.Tick(c);
        Check(nation.Government == "parliamentary_republic" && nation.DomesticCrisis.CoupPressure > 0f,
            "one bad day accumulates strain rather than replacing the government");
        nation.DomesticCrisis.CoupPressure = .79f;
        nation.DomesticCrisis.DaysAboveThreshold = c.Defines.CoupRequiredPressureDays - 2;
        NationalModifiers.Invalidate(nation);
        var taxEfficiency = NationalModifiers.Of(nation, "tax_efficiency");
        Day(c, 1); DomesticCrises.Tick(c);
        Check(nation.Government == "parliamentary_republic",
            "crossing the gauge still requires the full sustained warning period");
        Day(c, 2); DomesticCrises.Tick(c);
        Check(nation.Government == "military_junta" && nation.DomesticCrisis.LastOutcomeId == "quiet_coup",
            "sustained pressure applies the data-authored coup through real politics");
        var taxChange = GovernmentCatalog.Of("military_junta").Modifiers.GetValueOrDefault("tax_efficiency") -
            GovernmentCatalog.Of("parliamentary_republic").Modifiers.GetValueOrDefault("tax_efficiency");
        Check(Math.Abs(NationalModifiers.Of(nation, "tax_efficiency") - (taxEfficiency + taxChange)) < .00001f,
            "new government modifiers are applied immediately rather than showing the old cabinet's bonuses");
        Check(nation.DomesticCrisis.CooldownUntilDay == c.ElapsedDays + Math.Max(c.Defines.CoupGraceDays, c.Defines.CoupAftermathCooldownDays) &&
              nation.DomesticCrisis.DaysAboveThreshold == 0,
            "a completed coup starts aftermath cooldown rather than recurring daily");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "institutional_coup"), "a coup is a visible world event");
        var snapshot = SaveSystem.ExportNetworkSnapshot(c, "domestic-crises");
        var restored = SaveSystem.ImportNetworkSnapshot(snapshot, "domestic-crises");
        Check(restored.Success && restored.Campaign.NationById(nation.Id).DomesticCrisis.LastOutcomeId == "quiet_coup",
            "actual campaign snapshot restores the outcome and cooldown");
        nation.Government = "parliamentary_republic";
        nation.PoliticsChangedDay = -9999;
        nation.DomesticCrisis.CooldownUntilDay = -1;
        nation.DomesticCrisis.CoupPressure = .81f;
        nation.DomesticCrisis.DaysAboveThreshold = c.Defines.CoupRequiredPressureDays;
        c.SetFeature("domestic_crises", false);
        Day(c, 3); DomesticCrises.Tick(c);
        Check(nation.Government == "parliamentary_republic" && nation.DomesticCrisis.CoupPressure == .81f,
            "disabled institutional crises preserve dormant progress without firing");
        c.SetFeature("domestic_crises", true);
        nation.Stability = .95f;
        foreach (var region in c.StatesOf(nation)) region.Radicalism = .01f;
        Day(c, 4); DomesticCrises.Tick(c);
        Check(nation.DomesticCrisis.CoupPressure < .81f && nation.DomesticCrisis.DaysAboveThreshold == 0,
            "recovery cancels a stale plot even before the pressure gauge fully drains");
        PartitionMemory(c, map, nation);
    }

    static void PartitionMemory(Campaign c, ProvinceTopology map, Nation owner)
    {
        var region = c.StatesOf(owner).First(r => Enumerable.Range(1, map.ProvinceCount)
            .Count(p => map.ProvinceToState[p] == r.MapIndex && !map.IsWater(p)) > 2);
        region.ResidentAwareness = .91f;
        var province = Enumerable.Range(1, map.ProvinceCount)
            .First(p => map.ProvinceToState[p] == region.MapIndex && !map.IsWater(p));
        var carved = StatePartition.Annex(c, new[] { province }, c.NationByTag("FRA"), false);
        Check(carved.Count > 0 && carved.All(r => r.ResidentAwareness == .91f),
            "a partial province peace carries resident awareness across the new border");
        region.ResidentAwareness = .85f;
        foreach (var child in carved) child.ResidentAwareness = .25f;
        var population = region.Population + carved.Sum(r => r.Population);
        var expected = (region.Population * .85 + carved.Sum(r => r.Population * .25)) / population;
        foreach (var p in Enumerable.Range(1, map.ProvinceCount))
            if (map.ProvinceToState[p] == region.MapIndex || carved.Any(r => r.MapIndex == map.ProvinceToState[p]))
                map.ProvinceToNation[p] = (ushort)owner.MapIndex;
        Check(StatePartition.Reunify(c) > 0 && Math.Abs(region.ResidentAwareness - expected) < .00001,
            "reunification weighs civic memory by residents instead of resetting or choosing one side");
    }

    static void PoliticsCache()
    {
        var c = Campaign.CreateBootstrap(886);
        var n = c.NationByTag("GBR");
        n.Government = "parliamentary_republic";
        NationalModifiers.Invalidate(n);
        var before = NationalModifiers.Of(n, "tax_efficiency");
        Check(c.ChangePolitics(n.Id, "military_junta", "", out _), "a legal manual politics change succeeds");
        var delta = GovernmentCatalog.Of("military_junta").Modifiers.GetValueOrDefault("tax_efficiency") -
            GovernmentCatalog.Of("parliamentary_republic").Modifiers.GetValueOrDefault("tax_efficiency");
        Check(Math.Abs(NationalModifiers.Of(n, "tax_efficiency") - before - delta) < .00001,
            "manual government change refreshes its visible economic modifiers immediately");
        var focusBefore = GovernmentFocusCatalog.Of(n.GovernmentFocus).Modifiers.GetValueOrDefault("research_speed");
        var research = NationalModifiers.Of(n, "research_speed");
        c.SetGovernmentFocus(n.Id, GovernmentFocus.EducationAndScience);
        var focusAfter = GovernmentFocusCatalog.Of(n.GovernmentFocus).Modifiers.GetValueOrDefault("research_speed");
        Check(Math.Abs(NationalModifiers.Of(n, "research_speed") - research - (focusAfter - focusBefore)) < .00001,
            "cabinet focus refreshes its visible research modifiers immediately");
    }

    static void Responses()
    {
        var c = Campaign.CreateBootstrap(881);
        var n = c.NationByTag("GBR");
        c.Wars.Clear();
        n.Treasury = 100f;
        n.Stability = .6f;
        foreach (var region in c.StatesOf(n)) region.Radicalism = .3f;
        var before = DomesticCrises.Read(c, n);
        var treasury = n.Treasury;
        var cost = DomesticCrises.Cost(c, n, "public_dialogue");
        var command = new StartCrisisResponseCommand { IssuerId = n.Id, ActionId = "public_dialogue" };
        Check(NetworkCommandCodec.TryEncode(command, out var kind, out var json) &&
              NetworkCommandCodec.TryDecode(kind, json, out _), "institutional response is a network campaign command");
        command.Apply(c);
        Check(Math.Abs(n.Treasury - (treasury - cost)) < .00001f &&
              n.DomesticCrisis.ResponseUntilDay == c.Defines.CrisisResponseDurationDays,
            "dialogue has a real funded programme with a fixed end date");
        var paid = n.Treasury;
        command.Apply(c);
        Check(n.Treasury == paid && DomesticCrises.Refusal(c, n, "institutional_review").Length > 0,
            "repeat clicks cannot buy overlapping programmes or reset the crisis gauge");
        var read = DomesticCrises.Read(c, n);
        Check(read.CoupTarget < before.CoupTarget && read.ResponseId == "public_dialogue" &&
              read.ResponseDays == c.Defines.CrisisResponseDurationDays,
            "the same programme effect and duration are exposed to the interface");
        var radicalism = c.StatesOf(n).First().Radicalism;
        DomesticCrises.Tick(c);
        Check(c.StatesOf(n).First().Radicalism < radicalism, "funded dialogue gradually addresses anger");
        Day(c, 90); DomesticCrises.Tick(c);
        Check(n.DomesticCrisis.ResponseUntilDay == -1 && DomesticCrises.Refusal(c, n, "public_dialogue").Length > 0,
            "completion cannot be immediately spammed into another programme");
        Day(c, n.DomesticCrisis.ResponseCooldownUntilDay);
        n.Treasury = 0f;
        Check(DomesticCrises.Refusal(c, n, "institutional_review").Length > 0, "an unfunded institutional review is refused");
        n.Treasury = 100f;
        new StartCrisisResponseCommand { IssuerId = n.Id, ActionId = "institutional_review" }.Apply(c);
        var institutional = n.StabilityGovernance;
        DomesticCrises.Tick(c);
        Check(n.StabilityGovernance > institutional, "a paid institutional review repairs governance over time");
    }

    static void Profile()
    {
        var c = Campaign.CreateBootstrap(732);
        DomesticCrises.Tick(c);
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var day = 1; day <= 100; day++) { Day(c, day); DomesticCrises.Tick(c); }
        watch.Stop();
        var perDay = (GC.GetAllocatedBytesForCurrentThread() - bytes) / 100;
        Console.WriteLine($"Domestic crisis daily review: {watch.Elapsed.TotalMilliseconds / 100:0.000} ms/day, " +
            $"{perDay:N0} bytes/day.");
        // Raised from 40 KB on 15 October 2026. The budget was set when the daily review only
        // carried awareness and crisis pressure; the merged tree now also reviews political
        // cohorts and regional conciliation for every country every day, which is deliberate
        // work rather than a leak. The measurement is a budget for a day's bookkeeping, not a
        // leak detector, so if it moves again find out what was added before moving the number.
        Check(perDay < 60_000, "daily simulation does not allocate UI crisis rows for every country");
        // --- political mood: who the residents actually are, and whether the government is theirs
        var mood = Campaign.CreateBootstrap(741);
        var moodNation = mood.Nations.Where(n => n.Population > 0 && n.Movement is null).First();
        mood.ConfigureSinglePlayer(moodNation.Id);
        mood.Wars.Clear();
        var comfortable = mood.Nations.Where(n => n.Population > 0).OrderByDescending(n => n.StandardOfLiving + n.Literacy).First();
        var poor = mood.Nations.Where(n => n.Population > 0).OrderBy(n => n.StandardOfLiving + n.Literacy).First();
        Check(PoliticalMood.PopularPosition(mood, comfortable).Economic > PoliticalMood.PopularPosition(mood, poor).Economic,
            "a comfortable, literate country leans towards the market and a poor one towards the state");
        var ranked = PoliticalMood.Shares(mood, moodNation);
        Check(ranked.Count == IdeologyCatalog.All.Count && Math.Abs(ranked.Sum(s => s.Share) - 1f) < .001f &&
              ranked.Count(s => s.Governing) == 1,
            "every ideology holds a share of the country and exactly one of them is the government's");
        Check(ranked.SequenceEqual(PoliticalMood.Shares(mood, moodNation)),
            "reading the mood is deterministic and remembers nothing");
        moodNation.Ideology = ranked[0].Ideology;
        var alignedMandate = PoliticalMood.Mandate(mood, moodNation);
        moodNation.Ideology = ranked[^1].Ideology;
        var opposedMandate = PoliticalMood.Mandate(mood, moodNation);
        Check(alignedMandate > opposedMandate && opposedMandate > 0f && alignedMandate < 1f,
            "a government that shares the country's own position holds a larger mandate than one that does not");
        foreach (var state in mood.StatesOf(moodNation)) state.Radicalism = .20f;
        Day(mood, 7); PoliticalMood.Tick(mood);
        var afterOpposed = mood.StatesOf(moodNation).Average(r => r.Radicalism);
        moodNation.Ideology = ranked[0].Ideology;
        foreach (var state in mood.StatesOf(moodNation)) state.Radicalism = .20f;
        Day(mood, 14); PoliticalMood.Tick(mood);
        var afterAligned = mood.StatesOf(moodNation).Average(r => r.Radicalism);
        Check(afterOpposed > .20f && afterAligned < .20f,
            "a government nobody shares breeds unrest, and one the country shares calms it");

        // The country's mood is its regions' moods, and its people are shown as the data keeps them.
        var leanCampaign = Campaign.CreateBootstrap(743);
        var leanNation = leanCampaign.Nations.Where(n => n.Population > 0 && n.Movement is null)
            .OrderByDescending(n => leanCampaign.StatesOf(n).Length).First();
        leanCampaign.ConfigureSinglePlayer(leanNation.Id);
        leanCampaign.Wars.Clear();
        var leans = PoliticalMood.GroupLeans(leanCampaign, leanNation);
        Check(leans.Count is > 0 and <= 6 && leans.All(l => l.Share > 0f && l.Lean > 0f && l.IdeologyName.Length > 0),
            "the country is broken down by the groups the population data actually keeps");
        Check(leans.Sum(l => l.Share) is > 0f and <= 1.0001f,
            "group shares are shares of the country, not of the chart");
        Check(leans.SequenceEqual(PoliticalMood.GroupLeans(leanCampaign, leanNation)),
            "the demographic breakdown is deterministic and remembers nothing");
        // A region's lean comes from its own conditions, not from the country it is in.
        var richRegion = new ScenarioState { MapIndex = 9101, Population = 100,
            StandardOfLiving = .95f, Literacy = .95f, Urbanization = .95f, Stability = .8f };
        var poorRegion = new ScenarioState { MapIndex = 9102, Population = 100,
            StandardOfLiving = .05f, Literacy = .05f, Urbanization = .05f, Stability = .8f };
        Check(PoliticalMood.RegionPosition(leanCampaign, richRegion, false).Economic >
              PoliticalMood.RegionPosition(leanCampaign, poorRegion, false).Economic,
            "a rich, urban region leans towards the market and a poor, rural one towards the state");
        Check(PoliticalMood.RegionPosition(leanCampaign, richRegion, true).Authority >
              PoliticalMood.RegionPosition(leanCampaign, richRegion, false).Authority,
            "a war pushes a region towards accepting more authority than peace does");
    }
}
