using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Proves that splitting <see cref="CampaignFingerprint"/> across domain partials
/// neither changed its deterministic output nor omitted any domain part.
/// </summary>
public static class FingerprintPartsTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("fingerprint parts: " + label);
        Console.WriteLine("PASS fingerprint parts: " + label);
    }

    [TestSuite("--fingerprint-parts-only", Order = 2500, IncludeInDefaultRun = true,
        Description = "Verifies domain-partial fingerprint parity and mutation sensitivity across all five parts.")]
    public static void Run()
    {
        Check(CampaignFingerprint.Of(null) == 0, "null campaign hashes to 0");

        var c = Campaign.CreateBootstrap(42);
        var noMapBaseline = CampaignFingerprint.Of(c);
        Check(noMapBaseline != 0 && noMapBaseline == CampaignFingerprint.Of(c, longCampaignClock: false),
            "day-0 bootstrap produces stable non-zero hash");

        MapRasterCatalog.ResetCatalog();
        TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(
            WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var msg), msg);
        c.AttachProvinceTopology(map);

        var baseline = CampaignFingerprint.Of(c);
        Check(baseline != noMapBaseline, "attached province controllers alter fingerprint");
        Check(noMapBaseline == 783285672869976683UL, $"unmapped day-0 seed 42 matches baseline ({noMapBaseline})");
        Check(baseline == 2943056504474909493UL, $"mapped day-0 seed 42 matches baseline ({baseline})");

        var nation = c.NationByTag("RUS");
        var state = c.StatesOf(nation).First();
        var army = c.Armies.First(a => a.NationId == nation.Id);
        var war = c.Wars.First(w => !w.Ended);
        var rel = c.Relations.Values.First();

        void AssertMutatedChanges(string part, Action mutate, Action restore)
        {
            mutate();
            var mutated = CampaignFingerprint.Of(c);
            restore();
            var restored = CampaignFingerprint.Of(c);
            Check(mutated != baseline, $"{part}: mutating field changes fingerprint");
            Check(restored == baseline, $"{part}: restoring field restores exact baseline");
        }

        // 1. World partial
        var prevCurse = c.Curse;
        AssertMutatedChanges("World.Curse", () => c.Curse = "test_curse", () => c.Curse = prevCurse);
        var prevScenario = c.ScenarioId;
        AssertMutatedChanges("World.ScenarioId", () => c.ScenarioId = "alt_scenario", () => c.ScenarioId = prevScenario);
        var prevDefcon = c.DefconLevel;
        AssertMutatedChanges("World.DefconLevel", () => c.DefconLevel = prevDefcon == 4 ? 3 : 4, () => c.DefconLevel = prevDefcon);
        var prevCustomName = nation.CustomName;
        AssertMutatedChanges("World.CustomName", () => nation.CustomName = "Probe Name", () => nation.CustomName = prevCustomName);
        var prevHeld = state.HeldById;
        AssertMutatedChanges("World.HeldById", () => state.HeldById = 999, () => state.HeldById = prevHeld);
        var prevCtrl = c.ProvinceController[1];
        AssertMutatedChanges("World.ProvinceController", () => c.ProvinceController[1] = (ushort)(prevCtrl ^ 1), () => c.ProvinceController[1] = prevCtrl);

        // 2. Economy partial
        var prevTreasury = nation.Treasury;
        AssertMutatedChanges("Economy.Treasury", () => nation.Treasury = prevTreasury + 123.5f, () => nation.Treasury = prevTreasury);
        var prevDebt = nation.PublicDebt;
        AssertMutatedChanges("Economy.PublicDebt", () => nation.PublicDebt = prevDebt + 42f, () => nation.PublicDebt = prevDebt);
        var prevGdp = nation.Gdp;
        AssertMutatedChanges("Economy.Gdp", () => nation.Gdp = prevGdp + 10f, () => nation.Gdp = prevGdp);
        var prevLev = c.WorldLeverage;
        AssertMutatedChanges("Economy.WorldLeverage", () => c.WorldLeverage = 0.5f, () => c.WorldLeverage = prevLev);
        var prevTradeDay = c.TradeRoutesDay;
        AssertMutatedChanges("Economy.TradeRoutesDay", () => c.TradeRoutesDay = 1, () => c.TradeRoutesDay = prevTradeDay);
        var prevNextDeal = c.NextDealOfferId;
        AssertMutatedChanges("Economy.NextDealOfferId", () => c.NextDealOfferId = prevNextDeal + 1, () => c.NextDealOfferId = prevNextDeal);

        // 3. Diplomacy partial
        var prevOpinion = rel.Opinion;
        AssertMutatedChanges("Diplomacy.RelationOpinion", () => rel.Opinion = prevOpinion + 5f, () => rel.Opinion = prevOpinion);
        var prevUltId = c.NextUltimatumId;
        AssertMutatedChanges("Diplomacy.NextUltimatumId", () => c.NextUltimatumId = prevUltId + 1, () => c.NextUltimatumId = prevUltId);
        var prevOrgId = c.NextOrganizationId;
        AssertMutatedChanges("Diplomacy.NextOrganizationId", () => c.NextOrganizationId = prevOrgId + 1, () => c.NextOrganizationId = prevOrgId);
        var prevEsteem = nation.DomesticImage.Esteem;
        AssertMutatedChanges("Diplomacy.DomesticImage", () => nation.DomesticImage.Esteem = prevEsteem + 0.25f, () => nation.DomesticImage.Esteem = prevEsteem);
        var prevPrestige = nation.Prestige;
        AssertMutatedChanges("Diplomacy.Prestige", () => nation.Prestige = prevPrestige + 25f, () => nation.Prestige = prevPrestige);

        // 4. Military partial
        var prevPersonnel = army.Personnel;
        AssertMutatedChanges("Military.ArmyPersonnel", () => army.Personnel = prevPersonnel + 100, () => army.Personnel = prevPersonnel);
        var prevStance = army.Stance;
        var nextStance = prevStance == ArmyStance.Aggressive ? ArmyStance.Cautious : ArmyStance.Aggressive;
        AssertMutatedChanges("Military.ArmyStance", () => army.Stance = nextStance, () => army.Stance = prevStance);
        var prevBattleScore = war.BattleScore;
        AssertMutatedChanges("Military.WarBattleScore", () => war.BattleScore = prevBattleScore + 2.5f, () => war.BattleScore = prevBattleScore);
        var prevArmyExp = nation.ArmyExperience;
        AssertMutatedChanges("Military.ArmyExperience", () => nation.ArmyExperience = prevArmyExp + 1.5f, () => nation.ArmyExperience = prevArmyExp);
        var prevCutoff = c.Defines.SupplyCutoffDays;
        AssertMutatedChanges("Military.SupplyCutoffDays", () => c.Defines.SupplyCutoffDays = prevCutoff + 1, () => c.Defines.SupplyCutoffDays = prevCutoff);

        // 5. Society partial
        var prevPop = state.Population;
        AssertMutatedChanges("Society.StatePopulation", () => state.Population = prevPop + 1000, () => state.Population = prevPop);
        var prevRad = state.Radicalism;
        AssertMutatedChanges("Society.StateRadicalism", () => state.Radicalism = prevRad + 0.1f, () => state.Radicalism = prevRad);
        var prevStab = nation.Stability;
        AssertMutatedChanges("Society.NationStability", () => nation.Stability = prevStab + 0.05f, () => nation.Stability = prevStab);
        var prevCourse = nation.NationalCourse.Dominant;
        AssertMutatedChanges("Society.NationalCourse", () => nation.NationalCourse.Dominant = "probe_course", () => nation.NationalCourse.Dominant = prevCourse);
        var prevCoup = nation.DomesticCrisis.CoupPressure;
        AssertMutatedChanges("Society.DomesticCrisis", () => nation.DomesticCrisis.CoupPressure = prevCoup + 0.2f, () => nation.DomesticCrisis.CoupPressure = prevCoup);

        // 6. longCampaignClock toggle
        var prevGone = nation.GoneSinceDay;
        nation.GoneSinceDay = 12;
        Check(CampaignFingerprint.Of(c, longCampaignClock: true) != baseline,
            "Society/World.GoneSinceDay changes fingerprint when longCampaignClock=true");
        Check(CampaignFingerprint.Of(c, longCampaignClock: false) == baseline,
            "Society/World.GoneSinceDay is ignored when longCampaignClock=false");
        nation.GoneSinceDay = prevGone;

        // 7. Order invariance across core collections
        c.Nations.Reverse();
        c.States.Reverse();
        c.Armies.Reverse();
        c.Wars.Reverse();
        Check(CampaignFingerprint.Of(c) == baseline, "fingerprint is invariant to collection ordering");

        // 8. 30-day Seed 1 baseline parity against pre-refactor SimBench capture
        var s1 = Campaign.CreateBootstrap(1);
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(
            WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var s1Map, out var s1Msg), s1Msg);
        if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var hydrology, out _))
            s1Map.AttachHydrology(hydrology);
        s1.AttachProvinceTopology(s1Map);
        s1.IsPaused = false;
        for (var d = 0; d < 30; d++) s1.TickDay();
        var s1Day30 = CampaignFingerprint.Of(s1);
        Check(s1Day30 == 17410192510816920124UL, $"seed 1 day 30 matches current baseline ({s1Day30})");
    }
}
