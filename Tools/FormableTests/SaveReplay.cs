using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Load a real campaign and let it run, so a crash a player reported can be reproduced
/// instead of argued about.
///
/// The rule this tool obeys: a save is evidence, not a fixture. It is opened read-only, never
/// written back, and the run stops at the first day the simulation refuses to take - printing
/// the day, the exception and the frames that produced it, which is what the in-game diagnostic
/// snapshot does not keep.
/// </summary>
static class SaveReplay
{
    public static void Run(string[] args)
    {
        var at = Array.IndexOf(args, "--replay-save");
        if (at < 0 || at + 1 >= args.Length) throw new Exception("--replay-save needs a path to a .bdfsave");
        var path = Path.GetFullPath(args[at + 1]);
        var days = at + 2 < args.Length && int.TryParse(args[at + 2], out var requested) ? requested : 35;
        if (!File.Exists(path)) throw new Exception($"No save at {path}");

        // Catalogues and the map tables are static; a bare process must build them once before a
        // save that references them can be read at all.
        _ = Campaign.CreateBootstrap(947986505);
        var loaded = SaveSystem.Load(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), "");
        if (!loaded.Success || loaded.Campaign is null)
        {
            Console.WriteLine($"REPLAY_LOAD FAILED {loaded.Warning}");
            throw new Exception(loaded.Warning);
        }
        var c = loaded.Campaign;
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var why))
            throw new Exception(why);
        c.AttachProvinceTopology(topology);
        c.IsPaused = false;

        Console.WriteLine($"REPLAY_LOADED day={c.ElapsedDays} date={c.Date.Display()} file={Path.GetFileName(path)}");
        Console.WriteLine($"REPLAY_WORLD {Summary(c)}");

        var start = c.ElapsedDays;
        for (var day = 0; day < days; day++)
        {
            try
            {
                c.TickDay();
            }
            catch (Exception error)
            {
                Console.WriteLine($"REPLAY_CRASH day={c.ElapsedDays} after={(c.ElapsedDays - start)} " +
                                  $"type={error.GetType().Name} message={error.Message}");
                foreach (var line in (error.StackTrace ?? "").Split('\n').Take(8)) Console.WriteLine("  AT " + line.Trim());
                throw;
            }
            if (c.SimulationFault.Length > 0)
            {
                Console.WriteLine($"REPLAY_FAULT day={c.ElapsedDays} fault={c.SimulationFault}");
                throw new Exception(c.SimulationFault);
            }
        }

        Console.WriteLine($"REPLAY_OK days={c.ElapsedDays - start} day={c.ElapsedDays} date={c.Date.Display()}");
        Console.WriteLine($"REPLAY_WORLD {Summary(c)}");
    }

    /// <summary>What a campaign looks like from outside: enough to tell a living world from a dead one.</summary>
    static string Summary(Campaign c)
    {
        var movements = c.Nations.Count(n => n.Movement is not null);
        var offices = c.States.Count(s => s.AdministeredById >= 0);
        var wars = c.Wars.Count(w => !w.Ended);
        return $"nations={c.Nations.Count} movements={movements} offices={offices} openWars={wars} " +
               $"regions={c.States.Count} chronicle={c.Chronicle.Entries.Count}";
    }
}
