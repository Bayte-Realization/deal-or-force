using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Regression tests for three live-playtest warfare simulation bugs:
/// 1. Encircled zero-morale divisions must NEVER retreat onto enemy-controlled ground
///    (even enemy-occupied homeland or rebel slip-away) and must surrender cleanly with POWs.
/// 2. Armies entering or standing on enemy-occupied third-party ground in a 3-way war must
///    capture ProvinceController cleanly (and liberate for allies when appropriate).
/// 3. Front lines (Whole front, One border, drawn lines, FrontManager) must span allied and
///    friendly-controlled staging ground without snapping off at own borders.
/// </summary>
static class WarfareFrontAndPocketFixTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("warfare pocket/front fix: " + label);
        Console.WriteLine("PASS warfare pocket/front fix: " + label);
    }

    [TestSuite("--warfare-pocket-front-only", IncludeInDefaultRun = true,
        Description = "Encircled pocket surrender, 3-way occupation capture, and allied front staging")]
    public static void Run()
    {
        EncircledRegularDivisionSurrendersInsteadOfRetreatingOntoEnemyGround();
        EncircledRebelDivisionNeverSlipsOntoEnemyControlledGround();
        StrandedZeroMoraleUnitOnEnemyGroundSurrendersOnTick();
        ThreeWayWarStandingArmyCapturesEnemyOccupiedThirdPartyProvince();
        ThreeWayWarAdjacentAdvanceCapturesEnemyOccupiedThirdPartyProvince();
        AlliedLiberationReturnsControlToFriendlyOwner();
        AlliedFrontLinesSpanOwnAndAlliedGroundOnly();
    }

    static Campaign MakeLineCampaign(int seed, ushort[] owners, string[] tags, int[][] links)
    {
        var c = new Campaign(seed) { ScenarioId = "warfare_pocket_front_fixture" };
        var unique = owners.Zip(tags, (m, t) => (m, t)).Distinct().ToArray();
        foreach (var (mapIndex, tag) in unique)
            c.AddNation(new Nation
            {
                Tag = tag,
                Name = tag,
                MapIndex = mapIndex,
                Population = 2_000_000,
                Gdp = 80,
                Treasury = 80,
                OutputScale = 1,
                Stability = .8f,
                WarSupport = 1f
            });
        for (var i = 0; i < owners.Length; i++)
            c.States.Add(new ScenarioState
            {
                Id = $"wf_state_{i + 1}",
                Name = $"State {i + 1}",
                MapIndex = 1001 + i,
                NationId = tags[i],
                NationMapIndex = owners[i],
                Population = 1_000_000,
                Stability = .8f
            });
        var width = owners.Length * 4;
        const int height = 4;
        var cells = Enumerable.Range(0, width * height).Select(i => (ushort)(1001 + i % width / 4)).ToArray();
        var mapOwners = Enumerable.Range(0, width * height).Select(i => owners[i % width / 4]).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, mapOwners, width, height, _ => false));
        for (var p = 1; p <= links.Length; p++) c.ProvinceMap.Neighbors[p] = links[p - 1];
        c.SetFeature("militia", false);
        c.Defines.WarEmergencyRaisesPerDay = 0;
        c.Defines.WarEmergencyReinforcementsPerDay = 0;
        SovereignFinance.NormalizeAccounts(c);
        return c;
    }

    static Army AddArmy(Campaign c, int id, int nationId, int province, float org = 1f, int warId = -1,
        ArmyOrder order = ArmyOrder.Manual)
    {
        var a = new Army
        {
            Id = id,
            NationId = nationId,
            ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province],
            TemplateId = "line_infantry",
            Divisions = 1f,
            Strength = 1f,
            Organisation = org,
            SupplyLevel = 1f,
            Planning = 1f,
            Personnel = 15_000,
            MaxPersonnel = 15_000,
            Equipment = 1f,
            Cohesion = 1f,
            OrderKind = order,
            AssignedWarId = warId,
            PlanActive = order is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead
        };
        (a.Latitude, a.Longitude) = c.ProvinceMap.CenterOf(province);
        c.Armies.Add(a);
        return a;
    }

    static void EncircledRegularDivisionSurrendersInsteadOfRetreatingOntoEnemyGround()
    {
        // Provinces: 1 (UKR), 2 (SLB, occupied by UKR!), 3 (SLB last pocket), 4 (UKR)
        var c = MakeLineCampaign(20261127,
            new ushort[] { 501, 502, 502, 501 },
            new[] { "UKR", "SLB", "SLB", "UKR" },
            new[] { new[] { 2 }, new[] { 1, 3 }, new[] { 2, 4 }, new[] { 3 } });
        var ukr = c.Nations[0];
        var slb = c.Nations[1];
        // Province 2 is SLB's legal homeland, but occupied by UKR!
        c.SetProvinceController(2, (ushort)ukr.MapIndex);
        c.Defines.RetreatSearchDepth = 4;

        var war = new StrategicWar { Id = 1, AttackerId = ukr.Id, DefenderId = slb.Id, StartedDay = 0, FrontDirty = true };
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, ukr.Id, slb.Id).Stance = DiplomaticStance.AtWar;
        Warfare.RebuildFronts(c);

        var attacker = AddArmy(c, 10, ukr.Id, 2, org: 1f, warId: war.Id, order: ArmyOrder.AdvanceFront);
        var defender = AddArmy(c, 11, slb.Id, 3, org: 0f, warId: war.Id, order: ArmyOrder.DefendFront);

        Check(WarCoalition.SpreadRetreatProvince(c, war, defender, 3, beaten: true, slipAway: true, out _) == 0,
            "encircled zero-morale division has NO valid retreat onto enemy-controlled homeland or enemy ground");
        Check(WarCoalition.RetreatProvince(c, war, defender, 3, lowestId: true) == 0,
            "RetreatProvince also refuses enemy-controlled neighbors");

        var battle = new LandBattle
        {
            Id = c.NextBattleId++,
            WarId = war.Id,
            ProvinceId = 3,
            AttackerId = ukr.Id,
            DefenderId = slb.Id,
            StartedDay = c.ElapsedDays
        };
        battle.Attackers.Add(attacker.Id);
        battle.Defenders.Add(defender.Id);
        attacker.BattleId = battle.Id;
        defender.BattleId = battle.Id;
        c.Battles.Add(battle);

        c.StepOpensNewDay = true;
        Warfare.Tick(c);

        Check(!c.Armies.Any(a => a.Id == defender.Id),
            "encircled zero-morale division is destroyed cleanly instead of retreating onto enemy ground");
        Check(c.ProvinceController[3] == (ushort)ukr.MapIndex,
            "victor captures the last pocket province");
        Check(war.Toll.TryGetValue(slb.Id, out var slbToll) && slbToll.Captured > 0f,
            "POWs are properly recorded for the surrendered pocket division");
    }

    static void EncircledRebelDivisionNeverSlipsOntoEnemyControlledGround()
    {
        // Rebel breakaway (Republic of Sloboda Ukraine) 100% surrounded by parent UKR.
        var c = MakeLineCampaign(20261128,
            new ushort[] { 501, 502, 502 },
            new[] { "UKR", "SLB", "SLB" },
            new[] { new[] { 2 }, new[] { 1, 3 }, new[] { 2 } });
        var ukr = c.Nations[0];
        var slb = c.Nations[1];
        slb.SecededFromId = ukr.Id;
        c.Homelands[slb.Id] = new() { 1002, 1003 };
        // UKR occupies province 2 (SLB's homeland) and has no unit in province 1.
        c.SetProvinceController(2, (ushort)ukr.MapIndex);

        var war = new StrategicWar { Id = 2, AttackerId = ukr.Id, DefenderId = slb.Id, CivilConflict = true, StartedDay = 0 };
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, ukr.Id, slb.Id).Stance = DiplomaticStance.AtWar;

        var rebel = AddArmy(c, 20, slb.Id, 3, org: 0f, warId: war.Id, order: ArmyOrder.DefendFront);

        var retreat = WarCoalition.SpreadRetreatProvince(c, war, rebel, 3, beaten: true, slipAway: true, out var slipped);
        Check(retreat == 0 && !slipped,
            "encircled rebel division cannot slip away onto enemy-controlled province");
    }

    static void StrandedZeroMoraleUnitOnEnemyGroundSurrendersOnTick()
    {
        var c = MakeLineCampaign(20261129,
            new ushort[] { 501, 502 },
            new[] { "UKR", "SLB" },
            new[] { new[] { 2 }, new[] { 1 } });
        var ukr = c.Nations[0];
        var slb = c.Nations[1];
        // UKR occupies 100% of SLB, while SLB's 0-morale ghost unit stands on UKR-controlled province 2.
        c.SetProvinceController(2, (ushort)ukr.MapIndex);
        var war = new StrategicWar { Id = 3, AttackerId = ukr.Id, DefenderId = slb.Id, StartedDay = 0 };
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, ukr.Id, slb.Id).Stance = DiplomaticStance.AtWar;

        var ghost = AddArmy(c, 30, slb.Id, 2, org: 0f, warId: war.Id, order: ArmyOrder.Manual);

        c.StepOpensNewDay = true;
        Warfare.Tick(c);

        Check(!c.Armies.Any(a => a.Id == ghost.Id),
            "stranded zero-morale division on enemy-controlled ground with no escape surrenders instead of lingering");
        Check(war.Toll.TryGetValue(slb.Id, out var strandedToll) && strandedToll.Captured > 0f,
            "stranded zero-morale surrender records POWs");
    }

    static void ThreeWayWarStandingArmyCapturesEnemyOccupiedThirdPartyProvince()
    {
        // 3-way war: UKR (501) at war with NOV (502, legal owner of prov 2) AND RUS (503, occupier of prov 2).
        var c = MakeLineCampaign(20261130,
            new ushort[] { 501, 502, 503 },
            new[] { "UKR", "NOV", "RUS" },
            new[] { new[] { 2 }, new[] { 1, 3 }, new[] { 2 } });
        var ukr = c.Nations[0];
        var nov = c.Nations[1];
        var rus = c.Nations[2];

        // Russia occupies Novorossiya's province 2 (Dnipropetrovsk).
        c.SetProvinceController(2, (ushort)rus.MapIndex);

        var warNov = new StrategicWar { Id = 10, AttackerId = ukr.Id, DefenderId = nov.Id, StartedDay = 0, FrontDirty = true };
        var warRus = new StrategicWar { Id = 11, AttackerId = rus.Id, DefenderId = ukr.Id, StartedDay = 0, FrontDirty = true };
        c.Wars.Add(warNov);
        c.Wars.Add(warRus);
        CampaignDiplomacy.Get(c, ukr.Id, nov.Id).Stance = DiplomaticStance.AtWar;
        CampaignDiplomacy.Get(c, ukr.Id, rus.Id).Stance = DiplomaticStance.AtWar;

        // UKR division standing directly on Russian-occupied Novorossiya province 2.
        AddArmy(c, 40, ukr.Id, 2, org: 1f, warId: warNov.Id, order: ArmyOrder.Manual);

        c.StepOpensNewDay = true;
        Warfare.Tick(c);

        Check(c.ProvinceController[2] == (ushort)ukr.MapIndex,
            "3-way war: army standing on enemy-occupied third-party enemy province captures control for its own nation");
        Check(c.ProvinceMap.ProvinceToNation[2] == (ushort)nov.MapIndex,
            "legal owner stays intact while controller flips to victor");
    }

    static void ThreeWayWarAdjacentAdvanceCapturesEnemyOccupiedThirdPartyProvince()
    {
        var c = MakeLineCampaign(20261201,
            new ushort[] { 501, 502, 503 },
            new[] { "UKR", "NOV", "RUS" },
            new[] { new[] { 2 }, new[] { 1, 3 }, new[] { 2 } });
        var ukr = c.Nations[0];
        var nov = c.Nations[1];
        var rus = c.Nations[2];
        c.SetProvinceController(2, (ushort)rus.MapIndex);

        var warNov = new StrategicWar { Id = 12, AttackerId = ukr.Id, DefenderId = nov.Id, StartedDay = 0, FrontDirty = true };
        var warRus = new StrategicWar { Id = 13, AttackerId = rus.Id, DefenderId = ukr.Id, StartedDay = 0, FrontDirty = true };
        c.Wars.Add(warNov);
        c.Wars.Add(warRus);
        CampaignDiplomacy.Get(c, ukr.Id, nov.Id).Stance = DiplomaticStance.AtWar;
        CampaignDiplomacy.Get(c, ukr.Id, rus.Id).Stance = DiplomaticStance.AtWar;
        Warfare.RebuildFronts(c);

        // UKR army is assigned to War vs NOV, standing in province 1 adjacent to RUS-occupied province 2.
        var army = AddArmy(c, 41, ukr.Id, 1, org: 1f, warId: warNov.Id, order: ArmyOrder.AdvanceFront);
        army.DirectOrder = true;
        for (var d = 0; d < 240 && c.ProvinceController[2] != (ushort)ukr.MapIndex; d++)
        {
            c.StepOpensNewDay = d % 24 == 0;
            Warfare.Tick(c);
        }

        Check(c.ProvinceController[2] == (ushort)ukr.MapIndex,
            "3-way war: advancing army assigned to war vs owner still captures adjacent province occupied by second enemy");
    }

    static void AlliedLiberationReturnsControlToFriendlyOwner()
    {
        var c = MakeLineCampaign(20261202,
            new ushort[] { 501, 502, 503 },
            new[] { "UKR", "POL", "RUS" },
            new[] { new[] { 2 }, new[] { 1, 3 }, new[] { 2 } });
        var ukr = c.Nations[0];
        var pol = c.Nations[1];
        var rus = c.Nations[2];
        // Russia occupies Poland's province 2; UKR and POL are war allies against RUS.
        c.SetProvinceController(2, (ushort)rus.MapIndex);

        var war = new StrategicWar { Id = 14, AttackerId = rus.Id, DefenderId = ukr.Id, StartedDay = 0, FrontDirty = true };
        war.DefenderAllies.Add(pol.Id);
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, ukr.Id, rus.Id).Stance = DiplomaticStance.AtWar;
        CampaignDiplomacy.Get(c, pol.Id, rus.Id).Stance = DiplomaticStance.AtWar;

        AddArmy(c, 42, ukr.Id, 2, org: 1f, warId: war.Id, order: ArmyOrder.Manual);

        c.StepOpensNewDay = true;
        Warfare.Tick(c);

        Check(c.ProvinceController[2] == (ushort)pol.MapIndex,
            "capturing an ally's enemy-occupied province liberates control back to the friendly ally");
    }

    static void AlliedFrontLinesSpanOwnAndAlliedGroundOnly()
    {
        // Map: 1 (UKR) - 2 (POL, war ally) - 3 (RUS, enemy) - 4 (NEU, neutral bystander)
        // Also 1 (UKR) touches 3 (RUS), so UKR and POL share a continuous front facing RUS.
        var c = MakeLineCampaign(20261203,
            new ushort[] { 501, 502, 503, 504 },
            new[] { "UKR", "POL", "RUS", "NEU" },
            new[] { new[] { 2, 3 }, new[] { 1, 3 }, new[] { 1, 2, 4 }, new[] { 3 } });
        var ukr = c.Nations[0];
        var pol = c.Nations[1];
        var rus = c.Nations[2];

        var war = new StrategicWar { Id = 20, AttackerId = rus.Id, DefenderId = ukr.Id, StartedDay = 0, FrontDirty = true };
        war.DefenderAllies.Add(pol.Id);
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, ukr.Id, rus.Id).Stance = DiplomaticStance.AtWar;
        CampaignDiplomacy.Get(c, pol.Id, rus.Id).Stance = DiplomaticStance.AtWar;
        Warfare.RebuildFronts(c);

        Check(WarCoalition.CanHoldFrontPost(c, ukr, 1), "own border province is valid front staging ground");
        Check(WarCoalition.CanHoldFrontPost(c, ukr, 2), "war ally border province is valid front staging ground");
        Check(!WarCoalition.CanHoldFrontPost(c, ukr, 3), "enemy province is refused as front staging ground");
        Check(!WarCoalition.CanHoldFrontPost(c, ukr, 4), "neutral province without access is refused as front staging ground");

        var whole = BorderFronts.WholeFront(c, ukr.Id);
        Check(whole.Contains(1) && whole.Contains(2) && !whole.Contains(3) && !whole.Contains(4),
            "WholeFront spans BOTH own and war-ally border provinces facing enemy, excluding neutral/enemy");

        var oneBorder = BorderFronts.Candidates(c, ukr.Id, rus.Id);
        Check(oneBorder.Contains(1) && oneBorder.Contains(2) && !oneBorder.Contains(4),
            "One border (Candidates) spans BOTH own and war-ally border provinces facing enemy");

        var a1 = AddArmy(c, 50, ukr.Id, 1, org: 1f, warId: war.Id, order: ArmyOrder.DefendFront);
        var a2 = AddArmy(c, 51, ukr.Id, 1, org: 1f, warId: war.Id, order: ArmyOrder.DefendFront);
        new DrawFrontLineCommand
        {
            IssuerId = ukr.Id,
            Kind = FrontLineKind.Defence,
            AllEnemies = true,
            ArmyIds = new[] { a1.Id, a2.Id }
        }.Apply(c);

        var line = c.FrontLines.Single();
        Check(line.Provinces.Contains(1) && line.Provinces.Contains(2),
            "drawn Whole front contains both own and allied border posts");

        a1.OrderIssuedDay = -10;
        a2.OrderIssuedDay = -10;
        FrontManager.Tick(c);

        var coversAlliedPost = c.Armies.Any(a => a.NationId == ukr.Id && (a.OrderProvinceId == 2 || a.ProvinceId == 2));
        Check(coversAlliedPost,
            "FrontManager routes a division from own ground onto the empty allied border post");
    }
}
