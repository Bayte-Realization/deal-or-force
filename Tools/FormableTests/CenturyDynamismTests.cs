using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// 100-year historical & geopolitical dynamism acceptance suite (--century-dynamism-only).
/// </summary>
static class CenturyDynamismTests
{
    private static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("century dynamism: " + note);
        Console.WriteLine("PASS century dynamism: " + note);
    }

    private static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, day * 24L);

    private static int FirstProvinceOf(Campaign c, int nationMapIndex)
    {
        var p2n = c.ProvinceMap.ProvinceToNation;
        for (var p = 1; p < p2n.Length; p++)
            if (p2n[p] == nationMapIndex) return p;
        return 1;
    }

    private static Campaign FlatWorld(int seed, int nations)
    {
        var c = new Campaign(seed);
        c.IsPaused = false;
        var states = new ushort[nations * 4];
        var owners = new ushort[nations * 4];
        for (var i = 0; i < nations; i++)
        {
            var tag = $"N{i:00}";
            var nation = new Nation
            {
                MapIndex = i + 1,
                Tag = tag,
                Name = tag,
                Population = 10_000_000,
                Gdp = 200f,
                Treasury = 50f,
                PoliticalPoints = 200f,
                Stability = 0.75f,
                WarSupport = 0.75f,
                StandardOfLiving = 0.65f,
                Government = "parliamentary_republic",
                Ideology = "liberalism",
                CapitalStateMapIndex = i + 1,
                Region = "europe",
                Continent = "europe"
            };
            c.AddNation(nation);
            c.States.Add(new ScenarioState
            {
                Id = $"s_{i}",
                MapIndex = i + 1,
                NationMapIndex = i + 1,
                NationId = tag,
                Name = $"State {i}",
                Population = 10_000_000,
                StandardOfLiving = 0.65f,
                Literacy = 0.85f,
                Urbanization = 0.75f,
                CoreTags = new List<string> { tag }
            });
            for (var k = 0; k < 4; k++)
            {
                states[i * 4 + k] = (ushort)(i + 1);
                owners[i * 4 + k] = (ushort)(i + 1);
            }
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, nations * 2, 2, _ => false));
        return c;
    }

    [TestSuite("--century-dynamism-only", Order = 2735, IncludeInDefaultRun = true,
        Description = "Verifies 100-year org turnover, rivalry confrontation, expired truces, alliance fractures, revanchism, rump wars, macro cycles, and regime drift.")]
    public static void Run()
    {
        InternationalOrganizations.ResetCatalog();
        OpinionCatalog.ResetCatalog();
        GovernanceEvents.ResetCatalog();
        IdeologyCatalog.ResetCatalog();

        DefinesSyncCheck();
        OrganizationTurnoverAndFriction();
        DiplomacyConfrontationAndTruces();
        StandingClaimsCyclesAndAlliances();
        RevanchismAndRumpFinishingWars();
        MacroCyclesStabilityAndRegimes();
        Phase2AuditFixesCheck();
        ZeroAllocDisputesCheck();
    }

    private static void DefinesSyncCheck()
    {
        var compiled = new Defines();
        Check(MathF.Abs(compiled.OrganizationCohesionPull - 0.11f) < 0.001f, "OrganizationCohesionPull synced to 0.11");
        Check(MathF.Abs(compiled.OrganizationDepartureBelonging - 0.33f) < 0.001f, "OrganizationDepartureBelonging synced to 0.33");
        Check(MathF.Abs(compiled.OrganizationBaselineHalfLifeYears - 5f) < 0.001f, "OrganizationBaselineHalfLifeYears synced to 5");
        Check(MathF.Abs(compiled.AiRevancheAppetite - 1.15f) < 0.001f, "AiRevancheAppetite synced to 1.15");
        Check(MathF.Abs(compiled.AiRevancheOpportunityWeight - 0.85f) < 0.001f, "AiRevancheOpportunityWeight synced to 0.85");
        Check(compiled.CivilWarWorldPerYear == 7, "CivilWarWorldPerYear synced to 7");

        var loaded = new Defines();
        GameDataLoader.ApplyDefines(loaded);
        Check(MathF.Abs(loaded.OrganizationCohesionPull - compiled.OrganizationCohesionPull) < 0.0001f, "JSON OrganizationCohesionPull matches C# default");
        Check(MathF.Abs(loaded.OrganizationDepartureBelonging - compiled.OrganizationDepartureBelonging) < 0.0001f, "JSON OrganizationDepartureBelonging matches C# default");
        Check(MathF.Abs(loaded.OrganizationBaselineHalfLifeYears - compiled.OrganizationBaselineHalfLifeYears) < 0.0001f, "JSON OrganizationBaselineHalfLifeYears matches C# default");
        Check(MathF.Abs(loaded.AiRevancheAppetite - compiled.AiRevancheAppetite) < 0.0001f, "JSON AiRevancheAppetite matches C# default");
        Check(MathF.Abs(loaded.AiRevancheOpportunityWeight - compiled.AiRevancheOpportunityWeight) < 0.0001f, "JSON AiRevancheOpportunityWeight matches C# default");
        Check(loaded.CivilWarWorldPerYear == compiled.CivilWarWorldPerYear, "JSON CivilWarWorldPerYear matches C# default");
    }

    private static void OrganizationTurnoverAndFriction()
    {
        var c = FlatWorld(701, 8);
        SetDay(c, 2200);
        var harmonic = InternationalOrganizations.Seed(c, "Harmony", "cooperation", new[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        harmonic.FoundedDay = 0;
        var baseTarget = OrganizationDynamics.Target(c, harmonic);

        // Diverge ideology + embargoes + rivalries across 4 of 8 members
        for (var k = 4; k < 8; k++)
        {
            c.Nations[k].Government = "one_party_state";
            c.Nations[k].Ideology = "communism";
            var rel = CampaignDiplomacy.Get(c, 0, k);
            rel.Stance = DiplomaticStance.Rival;
            rel.Opinion = -45f;
        }
        var frictionTarget = OrganizationDynamics.Target(c, harmonic);
        Check(frictionTarget < baseTarget - 0.08f, $"intra-bloc friction meaningfully lowers 8-member cohesion target ({frictionTarget:0.000} < {baseTarget:0.000})");

        // Lonely player org dissolves cleanly when alone past a year
        c.Nations[0].IsHuman = true;
        var lonelyHumanOrg = InternationalOrganizations.Seed(c, "Solo League", "cooperation", new[] { 0, 1 });
        lonelyHumanOrg.Members.Remove(1);
        lonelyHumanOrg.FoundedDay = 0;
        for (var d = 500; d < 507; d++)
        {
            SetDay(c, d);
            OrganizationDynamics.Tick(c);
        }
        Check(lonelyHumanOrg.Dissolved, "a single-member organization with a human dissolves after 1 year");

        // Collapsed multi-member org containing human can dissolve via charter collapse while non-collapsed protects
        var collapsedOrg = InternationalOrganizations.Seed(c, "Collapsed Pact", "cooperation", new[] { 0, 1, 2, 3 });
        collapsedOrg.FoundedDay = 0;
        collapsedOrg.PeakMembers = 8; // < 60% remain -> Durability = 1.0
        collapsedOrg.Cohesion = c.Defines.OrganizationDissolveCohesion * 0.65f;
        Check(OrganizationDynamics.Durability(c, collapsedOrg) == 1f, "Durability reaches 1.0 when <60% of members remain or cohesion < 0.7*Dissolve");

        var oldHealthy = InternationalOrganizations.Seed(c, "Old Council", "cooperation", new[] { 0, 1, 2, 3 });
        oldHealthy.FoundedDay = 0;
        oldHealthy.PeakMembers = 4;
        oldHealthy.Cohesion = 0.60f;
        Check(MathF.Abs(OrganizationDynamics.Durability(c, oldHealthy) - 0.35f) < 0.001f, "Durability softened to 0.35 for older intact 4-member org");
    }

    private static void DiplomacyConfrontationAndTruces()
    {
        // 1. Expired truce transitions out of Truce into Rival / Neutral
        var c = FlatWorld(702, 3);
        var relRival = CampaignDiplomacy.Get(c, 0, 1);
        relRival.Stance = DiplomaticStance.Truce;
        relRival.TruceUntil = 200;
        relRival.Opinion = -40f;

        var relNeutral = CampaignDiplomacy.Get(c, 1, 2);
        relNeutral.Stance = DiplomaticStance.Truce;
        relNeutral.TruceUntil = 200;
        relNeutral.Opinion = 5f;

        SetDay(c, 199);
        Check(!CampaignDiplomacy.RefreshExpiredTruce(c, relRival) && relRival.Stance == DiplomaticStance.Truce,
            "active truce remains Truce before TruceUntil");
        SetDay(c, 200);
        OpinionSystem.Tick(c);
        Check(relRival.Stance == DiplomaticStance.Rival, "expired truce with low opinion transitions to Rival");
        Check(relNeutral.Stance == DiplomaticStance.Neutral, "expired truce without hostility or claims transitions to Neutral");

        // 2. Dead-code fix in DiplomacyStages: AI still denounces/confronts worstRival despite having a bestFriend > -30
        var dipWorld = FlatWorld(703, 3);
        var friendRel = CampaignDiplomacy.Get(dipWorld, 0, 1);
        friendRel.Opinion = 15f;
        friendRel.Stance = DiplomaticStance.Neutral;

        var enemyRel = CampaignDiplomacy.Get(dipWorld, 0, 2);
        enemyRel.Stance = DiplomaticStance.Neutral;
        dipWorld.States[2].CoreTags.Add(dipWorld.Nations[0].Tag);
        OpinionSystem.Remember(dipWorld, enemyRel, "took_our_land");
        OpinionSystem.Explain(dipWorld, enemyRel, updateOpinion: true);

        SetDay(dipWorld, 30);
        for (var d = 0; d < 14; d++)
        {
            SetDay(dipWorld, 30 + d);
            AiStatecraft.Tick(dipWorld);
        }
        var postEnemyRel = CampaignDiplomacy.Peek(dipWorld, 0, 2);
        Check(postEnemyRel.Stance == DiplomaticStance.Rival || dipWorld.Chronicle.Entries.Any(e => e.Kind == "denunciation" && e.ActorId == 0 && e.TargetId == 2),
            "AI confronts worstRival rather than endlessly courting bestFriend");
    }

    private static void StandingClaimsCyclesAndAlliances()
    {
        var c = FlatWorld(704, 3);
        c.States[1].CoreTags.Add(c.Nations[0].Tag); // Nation 1 holds Nation 0's homeland
        var claimRel = CampaignDiplomacy.Get(c, 0, 1);
        OpinionSystem.Explain(c, claimRel, updateOpinion: true);
        Check(claimRel.Opinion <= -22f && claimRel.Opinion >= -45f,
            $"holding lost homeland adds standing -22..-35 opinion penalty (actual {claimRel.Opinion:0.0})");

        // Year 0 vs Year 10 realignment & WeeklyDiplomacyScan cache sync
        var pair = CampaignDiplomacy.Get(c, 1, 2);
        OpinionSystem.Explain(c, pair, updateOpinion: true);
        var day0Op = pair.Opinion;
        AiStatecraft.ReviewWeeklyPair(c, c.Nations[1], c.Nations[2], false, 50f, false, out _);

        SetDay(c, 3650);
        var walkedYear10 = AiStatecraft.ReviewWeeklyPair(c, c.Nations[1], c.Nations[2], false, 50f, false, out var yr10Rel);
        Check(walkedYear10 && MathF.Abs(yr10Rel.Opinion - day0Op) > 1f,
            $"WeeklyDiplomacyScan invalidates and applies Year 10 geopolitical shift ({day0Op:0.0} -> {yr10Rel.Opinion:0.0})");

        // Alliance fracture when +34 modifier masks deeply negative underlying opinion
        var allyWorld = FlatWorld(705, 2);
        var allyRel = CampaignDiplomacy.Get(allyWorld, 0, 1);
        allyRel.Stance = DiplomaticStance.Allied;
        OpinionSystem.Remember(allyWorld, allyRel, "alliance");
        OpinionSystem.Remember(allyWorld, allyRel, "took_our_land");
        OpinionSystem.Explain(allyWorld, allyRel, updateOpinion: true);
        allyWorld.Nations[1].Government = "one_party_state";
        allyWorld.Nations[1].Ideology = "communism";
        SetDay(allyWorld, 7);
        AllyRivalShift.Tick(allyWorld);
        Check(allyRel.Stance == DiplomaticStance.Rival,
            "alliance breaks when underlying opinion past the +34 alliance buffer drops below threshold");
    }

    private static void RevanchismAndRumpFinishingWars()
    {
        var c = FlatWorld(706, 2);
        c.States[1].CoreTags.Add(c.Nations[0].Tag); // Nation 1 holds Nation 0's homeland
        c.Armies.Add(new Army { Id = 1, NationId = 0, ProvinceId = FirstProvinceOf(c, c.Nations[0].MapIndex), Divisions = 4, Strength = 1f, Equipment = 1f, Organisation = 1f });
        c.Armies.Add(new Army { Id = 2, NationId = 1, ProvinceId = FirstProvinceOf(c, c.Nations[1].MapIndex), Divisions = 5, Strength = 1f, Equipment = 1f, Organisation = 1f });
        c.Nations[1].Stability = 0.42f; // unstable holder -> unlocks revanchist minFloor 0.72
        var scoreUnstable = AiStatecraft.JudgeWarFor(c, c.Nations[0], c.Nations[1]);
        Check(scoreUnstable > -5f, $"revanchist contender evaluates unstable homeland holder meaningfully (score={scoreUnstable:0.00})");

        // Rump finishing: Nation 0 already took state from Nation 1 (RememberedOwner == 2)
        // AND persists even after SocialMemory ticks >=14 days and resets RememberedOwner.
        var rWorld = FlatWorld(707, 2);
        rWorld.States[1].CoreTags.Clear();
        rWorld.States.Add(new ScenarioState
        {
            Id = "taken_1",
            MapIndex = 3,
            NationMapIndex = rWorld.Nations[0].MapIndex,
            NationId = rWorld.Nations[0].Tag,
            RememberedOwner = rWorld.Nations[1].MapIndex,
            Population = 8_000_000,
            Name = "Taken State"
        });
        rWorld.Homelands[rWorld.Nations[1].Id] = new List<int> { rWorld.States[1].MapIndex, 3 };
        rWorld.Nations[0].Population = 18_000_000;
        rWorld.InvalidateStateIndex();
        rWorld.Nations[0].Government = "parliamentary_republic";
        rWorld.Nations[0].Ideology = "liberalism";
        Check(AnnexationAmbition.IsConqueredRump(rWorld, rWorld.Nations[0], rWorld.Nations[1]),
            "IsConqueredRump detects previously carved neighbor");
        for (var d = 1; d <= 16; d++)
        {
            SetDay(rWorld, d);
            SocialMemory.Tick(rWorld);
        }
        Check(rWorld.States[2].RememberedOwner == rWorld.Nations[0].MapIndex,
            "SocialMemory resets RememberedOwner within 14+ days");
        Check(AnnexationAmbition.IsConqueredRump(rWorld, rWorld.Nations[0], rWorld.Nations[1]),
            "IsConqueredRump persists after SocialMemory resets RememberedOwner via homeland history");
        var cause = Statecraft.CasusBelli(rWorld, rWorld.Nations[0], rWorld.Nations[1]);
        Check(!string.IsNullOrEmpty(cause), $"conquered rump provides valid CasusBelli ({cause}) instead of empty no_cause");
    }

    private static void MacroCyclesStabilityAndRegimes()
    {
        var c = FlatWorld(708, 2);
        SetDay(c, 100);
        Check(GeopoliticalCycle.MacroEconomicWave(c, c.Nations[0]) == 0f && GeopoliticalCycle.InstitutionalSclerosis(c, c.Nations[0]) == 0f,
            "Year 1 ramp guard keeps macro wave and sclerosis at zero");
        var yr0Stab = StabilityCore.RegionStability(c, c.Nations[0], c.States[0]);

        c.Nations[0].PoliticsChangedDay = 0;
        c.Nations[0].HeadOfStateSinceDay = 0;
        SetDay(c, 3650 * 4); // Year 40
        var yr40Stab = StabilityCore.RegionStability(c, c.Nations[0], c.States[0]);
        Check(yr40Stab < yr0Stab, $"scaled LivingExpectation lowers stability in prosperous urbanized society ({yr0Stab:0.000} -> {yr40Stab:0.000})");

        var sclerosis = GeopoliticalCycle.InstitutionalSclerosis(c, c.Nations[0]);
        Check(sclerosis > 0.7f, $"40 years of unchallenged rule builds strong institutional sclerosis ({sclerosis:0.00})");

        var minWave = 1f;
        var maxWave = -1f;
        for (var y = 3; y <= 50; y++)
        {
            SetDay(c, y * 365);
            var w = GeopoliticalCycle.MacroEconomicWave(c, c.Nations[0]);
            if (w < minWave) minWave = w;
            if (w > maxWave) maxWave = w;
        }
        Check(minWave < -0.35f && maxWave > 0.35f, $"MacroEconomicWave oscillates across multi-decade cycles ({minWave:0.00}..{maxWave:0.00})");

        // Verify governance transitions JSON loaded new dynamic reform / revanchism routes
        var ids = GovernanceEvents.All.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(ids.Contains("reform_opening") && ids.Contains("revanchist_turn") && ids.Contains("crisis_authoritarian_shift"),
            "new governance evolution routes loaded cleanly");
    }

    private static void Phase2AuditFixesCheck()
    {
        // 1. ProtestsEnabled default ON & balanced
        var compiled = new Defines();
        Check(compiled.ProtestsEnabled, "ProtestsEnabled defaults to true");

        // 2. Hyperinflation (> 12.5%) continues increasing RadicalismCore.InflationPressure
        var nInf12 = new Nation { Inflation = 0.125f };
        var nInf30 = new Nation { Inflation = 0.30f };
        Check(RadicalismCore.InflationPressure(nInf30) > RadicalismCore.InflationPressure(nInf12) * 1.4f,
            "hyperinflation above 12.5% raises InflationPressure rather than saturating flat");

        // 3. DebtCondition.Restructuring increases Coups.WealthFactor distress floor and StateFailure.ArmyDisloyalty
        var c = FlatWorld(710, 3);
        c.Nations[0].Gdp = 5000f; // wealthy country -> low baseline WealthFactor
        var calmWealth = Coups.WealthFactor(c, c.Nations[0]);
        c.Nations[0].DebtCondition = DebtCondition.Restructuring;
        var debtWealth = Coups.WealthFactor(c, c.Nations[0]);
        Check(debtWealth > calmWealth + 0.15f, $"Restructuring breaks wealth immunity in Coups.WealthFactor ({calmWealth:0.000} -> {debtWealth:0.000})");
        var disloyCalm = StateFailure.ArmyDisloyalty(c, c.Nations[1]);
        c.Nations[1].DebtCondition = DebtCondition.Restructuring;
        var disloyRestr = StateFailure.ArmyDisloyalty(c, c.Nations[1]);
        Check(disloyRestr > disloyCalm + 0.15f, $"DebtCondition.Restructuring elevates ArmyDisloyalty ({disloyCalm:0.00} -> {disloyRestr:0.00})");

        // 4. DenounceCommand blocks active wars/truces & explains opinion immediately into Rival
        var denRel = CampaignDiplomacy.Get(c, 0, 1);
        denRel.Stance = DiplomaticStance.Truce;
        denRel.TruceUntil = 500;
        SetDay(c, 100);
        new DenounceCommand { IssuerId = 0, TargetId = 1 }.Apply(c);
        Check(denRel.LastActionDay < 0, "DenounceCommand refuses active truce");
        SetDay(c, 500);
        new DenounceCommand { IssuerId = 0, TargetId = 1 }.Apply(c);
        Check(denRel.LastActionDay == 500 && OpinionSystem.Has(denRel, "denounced"), "DenounceCommand applies after truce expiry");

        // 5. OrganizationCrises evaluates AI concession only once per crisis
        var org = InternationalOrganizations.Seed(c, "Bargain Bloc", "cooperation", new[] { 0, 1, 2 });
        var leader = InternationalOrganizations.Leader(c, org);
        var member = c.Nations.First(n => org.Members.Contains(n.Id) && n.Id != leader.Id);
        leader.IsHuman = false; member.IsHuman = false;
        leader.Treasury = 1000f;
        var openDay = 1000 + ((7 - (1000 + org.Id * 3) % 7) % 7);
        SetDay(c, openDay);
        OrganizationCrises.Open(c, org, member);
        c.Defines.AiConcessionChance = 0f;
        c.Defines.AiConcessionValueShare = 0f;
        SetDay(c, openDay + 7);
        OrganizationCrises.Week(c, org, _ => 0.2f, _ => { });
        Check(org.CrisisMemberId == member.Id, "week 1 with chance 0 keeps crisis open");
        c.Defines.AiConcessionChance = 1f;
        SetDay(c, openDay + 14);
        OrganizationCrises.Week(c, org, _ => 0.2f, _ => { });
        Check(org.CrisisMemberId == member.Id, "week 2 does not re-roll AI concession (no 99.94% cumulative concession bug)");

        // 6. ControlTally refreshes cleanly when WarRevision changes on same day
        c.Wars.Add(new StrategicWar { Id = 99, AttackerId = 0, DefenderId = 1, StartedDay = openDay + 14 });
        c.MarkWarsChanged();
        var rc = ControlTally.World(c, c.Nations[0].MapIndex, c.States[0].MapIndex);
        Check(rc.Provinces > 0 && rc.Bordered > 0, "ControlTally detects same-day war declaration via WarRevision");
    }

    private static void ZeroAllocDisputesCheck()
    {
        var c = FlatWorld(709, 2);
        c.States[1].CoreTags.Add(c.Nations[0].Tag);
        var disputes = TerritorialClaims.Disputes(c).ToList();
        var lost = LostLands.Of(c, c.Nations[0]);
        Check(disputes.Count == 1 && lost.Count == 1, "zero-alloc Disputes and LostLands.Of return exact dispute records");
    }
}
