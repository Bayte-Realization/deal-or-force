using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static partial class BenchChecks
{

    internal static int Check(string description, string failure)
    {
        var ok = string.IsNullOrEmpty(failure);
        Console.WriteLine(ok ? $"  [ ok ] {description}" : $"  [FAIL] {description}: {failure}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Run a check with the campaign stepping a whole day at a time.
    ///
    /// The checks that use this count days at sea - a crossing, a blockade, an action fought
    /// to a standstill - and at the shipped one-hour step every one of those days is
    /// twenty-four passes over every fleet in the world. That is the correct simulation and
    /// the wrong price for a check whose subject is not the length of a step.
    ///
    /// Whether an hourly step gives the same answer as a daily one is a real question, and it
    /// is asked where it belongs: the clock checks run the world at four step lengths and
    /// hold the results against each other. Here the step is set to a day, the check runs,
    /// and the step goes back to whatever the defines said.
    /// </summary>
    internal static string ByTheDay(Campaign campaign, Func<string> body)
    {
        var was = campaign.Defines.HoursPerTick;
        campaign.Defines.HoursPerTick = 24;
        try
        {
            return body();
        }
        finally
        {
            campaign.Defines.HoursPerTick = was;
        }
    }

    /// <summary>
    /// One day at sea.
    ///
    /// `NavalOperations.Tick` advances a step, and a step is an hour. Everything at sea
    /// takes its share of the step - the degrees a fleet sails, the supply it burns, the
    /// order it loses in action - so a call is a twenty-fourth of what it used to be, and
    /// a loop that counts calls and prints "days" is off by that factor.
    ///
    /// The checks in this file reason in days, because days are what a player and a
    /// designer reason in: a crossing takes a fortnight, a blockade bites in a month. So
    /// they call this and keep their arithmetic, at any tick length the defines allow.
    /// </summary>
    internal static void SailADay(Campaign campaign)
    {
        var steps = (int)MathF.Round(1f / MathF.Max(0.0001f, campaign.TickScale));
        for (var step = 0; step < MathF.Max(1, steps); step++) NavalOperations.Tick(campaign);
    }

    /// <summary>
    /// Two worlds, ten years each, one raster apiece: region id per map cell.
    /// </summary>
    internal static int WriteGoreMaps(string folder)
    {
        Directory.CreateDirectory(folder);
        var width = DealOrForce.Simulation.WorldMapSource.Width;
        var height = DealOrForce.Simulation.WorldMapSource.Height;

        foreach (var tidy in new[] { true, false })
        {
            var campaign = Campaign.CreateBootstrap(20241227);
            if (!AttachMap(campaign))
            {
                Console.WriteLine("no province map; nothing to draw");
                return 1;
            }
            if (!tidy)
            {
                campaign.Defines.PeaceRoundUpShare = 0f;
                campaign.Defines.PeaceRoundDownShare = 0f;
            }
            campaign.IsPaused = false;
            for (var day = 0; day < 365 * 10; day++) campaign.TickDay();

            var topology = campaign.ProvinceMap!;
            var cells = new byte[width * height * 4];
            for (var cell = 0; cell < width * height; cell++)
            {
                var province = topology.CellToProvince[cell];
                var region = province == 0 ? 0 : topology.ProvinceToState[province];
                var owner = province == 0 ? 0 : topology.ProvinceToNation[province];
                cells[cell * 4 + 0] = (byte)(region & 0xFF);
                cells[cell * 4 + 1] = (byte)((region >> 8) & 0xFF);
                cells[cell * 4 + 2] = (byte)(owner & 0xFF);
                cells[cell * 4 + 3] = (byte)((owner >> 8) & 0xFF);
            }

            var name = tidy ? "tidy" : "ragged";
            File.WriteAllBytes(Path.Combine(folder, $"{name}.bin"), cells);
            Console.WriteLine($"  {name}: {campaign.States.Count:N0} region(s) after ten years, " +
                              $"{campaign.Wars.Sum(war => war.CreatedStates):N0} cut in two");
        }

        File.WriteAllText(Path.Combine(folder, "size.txt"), $"{width} {height}");
        Console.WriteLine($"  wrote {width}x{height} rasters to {folder}");
        return 0;
    }

    /// <summary>
    /// The war layer needs a province graph, and until now the bench never built one - so
    /// warfare, logistics and combat measured zero milliseconds because they never ran.
    /// The raster loader is engine-neutral, exactly like the rest of the simulation, so
    /// the bench can build the same 37 807-province graph the game does and actually
    /// exercise the half of the simulation that fights.
    /// </summary>
    internal static bool AttachMap(Campaign campaign)
    {
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States, DealOrForce.Simulation.WorldMapSource.Control,
                DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height, out var topology, out var message))
        {
            Console.WriteLine($"  province map unavailable: {message}");
            Console.WriteLine("  warfare, logistics and combat will not be exercised by this run.");
            return false;
        }
        if (MapRasterCatalog.TryLoadHydrology(DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height,
                out var hydrology, out _))
            topology.AttachHydrology(hydrology);
        campaign.AttachProvinceTopology(topology);
        Console.WriteLine($"Province graph attached: {topology.ProvinceCount:N0} provinces, " +
                          $"{topology.EdgeCount:N0} land edges, {campaign.Armies.Count:N0} divisions deployed.");
        return true;
    }

    /// <summary>
    /// A snapshot of who holds what, so a run can be asked how far a front actually moved
    /// rather than whether anything crashed.
    /// </summary>
    internal sealed class FrontSnapshot
    {
        private readonly Dictionary<int, int> _provinces = new();

        internal FrontSnapshot(Campaign campaign)
        {
            if (campaign.ProvinceController is null) return;
            for (var province = 1; province < campaign.ProvinceController.Length; province++)
            {
                var owner = campaign.ProvinceController[province];
                _provinces[owner] = _provinces.GetValueOrDefault(owner) + 1;
            }
        }

        internal int Held(Nation nation) =>
            nation is null ? 0 : _provinces.GetValueOrDefault(nation.MapIndex);
    }

    /// <summary>
    /// The scenario opens in the third year of a war that has been a metre-by-metre
    /// stalemate. If a year of hands-off simulation moves the line by hundreds of
    /// provinces, the combat model is wrong about the war it is depicting - whichever
    /// side is winning.
    /// </summary>
    internal static int HeldBy(Campaign campaign, int nationMapIndex)
    {
        var held = 0;
        var controllers = campaign.ProvinceController;
        if (controllers is null) return 0;
        for (var province = 1; province < controllers.Length; province++)
            if (controllers[province] == (ushort)nationMapIndex) held++;
        return held;
    }

    internal static void TraceFront(Campaign campaign, int day)
    {
        var russia = campaign.NationByTag("RUS");
        var ukraine = campaign.NationByTag("UKR");
        if (russia is null || ukraine is null) return;
        var war = campaign.Wars.Find(w => !w.Ended && w.Involves(russia.Id) && w.Involves(ukraine.Id));
        if (war is null) { Console.WriteLine($"  front d{day}: no live RUS-UKR war"); return; }
        string Side(Nation nation)
        {
            var force = campaign.Armies.Where(a => a.NationId == nation.Id && a.AssignedWarId == war.Id).ToList();
            var strength = force.Count == 0 ? 0f : force.Average(a => a.Strength);
            return $"{nation.Tag} reserve {Fmt.People(nation.ManpowerReserve)} " +
                   $"(ceiling {Fmt.People(nation.Population * campaign.Defines.MobilisationCeilingShare)}), " +
                   $"cap {Fmt.People(nation.ReinforcementCapacity)}/d, put back {Fmt.People(nation.ReinforcedLastWeek)}/wk, " +
                   $"{force.Count} div at {strength:P0}, overrun {WarWeariness.Overrun(campaign, nation)}, " +
                   $"war support {nation.WarSupport:P0} | " +
                   // How the force stands, not only how big it is: a front breaks when the line is
                   // raw, tired or cut off long before it runs out of men.
                   $"org {(force.Count == 0 ? 0f : force.Average(a => a.Organisation)):P0}, " +
                   $"exp {(force.Count == 0 ? 0f : force.Average(a => a.Experience)):P0}, " +
                   $"dug {(force.Count == 0 ? 0f : force.Average(a => a.Entrenchment)):P0}, " +
                   $"{force.Count(a => a.CutOff)} cut off, {force.Count(a => a.InBattle)} in battle";
        }
        Console.WriteLine($"  front d{day}: intensity {war.Intensity}, losses {war.AttackerLosses:N0}/{war.DefenderLosses:N0} " +
                          $"(killed {war.AttackerKilled:N0}/{war.DefenderKilled:N0}, taken {war.AttackerCaptured:N0}/{war.DefenderCaptured:N0}), " +
                          $"occupied A {war.AttackerOccupiedValue:P1} D {war.DefenderOccupiedValue:P1}");
        Console.WriteLine($"      {Side(russia)}");
        Console.WriteLine($"      {Side(ukraine)}");
    }

    /// <summary>
    /// Load a player's save and play on from it, headless, until the day count runs out or
    /// the simulation faults - whichever comes first. The point is the stack: a crash a
    /// player walked into is asked to happen here, where the exception can be read in full,
    /// instead of in a dialog that only quotes its message.
    /// </summary>
    /// <summary>
    /// Where a player's saves are when `--replay` is given no folder: the one game folder under
    /// Documents, the same one the game writes to.
    /// </summary>
    internal static string DefaultSaveRoot()
    {
        var folders = DealOrForce.Simulation.GameFolders.For(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        return folders?.Saves ?? Path.Combine(AppContext.BaseDirectory, "saves");
    }

    internal static int ReplaySave(string root, string slot, int days)
    {
        if (!Directory.Exists(root)) { Console.WriteLine($"  no such save root: {root}"); return 1; }
        SaveLoadResult loaded;
        try { loaded = SaveSystem.Load(root, slot, ""); }
        catch (Exception ex) { Console.WriteLine("  load failed: " + ex.Message); return 1; }
        var campaign = loaded.Campaign;
        if (campaign is null) { Console.WriteLine("  the save carried no campaign"); return 1; }
        campaign.IsPaused = false;
        Console.WriteLine($"  replay: {slot} from {campaign.Date} ({campaign.ElapsedDays} days), {days} day(s) on");
        for (var day = 0; day < days; day++)
        {
            try { campaign.TickDay(); }
            catch (Exception ex)
            {
                Console.WriteLine($"  [CRASH] day {campaign.ElapsedDays} ({campaign.Date}): {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine(ex.StackTrace ?? "");
                return 1;
            }
            if (campaign.SimulationFault.Length > 0)
            {
                Console.WriteLine($"  [FAULT] day {campaign.ElapsedDays} ({campaign.Date}): {campaign.SimulationFault}");
                return 1;
            }
            if (day % 10 == 9) Console.WriteLine($"  replayed to day {campaign.ElapsedDays} ({campaign.Date})");
        }
        Console.WriteLine($"  [ ok ] {days} day(s) replayed from {slot} without a fault");
        return 0;
    }

}
