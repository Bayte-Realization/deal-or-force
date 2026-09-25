using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The staff (item 44): a player's army is kept at its establishment by the general staff
/// only while the player lets it - a player who takes it back is not charged for formations
/// they never asked for - and an island garrison is supplied by sea instead of starving in
/// peacetime. Through a save as well.
/// </summary>
static class StaffTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        // A country at peace: one at war loses divisions in the field, which is the right
        // answer and not the one this is about.
        var probe = Campaign.CreateBootstrap(20260929);
        var tag = new[] { "JPN", "DEU", "AUS", "BRA", "CAN", "ESP" }.First(candidate =>
        {
            var nation = probe.NationByTag(candidate);
            return nation is not null && !probe.Wars.Any(war => !war.Ended && war.Involves(nation.Id)) &&
                   probe.Armies.Count(army => army.NationId == nation.Id) >= 4;
        });

        (int Raised, float Divisions) After(bool byHand, out Campaign campaign)
        {
            var c = Campaign.CreateBootstrap(20260929);
            c.AttachProvinceTopology(map);
            var nation = c.NationByTag(tag);
            c.ConfigureSinglePlayer(nation.Id);
            if (byHand) new SetDelegationCommand { IssuerId = nation.Id, Portfolio = Delegation.Army, Delegated = false }.Apply(c);
            c.IsPaused = false;
            nation.Treasury = MathF.Max(nation.Treasury, 5_000f);
            var raised = 0;
            // A budget that pays for several times the army the country has.
            for (var day = 0; day < c.Defines.ArmyRebuildIntervalDays * 3 + 2; day++)
            {
                nation.FoundingDivisions = 60f;
                nation.EstablishedDivisions = 60f;
                var before = c.Recruitment.Count(order => order.NationId == nation.Id);
                c.TickDay();
                raised += Math.Max(0, c.Recruitment.Count(order => order.NationId == nation.Id) - before);
            }
            campaign = c;
            return (raised, c.Armies.Where(army => army.NationId == nation.Id).Sum(army => army.Divisions));
        }

        var fresh = Campaign.CreateBootstrap(20260929);
        fresh.AttachProvinceTopology(map);
        var player = fresh.NationByTag(tag);
        fresh.ConfigureSinglePlayer(player.Id);
        var start = fresh.Armies.Where(army => army.NationId == player.Id).Sum(army => army.Divisions);
        Check(Delegation.Holds(player, Delegation.Army), "by default the general staff keeps a player's army");

        var staff = After(false, out _);
        var own = After(true, out var byHand);
        Check(staff.Raised > 0, $"left to the staff, {tag}'s army is raised toward what its budget pays for ({staff.Raised} order(s))");
        Check(own.Raised == 0, $"taken into the player's own hands, nothing is raised on {tag}'s treasury without being asked");

        // And nobody starves in a peacetime island garrison: Japan's army was deployed onto
        // islets no road reaches, and six of seventeen divisions died of it in six weeks.
        Check(own.Divisions >= start - 0.001f,
            $"{tag} keeps all {start:0} divisions it began with through {byHand.ElapsedDays} days of peace");
        var islanders = byHand.Armies.Where(army => army.NationId == byHand.NationByTag(tag).Id &&
                                                    army.ProvinceId > 0 && map.Neighbors[army.ProvinceId].Length == 0).ToList();
        Check(islanders.All(army => !army.CutOff && army.SupplyLevel > 0.3f),
            $"{islanders.Count} formation(s) on islands with no land neighbour are supplied by sea");

        var bytes = SaveSystem.ExportNetworkSnapshot(byHand, "");
        var back = SaveSystem.ImportNetworkSnapshot(bytes, "").Campaign;
        Check(back is not null && !Delegation.Holds(back.NationByTag(tag), Delegation.Army),
            "and the choice survives a save");
    }
}
