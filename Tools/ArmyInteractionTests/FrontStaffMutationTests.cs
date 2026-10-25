using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

static class FrontStaffMutationTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL front staff mutation: " + label); Console.WriteLine("PASS: front staff mutation " + label); }

    static (Campaign Campaign, DrawnFrontLine Line, Army Moving) Fixture(bool emptySibling)
    {
        var c = PartialFrontWithdrawalTests.Fixture(divisions: 1);
        c.Nations[0].IsHuman = true;
        var moving = c.Armies[0];
        var line = new DrawnFrontLine { Id = 7, NationId = 0, BorderNationId = 1, Kind = FrontLineKind.Offensive };
        line.Provinces.Add(3); line.ObjectiveSequence.Add(3);
        line.SourceProvinces.UnionWith(new[] { 2, 6 });
        line.ArmyIds.Add(moving.Id); line.Objectives[moving.Id] = 3;
        moving.FrontLineId = line.Id;
        c.FrontLines.Add(line);
        // A full formation can be detached while another force keeps the attack pinned.
        // The latter belongs to no drawn line and cannot itself be commandeered.
        var cover = new Army { Id = 3, NationId = 0, ProvinceId = 2, StateMapIndex = 2002,
            Divisions = 20, Personnel = 200_000, MaxPersonnel = 300_000,
            Strength = 1, Equipment = 1, Organisation = 1, SupplyLevel = 1,
            DirectOrder = true, OrderKind = ArmyOrder.AdvanceFront, AssignedWarId = 882,
            BattleId = c.Battles[0].Id };
        (cover.Latitude, cover.Longitude) = c.ProvinceMap.CenterOf(2);
        c.Armies.Add(cover); c.Battles[0].Attackers.Add(cover.Id);
        if (emptySibling) c.FrontLines.Add(new DrawnFrontLine { Id = 8, NationId = 0 });
        return (c, line, moving);
    }

    public static void Run()
    {
        foreach (var emptySibling in new[] { false, true })
        {
            var (c, line, moving) = Fixture(emptySibling);
            var rng = c.Random.State;
            var second = new DrawnFrontLine { Id = 9, NationId = 0, Kind = FrontLineKind.Fallback };
            second.Provinces.Add(5); second.DefensiveSequence.Add(5);
            var reserve = new Army { Id = 4, NationId = 0, ProvinceId = 5, StateMapIndex = 2005,
                Divisions = 1, Strength = 1, Equipment = 1, Organisation = 1, SupplyLevel = 1,
                FrontLineId = second.Id, OrderKind = ArmyOrder.DefendFront };
            (reserve.Latitude, reserve.Longitude) = c.ProvinceMap.CenterOf(5);
            second.ArmyIds.Add(reserve.Id); c.Armies.Add(reserve); c.FrontLines.Add(second);
            var people = c.Armies.Sum(a => a.Personnel);
            FrontManager.Tick(c);
            Check(!moving.InBattle && moving.OrderProvinceId == 6 && c.Battles[0].Attackers.Contains(3),
                "a whole small formation covers the hole while its rearguard keeps fighting, empty sibling=" + emptySibling);
            Check(c.FrontLines.Contains(line) && moving.FrontLineId == line.Id && line.ArmyIds.Contains(moving.Id),
                "automatic withdrawal keeps its original drawn plan live");
            Check(reserve.OrderProvinceId == 5 && second.Posts.ContainsKey(reserve.Id),
                "a later defensive line is still maintained on the same tick");
            Check(c.FrontLines.Select(l => l.Id).SequenceEqual(new[] { 7, 9 }) && c.Armies.Sum(a => a.Personnel) == people,
                "line order and personnel survive removal of an empty sibling without duplicates");
            Check(c.Random.State == rng, "the safe traversal uses no random draws");
            FrontManager.Tick(c);
            Check(c.FrontLines.Count == 2 && c.FrontLines.Contains(line) && c.SimulationFault.Length == 0,
                "another staff look neither loses nor duplicates plans");
        }
    }

    public static void Replay(string[] args)
    {
        var at = Array.IndexOf(args, "--front-staff-save");
        var path = Path.GetFullPath(args[at + 1]);
        var days = at + 2 < args.Length ? int.Parse(args[at + 2]) : 30;
        _ = Campaign.CreateBootstrap(216847528);
        var loaded = SaveSystem.Load(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), "");
        Check(loaded.Success, "real save loads: " + loaded.Warning);
        var c = loaded.Campaign;
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var why)) throw new Exception(why);
        c.AttachProvinceTopology(map);
        var start = c.ElapsedDays;
        c.IsPaused = false;
        Console.WriteLine($"FRONT_SAVE loaded day={start} date={c.Date} armies={c.Armies.Count} plans={c.FrontLines.Count}");
        for (var day = 0; day < days; day++)
        {
            c.TickDay();
            Check(c.SimulationFault.Length == 0, "replay day " + c.ElapsedDays + ": " + c.SimulationFault);
            Check(c.FrontLines.Select(l => l.Id).Distinct().Count() == c.FrontLines.Count,
                "unique live plans on day " + c.ElapsedDays);
        }
        c.IsPaused = true;
        // Never write beside the source: this is an isolated test artifact, not a repaired
        // version of the player's slot or of a partially executed diagnostic snapshot.
        var output = Path.Combine(Path.GetTempPath(), "dof-front-replay-" + Guid.NewGuid().ToString("N"));
        SaveSystem.Save(output, "replayed", c, "");
        var back = SaveSystem.Load(output, "replayed", "");
        Check(back.Success && back.Campaign.ElapsedDays == c.ElapsedDays, "post-replay save writes and reloads");
        Console.WriteLine($"FRONT_SAVE completed {days} days, {start}->{c.ElapsedDays}; test save={output}");
    }
}
