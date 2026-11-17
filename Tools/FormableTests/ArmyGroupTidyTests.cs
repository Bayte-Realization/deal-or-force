using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Army groups nobody serves in are struck off (Generals.Tidy).
///
/// Groups were only ever added - at the start, whenever a country with none needed one, whenever
/// the player made one - and never taken away. The owner's 107-year campaign carried 1 204 of them,
/// 1 032 with nobody under them. Pinned here on a fresh world: a group emptied by its divisions'
/// loss is first open for reuse and struck off after ArmyGroupEmptyDays; a country that is gone
/// loses its empty groups at once; a group the player made and never filled is kept, and one he
/// filled and lost is struck off like anybody's; no formation is left pointing at a group that is
/// not there; the day it was last staffed survives a save and is in the fingerprint.
/// </summary>
static class ArmyGroupTidyTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("army groups: " + note); Console.WriteLine("PASS army groups: " + note); }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, day * 24L);

    /// <summary>The first day on or after this one the weekly review runs.</summary>
    static int Review(int from) { while (from % 7 != 5) from++; return from; }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261121);
        // The map only so the campaign can be saved at the end.
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        var grace = c.Defines.ArmyGroupEmptyDays;
        Check(grace == 30, "a group stands empty thirty days before it is struck off (defines)");
        Check(c.ArmyGroups.Count > 0 && c.Armies.All(a => a.ArmyGroupId < 0 || c.ArmyGroups.Any(g => g.Id == a.ArmyGroupId)),
            $"the 2024 world opens with {c.ArmyGroups.Count} groups and every formation in one that exists");

        // A computer-run country with two groups or more: one of them loses every division.
        var player = c.Player;
        Check(player is { IsHuman: true }, "the bootstrap world has a human player");
        var ai = c.Nations.Where(n => !n.IsHuman && c.ArmyGroups.Count(g => g.NationId == n.Id) >= 2)
            .OrderByDescending(n => c.Armies.Count(a => a.NationId == n.Id)).First();
        var groups = c.ArmyGroups.Where(g => g.NationId == ai.Id).OrderBy(g => g.Id).ToArray();
        var emptied = groups[0];
        var kept = groups[1];
        c.Armies.RemoveAll(a => a.ArmyGroupId == emptied.Id && a.NationId == ai.Id);
        // A formation of another country still pointing at it - left by a merger or a defection.
        var stray = c.Armies.First(a => a.NationId != ai.Id);
        stray.ArmyGroupId = emptied.Id;

        var day = Review(7);
        SetDay(c, day);
        Check(Generals.Tidy(c) == 0, "the first review strikes nothing off");
        Check(emptied.StaffedDay == day && kept.StaffedDay == day,
            "it stamps the staffed group, and starts the clock of the computer's empty one");
        SetDay(c, day + 1);
        Check(Generals.Tidy(c) == 0 && emptied.StaffedDay == day, "off its weekly day the review does nothing");

        // Within the grace a new formation of that country joins it - the smallest group is the empty one.
        var recruit = new Army { Id = c.Armies.Max(a => a.Id) + 1, NationId = ai.Id, Name = "Test recruit", Divisions = 1f };
        c.Armies.Add(recruit);
        Check(Generals.GroupOf(c, recruit) == emptied && recruit.ArmyGroupId == emptied.Id,
            "within the grace a new formation is put under the empty group: reused, not founded anew");
        var reuseDay = Review(day + 1);
        SetDay(c, reuseDay);
        Generals.Tidy(c);
        Check(c.ArmyGroups.Contains(emptied) && emptied.StaffedDay == reuseDay, "staffed again, it stays");

        // Emptied again, and left empty.
        c.Armies.Remove(recruit);
        var lastStaffed = emptied.StaffedDay;
        var before = Review(lastStaffed + grace - 6);
        SetDay(c, before);
        Generals.Tidy(c);
        Check(c.ArmyGroups.Contains(emptied), $"{before - lastStaffed} days empty is inside the grace: kept");
        var after = Review(lastStaffed + grace);
        SetDay(c, after);
        var struck = Generals.Tidy(c);
        Check(struck >= 1 && !c.ArmyGroups.Contains(emptied) && c.ArmyGroups.Contains(kept),
            $"{after - lastStaffed} days empty: struck off, and the staffed group beside it stays");
        Check(stray.ArmyGroupId == -1, "the other country's formation that pointed at it answers to no group now");
        Check(c.Armies.All(a => a.ArmyGroupId < 0 || c.ArmyGroups.Any(g => g.Id == a.ArmyGroupId)),
            "no formation names a group that is not there");
        Check(Generals.Group(c, emptied.Id) is null, "the lookup does not hand back the struck-off group");
        Check(Generals.GroupOf(c, stray) is { } regrouped && regrouped.NationId == stray.NationId,
            "the stray formation joins a group of its own country on its next look");

        // The player's own: made and never filled, it waits for him.
        var issued = c.ArmyGroups.Count;
        new CreateArmyGroupCommand { IssuerId = player.Id, Name = "Army Group North" }.Apply(c);
        Check(c.ArmyGroups.Count == issued + 1, "the player makes a group");
        var north = c.ArmyGroups.Last();
        foreach (var later in new[] { Review(after + 1), Review(after + 60), Review(after + 400) })
        {
            SetDay(c, later);
            Generals.Tidy(c);
        }
        Check(c.ArmyGroups.Contains(north) && north.StaffedDay == -1, "a group the player made and never filled is kept, a year on");
        var division = c.Armies.First(a => a.NationId == player.Id);
        new AssignArmyGroupCommand { IssuerId = player.Id, ArmyId = division.Id, ArmyGroupId = north.Id }.Apply(c);
        var filled = Review(after + 401);
        SetDay(c, filled);
        Generals.Tidy(c);
        Check(north.StaffedDay == filled, "filled, it is staffed");
        c.Armies.Remove(division);
        SetDay(c, Review(filled + grace));
        Generals.Tidy(c);
        Check(!c.ArmyGroups.Contains(north), "filled and then truly empty for the grace, it is struck off like anybody's");

        // A country that is gone loses its empty groups at once.
        var lost = c.Nations.Where(n => !n.IsHuman && n.Id != ai.Id && n.Movement is null && c.StatesOf(n).Length > 0 &&
                                         c.ArmyGroups.Any(g => g.NationId == n.Id))
            .OrderBy(n => c.StatesOf(n).Length).First();
        c.Armies.RemoveAll(a => a.NationId == lost.Id);
        var other = c.Nations.First(n => n.Id != lost.Id && c.StatesOf(n).Length > 0);
        foreach (var region in c.StatesOf(lost)) region.NationMapIndex = other.MapIndex;
        c.InvalidateStateIndex();
        c.Fallen.Add(lost.Id);
        SetDay(c, Review(c.ElapsedDays + 1));
        Generals.Tidy(c);
        Check(!c.ArmyGroups.Any(g => g.NationId == lost.Id), $"{lost.Tag}, fallen with no ground, keeps no group");

        // Saved and fingerprinted.
        var stamped = c.ArmyGroups.First(g => g.StaffedDay >= 0);
        var print = CampaignFingerprint.Of(c);
        stamped.StaffedDay++;
        Check(CampaignFingerprint.Of(c) != print, "the day a group was last staffed is in the fingerprint");
        stamped.StaffedDay--;
        var folder = Path.Combine(Path.GetTempPath(), "dof-army-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SaveSystem.Save(folder, "groups", c, "");
            var loaded = SaveSystem.Load(folder, "groups", "");
            Check(loaded.Success, "the campaign saves and opens: " + loaded.Warning);
            Check(loaded.Campaign.ArmyGroups.Count == c.ArmyGroups.Count &&
                  loaded.Campaign.ArmyGroups.Single(g => g.Id == stamped.Id).StaffedDay == stamped.StaffedDay,
                "the groups and the day each was last staffed survive the save");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }

        // The same review on the same world strikes off the same groups.
        var one = Campaign.CreateBootstrap(20261122);
        var two = Campaign.CreateBootstrap(20261122);
        foreach (var w in new[] { one, two })
        {
            var victim = w.Nations.First(n => !n.IsHuman && w.ArmyGroups.Any(g => g.NationId == n.Id));
            w.Armies.RemoveAll(a => a.NationId == victim.Id);
            SetDay(w, Review(7)); Generals.Tidy(w);
            SetDay(w, Review(7 + grace + 7)); Generals.Tidy(w);
        }
        Check(one.ArmyGroups.Select(g => g.Id).SequenceEqual(two.ArmyGroups.Select(g => g.Id)) &&
              CampaignFingerprint.Of(one) == CampaignFingerprint.Of(two), "deterministic");
    }
}
