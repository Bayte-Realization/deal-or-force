using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// An ally who answered a defender's call walks into the enemy's empty province beside it.
///
/// The owner watched NATO divisions packed along the Belarusian border for months, in Ukraine's
/// war, while the Belarusian provinces behind it stood empty. The take-back-first rule
/// (AiDefenderInvadesFromScore) was read for every country on the defending side, so an ally
/// whose own land nobody had touched had every target struck off: ordered to advance, it had
/// nowhere it was allowed to go. The rule now holds a defender only while the enemy stands on
/// some of its own ground; the defender whose ground is occupied is still held to it.
///
/// The world: ALY(1) - ATK(2) - ATK(3) - DEF(4, held by ATK) - DEF(5) - DEF(6), with DEF(6)
/// also touching ATK(2). Nobody of ATK's side is anywhere, so no cover refuses a step.
/// </summary>
static class CoalitionAdvanceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("coalition advance: " + label);
        Console.WriteLine("PASS coalition advance: " + label);
    }

    const ushort Atk = 501, Def = 502, Aly = 503;

    public static void Run()
    {
        // The ally beside an empty enemy province may start an advance into it; the defender
        // whose own province is held by the enemy still may not invade before it is winning.
        {
            var c = Fixture(out var war);
            var ally = Unit(c, 1, 2, 1);
            var defender = Unit(c, 2, 1, 6);
            Check(Warfare.CanStartOpenAdvance(c, war, ally),
                "an ally whose own ground nobody holds may advance into an empty enemy province");
            Check(!Warfare.CanStartOpenAdvance(c, war, defender),
                "a defender whose own province is occupied still takes it back before it invades");
        }
        // An ally standing in the partner's country, whose ground the enemy holds, is that
        // partner's defence: it retakes the partner's province, and does not invade from there.
        {
            var c = Fixture(out var war);
            var corps = Unit(c, 1, 2, 6);
            Check(!Warfare.CanStartOpenAdvance(c, war, corps),
                "an ally in a partner's country that has lost ground does not invade from it");
            var c2 = Fixture(out var war2);
            var relief = Unit(c2, 1, 2, 5);
            Check(Warfare.CanStartOpenAdvance(c2, war2, relief),
                "an ally in a partner's country retakes the partner's empty occupied province");
        }
        // The defender whose ground is all its own again is held to nothing either.
        {
            var c = Fixture(out var war, occupied: false);
            var defender = Unit(c, 2, 1, 6);
            Check(Warfare.CanStartOpenAdvance(c, war, defender),
                "a defender with nothing of its own under the enemy may advance like anybody else");
        }
        // The old reading stays one define away for a mod that wants it.
        {
            var c = Fixture(out var war);
            c.Defines.AiDefenderFenceNeedsLostGround = false;
            var ally = Unit(c, 1, 2, 1);
            Check(!Warfare.CanStartOpenAdvance(c, war, ally),
                "AiDefenderFenceNeedsLostGround=false holds every defender, allies included, to its own ground");
        }
        // The whole day loop: the ally's army, run by the computer, enters the empty province
        // within a bounded number of days.
        {
            var c = Fixture(out var war);
            var ally = Unit(c, 1, 2, 1);
            // The province stays empty: nobody raises a garrison for it while the army walks.
            c.Defines.WarEmergencyRaisesPerDay = 0;
            c.Defines.WarEmergencyReinforcementsPerDay = 0;
            foreach (var nation in c.Nations) { nation.ManpowerReserve = 0; nation.Treasury = 0; }
            c.IsPaused = false;
            const int Bound = 30;
            var day = 0;
            for (; day < Bound && c.ProvinceController[2] != Aly; day++)
            {
                c.TickDay();
                if (Environment.GetEnvironmentVariable("COALITION_DEBUG") == "1")
                    Console.WriteLine($"DBG day={day} at={ally.ProvinceId} order={ally.OrderKind} act={ally.PlanActive} move={ally.MoveProgress:0.000} " +
                        $"moving={ally.IsMoving} route={string.Join(",", ally.Route)} planned={string.Join(",", ally.PlannedRoute)} target={ally.OrderProvinceId} " +
                        $"ready={ally.ReadyDay}/{c.ElapsedDays} org={ally.Organisation:0.00} sup={ally.SupplyLevel:0.00} battle={ally.InBattle} " +
                        $"gap={war.UnopposedProgress.GetValueOrDefault(2):0.000} ended={war.Ended} armies={string.Join(",", c.Armies.Select(x => x.NationId + "@" + x.ProvinceId))}");
            }
            Check(c.ProvinceController[2] == Aly && !war.Ended,
                $"the computer's ally army takes the empty enemy province within {Bound} days " +
                $"(day {day}, controller {c.ProvinceController[2]}, order {ally.OrderKind}, active {ally.PlanActive}, at {ally.ProvinceId})");
            Check(c.ProvinceMap.ProvinceToNation[2] == Atk,
                "walking in occupies the province; it does not change its owner");
        }
    }

    static Campaign Fixture(out StrategicWar war, bool occupied = true)
    {
        var c = new Campaign(20261115) { ScenarioId = "coalition_advance_fixture" };
        c.AddNation(new Nation { Tag = "ATK", MapIndex = Atk, Population = 3_000_000, Gdp = 100,
            Treasury = 100, OutputScale = 1, Stability = .8f, WarSupport = 1 });
        c.AddNation(new Nation { Tag = "DEF", MapIndex = Def, Population = 2_000_000, Gdp = 60,
            Treasury = 60, OutputScale = 1, Stability = .8f, WarSupport = 1 });
        c.AddNation(new Nation { Tag = "ALY", MapIndex = Aly, Population = 2_000_000, Gdp = 80,
            Treasury = 80, OutputScale = 1, Stability = .8f, WarSupport = 1 });
        var owner = new[] { Aly, Atk, Atk, Def, Def, Def };
        var tags = new[] { "ALY", "ATK", "ATK", "DEF", "DEF", "DEF" };
        for (var i = 0; i < owner.Length; i++)
            c.States.Add(new ScenarioState { Id = "coalition_fixture_" + i, MapIndex = 1001 + i,
                Name = "Coalition fixture " + i, NationId = tags[i], NationMapIndex = owner[i],
                Population = 1_000_000, Stability = .8f });
        // Six columns, four rows each, one region per column.
        const int width = 24, height = 4;
        var cells = Enumerable.Range(0, width * height).Select(i => (ushort)(1001 + i % width / 4)).ToArray();
        var owners = Enumerable.Range(0, width * height).Select(i => owner[i % width / 4]).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, width, height, _ => false));
        Check(c.ProvinceMap.ProvinceCount == 6, "the fixture has six provinces");
        var links = new[]
        {
            new[] { 2 },          // 1 ALY
            new[] { 1, 3, 6 },    // 2 ATK
            new[] { 2, 4 },       // 3 ATK
            new[] { 3, 5 },       // 4 DEF
            new[] { 4, 6 },       // 5 DEF
            new[] { 5, 2 },       // 6 DEF
        };
        for (var p = 1; p <= 6; p++) c.ProvinceMap.Neighbors[p] = links[p - 1];
        if (occupied) c.ProvinceController[4] = Atk;
        war = new StrategicWar { Id = 0, AttackerId = 0, DefenderId = 1, StartedDay = 0,
            AiNegotiationUnlockDay = 99999, ClaimedStateMapIndex = 1004, FrontDirty = true, LastFrontDay = -1 };
        war.DefenderAllies.Add(2);
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.AtWar;
        CampaignDiplomacy.Get(c, 0, 2).Stance = DiplomaticStance.AtWar;
        c.Nations[0].CapitalProvinceId = 3;
        c.Nations[1].CapitalProvinceId = 5;
        c.Nations[2].CapitalProvinceId = 1;
        c.SetFeature("militia", false);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 12L * 24);
        SovereignFinance.NormalizeAccounts(c);
        Warfare.RebuildFronts(c);
        return c;
    }

    static Army Unit(Campaign c, int id, int nationId, int province)
    {
        var a = new Army { Id = id, NationId = nationId, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], TemplateId = "line_infantry",
            Divisions = 1, Organisation = 1, Strength = 1, SupplyLevel = 1, Planning = 1,
            Personnel = 15000, MaxPersonnel = 15000, Equipment = 1, Cohesion = 1,
            OrderKind = ArmyOrder.AdvanceFront, AssignedWarId = 0 };
        var point = c.ProvinceMap.CenterOf(province); a.Latitude = point.Latitude; a.Longitude = point.Longitude;
        c.Armies.Add(a);
        return a;
    }
}
