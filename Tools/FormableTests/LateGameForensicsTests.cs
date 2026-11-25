using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Regression & forensics gate for Wave 6F-Sim (2053 late-game save findings):
/// 1. ObserverMode AI paralysis (`Campaign.IsHumanControlled` returns false while `ObserverMode == true`
///    so former human seats run AI statecraft, fronts, peace, finance, and combat normally).
/// 2. `CoverRefuses` AI front deadlock (`CombatOpportunitySelection` filters cover-refused empty gaps
///    when non-refused neighbors exist instead of stalling forever on an empty covered cell).
/// 3. Sovereign debt restructuring deadlock (`SovereignFinance.Settle` reserves/pays restructuring fee
///    before `PayArrears`, pauses new `DebtArrears` compounding during active restructuring, and
///    haircuts/clears `PaymentArrears` proportionally on completion).
/// 4. Late-game bloat pruning (`PopulationFolding`, `DemographicCycle.MoveCultures`, `LongCampaign` 365d
///    retirement, `RelationDormancy` inert neutral pruning, and `SocialMemory` dead/inert image pruning).
/// </summary>
static class LateGameForensicsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("late-game forensics: " + label);
        Console.WriteLine("PASS late-game forensics: " + label);
    }

    [TestSuite("--late-game-forensics-only", Order = 2697, IncludeInDefaultRun = true,
        Description = "ObserverMode AI paralysis, CoverRefuses deadlock, debt restructuring progress, and late-game bloat pruning.")]
    public static void Run()
    {
        TestObserverModeAiControl();
        TestCoverRefusesDoesNotDeadlockFront();
        TestDebtRestructuringProgressAndRecovery();
        TestLateGameBloatPruningAndDeterminism();
        TestRelationSweepAcrossSlices();
    }

    static void TestRelationSweepAcrossSlices()
    {
        Campaign MakeWorld(bool allAlive)
        {
            var campaign = new Campaign(66005);
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, 28L * 24);
            for (var id = 0; id < 36; id++)
                campaign.AddNation(new Nation
                {
                    Tag = $"T{id:00}", MapIndex = id + 1,
                    Population = allAlive || id % 3 != 0 ? 1_000 : 0,
                });
            for (var a = 0; a < campaign.Nations.Count; a++)
                for (var b = a + 1; b < campaign.Nations.Count; b++)
                {
                    var relation = CampaignDiplomacy.Get(campaign, a, b);
                    if (b % 5 == 0) relation.TradeAgreement = true;
                    if (a % 5 == 0)
                    {
                        var key = ((long)a << 32) | (uint)b;
                        campaign.Relations.Remove(key);
                        relation.Dormant = true;
                        campaign.DormantRelations.Add(key, relation);
                    }
                }
            return campaign;
        }

        var whole = MakeWorld(allAlive: false);
        var staged = MakeWorld(allAlive: false);
        var other = MakeWorld(allAlive: true);
        var viewed = MakeWorld(allAlive: true);
        RelationDormancy.Sweep(whole);
        var previousSlice = Campaign.StageSliceMilliseconds;
        try
        {
            Campaign.StageSliceMilliseconds = 0;
            using var sweep = RelationDormancy.SweepStages(staged).GetEnumerator();
            Check(sweep.MoveNext(), "a large weekly relation sweep yields before processing the whole table");
            RelationDormancy.Sweep(other);
            while (sweep.MoveNext()) { }

            using var viewedSweep = RelationDormancy.SweepStages(viewed).GetEnumerator();
            Check(viewedSweep.MoveNext(), "a relation sweep can be suspended for the interface");
            var opened = CampaignDiplomacy.Get(viewed, 5, 10);
            while (viewedSweep.MoveNext()) { }
            Check(ReferenceEquals(CampaignDiplomacy.Peek(viewed, 5, 10), opened) && !opened.Dormant,
                "opening a pending relation between slices preserves the woken record");
        }
        finally { Campaign.StageSliceMilliseconds = previousSlice; }

        Check(staged.Relations.Keys.SequenceEqual(whole.Relations.Keys) &&
              staged.DormantRelations.Keys.SequenceEqual(whole.DormantRelations.Keys) &&
              CampaignFingerprint.Of(staged) == CampaignFingerprint.Of(whole),
            "interleaving another world preserves the staged sweep's result and relation order");
    }

    static void TestObserverModeAiControl()
    {
        var c = new Campaign(66001);
        var india = c.AddNation(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "IND", Name = "India",
            Population = 100_000_000, Gdp = 3000f, Treasury = 0f, IsHuman = true,
            PublicDebt = 6000f, DebtArrears = 500f, PaymentArrears = 500f,
            DebtCrisisWeeks = 30, WeeklyTaxIncome = 10f,
            DebtCondition = DebtCondition.Insolvent, DebtMechanicActive = true, DebtInitialized = true
        }, player: true);
        var rival = c.AddNation(new Nation
        {
            Id = 1, MapIndex = 2, Tag = "PAK", Name = "Rival",
            Population = 50_000_000, Gdp = 400f, Treasury = 40f, IsHuman = false
        });
        c.ConfigureSinglePlayer(india.Id);

        Check(c.IsHumanControlled(india), "IsHumanControlled is true when ObserverMode is false");
        Check(!c.IsHumanControlled(rival), "IsHumanControlled is false for AI nation");
        Check(!AiStatecraft.IsCalmAi(c, india), "Human nation is not calm AI while actively played");

        // Enter ObserverMode while keeping HumanSeats intact (exact shape of 2053 save).
        c.ObserverMode = true;
        Check(c.HumanSeats.Contains(india.Id) && india.IsHuman, "ObserverMode preserves HumanSeats for F7 return");
        Check(!c.IsHumanControlled(india), "IsHumanControlled is false for former human seat during ObserverMode");
        Check(AiStatecraft.IsCalmAi(c, india), "AiStatecraft treats former human seat as AI while ObserverMode is true");

        // SovereignFinance auto-starts restructuring for insolvent former human seat in ObserverMode.
        SovereignFinance.Settle(c, india, 0f, 0f);
        Check(india.DebtCondition == DebtCondition.Restructuring,
            "SovereignFinance auto-restructures insolvent former human seat in ObserverMode");

        // FrontManager calls up idle manual armies of former human seat in ObserverMode.
        var map = ProvinceTopology.Build(new ushort[] { 1, 2 }, new ushort[] { 1, 2 }, 2, 1, _ => false);
        c.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Population = 100_000_000 });
        c.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 2, Population = 50_000_000 });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(map);
        var war = new StrategicWar { Id = 1, AttackerId = india.Id, DefenderId = rival.Id, StartedDay = 0 };
        war.AttackerFront.Add(1);
        war.DefenderFront.Add(2);
        c.Wars.Add(war);
        c.MarkWarsChanged();
        var army = new Army { Id = 1, NationId = india.Id, ProvinceId = 1, Divisions = 5f, Organisation = 0.95f, SupplyLevel = 1f, OrderKind = ArmyOrder.Manual };
        c.Armies.Add(army);
        FrontManager.Tick(c);
        Check(army.OrderKind != ArmyOrder.Manual,
            $"FrontManager calls up idle former-human army in ObserverMode (OrderKind={army.OrderKind})");

        // Exiting ObserverMode restores human control immediately.
        c.ObserverMode = false;
        Check(c.IsHumanControlled(india), "Exiting ObserverMode restores IsHumanControlled immediately");
    }

    static void TestCoverRefusesDoesNotDeadlockFront()
    {
        // Build a 3x2 grid where attacker at (0,0) [prov 1] borders two enemy provinces:
        //   prov 2 at (1,0): EMPTY, heavily covered by strong enemy stack at (2,0) [prov 3] -> CoverRefuses == true
        //   prov 4 at (0,1): DEFENDED by a weak enemy garrison -> CoverRefuses == false
        var states = new ushort[] { 1, 2, 3, 4, 5, 6 };
        var control = new ushort[] { 1, 2, 2, 2, 2, 2 };
        var map = ProvinceTopology.Build(states, control, 3, 2, _ => false);
        var c = new Campaign(66002);
        var att = c.AddNation(new Nation { Id = 0, MapIndex = 1, Tag = "ATT", Name = "Attacker", Population = 10_000_000, Gdp = 200f });
        var def = c.AddNation(new Nation { Id = 1, MapIndex = 2, Tag = "DEF", Name = "Defender", Population = 10_000_000, Gdp = 200f });
        for (ushort i = 1; i <= 6; i++)
            c.States.Add(new ScenarioState { MapIndex = i, NationMapIndex = i == 1 ? 1 : 2, Population = 2_000_000 });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(map);

        var war = new StrategicWar { Id = 1, AttackerId = att.Id, DefenderId = def.Id, StartedDay = 0 };
        war.AttackerFront.Add(1);
        war.DefenderFront.Add(2);
        war.DefenderFront.Add(4);
        c.Wars.Add(war);
        c.MarkWarsChanged();

        var attacker = new Army
        {
            Id = 1, NationId = att.Id, ProvinceId = 1, Divisions = 4f,
            Organisation = 0.98f, SupplyLevel = 1f, OrderKind = ArmyOrder.AdvanceFront,
            AssignedWarId = war.Id, PlanActive = true, Planning = 0.5f, ReadyDay = -10, DirectOrder = false
        };
        // Strong enemy stack in prov 3 covering empty prov 2 heavily.
        var coverStack = new Army
        {
            Id = 2, NationId = def.Id, ProvinceId = 3, Divisions = 25f,
            Organisation = 1f, SupplyLevel = 1f, OrderKind = ArmyOrder.DefendFront, AssignedWarId = war.Id
        };
        // Weak enemy garrison directly standing in prov 4 (adjacent to prov 1, NOT empty -> CoverRefuses == false).
        var weakGarrison = new Army
        {
            Id = 3, NationId = def.Id, ProvinceId = 4, Divisions = 0.5f,
            Organisation = 0.8f, SupplyLevel = 1f, OrderKind = ArmyOrder.DefendFront, AssignedWarId = war.Id
        };
        c.Armies.Add(attacker);
        c.Armies.Add(coverStack);
        c.Armies.Add(weakGarrison);

        c.IsPaused = false;
        c.Defines.HoursPerTick = 24;
        for (var step = 0; step < 5 && c.Battles.Count == 0 && !attacker.InBattle && attacker.MoveProgress <= 0f; step++)
            c.TickDay();

        Check(c.Battles.Count > 0 || attacker.InBattle || attacker.MoveProgress > 0f,
            $"AI army bypassed cover-refused empty gap and engaged/advanced on playable neighbor (battles={c.Battles.Count}, inBattle={attacker.InBattle}, prog={attacker.MoveProgress:F2})");
    }

    static void TestDebtRestructuringProgressAndRecovery()
    {
        var c = new Campaign(66003);
        var n = c.AddNation(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "DBT", Name = "Debtor",
            Population = 10_000_000, Gdp = 100f, Treasury = 0f,
            PublicDebt = 250f, DebtArrears = 15f, PaymentArrears = 8f,
            WeeklyTaxIncome = 0.40f, CreditConfidence = 0.25f,
            DebtCondition = DebtCondition.Restructuring, DebtMechanicActive = true, DebtInitialized = true
        });

        // Previously: PayArrears drained Treasury to 0 before fee check, locking Progress at 0 forever!
        // Now: even with huge existing DebtArrears/PaymentArrears and modest positive primary surplus,
        // fee is paid first, new DebtArrears do not compound during restructuring, and workout completes.
        var arrearsBefore = n.DebtArrears;
        for (var w = 0; w < c.Defines.DebtRestructuringWeeks; w++)
        {
            SovereignFinance.Prepare(c, n);
            // Modest surplus balance (+0.05) with heavy contractual interest (0.20) -> previously drained to 0 by PayArrears.
            SovereignFinance.Settle(c, n, 0.05f, 0.20f);
        }

        Check(n.DebtRestructuringProgress >= c.Defines.DebtRestructuringWeeks,
            $"Restructuring advanced despite existing arrears (progress={n.DebtRestructuringProgress}/{c.Defines.DebtRestructuringWeeks})");
        Check(n.DebtCondition is DebtCondition.Current or DebtCondition.Stressed,
            $"Nation recovered out of Restructuring cleanly (condition={n.DebtCondition})");
        Check(n.DebtArrears == 0f && n.PaymentArrears < 8f && n.PublicDebt < 250f,
            $"Haircut reduced principal ({n.PublicDebt:F1}) and arrears (debt={n.DebtArrears:F2}, pay={n.PaymentArrears:F2}, was {arrearsBefore:F1})");
    }

    static void TestLateGameBloatPruningAndDeterminism()
    {
        // 1. PopulationFolding tightened defaults & DemographicCycle.MoveCultures threshold.
        var d = new Defines();
        Check(d.FoldPeopleSliverPeople == 1500 && d.FoldPeopleEveryWeeks == 13 && MathF.Abs(d.FoldPeopleSliverShare - 0.0025f) < 1e-5f,
            $"PopulationFolding defaults tightened ({d.FoldPeopleSliverPeople} pop, {d.FoldPeopleSliverShare:P2}, every {d.FoldPeopleEveryWeeks}w)");
        Check(d.RetireDeadAfterDays == 365, $"LongCampaign RetireDeadAfterDays tightened to {d.RetireDeadAfterDays}d");
        Check(d.MaxDaysPerFrame == 24 && d.SmoothFastSpeedSimulationBudgetMs <= 16f && d.UncheckedSpeedSimulationBudgetMs <= 96f,
            $"Frame clock budgets configured ({d.MaxDaysPerFrame} steps/frame, smooth={d.SmoothFastSpeedSimulationBudgetMs}ms, unchecked={d.UncheckedSpeedSimulationBudgetMs}ms)");

        // 2. RelationDormancy prunes inert neutral rows & dead dormant ghosts on 28-day sweep.
        var c = new Campaign(66004);
        var a = c.AddNation(new Nation { Id = 0, MapIndex = 1, Tag = "AAA", Population = 5_000_000, Gdp = 50f, CapitalLatitude = 45f, CapitalLongitude = 10f, Government = "parliamentary_republic", Ideology = "liberalism" });
        var b = c.AddNation(new Nation { Id = 1, MapIndex = 2, Tag = "BBB", Population = 5_000_000, Gdp = 50f, CapitalLatitude = -35f, CapitalLongitude = 150f, Government = "parliamentary_republic", Ideology = "conservatism" });
        var dead = c.AddNation(new Nation { Id = 2, MapIndex = 3, Tag = "DED", Population = 0, Gdp = 0f });
        c.Fallen.Add(dead.Id);
        // Living nations must hold states so MovementFall.Decide does not retire them as stateless ghosts.
        c.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 1, Population = 5_000_000 });
        c.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 2, Population = 5_000_000 });
        var ally = c.AddNation(new Nation { Id = 3, MapIndex = 4, Tag = "ALY", Population = 5_000_000, Gdp = 50f, CapitalLatitude = 46f, CapitalLongitude = 11f, Government = "parliamentary_republic", Ideology = "liberalism" });
        c.States.Add(new ScenarioState { MapIndex = 4, NationMapIndex = 4, Population = 5_000_000 });
        c.InvalidateStateIndex();
        c.Defines.IntelEstimateError = 0f;
        // Create an inert neutral row between living A and B, and an inert dead row between A and DED.
        _ = CampaignDiplomacy.Get(c, a.Id, b.Id);
        _ = CampaignDiplomacy.Get(c, a.Id, dead.Id);
        // Also create an active alliance row (must NEVER be pruned!).
        CampaignDiplomacy.Get(c, a.Id, ally.Id).Stance = DiplomaticStance.Allied;

        c.IsPaused = false;
        c.Defines.HoursPerTick = 24;
        for (var day = 0; day < 28; day++) c.TickDay();
        RelationDormancy.Sweep(c);
        Check(CampaignDiplomacy.Peek(c, a.Id, ally.Id)?.Stance == DiplomaticStance.Allied,
            "RelationDormancy preserves active alliances");
        Check(CampaignDiplomacy.Peek(c, a.Id, dead.Id) is null && !c.DormantRelations.Values.Any(r => r.A == dead.Id || r.B == dead.Id),
            "RelationDormancy drops inert dead relation rows completely");
        Check(CampaignDiplomacy.Peek(c, a.Id, b.Id) is null && !c.DormantRelations.Values.Any(r => (r.A == a.Id && r.B == b.Id) || (r.A == b.Id && r.B == a.Id)),
            "RelationDormancy drops inert living baseline neutral rows on 28-day sweep");

        // 3. SocialMemory prunes dead & inert faded PublicImages.
        dead.RetiredDay = 10;
        a.PublicImages["nation:DED"] = new PublicImage { Esteem = 0.1f, Status = 0, LastReviewDay = 1 };
        a.PublicImages["nation:BBB"] = new PublicImage { Esteem = 0.005f, Incident = 0f, Status = 0, LastReviewDay = 0, LastIncidentDay = -1 };
        a.PublicImages["nation:ALY"] = new PublicImage { Esteem = 0.70f, Status = 1, LastReviewDay = 28 };
        for (var day = 0; day < 200; day++) c.TickDay();
        SocialMemory.Tick(c);
        Check(!a.PublicImages.ContainsKey("nation:DED"), "SocialMemory drops PublicImage of retired nation");
        Check(!a.PublicImages.ContainsKey("nation:BBB"), "SocialMemory prunes inert faded PublicImage of uninvolved nation");
        Check(a.PublicImages.ContainsKey("nation:ALY"), "SocialMemory preserves active Idealised/Stigmatised PublicImage");
    }
}
