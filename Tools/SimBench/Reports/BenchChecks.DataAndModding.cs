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

    /// <summary>Guards the cached ownership index against the linear scan it replaced.</summary>
    internal static string IndexAgrees(Campaign campaign)
    {
        foreach (var nation in campaign.Nations.Take(60))
        {
            var indexed = campaign.StatesOf(nation).Length;
            var scanned = campaign.States.Count(state => state.NationMapIndex == nation.MapIndex);
            if (indexed != scanned)
                return $"{nation.Tag}: index says {indexed}, scan says {scanned}";
        }
        return "";
    }

    /// <summary>
    /// Applies a scripted effect through the same path content uses and checks the value
    /// actually moved, so a refactor that quietly breaks every event is caught here
    /// rather than by a player wondering why nothing happens.
    /// </summary>
    internal static string ScriptedContentWorks(Campaign campaign)
    {
        var nation = campaign.Nations.FirstOrDefault(candidate => candidate.Gdp > 100f);
        if (nation is null) return "no nation to test against";

        var before = nation.Stability;
        ScriptedEffects.From(("stability", 0.05f)).Apply(campaign, nation);
        if (MathF.Abs(nation.Stability - before) < 0.0001f) return "a scripted effect changed nothing";
        nation.Stability = before;

        if (!ScriptedEffects.IsKnown("treasury_percent")) return "suffix operations stopped resolving";
        if (ScriptedEffects.IsKnown("not_a_real_key")) return "unknown keys are being accepted";
        if (!EventCatalog.All.Any()) return "no events loaded";
        return "";
    }

    internal static string ModifiersWork(Campaign campaign)
    {
        var nation = campaign.Nations.FirstOrDefault(candidate => candidate.Gdp > 100f);
        if (nation is null) return "no nation to test against";
        if (!ModifierCatalog.All.Any()) return "no modifiers loaded";

        var id = ModifierCatalog.All.First().Id;
        if (!ModifierSystem.Grant(campaign, nation, id, 30)) return $"could not grant '{id}'";
        if (!ModifierSystem.Has(nation, id)) return $"'{id}' did not attach";

        // Re-granting must refresh rather than stack.
        var count = nation.Modifiers.Count;
        ModifierSystem.Grant(campaign, nation, id, 30);
        if (nation.Modifiers.Count != count) return "re-granting stacked a duplicate";

        if (!ModifierSystem.Revoke(nation, id)) return $"could not revoke '{id}'";
        return "";
    }

    /// <summary>
    /// The shipped skin.json is what a modder copies as their starting point, so it has to
    /// still say what the game actually uses. A token added in code and forgotten in the
    /// file - or the reverse - is caught here rather than by a modder overriding a key
    /// that does nothing.
    /// </summary>
    internal static string SkinIsCoherent()
    {
        var tokens = InterfaceSkin.Tokens.ToList();
        if (tokens.Count < 40) return $"only {tokens.Count} interface tokens loaded";

        var overridden = InterfaceSkin.Overridden;
        if (overridden.Count > 0)
            return "game/interface/skin.json disagrees with the compiled defaults for: " +
                   string.Join(", ", overridden.Take(8));

        // The layout half of the same file. Colour drifting was bad enough; geometry
        // drifting would move panels off the screen edge with nothing to say why.
        var panels = InterfaceSkin.Panels.Count();
        if (panels < 15) return $"only {panels} panels have a layout";
        var moved = InterfaceSkin.MovedPanels;
        if (moved.Count > 0)
            return "the shipped layout disagrees with the compiled one for: " +
                   string.Join(", ", moved.Take(8));

        // A mod's layout override has to take, and must not need to restate the anchors.
        // What "undone" means is read off the panel before the override rather than written
        // in as a number: it used to say 14, which was hud.top's compiled offset until the HUD
        // was rearranged, and the moment the defaults caught up with the rearrangement this
        // check failed on a layout that was correct.
        var shippedLeft = InterfaceSkin.Layout("hud.top").OffsetLeft;
        InterfaceSkin.Merge("{\"layout\":{\"hud.top\":{\"offsets\":[1,2,3,4]}}}", "bench", null);
        var moving = InterfaceSkin.Layout("hud.top");
        if (Math.Abs(moving.OffsetLeft - 1f) > 0.001f || Math.Abs(moving.OffsetBottom - 4f) > 0.001f)
            return "a layout override did not apply";
        if (Math.Abs(moving.AnchorRight - 1f) > 0.001f)
            return "a layout override that only set offsets also lost the anchors";
        InterfaceSkin.ResetCatalog();
        if (Math.Abs(InterfaceSkin.Layout("hud.top").OffsetLeft - shippedLeft) > 0.001f)
            return "resetting the skin did not undo a layout override";

        foreach (var (token, value) in tokens)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '#')
                return $"'{token}' is not a colour: \"{value}\"";
            if (value.Length - 1 is not (3 or 4 or 6 or 8))
                return $"'{token}' = \"{value}\" is not #rgb, #rrggbb or #rrggbbaa";
        }

        // Roles the interface asks for by name everywhere. Losing one of these turns a
        // screen magenta rather than throwing, which is exactly the kind of failure a
        // build should catch instead of a player.
        foreach (var required in new[]
                 {
                     "text.accent", "text.bright", "text.body", "text.caption", "text.faint",
                     "state.positive", "state.negative", "state.warning",
                     "button.text", "button.fill", "button.border",
                     "surface.background", "map.player", "map.at_war",
                 })
            if (!InterfaceSkin.Has(required)) return $"required token '{required}' is missing";

        // A mod's override has to actually take, and resetting has to actually undo it.
        InterfaceSkin.Merge("{\"colors\":{\"text.accent\":\"#123456\"}}", "bench", null);
        if (InterfaceSkin.Colour("text.accent") != "#123456") return "a skin override did not apply";
        InterfaceSkin.ResetCatalog();
        if (InterfaceSkin.Colour("text.accent") == "#123456") return "resetting the skin did not undo an override";

        return "";
    }

    /// <summary>
    /// Headline figures are formatted once and read everywhere, so the boundaries between
    /// units are worth pinning down: a trillion-dollar economy printed as "$21433.20B" is
    /// the bug this format exists to prevent.
    /// </summary>
    /// <summary>
    /// Every loaded language names itself, names a plural rule and a culture that exist, and
    /// never asks string.Format for an argument the English does not pass. Each of those reads
    /// fine in a code review and wrong on the screen, where the player sees "{2}" or a raw
    /// pattern, so the bench reads them all instead.
    /// </summary>
    internal static string TranslationsAreSound()
    {
        var problems = Loc.Languages
            .Where(language => !string.Equals(language, Loc.DefaultLanguage, StringComparison.OrdinalIgnoreCase))
            .SelectMany(Loc.Problems)
            .ToList();
        var checkedLanguages = Loc.Languages.Count - 1;

        // And the check has to be able to fail: a probe language with every fault in it - no
        // name, a plural rule that does not exist, an argument the English never passes and a
        // brace that does not close.
        Loc.Merge("probe_broken", "{ \"Population\": \"x\", \"{0} of {1}\": \"{0} {2}\", " +
                                  "\"Wrote {0} line(s)\": \"{0 lines\", \"language.plural\": \"dual\" }", null);
        var caught = Loc.Problems("probe_broken");
        Loc.ResetCatalog();
        if (caught.Count < 4)
            return $"the translation check caught {caught.Count} of the probe's 4 faults: {string.Join(" / ", caught)}";

        // And the worked examples in mods/_templates: a translation template that fails its own
        // check teaches exactly the mistake the check exists to catch.
        var templates = FindTemplates();
        var exampleLanguages = 0;
        if (templates is not null)
        {
            foreach (var folder in Directory.EnumerateDirectories(templates))
            {
                var localization = Path.Combine(folder, "localization");
                if (!Directory.Exists(localization)) continue;
                var before = Loc.Languages.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var errors = new List<string>();
                Loc.LoadDirectory(localization, errors);
                var added = Loc.Languages.Where(language => !before.Contains(language)).ToList();
                problems.AddRange(errors.Select(error => $"{Path.GetFileName(folder)}: {error}"));
                foreach (var language in added)
                    problems.AddRange(Loc.Problems(language).Select(problem => $"{Path.GetFileName(folder)}: {problem}"));
                exampleLanguages += added.Count;
                Loc.ResetCatalog();
            }
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems.Take(40)) Console.WriteLine("    " + problem);
            return $"{problems.Count} problem(s) in {checkedLanguages} translation(s); the first is: {problems[0]}";
        }
        Console.WriteLine($"  translations: {checkedLanguages} language(s) besides English and {exampleLanguages} in the templates, " +
                          string.Join(", ", Loc.Languages.Select(language =>
                              $"{Loc.DisplayName(language)} ({Loc.Keys(language).Count():N0} keys, {Loc.CultureOf(language).Name})")));
        return "";
    }

    /// <summary>
    /// How often each system runs over a month, at a day's step and at an hour's.
    ///
    /// <see cref="Campaign.Tick"/> divides its work in two, and which half a pass belongs to
    /// is a decision about what that pass IS. Something that accumulates - a march, a supply
    /// draw, a round of fighting - runs per step and takes its share through
    /// <see cref="Campaign.TickScale"/>. Something that HAPPENS ON A DAY - the weekly
    /// clearing, an event roll, an army rebuilt to its establishment - runs once for each
    /// whole day the step crossed.
    ///
    /// Nothing checked which half a pass was in, and the failure is invisible at a daily
    /// step because a step IS a day there. At an hourly one it is twenty-four economies a
    /// day, twenty-four event rolls, and a country that rebuilds its army twenty-four times
    /// in one morning - not a cost, a different simulation.
    ///
    /// Counting tells them apart, and the profiler already counts.
    /// </summary>
    internal static string EverySystemRunsOnItsOwnCadence()
    {
        static Dictionary<string, int> RunsOverAMonth(int hoursPerTick, int days)
        {
            SimProfiler.Reset();
            var campaign = Campaign.CreateBootstrap(20240404);
            campaign.Defines.HoursPerTick = hoursPerTick;
            campaign.IsPaused = false;
            for (var day = 0; day < days; day++) campaign.TickDay();
            var counted = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var sample in SimProfiler.All) counted[sample.Name] = sample.Runs;
            return counted;
        }

        const int days = 30;
        var atADay = RunsOverAMonth(24, days);
        var atAnHour = RunsOverAMonth(1, days);
        SimProfiler.Reset();

        // Things that happen ON a day. Every one of these divides the day number somewhere
        // inside it, or is called from the loop that runs once per whole day crossed.
        var onceADay = new[]
        {
            "economy", "opinion", "modifiers", "militia", "diplomacy", "subjects",
            "statecraft", "events", "history", "secession", "governance", "occupations"
        };
        var daily = 0;
        foreach (var name in onceADay)
        {
            if (!atADay.TryGetValue(name, out var slow) || !atAnHour.TryGetValue(name, out var fast)) continue;
            daily++;
            if (fast != slow)
                return $"'{name}' happens on a day, and over {days} days it ran {slow:N0} time(s) at " +
                       $"a daily step and {fast:N0} at an hourly one - it is running per step";
        }
        if (daily == 0) return "not one of the once-a-day passes reported itself to the profiler";

        // And the ones that accumulate, which SHOULD multiply: twenty-four steps in a day,
        // each taking a twenty-fourth. One of these that did not multiply is being skipped
        // rather than scaled, which is the same bug seen from the other side.
        var perStep = new[] { "logistics", "military", "naval", "warfare", "attrition", "construction" };
        var stepped = 0;
        foreach (var name in perStep)
        {
            if (!atADay.TryGetValue(name, out var slow) || !atAnHour.TryGetValue(name, out var fast)) continue;
            stepped++;
            if (fast != slow * 24)
                return $"'{name}' accumulates, so over {days} days it should run {slow * 24:N0} time(s) " +
                       $"at an hourly step against {slow:N0} at a daily one, and it ran {fast:N0}";
        }
        if (stepped == 0) return "not one of the per-step passes reported itself to the profiler";

        Console.WriteLine($"  cadence: over {days} days, {daily:N0} pass(es) happen on a day and ran the " +
                          $"same number of times at 1 and 24 hours a step; {stepped:N0} accumulate and ran " +
                          "exactly twenty-four times as often.");
        return "";
    }

    /// <summary>
    /// Run the same stretch of world at every step length the game offers, and see whether
    /// the worlds recognise each other - over a day, a week, a month and a year.
    ///
    /// This is the promise written into <see cref="Defines.HoursPerTick"/>: the step is a
    /// pace, not a difficulty. It is four claims rather than one, and they break for
    /// different reasons. A DAY is where a rounding shows, and where a pass that runs per
    /// step instead of per day is out by exactly the number of steps in a day. A WEEK is the
    /// shortest horizon by which the economy has had to clear. A MONTH is where things with
    /// a duration - construction, training - either finish or do not. A YEAR is where
    /// anything that compounds has had time to, and a drift of a tenth of a per cent a week
    /// has become a different world.
    ///
    /// The step lengths come from `common/settings` rather than from a list here, so a mod
    /// that offers a different pace is checked by these same lines.
    /// </summary>
    internal static string TheClockRunsAtAnyStep()
    {
        static (string Date, long Hours, int Days, long People, int Armies) Run(int hoursPerTick, int hours)
        {
            var campaign = Campaign.CreateBootstrap(20240404);
            campaign.Defines.HoursPerTick = hoursPerTick;
            campaign.IsPaused = false;
            for (var elapsed = 0; elapsed < hours; elapsed += hoursPerTick) campaign.Tick();
            return (campaign.Date.ToString(), campaign.ElapsedHours, campaign.ElapsedDays,
                    campaign.Nations.Sum(nation => nation.Population), campaign.Armies.Count);
        }

        // What the settings screen offers, in hours, shortest first. Read rather than
        // listed, so this checks whatever a mod decided the paces are.
        var declaredSteps = new List<int>();
        var stepSetting = SettingsCatalog.Of("hours_per_tick");
        if (stepSetting is not null)
            foreach (var value in stepSetting.Values)
                if (int.TryParse(value, out var step) && step > 0) declaredSteps.Add(step);
        if (declaredSteps.Count == 0) declaredSteps.Add(new Defines().HoursPerTick);
        declaredSteps.Sort();

        // A day, a week, a month, a year - and how far apart two worlds may end up at each,
        // which is not one tolerance: a difference a day cannot show has had a year to grow.
        var horizons = new (string Name, int Days, double People, double Armies)[]
        {
            ("a day", 1, 0.0010, 0.03),
            ("a week", 7, 0.0030, 0.05),
            ("a month", 30, 0.0100, 0.06),
            ("a year", 365, 0.0400, 0.15)
        };

        var covered = new List<string>();
        foreach (var (name, days, peopleAllowed, armiesAllowed) in horizons)
        {
            var hours = days * 24;
            // A step that does not divide the horizon cannot land on it: one day at a
            // three-day step is three days, and comparing the two would be comparing two
            // different amounts of world. Saying so is part of the check rather than a
            // convenience - it is why a 48-hour step is absent from the day and the week.
            var steps = declaredSteps.Where(step => hours % step == 0).ToArray();
            if (steps.Length < 2) continue;

            var baseline = Run(steps[^1], hours);
            var worstPeople = 0.0;
            var worstArmies = 0.0;
            foreach (var step in steps)
            {
                var run = Run(step, hours);

                if (run.Hours != baseline.Hours)
                    return $"over {name}, a {step}-hour step reached {run.Hours:N0} hours where a " +
                           $"{steps[^1]}-hour step reached {baseline.Hours:N0}";
                if (run.Days != baseline.Days)
                    return $"over {name}, a {step}-hour step reached day {run.Days:N0} where a " +
                           $"{steps[^1]}-hour step reached day {baseline.Days:N0}";
                if (!string.Equals(run.Date, baseline.Date, StringComparison.Ordinal))
                    return $"over {name}, a {step}-hour step ended on {run.Date} and a " +
                           $"{steps[^1]}-hour step on {baseline.Date}";

                // The world itself only has to be recognisable. A pass that ran the wrong
                // number of times shows up here as a figure out by a multiple rather than by
                // a rounding, which is exactly what this is for.
                var people = Math.Abs(run.People - baseline.People) / (double)Math.Max(1L, baseline.People);
                var armies = Math.Abs(run.Armies - baseline.Armies) / (double)Math.Max(1, baseline.Armies);
                if (people > peopleAllowed)
                    return $"over {name}, a {step}-hour step and a {steps[^1]}-hour one disagree " +
                           $"about the population by {people:P2}: {run.People:N0} against {baseline.People:N0}";
                if (armies > armiesAllowed)
                    return $"over {name}, a {step}-hour step and a {steps[^1]}-hour one disagree " +
                           $"about the armies by {armies:P1}: {run.Armies:N0} against {baseline.Armies:N0}";
                worstPeople = Math.Max(worstPeople, people);
                worstArmies = Math.Max(worstArmies, armies);
            }
            covered.Add($"{name} at {string.Join("/", steps)}h (people \u00b1{worstPeople:P2}, " +
                        $"armies \u00b1{worstArmies:P1})");
        }
        if (covered.Count < horizons.Length)
            return $"only {covered.Count} of {horizons.Length} horizons could be checked - the " +
                   "settings screen offers too few step lengths that divide a day";

        // And a day is a day however it is reached: asking a campaign to advance one, at any
        // step, has to advance exactly one until the step is longer than a day itself.
        foreach (var step in declaredSteps)
        {
            var campaign = Campaign.CreateBootstrap(20240404);
            campaign.Defines.HoursPerTick = step;
            campaign.IsPaused = false;
            var before = campaign.ElapsedDays;
            campaign.TickDay();
            var crossed = campaign.ElapsedDays - before;
            var wanted = Math.Max(1, step / 24);
            if (crossed != wanted)
                return $"TickDay at a {step}-hour step advanced {crossed:N0} day(s) rather than {wanted:N0}";
        }

        // The setting a player actually turns, against the value the code compiles with. They
        // drifted apart in the worst possible way once already: the preference was applied
        // only to a campaign that already existed, and it is chosen before there is one, so
        // choosing six hours changed nothing at all and the clock never moved.
        var declared = SettingsCatalog.Chosen("hours_per_tick");
        if (!int.TryParse(declared, out var declaredHours) || declaredHours <= 0)
            return $"common/settings offers '{declared}' as a step length, which is not hours";
        if (declaredHours != new Defines().HoursPerTick)
            return $"the settings screen defaults to {declaredHours} hours a step and the code " +
                   $"to {new Defines().HoursPerTick}";

        // And the clock has to be printable. A campaign stepping in fractions of a day
        // spends part of it at midnight, and a clock that vanished then would read as a bug.
        var midnight = new GameDate(2024, 1, 1);
        if (midnight.Display(true) == midnight.Display(false))
            return "the date reads the same whether or not it was asked to carry the time";
        if (!midnight.Display(true).Contains("00:00", StringComparison.Ordinal))
            return $"a date asked to carry the time printed '{midnight.Display(true)}'";

        Console.WriteLine($"  clock: {declaredSteps.Count:N0} step length(s) offered " +
                          $"({string.Join("/", declaredSteps)}h), default {new Defines().HoursPerTick}h; " +
                          $"{string.Join("; ", covered)}.");
        return "";
    }

    /// <summary>
    /// The contract the whole data layout rests on: a category is a folder, everything in
    /// it is read in file-name order, and a later file wins.
    ///
    /// This is what makes a mod cheap - it drops a file in beside the base game's instead
    /// of forking one - and it is easy to break by accident, because breaking it is silent.
    /// Renaming the shipped files to numbered ones broke three loaders that were still
    /// asking for the old names, and the game booted, and history simply did not load.
    /// So the check writes a file into a real category folder, reloads, and takes it away
    /// again.
    /// </summary>
    /// <summary>
    /// The interface must ask for pictures by name, and the names must be declared in data.
    ///
    /// Twelve texture paths used to be spelled out in the middle of the interface code,
    /// which made every one of them a picture no mod could replace without forking a C#
    /// file. The check is deliberately two-sided: the declarations have to exist and
    /// resolve, and a later file has to win - because a `gfx/` folder that is read but not
    /// layered is a folder a mod can only replace wholesale, which is the thing the layout
    /// exists to avoid.
    /// </summary>
    internal static string ArtworkIsNamedRatherThanSpelledOut()
    {
        SpriteCatalog.Load();
        if (SpriteCatalog.Count == 0) return "gfx/ declares no sprites at all";

        // The pieces the interface will not boot without.
        var required = new[]
        {
            "icon_army_advance", "icon_diplomacy", "icon_plan_front",
            "panel_dossier", "panel_command_frame", "brand_logo_banner",
            "globe_capital_star", "globe_division_marker", "globe_space_background",
            "globe_plan_arrows"
        };
        var missing = required.Where(name => !SpriteCatalog.Has(name)).ToList();
        if (missing.Count > 0)
            return $"gfx/ declares nothing for {string.Join(", ", missing)}";

        // Declared paths must point at art that is actually there. A sprite table whose
        // entries resolve to nothing is worse than no sprite table: the fallback hides it
        // in the shipped game and the mod that overrides it inherits a broken path.
        var root = GameDataLoader.DataFolder("gfx");
        var projectRoot = root is null ? null : Directory.GetParent(root)?.Parent?.FullName;
        var absent = new List<string>();
        foreach (var name in SpriteCatalog.Names)
        {
            var texture = SpriteCatalog.Get(name).Texture;
            if (projectRoot is null || !texture.StartsWith("res://", StringComparison.Ordinal)) continue;
            var relative = texture.Substring("res://".Length).Replace('/', Path.DirectorySeparatorChar);
            if (!File.Exists(Path.Combine(projectRoot, relative))) absent.Add($"{name} -> {texture}");
        }
        if (absent.Count > 0)
            return $"{absent.Count:N0} sprite(s) point at art that is not there: " +
                   string.Join("; ", absent.Take(3));

        // The convention is the point: a picture in the tree is a sprite because of what
        // it is called, not because a file somewhere lists it. If that ever needs a
        // declaration again, a modder is back to writing JSON to replace an icon.
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root!, "*.json", SearchOption.AllDirectories))
        {
            if (file.Replace('\\', '/').Contains("/models/", StringComparison.OrdinalIgnoreCase)) continue;
            using (var document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson))
            {
                var body = document.RootElement;
                if (body.TryGetProperty("sprites", out var inner)) body = inner;
                if (body.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in body.EnumerateObject()) declared.Add(entry.Name);
            }
        }
        var byConvention = SpriteCatalog.Names.Count(name => !declared.Contains(name));
        if (byConvention < 20)
            return $"only {byConvention:N0} sprite(s) come from the file names alone; " +
                   "the art is not laid out where the game looks for it";
        if (declared.Contains("icon_diplomacy"))
            return "icon_diplomacy still needs a declaration, so the convention is not carrying it";

        // And the layering, tested the way a mod would use it.
        var before = SpriteCatalog.TextureOf("panel_dossier");
        SpriteCatalog.Merge("{ \"sprites\": { \"panel_dossier\": \"interface/panels/mine.png\" } }",
            "user://mods/example/gfx", "test", null);
        var after = SpriteCatalog.TextureOf("panel_dossier");
        SpriteCatalog.Load();
        if (after == before)
            return "a later gfx file did not override an earlier one";
        if (after != "user://mods/example/gfx/interface/panels/mine.png")
            return $"a mod's own art resolved to '{after}' instead of inside the mod folder";

        Console.WriteLine($"  artwork: {SpriteCatalog.Count:N0} named sprite(s) in gfx/, " +
                          $"{byConvention:N0} of them from the file name alone, every path present, " +
                          "and a later file wins.");
        return "";
    }

    internal static string ASecondCampaignGetsTheMapToo()
    {
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(DealOrForce.Simulation.WorldMapSource.States,
                DealOrForce.Simulation.WorldMapSource.Control, DealOrForce.Simulation.WorldMapSource.Width, DealOrForce.Simulation.WorldMapSource.Height,
                out var topology, out _))
            return "";

        // Taken before anything plays on it, which is the only moment it is the scenario's.
        topology.RememberAuthoredBorders();

        var first = Campaign.CreateBootstrap(20240909);
        first.AttachProvinceTopology(topology);

        // The baseline is day zero of a campaign, not the raw raster. Attaching legitimately
        // changes the map: the 2024 borders cut nine regions across two owners, and the
        // first thing any campaign does is split them. A second campaign must arrive at the
        // same day zero - not at the same raster.
        var authoredOwners = (ushort[])topology.ProvinceToNation.Clone();
        var authoredStates = (ushort[])topology.ProvinceToState.Clone();
        var spreadBefore = Spread(first, "RUS");
        if (spreadBefore <= 1)
            return "the first campaign put every Russian division in one province, so this " +
                   "check cannot tell a deployed world from an undeployed one";

        // Play it far enough to move a border and cut a region, which is what makes the
        // topology stop describing 2024.
        first.IsPaused = false;
        for (var day = 0; day < 400; day++) first.TickDay();

        var moved = 0;
        for (var province = 1; province < authoredOwners.Length; province++)
            if (topology.ProvinceToNation[province] != authoredOwners[province]) moved++;

        // Now the second campaign, given the same topology the way the game gives it.
        var second = Campaign.CreateBootstrap(20240909);
        if (!topology.RestoreAuthoredBorders())
            return "the topology had no authored borders to put back";
        second.AttachProvinceTopology(topology);

        if (second.ProvinceMap is null)
            return "the second campaign was never given a province map, so nothing in it " +
                   "can march, fight or hold a front";

        var spreadAfter = Spread(second, "RUS");
        if (spreadAfter <= 1)
            return $"the second campaign left every Russian division in one province " +
                   $"({spreadAfter} occupied) where the first spread them over {spreadBefore:N0}";
        if (spreadAfter < spreadBefore / 2)
            return $"the second campaign deployed into {spreadAfter:N0} province(s) against " +
                   $"the first campaign's {spreadBefore:N0}";

        // And it is the authored world, not the one the first campaign left behind.
        for (var province = 1; province < authoredOwners.Length; province++)
            if (topology.ProvinceToNation[province] != authoredOwners[province])
                return $"the second campaign opened with province {province} held by " +
                       $"{topology.ProvinceToNation[province]} rather than the day-zero " +
                       $"{authoredOwners[province]} - it inherited the first campaign's war";
        for (var province = 1; province < authoredStates.Length; province++)
            if (topology.ProvinceToState[province] != authoredStates[province])
                return $"the second campaign opened with province {province} in region " +
                       $"{topology.ProvinceToState[province]} rather than the day-zero " +
                       $"{authoredStates[province]} - it inherited the first campaign's split regions";

        Console.WriteLine($"  second campaign: the first moved {moved:N0} province(s) across the map " +
                          $"in 400 days; the second opened in the authored world and deployed Russia " +
                          $"over {spreadAfter:N0} province(s) rather than stacking it on one.");
        return "";
    }

    internal static string AudioIsDeclaredRatherThanWiredIn()
    {
        SoundCatalog.Load();
        MusicCatalog.Load();
        if (SoundCatalog.Count == 0) return "sound/ declares no cues at all";

        // The moments the interface raises by name.
        var raised = new[]
        {
            "war_declared", "peace_signed", "event_alert",
            "battle_won", "battle_lost", "ui_select"
        };
        var missing = raised.Where(name => !SoundCatalog.Has(name)).ToList();
        if (missing.Count > 0)
            return $"sound/ declares nothing for {string.Join(", ", missing)}";

        var root = GameDataLoader.DataFolder("sound");
        var projectRoot = root is null ? null : Directory.GetParent(root)?.Parent?.FullName;
        var problems = new List<string>();
        foreach (var cue in SoundCatalog.All)
        {
            // A cue routed to a channel nobody declares would play at full volume through
            // whatever player happened to exist, which is how a click becomes a klaxon.
            if (!SoundCatalog.AllChannels.Any(channel =>
                    string.Equals(channel.Name, cue.Channel, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{cue.Name} plays on undeclared channel '{cue.Channel}'");

            if (projectRoot is null || !cue.File.StartsWith("res://", StringComparison.Ordinal)) continue;
            var relative = cue.File.Substring("res://".Length).Replace('/', Path.DirectorySeparatorChar);
            if (!File.Exists(Path.Combine(projectRoot, relative)))
                problems.Add($"{cue.Name} -> {cue.File} is not there");
        }
        if (problems.Count > 0)
            return $"{problems.Count:N0} broken cue(s): {string.Join("; ", problems.Take(3))}";

        // A mod retiming one cue must keep everything else about it, because restating a
        // whole declaration to change one number is how a mod goes stale next patch.
        var before = SoundCatalog.Get("battle_won");
        SoundCatalog.Merge("{ \"cues\": { \"battle_won\": { \"cooldown_ms\": 250 } } }",
            "user://mods/example/sound", "test", null);
        var after = SoundCatalog.Get("battle_won");
        if (after.CooldownMs != 250) return "a mod could not retime a cue";
        if (after.File != before.File || after.Channel != before.Channel)
            return "retiming a cue lost the rest of its declaration";

        // And a mod's own audio resolves inside the mod's own sound folder - rooted there
        // rather than at the mod's root, the way gfx/ has always been. Before that, a path
        // in the base game's own cue file had to name `Assets/Audio/...` beside the source
        // code, and the sound of this game was the one kind of content a mod could not
        // reach without knowing where the executable had been built from.
        SoundCatalog.Merge("{ \"cues\": { \"battle_won\": \"audio/mine.wav\" } }",
            "user://mods/example/sound", "test", null);
        var replaced = SoundCatalog.Get("battle_won");
        if (replaced.File != "user://mods/example/sound/audio/mine.wav")
            return $"a mod's own sound resolved to '{replaced.File}' instead of inside its sound folder";
        if (replaced.CooldownMs != 250)
            return "replacing a cue's file discarded how the cue behaves";

        // Counted against what the game ships, which may be music of its own: a mod's menu track
        // is one more menu track and no more campaign ones.
        var menuBefore = MusicCatalog.For(MusicCatalog.Moment.Menu).Count;
        var campaignBefore = MusicCatalog.For(MusicCatalog.Moment.Campaign).Count;
        MusicCatalog.Merge("{ \"tracks\": { \"mod_example_theme\": { \"file\": \"audio/theme.ogg\", \"when\": \"menu\" } } }",
            "user://mods/example/music", "test", null);
        var menu = MusicCatalog.For(MusicCatalog.Moment.Menu);
        var campaign = MusicCatalog.For(MusicCatalog.Moment.Campaign);
        if (menu.Count != menuBefore + 1 || !menu.Any(track => track.File == "user://mods/example/music/audio/theme.ogg"))
            return "a mod could not add a track of its own";
        if (campaign.Count != campaignBefore)
            return "a menu track was offered to the campaign as well";

        SoundCatalog.Load();
        MusicCatalog.Load();
        Console.WriteLine($"  audio: {SoundCatalog.Count:N0} declared cue(s) across " +
                          $"{SoundCatalog.AllChannels.Count():N0} channel(s), every file present, " +
                          $"{MusicCatalog.Count:N0} shipped track(s) and a mod can add its own.");
        return "";
    }

    internal static string TheRegionsOfTheWorldAreData()
    {
        // Its own world rather than the shared sandbox: by the time this runs, earlier
        // checks have partitioned Gujarat to see what a treaty does to a national minority,
        // and a region invented at runtime is legitimately not in a file that ships.
        var campaign = Campaign.CreateBootstrap(20240301);
        var scripts = FindScripts();
        if (scripts is null) return "";
        var folder = Path.Combine(Path.GetDirectoryName(scripts)!, "game", "common", "states");
        if (!Directory.Exists(folder)) return "game/common/states does not exist";

        var files = Directory.GetFiles(folder, "*.json");
        if (files.Length == 0) return "game/common/states describes no regions at all";

        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(file), GameDataLoader.ForgivingJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            foreach (var entry in document.RootElement.EnumerateObject())
                if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    entry.Value.TryGetProperty("name", out var name) &&
                    name.ValueKind == System.Text.Json.JsonValueKind.String)
                    declared[entry.Name] = name.GetString() ?? "";
        }

        // Every region on the map is named by the file, under the id the game gives it.
        var missing = campaign.States.Where(state => !declared.ContainsKey(state.Id)).ToList();
        if (missing.Count > 0)
            return $"{missing.Count:N0} region(s) of {campaign.States.Count:N0} are not in " +
                   $"common/states, starting with {string.Join(", ", missing.Take(3).Select(state => $"{state.Id} ({state.Name})"))}";

        // And it names them what they are called, so the file is the world rather than a
        // description of an older one.
        var drifted = campaign.States
            .Where(state => !string.Equals(declared[state.Id], state.Name, StringComparison.Ordinal))
            .ToList();
        if (drifted.Count > 0)
            return $"{drifted.Count:N0} region(s) are called something else in common/states, " +
                   $"starting with {string.Join(", ", drifted.Take(3).Select(state => $"{state.Id}: file says {declared[state.Id]}, game says {state.Name}"))}";

        // One line, one region. A mod that renames Entre Ríos renames Entre Ríos.
        var subject = campaign.States.FirstOrDefault(state => state.Id.Length > 0);
        if (subject is null) return "the campaign has no regions to test with";
        var was = subject.Name;
        var wasKind = subject.Kind;
        var neighbours = campaign.States.Where(state => !ReferenceEquals(state, subject))
            .Take(64).Select(state => (state, state.Name)).ToList();
        try
        {
            var applied = StateDefinitions.Apply(campaign,
                "{ \"" + subject.Id + "\": { \"name\": \"Bench Region\", \"kind\": \"Bench\" } }");
            if (applied != 1) return $"a one-region file spoke for {applied} region(s)";
            if (subject.Name != "Bench Region" || subject.Kind != "Bench")
                return $"a mod could not rename a region: {subject.Id} is still {subject.Name}";
            var disturbed = neighbours.Where(pair => pair.state.Name != pair.Name).ToList();
            if (disturbed.Count > 0)
                return $"renaming one region also renamed {disturbed.Count:N0} other(s)";
        }
        finally
        {
            subject.Name = was;
            subject.Kind = wasKind;
        }

        Console.WriteLine($"  regions: {declared.Count:N0} named in common/states, " +
                          $"all {campaign.States.Count:N0} on the map among them.");
        return "";
    }

    internal static string TheOrderOfBattleIsAModsToRewrite(Campaign campaign)
    {
        var britain = campaign.NationByTag("GBR");
        if (britain is null) return "GBR is missing from the scenario";

        var shippedPersonnel = MilitaryBootstrap.Personnel("GBR", britain.Population);
        var shippedFleets = campaign.Fleets.Count(fleet => fleet.NationId == britain.Id);
        if (shippedFleets == 0) return "GBR ships without a navy, so the check proves nothing";

        var sandbox = Path.Combine(Path.GetTempPath(), "dof_bench_mod", "history", "units", "2024");
        try
        {
            Directory.CreateDirectory(sandbox);
            File.WriteAllText(Path.Combine(sandbox, "50_test.json"),
                "{ \"GBR\": 4242, \"fleets\": { \"GBR\": [ " +
                "{ \"name\": \"Bench Squadron\", \"hulls\": 3, " +
                "\"latitude\": 50.2, \"longitude\": -4.1 } ] } }");

            var root = Path.Combine(Path.GetTempPath(), "dof_bench_mod", "history", "units");
            MilitaryBootstrap.MergePersonnel(root, null);
            FleetHistory.ApplyDirectory(campaign, root, null);

            if (Math.Abs(MilitaryBootstrap.Personnel("GBR", britain.Population) - 4242f) > 0.5f)
                return "a mod could not change how many soldiers a country keeps";

            var navy = campaign.Fleets.Where(fleet => fleet.NationId == britain.Id).ToList();
            if (navy.Count != 1 || navy[0].Name != "Bench Squadron")
                return $"a mod's navy did not replace the shipped one: {navy.Count:N0} fleet(s) " +
                       $"named {string.Join(", ", navy.Take(3).Select(fleet => fleet.Name))}";
            if (navy[0].HomeProvinceId <= 0)
                return "a mod's fleet was given no home port";

            // Every other country keeps what it had: replacing a list means that country's
            // list, not the whole world's.
            if (campaign.Fleets.Count(fleet => fleet.NationId != britain.Id) == 0)
                return "one country's navy replaced everybody's";
        }
        finally
        {
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "dof_bench_mod"), true); }
            catch (Exception) { /* a leftover temp folder is not worth failing a run over */ }
            MilitaryBootstrap.ResetPersonnel();
            FleetHistory.Apply(campaign);
        }

        var restored = campaign.Fleets.Count(fleet => fleet.NationId == britain.Id);
        if (restored != shippedFleets)
            return $"the shipped navy did not come back: {restored:N0} of {shippedFleets:N0}";
        if (Math.Abs(MilitaryBootstrap.Personnel("GBR", britain.Population) - shippedPersonnel) > 0.5f)
            return "the shipped army did not come back";

        FontCatalog.Load();
        var shippedFaces = FontCatalog.Count;
        FontCatalog.Merge("{ \"fonts\": { \"ui.default\": \"fonts/mine.ttf\" } }",
            "user://mods/example", "test", null);
        var declared = FontCatalog.FileOf("ui.default");
        FontCatalog.Load();
        if (declared != "user://mods/example/fonts/mine.ttf")
            return $"a mod's typeface resolved to '{declared}' instead of inside the mod folder";

        Console.WriteLine($"  order of battle: a mod can replace one country's army and navy and " +
                          $"leave the rest alone; fonts ship {shippedFaces:N0} face(s) and a mod can add one.");
        return "";
    }

    /// <summary>
    /// Whether the worked examples in mods/_templates still describe this game.
    ///
    /// A template that has rotted is worse than no template at all: somebody follows the
    /// guide, the mod loader reports "unknown define", and they reasonably conclude the
    /// documentation is wrong rather than old. Every key in every template is checked
    /// against the thing it claims to name - a define against the properties on Defines, a
    /// good and a building against their enumerations - so that renaming any of them breaks
    /// the build of the documentation as loudly as it breaks the code.
    /// </summary>
    /// <summary>
    /// Whether the English table still indexes every caption the interface shows.
    ///
    /// The keys of a localization table here are the English text itself, so a lookup that
    /// misses falls through to the key and renders as its own English source. That is a good
    /// property - a half-finished translation reads as slightly odd rather than as an empty
    /// panel - and it had one bad consequence: the English table did not have to exist for
    /// the game to look right, so it did not. It was three rows long.
    ///
    /// What that cost was not cosmetic. `Loc.MissingKeys` exists to be a translator's to-do
    /// list, and the developer console's `loc &lt;language&gt;` command prints it. Both compared
    /// every language against those three rows, so both always answered that nothing was
    /// missing, however much was.
    ///
    /// So English is now the index of what there is to translate, and this check is what
    /// keeps it one. Every caption the source hands to a lookup has to be in it - which is
    /// how the next screen somebody writes gets indexed rather than quietly not.
    /// </summary>
    /// <summary>
    /// The budget moved out of the weekly tick and into `common/budget`, and did not change
    /// on the way.
    ///
    /// Two halves, and the first is the one that matters. Eight multiplications used to sit
    /// in EconomyCore; they are now eight rows in a file, and the expressions they replaced
    /// are written out here so that a row whose yield somebody fat-fingers is caught by the
    /// bench rather than by a player noticing that the treasury drains. Reproducing an old
    /// formula is the only proof that a refactor of a balanced economy was a refactor.
    ///
    /// The second half is the reason for the first: a mod can add a tax the game has never
    /// had, get a rate, an amount and a slider for it, and take it away again.
    /// </summary>
    /// <summary>
    /// One `revenues` or `expenses` group out of a template, judged line by line.
    ///
    /// Always answers true because it reports its own complaints in the loader's own words -
    /// a factor nobody has, a basis that is not one of the four - which are more use than
    /// "budget: revenues" appearing on a list of unknown keys.
    /// </summary>
    /// <summary>
    /// One cabinet or one trade stance out of a template, judged by its own catalogue.
    ///
    /// The entry is wrapped back into a one-row object because both catalogues validate a
    /// file's worth at a time, and a template is walked a row at a time. Nothing is kept:
    /// validation reads and reports, so the bench never inherits a mod's cabinet.
    /// </summary>
    internal static bool PolicyEntry(System.Text.Json.JsonProperty entry, string file,
        string templates, List<string> complaints,
        Action<System.Text.Json.JsonElement, List<string>> validate)
    {
        if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) return true;
        var wrapped = $"{{ {System.Text.Json.JsonSerializer.Serialize(entry.Name)}: {entry.Value.GetRawText()} }}";
        var problems = new List<string>();
        using (var document = System.Text.Json.JsonDocument.Parse(wrapped, GameDataLoader.ForgivingJson))
            validate(document.RootElement, problems);
        foreach (var problem in problems) complaints.Add($"{Short(templates, file)}: {problem}");
        return true;
    }

    internal static bool BudgetGroup(System.Text.Json.JsonProperty entry, string file,
        string templates, List<string> complaints)
    {
        var name = entry.Name.ToLowerInvariant();
        if (name is not ("revenues" or "revenue" or "expenses" or "expense"))
        {
            complaints.Add($"{Short(templates, file)}: `{entry.Name}` is neither revenues nor expenses");
            return true;
        }
        if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) return true;

        var problems = new List<string>();
        foreach (var line in entry.Value.EnumerateObject())
            if (line.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                BudgetCore.ValidateLine(line.Name, line.Value, problems);
        foreach (var problem in problems) complaints.Add($"{Short(templates, file)}: {problem}");
        return true;
    }

    internal static string TheBudgetIsData(Campaign world)
    {
        var nation = world?.Nations?.FirstOrDefault(candidate => candidate.Gdp > 0f);
        if (nation is null) return "no country with any output to price a budget against";

        var output = nation.Gdp / 52f;
        const float tolerance = 1e-3f;

        string Compare(string id, float expected)
        {
            var line = BudgetCore.Find(id);
            if (line is null) return $"the budget has no line called `{id}`";
            var actual = BudgetCore.Amount(nation, line, output, atWar: false);
            var slack = MathF.Max(tolerance, MathF.Abs(expected) * 1e-4f);
            return MathF.Abs(actual - expected) <= slack
                ? ""
                : $"`{id}` is worth {actual:0.####} where the formula it replaced gives {expected:0.####}";
        }

        // The eight expressions as they were written before any of this was data.
        var shipped = new[]
        {
            // And what the country's government and spirits make of its collection (tax_efficiency),
            // which the old formula had no word for.
            Compare("income_tax", output * nation.TaxRate * 0.24f * (0.72f + nation.Stability * 0.22f) *
                NationalModifiers.Factor(nation, "tax_efficiency")),
            Compare("tariffs", nation.WeeklyTariffIncome),
            // A surplus whole and a tenth of a deficit: a country's trade deficit is its
            // importers' bill, and only the fuel and food subsidies are the state's.
            Compare("trade", nation.WeeklyTradeBalance >= 0f
                ? nation.WeeklyTradeBalance
                : nation.WeeklyTradeBalance * 0.10f),
            Compare("administration", output * 0.030f),
            Compare("armed_forces", output * nation.MilitarySpending * 0.22f),
            Compare("welfare", output * nation.WelfareSpending * 0.18f),
            Compare("education", output * nation.EducationSpending * 0.14f),
            Compare("policing", output * (1f - nation.Stability) * 0.025f)
        };
        foreach (var problem in shipped)
            if (problem.Length > 0) return problem;

        // Nobody is evading anything at the 2024 start, which is what lets the eight above
        // still reproduce their old formulas exactly. If a scenario ever raises a country's
        // taxes past what it tolerates, that country's budget changes and this says so
        // rather than leaving it to be discovered as a drifting calibration.
        //
        // Against a campaign of its own, deliberately. The world handed in here has been
        // running, and a running world has an AI in it that raises and lowers taxes: Bir
        // Tawil sits at 46 % after a while and evades a little, which is the Laffer curve
        // working rather than the shipped budget drifting. What this is for is the shipped
        // state, so it asks the shipped state.
        var start = Campaign.CreateBootstrap(20240101);
        foreach (var country in start.Nations)
        {
            if (country.Gdp <= 0f) continue;
            var compliance = BudgetCore.ComplianceAt(country, country.Gdp / 52f, atWar: false);
            if (compliance < 0.99999f)
                return $"{country.Tag} (stability {country.Stability:P1}, tax {country.TaxRate:P1}, " +
                       $"gdp {country.Gdp:0.###}B) collects only {compliance:P1} of what it levies on day zero, " +
                       "so the shipped budget is not the shipped budget any more";
        }

        // And the thing the levies were changed for: six taxes on one economy are not six
        // times one tax. Each is charged against the same week's output, so asking all of
        // them for everything at once has to collect less than the six asked for separately.
        var levies = BudgetCore.PlayerLines.Where(line => line.Side == BudgetSide.Revenue).ToList();
        if (levies.Count < 2) return "there is only one levy, so nothing can share a base with it";
        var kept = levies.ToDictionary(line => line.Id, line => BudgetCore.RateOf(nation, line));
        float naive = 0f;
        foreach (var line in levies)
        {
            foreach (var other in levies) BudgetCore.SetRate(nation, other, other.Id == line.Id ? line.Maximum : 0f);
            naive += BudgetCore.Amount(nation, line, output, atWar: false);
        }
        foreach (var line in levies) BudgetCore.SetRate(nation, line, line.Maximum);
        var together = levies.Sum(line => BudgetCore.Amount(nation, line, output, atWar: false));
        foreach (var line in levies) BudgetCore.SetRate(nation, line, kept[line.Id]);

        if (together >= naive * 0.75f)
            return $"every levy at its maximum collects {together:0.##} against {naive:0.##} levied one at a " +
                   "time, which is close enough to additive that they are not sharing a tax base";
        var squeeze = naive <= 0f ? 1f : together / naive;

        // Every line adds up to the balance the country is actually paid.
        var outcome = BudgetCore.Evaluate(nation, output, atWar: false);
        float revenue = 0f, expenses = 0f;
        foreach (var line in BudgetCore.All)
        {
            var amount = BudgetCore.Amount(nation, line, output, atWar: false);
            if (line.Side == BudgetSide.Revenue) revenue += amount; else expenses += amount;
        }
        var expectedBalance = revenue - outcome.IncomePenalty - expenses;
        if (MathF.Abs(outcome.Balance - expectedBalance) > MathF.Max(tolerance, MathF.Abs(revenue) * 1e-4f))
            return $"the itemised lines come to {expectedBalance:0.##} (gross {revenue:0.##} - income penalty {outcome.IncomePenalty:0.##} - expenses {expenses:0.##}) and the week's balance is {outcome.Balance:0.##}";

        // And a tax the game has never had.
        var before = BudgetCore.All.Count;
        var errors = new List<string>();
        BudgetCore.Merge("""
        {
          "revenues": {
            "bench_wealth_tax": {
              "name": "Wealth tax",
              "yield": 0.40,
              "scale": { "constant": 1.0 },
              "rate": { "default": 0.10, "min": 0.0, "max": 0.50, "neutral": 0.0, "player": true },
              "effects": { "growth": -0.09 }
            }
          }
        }
        """, errors);
        if (errors.Count > 0) return $"a sound mod line was rejected: {errors[0]}";

        var added = BudgetCore.Find("bench_wealth_tax");
        if (added is null) return "a mod added a budget line and the catalogue did not keep it";
        if (BudgetCore.All.Count != before + 1)
            return $"adding one line took the catalogue from {before} to {BudgetCore.All.Count}";
        if (!BudgetCore.PlayerLines.Any(line => line.Id == "bench_wealth_tax"))
            return "the mod's line is not offered to the government that would set it";

        var modded = BudgetCore.Amount(nation, added, output, atWar: false);
        // Its own numbers, and then what the country is actually willing to hand over: a
        // seventh levy lands on the same economy as the other six.
        var expectedModded = output * 0.10f * 0.40f *
                             BudgetCore.ComplianceAt(nation, output, atWar: false) *
                             NationalModifiers.Factor(nation, "tax_efficiency");
        if (MathF.Abs(modded - expectedModded) > MathF.Max(tolerance, expectedModded * 1e-4f))
            return $"the mod's tax is worth {modded:0.##} where its own numbers say {expectedModded:0.##}";

        BudgetCore.SetRate(nation, added, 0.25f);
        if (MathF.Abs(BudgetCore.RateOf(nation, added) - 0.25f) > 1e-5f)
            return "a rate set on a mod's line did not stick";
        nation.PolicyRates.Remove("bench_wealth_tax");

        // Put the catalogue back, so nothing downstream inherits a tax from the bench.
        BudgetCore.ResetCatalog();
        if (BudgetCore.Find("bench_wealth_tax") is not null)
            return "resetting the catalogue left the bench's tax behind";
        if (BudgetCore.All.Count != before)
            return $"the catalogue came back with {BudgetCore.All.Count} line(s) instead of {before}";

        var player = BudgetCore.PlayerLines.Count();
        Console.WriteLine($"  budget: {before} line(s) - {BudgetCore.All.Count(line => line.Side == BudgetSide.Revenue)} " +
                          $"raising, {BudgetCore.All.Count(line => line.Side == BudgetSide.Expense)} spending, " +
                          $"{player} of them a government's to set. The eight the game shipped with " +
                          "reproduce their old formulas exactly, and every levy at once collects " +
                          $"{squeeze:P0} of what the same rates would raise one at a time.");
        return "";
    }

    /// <summary>
    /// The five cabinets and three trade stances are rows, and a sixth and a fourth are rows
    /// somebody else may write.
    ///
    /// Both are enums underneath, because a country carries one of each in every save ever
    /// written; what makes a mod's entry work anyway is that nothing switches on the enum any
    /// more - the shipped effects come out of the catalogue exactly as a mod's do. This is
    /// the check that would fail if somebody put a switch back.
    /// </summary>
    internal static string PolicyCataloguesTakeNewEntries()
    {
        var shippedFocuses = GovernmentFocusCatalog.All.Count;
        var shippedStances = TradePolicyCatalog.All.Count;
        if (shippedFocuses < 5) return $"only {shippedFocuses} cabinet(s) in common/government_focus";
        if (shippedStances < 3) return $"only {shippedStances} stance(s) in common/trade_policies";

        // What the shipped five still are, so a retune that slips is caught here.
        var science = GovernmentFocusCatalog.Of(GovernmentFocus.EducationAndScience);
        if (MathF.Abs(science.Literacy - 0.00008f) > 1e-9f ||
            MathF.Abs(science.ProductivityTarget - 0.04f) > 1e-6f)
            return $"education & science is {science.Literacy} literacy and " +
                   $"{science.ProductivityTarget} productivity where the code it replaced gave 8e-5 and 0.04";
        if (MathF.Abs(TradePolicyRules.TariffRate(TradePolicy.StrategicAutarky) - 0.14f) > 1e-6f)
            return "autarky's tariff is not the 14% the code it replaced charged";

        var errors = new List<string>();
        GovernmentFocusCatalog.Merge("""
        {
          "bench_war_economy": {
            "name": "Bench war economy",
            "short_name": "Bench",
            "description": "A cabinet that exists for as long as this check runs.",
            "effects": { "growth": 0.009, "war_support": 0.0002, "radicalism": 0.0003 }
          }
        }
        """, errors);
        TradePolicyCatalog.Merge("""
        {
          "bench_blockade_running": {
            "name": "Bench blockade running",
            "description": "A stance that exists for as long as this check runs.",
            "import_access": 0.71, "export_access": 0.66, "tariff": 0.23
          }
        }
        """, errors);
        if (errors.Count > 0) return $"a sound entry was rejected: {errors[0]}";

        if (GovernmentFocusCatalog.All.Count != shippedFocuses + 1)
            return $"adding one cabinet took the catalogue to {GovernmentFocusCatalog.All.Count}";
        if (TradePolicyCatalog.All.Count != shippedStances + 1)
            return $"adding one stance took the catalogue to {TradePolicyCatalog.All.Count}";

        var added = GovernmentFocusCatalog.All[^1];
        if (!GovernmentFocusCatalog.Knows(added))
            return "the catalogue lists a cabinet it does not know";
        if ((int)added < shippedFocuses)
            return $"the new cabinet took slot {(int)added}, which one the game ships already has";
        if (MathF.Abs(GovernmentFocusRules.AnnualGrowth(added) - 0.009f) > 1e-6f)
            return "a mod's cabinet does not reach the growth the weekly tick reads";

        var stance = TradePolicyCatalog.All[^1];
        if (MathF.Abs(TradePolicyRules.TariffRate(stance) - 0.23f) > 1e-6f ||
            MathF.Abs(TradePolicyRules.ImportAccess(stance) - 0.71f) > 1e-6f)
            return "a mod's trade stance does not reach the market";

        GovernmentFocusCatalog.ResetCatalog();
        TradePolicyCatalog.ResetCatalog();
        if (GovernmentFocusCatalog.All.Count != shippedFocuses ||
            TradePolicyCatalog.All.Count != shippedStances)
            return "resetting the catalogues left the bench's entries behind";

        Console.WriteLine($"  policy: {shippedFocuses} cabinet(s) and {shippedStances} trade stance(s) " +
                          $"as data, with room for {GovernmentFocusCatalog.ModdedHeadroom} and " +
                          $"{TradePolicyCatalog.ModdedHeadroom} more; a mod's cabinet reaches the weekly tick.");
        return "";
    }

    internal static string TheEnglishTableIsAnIndex()
    {
        var scripts = FindScripts();
        if (scripts is null) return "";
        var english = ReadLocalizationTable("english");
        if (english is null) return "game/localization/english has no table";
        if (english.Count < 2) return $"the English table has {english.Count} row(s), so it indexes nothing";

        // Only the captions a reader of the source can be sure about: a literal handed
        // straight to one of the three lookups. A caption that arrives through a parameter,
        // or is assembled from a country's name and a number, cannot be found this way and
        // is not expected to be - which is why this check is one-directional.
        var captions = VisibleCaptionIndex(scripts);
        if (captions.Count == 0) return "found no interface captions in the source at all";

        // And the captions that are not in the source at all any more.
        //
        // The budget's seventeen lines, the cabinets and the trade stances name and explain
        // themselves in `common/`, which is the point of them being data - but it moved
        // those words out of reach of a scan that only reads C#. A name a file gives
        // something the player reads is a key exactly as much as a name the code gives it,
        // and this is what keeps that true as more of the game becomes data.
        var declared = new SortedSet<string>(StringComparer.Ordinal);
        var game = Path.Combine(Path.GetDirectoryName(scripts)!, "game");

        // Every folder that names something a player reads. This used to be six of them,
        // which is how 2,260 strings accumulated in the data with nobody translating them:
        // a check that covers a third of the content is a check that reports a third of the
        // problem and reads like it reported all of it.
        //
        // `common/states` is the one exception and it is deliberate. There are 1,730 region
        // names, they are proper nouns, and translating them is a translator's job rather
        // than an afternoon's - `ScenarioState.DisplayName` looks them up so the rows work
        // the day somebody writes them, and until then they read as authored.
        var folders = new[]
        {
            "common/budget", "common/government_focus", "common/trade_policies",
            "common/governments", "common/ideologies", "common/countries", "common/units",
            "common/settings", "common/goods", "common/buildings", "common/terrain",
            "common/professions", "common/religions", "common/cultures", "common/decisions",
            "common/modifiers", "common/opinion_modifiers", "common/trade", "common/support",
            "common/programme", "common/laws", "common/agendas", "common/national_courses",
            "common/alliance_programs", "common/formable_nations", "common/movements", "events"
        };
        foreach (var folder in folders)
        {
            var directory = Path.Combine(game, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                using var document = System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(file), GameDataLoader.ForgivingJson);
                CollectDeclaredNames(document.RootElement, declared);
            }
        }

        var absent = new List<string>();
        foreach (var caption in captions)
            if (!english.ContainsKey(caption)) absent.Add(caption);
        foreach (var caption in declared)
            if (!english.ContainsKey(caption)) absent.Add(caption);
        if (absent.Count > 0)
            return $"{absent.Count} caption(s) the interface shows are missing from the English " +
                   $"table, starting with: {string.Join(" | ", absent)}";

        var report = new List<string>();
        foreach (var language in Directory.GetDirectories(
                     Path.Combine(Path.GetDirectoryName(scripts)!, "game", "localization")))
        {
            var name = Path.GetFileName(language);
            if (string.Equals(name, "english", StringComparison.OrdinalIgnoreCase)) continue;
            var table = ReadLocalizationTable(name);
            if (table is null) continue;
            // `language.name` is every table's own name for its own language, so it is not
            // one of the strings there is to translate and belongs in neither half of this.
            var translated = 0;
            foreach (var key in english.Keys)
                if (key != "language.name" && table.ContainsKey(key)) translated++;
            var orphans = 0;
            foreach (var key in table.Keys) if (!english.ContainsKey(key)) orphans++;
            report.Add($"{name} {translated}/{english.Count - 1} ({orphans} row(s) naming nothing indexed)");
        }

        Console.WriteLine($"  localization: the English table indexes {english.Count - 1:N0} string(s), " +
                          $"{captions.Count:N0} of them found in the source by name" +
                          (report.Count == 0 ? "." : $"; {string.Join(", ", report)}."));
        return "";
    }

    /// <summary>
    /// Which arguments of which calls end up in front of a player, translated.
    ///
    /// It used to be three, all of them taking their caption first, and that was enough
    /// while the interface was three lookups deep. It is not: `DetailRow` builds every
    /// caption/value row in the game and reaches `Tr` through `Title`, and so do
    /// `DetailHeading`, `RowLabel`, `SliderRow` and `StateSection`. Every one of those
    /// captions was translated at runtime and absent from the English table, which is the
    /// worst of both - the machinery worked and no translator could see there was anything
    /// to translate. `StateSection` is also why this now counts arguments rather than
    /// assuming the first: its first argument is the container, and both of the others are
    /// text.
    /// </summary>
    internal static readonly (string Call, int[] Arguments)[] TranslatedCalls =
    {
        ("Title(", new[] { 0 }),
        ("ActionButton(", new[] { 0 }),
        ("Tr(", new[] { 0 }),
        ("DetailRow(", new[] { 0 }),
        ("DetailHeading(", new[] { 0 }),
        ("RowLabel(", new[] { 0 }),
        ("SliderRow(", new[] { 0 }),
        ("StateSection(", new[] { 1, 2 }),
        ("PolicyHeading(", new[] { 1 }),
        ("PolicyText(", new[] { 1 }),
        ("PolicyStat(", new[] { 1 }),
        ("CrisisGauge(", new[] { 1, 4 }),
        ("Placeholder(", new[] { 1 }),
        ("Loc.Get(", new[] { 0 }),
        ("Loc.Format(", new[] { 0 }),
        // A sentence that counts hands its singular key to Plural.Key and gets back one of
        // three rows. The singular is still a literal a translator has to be shown, so it is
        // scanned here; the "|few" and "|many" rows beside it are assembled at runtime and
        // are the one place the table is allowed to hold a key the source never spells.
        ("Plural.Key(", new[] { 0 }),
        // The menu scene is not part of the campaign's widget helpers - it is its own scene
        // with two of its own - so its captions are named here or they are named nowhere.
        ("MenuCaption(", new[] { 0 }),
        ("MenuAction(", new[] { 0 }),
        // And the local helpers a screen builds for itself: the war ledger's `Heading`, `Line`,
        // `Caption` and `Cell`, the layout editor's `Caption` and `Button`. Each reaches `Tr`
        // or `Loc.Get` one call down, so each caption was translated at runtime and invisible
        // here - which is how the war screen's headings shipped in English to Russian players,
        // missing from both tables, while this check reported the tables complete.
        ("Heading(", new[] { 0 }),
        ("Line(", new[] { 0 }),
        ("Caption(", new[] { 0 }),
        ("Cell(", new[] { 0 }),
        ("Button(", new[] { 0 })
    };

    // Reused by CI and the opt-in translator index generator. Expanding a workspace
    // must expand its localization coverage too; empty descriptions are not keys.
    internal static SortedSet<string> VisibleCaptionIndex(string scripts)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ProductionSourceFiles(scripts))
        {
            var source = File.ReadAllText(file);
            foreach (var caption in Captions(source))
                if (caption.Length > 0) result.Add(caption);
            // Event captions are addressed indirectly by recorded kind, including
            // both branches of a ternary. They must not leak raw chronicle.* IDs.
            foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(source,
                         @"Chronicle\.Record\([^,]+,\s*([^,]+),"))
            {
                // A ternary's condition is not a kind: `kind == "shut" ? "a" : "b"`
                // records "a" or "b", never "shut". Only the branches are captions.
                var expression = call.Groups[1].Value;
                var branch = expression.IndexOf('?');
                if (branch >= 0) expression = expression[(branch + 1)..];
                foreach (System.Text.RegularExpressions.Match kind in System.Text.RegularExpressions.Regex.Matches(
                             expression, "\"([a-z_]+)\""))
                {
                    var id = kind.Groups[1].Value;
                    // A kind assembled at run time - "covert_" + operation + "_done" - hands the
                    // harvester its fragments, and a fragment is not a caption. The assembled
                    // keys are what the tables actually hold, and demanding a row for "_done"
                    // would mean teaching the tables to translate half a word.
                    if (id.StartsWith("_", StringComparison.Ordinal) || id.EndsWith("_", StringComparison.Ordinal)) continue;
                    result.Add("chronicle." + id);
                }
            }
        }
        var common = Path.Combine(Path.GetDirectoryName(scripts)!, "game", "common");
        foreach (var folder in new[] { "budget", "government_focus", "trade_policies", "governments", "ideologies", "countries",
                     "units", "settings", "goods", "buildings", "terrain", "professions", "religions", "cultures", "decisions",
                     "modifiers", "opinion_modifiers", "trade", "support", "programme", "laws", "agendas", "national_courses",
                     "alliance_programs", "formable_nations", "movements" })
        {
            var directory = Path.Combine(common, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                CollectDeclaredNames(document.RootElement, result);
            }
        }
        return result;
    }

    // A local agent's nested checkout is not game source. Skip it before descent,
    // rather than scanning its old game/tests and then filtering thousands of files.
    internal static IEnumerable<string> ProductionSourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || name is "bin" or "obj" or "build" ||
                (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in ProductionSourceFiles(child)) yield return file;
        }
    }

    /// <summary>
    /// Every `name`, `short_name` and `description` a data file gives something, at any
    /// depth, because the layouts differ - the budget nests its lines under `revenues` and
    /// `expenses`, the cabinets sit at the top level - and what matters is the words.
    /// </summary>
    internal static void CollectDeclaredNames(System.Text.Json.JsonElement element, SortedSet<string> into)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var field in element.EnumerateObject())
                {
                    if (field.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        field.Name is "name" or "short_name" or "description")
                    {
                        var text = field.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) into.Add(text);
                        continue;
                    }
                    // `reads` is a small object of sentences rather than one named string -
                    // what a trade control is doing at rest, at nothing, at everything and
                    // anywhere between - and every one of them is prose a player reads.
                    if (field.Name == "reads" &&
                        field.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var sentence in field.Value.EnumerateObject())
                            if (sentence.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(sentence.Value.GetString()))
                                into.Add(sentence.Value.GetString());
                        continue;
                    }
                    CollectDeclaredNames(field.Value, into);
                }
                return;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectDeclaredNames(item, into);
                return;
        }
    }

    /// <summary>Every literal this file hands to a translation lookup, however indirectly.</summary>
    internal static IEnumerable<string> Captions(string source)
    {
        foreach (var (call, wanted) in TranslatedCalls)
        {
            var at = 0;
            while ((at = source.IndexOf(call, at, StringComparison.Ordinal)) >= 0)
            {
                // `SomethingElseTr(` is not `Tr(`. A call name has to start on a boundary.
                var before = at > 0 ? source[at - 1] : ' ';
                at += call.Length;
                if (char.IsLetterOrDigit(before) || before == '_') continue;

                // `new Line(...)` constructs a record whose first member is a routing id
                // (StrategicHudBrief.Line), not a caption; only the bare `Line(...)`
                // helper takes one.
                if (call == "Line(")
                {
                    var head = source.Substring(0, at - call.Length).TrimEnd();
                    if (head.EndsWith("new", StringComparison.Ordinal) &&
                        (head.Length == 3 ||
                         !(char.IsLetterOrDigit(head[head.Length - 4]) || head[head.Length - 4] == '_')))
                        continue;
                }

                // ScenarioMapEditor has ActionButton(nodeName, caption, action), while
                // campaign widgets use ActionButton(caption, action, ...). Two literal
                // strings identify the named-node overload: index its caption, not its ID.
                if (call == "ActionButton(" && Argument(source, at, 0) is not null &&
                    Argument(source, at, 1) is string namedCaption)
                {
                    yield return namedCaption;
                    continue;
                }
                foreach (var index in wanted)
                {
                    var argument = Argument(source, at, index);
                    if (argument is not null) yield return argument;
                }
            }
        }
    }

    /// <summary>
    /// The Nth argument of a call whose arguments start at `open`, if that argument is one
    /// plain string literal and nothing else.
    ///
    /// Anything assembled - interpolated, concatenated, handed in through a variable -
    /// returns null, which is the honest answer: a reader of the source cannot know what it
    /// will say, and the English table is not expected to carry it.
    /// </summary>
    internal static string Argument(string source, int open, int index)
    {
        var depth = 0;
        var argument = 0;
        var cursor = open;
        var start = open;
        while (cursor < source.Length)
        {
            var character = source[cursor];
            if (character == '"')
            {
                cursor = SkipString(source, cursor);
                continue;
            }
            if (character is '(' or '[' or '{') depth++;
            else if (character is ')' or ']' or '}')
            {
                if (depth == 0 && character == ')')
                    return argument == index ? Literal(source, start, cursor) : null;
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                if (argument == index) return Literal(source, start, cursor);
                argument++;
                start = cursor + 1;
            }
            cursor++;
        }
        return null;
    }

    /// <summary>
    /// The text of `source[from..to]` when it is string literals and nothing else.
    ///
    /// A sentence too long for one line is written as `"half " + "the rest"`, and what the
    /// player reads is the join, so the join is the key. Anything else in the expression -
    /// a variable, a call, an interpolation - and there is no key a reader of the source
    /// can be sure of, so this answers null and the caption is not expected in the table.
    /// </summary>
    internal static string Literal(string source, int from, int to)
    {
        var span = source[from..to].Trim();
        if (span.Length < 2 || span[0] != '"') return null;
        var text = new System.Text.StringBuilder();
        var cursor = 0;
        while (true)
        {
            if (cursor >= span.Length || span[cursor] != '"') return null;
            cursor++;
            var closed = false;
            while (cursor < span.Length)
            {
                var character = span[cursor++];
                if (character == '\\' && cursor < span.Length)
                {
                    var escaped = span[cursor++];
                    text.Append(escaped switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => escaped });
                    continue;
                }
                if (character == '"') { closed = true; break; }
                text.Append(character);
            }
            if (!closed) return null;

            while (cursor < span.Length && char.IsWhiteSpace(span[cursor])) cursor++;
            if (cursor >= span.Length) return text.ToString();
            if (span[cursor] != '+') return null;
            cursor++;
            while (cursor < span.Length && char.IsWhiteSpace(span[cursor])) cursor++;
        }
    }

    internal static int SkipString(string source, int quote)
    {
        var cursor = quote + 1;
        while (cursor < source.Length)
        {
            if (source[cursor] == '\\') { cursor += 2; continue; }
            if (source[cursor] == '"') return cursor + 1;
            cursor++;
        }
        return cursor;
    }

    /// <summary>One language's table, read the layered way the game reads it.</summary>
    internal static Dictionary<string, string> ReadLocalizationTable(string language)
    {
        var scripts = FindScripts();
        if (scripts is null) return null;
        var folder = Path.Combine(Path.GetDirectoryName(scripts)!, "game", "localization", language);
        if (!Directory.Exists(folder)) return null;

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            // A half-written file from a parallel edit used to crash the audit with a stack
            // trace and no name; now the audit says which file to look at.
            System.Text.Json.JsonDocument document;
            try
            {
                document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file),
                    new System.Text.Json.JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip
                    });
            }
            catch (System.Text.Json.JsonException error)
            {
                Console.WriteLine($"  unreadable {language} localization file: {Path.GetFileName(file)} — {error.Message}");
                continue;
            }
            using (document)
            {
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                    if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        table[entry.Name] = entry.Value.GetString() ?? "";
            }
        }
        return table;
    }

    /// <summary>The interface source, found the same way the mod templates are.</summary>
    internal static string FindScripts()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var scripts = Path.Combine(directory.FullName, "Scripts");
            if (Directory.Exists(scripts) &&
                Directory.Exists(Path.Combine(directory.FullName, "game", "localization")))
                return scripts;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>
    /// Whether a twenty-first good is really possible, or only nearly.
    ///
    /// The goods and the buildings are enums, which sounds like a hard limit and is not: an
    /// enum in C# is an int with names for some of its values, and `(Commodity)20` is a
    /// perfectly good dictionary key, field and number in a save. What used to stop a mod was
    /// that every road into the catalogs went through `Enum.TryParse`, which knows only the
    /// names that were compiled. The catalogs keep their own name tables now.
    ///
    /// This declares a good and a building that exist nowhere in the game, gives the building
    /// a recipe that makes the good, and then insists on the whole chain: that a country ends
    /// up with a market quote for it, that a region can hold the building, that production
    /// actually happens, and - the part that is easy to get wrong - that a save written with
    /// them comes back with them, on the right numbers.
    ///
    /// That last one is why the save carries a name-to-number table. A modded good is handed
    /// the next free number, so which number it gets depends on the order the mods were read;
    /// without the table, the same save opened with the same mods in a different order would
    /// quietly find one good where another had been. The check simulates exactly that by
    /// moving the good before restoring.
    /// </summary>
    internal static string AModCanAddAGood(Campaign world)
    {
        const string GoodId = "bench_alloy";
        const string BuildingId = "bench_works";
        try
        {
            if (!CommodityCatalog.Override(GoodId, "Bench alloy", "◆", 0.06f, 0.25f, "A good no enum names."))
                return "the catalog refused a complete new good";
            if (!CommodityCatalog.TryParse(GoodId, out var alloy))
                return "the catalog accepted a new good and then could not find it by name";
            if (!BuildingCatalog.Declare(BuildingId, "Bench works", 2.0f, 200, 0.1f, "A building no enum names."))
                return "the catalog refused a complete new building";
            if (!BuildingCatalog.TryParse(BuildingId, out var works))
                return "the catalog accepted a new building and then could not find it by name";
            if ((int)alloy < 20 || (int)works < 17)
                return $"the new good and building took numbers {(int)alloy} and {(int)works}, " +
                       "which are numbers the game already uses";

            var problems = new List<string>();
            ProductionChains.Merge($@"{{
                ""bench_alloy_works"": {{
                    ""good"": ""{GoodId}"",
                    ""output"": {{ ""{BuildingId}"": 3.0, ""CivilianIndustry"": 0.4 }},
                    ""demand"": {{ ""population"": 0.05 }}
                }} }}", problems);
            if (problems.Count > 0)
                return $"a recipe naming the new good and building was rejected: {problems[0]}";

            // On a copy of the world rather than on the world: this check plants buildings
            // and ticks days, and every check after it would inherit that.
            var seed = SaveSystem.ExportNetworkSnapshot(world, "bench");
            if (seed is null || seed.Length == 0) return "the world would not export to copy";
            var campaign = SaveSystem.ImportNetworkSnapshot(seed, "bench").Campaign;
            if (campaign is null) return "the copy of the world would not load";
            campaign.AttachProvinceTopology(world.ProvinceMap);
            var nation = campaign.Nations.FirstOrDefault(candidate => candidate.Population > 0);
            if (nation is null) return "the scenario made no country to test with";
            var region = campaign.States.FirstOrDefault(state => state.NationMapIndex == nation.MapIndex);
            if (region is null) return $"{nation.Tag} holds no region";
            region.Buildings[works] = 6;

            for (var day = 0; day < 30; day++) campaign.TickDay();

            if (!nation.Market.TryGetValue(alloy, out var quote))
                return $"{nation.Tag} has no market quote for a good the catalog knows about";
            if (quote.Production <= 0f)
                return $"six of the new building produced {quote.Production:F3} of the new good - " +
                       "the recipe reached the market pass but made nothing";

            // Now move it, the way a different mod order would, and make sure the save
            // brings it back where it belongs rather than where its number pointed.
            var bytes = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
            if (bytes is null || bytes.Length == 0) return "the campaign would not export";
            var beforeMove = (int)alloy;
            CommodityCatalog.ResetCatalog();
            CommodityCatalog.Override("bench_spacer", "Spacer", "·", 0.01f, 0f, "Takes the number the alloy had.");
            CommodityCatalog.Override(GoodId, "Bench alloy", "◆", 0.06f, 0.25f, "A good no enum names.");
            if (!CommodityCatalog.TryParse(GoodId, out var moved) || (int)moved == beforeMove)
                return "the test could not move the good, so it proves nothing about moving it";

            var restored = SaveSystem.ImportNetworkSnapshot(bytes, "bench").Campaign;
            if (restored is null) return "the campaign with a modded good would not load back";
            var restoredNation = restored.Nations.FirstOrDefault(candidate => candidate.Id == nation.Id);
            if (restoredNation is null) return "the country did not come back";
            if (!restoredNation.Market.TryGetValue(moved, out var back))
                return $"the new good was saved as {beforeMove} and the catalog now calls it " +
                       $"{(int)moved}, and the save did not follow it";
            if (Math.Abs(back.Production - quote.Production) > 0.001f)
                return $"the good came back on the right number with the wrong figure: " +
                       $"{back.Production:F3} against {quote.Production:F3}";

            Console.WriteLine($"  moddability: a good and a building the enums never named took numbers " +
                              $"{beforeMove} and {(int)works}, were built and produced, survived a save, and " +
                              $"followed their own name to {(int)moved} when the numbering changed under them.");
            return "";
        }
        finally
        {
            // Whatever happened, the next check gets the game's own catalogs back.
            CommodityCatalog.ResetCatalog();
            BuildingCatalog.ResetCatalog();
            ProductionChains.ResetCatalog();
        }
    }

    /// <summary>
    /// Whether the shipped production chains describe a world that works.
    ///
    /// They are data now, which is the point and also the risk: a factor spelled
    /// `standard_of_life` instead of `standard_of_living`, or a good called `Aluminum`, used
    /// to be a compiler error and is now a line in a file that quietly makes nothing. The
    /// symptom would be a world that opens in a famine of one good, several hours of play
    /// away from the typo that caused it.
    ///
    /// So the loader is asked to report, and every complaint is a failure here. Then two
    /// things every good needs: somebody who makes it, and somebody who wants it. A good
    /// nobody makes can only be imported from a world that has none either; a good nobody
    /// wants piles up forever and is worth nothing to anybody, which is a strange thing for
    /// a game about trade to ship.
    /// </summary>
    internal static string TheChainsHangTogether()
    {
        var complaints = new List<string>();
        ProductionChains.ResetCatalog(complaints);
        if (complaints.Count > 0)
            return $"common/production has {complaints.Count} problem(s): {string.Join("; ", complaints.Take(3))}";
        if (ProductionChains.RecipeCount == 0) return "common/production describes no recipes at all";

        var made = new HashSet<Commodity>(ProductionChains.GoodsProduced());
        var eaten = new HashSet<Commodity>(ProductionChains.GoodsConsumed());
        var unmade = new List<string>();
        var unwanted = new List<string>();
        foreach (var commodity in CommodityCatalog.All)
        {
            if (!made.Contains(commodity)) unmade.Add(CommodityCatalog.Get(commodity).Name);
            if (!eaten.Contains(commodity)) unwanted.Add(CommodityCatalog.Get(commodity).Name);
        }
        if (unmade.Count > 0)
            return $"nothing in the world makes {string.Join(", ", unmade)} - it can only be " +
                   "imported from countries that have none either";
        if (unwanted.Count > 0)
            return $"nothing in the world wants {string.Join(", ", unwanted)}, so it piles up " +
                   "forever and is worth nothing to anybody";

        Console.WriteLine($"  production: {ProductionChains.RecipeCount} recipe(s) in common/production " +
                          $"make and consume all {made.Count} good(s), and a mod can retune any of them by name.");
        return "";
    }

    /// <summary>
    /// Everything one template declares, declared - so the rest of it can be read.
    ///
    /// A template that invents a good and then writes a recipe for it is a correct template,
    /// and judging its recipe against the base game's twenty goods would call it broken. So
    /// the check does what the loader does: goods and buildings first, then the rest.
    /// </summary>
    internal static void DeclareTemplateContent(string common, System.Text.Json.JsonDocumentOptions options)
    {
        Declare(Path.Combine(common, "goods"), (name, value) =>
        {
            string Text(string key) => value.TryGetProperty(key, out var e) &&
                e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;
            float? Number(string key) => value.TryGetProperty(key, out var e) && e.TryGetSingle(out var n) ? n : null;
            CommodityCatalog.Override(name, Text("name"), Text("icon"), Number("base_price"),
                Number("essential_weight"), Text("description"));
        });
        Declare(Path.Combine(common, "buildings"), (name, value) =>
        {
            string Text(string key) => value.TryGetProperty(key, out var e) &&
                e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;
            float? Number(string key) => value.TryGetProperty(key, out var e) && e.TryGetSingle(out var n) ? n : null;
            int? Integer(string key) => value.TryGetProperty(key, out var e) && e.TryGetInt32(out var n) ? n : null;
            BuildingCatalog.Declare(name, Text("name"), Number("cost"), Integer("days"),
                Number("capacity_gain"), Text("description"), jobs: Number("jobs"));
        });

        void Declare(string folder, Action<string, System.Text.Json.JsonElement> apply)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in StructuredData.Files(folder))
            {
                System.Text.Json.JsonDocument document;
                try { document = StructuredData.ReadDocument(file); }
                catch { continue; }
                using (document)
                {
                    if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    foreach (var entry in document.RootElement.EnumerateObject())
                        if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            apply(entry.Name, entry.Value);
                }
            }
        }
    }

    /// <summary>One template recipe, put through the loader that will have to read it.</summary>
    internal static bool Recipe(System.Text.Json.JsonProperty entry, string file, string templates,
        List<string> complaints)
    {
        var problems = new List<string>();
        ProductionChains.Validate(
            System.Text.Json.JsonDocument.Parse($"{{\"{entry.Name}\":{entry.Value.GetRawText()}}}",
                GameDataLoader.ForgivingJson).RootElement, problems);
        foreach (var problem in problems) complaints.Add($"{Short(templates, file)}: {problem}");
        return true;
    }

    /// <summary>
    /// One trade control out of a template: a scope and a shape the game knows, bounds that
    /// contain the default, and - where it says anything at all about what it reads like -
    /// sentences the screen will actually look for.
    /// </summary>
    internal static bool TradeControl(System.Text.Json.JsonProperty entry, string file,
        string templates, List<string> complaints)
    {
        if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            complaints.Add($"{Short(templates, file)}: '{entry.Name}' is not an object");
            return false;
        }

        string Text(string name) =>
            entry.Value.TryGetProperty(name, out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        float? Number(string name) =>
            entry.Value.TryGetProperty(name, out var value) && value.TryGetSingle(out var number)
                ? number : null;

        var sound = true;
        var scope = Text("scope");
        if (scope is not null && scope != TradeDesk.GoodScope && scope != TradeDesk.PartnerScope)
        {
            complaints.Add($"{Short(templates, file)}: '{entry.Name}' is scoped to '{scope}', " +
                           $"which is neither '{TradeDesk.GoodScope}' nor '{TradeDesk.PartnerScope}'");
            sound = false;
        }

        var shape = Text("shape");
        if (shape is not null && shape is not ("share" or "multiplier" or "flag"))
        {
            complaints.Add($"{Short(templates, file)}: '{entry.Name}' has shape '{shape}', " +
                           "which is not 'share', 'multiplier' or 'flag'");
            sound = false;
        }

        var minimum = Number("minimum");
        var maximum = Number("maximum");
        var fallback = Number("default");
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            complaints.Add($"{Short(templates, file)}: '{entry.Name}' has a minimum above its maximum");
            sound = false;
        }
        if (fallback is not null &&
            ((minimum is not null && fallback < minimum) || (maximum is not null && fallback > maximum)))
        {
            complaints.Add($"{Short(templates, file)}: '{entry.Name}' defaults to a value its own " +
                           "bounds forbid, so every country would start outside them");
            sound = false;
        }

        if (entry.Value.TryGetProperty("reads", out var reads))
        {
            if (reads.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                complaints.Add($"{Short(templates, file)}: '{entry.Name}' has a 'reads' that is not an object");
                sound = false;
            }
            else
                foreach (var sentence in reads.EnumerateObject())
                    if (sentence.Name is not ("resting" or "off" or "on" or "set"))
                    {
                        complaints.Add($"{Short(templates, file)}: '{entry.Name}' reads '{sentence.Name}', " +
                                       "which is not 'resting', 'off', 'on' or 'set'");
                        sound = false;
                    }
        }

        return sound;
    }

    internal static string TheTemplatesStillNameRealThings()
    {
        var templates = FindTemplates();
        if (templates is null) return "";

        var defines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in typeof(Defines).GetProperties(
                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            if (property.CanWrite) defines.Add(Squash(property.Name));

        var options = new System.Text.Json.JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip
        };

        var complaints = new List<string>();
        var keys = 0;
        var packages = 0;

        foreach (var directory in Directory.GetDirectories(templates))
        {
            packages++;
            var manifest = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifest))
            {
                complaints.Add($"{Path.GetFileName(directory)} has no manifest.json");
                continue;
            }
            try
            {
                using var declared = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest), options);
                if (!declared.RootElement.TryGetProperty("id", out _))
                    complaints.Add($"{Path.GetFileName(directory)}/manifest.json has no id");
            }
            catch (Exception exception)
            {
                complaints.Add($"{Path.GetFileName(directory)}/manifest.json will not parse: {exception.Message}");
                continue;
            }

            var common = Path.Combine(directory, "common");
            if (!Directory.Exists(common)) continue;

            // Declare this package's own goods and buildings before judging anything, in the
            // order the mod loader would, because a template is allowed to invent them and a
            // recipe naming one has to be readable. The catalogs go back to the game's own
            // at the end of the package.
            DeclareTemplateContent(common, options);
            foreach (var category in Directory.GetDirectories(common))
            foreach (var file in StructuredData.Files(category))
            {
                System.Text.Json.JsonDocument document;
                try { document = StructuredData.ReadDocument(file); }
                catch (Exception exception)
                {
                    complaints.Add($"{Short(templates, file)} will not parse: {exception.Message}");
                    continue;
                }
                using (document)
                {
                    if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var kind = Path.GetFileName(category);

                    // The monetary model reports an unknown name rather than ignoring it, so
                    // a template's file can go through the real parser instead of through a
                    // list of names kept in step by hand. Both halves are asked, because
                    // `central_bank` is an object the first one steps over.
                    if (kind == "inflation")
                    {
                        var monetary = new List<string>();
                        InflationCore.Merge(document.RootElement, monetary);
                        CentralBank.Merge(document.RootElement, monetary);
                        foreach (var complaint in monetary)
                            complaints.Add($"{Short(templates, file)}: {complaint}");
                    }

                    foreach (var entry in document.RootElement.EnumerateObject())
                    {
                        keys++;
                        var known = kind switch
                        {
                            "defines" => defines.Contains(Squash(entry.Name)),
                            // The catalog's rule, not the enum's: a template may invent a
                            // good or a building, and by now this package's have been.
                            "goods" => CommodityCatalog.TryParse(entry.Name, out _),
                            "buildings" => BuildingCatalog.TryParse(entry.Name, out _),
                            // A recipe's name is its own, so what is checked is the recipe:
                            // the good it claims to make and every factor it asks for.
                            "production" => Recipe(entry, file, templates, complaints),
                            // A budget line's name is its own, so what is checked is the
                            // line: its basis, its factors, its effects and its limits.
                            "budget" => BudgetGroup(entry, file, templates, complaints),
                            // Same for a cabinet and a trade stance: the id is the mod's to
                            // choose, and what has to be sound is what it says it does.
                            "government_focus" => PolicyEntry(entry, file, templates, complaints,
                                GovernmentFocusCatalog.Validate),
                            "trade_policies" => PolicyEntry(entry, file, templates, complaints,
                                TradePolicyCatalog.Validate),
                            "governments" => PolicyEntry(entry, file, templates, complaints,
                                GovernmentCatalog.Validate),
                            "ideologies" => PolicyEntry(entry, file, templates, complaints,
                                IdeologyCatalog.Validate),
                            "opinion_modifiers" =>
                                entry.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                entry.Value.TryGetProperty("opinion", out _),
                            // A trade control's id is the mod's to choose - the seven the
                            // weekly pass reads are only seven of them - so what has to be
                            // sound is what it says about itself.
                            "trade" => TradeControl(entry, file, templates, complaints),
                            // A kind of long-range formation is the mod's to name; what has to be
                            // sound is its kind and the missions it may be given.
                            "support" => SupportEntry(entry),
                            // Militia is one set of numbers, read by name: a key its reader
                            // does not know is a number nobody reads.
                            "militia" => MilitiaKnows(entry.Name),
                            _ => true
                        };
                        if (known) continue;
                        complaints.Add(kind == "opinion_modifiers"
                            ? $"{Short(templates, file)}: '{entry.Name}' says nothing about opinion"
                            : $"{Short(templates, file)}: '{entry.Name}' is not a {kind.TrimEnd('s')} this game has");
                    }
                }
            }
            CommodityCatalog.ResetCatalog();
            BuildingCatalog.ResetCatalog();
            ProductionChains.ResetCatalog();
            InflationCore.ResetCatalog();
            CentralBank.ResetCatalog();
        }

        if (complaints.Count > 0) return string.Join("; ", complaints);
        Console.WriteLine($"  templates: {packages:N0} worked example(s) in mods/_templates, " +
                          $"{keys:N0} key(s) between them, every one naming something that exists.");
        return "";
    }

    internal static readonly string[] SupportMissions =
        { "standby", "close_air_support", "interdiction", "air_superiority" };

    internal static bool SupportEntry(System.Text.Json.JsonProperty entry)
    {
        var value = entry.Value;
        if (value.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        if (value.TryGetProperty("kind", out var kind) &&
            (kind.ValueKind != System.Text.Json.JsonValueKind.String ||
             !Enum.TryParse<SupportKind>(kind.GetString(), true, out _))) return false;
        if (value.TryGetProperty("missions", out var missions))
        {
            if (missions.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            foreach (var mission in missions.EnumerateArray())
                if (mission.ValueKind != System.Text.Json.JsonValueKind.String ||
                    Array.IndexOf(SupportMissions, mission.GetString()) < 0) return false;
        }
        return true;
    }

    internal static bool MilitiaKnows(string key)
    {
        var errors = new List<string>();
        MilitiaCore.Merge("{ " + System.Text.Json.JsonSerializer.Serialize(key) + ": 0 }", errors);
        MilitiaCore.ResetCatalog();
        return errors.Count == 0;
    }

    /// <summary>The template folder, wherever the bench happens to have been started from.</summary>
    internal static string FindTemplates()
    {
        var roots = new List<string> { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
            roots.Add(directory.FullName);
        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "mods", "_templates");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static string Squash(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    internal static string Short(string root, string file) =>
        file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? file[(root.Length + 1)..].Replace('\\', '/')
            : Path.GetFileName(file);

    internal static string ACategoryFolderIsReadWhole()
    {
        var folder = GameDataLoader.DataFolder("common", "defines");
        if (folder is null) return "common/defines does not exist";
        var shipped = GameDataLoader.DataFiles("common", "defines");
        if (shipped.Count < 2)
            return $"common/defines ships {shipped.Count} file(s); the base game should be laid " +
                   "out the way it asks mods to lay themselves out";

        var probe = System.IO.Path.Combine(folder, "99_zz_bench_probe.json");
        try
        {
            System.IO.File.WriteAllText(probe, "{ \"DefenderAdvantage\": 7.25 }");
            var probed = new Defines();
            GameDataLoader.ApplyDefines(probed);
            if (MathF.Abs(probed.DefenderAdvantage - 7.25f) > 0.0001f)
                return $"a file added to common/defines was ignored; defender advantage stayed " +
                       $"{probed.DefenderAdvantage:0.##} instead of taking the 7.25 the new file asked for";
        }
        catch (Exception exception)
        {
            return $"could not write into common/defines to test the load order: {exception.Message}";
        }
        finally
        {
            if (System.IO.File.Exists(probe)) System.IO.File.Delete(probe);
        }

        var restored = new Defines();
        GameDataLoader.ApplyDefines(restored);
        if (MathF.Abs(restored.DefenderAdvantage - new Defines().DefenderAdvantage) > 0.0001f)
            return "taking the added file away did not restore the shipped value";

        Console.WriteLine($"  data layout: common/defines ships {shipped.Count} file(s), read in name " +
                          "order, and a file dropped in beside them overrides them.");
        return "";
    }

    /// <summary>
    /// The shipped defines file must still say what the code says.
    ///
    /// It did not, and nothing noticed: game/common/defines/military.json had been left
    /// behind by a rebalance and was quietly overriding defender advantage, combat width,
    /// unopposed capture, supply throughput and the AI's war chance with the values from
    /// before it. The data wins over the code by design - that is what makes it moddable -
    /// so a stale shipped file undoes a rebalance without failing anything at all.
    /// </summary>
    internal static string ShippedDefinesMatchTheCode()
    {
        var files = GameDataLoader.DataFiles("common", "defines");
        if (files.Count == 0) return "no defines are shipped at all; common/defines is missing or empty";

        var compiled = new Defines();
        var effective = new Defines();
        GameDataLoader.ApplyDefines(effective);
        var properties = typeof(Defines)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        var drifted = new List<string>();
        var checkedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "defines"))
        using (document)
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!properties.TryGetValue(entry.Name, out var property)) continue;
            // Compare the final layered value, not an intermediate file overridden later.
            // ApplyDefines also merges individual feature flags and unlock rules.
            if (!checkedNames.Add(property.Name)) continue;
            var shippedValue = property.GetValue(effective);
            var compiledValue = property.GetValue(compiled);
            if (DefineValuesAgree(shippedValue, compiledValue)) continue;
            var shipped = JsonSerializer.Serialize(shippedValue);
            var code = Convert.ToString(property.GetValue(compiled),
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (compiledValue is System.Collections.IDictionary or Array) code = JsonSerializer.Serialize(compiledValue);
            drifted.Add($"{entry.Name}: file {shipped}, code {code}");
        }

        if (checkedNames.Count == 0) return "the shipped defines file sets nothing the code recognises";
        if (drifted.Count > 0)
            return $"{drifted.Count} shipped define(s) disagree with the code: " +
                   string.Join("; ", drifted);
        Console.WriteLine($"  {checkedNames.Count:N0} effective shipped defines all agree with the compiled values.");
        return "";
    }

    internal static bool DefineValuesAgree(object shipped, object compiled)
    {
        if (shipped is float number && compiled is float baseline)
            return MathF.Abs(number - baseline) < 0.0001f;
        if (shipped is Array values && compiled is Array baselineValues)
        {
            if (values.Rank != 1 || baselineValues.Rank != 1 || values.Length != baselineValues.Length) return false;
            for (var i = 0; i < values.Length; i++)
                if (!DefineValuesAgree(values.GetValue(i), baselineValues.GetValue(i))) return false;
            return true;
        }
        if (shipped is System.Collections.IDictionary map && compiled is System.Collections.IDictionary defaults)
        {
            if (map.Count != defaults.Count) return false;
            foreach (System.Collections.DictionaryEntry item in map)
                if (!defaults.Contains(item.Key) || !DefineValuesAgree(item.Value, defaults[item.Key])) return false;
            return true;
        }
        return Equals(shipped, compiled);
    }

    internal static string IntegrityScannerSelfTest()
    {
        var captions = Captions("ActionButton(\"MapSettings\", \"Scenario settings…\", EditSettings); " +
            "ActionButton(\"Clear plans\", () => ClearPlans(1, 2));").ToHashSet(StringComparer.Ordinal);
        if (captions.Contains("MapSettings") || !captions.Contains("Scenario settings…") || !captions.Contains("Clear plans"))
            return "the caption scanner confused a node identifier with visible text";
        var fixture = Path.Combine(Path.GetTempPath(), "deal-source-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "ui"));
            Directory.CreateDirectory(Path.Combine(fixture, ".kilo", "worktrees", "old", "Scripts"));
            Directory.CreateDirectory(Path.Combine(fixture, "obj"));
            File.WriteAllText(Path.Combine(fixture, "ui", "Current.cs"), "Tr(\"Current caption\");");
            File.WriteAllText(Path.Combine(fixture, ".kilo", "worktrees", "old", "Scripts", "Old.cs"), "Tr(\"Removed command\");");
            File.WriteAllText(Path.Combine(fixture, "obj", "Generated.cs"), "Tr(\"Generated test caption\");");
            var indexed = ProductionSourceFiles(fixture).SelectMany(file => Captions(File.ReadAllText(file))).ToArray();
            if (indexed.Length != 1 || indexed[0] != "Current caption")
                return "the source scanner indexed nested private checkouts or generated output";
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
        var left = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["aviation"] = false };
        var right = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["AVIATION"] = false };
        if (!DefineValuesAgree(left, right)) return "equivalent feature dictionaries disagree";
        right["aviation"] = true;
        if (DefineValuesAgree(left, right)) return "a real feature change went undetected";
        if (DefineValuesAgree(0.25f, 0.5f)) return "a real numeric rebalance went undetected";
        right.Clear();
        if (DefineValuesAgree(left, right)) return "a missing feature went undetected";
        if (!DefineValuesAgree(new[] { "#C57857", "#4F94C0" }, new[] { "#C57857", "#4F94C0" }))
            return "equivalent ordered define arrays disagree";
        if (DefineValuesAgree(new[] { "#C57857", "#4F94C0" }, new[] { "#4F94C0", "#C57857" }))
            return "an array reorder went undetected";
        if (DefineValuesAgree(new[] { "#C57857" }, new[] { "#C57857", "#4F94C0" }))
            return "an array length change went undetected";
        return "";
    }

    /// <summary>
    /// A content pack is locked until it is owned, and works the moment it is.
    ///
    /// Both halves matter and each fails silently on its own. A pack whose content leaks
    /// into an unentitled game has no gate; a pack that is gated but broken sells somebody
    /// a folder of JSON that does nothing. So: the shipped game must not contain the pack's
    /// decisions, and loading the pack's own files by hand must produce them.
    ///
    /// This checks the data rather than the loader, because the loader needs the engine and
    /// this bench deliberately does not. What it proves is the part that can actually rot -
    /// a pack file with a typo in it, or a decision id that quietly collided with the base
    /// game and was there all along.
    /// </summary>
    internal static string ContentPacksAreLockedUntilOwned()
    {
        // Through the same lookup the game uses, rather than off the working directory.
        // Hanging it on the working directory meant this passed from the project root and
        // reported the content pack framework missing from anywhere else - a check that
        // says where it was run from rather than whether the packs are sound. It also means
        // it now exercises the resolution an exported build depends on.
        var root = GameDataLoader.InstallFolders("dlc").FirstOrDefault();
        var packs = string.IsNullOrEmpty(root) || !Directory.Exists(root)
            ? new List<string>()
            : Directory.GetDirectories(root)
                .Where(directory => File.Exists(Path.Combine(directory, "dlc.json")))
                .OrderBy(directory => directory, StringComparer.Ordinal)
                .ToList();

        // The public repository ships the framework and no pack - a pack is content somebody
        // sells, and the source is not where it lives. So where none is installed the bench
        // writes itself a small gated one and proves the gate on that, rather than passing
        // because there was nothing to check.
        var sample = packs.Count == 0 ? WriteSamplePack() : null;
        if (sample is not null) packs.Add(sample);
        try
        {
            return CheckContentPacks(packs, sample is not null);
        }
        finally
        {
            if (sample is not null)
                try { Directory.Delete(Path.GetDirectoryName(sample)!, true); } catch (IOException) { }
        }
    }

    /// <summary>A gated pack with one decision in it, in a folder of its own under the temp path.</summary>
    internal static string WriteSamplePack()
    {
        var pack = Path.Combine(Path.GetTempPath(), "dof_bench_dlc", "bench_sample_pack");
        Directory.CreateDirectory(Path.Combine(pack, "common", "decisions"));
        File.WriteAllText(Path.Combine(pack, "dlc.json"),
            "{ \"id\": \"bench_sample_pack\", \"name\": \"Bench sample\", \"version\": \"1.0.0\", " +
            "\"entitlement\": \"bench_sample_pack\", \"load_order\": 100, \"requires\": [], \"affects_rules\": true }");
        File.WriteAllText(Path.Combine(pack, "common", "decisions", "00_bench_sample.json"),
            "{ \"bench_sample_decision\": { \"name\": \"Bench sample decision\", " +
            "\"description\": \"Exists only to prove a gated pack loads once owned.\", " +
            "\"cooldown_days\": 365, \"gdp_cost_share\": 0.0, \"effects\": { \"stability\": 0.01 } } }");
        return pack;
    }

    internal static string CheckContentPacks(List<string> packs, bool sampled)
    {
        var reported = new List<string>();
        foreach (var pack in packs)
        {
            using var descriptor = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "dlc.json")),
                GameDataLoader.ForgivingJson);
            var root_ = descriptor.RootElement;
            if (!root_.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.GetString()))
                return $"{Path.GetFileName(pack)} has no id";
            var entitlement = root_.TryGetProperty("entitlement", out var owned) ? owned.GetString() ?? "" : "";

            var files = Directory.Exists(Path.Combine(pack, "common"))
                ? Directory.GetFiles(Path.Combine(pack, "common"), "*.json", SearchOption.AllDirectories)
                : Array.Empty<string>();
            if (files.Length == 0) return $"{id.GetString()} ships no content at all";

            var decisions = files.Where(file => file.Contains(Path.Combine("common", "decisions"))).ToArray();
            var added = new List<string>();
            foreach (var file in decisions)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                    added.Add(entry.Name);
                    // Locked, and therefore absent from the game the player is running.
                    if (!string.IsNullOrWhiteSpace(entitlement) && DecisionCatalog.Find(entry.Name) is not null)
                        return $"'{entry.Name}' is in the base game although {id.GetString()} is not owned";
                }
            }
            if (added.Count == 0) continue;

            // Unlocked: the same files have to actually load and produce those decisions.
            var errors = new List<string>();
            ScriptLibrary.LoadDirectories(pack, errors);
            var before = DecisionCatalog.All.Count;
            foreach (var file in decisions)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                foreach (var entry in document.RootElement.EnumerateObject())
                    if (!entry.Name.StartsWith("//", StringComparison.Ordinal))
                        DecisionCatalog.Apply(entry.Name, entry.Value, errors);
            }
            var missing = added.Where(name => DecisionCatalog.Find(name) is null).ToArray();
            if (missing.Length > 0)
                return $"{id.GetString()} was unlocked and still did not deliver: {string.Join(", ", missing)}";
            if (errors.Count > 0)
                return $"{id.GetString()} loaded with complaints: {errors[0]}";
            reported.Add($"{id.GetString()} ({(string.IsNullOrWhiteSpace(entitlement) ? "free" : "gated")}, " +
                         $"{DecisionCatalog.All.Count - before} decision(s))");
        }

        // Put the game back: the pack was loaded to prove it works, not to play with it.
        DecisionCatalog.ResetCatalog();
        ScriptLibrary.ResetCatalog();
        if (reported.Count == 0) return "no content pack delivered a single decision, so nothing proves the pipeline works";
        Console.WriteLine($"  content packs{(sampled ? " (none installed; the bench's own sample)" : "")}: " +
                          $"{string.Join(", ", reported)} - absent while locked, complete once owned.");
        return "";
    }

    internal static string TheInterfaceComposesThroughTheTable()
    {
        const int Ceiling = 210;

        var scripts = FindScripts();
        if (scripts is null) return "";

        // The developer console is a tool for whoever is building the game, and it is
        // deliberately English - it prints type names and command syntax.
        var skip = new[] { "DeveloperConsoleCommands.cs", "DeveloperConsoleUI.cs" };

        var offenders = new List<string>();
        var total = 0;
        foreach (var file in ProductionSourceFiles(scripts))
        {
            var name = Path.GetFileName(file);
            if (skip.Contains(name)) continue;
            // Only the presentation layer: the simulation's own strings go through Loc, and
            // its diagnostics are meant to be English.
            var relative = Path.GetRelativePath(scripts, file).Replace('\\', '/');
            if (relative.StartsWith("Simulation/", StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith("Modding/", StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith("LegacySourceMap/", StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith("ui/Console/", StringComparison.OrdinalIgnoreCase)) continue;

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.Contains("GD.Print", StringComparison.Ordinal)) continue;
                if (trimmed.Contains("DiagnosticLogs", StringComparison.Ordinal)) continue;
                if (!line.Contains("$\"", StringComparison.Ordinal)) continue;
                if (!HasProse(line)) continue;
                total++;
                if (offenders.Count < 6)
                    offenders.Add($"{relative}:{index + 1}");
            }
        }

        Console.WriteLine($"  localization: {total:N0} interpolated line(s) of prose the table " +
                          $"cannot reach, against a ceiling of {Ceiling:N0}.");
        if (total > Ceiling)
            return $"{total:N0} interpolated line(s) of English the localisation table cannot " +
                   $"reach, over the ceiling of {Ceiling:N0}: {string.Join(", ", offenders)}";
        return "";
    }

    /// <summary>
    /// Whether a line of source has actual words in a string rather than only a format.
    ///
    /// Two letters in a row outside a brace is the test, and it is deliberately crude: it
    /// misses nothing that matters and admits the occasional format specifier, which is a
    /// far better failure than the other way round.
    /// </summary>
    internal static bool HasProse(string line)
    {
        var start = line.IndexOf("$\"", StringComparison.Ordinal);
        while (start >= 0)
        {
            var depth = 0;
            var letters = 0;
            for (var index = start + 2; index < line.Length; index++)
            {
                var character = line[index];
                if (character == '{') { depth++; continue; }
                if (character == '}') { depth = Math.Max(0, depth - 1); continue; }
                if (character == '"' && depth == 0) break;
                if (depth > 0) continue;
                if (char.IsLetter(character)) { if (++letters >= 3) return true; }
                else letters = 0;
            }
            start = line.IndexOf("$\"", start + 2, StringComparison.Ordinal);
        }
        return false;
    }

    internal static string EveryFolderOfDataIsRead()
    {
        var scripts = FindScripts();
        if (scripts is null) return "";
        var game = Path.Combine(Path.GetDirectoryName(scripts)!, "game");
        if (!Directory.Exists(game)) return "";

        // Loading a campaign asks for nearly everything, and these are asked for by the shell
        // rather than the simulation - the options screen, the diagnostic channels, the skin and
        // the console's verse - so the bench asks for them here rather than pretending nothing
        // reads them.
        SettingsCatalog.ResetCatalog();
        DiagnosticLogs.ResetCatalog();
        InterfaceSkin.ResetCatalog();
        ScenarioInspectorCatalog.ResetCatalog();
        // The sound/art workspace loads its inventory lazily, outside the headless
        // campaign. Exercise the real reader rather than exempting its folder.
        MediaLibraryCatalog.Load();
        // Make this audit independent of which expensive campaign fixtures ran
        // before it. These are real lazy catalog readers, not an allowlist of paths.
        ScriptLibrary.ResetCatalog();
        AddressToNation.ResetCatalog();
        CovertOperations.ResetCatalog();
        DecisionCatalog.ResetCatalog();
        PromiseCatalog.ResetCatalog();
        GoalCatalog.ResetCatalog();
        FlagCatalog.ResetCatalog();
        FormableNations.ResetCatalog();
        GovernanceEvents.ResetCatalog();
        PeaceTermCatalog.ResetCatalog();
        SecessionCore.ResetCatalog();
        SocialIncidentCatalog.ResetCatalog();
        // Dated history and deterrence are lazy catalogues too: reading them here is what
        // makes the folder audit see the real reader instead of an allowlist entry.
        EventCatalog.ResetCatalog();
        Deterrence.ResetCatalog();
        _ = Deterrence.Holders.Count;
        _ = UnitTypeCatalog.All.Count();
        // And the catalogues read the first time something needs them - the handbook when F1 is
        // pressed, the news weights at the first headline, the coups at the first review - so a
        // bench that has not happened to need them yet does not report their folders as dead.
        HandbookCatalog.ResetCatalog();
        NewsFeed.ResetCatalog();
        AllianceProgramCatalog.ResetCatalog();
        OutreachCatalog.ResetCatalog();
        IncidentCatalog.ResetCatalog();
        MediaLibraryCatalog.Load();
        Coups.ResetCatalog();
        Coups.GovernmentRisk(new Nation());
        // The drawn layers, the quality ladder and event art are lazy too: the bench never
        // opens the globe or the settings screen, so without this their folders read as dead.
        WorldLayers.ResetCatalog();
        QualityProfiles.ResetCatalog();
        _ = EventPictures.Table;
        // Catalogues first read when a mechanic first fires: houses on a succession, shocks on a
        // disaster roll, election control on a first election, formable ambitions on a review,
        // the land names on a map label, the book of war when a doctrine is studied.
        DynastyCatalog.ResetCatalog(); _ = DynastyCatalog.DynastyOfTag;
        TreasuryDynamics.ResetCatalog();
        ElectoralPolitics.ResetCatalog(); _ = ElectoralPolitics.InitialControlTable;
        FormableProclamationAi.ResetCatalog();
        LandAtlas.ResetCatalog(); _ = LandAtlas.All;
        TacticalDoctrineCatalog.ResetCatalog();
        // Console prose belongs to the normal moddable language tables, not common/console.
        if (Loc.Get("console.touchgrass.verse") is "" or "console.touchgrass.verse")
            return "the console's localized verse came back empty";

        var asked = new HashSet<string>(GameDataLoader.Asked, StringComparer.OrdinalIgnoreCase);
        if (asked.Count == 0) return "nothing has asked the loader for any data at all";

        // Categories that are read by name rather than as a folder of tables - rasters,
        // pictures, prose - and are moddable by other means. Each is named here rather than
        // guessed at, so adding one is a decision somebody wrote down.
        var byOtherMeans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gfx", "fonts", "sound", "music", "localization", "interface", "map_data",
            "history", "events", "scenarios",
            // Panel geometry and shell layout: InterfaceSkin.LoadDirectory reads the folder
            // straight off the package root rather than through the data loader's Asked set.
            "gui",
            // Prose for whoever opens the folder, shipped on purpose and read by nobody.
            "documentation",
            // Native script extensions, loaded by the engine through their .gdextension file
            // (EngineScriptMod) rather than by the data loader.
            "extensions"
        };

        var unread = new List<string>();
        foreach (var directory in Directory.GetDirectories(Path.Combine(game, "common")))
        {
            var name = "common/" + Path.GetFileName(directory);
            if (asked.Contains(name)) continue;
            unread.Add(name);
        }
        foreach (var directory in Directory.GetDirectories(game))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase)) continue;
            if (byOtherMeans.Contains(name) || asked.Contains(name)) continue;
            unread.Add(name);
        }

        if (unread.Count > 0)
            return $"{unread.Count} folder(s) of data that nothing reads, so a mod editing " +
                   $"them changes nothing and is told nothing: {string.Join(", ", unread)}";

        // And the other way: a category something asks for and the base game does not ship,
        // which leaves a modder with a feature and no worked example of it.
        var undocumented = new List<string>();
        foreach (var name in asked)
        {
            if (!name.StartsWith("common/", StringComparison.OrdinalIgnoreCase)) continue;
            // An old name still honoured for mods written before it was folded into another
            // folder (LandAtlas: common/lands became common/naming). Shipping it would be two homes.
            if (string.Equals(name, "common/lands", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(Path.Combine(game, name.Replace('/', Path.DirectorySeparatorChar))))
                continue;
            undocumented.Add(name);
        }
        if (undocumented.Count > 0)
            return $"{undocumented.Count} category(ies) the game reads and does not ship, so a " +
                   $"mod has nothing to copy: {string.Join(", ", undocumented)}";

        var common = Directory.GetDirectories(Path.Combine(game, "common")).Length;
        Console.WriteLine($"  data: {common:N0} category folder(s) under common/, every one of them " +
                          $"read by name, and {asked.Count:N0} categor(ies) asked for in total.");
        return "";
    }

    /// <summary>
    /// The records a save writes as rows (Simulation/SaveRows.cs) come back whole.
    ///
    /// A row is written field by field, by hand, so a field added to the type and not to its
    /// row would be saved as nothing at all - and nothing would say so until a loaded campaign
    /// had quietly lost it. Every public field gets a value of its own here, goes through the
    /// converter and back, and has to come back as it went.
    /// </summary>
    internal static string ASaveRowCarriesEveryField()
    {
        var wrong = new List<string>();

        var group = new PopulationGroup();
        var fields = typeof(PopulationGroup).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        for (var index = 0; index < fields.Length; index++) fields[index].SetValue(group, Distinct(fields[index].FieldType, index));
        var groupBack = JsonSerializer.Deserialize<PopulationGroup>(JsonSerializer.Serialize(group));
        foreach (var field in fields)
            if (!Equals(field.GetValue(group), field.GetValue(groupBack)))
                wrong.Add($"PopulationGroup.{field.Name}");

        var constructor = typeof(NationHistorySample).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select((parameter, index) => Distinct(parameter.ParameterType, index)).ToArray();
        var sample = (NationHistorySample)constructor.Invoke(arguments);
        var sampleBack = JsonSerializer.Deserialize<NationHistorySample>(JsonSerializer.Serialize(sample));
        foreach (var property in typeof(NationHistorySample).GetProperties())
            if (property.GetIndexParameters().Length == 0 && !Equals(property.GetValue(sample), property.GetValue(sampleBack)))
                wrong.Add($"NationHistorySample.{property.Name}");

        foreach (var rowType in new[] { typeof(PublicImage), typeof(MarketQuote), typeof(OpinionReason), typeof(LandBattle.Round) })
        {
            var instance = Activator.CreateInstance(rowType)!;
            var rowFields = rowType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            for (var index = 0; index < rowFields.Length; index++)
                rowFields[index].SetValue(instance, Distinct(rowFields[index].FieldType, index));
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(instance, rowType), rowType)!;
            foreach (var field in rowFields)
                if (!Equals(field.GetValue(instance), field.GetValue(restored)))
                    wrong.Add($"{rowType.Name}.{field.Name}");
        }

        if (wrong.Count > 0)
            return $"{wrong.Count} field(s) do not survive their save row: {string.Join(", ", wrong)} - add them to Simulation/SaveRows.cs";
        Console.WriteLine($"  save rows: all {fields.Length} fields of a people and {arguments.Length} of a history week come back as they went.");
        return "";

        static object Distinct(Type type, int index) =>
            type == typeof(string) ? $"value{index}" :
            type == typeof(int) ? 1000 + index :
            type == typeof(long) ? 1_000_000L + index :
            type == typeof(float) ? 0.125f + index :
            type == typeof(double) ? 0.25 + index :
            type == typeof(bool) ? true :
            type.IsEnum ? Enum.GetValues(type).GetValue(Math.Min(1, Enum.GetValues(type).Length - 1))! :
            throw new InvalidOperationException($"no distinct value for {type.Name}");
    }

    internal static string AWorldThatCanBeUnbuilt()
    {
        var campaign = Campaign.CreateBootstrap(20241014);
        if (!AttachMap(campaign)) return "";
        var nation = campaign.Nations.FirstOrDefault(country => country.Population > 20_000_000);
        if (nation is null) return "no country large enough to build anything";
        var state = campaign.StatesOf(nation).FirstOrDefault();
        if (state is null) return $"{nation.Tag} owns no region";

        var definition = BuildingCatalog.Get(BuildingKind.CivilianIndustry);
        var yield = Waterfront.Yield(campaign, state.MapIndex, BuildingKind.CivilianIndustry);
        var standing = state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry);
        var gdp = nation.Gdp;
        var capacity = nation.ConstructionCapacity;
        var treasury = nation.Treasury;
        var demolitionValue = ConstructionCost.Of(campaign, nation, BuildingKind.CivilianIndustry);

        // Up. Ordered and finished on the spot rather than waited out: what is being tested
        // is the arithmetic of the two orders, not the calendar between them.
        nation.Treasury = 500f;
        new QueueConstructionCommand
        {
            IssuerId = nation.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.CivilianIndustry
        }.Apply(campaign);
        var project = campaign.Construction.FindLast(candidate => candidate.NationId == nation.Id);
        if (project is null) return $"{nation.Tag} could not open a site at all";

        // And back out of it, which is the cheaper half of the tester's request and the one
        // that matters most: twelve sites run at once and nothing could clear one.
        var queued = campaign.Construction.Count;
        new CancelConstructionCommand { IssuerId = nation.Id, ProjectId = project.Id }.Apply(campaign);
        if (campaign.Construction.Count != queued - 1)
            return "a site was called off and is still in the queue";

        // Somebody else's site is not theirs to call off.
        var other = campaign.Nations.FirstOrDefault(country => country.Id != nation.Id &&
                                                              country.Population > 20_000_000);
        if (other is not null)
        {
            new QueueConstructionCommand
            {
                IssuerId = nation.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.CivilianIndustry
            }.Apply(campaign);
            var mine = campaign.Construction.FindLast(candidate => candidate.NationId == nation.Id);
            if (mine is not null)
            {
                new CancelConstructionCommand { IssuerId = other.Id, ProjectId = mine.Id }.Apply(campaign);
                if (!campaign.Construction.Contains(mine))
                    return $"{other.Tag} called off a site belonging to {nation.Tag}";
                new CancelConstructionCommand { IssuerId = nation.Id, ProjectId = mine.Id }.Apply(campaign);
            }
        }

        // Now the round trip. One building placed by hand, then pulled down, and everything
        // back where it started bar the scrap value.
        state.Buildings[BuildingKind.CivilianIndustry] = standing + 1;
        // No GDP step on completion any more (the market counts a building's output), so GDP
        // must come back to exactly where it was.
        nation.Gdp = gdp;
        nation.ConstructionCapacity = capacity + definition.CapacityGain * yield;
        nation.Treasury = treasury;

        new DemolishBuildingCommand
        {
            IssuerId = nation.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.CivilianIndustry
        }.Apply(campaign);

        if (state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry) != standing)
            return $"a building was demolished and {state.Name} still counts " +
                   $"{state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry)} of them " +
                   $"against {standing} before it was built";
        if (MathF.Abs(nation.Gdp - gdp) > 0.01f)
            return $"{nation.Tag}'s GDP is {nation.Gdp:0.###} after building and demolishing " +
                   $"the same thing, against {gdp:0.###} before";
        if (MathF.Abs(nation.ConstructionCapacity - capacity) > 0.001f)
            return $"{nation.Tag}'s construction capacity is {nation.ConstructionCapacity:0.###} " +
                   $"against {capacity:0.###} before";

        // Refund a share of this country's construction price, not the unscaled catalogue price.
        var scrap = demolitionValue * campaign.Defines.DemolitionRefund;
        if (MathF.Abs(nation.Treasury - (treasury + scrap)) > 0.01f)
            return $"demolition returned {nation.Treasury - treasury:0.###} rather than the " +
                   $"{scrap:0.###} the defines promise";

        // And nothing comes of demolishing what is not there.
        var empty = campaign.Nations.FirstOrDefault(country => country.Id != nation.Id);
        var before = nation.Treasury;
        new DemolishBuildingCommand
        {
            IssuerId = nation.Id, StateMapIndex = state.MapIndex, Building = BuildingKind.Shipyard
        }.Apply(campaign);
        if (state.Buildings.GetValueOrDefault(BuildingKind.Shipyard) == 0 &&
            MathF.Abs(nation.Treasury - before) > 0.0001f)
            return "demolishing a building that was never there paid out";
        if (empty is not null)
        {
            var theirs = campaign.StatesOf(empty).FirstOrDefault();
            if (theirs is not null)
            {
                var had = theirs.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry);
                new DemolishBuildingCommand
                {
                    IssuerId = nation.Id, StateMapIndex = theirs.MapIndex,
                    Building = BuildingKind.CivilianIndustry
                }.Apply(campaign);
                if (theirs.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry) != had)
                    return $"{nation.Tag} demolished a building in {empty.Tag}'s own region";
            }
        }

        Console.WriteLine($"  building: {nation.Tag} raised and razed a {definition.Name.ToLowerInvariant()} " +
                          $"in {state.Name}; GDP, capacity and the queue all came back, " +
                          $"{scrap:0.##}B in scrap.");
        return "";
    }

    internal static string AnEventSpeaksTheLanguageTheGameIsIn()
    {
        EventCatalog.ResetCatalog();
        if (EventCatalog.All.Count == 0) return "the game has no events at all";

        var shown = new List<(string Where, string Text)>();
        foreach (var definition in EventCatalog.All)
        {
            shown.Add(($"{definition.Id}.title", definition.Title));
            shown.Add(($"{definition.Id}.description", definition.Description));
            foreach (var option in definition.Options)
            {
                shown.Add(($"{definition.Id}.option", option.Title));
                if (!string.IsNullOrWhiteSpace(option.Consequence))
                    shown.Add(($"{definition.Id}.consequence", option.Consequence));
            }
        }

        // Membership rather than lookup, and no switching of the language the run is in:
        // a check that leaves the game in another language is a check that breaks the next
        // one.
        var gaps = new List<string>();
        foreach (var language in new[] { "english", "russian" })
        {
            var known = new HashSet<string>(Loc.Keys(language), StringComparer.Ordinal);
            foreach (var (where, content) in shown)
            {
                if (string.IsNullOrWhiteSpace(content) || known.Contains(content)) continue;
                gaps.Add($"{language}: {where} - " + content[..Math.Min(48, content.Length)]);
            }
        }

        if (gaps.Count > 0)
            return $"{gaps.Count:N0} event string(s) never reach a table, starting with " +
                   string.Join("; ", gaps.Take(3));

        Console.WriteLine($"  events: {EventCatalog.All.Count:N0} event(s) put {shown.Count:N0} string(s) on the " +
                          "screen, and every one of them is in both tables.");
        return "";
    }

}
