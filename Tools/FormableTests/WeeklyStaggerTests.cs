using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Kernels;

/// <summary>
/// Weekly stagger and deterministic scheduling gate:
/// - Verifies weekly systems split deterministically off market day 0 (`ElapsedDays % 7`):
///   MarketSystem (day 0), BrainDrain (day 1), ResearchSystem (day 2),
///   NumericIntegrity (day 3), MovementRules weekly fall (day 4),
///   and InternalMigration per-nation `(ElapsedDays + nation.Id) % 7` cohort across days 0..6.
/// - Verifies strict 7-day cadence per entity, population conservation, MovementPeople on-due gating,
///   disabled-gate no-ops, and bit-identical CampaignFingerprint across identical seeds and StepFor vs TickDay.
/// </summary>
public static class WeeklyStaggerTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("weekly stagger: " + label);
        Console.WriteLine("PASS weekly stagger: " + label);
    }

    private static void SetElapsedDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!
            .GetSetMethod(true)!
            .Invoke(campaign, new object[] { day * 24L });

    [TestSuite("--weekly-stagger-only", Order = 2740, IncludeInDefaultRun = true,
        Description = "Deterministic Day 0..6 weekly system stagger slots, cohorts, conservation, and fingerprint parity.")]
    public static void Run()
    {
        ScheduleSlotsAreDisjointOffDayZero();
        BrainDrainRunsStrictlyOnSlotOne();
        ResearchRunsStrictlyOnSlotTwo();
        InternalMigrationStaggersAcrossCohortsWithConservation();
        MovementRulesWeeklyFallAndPeopleIndexGating();
        MultiSeedAndStagedStepDeterminism();
        MarketNativeKernelCampaignParity();
    }

    private static void ScheduleSlotsAreDisjointOffDayZero()
    {
        var market = MarketSystem.ScheduleSlot(7);
        var drain = BrainDrain.ScheduleSlot(7);
        var research = ResearchSystem.WeeklySlot;
        var integrity = Campaign.NumericIntegritySlot(7);
        var movFall = MovementRules.WeeklyFallSlot(7);

        var slots = new[] { market, drain, research, integrity, movFall };
        Check(market == 0 && drain == 1 && research == 2 && integrity == 3 && movFall == 4,
            "default 7-day slots map Market=0, BrainDrain=1, Research=2, Integrity=3, MovementFall=4");
        Check(slots.Distinct().Count() == slots.Length,
            "every shifted global weekly pass occupies its own distinct day of the week");

        Check(MarketSystem.ScheduleSlot(1) == 0 &&
              BrainDrain.ScheduleSlot(1) == 0 &&
              Campaign.NumericIntegritySlot(1) == 0 &&
              MovementRules.WeeklyFallSlot(1) == 0,
            "1-day custom cycles safely collapse all weekly slots to 0");
        Check(MarketSystem.ScheduleSlot(3) == 0 &&
              BrainDrain.ScheduleSlot(3) == 1 &&
              Campaign.NumericIntegritySlot(3) == 2 &&
              MovementRules.WeeklyFallSlot(3) == 2,
            "short mod cycles keep Market isolated on day 0");
    }

    private static void BrainDrainRunsStrictlyOnSlotOne()
    {
        var c = new Campaign(20261);
        var rich = new Nation
        {
            Id = 0, MapIndex = 100, Tag = "RCH", Name = "Richland", Continent = "Europe",
            Treasury = 500, Gdp = 2000, Population = 10_000_000, Stability = .85f,
            StandardOfLiving = .78f, Literacy = .95f, Productivity = 1.4f, CapitalStateMapIndex = 401
        };
        var poor = new Nation
        {
            Id = 1, MapIndex = 200, Tag = "POR", Name = "Poorland", Continent = "Europe",
            Treasury = 5, Gdp = 200, Population = 10_000_000, Stability = .2f,
            StandardOfLiving = .55f, Literacy = .70f, Productivity = .9f, CapitalStateMapIndex = 501
        };
        var richState = new ScenarioState
        {
            MapIndex = 401, NationMapIndex = 100, Population = 10_000_000,
            StandardOfLiving = .78f, Literacy = .95f, Stability = .85f, EmploymentTarget = .9f
        };
        var poorState = new ScenarioState
        {
            MapIndex = 501, NationMapIndex = 200, Population = 10_000_000,
            StandardOfLiving = .55f, Literacy = .70f, Stability = .6f, EmploymentTarget = .5f,
            WarDamage = .7f, LivelihoodStress = .6f
        };
        richState.PopulationGroups.Add(new PopulationGroup { Size = 10_000_000, Employment = .9f, Wealth = .6f, StandardOfLiving = .78f });
        poorState.PopulationGroups.Add(new PopulationGroup { Size = 10_000_000, Employment = .5f, Wealth = .3f, StandardOfLiving = .40f });
        c.Nations.Add(rich);
        c.Nations.Add(poor);
        c.States.Add(richState);
        c.States.Add(poorState);
        c.ConfigureSinglePlayer(rich.Id);

        var firedDays = new List<int>();
        var worldBefore = c.Nations.Sum(n => n.Population);
        for (var day = 1; day <= 14; day++)
        {
            SetElapsedDay(c, day);
            var beforeFp = CampaignFingerprint.Of(c);
            var beforePoorPop = poor.Population;
            BrainDrain.Tick(c);
            if (poor.Population != beforePoorPop)
            {
                firedDays.Add(day);
            }
            else
            {
                Check(CampaignFingerprint.Of(c) == beforeFp,
                    $"BrainDrain day {day} off-slot leaves world fingerprint untouched");
            }
            Check(c.Nations.Sum(n => n.Population) == worldBefore && c.States.Sum(s => s.Population) == worldBefore,
                $"BrainDrain day {day} conserves world population");
        }

        Check(firedDays.Count == 2 && firedDays[0] == 1 && firedDays[1] == 8,
            "BrainDrain executes strictly once every 7 days on slot 1 (days 1 and 8), never on day 0/7");

        c.Defines.BrainDrainEnabled = false;
        SetElapsedDay(c, 15);
        var disabledFp = CampaignFingerprint.Of(c);
        BrainDrain.Tick(c);
        Check(CampaignFingerprint.Of(c) == disabledFp,
            "BrainDrain feature toggle leaves world untouched even on scheduled slot");
    }

    private static void ResearchRunsStrictlyOnSlotTwo()
    {
        TechnologyCatalog.ResetCatalog();
        var c = Campaign.CreateBootstrap(20262);
        var nation = c.NationByTag("POL") ?? c.Nations[0];
        c.ConfigureSinglePlayer(nation.Id);
        var tech = TechnologyCatalog.All.First(t => ResearchSystem.Available(nation, t));
        nation.CurrentResearch = tech.Id;
        nation.ResearchProgress[tech.Id] = 0f;

        var firedDays = new List<int>();
        for (var day = 1; day <= 14; day++)
        {
            SetElapsedDay(c, day);
            var before = nation.ResearchProgress.GetValueOrDefault(tech.Id);
            var beforeKnown = nation.Technologies.Count;
            var beforeFp = CampaignFingerprint.Of(c);
            ResearchSystem.Tick(c);
            var after = nation.ResearchProgress.GetValueOrDefault(tech.Id);
            if (after > before || nation.Technologies.Count > beforeKnown)
            {
                firedDays.Add(day);
                nation.Technologies.Remove(tech.Id);
                nation.CurrentResearch = tech.Id;
            }
            else
            {
                Check(CampaignFingerprint.Of(c) == beforeFp,
                    $"ResearchSystem day {day} off-slot leaves world fingerprint untouched");
            }
        }

        Check(firedDays.Count == 2 && firedDays[0] == 2 && firedDays[1] == 9,
            "ResearchSystem advances strictly once every 7 days on slot 2 (days 2 and 9), never on day 0/7");

        c.Defines.Features["research_gate"] = false;
        c.Defines.FeatureSystems[nameof(ResearchSystem)] = "research_gate";
        SetElapsedDay(c, 16);
        var gatedFp = CampaignFingerprint.Of(c);
        ResearchSystem.Tick(c);
        Check(CampaignFingerprint.Of(c) == gatedFp,
            "ResearchSystem feature gate skips scheduled day without changing fingerprint");
    }

    private static void InternalMigrationStaggersAcrossCohortsWithConservation()
    {
        var c = new Campaign(20263);
        c.Defines.StaggerWeeklyEconomy = true;
        c.Defines.InternalMigrationEnabled = true;
        c.Defines.HouseholdDynamicsEnabled = true;

        const int nationCount = 7;
        for (var id = 0; id < nationCount; id++)
        {
            var tag = $"N0{id}";
            var nationMap = 100 + id;
            var poorStateMap = 1000 + id * 2;
            var goodStateMap = 1000 + id * 2 + 1;

            var nation = new Nation
            {
                Id = id, MapIndex = nationMap, Tag = tag, Name = "Nation " + id,
                Population = 10_000_000, Stability = .8f, StandardOfLiving = .6f,
                CapitalStateMapIndex = goodStateMap
            };
            var poor = new ScenarioState
            {
                Id = $"poor_{id}", MapIndex = poorStateMap, NationMapIndex = nationMap,
                Population = 5_000_000, StandardOfLiving = .20f, EmploymentTarget = .65f,
                Stability = .30f, WarDamage = .45f, LivelihoodStress = .60f
            };
            var good = new ScenarioState
            {
                Id = $"good_{id}", MapIndex = goodStateMap, NationMapIndex = nationMap,
                Population = 5_000_000, StandardOfLiving = .92f, EmploymentTarget = .95f,
                Stability = .90f, WarDamage = 0f, LivelihoodStress = 0f
            };
            poor.PopulationGroups.Add(new PopulationGroup { Culture = "cult_" + id, Religion = "secular", Size = 5_000_000 });
            good.PopulationGroups.Add(new PopulationGroup { Culture = "cult_" + id, Religion = "secular", Size = 5_000_000 });
            c.Nations.Add(nation);
            c.States.Add(poor);
            c.States.Add(good);
        }

        var worldBefore = c.States.Sum(s => s.Population);
        var movedDaysByNation = new List<int>[nationCount];
        for (var id = 0; id < nationCount; id++) movedDaysByNation[id] = new List<int>();

        for (var day = 1; day <= 14; day++)
        {
            SetElapsedDay(c, day);
            var beforeByNation = new long[nationCount];
            var reviewBefore = new long[nationCount];
            for (var id = 0; id < nationCount; id++)
            {
                beforeByNation[id] = c.States[id * 2].Population;
                reviewBefore[id] = c.States[id * 2].ReviewInternalMigration;
            }

            InternalMigration.Tick(c);
            Check(InternalMigration.MovedToday > 0, $"InternalMigration moves one daily cohort on day {day}");
            Check(c.States.Sum(s => s.Population) == worldBefore, $"InternalMigration day {day} conserves world total");

            var movedTodayCount = 0;
            for (var id = 0; id < nationCount; id++)
            {
                var poor = c.States[id * 2];
                var good = c.States[id * 2 + 1];
                Check(poor.Population + good.Population == 10_000_000,
                    $"InternalMigration day {day} conserves nation {id} total population");

                var expectedDue = InternalMigration.IsNationDue(c, id, 7, staggered: true);
                var delta = poor.Population - beforeByNation[id];
                if (delta < 0)
                {
                    Check(expectedDue, $"nation {id} only migrates on its OnItsDay cohort (day {day})");
                    Check(poor.ReviewInternalMigration < 0 && good.ReviewInternalMigration == -poor.ReviewInternalMigration,
                        $"nation {id} balanced ReviewInternalMigration on due day {day}");
                    movedTodayCount++;
                    movedDaysByNation[id].Add(day);
                }
                else
                {
                    Check(!expectedDue, $"due nation {id} must actually run internal migration on day {day}");
                    Check(poor.ReviewInternalMigration == reviewBefore[id],
                        $"off-day {day} preserves nation {id} previous ReviewInternalMigration readout");
                }
            }
            Check(movedTodayCount == 1, $"exactly 1 of 7 mod-7 cohorts runs InternalMigration on day {day}");
        }

        for (var id = 0; id < nationCount; id++)
        {
            var days = movedDaysByNation[id];
            Check(days.Count == 2 && days[1] - days[0] == 7,
                $"nation {id} runs twice across 14 days at exact 7-day spacing ({string.Join(",", days)})");
        }

        c.Defines.StaggerWeeklyEconomy = false;
        c.Defines.EconomyWeekStaggered = false;
        SetElapsedDay(c, 15);
        InternalMigration.Tick(c);
        Check(InternalMigration.MovedToday == 0,
            "un-staggered mode reverts cleanly off non-day-0 days");

        c.Defines.InternalMigrationEnabled = false;
        SetElapsedDay(c, 21);
        var offFp = CampaignFingerprint.Of(c);
        InternalMigration.Tick(c);
        Check(CampaignFingerprint.Of(c) == offFp,
            "InternalMigration disabled toggle leaves world untouched");
    }

    private static void MovementRulesWeeklyFallAndPeopleIndexGating()
    {
        // Confirm MovementRules weekly fall triggers on slot 4, not slot 0 (day 7).
        var fall = new Campaign(20264);
        fall.Defines.MovementExileBase = -10f;
        fall.Defines.MovementUndergroundBase = -10f;
        var survivor = new Nation { Id = 0, MapIndex = 10, Tag = "SRV", Name = "Survivor", Population = 1_000_000, CapitalStateMapIndex = 100 };
        var fallen = new Nation { Id = 1, MapIndex = 20, Tag = "FLN", Name = "Fallen", Population = 1_000, Stability = .5f };
        var region = new ScenarioState { Id = "srv_1", MapIndex = 100, NationMapIndex = 10, Population = 1_000_000 };
        region.PopulationGroups.Add(new PopulationGroup { Culture = "srv", Religion = "secular", Size = 1_000_000 });
        fall.Nations.Add(survivor);
        fall.Nations.Add(fallen);
        fall.States.Add(region);
        fall.Homelands[fallen.Id] = new List<int> { 200 };

        for (var day = 1; day <= 3; day++)
        {
            SetElapsedDay(fall, day);
            MovementRules.Tick(fall);
            Check(!fall.Fallen.Contains(fallen.Id) && fallen.Movement is null,
                $"weekly fall holds off before slot 4 (day {day})");
        }
        SetElapsedDay(fall, 4);
        MovementRules.Tick(fall);
        Check(fall.Fallen.Contains(fallen.Id) || fallen.Movement is not null,
            "MovementRules runs weekly fall review on slot 4 (day 4)");

        // Confirm EnsurePeopleIndexedStages is skipped inside MovementRules.TickStages when no movement is due today.
        var mov = new Campaign(20265);
        var host = new Nation { Id = 0, MapIndex = 10, Tag = "HST", Name = "Host", Population = 500_000, CapitalStateMapIndex = 100 };
        var mover = new Nation
        {
            Id = 2, MapIndex = 900, Tag = "MVR", Name = "Mover", Population = 0,
            Movement = new MovementState { Landless = true, HeadquartersStateMapIndex = 100, Peoples = { "mvr" } }
        };
        var state = new ScenarioState { Id = "hst_1", MapIndex = 100, NationMapIndex = 10, Population = 500_000 };
        state.PopulationGroups.Add(new PopulationGroup { Culture = "mvr", Religion = "secular", Size = 500_000 });
        mov.Nations.Add(host);
        mov.Nations.Add(mover);
        mov.States.Add(state);

        // Id = 2 -> OnItsDay cycle 7 is day 5 ((5 + 2) % 7 == 0). Day 1 is NOT due.
        SetElapsedDay(mov, 1);
        MovementRules.Tick(mov);
        Check(MovementRules.PeopleIndexBuilds(mov) == 0,
            "MovementRules skips rebuilding the people index on days with zero due movements");

        SetElapsedDay(mov, 5);
        MovementRules.Tick(mov);
        Check(MovementRules.PeopleIndexBuilds(mov) == 1,
            "MovementRules builds the people index on the movement's OnItsDay");
    }

    private static void MultiSeedAndStagedStepDeterminism()
    {
        static (ulong Fingerprint, ulong RandomState, Campaign World) RunDays(int seed, int days, bool staged)
        {
            var c = Campaign.CreateBootstrap(seed);
            c.Defines.HoursPerTick = 24;
            c.IsPaused = false;
            for (var d = 0; d < days; d++)
            {
                if (!staged)
                {
                    c.TickDay();
                }
                else
                {
                    var guard = 0;
                    while (!c.StepFor(1.25) && ++guard < 10000) { }
                    Check(!c.MidStep, $"bounded StepFor completed day {d + 1}");
                }
            }
            return (CampaignFingerprint.Of(c), c.Random.State, c);
        }

        var (fp7A, rng7A, world7A) = RunDays(20261126, 7, staged: false);
        var (fp7B, rng7B, _) = RunDays(20261126, 7, staged: false);
        Check(fp7A == fp7B && rng7A == rng7B,
            "identical seed and input yield bit-identical day-7 CampaignFingerprint and RNG state across full staggered week");
        Check(NumericIntegrity.FirstLiveIssue(world7A) is null,
            "staggered week passes NumericIntegrity with zero non-finite fields at day 7");

        var (fp14A, rng14A, world14A) = RunDays(20261126, 14, staged: false);
        var (fp14B, rng14B, _) = RunDays(20261126, 14, staged: false);
        var (fp14Staged, rng14Staged, _) = RunDays(20261126, 14, staged: true);
        Check(fp14A == fp14B && rng14A == rng14B,
            "identical seed yields bit-identical day-14 CampaignFingerprint across two full weekly cycles");
        Check(fp14A == fp14Staged && rng14A == rng14Staged,
            "bounded StepFor yields preserve exact bit-for-bit CampaignFingerprint parity vs TickDay across staggered weekly cycles");
        Check(NumericIntegrity.FirstLiveIssue(world14A) is null,
            "staggered two-week run passes NumericIntegrity with zero non-finite fields");
    }

    private static void MarketNativeKernelCampaignParity()
    {
        static (ulong Fingerprint, ulong RandomState, Campaign World) RunWeek(int seed, bool disableNative)
        {
            var prevSim = NativeKernelsLoader.SimulateUnavailableForTests;
            NativeKernelsLoader.SimulateUnavailableForTests = disableNative;
            try
            {
                var c = Campaign.CreateBootstrap(seed);
                c.Defines.HoursPerTick = 24;
                c.IsPaused = false;
                for (var d = 0; d < 14; d++) c.TickDay();
                return (CampaignFingerprint.Of(c), c.Random.State, c);
            }
            finally
            {
                NativeKernelsLoader.SimulateUnavailableForTests = prevSim;
            }
        }

        var managed = RunWeek(20261127, disableNative: true);
        var native = RunWeek(20261127, disableNative: false);
        Check(native.Fingerprint == managed.Fingerprint && native.RandomState == managed.RandomState,
            $"MarketSystem native kernels ({NativeKernelsLoader.IsAvailable}) match C# fallback bit-for-bit over 14d ({native.Fingerprint:X16})");
    }
}
