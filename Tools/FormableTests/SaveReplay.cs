using System;
using System.Collections.Generic;
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
        // Opt-in, because a full dump per day is noise on a run that is only looking for a
        // crash: `--replay-detail [n]` names the passes a single day was spent in, and counts
        // the collections it paid for. A weekly spike is invisible in a 30-day total - it is
        // one day out of thirty - and a spike is what a player feels.
        var detailAt = Array.IndexOf(args, "--replay-detail");
        var detail = detailAt >= 0;
        var detailTop = detail && detailAt + 1 < args.Length && int.TryParse(args[detailAt + 1], out var top) ? Math.Clamp(top, 1, 40) : 8;

        // SaveSystem.Load sanitises the slot to letters, digits, '_' and '-'. A player title
        // with spaces is a real file on disk and must still open. The copy is a temp name
        // only; the evidence file is never written back.
        var loadRoot = Path.GetDirectoryName(path)!;
        var loadSlot = Path.GetFileNameWithoutExtension(path);
        var safeSlot = new string(loadSlot.Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').ToArray()).ToLowerInvariant();
        string stagedDir = null;
        if (!string.Equals(loadSlot, safeSlot, StringComparison.Ordinal))
        {
            stagedDir = Path.Combine(Path.GetTempPath(), "dof-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagedDir);
            File.Copy(path, Path.Combine(stagedDir, safeSlot + ".bdfsave"));
            loadRoot = stagedDir;
            loadSlot = safeSlot;
            Console.WriteLine($"REPLAY_STAGED slot={safeSlot} source={Path.GetFileName(path)}");
        }

        // Catalogues and the map tables are static; a bare process must build them once before a
        // save that references them can be read at all.
        _ = Campaign.CreateBootstrap(947986505);
        SaveLoadResult loaded;
        try
        {
            loaded = SaveSystem.Load(loadRoot, loadSlot, "");
        }
        finally
        {
            if (stagedDir is not null)
            {
                try { Directory.Delete(stagedDir, true); }
                catch (Exception error) { Console.WriteLine($"REPLAY_STAGE_CLEANUP {error.Message}"); }
            }
        }
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
        // `--replay-watch TAG,TAG`: the people and the output of named countries at the start and
        // the end of the run - the same list in two builds, so "the fix brought them back" is two
        // rows of numbers rather than a feeling.
        var watchAt = Array.IndexOf(args, "--replay-watch");
        var watch = watchAt >= 0 && watchAt + 1 < args.Length
            ? args[watchAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        Health(c, "load", watch);
        Console.WriteLine($"REPLAY_SCALE armies={c.Armies.Count} battles={c.Battles.Count} wars={c.Wars.Count} " +
                          $"fronts={c.FrontLines.Count} states={c.States.Count} relations={c.Relations.Count}");
        // Smooth time is a frame budget in the Godot loop (SettingsCatalog smooth_ticks →
        // Campaign.StepFor). This replay calls TickDay, which finishes each step in one
        // StepFor(infinity). The same passes run either way; only the frame they land in changes.
        Console.WriteLine("REPLAY_SMOOTH headless=full-step presentation_flag=smooth_ticks not consulted");

        SimProfiler.Reset();
        Warfare.ResetAdvanceRouteSearches();
        var pace = System.Diagnostics.Stopwatch.StartNew();
        long steadyMs = 0;
        var steadyDays = 0;
        // Per-day attribution. The profiler keeps running totals, so a day's own share is the
        // difference from the totals read before it started; the same trick the chronicle uses
        // for flows, and for the same reason - a total is not a day.
        var previousScopeMs = new System.Collections.Generic.Dictionary<string, double>();
        // Baseline the collection counters and the allocation marker before the first day, so
        // the first row of the detail is that day and not the whole load before it.
        _gen0 = GC.CollectionCount(0); _gen1 = GC.CollectionCount(1); _gen2 = GC.CollectionCount(2);
        _allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = c.ElapsedDays;
        for (var day = 0; day < days; day++)
        {
            var dayClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                c.TickDay();
                Console.WriteLine($"REPLAY_DAY n={day + 1} day={c.ElapsedDays} ms={dayClock.ElapsedMilliseconds}");
                // The first day compiles the passes. Its samples are not the crawl the player feels
                // on the days after it, so the scope table below starts once that day has finished.
                if (day == 0)
                {
                    SimProfiler.Reset();
                    Warfare.ResetAdvanceRouteSearches();
                }
                else { steadyMs += dayClock.ElapsedMilliseconds; steadyDays++; }
                if (detail) PrintDayDetail(day + 1, dayClock, detailTop, previousScopeMs);
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

        pace.Stop();
        var ran = c.ElapsedDays - start;
        Console.WriteLine($"REPLAY_OK days={ran} day={c.ElapsedDays} date={c.Date.Display()}");
        Console.WriteLine($"REPLAY_FINGERPRINT {CampaignFingerprint.Of(c):X16}");
        Console.WriteLine($"REPLAY_PACE days={ran} total_ms={pace.ElapsedMilliseconds} " +
                          $"ms_per_day={(ran == 0 ? 0 : pace.ElapsedMilliseconds / (double)ran):0.0} " +
                          $"steady_days={steadyDays} steady_ms={steadyMs} " +
                          $"steady_ms_per_day={(steadyDays == 0 ? 0 : steadyMs / (double)steadyDays):0.0}");
        Console.WriteLine($"REPLAY_WORLD {Summary(c)}");
        Health(c, "end", watch);
        Console.WriteLine($"REPLAY_ROUTE asks={Warfare.AdvanceRouteAsks} keeps={Warfare.AdvanceRouteKeeps} " +
                          $"reuses={Warfare.AdvanceRouteReuses} walks={Warfare.AdvanceRouteSearches}");
        // The allocation column used to be formatted `,8:1`, which is a custom numeric format
        // string: every row printed "1". Reading it as a measurement is how a scope that
        // allocates 34 MB is mistaken for one that allocates nothing.
        foreach (var sample in SimProfiler.All.OrderByDescending(s => s.TotalMilliseconds).Take(30))
            Console.WriteLine($"REPLAY_SCOPE {sample.Name,-32} runs={sample.Runs,-6} total_ms={sample.TotalMilliseconds,10:0.0} " +
                              $"mean_ms={sample.MeanMilliseconds,8:0.00} alloc_mb={sample.AllocatedBytes / 1048576.0,8:0.0}");
        foreach (var sample in SimProfiler.All.OrderByDescending(s => s.AllocatedBytes).Take(detail ? 12 : 0))
            Console.WriteLine($"REPLAY_ALLOC {sample.Name,-32} runs={sample.Runs,-6} alloc_mb={sample.AllocatedBytes / 1048576.0,8:0.0} " +
                              $"kb_per_run={(sample.Runs == 0 ? 0 : sample.AllocatedBytes / 1024.0 / sample.Runs),8:0.0}");
        // The top-30 table cannot see a pass that costs a few milliseconds a day, which is
        // exactly the passes a slice or a parallel gate gets judged on. Named dumps instead.
        foreach (var sample in SimProfiler.All
                     .Where(s => s.Name.Contains("tally:") || s.Name.Contains("mod:") || s.Name.Contains("mkt:"))
                     .OrderByDescending(s => s.TotalMilliseconds))
            Console.WriteLine($"REPLAY_EXACT {sample.Name,-32} runs={sample.Runs,-6} total_ms={sample.TotalMilliseconds,10:0.0} " +
                              $"mean_ms={sample.MeanMilliseconds,8:0.00} alloc_mb={sample.AllocatedBytes / 1048576.0,8:0.0}");
    }

    /// <summary>
    /// What one day was spent in: the passes that grew since the day before it, the collections
    /// it paid for and the heap it left behind.
    ///
    /// A day that costs four times the one before it is the day a player sees as a freeze, and the
    /// thirty-day table cannot name it - the spike is one day out of thirty. Reading the difference
    /// is how "the game is slow" turns into "the weekly pass takes two and a half seconds".
    /// </summary>
    static int _gen0, _gen1, _gen2;
    static long _allocated;

    static void PrintDayDetail(int n, System.Diagnostics.Stopwatch dayClock,
        int top, System.Collections.Generic.Dictionary<string, double> previous)
    {
        var deltas = new System.Collections.Generic.List<(string Name, double Ms)>();
        double topLevel = 0;
        foreach (var sample in SimProfiler.All)
        {
            var before = previous.TryGetValue(sample.Name, out var value) ? value : 0;
            var delta = sample.TotalMilliseconds - before;
            previous[sample.Name] = sample.TotalMilliseconds;
            if (delta <= 0) continue;
            deltas.Add((sample.Name, delta));
            // Scopes nest, so summing all of them counts a stage inside its parent as well. Only
            // the unindented ones are passes of the day in their own right.
            if (sample.Name.Length > 0 && sample.Name[0] != ' ') topLevel += delta;
        }
        var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        Console.WriteLine($"REPLAY_DAY_GC n={n} ms={dayClock.ElapsedMilliseconds} passes_ms={topLevel:0.0} " +
                          $"unmeasured_ms={Math.Max(0, dayClock.ElapsedMilliseconds - topLevel):0.0} " +
                          $"alloc_mb={(allocated - _allocated) / 1048576.0:0.0} " +
                          $"gen0={gen0 - _gen0} gen1={gen1 - _gen1} gen2={gen2 - _gen2} " +
                          $"heap_mb={GC.GetTotalMemory(false) / 1048576.0:0.0}");
        _gen0 = gen0; _gen1 = gen1; _gen2 = gen2; _allocated = allocated;
        foreach (var (name, ms) in deltas.OrderByDescending(d => d.Ms).Take(top))
            Console.WriteLine($"REPLAY_DAY_SCOPE n={n} name={name.Trim()} ms={ms:0.0}");
    }

    /// <summary>
    /// Three things a long campaign can quietly get wrong, counted: countries that hold ground and
    /// are still on the fallen list (the economy then empties them every week), countries with
    /// ground and nobody in them, army groups with nobody under them, and militia that never fill
    /// again. Read at load and at the end, so a repair on load and what the days after it do are
    /// two separate numbers.
    /// </summary>
    static void Health(Campaign c, string when, string[] watch)
    {
        int landed = 0, peopled = 0, fallenLanded = 0, landedEmpty = 0;
        var odd = new System.Collections.Generic.List<string>();
        foreach (var n in c.Nations)
        {
            var held = c.StatesOf(n).Length > 0;
            if (n.Population > 0) peopled++;
            if (!held) continue;
            landed++;
            var fallen = c.Fallen.Contains(n.Id);
            if (fallen) fallenLanded++;
            if (n.Population <= 0) landedEmpty++;
            if (fallen || n.Population <= 0) odd.Add(n.Tag);
            if (fallen && when == "load")
                Console.WriteLine($"REPLAY_FALLEN_LANDED tag={n.Tag} id={n.Id} map={n.MapIndex} movement={(n.Movement is null ? "none" : n.Movement.Id)} " +
                                  $"regions={c.StatesOf(n).Length} region_people={c.StatesOf(n).Sum(s => s.Population)} population={n.Population} " +
                                  $"homeland={(c.Homelands.TryGetValue(n.Id, out var home) ? home.Count : -1)} seceded_from={n.SecededFromId} " +
                                  $"collapse={n.CollapseAnnounced} armies={c.Armies.Count(a => a.NationId == n.Id)} " +
                                  $"chronicle=[{string.Join(" | ", c.Chronicle.Entries.Where(e => e.ActorId == n.Id || e.TargetId == n.Id).TakeLast(4).Select(e => $"{e.Day}:{e.Kind}:{e.ActorId}>{e.TargetId}"))}]");
        }
        var members = new System.Collections.Generic.Dictionary<int, int>();
        var own = new System.Collections.Generic.HashSet<int>();
        var groupNation = c.ArmyGroups.ToDictionary(g => g.Id, g => g.NationId);
        int militia = 0, militiaShort = 0;
        long militiaMen = 0, militiaRoom = 0;
        foreach (var army in c.Armies)
        {
            members[army.ArmyGroupId] = members.GetValueOrDefault(army.ArmyGroupId) + 1;
            if (groupNation.TryGetValue(army.ArmyGroupId, out var owner) && owner == army.NationId) own.Add(army.ArmyGroupId);
            if (army.TemplateId != MilitiaCore.TemplateId) continue;
            militia++;
            militiaMen += Math.Max(0, army.Personnel);
            militiaRoom += Math.Max(0, army.MaxPersonnel);
            if (army.Personnel < army.MaxPersonnel) militiaShort++;
        }
        var groupsEmpty = c.ArmyGroups.Count(g => !members.ContainsKey(g.Id));
        var groupsNoOwn = c.ArmyGroups.Count(g => !own.Contains(g.Id));
        var dangling = c.Armies.Count(a => a.ArmyGroupId >= 0 && !groupNation.ContainsKey(a.ArmyGroupId));
        Console.WriteLine($"REPLAY_HEALTH at={when} day={c.ElapsedDays} landed={landed} peopled={peopled} fallen={c.Fallen.Count} " +
                          $"fallen_landed={fallenLanded} landed_no_people={landedEmpty} " +
                          $"groups={c.ArmyGroups.Count} groups_no_army={groupsEmpty} groups_no_own_army={groupsNoOwn} " +
                          $"armies={c.Armies.Count} armies_dangling_group={dangling} " +
                          $"militia={militia} militia_below_strength={militiaShort} militia_men={militiaMen} militia_room={militiaRoom}");
        if (odd.Count > 0) Console.WriteLine($"REPLAY_LANDED_EMPTY at={when} {string.Join(",", odd)}");
        foreach (var tag in watch)
        {
            var n = c.NationByTag(tag);
            if (n is null) { Console.WriteLine($"REPLAY_WATCH at={when} tag={tag} missing"); continue; }
            Console.WriteLine($"REPLAY_WATCH at={when} tag={tag} fallen={c.Fallen.Contains(n.Id)} regions={c.StatesOf(n).Length} " +
                              $"region_people={c.StatesOf(n).Sum(s => s.Population)} population={n.Population} gdp={n.Gdp:0} " +
                              $"armies={c.Armies.Count(a => a.NationId == n.Id)}");
        }
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
