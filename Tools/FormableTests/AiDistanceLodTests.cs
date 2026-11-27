using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Kernels;

/// <summary>
/// Verifies Approved Point 4: Two-Tier AI Distance LOD and Quiet-Front Caching.
/// Asserts active-tier classification rules, deterministic 4-slot weekly sharding
/// (including 14-day CalmCadences zero-starvation parity), OpinionSystem &amp; Statecraft
/// background LOD, Quiet-Front cache hits/invalidations, campaign isolation, and
/// ST == MT == snapshot determinism.
/// </summary>
public static class AiDistanceLodTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("ai distance lod: " + label);
        Console.WriteLine("PASS ai distance lod: " + label);
    }

    [TestSuite("--ai-distance-lod-only", Order = 2750, IncludeInDefaultRun = true,
        Description = "Two-tier AI distance LOD, 4-slot weekly sharding, quiet-front caching, kernel parity, and ST/MT determinism.")]
    public static void Run()
    {
        TestActiveTierRules();
        TestBackgroundFourSlotWeeklySharding();
        TestCalmCadenceFourteenDayZeroStarvation();
        TestOpinionSystemAndNotorietyLod();
        TestOpinionKernelBitParity();
        TestQuietFrontCachingAndInvalidations();
        TestCrossCampaignIsolation();
        TestBootstrapDeterminismAndSnapshotParity();
    }

    static Campaign CreateLodWorld(out List<Nation> gps, out List<Nation> minors)
    {
        var campaign = new Campaign(71401);
        gps = new List<Nation>();
        minors = new List<Nation>();
        for (var i = 0; i < 12; i++)
        {
            var gp = campaign.AddNation(new Nation
            {
                Tag = $"GP{i}",
                Name = $"GreatPower{i}",
                MapIndex = 10 + i,
                Population = 50_000_000,
                Gdp = 2000f - i * 100f,
                Treasury = 500f,
                Stability = 0.75f,
                Ideology = "liberalism",
                Region = "europe",
                CapitalLatitude = 48f,
                CapitalLongitude = 2f + i * 2f,
                AssessedDay = 0,
                AssessedStrength = 10f
            });
            gps.Add(gp);
        }

        var coords = new (float Lat, float Lon, string Region)[]
        {
            (45f, 10f, "reg_alpha"),
            (-35f, 140f, "reg_beta"),
            (-15f, -60f, "reg_gamma"),
            (35f, -100f, "reg_delta"),
            (10f, 75f, "reg_epsilon")
        };
        for (var i = 0; i < coords.Length; i++)
        {
            var m = campaign.AddNation(new Nation
            {
                Tag = $"MN{i}",
                Name = $"Minor{i}",
                MapIndex = 50 + i,
                Population = 300_000,
                Gdp = 5f,
                Treasury = 20f,
                Stability = 0.7f,
                Ideology = "liberalism",
                Region = coords[i].Region,
                CapitalLatitude = coords[i].Lat,
                CapitalLongitude = coords[i].Lon,
                AssessedDay = 0,
                AssessedStrength = 2f
            });
            minors.Add(m);
        }
        return campaign;
    }

    static void SetDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, Math.Max(0, day) * 24L);

    static void TestActiveTierRules()
    {
        var c = CreateLodWorld(out var gps, out var minors);
        var a = minors[0];
        var b = minors[1];
        Check(a.Id == 12 && b.Id == 13, "test minors have IDs 12 and 13");

        Check(!AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: false),
            "distant peaceful minor-minor pair starts in Background tier");

        a.IsHuman = true;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: false),
            "human involvement promotes pair to Active tier");
        a.IsHuman = false;

        Check(AiStatecraft.IsActiveDiplomacyTier(c, gps[0], a, null, bordersKnown: true, borders: false),
            "great power involvement promotes pair to Active tier");

        a.Gdp = 450f;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: false),
            "major GDP (>= 400) promotes pair to Active tier");
        a.Gdp = 5f;

        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: true),
            "land border promotes pair to Active tier");

        var oldRegion = b.Region;
        b.Region = a.Region;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: false),
            "shared region promotes pair to Active tier");
        b.Region = oldRegion;

        var oldLat = b.CapitalLatitude;
        var oldLon = b.CapitalLongitude;
        b.CapitalLatitude = a.CapitalLatitude + 1f;
        b.CapitalLongitude = a.CapitalLongitude + 1f;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: false),
            "capital proximity within WitnessNearKm promotes pair to Active tier");
        b.CapitalLatitude = oldLat;
        b.CapitalLongitude = oldLon;

        b.OverlordId = a.Id;
        b.SubjectType = "puppet";
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: false),
            "overlord-subject link promotes pair to Active tier");
        b.SubjectType = "";
        b.OverlordId = 0;

        a.TradePartners[b.Id] = 12f;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, null, bordersKnown: true, borders: false),
            "active trade partner promotes pair to Active tier");
        a.TradePartners.Clear();

        var rel = CampaignDiplomacy.Get(c, a.Id, b.Id);
        Check(!AiStatecraft.IsActiveDiplomacyTier(c, a, b, rel, bordersKnown: true, borders: false),
            "empty neutral relation remains in Background tier");

        rel.Stance = DiplomaticStance.Allied;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, rel, bordersKnown: true, borders: false),
            "allied stance promotes pair to Active tier");
        rel.Stance = DiplomaticStance.AtWar;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, rel, bordersKnown: true, borders: false),
            "active war stance promotes pair to Active tier");
        rel.Stance = DiplomaticStance.Truce;
        rel.TruceUntil = 20;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, rel, bordersKnown: true, borders: false),
            "active truce promotes pair to Active tier");
        rel.TruceUntil = 0;
        rel.Stance = DiplomaticStance.Neutral;

        rel.EmbargoByA = true;
        Check(AiStatecraft.IsActiveDiplomacyTier(c, a, b, rel, bordersKnown: true, borders: false),
            "active embargo promotes pair to Active tier");
        rel.EmbargoByA = false;
    }

    static void TestBackgroundFourSlotWeeklySharding()
    {
        var c = CreateLodWorld(out var gps, out var minors);
        var identity = c.FeatureEnabled("state_identity");

        // Four distant minor-minor pairs covering all 4 (idA + idB) & 3 residues:
        // (8,9)->1, (8,10)->2, (8,11)->3, (9,11)->0
        var pairs = new (Nation A, Nation B, int ExpectedSlot)[]
        {
            (minors[1], minors[3], (minors[1].Id + minors[3].Id) & 3), // 9+11=20 -> 0
            (minors[0], minors[1], (minors[0].Id + minors[1].Id) & 3), // 8+9=17  -> 1
            (minors[0], minors[2], (minors[0].Id + minors[2].Id) & 3), // 8+10=18 -> 2
            (minors[0], minors[3], (minors[0].Id + minors[3].Id) & 3), // 8+11=19 -> 3
        };
        var walkedOnWeek = new int[pairs.Length];
        Array.Fill(walkedOnWeek, -1);
        var walkedCount = new int[pairs.Length];

        var expectedTotalWalks = 0;
        for (var week = 0; week < 4; week++)
        {
            SetDay(c, week * 7);
            for (var pi = 0; pi < pairs.Length; pi++)
            {
                var p = pairs[pi];
                var walked = AiStatecraft.ReviewWeeklyPair(c, p.A, p.B, borders: false, myStrength: 2f,
                    identityOn: identity, out _);
                if (walked)
                {
                    walkedCount[pi]++;
                    walkedOnWeek[pi] = week;
                    expectedTotalWalks++;
                }
            }

            // Active GP pair walks on week 0 immediately; if unchanged it won't re-write later,
            // verify IsPairDueThisCycle stays true on ALL 4 weeks for Active tier.
            Check(AiStatecraft.IsPairDueThisCycle(c, gps[0], minors[0], null, bordersKnown: true,
                    borders: false, respectStatecraftCalmCadence: false),
                $"active GP pair due on week {week}");
        }

        Check(expectedTotalWalks == 4 && AiStatecraft.WeeklyPairWalks(c) == 4,
            "4 distinct background pairs collectively perform exactly 4 walks across 4 weeks");
        for (var pi = 0; pi < pairs.Length; pi++)
        {
            Check(walkedCount[pi] == 1 && walkedOnWeek[pi] == pairs[pi].ExpectedSlot,
                $"pair ({pairs[pi].A.Id},{pairs[pi].B.Id}) walked exactly once on slot week {pairs[pi].ExpectedSlot} (got week {walkedOnWeek[pi]}, count={walkedCount[pi]})");
        }
    }

    static void TestCalmCadenceFourteenDayZeroStarvation()
    {
        // Calm minor nations are only visited when (elapsedDays + actorId) % 14 == 0.
        // Prove across all 14 actorId residues and all targetId & 3 slots over 112 days
        // that outerCadenceWeeks=2 never starves any pair and hits exact 28-day cadence.
        var checkedPairs = 0;
        for (var actorId = 0; actorId < 14; actorId++)
        {
            for (var otherId = 100; otherId < 108; otherId++)
            {
                var hits = 0;
                for (var day = 0; day < 112; day++)
                {
                    if ((day + actorId) % 14 != 0) continue;
                    if (AiStatecraft.IsBackgroundSlotDueForWeek(day / 7, actorId, otherId, outerCadenceWeeks: 2))
                        hits++;
                }
                if (hits != 4)
                    Check(false, $"14d calm actor {actorId} vs {otherId} hits exact 28-day cadence (4 hits/112d, got {hits})");
                checkedPairs++;
            }
        }
        Check(checkedPairs == 112, $"all {checkedPairs} 14d calm actor/target pairs hit exact 28-day cadence (4 hits/112d, 0 starved)");
    }

    static void TestOpinionSystemAndNotorietyLod()
    {
        var c = CreateLodWorld(out var gps, out var minors);
        var a = minors[0]; // 12
        var b = minors[1]; // 13 -> (12+13)&3 == 1; (12+13)%7 == 4 -> weekly due day (today+4)%7==0 => 3, 10, 17, 24

        var bgRel = CampaignDiplomacy.Get(c, a.Id, b.Id);
        OpinionSystem.Remember(c, bgRel, "border_friction", 1f, settle: false);
        var activeRel = CampaignDiplomacy.Get(c, gps[0].Id, a.Id);
        OpinionSystem.Remember(c, activeRel, "border_friction", 1f, settle: false);

        var bgOpinion0 = bgRel.Opinion;
        var gpOpinion0 = activeRel.Opinion;

        // Day 3 -> week 0, (3 + 25) % 7 == 0. Background slot 1 should skip settle.
        SetDay(c, 3);
        foreach (var _ in OpinionSystem.TickStages(c)) { }
        Check(bgRel.Opinion == bgOpinion0, "background pair skips settlement on off-slot week 0");

        // Day 10 -> week 1, matching slot 1 for (12,13): background pair settles now!
        SetDay(c, 10);
        foreach (var _ in OpinionSystem.TickStages(c)) { }
        Check(bgRel.Opinion != bgOpinion0, "background pair settles accurately on its matching slot week 1");

        // Expired truce settles immediately regardless of slot week!
        bgRel.Stance = DiplomaticStance.Truce;
        bgRel.TruceUntil = 14;
        SetDay(c, 14); // week 2 (off-slot for (12,13))
        foreach (var _ in OpinionSystem.TickStages(c)) { }
        Check(bgRel.Stance != DiplomaticStance.Truce, "expired truce refreshes and settles immediately even on off-slot day");

        // Notoriety LOD check via Statecraft.TickStages (WeeklySlot == 5: day 12 skips background (12,14) slot 2, day 19 updates ALL slots on week 2 regardless of (A+B)%7!)
        var cNot = CreateLodWorld(out var nGps, out var nMinors);
        var mA = nMinors[0]; // id=12 (45, 10)
        var mB = nMinors[2]; // id=14 -> (12+14)&3 == 2 (week 2), (12+14)%7 == 5 (!= 2, proves no 1/7 mod-7 starvation)
        mB.CapitalLatitude = mA.CapitalLatitude + 18.5f; // ~2057 km (> 1500 km WitnessNearKm => Background tier, < 3000 km WitnessRegionKm => non-zero concern)
        mB.CapitalLongitude = mA.CapitalLongitude;
        var bgNotRel = CampaignDiplomacy.Get(cNot, mA.Id, mB.Id);
        var actNotRel = CampaignDiplomacy.Get(cNot, nGps[1].Id, mA.Id);
        mA.Notoriety = 60f;
        nGps[1].CapitalLatitude = mA.CapitalLatitude + 1f;
        nGps[1].CapitalLongitude = mA.CapitalLongitude + 1f;

        // Day 7 is NOT Statecraft.WeeklySlot (5) -> skipped completely even for active pairs.
        SetDay(cNot, 7);
        foreach (var _ in Statecraft.TickStages(cNot)) { }
        Check(!OpinionSystem.Has(actNotRel, "notoriety") && !OpinionSystem.Has(bgNotRel, "notoriety"),
            $"day 7 (!= WeeklySlot {Statecraft.WeeklySlot}) skips weekly notoriety review");

        // Day 12 (7 + 5 == WeeklySlot, week 1): active GP updates immediately, distant minor pair (slot 2) waits.
        SetDay(cNot, 7 + Statecraft.WeeklySlot);
        foreach (var _ in Statecraft.TickStages(cNot)) { }
        Check(OpinionSystem.Has(actNotRel, "notoriety") && !OpinionSystem.Has(bgNotRel, "notoriety"),
            "day 12 (WeeklySlot) updates active GP notoriety immediately while distant minor pair waits for slot week 2");

        // Day 19 (14 + 5 == WeeklySlot, week 2): matching 4-week slot processes background pair cleanly without (A+B)%7 starvation.
        SetDay(cNot, 14 + Statecraft.WeeklySlot);
        foreach (var _ in Statecraft.TickStages(cNot)) { }
        Check(OpinionSystem.Has(bgNotRel, "notoriety"),
            "day 19 (WeeklySlot + slot week 2) applies background minor notoriety cleanly across all mod-7 residues");
    }

    static Campaign CreateKernelParityWorld()
    {
        var c = CreateLodWorld(out var gps, out var minors);
        // Set day = 500 (500 % 7 == 3; geopoliticsRamp > 0 since 500 > 365).
        SetDay(c, 500);
        gps[0].Ideology = "liberalism";
        gps[1].Ideology = "nationalism";
        gps[4].Ideology = "socialism";
        gps[10].Ideology = "conservatism";
        gps[11].Ideology = "nationalism";
        gps[0].DeclaredReligion = "catholic";
        gps[4].DeclaredReligion = "catholic";
        gps[4].DeclaredCulture = gps[0].DeclaredCulture = "western";

        // (0,4), (0,11), (1,10) all have (A+B)%7 == 4 -> (500 + 4) % 7 == 0 -> settled on day 500!
        var pairs = new (Nation Left, Nation Right)[] { (gps[0], gps[4]), (gps[0], gps[11]), (gps[1], gps[10]) };
        foreach (var (left, right) in pairs)
        {
            var rel = CampaignDiplomacy.Get(c, left.Id, right.Id);
            OpinionSystem.Remember(c, rel, "border_friction", 1f, settle: false);
            OpinionSystem.Remember(c, rel, "nuclear_test", 1.2f, settle: false);
            OpinionSystem.Remember(c, rel, "attacked_a_fellow_member", 1f, settle: false);
            // Force an expired reason + unknown id to exercise EvalReasonDecay pruning + compaction.
            rel.Reasons.Add(new OpinionReason { Id = "nuclear_test", GrantedDay = 10, ExpiresDay = 200, Scale = 1f, Stacks = 1 });
            rel.Reasons.Add(new OpinionReason { Id = "nonexistent_reason_test", GrantedDay = 400, ExpiresDay = 600, Scale = 1f, Stacks = 1 });
            if (SocialMemory.ImagesEnabled(c))
            {
                var img = SocialMemory.Image(left, SocialMemory.NationKey(right), create: true);
                if (img is not null) img.Esteem = -12.5f;
            }
        }
        return c;
    }

    static void TestOpinionKernelBitParity()
    {
        var prevSim = NativeKernelsLoader.SimulateUnavailableForTests;
        try
        {
            var cManaged = CreateKernelParityWorld();
            NativeKernelsLoader.SimulateUnavailableForTests = true;
            foreach (var _ in OpinionSystem.TickStages(cManaged)) { }

            var cNative = CreateKernelParityWorld();
            NativeKernelsLoader.SimulateUnavailableForTests = false;
            foreach (var _ in OpinionSystem.TickStages(cNative)) { }

            var cScalar = CreateKernelParityWorld();
            NativeKernelsLoader.SimulateUnavailableForTests = true;
            var rScalar = CampaignDiplomacy.Get(cScalar, 0, 4);
            OpinionSystem.Settle(cScalar, rScalar, explain: false);

            var fpManaged = CampaignFingerprint.Of(cManaged);
            var fpNative = CampaignFingerprint.Of(cNative);
            Check(fpManaged == fpNative,
                $"OpinionSystem managed vs native CampaignFingerprint matches bit-for-bit ({fpManaged:X16} == {fpNative:X16})");

            foreach (var (a, b) in new[] { (0, 4), (0, 11), (1, 10) })
            {
                var rM = CampaignDiplomacy.Peek(cManaged, a, b)!;
                var rN = CampaignDiplomacy.Peek(cNative, a, b)!;
                int bitsM = BitConverter.SingleToInt32Bits(rM.Opinion);
                int bitsN = BitConverter.SingleToInt32Bits(rN.Opinion);
                Check(bitsM == bitsN && rM.Reasons.Count == rN.Reasons.Count,
                    $"pair ({a},{b}) native vs managed opinion exact bits (0x{bitsN:X8} == 0x{bitsM:X8}, reasons={rN.Reasons.Count})");
            }

            var rBatch04 = CampaignDiplomacy.Peek(cNative, 0, 4)!;
            Check(BitConverter.SingleToInt32Bits(rBatch04.Opinion) == BitConverter.SingleToInt32Bits(rScalar.Opinion) &&
                  rBatch04.Reasons.Count == rScalar.Reasons.Count,
                $"batched OpinionSystem kernel matches scalar Explain/Settle bit-for-bit ({rBatch04.Opinion:R} == {rScalar.Opinion:R})");
        }
        finally
        {
            NativeKernelsLoader.SimulateUnavailableForTests = prevSim;
        }
    }

    static Campaign CreateQuietFrontWorld(out Army army1, out Army army2)
    {
        var states = new ushort[] { 0, 1, 1, 2, 3 };
        var nations = new ushort[] { 0, 10, 10, 20, 30 };
        var controllers = new ushort[] { 0, 10, 10, 20, 30 };
        var occupation = new float[] { 0f, 0f, 0f, 0f, 0f };
        var neighbors = new int[][]
        {
            Array.Empty<int>(),
            new[] { 2 },
            new[] { 1, 3 },
            new[] { 2 },
            Array.Empty<int>()
        };
        var topo = BuildTopology(states, nations, neighbors);

        var c = new Campaign(71402);
        c.AddNation(new Nation { Id = 0, MapIndex = 10, Tag = "QFA", Population = 2_000_000, Gdp = 100 });
        c.AddNation(new Nation { Id = 1, MapIndex = 20, Tag = "QFB", Population = 2_000_000, Gdp = 100 });
        c.AddNation(new Nation { Id = 2, MapIndex = 30, Tag = "QFC", Population = 2_000_000, Gdp = 100 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, topo);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(c, controllers);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(c, occupation);

        var war = new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 };
        war.AttackerFront.Add(2);
        war.AttackerFront.Add(1);
        war.DefenderFront.Add(3);
        c.Wars.Add(war);

        army1 = new Army
        {
            Id = 1, NationId = 0, ProvinceId = 1, OrderProvinceId = 1,
            AssignedWarId = war.Id, OrderKind = ArmyOrder.DefendFront,
            OrderIssuedDay = 0, Divisions = 1f, Personnel = 1000
        };
        army2 = new Army
        {
            Id = 2, NationId = 0, ProvinceId = 2, OrderProvinceId = 2,
            AssignedWarId = war.Id, OrderKind = ArmyOrder.DefendFront,
            OrderIssuedDay = 0, Divisions = 1f, Personnel = 1000
        };
        var enemy = new Army
        {
            Id = 3, NationId = 1, ProvinceId = 3, OrderProvinceId = 3,
            AssignedWarId = war.Id, OrderKind = ArmyOrder.DefendFront,
            Divisions = 1f, Personnel = 1000
        };
        c.Armies.Add(army1);
        c.Armies.Add(army2);
        c.Armies.Add(enemy);
        return c;
    }

    static void TestQuietFrontCachingAndInvalidations()
    {
        var c = CreateQuietFrontWorld(out var army1, out var army2);
        var twinUncached = CreateQuietFrontWorld(out _, out _);

        SetDay(c, 5); // past FrontSettleDays=3
        FrontManager.Tick(c);
        var hitsAfterDay5 = FrontManager.QuietFrontCacheHits(c);
        var posts0 = FrontManager.PostedProvinces(c).ToArray();
        var armiesAt2Day5 = FrontManager.PostedArmies(c, 2).Select(a => a.Id).ToArray();
        var reposts0 = FrontManager.Reposts;
        Check(hitsAfterDay5 == 0, "first quiet-front arrange populates cache cleanly (0 hits)");

        // Day 6: identical static state -> cache HIT! Compare fingerprint against fresh uncached twin on Day 6.
        SetDay(c, 6);
        FrontManager.Tick(c);
        var hitsAfterDay6 = FrontManager.QuietFrontCacheHits(c);
        var fpCachedDay6 = CampaignFingerprint.Of(c);
        SetDay(twinUncached, 6);
        FrontManager.Tick(twinUncached);
        var fpUncachedDay6 = CampaignFingerprint.Of(twinUncached);
        Check(hitsAfterDay6 > hitsAfterDay5, $"consecutive idle day hits quiet-front cache (hits {hitsAfterDay5} -> {hitsAfterDay6})");
        Check(FrontManager.PostedProvinces(c).SequenceEqual(posts0) &&
              FrontManager.PostedArmies(c, 2).Select(a => a.Id).SequenceEqual(armiesAt2Day5),
            "cached restore preserves exact PostedProvinces ordering and PostedArmies buckets");
        Check(FrontManager.Reposts == reposts0 && fpCachedDay6 == fpUncachedDay6,
            $"quiet-front cache hit produces bit-identical CampaignFingerprint ({fpCachedDay6:X16} == {fpUncachedDay6:X16})");

        // Simulated foreign war MapRevision bump on unrelated province 4 -> zero holes/pockets still hits cache!
        c.SetProvinceController(4, 31);
        SetDay(c, 7);
        FrontManager.Tick(c);
        var hitsAfterForeignFlip = FrontManager.QuietFrontCacheHits(c);
        Check(hitsAfterForeignFlip > hitsAfterDay6,
            $"foreign MapRevision change does not invalidate quiet hole-free front (hits {hitsAfterDay6} -> {hitsAfterForeignFlip})");

        // OwnershipRevision invalidation -> cache MISS!
        c.InvalidateStateIndex();
        SetDay(c, 8);
        FrontManager.Tick(c);
        var hitsAfterOwnershipBump = FrontManager.QuietFrontCacheHits(c);
        Check(hitsAfterOwnershipBump == hitsAfterForeignFlip,
            "OwnershipRevision bump invalidates quiet-front cache cleanly");

        // Settle-day NextWakeDay invalidation check:
        army1.OrderIssuedDay = 8; // FrontSettleDays = 3 -> settles on day 11
        SetDay(c, 9);
        FrontManager.Tick(c); // caches with NextWakeDay = 11
        var hitsDay9 = FrontManager.QuietFrontCacheHits(c);
        SetDay(c, 10);
        FrontManager.Tick(c); // day 10 < 11 -> both sides hit (+2)
        var hitsDay10 = FrontManager.QuietFrontCacheHits(c);
        Check(hitsDay10 == hitsDay9 + 2, $"quiet front hits cache while army remains within settle window (hits {hitsDay9} -> {hitsDay10})");
        SetDay(c, 11);
        FrontManager.Tick(c); // day 11 == 11 -> Nation 0 misses on settle day while Nation 1 still hits (+1)
        var hitsDay11 = FrontManager.QuietFrontCacheHits(c);
        Check(hitsDay11 == hitsDay10 + 1,
            $"quiet front automatically invalidates attacker on army settle day while defender keeps hitting (hits {hitsDay10} -> {hitsDay11})");

        // Division / composition mutation invalidation:
        SetDay(c, 12);
        FrontManager.Tick(c); // warm both on day 12 (+1 for newly warmed attacker)
        var hitsDay12 = FrontManager.QuietFrontCacheHits(c);
        army2.Divisions = 3f;
        SetDay(c, 13);
        FrontManager.Tick(c); // Frontline division change invalidates attacker arming AND defender EnemyPower surface hash (+0)
        var hitsDay13 = FrontManager.QuietFrontCacheHits(c);
        Check(hitsDay13 == hitsDay12,
            $"frontline division change invalidates both attacker armies and defender EnemyPower cache (hits {hitsDay12} -> {hitsDay13})");

        // Marching Latitude/Longitude movement without changing ProvinceId must also invalidate!
        SetDay(c, 14);
        FrontManager.Tick(c); // warm both on day 14
        var hitsDay14 = FrontManager.QuietFrontCacheHits(c);
        army1.Latitude += 0.5f;
        army1.Longitude += 0.5f;
        SetDay(c, 15);
        FrontManager.Tick(c); // Nation 0 misses on lat/lon shift while Nation 1 still hits (+1)
        var hitsDay15 = FrontManager.QuietFrontCacheHits(c);
        Check(hitsDay15 == hitsDay14 + 1,
            $"army Latitude/Longitude change invalidates attacker quiet-front cache even at same ProvinceId (hits {hitsDay14} -> {hitsDay15})");
    }

    static void TestCrossCampaignIsolation()
    {
        var c1 = CreateLodWorld(out _, out var m1);
        var c2 = CreateLodWorld(out _, out var m2);
        SetDay(c1, 7); // week 1 -> matches (8,9)
        SetDay(c2, 0); // week 0 -> skips (8,9)

        var w1 = AiStatecraft.ReviewWeeklyPair(c1, m1[0], m1[1], borders: false, 2f,
            c1.FeatureEnabled("state_identity"), out _);
        var w2 = AiStatecraft.ReviewWeeklyPair(c2, m2[0], m2[1], borders: false, 2f,
            c2.FeatureEnabled("state_identity"), out _);

        Check(w1 && !w2 && AiStatecraft.WeeklyPairWalks(c1) == 1 && AiStatecraft.WeeklyPairWalks(c2) == 0,
            "separate campaigns maintain independent LOD schedules and scan memory");
    }

    static ProvinceTopology BuildEarthTopology()
    {
        MapRasterCatalog.ResetCatalog();
        TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var msg), msg);
        return map;
    }

    static void TestBootstrapDeterminismAndSnapshotParity()
    {
        var savedOverride = DeterministicParallel.MaxThreadsOverride;
        var savedSerial = DeterministicParallel.ForceSerial;
        try
        {
            DeterministicParallel.ForceSerial = false;

            DeterministicParallel.MaxThreadsOverride = 1;
            var serial = Campaign.CreateBootstrap(42);
            serial.AttachProvinceTopology(BuildEarthTopology());
            serial.IsPaused = false;
            serial.Defines.HoursPerTick = 24;
            for (var d = 0; d < 28; d++) serial.TickDay();
            var serialIssue = NumericIntegrity.FirstLiveIssue(serial);
            var serialFp = CampaignFingerprint.Of(serial);

            DeterministicParallel.MaxThreadsOverride = 0;
            var parallel = Campaign.CreateBootstrap(42);
            parallel.AttachProvinceTopology(BuildEarthTopology());
            parallel.IsPaused = false;
            parallel.Defines.HoursPerTick = 24;
            for (var d = 0; d < 28; d++) parallel.TickDay();
            var parallelIssue = NumericIntegrity.FirstLiveIssue(parallel);
            var parallelFp = CampaignFingerprint.Of(parallel);

            Check(serialIssue is null && parallelIssue is null,
                "28d world with Earth topology has zero numeric integrity issues under ST and MT");
            Check(serialFp == parallelFp,
                $"28d Earth CampaignFingerprint matches 1-thread ({serialFp:X16}) == N-thread ({parallelFp:X16})");

            // Snapshot mid-cycle save + reload at day 10 preserves exact day-28 fingerprint.
            var split = Campaign.CreateBootstrap(42);
            split.AttachProvinceTopology(BuildEarthTopology());
            split.IsPaused = false;
            split.Defines.HoursPerTick = 24;
            for (var d = 0; d < 10; d++) split.TickDay();
            var folder = Path.Combine(Path.GetTempPath(), "dof-lod-snap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                SaveSystem.Save(folder, "snap", split, "");
                var loaded = SaveSystem.Load(folder, "snap", "");
                Check(loaded.Success, $"day-10 save/reload succeeded ({loaded.Warning})");
                var reloaded = loaded.Campaign;
                reloaded.AttachProvinceTopology(BuildEarthTopology());
                reloaded.IsPaused = false;
                reloaded.Defines.HoursPerTick = 24;
                for (var d = 10; d < 28; d++) reloaded.TickDay();
                var reloadedFp = CampaignFingerprint.Of(reloaded);
                Check(reloadedFp == serialFp,
                    $"day-10 snapshot save/reload matches continuous 28d fingerprint ({reloadedFp:X16} == {serialFp:X16})");
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        }
        finally
        {
            DeterministicParallel.MaxThreadsOverride = savedOverride;
            DeterministicParallel.ForceSerial = savedSerial;
        }
    }

    static ProvinceTopology BuildTopology(ushort[] states, ushort[] owners, int[][] neighbors)
    {
        var constructor = typeof(ProvinceTopology).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (ProvinceTopology)constructor.Invoke(new object[]
        {
            new ushort[1], states, owners, new int[states.Length], neighbors, 1, 1
        });
    }
}
