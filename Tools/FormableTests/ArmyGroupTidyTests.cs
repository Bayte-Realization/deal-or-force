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

        RecruitsKeepTheirHeadquarters(grace);
        StruckIdsAreNotHandedOutAgain(grace);
    }

    /// <summary>
    /// A country rebuilding in peace keeps the headquarters its recruits will join. Nothing puts a
    /// new formation under a group the day it is raised - recruitment, militia, contractors and
    /// defectors all start in none, and GroupOf runs only in battle - so before the fix the review
    /// struck off the veteran group thirty days after its last original division died, and the
    /// recruits' first battle founded a new "Army 1" with a fresh commander: 80 groups with a
    /// record of victories lost that way in the owner's campaign.
    /// </summary>
    static void RecruitsKeepTheirHeadquarters(int grace)
    {
        var c = Campaign.CreateBootstrap(20261123);
        var x = c.Nations.Where(n => !n.IsHuman && c.ArmyGroups.Count(g => g.NationId == n.Id) >= 2)
            .OrderByDescending(n => c.Armies.Count(a => a.NationId == n.Id)).First();
        var groups = c.ArmyGroups.Where(g => g.NationId == x.Id).OrderBy(g => g.Id).ToArray();
        var veteran = groups[0];
        var other = groups[1];
        // Every division of the veteran group lost; the rest of the country's in its other group.
        c.Armies.RemoveAll(a => a.NationId == x.Id && a.ArmyGroupId == veteran.Id);
        foreach (var a in c.Armies.Where(a => a.NationId == x.Id)) a.ArmyGroupId = other.Id;
        foreach (var a in c.Armies.Where(a => a.NationId != x.Id && a.ArmyGroupId == veteran.Id)) a.ArmyGroupId = -1;
        veteran.Victories = 5;
        var skill = veteran.StaffSkill;
        var commander = veteran.Commander;
        // Raised the way Recruitment raises it: answering to no group.
        var recruit = new Army { Id = c.Armies.Max(a => a.Id) + 1, NationId = x.Id, Name = "Test recruit", Divisions = 1f };
        Check(recruit.ArmyGroupId == -1, "a recruit is raised answering to no group");
        c.Armies.Add(recruit);
        var first = Review(7);
        for (var day = first; day <= first + grace * 3; day += 7)
        {
            SetDay(c, day);
            Generals.Tidy(c);
        }
        Check(c.ArmyGroups.Contains(veteran), $"{grace * 3} days with a recruit waiting: the veteran group is kept for it");
        Check(Generals.GroupOf(c, recruit) == veteran && veteran.Victories == 5 && veteran.StaffSkill == skill &&
              veteran.Commander == commander,
            $"and the recruit's first battle puts it under {veteran.Name}, its commander and five victories intact");
        // With nobody waiting for it any more, it goes like any empty group.
        c.Armies.Remove(recruit);
        var last = c.ElapsedDays;
        for (var day = Review(last + 1); day <= last + grace + 14; day += 7)
        {
            SetDay(c, day);
            Generals.Tidy(c);
        }
        Check(!c.ArmyGroups.Contains(veteran) && c.ArmyGroups.Contains(other),
            "with no formation waiting, the empty group is struck off after the grace and the staffed one stays");

        // A group the player made and never filled is not made a filled one by being next in line.
        var player = c.Player;
        // His only group, so it is the one his waiting divisions would join.
        c.ArmyGroups.RemoveAll(g => g.NationId == player.Id);
        foreach (var a in c.Armies.Where(a => a.NationId == player.Id)) a.ArmyGroupId = -1;
        new CreateArmyGroupCommand { IssuerId = player.Id, Name = "Reserve" }.Apply(c);
        var reserve = c.ArmyGroups.Last();
        SetDay(c, Review(c.ElapsedDays + 1));
        Generals.Tidy(c);
        Check(c.ArmyGroups.Contains(reserve) && reserve.StaffedDay == -1,
            "the player's unfilled group, held for his waiting divisions, still counts as never filled");
    }

    /// <summary>
    /// An id or a name a struck-off group had is not handed out again. Ids were the highest
    /// standing plus one: striking off the group with the highest id let the next group take it -
    /// and with it the first commander the id draws, and any order queued for the old group. The
    /// player's default names counted his groups, so striking one in the middle named the next
    /// after one still standing.
    /// </summary>
    static void StruckIdsAreNotHandedOutAgain(int grace)
    {
        var c = Campaign.CreateBootstrap(20261124);
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        Check(c.NextArmyGroupId == 0, "a world that has struck nothing off keeps no counter");
        var highest = c.ArmyGroups.OrderByDescending(g => g.Id).First();
        var y = c.NationById(highest.NationId);
        Check(!y.IsHuman, $"the highest id ({highest.Id}) is a computer country's group ({y.Tag})");
        var struckId = highest.Id;
        var struckCommander = highest.Commander;
        var yGroups = c.ArmyGroups.Where(g => g.NationId == y.Id).Select(g => g.Id).ToHashSet();
        // Its armies all destroyed: every one of its groups empties and is struck off.
        c.Armies.RemoveAll(a => a.NationId == y.Id);
        foreach (var a in c.Armies.Where(a => yGroups.Contains(a.ArmyGroupId))) a.ArmyGroupId = -1;
        for (var day = Review(7); day <= Review(7) + grace + 14; day += 7) { SetDay(c, day); Generals.Tidy(c); }
        Check(!c.ArmyGroups.Any(g => g.NationId == y.Id), $"{y.Tag}'s groups are struck off");
        Check(c.NextArmyGroupId > struckId, $"and the counter stands past the highest id that stood ({c.NextArmyGroupId} > {struckId})");
        // It raises an army and fights: GroupOf founds a group.
        var raised = new Army { Id = c.Armies.Max(a => a.Id) + 1, NationId = y.Id, Name = "Raised again", Divisions = 1f };
        c.Armies.Add(raised);
        var founded = Generals.GroupOf(c, raised);
        Generals.EnsureCommander(c, founded);
        Check(founded.Id > struckId, $"the group founded for it takes a new id ({founded.Id}), not the struck-off {struckId}");
        Check(founded.Commander != struckCommander, $"so a new officer leads it ({founded.Commander}, not {struckCommander})");

        // The player's default names: the lowest number he does not have.
        var player = c.Player;
        for (var i = 0; i < 3; i++) new CreateArmyGroupCommand { IssuerId = player.Id }.Apply(c);
        var made = c.ArmyGroups.Where(g => g.NationId == player.Id && g.Name.StartsWith("Army Group ")).OrderBy(g => g.Id).ToArray();
        Check(made.Select(g => g.Name).SequenceEqual(new[] { "Army Group 1", "Army Group 2", "Army Group 3" }),
            "three new groups are Army Group 1, 2 and 3");
        var divisions = c.Armies.Where(a => a.NationId == player.Id).Take(3).ToArray();
        for (var i = 0; i < 3; i++)
            new AssignArmyGroupCommand { IssuerId = player.Id, ArmyId = divisions[i].Id, ArmyGroupId = made[i].Id }.Apply(c);
        foreach (var a in c.Armies.Where(a => a.NationId == player.Id && a.ArmyGroupId < 0)) a.ArmyGroupId = made[0].Id;
        var filled = Review(c.ElapsedDays + 1);
        SetDay(c, filled); Generals.Tidy(c);
        c.Armies.Remove(divisions[1]);
        for (var day = Review(filled + 1); day <= filled + grace + 14; day += 7) { SetDay(c, day); Generals.Tidy(c); }
        Check(!c.ArmyGroups.Contains(made[1]) && c.ArmyGroups.Contains(made[0]) && c.ArmyGroups.Contains(made[2]),
            "Army Group 2 emptied and is struck off; 1 and 3 stand");
        // An order queued for the struck-off group finds nothing; it does not land on a new one.
        var queued = new AssignArmyGroupCommand { IssuerId = player.Id, ArmyId = divisions[0].Id, ArmyGroupId = made[1].Id };
        new CreateArmyGroupCommand { IssuerId = player.Id }.Apply(c);
        var next = c.ArmyGroups.Last();
        Check(next.Name == "Army Group 2" && c.ArmyGroups.Count(g => g.NationId == player.Id && g.Name == "Army Group 3") == 1,
            $"the next new group is named {next.Name}, not a second Army Group 3");
        Check(next.Id != made[1].Id, "and it does not take the struck-off group's id");
        queued.Apply(c);
        Check(divisions[0].ArmyGroupId == made[0].Id, "an order queued for the struck-off group is refused, not applied to the new one");

        // The counter is saved and fingerprinted.
        var print = CampaignFingerprint.Of(c);
        c.NextArmyGroupId++;
        Check(CampaignFingerprint.Of(c) != print, "the next army group id is in the fingerprint");
        c.NextArmyGroupId--;
        var folder = Path.Combine(Path.GetTempPath(), "dof-army-group-ids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SaveSystem.Save(folder, "ids", c, "");
            var loaded = SaveSystem.Load(folder, "ids", "");
            Check(loaded.Success && loaded.Campaign.NextArmyGroupId == c.NextArmyGroupId,
                $"the counter survives the save ({loaded.Campaign?.NextArmyGroupId})");
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}
