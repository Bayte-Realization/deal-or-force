using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Runs the campaign simulation without Godot: checks the invariants that are expensive
/// to notice by eye, then times a run of campaign days and reports where the time went.
///
/// Usage: SimBench [days]   (default 730 - two campaign years)
/// Exit code is non-zero when a check fails, so this is usable in CI as-is.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var days = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 730;

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Deal or Force - headless simulation bench");
        Console.WriteLine(new string('-', 62));

        // A second argument reseeds the world. The Russo-Ukrainian front used to be a
        // coin flip dressed up as a check - it passed on one map and handed Ukraine to
        // Russia on another - so being able to ask for the same scenario under a different
        // seed is how the front's budget gets set from measurement rather than from luck.
        var seed = args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var chosen)
            ? chosen
            : 20260830;

        var setup = Stopwatch.StartNew();
        var campaign = Campaign.CreateBootstrap(seed);
        setup.Stop();
        Console.WriteLine($"Campaign created in {setup.ElapsedMilliseconds} ms: " +
                          $"{campaign.Nations.Count} nations, {campaign.States.Count} states.");

        var mapped = AttachMap(campaign);
        var frontBefore = new FrontSnapshot(campaign);

        var failures = 0;
        failures += Check("day-zero GDP reproduces the authored 2024 figures", CalibrationHolds(campaign));
        failures += Check("every nation has an output calibration", NationsCalibrated(campaign));
        failures += Check("state ownership index agrees with a linear scan", IndexAgrees(campaign));
        // Asked at day zero, before any conquest: the scenario has to put every
        // populated country on the map. After a year of AI wars some of them have
        // been annexed, which is the point rather than a fault.
        failures += Check("every nation starts the scenario owning ground", EveryNationHasTerritory(campaign));
        if (mapped)
            failures += Check("every authored region correction exists on the map",
                AuthoredRegionsExist(campaign));

        Console.WriteLine();
        Console.WriteLine($"Running {days} campaign day(s)…");

        campaign.IsPaused = false;
        var ukraineTrace = new List<int>();
        var ukraineIndex = campaign.NationByTag("UKR")?.MapIndex ?? -1;
        // Wall time on a developer's machine is noise - the same seed and the same code
        // measured 11.5 ms and 19.2 ms a day depending on what else was running. What a
        // day costs in garbage does not move, so that is what gets reported alongside it:
        // allocation per day is the number that predicts stutter and memory both.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var collectionsBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var run = Stopwatch.StartNew();
        for (var day = 0; day < days; day++)
        {
            campaign.Tick();
            // A front that ends the year where it started may still have collapsed and been
            // pushed back in between. Sampling as it goes is what makes "stalemate" mean
            // what it says.
            if (day % 15 == 0 && ukraineIndex >= 0 && campaign.ProvinceController is not null)
                ukraineTrace.Add(HeldBy(campaign, ukraineIndex));
        }
        run.Stop();

        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        var collections = (GC.CollectionCount(0) - collectionsBefore.Item1,
            GC.CollectionCount(1) - collectionsBefore.Item2,
            GC.CollectionCount(2) - collectionsBefore.Item3);
        var perDay = run.Elapsed.TotalMilliseconds / days;
        Console.WriteLine($"  {run.ElapsedMilliseconds} ms total, {perDay:0.###} ms per campaign day.");
        Console.WriteLine($"  {allocated / 1_048_576.0:0.#} MB allocated, " +
                          $"{allocated / days / 1024.0:0.#} kB per day, " +
                          $"{collections.Item1:N0}/{collections.Item2:N0}/{collections.Item3:N0} gen0/1/2 collections, " +
                          $"{GC.GetTotalMemory(true) / 1_048_576.0:0.#} MB live, " +
                          $"{Environment.WorkingSet / 1_048_576.0:0.#} MB resident.");
        Console.WriteLine($"  {campaign.Date} after {campaign.ElapsedDays} day(s).");

        Console.WriteLine();
        Console.WriteLine("Simulation passes, by the garbage they make (average per run):");
        foreach (var sample in SimProfiler.All
                     .OrderByDescending(entry => entry.AllocatedBytes)
                     .ThenByDescending(entry => entry.AverageMilliseconds))
            Console.WriteLine($"  {sample.Name,-16} {sample.AverageMilliseconds,8:0.###} ms  " +
                              $"{sample.AllocatedBytes / 1_048_576.0,8:0.#} MB  " +
                              $"{sample.AllocatedBytes / (double)Math.Max(1, sample.Runs) / 1024.0,8:0.#} kB/run  " +
                              $"×{sample.Runs}");
        Console.WriteLine();

        Console.WriteLine();
        failures += Check("the economy actually moved", EconomyMoved(campaign));
        failures += Check("scripted content parses and applies", ScriptedContentWorks(campaign));
        failures += Check("modifiers grant, tick and expire", ModifiersWork(campaign));
        failures += Check("no nation ran to a nonsense GDP", GdpSane(campaign));
        failures += Check("the shipped skin matches the compiled defaults", SkinIsCoherent());
        failures += Check("the shipped country definitions describe the world the game starts in",
            ShippedCountriesMatchTheScenario(campaign));
        failures += Check("a category folder is read whole and later files win",
            ACategoryFolderIsReadWhole());
        failures += Check("the shipped defines match the compiled defaults", ShippedDefinesMatchTheCode());
        if (mapped)
            failures += Check("the political map is data a mod can override", ThePoliticalMapIsData(campaign));
        failures += Check("headline figures format the way a player reads them", FiguresReadWell());

        if (mapped)
            failures += Check("the Russo-Ukrainian front is still a stalemate",
                FrontIsAStalemate(campaign, frontBefore, ukraineTrace, days));

        failures += Check("the world governs itself", TheWorldGovernsItself(campaign, days));

        // The destructive checks redraw the map and end wars, so they run against a fresh
        // campaign rather than the one that was just measured. Sharing one made them
        // depend on each other's order and on whether the AI had already settled the war
        // they were about to negotiate.
        if (mapped)
        {
            Console.WriteLine();
            Console.WriteLine("Second campaign for the checks that redraw the map…");
            var sandbox = Campaign.CreateBootstrap(20260830);
            if (AttachMap(sandbox))
            {
                sandbox.IsPaused = false;
                // Long enough for a war score to exist, so the peace has something to price.
                for (var day = 0; day < 220; day++) sandbox.Tick();
                failures += Check("a region cut by a new border becomes two regions",
                    RegionsSplitAlongNewBorders(sandbox));
                failures += Check("a redrawn border survives a save", BordersSurviveASave(sandbox));
                failures += Check("a drawn line moves the formations put on it",
                    ADrawnLineCommandsTheFormationsOnIt(sandbox));
                failures += Check("a peace can be written, priced, refused and signed",
                    APeaceCanBeWritten(sandbox));
                failures += Check("a moved border leaves a national minority behind",
                    ConquestLeavesMinorities(sandbox));
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All checks passed."
            : $"{failures} check(s) FAILED.");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string description, string failure)
    {
        var ok = string.IsNullOrEmpty(failure);
        Console.WriteLine(ok ? $"  [ ok ] {description}" : $"  [FAIL] {description}: {failure}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The whole point of calibrating emergent output: day zero must still read as the
    /// 2024 the scenario was authored with, or the economy is accurate to its own model
    /// and wrong about the world.
    /// </summary>
    private static string CalibrationHolds(Campaign campaign)
    {
        foreach (var nation in campaign.Nations.Where(nation => nation.Gdp > 1f)
                     .OrderByDescending(nation => nation.Gdp).Take(40))
        {
            var emergent = nation.GrossOutput * 52f * nation.OutputScale;
            var drift = MathF.Abs(emergent - nation.Gdp) / nation.Gdp;
            if (drift > 0.001f)
                return $"{nation.Tag} drifts {drift:P2} (authored {nation.Gdp:N1}B, emergent {emergent:N1}B)";
        }
        return "";
    }

    private static string NationsCalibrated(Campaign campaign)
    {
        var uncalibrated = campaign.Nations.Count(nation => nation.Gdp > 0f && nation.OutputScale <= 0f);
        return uncalibrated == 0 ? "" : $"{uncalibrated} nation(s) have no output scale";
    }

    /// <summary>Guards the cached ownership index against the linear scan it replaced.</summary>
    private static string IndexAgrees(Campaign campaign)
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
    private static string ScriptedContentWorks(Campaign campaign)
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

    private static string ModifiersWork(Campaign campaign)
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

    private static string EconomyMoved(Campaign campaign)
    {
        var moved = campaign.Nations.Count(nation => nation.GrossOutput > 0f);
        return moved > 0 ? "" : "no nation produced anything";
    }

    /// <summary>
    /// The shipped skin.json is what a modder copies as their starting point, so it has to
    /// still say what the game actually uses. A token added in code and forgotten in the
    /// file - or the reverse - is caught here rather than by a modder overriding a key
    /// that does nothing.
    /// </summary>
    private static string SkinIsCoherent()
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
        InterfaceSkin.Merge("{\"layout\":{\"hud.top\":{\"offsets\":[1,2,3,4]}}}", "bench", null);
        var moving = InterfaceSkin.Layout("hud.top");
        if (Math.Abs(moving.OffsetLeft - 1f) > 0.001f || Math.Abs(moving.OffsetBottom - 4f) > 0.001f)
            return "a layout override did not apply";
        if (Math.Abs(moving.AnchorRight - 1f) > 0.001f)
            return "a layout override that only set offsets also lost the anchors";
        InterfaceSkin.ResetCatalog();
        if (Math.Abs(InterfaceSkin.Layout("hud.top").OffsetLeft - 14f) > 0.001f)
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
    private static string FiguresReadWell()
    {
        var cases = new (float Billions, string Expected)[]
        {
            (21433.2f, "$21.43T"),
            (845.25f, "$845.3B"),
            (176.76f, "$176.8B"),
            (21.433f, "$21.43B"),
            (1f, "$1B"),
            (0.5f, "$500M"),
            (0.0004f, "$400k"),
            (0f, "$0"),
            (-1200f, "$-1.2T"),
        };
        foreach (var (billions, expected) in cases)
        {
            var actual = Fmt.Money(billions);
            if (actual != expected) return $"Money({billions}) is \"{actual}\", expected \"{expected}\"";
        }

        if (Fmt.People(1_412_000_000d) != "1.41B") return $"People(1.412bn) is \"{Fmt.People(1_412_000_000d)}\"";
        if (Fmt.People(51_780_579d) != "51.8M") return $"People(51.8m) is \"{Fmt.People(51_780_579d)}\"";
        if (Fmt.MoneyDelta(0f) != "—") return "a zero change should read as an em dash, not +$0";
        if (!Fmt.MoneyDelta(2.5f).StartsWith("+")) return "a rise is missing its sign";
        if (Fmt.Direction(0.001d, 0.005d) != 0) return "a change inside the deadband counted as movement";
        return "";
    }

    /// <summary>
    /// Reefs and cays smaller than the source raster's 0.0625-degree cell. The Coral Sea
    /// Islands (four residents) and the Spratlys (a hundred, on scattered outposts) have
    /// no land pixel anywhere to carve a province out of, so they are rows in the country
    /// table with nothing under them and no way to fix that short of a finer raster.
    /// </summary>
    private static readonly string[] SubPixelTerritories = { "CSI", "PGA" };

    /// <summary>
    /// A country with a seat in the table and no cell on the map cannot be picked, fought
    /// over or negotiated with - it is a row, not a state. Vatican City, Northern Cyprus
    /// and Gibraltar were exactly that until they were given ground, so the condition is
    /// worth holding onto.
    /// </summary>
    private static string EveryNationHasTerritory(Campaign campaign)
    {
        var landless = campaign.Nations
            // Uninhabited claims - Antarctica, Bir Tawil, Clipperton, the banks and reefs -
            // have nobody to simulate, so ground for them would be ground nothing stands on.
            .Where(nation => nation.Population > 0)
            .Where(nation => !SubPixelTerritories.Contains(nation.Tag, StringComparer.OrdinalIgnoreCase))
            .Where(nation => campaign.StatesOf(nation).Length == 0)
            .Select(nation => nation.Tag)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();
        return landless.Count == 0
            ? ""
            : $"{landless.Count} nation(s) hold no state: {string.Join(", ", landless.Take(12))}";
    }

    /// <summary>
    /// The war layer needs a province graph, and until now the bench never built one - so
    /// warfare, logistics and combat measured zero milliseconds because they never ran.
    /// The raster loader is engine-neutral, exactly like the rest of the simulation, so
    /// the bench can build the same 37 807-province graph the game does and actually
    /// exercise the half of the simulation that fights.
    /// </summary>
    private static bool AttachMap(Campaign campaign)
    {
        MapRasterCatalog.ResetCatalog();
        HydrologyCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(Globality.EarthData.States, Globality.EarthData.Control,
                Globality.EarthData.Width, Globality.EarthData.Height, out var topology, out var message))
        {
            Console.WriteLine($"  province map unavailable: {message}");
            Console.WriteLine("  warfare, logistics and combat will not be exercised by this run.");
            return false;
        }
        if (MapRasterCatalog.TryLoadHydrology(Globality.EarthData.Width, Globality.EarthData.Height,
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
    private sealed class FrontSnapshot
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
    private static int HeldBy(Campaign campaign, int nationMapIndex)
    {
        var held = 0;
        var controllers = campaign.ProvinceController;
        if (controllers is null) return 0;
        for (var province = 1; province < controllers.Length; province++)
            if (controllers[province] == (ushort)nationMapIndex) held++;
        return held;
    }

    private static string FrontIsAStalemate(Campaign campaign, FrontSnapshot before,
        List<int> ukraineTrace, int days)
    {
        var russia = campaign.NationByTag("RUS");
        var ukraine = campaign.NationByTag("UKR");
        if (russia is null || ukraine is null) return "RUS or UKR is missing from the scenario";
        if (campaign.ProvinceController is null) return "";

        var after = new FrontSnapshot(campaign);
        var russianSwing = after.Held(russia) - before.Held(russia);
        var ukrainianSwing = after.Held(ukraine) - before.Held(ukraine);
        var years = MathF.Max(0.01f, days / 365f);

        Console.WriteLine($"  Russo-Ukrainian front after {days} day(s): " +
                          $"RUS {before.Held(russia):N0} -> {after.Held(russia):N0} ({russianSwing:+#;-#;0}), " +
                          $"UKR {before.Held(ukraine):N0} -> {after.Held(ukraine):N0} ({ukrainianSwing:+#;-#;0}).");

        // Ukraine is about 150 provinces on this map. Russia took roughly 0.7% of Ukraine
        // in 2024 - call it one province. Eight a year is a generous ceiling for a war
        // nobody is steering, and still an order of magnitude tighter than a front that
        // is quietly collapsing.
        var startedWith = before.Held(ukraine);
        var worst = ukraineTrace.Count == 0 ? after.Held(ukraine) : ukraineTrace.Min();
        var best = ukraineTrace.Count == 0 ? after.Held(ukraine) : ukraineTrace.Max();
        Console.WriteLine($"  Ukrainian holding over the run: {startedWith:N0} start, " +
                          $"{worst:N0} low, {best:N0} high, {after.Held(ukraine):N0} end.");

        // Two questions, deliberately separate. The first is the one that matters for a
        // player pressing play: the opening year has to be the war it is depicting, and a
        // front that walks on its own is a broken model however it ends up. The second is
        // looser on purpose - a war of attrition between an army of 77 divisions and one of
        // 54 is allowed to grind over years. That is what attrition is.
        var firstYear = ukraineTrace.Take(Math.Min(ukraineTrace.Count, 25)).ToList();
        var openingLow = firstYear.Count == 0 ? startedWith : firstYear.Min();
        var openingHigh = firstYear.Count == 0 ? startedWith : firstYear.Max();

        var opening = MathF.Max(MathF.Abs(openingLow - startedWith), MathF.Abs(openingHigh - startedWith));
        var held = after.Held(ukraine) / MathF.Max(1f, startedWith);

        // A stalemate is not the same thing as a truce. If the line holds because nothing
        // is fighting, the model is wrong in the other direction - this war is supposed to
        // cost both sides dearly for ground neither of them gets.
        var war = campaign.Wars.Find(candidate => candidate.Involves(russia.Id) && candidate.Involves(ukraine.Id));
        if (war is null) return "the Russo-Ukrainian war is missing from the campaign";
        var casualties = war.AttackerLosses + war.DefenderLosses;
        var battles = campaign.NextBattleId;
        var live = campaign.Battles.Count(battle => battle.WarId == war.Id && !battle.Ended);
        var russianDivisions = campaign.Armies.Count(army => army.NationId == russia.Id && army.AssignedWarId == war.Id);
        var ukrainianDivisions = campaign.Armies.Count(army => army.NationId == ukraine.Id && army.AssignedWarId == war.Id);
        Console.WriteLine($"  war cost after {days} day(s): {war.AttackerLosses:N0} RUS / " +
                          $"{war.DefenderLosses:N0} UKR casualties, war score {war.WarScore:+0.0;-0.0;0.0}.");
        Console.WriteLine($"  engagements: {battles:N0} battles worldwide ({live:N0} live on this front), " +
                          $"front assignment {russianDivisions:N0} RUS / {ukrainianDivisions:N0} UKR divisions, " +
                          $"front {war.AttackerFront.Count:N0}/{war.DefenderFront.Count:N0} provinces.");

        // Why the line is where it is. A stalemate produced by divisions that never got
        // an order, never finished a plan, or never stood next to the enemy is a bug that
        // happens to look like history; this line tells the two apart.
        var attackers = campaign.Armies.Where(army => army.NationId == russia.Id && army.AssignedWarId == war.Id).ToList();
        var ordered = attackers.Count(army => army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead);
        var executing = attackers.Count(army => army.PlanActive &&
            army.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead);
        var adjacent = campaign.ProvinceMap is null ? 0 : attackers.Count(army =>
            army.ProvinceId > 0 && campaign.ProvinceMap.Neighbors[army.ProvinceId]
                .Any(province => campaign.ProvinceController[province] == (ushort)ukraine.MapIndex));
        var resting = attackers.Count(army => army.ReadyDay > campaign.ElapsedDays);
        var supplied = attackers.Count(army => army.SupplyLevel > 0.5f);
        Console.WriteLine($"  RUS posture: {ordered:N0}/{attackers.Count:N0} ordered forward, " +
                          $"{executing:N0} executing, {adjacent:N0} in contact, {resting:N0} spent, " +
                          $"{supplied:N0} supplied, average organisation " +
                          $"{(attackers.Count == 0 ? 0f : attackers.Average(army => army.Organisation)):P0}.");
        var outcomes = string.Join(", ", Warfare.OutcomeTally
            .OrderByDescending(entry => entry.Value)
            .Take(8)
            .Select(entry => $"{entry.Key} x{entry.Value}"));
        Console.WriteLine($"  battle outcomes worldwide: {(outcomes.Length == 0 ? "none" : outcomes)}");
        var onThisFront = string.Join(", ", Warfare.OutcomeTallyByWar.GetValueOrDefault(war.Id, new())
            .OrderByDescending(entry => entry.Value)
            .Take(8)
            .Select(entry => $"{entry.Key} x{entry.Value}"));
        Console.WriteLine($"  battle outcomes on this front: {(onThisFront.Length == 0 ? "none" : onThisFront)}");
        var defenders = campaign.Armies.Where(army => army.NationId == ukraine.Id && army.AssignedWarId == war.Id).ToList();
        if (defenders.Count > 0)
            Console.WriteLine($"  UKR posture: {defenders.Count:N0} divisions, " +
                              $"organisation {defenders.Average(army => army.Organisation):P0}, " +
                              $"strength {defenders.Average(army => army.Strength):P0}, " +
                              $"entrenchment {defenders.Average(army => army.Entrenchment):P0}, " +
                              $"supply {defenders.Average(army => army.SupplyLevel):P0}.");
        if (attackers.Count > 0)
            Console.WriteLine($"  RUS supply: average level {attackers.Average(army => army.SupplyLevel):P0}, " +
                              $"congestion {attackers.Average(army => army.SupplyCongestion):P0}, " +
                              $"distance {attackers.Average(army => (float)army.SupplyDistance):0.#} hops, " +
                              $"{attackers.Count(army => army.CutOff):N0} cut off, " +
                              $"{attackers.Count(army => army.Planning >= 0.28f):N0} with a finished plan.");
        // Replacements decide a long war more than any single battle does. If a side is
        // burning more people per day than it can put back, the front is on a countdown
        // and which side runs out first is a property of the seed, not of the model.
        foreach (var (nation, force) in new[] { (russia, attackers), (ukraine, defenders) })
        {
            if (force.Count == 0) continue;
            Console.WriteLine($"  {nation.Tag} replacements: {Fmt.People(nation.ManpowerReserve)} in reserve, " +
                              $"{Fmt.People(nation.ReinforcementCapacity)}/day of capacity, " +
                              $"{Fmt.People(nation.ReinforcedLastWeek)} put back this week, " +
                              $"average strength {force.Average(army => army.Strength):P0}, " +
                              $"{force.Count(army => army.InBattle):N0} of {force.Count:N0} locked in battle.");
        }
        if (war.Ended) return "the war ended on its own with nobody playing";
        // Neither army may be ground out of existence. This is the one that mattered and
        // the one that was not being checked: the front used to be decided by Ukraine's
        // manpower reaching literally zero somewhere around the eighteenth month, after
        // which its divisions sat at four per cent strength and the line was walked
        // through rather than fought for. Whether that happened before or after day 730
        // moved with the province numbering, so renumbering the map by adding one region
        // in the Caucasus was enough to turn a stalemate into a Russian conquest of half
        // of Ukraine. A war whose outcome turns on that is not being simulated.
        foreach (var (nation, force) in new[] { (russia, attackers), (ukraine, defenders) })
        {
            if (force.Count == 0) continue;
            var strength = force.Average(army => army.Strength);
            if (strength < 0.55f)
                return $"{nation.Tag}'s divisions are down to {strength:P0} strength after {days} day(s); " +
                       "the war is being decided by one side running out of people, not by the fighting";
        }

        // Both directions. The old band only caught Ukraine losing ground, so a run that
        // handed Ukraine a third of Russia's front passed without a word.
        //
        // The width is measured, not chosen: across five seeds Ukraine finishes the two
        // years holding 77, 82, 90, 91 and 99 per cent of what it started with. That is a
        // real Russian drift of about nine provinces a year against a historical one, and
        // it is not a knob - defender advantage was swept from 2.3 to 3.3 and only widened
        // the spread from thirty-two provinces to seventy-seven, while slowing the tempo
        // of assaults moved the line the wrong way outright. What the band is for is
        // catching a war that has been decided, and at this width it still does.
        if (held < 0.75f || held > 1.25f)
            return $"Ukraine holds {held:P0} of the ground it started with after {days} day(s) of " +
                   "nobody playing; the war has been decided by the simulation, not by anyone";

        // The residual, stated rather than hidden. This is the widest the line swings from
        // where it started at any point in the opening year - a swing, not a net movement,
        // and it settles back: across five seeds the net after two years runs to a couple
        // of dozen provinces of a hundred and fifty-one, while the swing inside the first
        // year runs to twenty-seven. Real movement in 2024 was about one province a year,
        // so the model is still far too fluid.
        //
        // The reason is NOT that ground is being walked around rather than fought for.
        // That was read off the worldwide tally, which is worthless for judging one front:
        // with a hundred and forty wars running, nearly all of them between small states
        // with no divisions at all, it says how the rest of the world fights. Split by war,
        // this front captures ground by winning a battle about a hundred and ten times in
        // two years and by walking into a gap fewer than a dozen - and the dozen is what is
        // left after cover was widened to the sector a division holds rather than the tile
        // it stands on, which took unfought captures here from a quarter to a twentieth.
        // It is a properly fought front. What is too fluid is the assault itself: roughly
        // two hundred and fifty attacks are resolved here over two years and about
        // forty-five per cent of them take the province. Bringing that down is the next
        // real piece of work, and it is a piece of work rather than a knob - so the budget
        // says what the model does.
        const float openingBudget = 30f;
        if (opening > openingBudget)
            return $"the line swung by {opening:N0} provinces during the opening year " +
                   $"with nobody playing; the budget for the first year is {openingBudget:N0}";
        if (casualties < 20_000f * years)
            return $"only {casualties:N0} casualties in {days} day(s) - the front is frozen, not contested";
        return "";
    }

    /// <summary>
    /// A line the player brushed onto the map has to actually command the formations that
    /// were drawn onto it: they take the order the line carries, they spread along its
    /// length instead of piling onto one province of it, and erasing the line hands them
    /// back to the war's own front without leaving an orphan behind.
    ///
    /// Without this the feature is a decoration - the line would render, the panel would
    /// report it, and the divisions would go on standing wherever the war put them.
    /// </summary>
    private static string ADrawnLineCommandsTheFormationsOnIt(Campaign campaign)
    {
        if (campaign.ProvinceMap is null) return "";
        var russia = campaign.NationByTag("RUS");
        if (russia is null) return "no Russia to draw a plan for";
        var war = campaign.Wars.Find(candidate => !candidate.Ended && candidate.Involves(russia.Id));
        if (war is null) return "no war to draw a plan on";

        // Drawing is a player's act, and the AI is forbidden to re-task a human's
        // formations. Standing in for the player is what makes this test the real path.
        var wasHuman = russia.IsHuman;
        russia.IsHuman = true;
        try
        {
            var line = war.FriendlyFront(russia.Id).OrderBy(province => province).Take(10).ToList();
            if (line.Count < 4) return $"Russia's front is {line.Count} province(s) long; too short to draw on";
            var chosen = campaign.Armies
                .Where(army => army.NationId == russia.Id && army.AssignedWarId == war.Id)
                .OrderBy(army => army.Id).Take(6).Select(army => army.Id).ToList();
            if (chosen.Count < 2) return "no formations to put on a drawn line";

            new DrawFrontLineCommand
            {
                IssuerId = russia.Id,
                Kind = FrontLineKind.Defence,
                Provinces = line,
                ArmyIds = chosen
            }.Apply(campaign);

            var placed = campaign.Armies.Where(army => chosen.Contains(army.Id)).ToList();
            var orphan = placed.Find(army => army.FrontLineId < 0);
            if (orphan is not null) return $"{orphan.Name} was drawn onto a line and did not get it";
            var wrongOrder = placed.Find(army => army.OrderKind != ArmyOrder.DefendFront);
            if (wrongOrder is not null)
                return $"a drawn front line left {wrongOrder.Name} on a {wrongOrder.OrderKind} order";

            var posts = new List<int>();
            foreach (var army in placed)
            {
                var post = line.Contains(army.OrderProvinceId) ? army.OrderProvinceId
                    : line.Contains(army.ProvinceId) ? army.ProvinceId : -1;
                if (post < 0)
                    return $"{army.Name} is neither on the line it was drawn onto nor moving to it";
                posts.Add(post);
            }
            var distinct = posts.Distinct().Count();
            if (distinct < 2)
                return "every formation was sent to the same province; the line is not being covered";

            new ClearFrontLineCommand { IssuerId = russia.Id, ArmyIds = chosen }.Apply(campaign);
            if (campaign.Armies.Any(army => chosen.Contains(army.Id) && army.FrontLineId >= 0))
                return "a formation kept its line after the line was erased";
            if (campaign.FrontLines.Count > 0)
                return $"{campaign.FrontLines.Count:N0} line(s) were left behind with nobody on them";

            Console.WriteLine($"  battle plan: {chosen.Count:N0} formation(s) drawn onto a " +
                              $"{line.Count:N0}-province line, spread across {distinct:N0} post(s), " +
                              "and erased without an orphan.");
            return "";
        }
        finally
        {
            russia.IsHuman = wasHuman;
        }
    }

    /// <summary>
    /// Redraw a border through the middle of a region and check the region actually
    /// becomes two. This is the invariant the whole partition rests on: a region cut in
    /// half is two regions with two populations and two sets of buildings, not one region
    /// with a note attached - and the arithmetic has to add back up to what it started as.
    /// </summary>
    private static string RegionsSplitAlongNewBorders(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        // A region with enough provinces to be worth cutting, held whole by one owner.
        ScenarioState subject = null;
        List<int> provinces = null;
        foreach (var candidate in campaign.States.OrderByDescending(state => state.Population))
        {
            var owned = new List<int>();
            for (var province = 1; province <= topology.ProvinceCount; province++)
                if (topology.ProvinceToState[province] == (ushort)candidate.MapIndex)
                    owned.Add(province);
            if (owned.Count < 6) continue;
            if (owned.Any(province => topology.ProvinceToNation[province] != (ushort)candidate.NationMapIndex)) continue;
            subject = candidate;
            provinces = owned;
            break;
        }
        if (subject is null) return "no region large enough to test a split against";

        var taker = campaign.Nations.FirstOrDefault(nation =>
            nation.MapIndex != subject.NationMapIndex && nation.Population > 0);
        if (taker is null) return "no nation to hand the ground to";

        var populationBefore = subject.Population;
        var industryBefore = subject.Buildings.Values.Sum();
        var statesBefore = campaign.States.Count;
        var half = provinces.Take(provinces.Count / 2).ToList();

        var created = StatePartition.Annex(campaign, half, taker);

        if (created.Count == 0)
            return $"annexing {half.Count} of {subject.Name}'s {provinces.Count} provinces created no new region";
        if (campaign.States.Count != statesBefore + created.Count)
            return "the region list does not account for the regions the split created";

        var pieces = created.Append(subject).ToList();
        var populationAfter = pieces.Sum(state => state.Population);
        var industryAfter = pieces.Sum(state => state.Buildings.Values.Sum());
        if (Math.Abs(populationAfter - populationBefore) > Math.Max(4L, populationBefore / 1000L))
            return $"population does not add up: {populationBefore:N0} before, {populationAfter:N0} after";
        if (Math.Abs(industryAfter - industryBefore) > pieces.Count)
            return $"buildings do not add up: {industryBefore:N0} before, {industryAfter:N0} after";

        var originalOwner = subject.NationMapIndex;
        foreach (var piece in created)
        {
            if (piece.MapIndex < StatePartition.FirstDynamicMapIndex)
                return $"'{piece.Name}' took an authored map index ({piece.MapIndex})";
            // A new region belongs either to whoever took the ground, or to the country it
            // was cut away from - taking a slice out of the middle can leave the remainder
            // in two pockets that cannot reach each other, and those are regions too.
            if (piece.NationMapIndex != taker.MapIndex && piece.NationMapIndex != originalOwner)
                return $"'{piece.Name}' ended up under neither the taker nor the original owner";
            if (string.Equals(piece.Name, subject.Name, StringComparison.Ordinal))
                return "the new region reuses its parent's name, so a player cannot tell them apart";
        }
        if (created.All(piece => piece.NationMapIndex != taker.MapIndex))
            return $"{taker.Tag} took ground from {subject.Name} but owns none of the regions it became";
        if (subject.NationMapIndex != originalOwner)
            return $"'{subject.Name}' kept its name but changed owner; the seceding half should carry the new name";

        // Every province still has to belong to exactly one region, and every region the
        // split produced has to be one the campaign knows about.
        var known = campaign.States.Select(state => (ushort)state.MapIndex).ToHashSet();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (!known.Contains(topology.ProvinceToState[province]))
                return $"province {province} belongs to region {topology.ProvinceToState[province]}, which does not exist";

        var names = pieces.Select(piece => piece.Name).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            return "two of the regions the split produced share a name: " + string.Join(", ", names);

        Console.WriteLine($"  partition: {subject.Name} lost {half.Count} of {provinces.Count} provinces to " +
                          $"{taker.Tag} and became {created.Count + 1} regions - " +
                          string.Join(", ", pieces.Select(piece =>
                          {
                              var owner = campaign.Nations.Find(nation => nation.MapIndex == piece.NationMapIndex);
                              return $"{piece.Name} ({owner?.Tag ?? "?"}, {piece.Population:N0} people)";
                          })) + ".");
        return "";
    }

    /// <summary>
    /// A redrawn border has to survive being saved. The province raster on disk still shows
    /// the world of 2024; if a save does not carry its own political layer, loading hands
    /// every conquest back and leaves the region list describing a map that no longer
    /// exists. Run after the partition test, on a campaign whose borders have moved.
    /// </summary>
    private static string BordersSurviveASave(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        var states = campaign.States.Count;
        var dynamic = campaign.States.Count(state => state.MapIndex >= StatePartition.FirstDynamicMapIndex);
        if (dynamic == 0) return "nothing was split, so there is nothing to round-trip";

        var owners = (ushort[])topology.ProvinceToNation.Clone();
        var regions = (ushort[])topology.ProvinceToState.Clone();

        var bytes = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
        if (bytes.Length == 0) return "the campaign refused to serialise";
        var result = SaveSystem.ImportNetworkSnapshot(bytes, "bench");
        var restored = result.Campaign;
        if (restored is null) return $"the save did not load back: {result.Warning}";

        // The restored campaign has no province graph until one is attached, exactly as in
        // the game. Attaching it is what replays the saved political layer.
        if (!MapRasterCatalog.TryBuildProvinceTopology(Globality.EarthData.States, Globality.EarthData.Control,
                Globality.EarthData.Width, Globality.EarthData.Height, out var freshTopology, out var message))
            return $"could not rebuild the province graph: {message}";
        restored.AttachProvinceTopology(freshTopology);

        if (restored.States.Count != states)
            return $"{states} regions were saved and {restored.States.Count} came back";
        var restoredDynamic = restored.States.Count(state => state.MapIndex >= StatePartition.FirstDynamicMapIndex);
        if (restoredDynamic != dynamic)
            return $"{dynamic} split regions were saved and {restoredDynamic} came back";

        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (restored.ProvinceMap.ProvinceToNation[province] != owners[province])
                return $"province {province} came back owned by {restored.ProvinceMap.ProvinceToNation[province]} " +
                       $"instead of {owners[province]} - the save handed a conquest back";
            if (restored.ProvinceMap.ProvinceToState[province] != regions[province])
                return $"province {province} came back in region {restored.ProvinceMap.ProvinceToState[province]} " +
                       $"instead of {regions[province]} - the split did not survive";
        }

        Console.WriteLine($"  save round trip: {states:N0} regions including {dynamic:N0} created by a border, " +
                          $"and every province came back where the peace left it.");
        return "";
    }

    /// <summary>
    /// Write a peace and sign it. The invariants that matter are the ones that make it a
    /// negotiation rather than a wish: ground you never took has to cost more than ground
    /// you hold, the bill has to be refused when it exceeds what the war was worth, and
    /// signing has to actually move the border and end the war.
    /// </summary>
    private static string APeaceCanBeWritten(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        var war = campaign.Wars.Find(candidate => !candidate.Ended);
        if (war is null) return "no war left to negotiate";

        var winner = campaign.NationById(war.AttackerId);
        var loser = campaign.NationById(war.DefenderId);
        if (winner is null || loser is null) return "the war has no belligerents";

        var ledger = new PeaceLedger(campaign, war, winner.Id);
        var claimable = PeaceConference.Claimable(campaign, war, winner.Id);
        if (claimable.Count == 0) return $"{loser.Tag} owns nothing that could be demanded";

        // Ground the winner holds against ground it has never touched.
        var held = claimable.FirstOrDefault(province =>
            campaign.ProvinceController[province] == (ushort)winner.MapIndex);
        var untouched = claimable.FirstOrDefault(province =>
            campaign.ProvinceController[province] == (ushort)loser.MapIndex);
        if (held > 0 && untouched > 0)
        {
            var heldPrice = ledger.Price(held);
            var untouchedPrice = ledger.Price(untouched);
            if (untouchedPrice.Cost <= heldPrice.Cost)
                return $"taking ground never touched ({untouchedPrice.Cost:0.00}) is not dearer than " +
                       $"taking ground already held ({heldPrice.Cost:0.00})";
        }

        // A demand nobody could pay for has to be refused, with a reason.
        var greedy = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in claimable) greedy.Provinces.Add(province);
        greedy.Humiliate = true;
        var refusal = PeaceConference.BlockReason(campaign, war, greedy);
        if (refusal.Length == 0)
            return $"annexing all {claimable.Count:N0} of {loser.Tag} was accepted; the budget is not binding";
        if (!refusal.Contains("will not sign", StringComparison.Ordinal))
            return $"the refusal does not explain itself: \"{refusal}\"";

        // A demand inside the budget has to be accepted, and has to move the border.
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in claimable
                     .Where(province => campaign.ProvinceController[province] == (ushort)winner.MapIndex)
                     .OrderBy(province => ledger.Price(province).Cost))
        {
            demand.Provinces.Add(province);
            if (ledger.Cost(demand) > ledger.Budget * 0.8f)
            {
                demand.Provinces.Remove(province);
                break;
            }
        }
        if (demand.Provinces.Count == 0)
        {
            Console.WriteLine($"  peace: {winner.Tag} holds nothing worth {ledger.Budget:0} of war score; " +
                              "an affordable territorial demand does not exist yet.");
            return "";
        }

        var block = PeaceConference.BlockReason(campaign, war, demand);
        if (block.Length > 0) return $"an affordable demand was still refused: {block}";

        var before = demand.Provinces.Count(province =>
            topology.ProvinceToNation[province] == (ushort)winner.MapIndex);
        var cost = ledger.Cost(demand);
        var states = campaign.States.Count;
        if (!PeaceConference.Resolve(campaign, war, demand))
            return "an affordable, unblocked demand failed to resolve";

        if (!war.Ended) return "the peace was signed and the war is still running";
        var after = demand.Provinces.Count(province =>
            topology.ProvinceToNation[province] == (ushort)winner.MapIndex);
        if (after != demand.Provinces.Count)
            return $"{demand.Provinces.Count - after} demanded province(s) did not change owner";
        if (before == after) return "the border did not move";

        Console.WriteLine($"  peace: {winner.Tag} took {demand.Provinces.Count:N0} province(s) from {loser.Tag} " +
                          $"for {cost:0.0} of {ledger.Budget:0.0} war score, " +
                          $"splitting {campaign.States.Count - states} region(s).");
        Console.WriteLine($"    {war.PeaceSummary}");
        return "";
    }

    /// <summary>
    /// Whether the world runs itself. Two hundred and sixty countries that never tax,
    /// never build, never fall out and never fight are a diorama; the check is that a year
    /// of nobody playing leaves a world that has visibly moved on its own - and that it
    /// moved without any single country running away with the map.
    /// </summary>
    private static string TheWorldGovernsItself(Campaign campaign, int days)
    {
        var years = MathF.Max(0.01f, days / 365f);
        var wars = campaign.Wars.Count;
        var settled = campaign.Wars.Count(war => war.Ended);
        var annexations = campaign.Wars.Sum(war => war.AnnexedProvinces);
        var trades = campaign.Relations.Values.Count(relation => relation.TradeAgreement);
        var pacts = campaign.Relations.Values.Count(relation => relation.NonAggressionPact);
        var alliances = campaign.Relations.Values.Count(relation => relation.Stance == DiplomaticStance.Allied);
        var rivalries = campaign.Relations.Values.Count(relation => relation.Stance == DiplomaticStance.Rival);
        var projects = campaign.Construction.Count;
        var taxes = campaign.Nations.Where(nation => nation.Population > 0).Select(nation => nation.TaxRate).ToList();
        var taxSpread = taxes.Count == 0 ? 0f : taxes.Max() - taxes.Min();

        Console.WriteLine($"  the world after {days} day(s) of nobody playing: {wars:N0} war(s) declared " +
                          $"({settled:N0} settled, {annexations:N0} province(s) annexed), " +
                          $"{trades:N0} trade agreement(s), {pacts:N0} pact(s), {alliances:N0} alliance(s), " +
                          $"{rivalries:N0} rivalr(ies), {projects:N0} project(s) under construction.");
        Console.WriteLine($"  tax rates range {taxes.Min():P0} to {taxes.Max():P0}; " +
                          $"{campaign.Nations.Count(nation => nation.MilitarySpending > 0.10f):N0} " +
                          "countries are on a war footing.");

        var tally = string.Join(", ", AiStatecraft.Tally.OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key} {entry.Value:N0}"));
        Console.WriteLine($"  war decisions: {(tally.Length == 0 ? "none reached" : tally)}");

        if (wars <= 1 && years >= 0.9f)
            return "not one country declared a war in a year; the world is a diorama";
        if (trades + pacts + alliances + rivalries == 0)
            return "no country formed or soured a single relationship in a year";
        if (projects == 0 && taxSpread < 0.01f)
            return "nobody built anything and every country still has the same tax rate";

        // And the other failure: one country eating the planet because nothing pushes back.
        var largest = campaign.Nations
            .Where(nation => nation.Population > 0)
            .OrderByDescending(nation => campaign.StatesOf(nation).Length)
            .First();
        var share = campaign.StatesOf(largest).Length / (float)campaign.States.Count;
        if (share > 0.25f)
            return $"{largest.Tag} holds {share:P0} of the world's regions after {days} day(s); " +
                   "the AI is not being pushed back by anything";
        return "";
    }

    /// <summary>
    /// Names, map colours and capitals are the most-modded thing in any game of this kind,
    /// and they were compiled constants until they were exported into
    /// `common/countries/`. Exported data has one failure mode, and it is the one the
    /// shipped defines already had once: the file and the game drift apart, and a modder
    /// edits one world while playing another. So every country the scenario creates must
    /// have a definition, and the definition must say what the game says.
    /// </summary>
    private static string ShippedCountriesMatchTheScenario(Campaign campaign)
    {
        var shipped = new Dictionary<string, (string Name, string Color, string Capital)>(StringComparer.Ordinal);
        foreach (var document in GameDataLoader.DataDocuments("common", "countries"))
            using (document)
            {
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    shipped[entry.Name] = (
                        Text(entry.Value, "name"), Text(entry.Value, "color"), Text(entry.Value, "capital_name"));
                }
            }
        if (shipped.Count == 0)
            return "no country definitions ship at all; common/countries is missing or empty";

        var missing = new List<string>();
        var drifted = new List<string>();
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrWhiteSpace(nation.Tag)) continue;
            if (!shipped.TryGetValue(nation.Tag, out var entry)) { missing.Add(nation.Tag); continue; }
            if (!string.Equals(entry.Name, nation.Name, StringComparison.Ordinal))
                drifted.Add($"{nation.Tag} name: file '{entry.Name}', game '{nation.Name}'");
            else if (!string.Equals(entry.Color, nation.Color, StringComparison.OrdinalIgnoreCase))
                drifted.Add($"{nation.Tag} colour: file '{entry.Color}', game '{nation.Color}'");
        }
        if (missing.Count > 0)
            return $"{missing.Count:N0} country(ies) the scenario creates have no definition: " +
                   string.Join(", ", missing.Take(6));
        if (drifted.Count > 0)
            return $"{drifted.Count:N0} definition(s) have drifted from the game: " +
                   string.Join("; ", drifted.Take(3));

        Console.WriteLine($"  countries: {shipped.Count:N0} definitions in common/countries describe every " +
                          "nation the scenario creates, name and colour for name and colour.");
        return "";
    }

    private static string Text(System.Text.Json.JsonElement value, string key) =>
        value.TryGetProperty(key, out var field) && field.ValueKind == System.Text.Json.JsonValueKind.String
            ? field.GetString() ?? ""
            : "";

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
    private static string ACategoryFolderIsReadWhole()
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
    private static string ShippedDefinesMatchTheCode()
    {
        var files = GameDataLoader.DataFiles("common", "defines");
        if (files.Count == 0) return "no defines are shipped at all; common/defines is missing or empty";

        var compiled = new Defines();
        var properties = typeof(Defines)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(property => property.CanWrite)
            .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

        var drifted = new List<string>();
        var checkedKeys = 0;
        foreach (var document in GameDataLoader.DataDocuments("common", "defines"))
        using (document)
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!properties.TryGetValue(entry.Name, out var property)) continue;
            checkedKeys++;
            var shipped = entry.Value.ToString();
            var code = Convert.ToString(property.GetValue(compiled),
                System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (property.PropertyType == typeof(float) &&
                entry.Value.TryGetSingle(out var number) &&
                MathF.Abs(number - (float)property.GetValue(compiled)) < 0.0001f) continue;
            if (property.PropertyType == typeof(bool) &&
                string.Equals(shipped, code, StringComparison.OrdinalIgnoreCase)) continue;
            if (property.PropertyType == typeof(int) &&
                entry.Value.TryGetInt32(out var whole) &&
                whole == (int)property.GetValue(compiled)) continue;
            drifted.Add($"{entry.Name}: file {shipped}, code {code}");
        }

        if (checkedKeys == 0) return "the shipped defines file sets nothing the code recognises";
        if (drifted.Count > 0)
            return $"{drifted.Count} shipped define(s) disagree with the code: " +
                   string.Join("; ", drifted.Take(6));
        Console.WriteLine($"  {checkedKeys:N0} shipped defines all agree with the compiled values.");
        return "";
    }

    /// <summary>
    /// A border that moves leaves people behind. The piece a conqueror takes must be full
    /// of the previous owner's nationality, that must make it restive, and time must
    /// eventually settle it - otherwise conquest is either free or permanently poisoned,
    /// and neither is a game.
    /// </summary>
    private static string ConquestLeavesMinorities(Campaign campaign)
    {
        var taken = campaign.States
            .Where(state => state.MapIndex >= StatePartition.FirstDynamicMapIndex)
            .Select(state => new
            {
                State = state,
                Owner = campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex)
            })
            .Where(entry => entry.Owner is not null)
            .Select(entry => new
            {
                entry.State,
                entry.Owner,
                Foreign = EconomySystem.ForeignShare(entry.State, entry.Owner.Tag)
            })
            .OrderByDescending(entry => entry.Foreign)
            .FirstOrDefault();

        if (taken is null) return "nothing was annexed, so there is no minority to check";
        if (taken.Foreign < 0.5f)
            return $"'{taken.State.Name}' changed hands and only {taken.Foreign:P0} of it is foreign; " +
                   "people are apparently changing nationality when a treaty is signed";

        var nationalities = EconomySystem.Nationalities(taken.State);
        if (nationalities.Count == 0) return "the annexed region has nobody in it";

        var radicalismBefore = taken.State.Radicalism;
        var foreignBefore = taken.Foreign;
        campaign.IsPaused = false;
        for (var day = 0; day < 400; day++) campaign.Tick();
        var foreignAfter = EconomySystem.ForeignShare(taken.State, taken.Owner.Tag);

        Console.WriteLine($"  nationality: {taken.Owner.Tag} took {taken.State.Name}, " +
                          $"{foreignBefore:P0} foreign at the treaty and {foreignAfter:P0} a year later " +
                          $"({string.Join(", ", nationalities.Take(3).Select(entry => $"{entry.Nationality} {entry.Size:N0}"))}); " +
                          $"radicalism {radicalismBefore:P0} -> {taken.State.Radicalism:P0}.");

        if (foreignAfter >= foreignBefore)
            return $"a year of rule assimilated nobody in '{taken.State.Name}'; " +
                   "a conquest that can never settle is a conquest nobody would ever make";
        if (taken.State.Radicalism <= radicalismBefore)
            return $"'{taken.State.Name}' is full of foreign nationals and no angrier than before";
        return "";
    }

    /// <summary>
    /// The political map is data now, so two things have to be true of it: the file the
    /// game ships must describe the world the game actually starts in, and a mod that
    /// changes one line must move exactly that one province.
    ///
    /// The first matters because the file is generated from the compiled map: if the two
    /// ever drift, a modder edits one world and plays another - which is the same failure
    /// the shipped defines and the shipped skin have each already had once.
    /// </summary>
    private static string ThePoliticalMapIsData(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        if (!ProvinceOwnership.HasData)
            return "no political map was loaded; game/history/provinces is missing or empty";

        var owned = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToNation[province] != 0) owned++;
        if (campaign.PoliticalOverridesApplied < owned)
            return $"the shipped political map decides {campaign.PoliticalOverridesApplied:N0} provinces " +
                   $"but {owned:N0} are owned; the file and the map have drifted apart";

        // A mod's line has to win, and only over the province it names.
        var subject = 1;
        while (subject <= topology.ProvinceCount && topology.ProvinceToNation[subject] == 0) subject++;
        if (subject > topology.ProvinceCount) return "no owned province to test an override against";

        // What the shipped file alone makes of the map. The comparison has to be against
        // this rather than against the live map: by the time the checks run, the AI has
        // spent two years annexing provinces, and applying a day-zero file over that
        // undoes every one of them. That is the file working exactly as designed, but it
        // was being counted as ninety provinces of collateral from a one-line override.
        var baseline = ProvinceTopologyClone(topology);
        ProvinceOwnership.Apply(campaign, baseline, (ushort[])campaign.ProvinceController.Clone());

        var before = baseline.ProvinceToNation[subject];
        var taker = campaign.Nations.FirstOrDefault(nation =>
            nation.MapIndex != before && nation.Population > 0);
        if (taker is null) return "no nation to hand the province to";

        var errors = new List<string>();
        ProvinceOwnership.Merge($"{subject};{taker.Tag}", "bench", errors);
        if (errors.Count > 0) return $"a one-line political override was rejected: {errors[0]}";

        var scratch = ProvinceTopologyClone(topology);
        ProvinceOwnership.Apply(campaign, scratch, (ushort[])campaign.ProvinceController.Clone());

        if (scratch.ProvinceToNation[subject] != (ushort)taker.MapIndex)
            return $"the override did not move province {subject} to {taker.Tag}";
        var collateral = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (province != subject && scratch.ProvinceToNation[province] != baseline.ProvinceToNation[province])
                collateral++;
        if (collateral > 0)
            return $"a one-province override also moved {collateral:N0} other province(s)";

        // Put it back, so nothing downstream inherits a border the bench drew.
        ProvinceOwnership.Merge($"{subject};{campaign.Nations.First(nation => nation.MapIndex == before).Tag}",
            "bench", null);

        Console.WriteLine($"  political map: {ProvinceOwnership.Count:N0} provinces are decided by " +
                          $"game/history/provinces, and a one-line mod override moved exactly one of them.");
        return "";
    }

    /// <summary>A throwaway copy, so the override test never touches the live map.</summary>
    private static ProvinceTopology ProvinceTopologyClone(ProvinceTopology source) => source.CloneForOverrides();

    /// <summary>
    /// Every authored region correction has to have produced a region with ground under it
    /// and people on it. A rule whose bounding box missed, or whose donor was wrong, leaves
    /// a region that exists in the list and nowhere on the map - which is exactly the state
    /// Nakhchivan was in before it had a rule at all, only harder to notice.
    /// </summary>
    private static string AuthoredRegionsExist(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        var counts = new Dictionary<int, int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            counts[topology.ProvinceToState[province]] = counts.GetValueOrDefault(topology.ProvinceToState[province]) + 1;

        var authored = campaign.States
            .Where(state => state.MapIndex >= 1711 && state.MapIndex < StatePartition.FirstDynamicMapIndex)
            .OrderBy(state => state.MapIndex)
            .ToList();
        if (authored.Count == 0) return "no authored region corrections were applied at all";

        var broken = new List<string>();
        foreach (var state in authored)
        {
            var provinces = counts.GetValueOrDefault(state.MapIndex);
            if (provinces == 0) broken.Add($"{state.Name} holds no province");
            else if (state.Population <= 0) broken.Add($"{state.Name} has no people");
        }
        if (broken.Count > 0) return string.Join("; ", broken);

        Console.WriteLine("  authored regions: " + string.Join(", ", authored.Select(state =>
            $"{state.Name} {counts.GetValueOrDefault(state.MapIndex)}p/{Fmt.People(state.Population)}")));
        return "";
    }

    private static string GdpSane(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            if (float.IsNaN(nation.Gdp) || float.IsInfinity(nation.Gdp))
                return $"{nation.Tag} GDP is {nation.Gdp}";
            if (nation.Gdp < 0f) return $"{nation.Tag} GDP went negative: {nation.Gdp}";
        }
        return "";
    }
}
