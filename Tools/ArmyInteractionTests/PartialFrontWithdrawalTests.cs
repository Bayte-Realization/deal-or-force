using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PartialFrontWithdrawalTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL partial front: " + label); Console.WriteLine("PASS: partial front " + label); }
    static string State(Campaign c) => JsonSerializer.Serialize(new
    { c.Armies, c.Battles, c.FrontLines, c.Random.State }, new JsonSerializerOptions { IncludeFields = true });

    internal static Campaign Fixture(bool defending = false, float divisions = 20)
    {
        var c = new Campaign(20261020) { ScenarioId = "partial_front_fixture" };
        c.Defines.HoursPerTick = 24;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 2400L);
        for (var n = 0; n < 3; n++) c.Nations.Add(new Nation { Id = n, MapIndex = 500 + n,
            Tag = "PF" + n, Name = "Country " + n, Population = 10_000_000, Gdp = 100, Treasury = 20, OutputScale = 1 });
        const int width = 64, height = 32;
        var states = new ushort[width * height]; var owners = new ushort[states.Length]; var rgb = new byte[states.Length * 3];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var p = (y / 16) * 4 + x / 16 + 1; var i = y * width + x;
            states[i] = (ushort)(2000 + p); owners[i] = (ushort)(x < 32 ? 500 : 501); rgb[i * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 8).ToDictionary(p => p, p => p));
        for (var p = 1; p <= 8; p++) c.States.Add(new ScenarioState { Id = "pf" + p, Name = "Region " + p,
            MapIndex = 2000 + p, NationMapIndex = map.ProvinceToNation[p], NationId = "PF" + (map.ProvinceToNation[p] - 500), Population = 1_000_000 });
        c.AttachProvinceTopology(map); c.Armies.Clear();
        var war = new StrategicWar { Id = 882, AttackerId = defending ? 1 : 0, DefenderId = defending ? 0 : 1,
            LastFrontDay = c.ElapsedDays, FrontDirty = false };
        (defending ? war.DefenderFront : war.AttackerFront).UnionWith(new[] { 2, 6 });
        (defending ? war.AttackerFront : war.DefenderFront).UnionWith(new[] { 3, 7 }); c.Wars.Add(war);
        var source = Unit(c, 1, 0, 2, divisions, defending ? ArmyOrder.DefendFront : ArmyOrder.AdvanceFront);
        var enemy = Unit(c, 2, 1, 3, 1, defending ? ArmyOrder.AdvanceFront : ArmyOrder.DefendFront);
        var b = new LandBattle { Id = 991, WarId = war.Id, ProvinceId = defending ? 2 : 3,
            AttackerId = war.AttackerId, DefenderId = war.DefenderId, StartedDay = 0, AttackerLosses = 123, DefenderLosses = 456 };
        (defending ? b.Defenders : b.Attackers).Add(source.Id); (defending ? b.Attackers : b.Defenders).Add(enemy.Id);
        source.BattleId = enemy.BattleId = b.Id; c.Battles.Add(b); c.SetPlayer(0); c.Nations[0].IsHuman = false; c.HumanSeats.Clear();
        SovereignFinance.NormalizeAccounts(c); EconomySystem.Explain(c); return c;
    }
    static Army Unit(Campaign c, int id, int nation, int province, float divisions, ArmyOrder duty)
    {
        var a = new Army { Id = id, NationId = nation, ProvinceId = province, StateMapIndex = 2000 + province,
            Divisions = divisions, Personnel = (int)(divisions * 10_000), MaxPersonnel = (int)(divisions * 15_000),
            OrderKind = duty, AssignedWarId = 882, OrderProvinceId = province, OrderIssuedDay = -100,
            Strength = .7f, Organisation = .8f, Equipment = .37f, Experience = .61f, Cohesion = .42f,
            Recon = .54f, Engineers = .63f, Artillery = .72f, Recovering = 113, SupplyCongestion = .71f,
            ArmyGroupId = 20, ContractUntilDay = 180, SupplyLevel = .9f };
        (a.Latitude, a.Longitude) = c.ProvinceMap.CenterOf(province); c.Armies.Add(a); return a;
    }

    public static void Run()
    {
        foreach (var divisions in new[] { 20f, 37f, 43f })
        {
            var c = Fixture(divisions: divisions); var a = c.Armies[0]; var b = c.Battles[0];
            var people = a.Personnel; var max = a.MaxPersonnel; var wounded = a.Recovering; var power = a.Power;
            var take = MathF.Floor(divisions * .35f);
            var command = new WithdrawArmyFromBattleCommand { IssuerId = 0, ArmyId = 1, Share = take / divisions };
            var fp = CampaignFingerprint.Of(c); command.Apply(c); var part = c.Armies.Single(u => u.Id == command.WithdrawnArmyId);
            Check(command.Withdrawn && part.Divisions == take && a.Divisions + part.Divisions == divisions && a.InBattle && !part.InBattle && !b.Ended,
                "split keeps exact division count and fighting rearguard (" + divisions + ")");
            Check(a.Personnel + part.Personnel == people && a.MaxPersonnel + part.MaxPersonnel == max &&
                Math.Abs(a.Recovering + part.Recovering - wounded) < .001 && Math.Abs(a.Power + part.Power - power) < .001,
                "split conserves personnel, establishments, wounded and equipped fighting power (" + divisions + ")");
            Check(part.Equipment == a.Equipment && part.Experience == a.Experience && part.Cohesion == a.Cohesion &&
                part.Recon == a.Recon && part.Engineers == a.Engineers && part.Artillery == a.Artillery &&
                part.ArmyGroupId == 20 && part.ContractUntilDay == 180 && part.SupplyCongestion == a.SupplyCongestion,
                "detached troops keep their actual equipment, skills, staff and contract");
            Check(b.WithdrawnAttackers.Contains(part.Id) && b.AttackerLosses == 123 && b.DefenderLosses == 456 &&
                CampaignFingerprint.Of(c) != fp, "withdrawal history prevents re-pinning and changes authority fingerprint");
            var snapshot = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "partial_front"), "partial_front");
            Check(snapshot.Success, "actual snapshot accepts split formations: " + snapshot.Warning);
            snapshot.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
            if (State(snapshot.Campaign) != State(c))
            { Console.WriteLine("SPLIT_ORIGINAL " + State(c)); Console.WriteLine("SPLIT_RESTORED " + State(snapshot.Campaign)); }
            if (CampaignFingerprint.Of(snapshot.Campaign) != CampaignFingerprint.Of(c))
            {
                Console.WriteLine("SPLIT_FINGERPRINT " + CampaignFingerprint.Of(c) + " -> " + CampaignFingerprint.Of(snapshot.Campaign));
                foreach (var n in c.Nations)
                    foreach (var f in typeof(Nation).GetFields().Where(f => f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string)))
                        if (!Equals(f.GetValue(n), f.GetValue(snapshot.Campaign.NationById(n.Id))))
                            Console.WriteLine("SPLIT_DIFF " + n.Tag + "." + f.Name + " " + f.GetValue(n) + " -> " + f.GetValue(snapshot.Campaign.NationById(n.Id)));
            }
            Check(State(snapshot.Campaign) == State(c) && CampaignFingerprint.Of(snapshot.Campaign) == CampaignFingerprint.Of(c),
                "saved formations, losses, orders and withdrawal membership survive reload");
        }
        foreach (var badShare in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 1.1f })
        {
            var c = Fixture(); var before = State(c);
            var cmd = new WithdrawArmyFromBattleCommand { IssuerId = 0, ArmyId = 1, Share = badShare }; cmd.Apply(c);
            Check(!cmd.Withdrawn && State(c) == before, "malformed fraction cannot mutate troops (" + badShare + ")");
        }
        {
            var c = Fixture(); var before = State(c);
            new WithdrawArmyFromBattleCommand { IssuerId = 1, ArmyId = 1, Share = .5f }.Apply(c);
            Check(State(c) == before, "foreign issuer cannot split or retreat another country's army");
            var cmd = new WithdrawArmyFromBattleCommand { IssuerId = 0, ArmyId = 1, Share = .35f, TargetProvinceId = 6 };
            Check(NetworkCommandCodec.TryEncode(cmd, out var type, out var json) && NetworkCommandCodec.TryDecode(type, json, out var decoded) &&
                decoded is WithdrawArmyFromBattleCommand wire && wire.Share == .35f && wire.TargetProvinceId == 6,
                "wire codec carries partial fraction and intended retreat destination");
        }
        foreach (var defending in new[] { false, true })
        {
            var c = Fixture(defending); var a = c.Armies[0]; var random = c.Random.State;
            FrontManager.Tick(c);
            var part = c.Armies.FirstOrDefault(u => u.NationId == 0 && !u.InBattle && u.OrderProvinceId == 6);
            Check(part is not null && part.OrderKind == ArmyOrder.DefendFront && part.AssignedWarId == 882 && !part.DirectOrder &&
                a.InBattle && !c.Battles[0].Ended, "automatic daily staff frees only a reserve and keeps war duty (defending=" + defending + ")");
            Check(part.ProvinceId == 6 || part.Route.LastOrDefault() == 6, "reserve has a real route to the uncovered post");
            Check(c.Random.State == random, "staff routing is deterministic and consumes no random draw");
        }
        foreach (var guard in new[] { "direct", "different war", "drawn line", "manual", "spearhead", "no delegation", "disabled" })
        {
            var c = Fixture(); var a = c.Armies[0];
            switch (guard)
            {
                case "direct": a.DirectOrder = true; break;
                case "different war": a.AssignedWarId = 888; break;
                case "drawn line": a.FrontLineId = 8; break;
                case "manual": a.OrderKind = ArmyOrder.Manual; break;
                case "spearhead": a.OrderKind = ArmyOrder.Spearhead; break;
                case "no delegation": c.Nations[0].IsHuman = true; break;
                case "disabled": c.Defines.FrontStaffDisengages = false; break;
            }
            var before = State(c); FrontManager.Balance(c, c.Wars[0]);
            Check(State(c) == before, "autonomous reserve release respects " + guard);
        }
        {
            var c = Fixture(true); c.Nations[0].IsHuman = true;
            var line = new DrawnFrontLine { Id = 7, NationId = 0, Kind = FrontLineKind.Defence };
            line.Provinces.UnionWith(new[] { 2, 6 }); line.ArmyIds.Add(1); c.FrontLines.Add(line); c.Armies[0].FrontLineId = 7;
            FrontManager.Tick(c);
            var part = c.Armies.FirstOrDefault(a => a.Id > 2 && a.OrderProvinceId == 6);
            Check(part is not null && part.FrontLineId == 7 && line.ArmyIds.Contains(part.Id),
                "a player's assigned drawn front redistributes itself without a separate distribute button");
        }
        {
            var c = Fixture(); c.Nations[0].IsHuman = true;
            var line = new DrawnFrontLine { Id = 7, NationId = 0, BorderNationId = 1, Kind = FrontLineKind.Offensive };
            line.Provinces.Add(3); line.ObjectiveSequence.Add(3); line.SourceProvinces.UnionWith(new[] { 2, 6 });
            line.ArmyIds.Add(1); line.Objectives[1] = 3; c.FrontLines.Add(line); c.Armies[0].FrontLineId = 7;
            FrontManager.Tick(c);
            var part = c.Armies.FirstOrDefault(a => a.Id > 2 && a.OrderProvinceId == 6);
            Check(part is not null && part.FrontLineId == 7 && !part.PlanActive && c.Armies[0].InBattle,
                "an active player's offensive front releases a paused covering reserve without dissolving its attack");
            Check(line.Objectives.GetValueOrDefault(part.Id) == 3 && line.ArmyIds.Contains(part.Id),
                "released reserve preserves the drawn offensive's intent for later resumption");
        }
        {
            var c = Fixture(true); var before = State(c);
            c.ProvinceController[6] = 502; c.ProvinceController[5] = 502;
            FrontManager.Balance(c, c.Wars[0]);
            Check(State(c) == before, "an inaccessible hole does not first withdraw and then discover a missing route");
        }
        {
            var c = Fixture(true); Unit(c, 3, 0, 5, 1, ArmyOrder.ReserveFront);
            FrontManager.Tick(c);
            Check(c.Armies[0].Divisions == 20 && c.Armies.Count == 3 && c.Armies[0].InBattle && c.Armies[2].OrderProvinceId == 6,
                "a free reserve fills a hole first; the staff does not also strip a fighting formation for it");
        }
        {
            var c = Fixture(true); var a = c.Armies[0];
            foreach (var p in c.ProvinceMap.Neighbors[2]) c.ProvinceController[p] = 502;
            var before = State(c); var cmd = new WithdrawArmyFromBattleCommand { IssuerId = 0, ArmyId = 1, Share = .5f }; cmd.Apply(c);
            Check(!cmd.Withdrawn && State(c) == before, "surrounded defender cannot split out of encirclement");
            c.ProvinceController[6] = 500;
            c.Battles.Add(new LandBattle { Id = 992, WarId = 882, ProvinceId = 6 });
            Check(!Warfare.WithdrawShare(c, a, .5f, out _, 6), "friendly-controlled battle tile is not a safe retreat destination");
        }
        {
            var c = Fixture(); c.Armies[0].Divisions = 1;
            c.Armies[0].Personnel = 10000; c.Armies[0].MaxPersonnel = 15000;
            for (var i = 3; i <= 4; i++)
            {
                var spent = Unit(c, i, 0, 2, 1, ArmyOrder.AdvanceFront); spent.Organisation = .01f;
                spent.BattleId = 991; c.Battles[0].Attackers.Add(i);
            }
            FrontManager.Tick(c);
            Check(c.Armies[0].InBattle, "two spent formations do not trick staff into withdrawing the only healthy anchor");
            Check(c.Armies.Any(a => a.NationId == 0 && !a.InBattle && a.OrderProvinceId == 6), "a spent surplus may cover the open flank instead");
        }
        {
            var c = Fixture(); var b = c.Battles[0];
            var orphan = Unit(c, 3, 0, 6, 1, ArmyOrder.DefendFront); orphan.BattleId = 987654;
            orphan.Route.Add(5); orphan.PlannedRoute.Add(5); orphan.Organisation = .3f; orphan.Strength = .4f;
            var people = orphan.Personnel; var equipment = orphan.Equipment; var random = c.Random.State;
            var fp = CampaignFingerprint.Of(c);
            Check(Warfare.ReconcileBattleAssignments(c) == 1 && !orphan.InBattle && c.Armies[0].InBattle && c.Armies[1].InBattle,
                "an absent old battle releases its orphan without cancelling a real engagement");
            Check(orphan.Personnel == people && orphan.Equipment == equipment && orphan.Organisation == .3f && orphan.Strength == .4f &&
                orphan.Route.SequenceEqual(new[] { 5 }) && orphan.AssignedWarId == 882 && orphan.ProvinceId == 6,
                "repair preserves damage, people, position, duty and queued orders");
            Check(orphan.ReadyDay > c.ElapsedDays && c.Random.State == random && CampaignFingerprint.Of(c) != fp,
                "repair has recovery delay, no random draws, and an authoritative fingerprint change");
            Check(Warfare.ReconcileBattleAssignments(c) == 0, "repeating repair is idempotent");
            b.Ended = true;
            Check(Warfare.ReconcileBattleAssignments(c) == 2 && c.Armies.All(a => !a.InBattle),
                "legacy ended reports no longer pin their former participants");
        }
        {
            var c = Fixture(); var b = c.Battles[0]; b.Attackers.Remove(1);
            Check(Warfare.ReconcileBattleAssignments(c) == 1 && !c.Armies[0].InBattle && c.Armies[1].InBattle,
                "a mismatched participant is repaired without ending somebody else's live battle");
            Warfare.EndBattleWithoutVictory(c, b, "Peace agreement");
            Check(b.Ended && b.WinnerId == -1 && c.Armies.All(a => !a.InBattle) && b.AttackerLosses == 123 && b.DefenderLosses == 456,
                "ordinary non-victory termination releases participants immediately and preserves losses");
        }
    }
}
