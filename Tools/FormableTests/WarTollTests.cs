using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// What a war cost each country in it (WarLosses.Record, WarLosses.ByCountry).
///
/// The war panel showed two figures, one a side, under the two leaders' names - so a coalition's
/// dead were all "Russia's" or all "Ukraine's". These follow the men to the country that lost
/// them: an ally's losses are the ally's, the side's total is still the sum, what a war lost
/// before the record was kept is said to be unrecorded rather than given to somebody, a country
/// that left the war keeps its line, and the record survives a save.
/// </summary>
static class WarTollTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261116);
        var rus = c.NationByTag("RUS");
        var ukr = c.NationByTag("UKR");
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));
        var attacking = war.IsAttacking(rus.Id);
        var before = (Attackers: war.AttackerLosses, Defenders: war.DefenderLosses);
        Check(war.Toll.Count == 0, "a war that opens with the scenario has nobody's losses recorded by country yet");
        Check(Math.Abs(WarLosses.ByCountry(war, true).Unattributed - before.Attackers) < 1f &&
              Math.Abs(WarLosses.ByCountry(war, false).Unattributed - before.Defenders) < 1f,
            "what the war had cost before the record began is reported as unrecorded, in full");

        var ally = c.Nations.First(n => n.Tag is "POL" or "BLR" or "DEU" && !war.Involves(n.Id));
        (attacking ? war.DefenderAllies : war.AttackerAllies).Add(ally.Id);
        Check(war.Involves(ally.Id) && war.IsAttacking(ally.Id) != attacking, "an ally stands on the side it joined");

        WarLosses.Record(war, rus.Id, 1000f, 2000f, 300f);
        WarLosses.Record(war, ukr.Id, 700f, 900f, 0f);
        WarLosses.Record(war, ally.Id, 4000f, 500f, 50f);
        WarLosses.Record(war, ally.Id, 100f, 0f, 0f);

        var theirs = WarLosses.ByCountry(war, !attacking);
        var ours = WarLosses.ByCountry(war, attacking);
        Check(ours.Countries.Count == 1 && ours.Countries[0].NationId == rus.Id &&
              Math.Abs(ours.Countries[0].Toll.Total - 3300f) < 0.5f,
            "a country fighting alone has one line: its own 3,300");
        Check(theirs.Countries.Count == 2 && theirs.Countries[0].NationId == ally.Id && theirs.Countries[1].NationId == ukr.Id,
            "a coalition has a line for every country, the heaviest loser first - not the leader first");
        Check(Math.Abs(theirs.Countries[0].Toll.Killed - 4100f) < 0.5f && Math.Abs(theirs.Countries[0].Toll.Wounded - 500f) < 0.5f &&
              Math.Abs(theirs.Countries[0].Toll.Captured - 50f) < 0.5f,
            "the ally's killed, wounded and captured are the ally's own, added up over two entries");
        var sideTotal = attacking ? war.DefenderLosses : war.AttackerLosses;
        Check(Math.Abs(theirs.Countries.Sum(entry => entry.Toll.Total) + theirs.Unattributed - sideTotal) < 1f,
            "the lines and the unrecorded remainder add up to the side's total");
        Check(Math.Abs(theirs.Unattributed - (attacking ? before.Defenders : before.Attackers)) < 1f,
            "new losses never leak into the unrecorded remainder");

        WarLosses.DiedOfWounds(war, ally.Id, 200f);
        Check(Math.Abs(war.Toll[ally.Id].Killed - 4300f) < 0.5f && Math.Abs(war.Toll[ally.Id].Wounded - 300f) < 0.5f &&
              Math.Abs(war.Toll[ally.Id].Total - 4650f) < 0.5f,
            "wounded who die change column on their own country's line; its total does not move");
        WarLosses.DiedOfWounds(war, ukr.Id, 5000f);
        Check(war.Toll[ukr.Id].Wounded >= 0f && Math.Abs(war.Toll[ukr.Id].Total - 1600f) < 0.5f,
            "no country can lose more wounded than it has");

        (attacking ? war.DefenderAllies : war.AttackerAllies).Remove(ally.Id);
        var after = WarLosses.ByCountry(war, !attacking);
        Check(after.Countries.Any(entry => entry.NationId == ally.Id) && !war.Involves(ally.Id),
            "a country that made a separate peace keeps its line, on the side it fought on");
        Check(WarLosses.ByCountry(war, attacking).Countries.All(entry => entry.NationId != ally.Id),
            "and it is not moved to the other side by having left");

        WarLosses.Record(war, rus.Id, 0f, 0f, 0f);
        WarLosses.Record(war, rus.Id, -50f, -1f, -3f);
        Check(Math.Abs(war.Toll[rus.Id].Total - 3300f) < 0.5f, "nothing and less than nothing are not losses");

        var s = c.States.First(r => r.NationMapIndex == c.Player.MapIndex);
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)s.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)c.Player.MapIndex, 512).ToArray(), 32, 16, _ => false));
        var folder = Path.Combine(Path.GetTempPath(), "dof-war-toll-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var text in new[] { true, false })
            {
                c.Defines.SaveTextFormat = text;
                SaveSystem.Save(folder, "toll", c, "tests");
                var loaded = SaveSystem.Load(folder, "toll", "", c.ProvinceMap.ProvinceCount).Campaign;
                var again = loaded.Wars.First(w => w.Id == war.Id);
                Check(again.Toll.Count == war.Toll.Count &&
                      again.Toll.TryGetValue(ally.Id, out var kept) && Math.Abs(kept.Killed - 4300f) < 0.5f &&
                      Math.Abs(kept.Wounded - 300f) < 0.5f && Math.Abs(kept.Captured - 50f) < 0.5f &&
                      kept.Attacking == war.Toll[ally.Id].Attacking,
                    "each country's line survives a " + (text ? "text" : "compact") + " save");
            }
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
