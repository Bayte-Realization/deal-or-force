using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
        failures += Check("every province belongs to the country whose ground it is on",
            ProvincesSitWhereTheirOwnerIs(campaign));
        failures += Check("every nation starts the scenario owning ground", EveryNationHasTerritory(campaign));
        if (mapped)
            failures += Check("every authored region correction exists on the map",
                AuthoredRegionsExist(campaign));
        if (mapped)
            failures += Check("a lake is a place armies cannot walk on",
                LakesArePlacesArmiesCannotWalkOn(campaign));
        if (mapped)
            failures += Check("the compiled map matches the code that drew it",
                TheCompiledMapMatchesTheCodeThatDrewIt());
        failures += Check("every region knows who lives in it",
            EveryRegionKnowsWhoLivesInIt(campaign));
        failures += Check("a content pack is locked until it is owned",
            ContentPacksAreLockedUntilOwned());
        failures += Check("a country that states a figure about itself gets that figure",
            TheStatedStatisticsCameTrue(campaign));

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
        failures += Check("every picture the interface asks for has a name a mod can rebind",
            ArtworkIsNamedRatherThanSpelledOut());
        failures += Check("every sound the game makes is declared where a mod can reach it",
            AudioIsDeclaredRatherThanWiredIn());
        if (mapped)
            failures += Check("the political map is data a mod can override", ThePoliticalMapIsData(campaign));
        failures += Check("every explained number adds up to what the screen shows",
            BreakdownsAddUp(campaign));
        failures += Check("headline figures format the way a player reads them", FiguresReadWell());

        if (mapped)
            failures += Check("the Russo-Ukrainian front is still a stalemate",
                FrontIsAStalemate(campaign, frontBefore, ukraineTrace, days));

        failures += Check("the world governs itself", TheWorldGovernsItself(campaign, days));
        failures += Check("governments take decisions written in data",
            GovernmentsTakeScriptedDecisions(campaign, days));

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
                failures += Check("a navy is worth having", ANavyIsWorthHaving(sandbox));
                failures += Check("an army can be put ashore across water",
                    AnArmyCanBePutAshore(sandbox));
                failures += Check("an AI mounts a landing of its own",
                    AnAiMountsALanding(sandbox));
                failures += Check("a convoy caught at sea pays for it",
                    AConvoyCaughtAtSeaPaysForIt(sandbox));
                failures += Check("troops trained for a landing land better",
                    MarinesLandBetter(sandbox));
                failures += Check("a drawn line moves the formations put on it",
                    ADrawnLineCommandsTheFormationsOnIt(sandbox));
                failures += Check("a mod can rewrite a country's army, navy and typeface",
                    TheOrderOfBattleIsAModsToRewrite(sandbox));
                failures += Check("a country remembers why it dislikes another",
                    OpinionIsRememberedAndForgotten(sandbox));
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
    /// The player can invade. An AI that cannot is half a feature - the islands would be
    /// safe from everyone but the person playing, which is the same asymmetry the flat
    /// refusal used to be.
    ///
    /// Whether an AI *chooses* to declare a war across water is a separate matter of
    /// appetite, and rare on any given seed. What has to be true is that one which finds
    /// itself in such a war knows what to do: bring its fleet home, put troops on it, and
    /// sail. So the war is arranged, and the admiralty is left to work it out.
    /// </summary>
    private static string AnAiMountsALanding(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        var invader = campaign.NationByTag("GBR");
        if (invader is null) return "no navy to test with";
        var fleet = campaign.Fleets.Find(candidate => candidate.NationId == invader.Id);
        if (fleet is null) return "GBR has no fleet";

        // The enemy has to be one this fleet could plausibly sail to. Taking the first
        // country Britain shares no land border with picks Indonesia as readily as France,
        // and an admiralty that refuses to carry one division a third of the way round the
        // planet is behaving correctly - the check would then be failing the AI for being
        // sensible. So the target is the nearest overseas neighbour, found the same way the
        // AI looks for a beach: by distance from the fleet's own harbour.
        Nation target = null;
        var controllers = campaign.ProvinceController;
        var nearest = campaign.Defines.AiInvasionSearchDegrees;
        for (var province = 1; controllers is not null && province <= topology.ProvinceCount; province++)
        {
            var owner = controllers[province];
            if (owner == 0 || owner == (ushort)invader.MapIndex) continue;
            var centre = topology.CenterOf(province);
            var distance = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude,
                centre.Latitude, centre.Longitude);
            if (distance >= nearest) continue;
            var candidate = campaign.Nations.Find(nation => nation.MapIndex == owner);
            if (candidate is null || candidate.Id == invader.Id || candidate.Population <= 0) continue;
            if (CampaignDiplomacy.SharesLandBorder(campaign, invader.MapIndex, candidate.MapIndex)) continue;
            if (!CampaignDiplomacy.CanDeclareWar(campaign, invader.Id, candidate.Id, out _)) continue;
            nearest = distance;
            target = candidate;
        }
        if (target is null) return "no overseas country within reach to invade";

        var wasHuman = invader.IsHuman;
        invader.IsHuman = false;

        // Two hundred and twenty days in, Britain is usually fighting somebody it can walk
        // to as well, and this check is not about how a navy chooses between wars - it is
        // about whether one that finds itself in an overseas war knows what to do. So the
        // others are set aside for the length of it.
        var suspended = campaign.Wars
            .Where(candidate => !candidate.Ended && candidate.Involves(invader.Id)).ToList();
        foreach (var other in suspended) other.Ended = true;

        var war = CampaignDiplomacy.DeclareWar(campaign, invader.Id, target.Id);
        if (war is null)
        {
            foreach (var other in suspended) other.Ended = false;
            invader.IsHuman = wasHuman;
            return "the overseas war was refused";
        }
        try
        {
            // Put the fleet home and idle, and give it something to carry within reach.
            fleet.Mission = FleetMission.Anchored;
            fleet.HasOrder = false;
            fleet.Carried.Clear();
            fleet.Latitude = fleet.HomeLatitude;
            fleet.Longitude = fleet.HomeLongitude;
            var spare = campaign.Armies.Find(army => army.NationId == invader.Id &&
                army.EmbarkedFleetId < 0 && !army.InBattle);
            if (spare is null) return "GBR has no division free to load";
            if (fleet.HomeProvinceId > 0)
            {
                spare.ProvinceId = fleet.HomeProvinceId;
                var where = topology.CenterOf(fleet.HomeProvinceId);
                spare.Latitude = where.Latitude;
                spare.Longitude = where.Longitude;
            }
            spare.OrderKind = ArmyOrder.Manual;
            spare.Route.Clear();

            // The admiralty staggers its thinking by the day, and the day does not advance
            // inside a check - so for the length of this one, every fleet thinks every tick.
            var interval = campaign.Defines.AiFleetDecisionInterval;
            campaign.Defines.AiFleetDecisionInterval = 1;

            var loadedOn = -1;
            var launchedOn = -1;
            for (var day = 0; day < 200; day++)
            {
                AiAdmiralty.Tick(campaign);
                if (loadedOn < 0 && fleet.Carried.Count > 0) loadedOn = day;
                if (launchedOn < 0 && fleet.Mission == FleetMission.Invasion) launchedOn = day;
                if (launchedOn >= 0) break;
                NavalOperations.Tick(campaign);
            }
            campaign.Defines.AiFleetDecisionInterval = interval;
            if (loadedOn < 0)
            {
                NavalInvasion.CanEmbark(campaign, fleet, spare, out var refused);
                return $"an AI at war with {target.Tag} across water never put a division on its " +
                       $"fleet: {refused}";
            }
            if (launchedOn < 0) return "an AI loaded its fleet and never sent it anywhere";

            Console.WriteLine($"  ai navy: {invader.Tag} found itself at war with {target.Tag} across water, " +
                              $"loaded on day {loadedOn:N0} and sailed on day {launchedOn:N0}.");
            return "";
        }
        finally
        {
            invader.IsHuman = wasHuman;
            war.Ended = true;
            foreach (var other in suspended) other.Ended = false;
            foreach (var id in new List<int>(fleet.Carried))
            {
                var army = campaign.Armies.Find(candidate => candidate.Id == id);
                if (army is not null) { army.EmbarkedFleetId = -1; army.ProvinceId = fleet.HomeProvinceId; }
            }
            fleet.Carried.Clear();
            fleet.Mission = FleetMission.Anchored;
            fleet.HasOrder = false;
            fleet.TargetProvinceId = -1;
        }
    }

    /// <summary>
    /// A landing costs a formation its order, and troops trained for one should pay less of
    /// that price. Without this the marine template is a line of data nothing reads: the
    /// trait is on the template, but whether the landing consults it is the part that
    /// silently stops being true.
    /// </summary>
    private static string MarinesLandBetter(Campaign campaign)
    {
        var marine = UnitTypeCatalog.Get("marine_brigade");
        if (marine is null || marine.Amphibious <= 0f)
            return "no amphibious template ships at all; common/units has no marines";
        var line = UnitTypeCatalog.Get("line_infantry");
        if (line is null) return "no line infantry to compare against";
        if (line.Amphibious >= marine.Amphibious)
            return $"line infantry is as amphibious as marines ({line.Amphibious:P0})";

        var cost = campaign.Defines.InvasionLandingOrganisationCost;
        var marineCost = cost * (1f - marine.Amphibious);
        var lineCost = cost * (1f - line.Amphibious);
        if (marineCost >= lineCost)
            return "the landing does not read the amphibious trait";

        Console.WriteLine($"  marines: a landing costs line infantry {lineCost:P0} of its order and " +
                          $"a marine brigade {marineCost:P0}.");
        return "";
    }

    /// <summary>
    /// A convoy is the reason to keep ships in your own waters. If an invasion can sail
    /// past a hostile fleet untouched, escorting is a chore with no payoff and blockading
    /// one's own coast is pointless.
    /// </summary>
    private static string AConvoyCaughtAtSeaPaysForIt(Campaign campaign)
    {
        var left = campaign.NationByTag("GBR");
        var right = campaign.NationByTag("FRA");
        if (left is null || right is null) return "no two navies to test with";
        var convoy = campaign.Fleets.Find(fleet => fleet.NationId == left.Id);
        var raider = campaign.Fleets.Find(fleet => fleet.NationId == right.Id);
        var passenger = campaign.Armies.Find(army => army.NationId == left.Id && army.EmbarkedFleetId < 0);
        if (convoy is null || raider is null || passenger is null) return "nothing to make a convoy out of";

        var war = new StrategicWar
        {
            Id = 90_001, Name = "Bench convoy war", AttackerId = left.Id, DefenderId = right.Id,
            StartedDay = campaign.ElapsedDays, AiNegotiationUnlockDay = int.MaxValue
        };
        campaign.Wars.Add(war);
        var wasProvince = passenger.ProvinceId;
        try
        {
            convoy.Latitude = raider.Latitude = 47f;
            convoy.Longitude = raider.Longitude = -9f;
            convoy.Mission = FleetMission.Invasion;
            raider.Mission = FleetMission.Patrol;
            convoy.HasOrder = raider.HasOrder = false;
            convoy.TargetProvinceId = -1;
            convoy.Organisation = raider.Organisation = 1f;
            convoy.ReadyDay = raider.ReadyDay = 0;
            convoy.Carried.Clear();
            passenger.EmbarkedFleetId = convoy.Id;
            passenger.ProvinceId = 0;
            passenger.Strength = 1f;
            passenger.Organisation = 1f;
            convoy.Carried.Add(passenger.Id);

            NavalOperations.Tick(campaign);
            if (!convoy.InAction) return "a convoy sat on top of a hostile fleet and was not found";
            if (passenger.Strength >= 1f)
                return "an army was caught at sea aboard its transports and lost nobody";
            if (passenger.Organisation >= 1f) return "being attacked at sea cost the passengers no order";

            Console.WriteLine($"  convoy: caught at sea, the landing force came out at " +
                              $"{passenger.Strength:P0} strength and {passenger.Organisation:P0} order.");
            return "";
        }
        finally
        {
            campaign.Wars.Remove(war);
            passenger.EmbarkedFleetId = -1;
            passenger.ProvinceId = wasProvince;
            convoy.Carried.Clear();
            convoy.Mission = raider.Mission = FleetMission.Anchored;
            convoy.HasOrder = raider.HasOrder = false;
        }
    }

    /// <summary>
    /// The hole the whole naval side was shaped around: every war across water was refused
    /// with "naval invasions are not ported yet", so every island was permanently safe and
    /// a navy had nothing to do.
    ///
    /// Four things have to be true for the word to be earned. A country with ships may
    /// declare a war it has no land border for, and one without ships may not. Divisions
    /// can be loaded onto a fleet, and while aboard they hold no ground - or an invasion
    /// force would still be standing on its own coast. A loaded fleet sent at a shore
    /// arrives and puts them there. And the landing costs them their order, because coming
    /// off a ship onto somebody else's beach should not be free.
    /// </summary>
    private static string AnArmyCanBePutAshore(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";
        var invader = campaign.NationByTag("GBR");
        if (invader is null) return "no invader to test with";
        var fleet = campaign.Fleets.Find(candidate => candidate.NationId == invader.Id);
        if (fleet is null) return "GBR has no fleet";

        // Somebody across water that GBR shares no land border with.
        var target = campaign.Nations.Find(nation => nation.Id != invader.Id && nation.Population > 0 &&
            !CampaignDiplomacy.SharesLandBorder(campaign, invader.MapIndex, nation.MapIndex) &&
            CampaignDiplomacy.CanDeclareWar(campaign, invader.Id, nation.Id, out _));
        if (target is null) return "no overseas country to invade";
        if (!CampaignDiplomacy.CanDeclareWar(campaign, invader.Id, target.Id, out var refusal))
            return $"a country with {fleet.Hulls:N0} hulls was refused a war across water: {refusal}";

        var landless = campaign.Nations.Find(nation => nation.Population > 0 &&
            !campaign.Fleets.Exists(candidate => candidate.NationId == nation.Id));
        if (landless is not null)
        {
            var far = campaign.Nations.Find(nation => nation.Id != landless.Id && nation.Population > 0 &&
                !CampaignDiplomacy.SharesLandBorder(campaign, landless.MapIndex, nation.MapIndex));
            if (far is not null && CampaignDiplomacy.CanDeclareWar(campaign, landless.Id, far.Id, out _))
                return $"{landless.Tag} has no ships at all and was still allowed a war across water";
        }

        var war = CampaignDiplomacy.DeclareWar(campaign, invader.Id, target.Id);
        if (war is null) return "the overseas war was refused after all";
        try
        {
            var troops = campaign.Armies.FindAll(army => army.NationId == invader.Id && army.EmbarkedFleetId < 0);
            if (troops.Count == 0) return "GBR has no divisions to load";
            var loaded = troops.GetRange(0, Math.Min(2, troops.Count));
            foreach (var army in loaded)
            {
                // Put them in the fleet's own port so they can walk up the gangway.
                if (fleet.HomeProvinceId > 0)
                {
                    army.ProvinceId = fleet.HomeProvinceId;
                    var where = topology.CenterOf(fleet.HomeProvinceId);
                    army.Latitude = where.Latitude;
                    army.Longitude = where.Longitude;
                }
                army.Organisation = 1f;
            }
            fleet.HasOrder = false;
            fleet.Mission = FleetMission.Anchored;

            new EmbarkArmiesCommand
            {
                IssuerId = invader.Id, FleetId = fleet.Id,
                ArmyIds = loaded.ConvertAll(army => army.Id)
            }.Apply(campaign);
            if (fleet.Carried.Count == 0) return "no division could be loaded onto a fleet in its own port";
            var aboard = campaign.Armies.Find(army => army.Id == fleet.Carried[0]);
            if (aboard is null || aboard.ProvinceId > 0)
                return "a formation aboard a ship was still holding ground ashore";

            // A coastal province the target holds.
            var beach = -1;
            for (var province = 1; province <= topology.ProvinceCount && beach < 0; province++)
                if (campaign.ProvinceController[province] == (ushort)target.MapIndex) beach = province;
            if (beach < 0) return $"{target.Tag} holds no province to land on";

            new LaunchInvasionCommand
            {
                IssuerId = invader.Id, FleetId = fleet.Id, ProvinceId = beach
            }.Apply(campaign);
            if (fleet.Mission != FleetMission.Invasion) return "the fleet refused the invasion order";

            var before = aboard.Organisation;
            var sailed = 0;
            for (; sailed < 400 && fleet.Carried.Count > 0; sailed++) NavalOperations.Tick(campaign);
            if (fleet.Carried.Count > 0) return $"the fleet never arrived after {sailed:N0} days at sea";
            if (aboard.ProvinceId <= 0) return "the landing left the formation nowhere";
            if (aboard.Organisation >= before) return "coming ashore on a hostile beach cost nothing";

            Console.WriteLine($"  invasion: {invader.Tag} declared war on {target.Tag} across water, loaded " +
                              $"{loaded.Count:N0} division(s) onto {fleet.Hulls:N0} hulls, sailed {sailed:N0} day(s) " +
                              $"and landed at {aboard.Organisation:P0} organisation against {before:P0} aboard.");
            return "";
        }
        finally
        {
            war.Ended = true;
            foreach (var id in new List<int>(fleet.Carried))
            {
                var army = campaign.Armies.Find(candidate => candidate.Id == id);
                if (army is not null) army.EmbarkedFleetId = -1;
            }
            fleet.Carried.Clear();
            fleet.Mission = FleetMission.Anchored;
            fleet.HasOrder = false;
            fleet.TargetProvinceId = -1;
        }
    }

    /// <summary>
    /// The navy was fifteen counters that sat where they were put, could not be clicked,
    /// and changed nothing anywhere. Rebuilt on the same bones as a division, it has to
    /// earn the word: fleets that meet must fight, a fight must cost both of them, a beaten
    /// fleet must go home, and a fleet on an enemy's trade must cost that enemy money.
    ///
    /// Everything here is done to a throwaway war between two navies, and put back after.
    /// </summary>
    private static string ANavyIsWorthHaving(Campaign campaign)
    {
        var left = campaign.NationByTag("GBR");
        var right = campaign.NationByTag("FRA");
        if (left is null || right is null) return "no two navies to test with";
        var mine = campaign.Fleets.Find(fleet => fleet.NationId == left.Id);
        var theirs = campaign.Fleets.Find(fleet => fleet.NationId == right.Id);
        if (mine is null || theirs is null) return "GBR or FRA has no fleet in the scenario";

        var war = new StrategicWar
        {
            Id = 90_000,
            Name = "Bench naval war",
            AttackerId = left.Id,
            DefenderId = right.Id,
            StartedDay = campaign.ElapsedDays,
            AiNegotiationUnlockDay = int.MaxValue
        };
        campaign.Wars.Add(war);
        try
        {
            // Put them within sight of each other, both at sea and both willing.
            mine.Latitude = theirs.Latitude = 48f;
            mine.Longitude = theirs.Longitude = -8f;
            mine.Mission = FleetMission.Patrol;
            theirs.Mission = FleetMission.Patrol;
            mine.HasOrder = theirs.HasOrder = false;
            mine.Organisation = theirs.Organisation = 1f;
            mine.Strength = theirs.Strength = 1f;
            mine.Experience = theirs.Experience = 0f;
            mine.ReadyDay = theirs.ReadyDay = 0;

            NavalOperations.Tick(campaign);
            if (!mine.InAction || !theirs.InAction)
                return "two hostile fleets on the same spot did not find each other";
            if (mine.Organisation >= 1f || theirs.Organisation >= 1f)
                return "an action cost neither fleet any cohesion";
            if (mine.Strength >= 1f && theirs.Strength >= 1f)
                return "an action cost neither fleet a single hull";
            if (mine.Experience <= 0f) return "a fleet learned nothing from being in action";

            // Fought to a standstill, the weaker one must break off for home rather than
            // fight forever on the spot.
            var fought = 1;
            for (; fought < 60 && mine.Mission == FleetMission.Patrol &&
                   theirs.Mission == FleetMission.Patrol; fought++)
                NavalOperations.Tick(campaign);
            if (mine.Mission == FleetMission.Patrol && theirs.Mission == FleetMission.Patrol)
                return $"neither fleet broke off after {fought:N0} days of action";

            // And a navy on an enemy's trade has to be felt in the enemy's budget.
            theirs.Mission = FleetMission.Raid;
            theirs.Organisation = 1f;
            var before = left.WeeklyTradeBalance;
            NavalOperations.ApplyCommerceEffects(campaign, left, null);
            if (left.WeeklyTradeBalance >= before)
                return "an enemy fleet raiding trade cost the victim nothing at all";
            var raided = before - left.WeeklyTradeBalance;

            Console.WriteLine($"  navy: two fleets met and fought for {fought:N0} day(s) - " +
                              $"{mine.Hulls:N0} hulls at {mine.Strength:P0} strength against {theirs.Hulls:N0} at " +
                              $"{theirs.Strength:P0} - and raiding cost the loser {Fmt.Money(raided)} a week.");
            return "";
        }
        finally
        {
            campaign.Wars.Remove(war);
            mine.Mission = theirs.Mission = FleetMission.Anchored;
            mine.HasOrder = theirs.HasOrder = false;
        }
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

        // Whether the sea is contested or decorative. Every navy but the player's used to
        // sit in port for the whole campaign, which is easy to miss when nothing checks it.
        var byMission = campaign.Fleets
            .Where(fleet => fleet.Hulls > 0)
            .GroupBy(fleet => fleet.Mission)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} x{group.Count()}");
        var atSea = campaign.Fleets.Count(fleet => fleet.Mission != FleetMission.Anchored);
        var carrying = campaign.Fleets.Count(fleet => fleet.Carried.Count > 0);
        var worn = campaign.Fleets.Where(fleet => fleet.Hulls > 0).ToList();
        // Whether anybody with a navy is fighting anybody else with one. Zero naval actions
        // is the right answer when no two navies are at war, and the wrong one otherwise -
        // the number that tells them apart belongs in the report.
        var navied = new HashSet<int>();
        foreach (var fleet in campaign.Fleets) if (fleet.Hulls > 0) navied.Add(fleet.NationId);
        var navalWars = campaign.Wars.Count(war => !war.Ended &&
            navied.Contains(war.AttackerId) && navied.Contains(war.DefenderId));
        var overseasWars = campaign.Wars.Count(war => !war.Ended &&
            campaign.NationById(war.AttackerId) is { } a && campaign.NationById(war.DefenderId) is { } b &&
            !CampaignDiplomacy.SharesLandBorder(campaign, a.MapIndex, b.MapIndex));
        Console.WriteLine($"  wars at sea: {navalWars:N0} live war(s) have a navy on both sides, " +
                          $"{overseasWars:N0} are between countries that do not touch by land.");
        Console.WriteLine($"  the sea: {atSea:N0} of {worn.Count:N0} fleet(s) are out, {carrying:N0} carrying " +
                          $"troops - {string.Join(", ", byMission)}. Average strength " +
                          $"{(worn.Count == 0 ? 0f : worn.Average(fleet => fleet.Strength)):P0}, " +
                          $"experience {(worn.Count == 0 ? 0f : worn.Average(fleet => fleet.Experience)):P0}.");

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
    /// A tooltip that explains a number must add up to that number.
    ///
    /// This is the whole value of the thing. A player who hovers a falling budget and reads
    /// a list that does not come to what the top bar says has been told something false,
    /// and would have been better served by no explanation at all. Clamps and floors are
    /// where it goes wrong: the parts are computed, then the result is limited, and the
    /// listed parts quietly stop describing it. Every breakdown therefore has to name that
    /// difference rather than swallow it.
    /// </summary>
    private static string BreakdownsAddUp(Campaign campaign)
    {
        var checkedNations = 0;
        var checkedEntries = 0;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0) continue;
            foreach (var (breakdown, shown, what) in new[]
                     {
                         (nation.Budget, nation.WeeklyBalance, "the weekly balance"),
                         (nation.StabilityDrivers, float.NaN, "stability"),
                         (nation.WarSupportDrivers, float.NaN, "war support")
                     })
            {
                if (!breakdown.HasEntries) continue;
                checkedNations++;
                var sum = 0f;
                foreach (var entry in breakdown.Entries)
                {
                    checkedEntries++;
                    if (string.IsNullOrWhiteSpace(entry.Source))
                        return $"{nation.Tag} has an unnamed part in {what}";
                    sum += entry.Value;
                }
                var scale = MathF.Max(1f, MathF.Abs(breakdown.Total));
                if (MathF.Abs(sum - breakdown.Total) / scale > 0.001f)
                    return $"{nation.Tag}: the parts of {what} come to {sum:0.####} but the total says " +
                           $"{breakdown.Total:0.####}";
                if (!float.IsNaN(shown) && MathF.Abs(breakdown.Total - shown) / MathF.Max(1f, MathF.Abs(shown)) > 0.001f)
                    return $"{nation.Tag}: {what} is explained as {breakdown.Total:0.##} but the screen shows " +
                           $"{shown:0.##}";
            }
        }
        if (checkedNations == 0) return "no country explained a single number; the ledgers were never filled";

        Console.WriteLine($"  explained numbers: {checkedEntries:N0} named part(s) across {checkedNations:N0} " +
                          "ledger(s), each adding up to the figure it explains.");
        return "";
    }

    /// <summary>
    /// A province must belong to the country whose ground its own pixels are on.
    ///
    /// This is the most basic thing that can be true of a political map, and it was quietly
    /// false for half the world. The map compiler builds the provinces from the source
    /// raster and then exports the political map from the result - but attaching a topology
    /// applies whatever ownership file is already on disk, and that file was written against
    /// the *previous* province numbering. So each recompile that changed the province count
    /// laid the old world over the new provinces and wrote the wreckage back out as truth.
    /// Kentucky was Chinese. Ashgabat was American. Every screen in the game reported it
    /// faithfully, every existing check passed, and the front lines were drawn across three
    /// continents because half the planet was suddenly at war with itself.
    ///
    /// Nothing caught it because nothing compared the map against the ground it is drawn on.
    /// </summary>
    private static string ProvincesSitWhereTheirOwnerIs(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        var checkedProvinces = 0;
        var wrong = new List<string>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var cell = topology.ProvinceCenterCell[province];
            if (cell < 0 || cell >= Globality.EarthData.Control.Length) continue;
            var ground = Globality.EarthData.Control[cell];
            if (ground == 0) continue;
            checkedProvinces++;
            var owner = topology.ProvinceToNation[province];
            if (owner == ground) continue;
            if (wrong.Count < 4)
            {
                string Tag(int mapIndex) =>
                    campaign.Nations.Find(nation => nation.MapIndex == mapIndex)?.Tag ?? $"#{mapIndex}";
                wrong.Add($"province {province:N0} stands on {Tag(ground)} ground and belongs to {Tag(owner)}");
            }
            else wrong.Add("");
        }

        if (wrong.Count > 0)
            return $"{wrong.Count:N0} of {checkedProvinces:N0} provinces belong to somebody other than the " +
                   $"country they are standing on: {string.Join("; ", wrong.Where(entry => entry.Length > 0))}";

        Console.WriteLine($"  geography: all {checkedProvinces:N0} provinces belong to the country whose " +
                          "ground they are drawn on.");
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
    private static string ArtworkIsNamedRatherThanSpelledOut()
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
            using (var document = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson))
            {
                var body = document.RootElement;
                if (body.TryGetProperty("sprites", out var inner)) body = inner;
                if (body.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in body.EnumerateObject()) declared.Add(entry.Name);
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

    /// <summary>
    /// The moments belong to the code; what they sound like belongs to the data.
    ///
    /// Every part of a cue used to be a constant in the interface: the file, which of the
    /// three players it went to, how loud, and how long before it could sound again - that
    /// last one a `switch` on the cue's own name, which is the least reachable place a
    /// modder could look. The check holds the two halves apart: the six moments the code
    /// raises must all be declared, and the declaration has to be the thing that decides.
    /// </summary>
    private static string AudioIsDeclaredRatherThanWiredIn()
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
            "user://mods/example", "test", null);
        var after = SoundCatalog.Get("battle_won");
        if (after.CooldownMs != 250) return "a mod could not retime a cue";
        if (after.File != before.File || after.Channel != before.Channel)
            return "retiming a cue lost the rest of its declaration";

        // And a mod's own audio resolves inside the mod's own folder.
        SoundCatalog.Merge("{ \"cues\": { \"battle_won\": \"audio/mine.wav\" } }",
            "user://mods/example", "test", null);
        var replaced = SoundCatalog.Get("battle_won");
        if (replaced.File != "user://mods/example/audio/mine.wav")
            return $"a mod's own sound resolved to '{replaced.File}' instead of inside the mod folder";
        if (replaced.CooldownMs != 250)
            return "replacing a cue's file discarded how the cue behaves";

        MusicCatalog.Merge("{ \"tracks\": { \"theme\": { \"file\": \"audio/theme.ogg\", \"when\": \"menu\" } } }",
            "user://mods/example", "test", null);
        var menu = MusicCatalog.For(MusicCatalog.Moment.Menu);
        var campaign = MusicCatalog.For(MusicCatalog.Moment.Campaign);
        if (menu.Count != 1 || menu[0].File != "user://mods/example/audio/theme.ogg")
            return "a mod could not add a track of its own";
        if (campaign.Count != 0)
            return "a menu track was offered to the campaign as well";

        SoundCatalog.Load();
        MusicCatalog.Load();
        Console.WriteLine($"  audio: {SoundCatalog.Count:N0} declared cue(s) across " +
                          $"{SoundCatalog.AllChannels.Count():N0} channel(s), every file present, " +
                          $"{MusicCatalog.Count:N0} shipped track(s) and a mod can add its own.");
        return "";
    }

    /// <summary>
    /// A total conversion may redraw the world; it must not be fought by the armies of 2024.
    ///
    /// `history/units` was the one history folder the mod loader never read. A mod could
    /// move every border, rename every country and rewrite every economy, and the
    /// standing armies and the navies would still be the base game's - which is exactly
    /// the kind of hole that only shows up once somebody tries to build the mod.
    ///
    /// `fonts/` is checked alongside it because it is the same class of hole: a mod can
    /// translate every string in the game and render a screen of empty boxes, since the
    /// face the interface is set in was the engine's and nothing could say otherwise.
    /// </summary>
    private static string TheOrderOfBattleIsAModsToRewrite(Campaign campaign)
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
    /// Decisions are content, and content the AI acts on.
    ///
    /// Two holes, one check. A decision used to need a `kind` - a consequence compiled
    /// into the executable - so a mod could rename the five the game shipped and write no
    /// sixth; and no AI had ever taken one, so two hundred and sixty governments sat on
    /// the same five levers for a whole campaign while only the player pulled them.
    ///
    /// The check refuses to accept either half on its own: the libraries have to load and
    /// compose, a shipped decision has to be nothing but data, and somebody who is not the
    /// player has to have actually done something over two years.
    /// </summary>
    private static string GovernmentsTakeScriptedDecisions(Campaign campaign, int days)
    {
        if (ScriptLibrary.TriggerCount == 0) return "common/scripted_triggers declares nothing";
        if (ScriptLibrary.EffectCount == 0) return "common/scripted_effects declares nothing";
        if (ScriptLibrary.ScoreCount == 0) return "common/scripted_modifiers declares nothing";

        // A decision that needs no compiled case is the one a mod could write.
        var scripted = DecisionCatalog.All
            .Where(decision => decision.Kind == DecisionKind.Scripted)
            .ToList();
        if (scripted.Count == 0)
            return "every shipped decision still needs a compiled kind, so a mod cannot write one";
        var toothless = scripted.FirstOrDefault(decision => decision.Effect is null || decision.Effect.IsEmpty);
        if (toothless.Id is not null)
            return $"scripted decision '{toothless.Id}' does nothing: its effects did not parse";
        var ungated = scripted.FirstOrDefault(decision => decision.Trigger is null || decision.Trigger.IsEmpty);
        if (ungated.Id is not null)
            return $"scripted decision '{ungated.Id}' has no condition, so it is always on offer";

        // `use` has to actually fold a named block in, or the libraries are decoration.
        using (var document = JsonDocument.Parse("{ \"use\": [ \"restive\" ] }"))
        {
            var borrowed = ScriptedTrigger.Parse(document.RootElement, "bench", null);
            if (borrowed.IsEmpty) return "a trigger that uses a named one came out empty";

            var calm = campaign.Nations.Find(nation => nation.Stability > 0.60f);
            var troubled = campaign.Nations.Find(nation => nation.Stability < 0.40f);
            if (calm is not null && borrowed.Matches(campaign, calm))
                return $"'restive' matched {calm.Tag}, whose stability is {calm.Stability:P0}";
            if (troubled is not null && !borrowed.Matches(campaign, troubled))
                return $"'restive' refused {troubled.Tag}, whose stability is {troubled.Stability:P0}";
        }

        var acted = campaign.Nations.Count(nation => !nation.IsHuman && nation.DecisionAvailableDay.Count > 0);
        if (acted == 0)
            return $"no government took a decision in {days:N0} day(s); the AI cabinet is not sitting";

        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var nation in campaign.Nations)
            foreach (var decision in nation.DecisionAvailableDay.Keys)
                taken[decision] = taken.GetValueOrDefault(decision) + 1;
        var popular = string.Join(", ", taken
            .OrderByDescending(entry => entry.Value)
            .Take(4)
            .Select(entry => $"{entry.Key} x{entry.Value:N0}"));

        Console.WriteLine($"  cabinets: {acted:N0} of {campaign.Nations.Count:N0} governments took a decision " +
                          $"in {days:N0} day(s) - {popular}.");
        Console.WriteLine($"  script libraries: {ScriptLibrary.TriggerCount:N0} trigger(s), " +
                          $"{ScriptLibrary.EffectCount:N0} effect(s), {ScriptLibrary.ScoreCount:N0} score(s); " +
                          $"{scripted.Count:N0} decision(s) are data alone.");
        return "";
    }

    /// <summary>
    /// An opinion has to be made of reasons, and the reasons have to fade.
    ///
    /// Opinion was one number that two buttons moved and a war slammed to minus sixty. It
    /// remembered nothing, so nothing could explain it to the player, and it forgot
    /// nothing either: a war fought in 2024 held a country at minus sixty in 2124, while a
    /// decade of patient diplomacy read exactly like a bribe paid last Tuesday.
    ///
    /// The check holds both halves. The parts have to add up to the number - an opinion
    /// that does not equal its own breakdown is a screen that lies - and time has to
    /// actually work on them.
    /// </summary>
    private static string OpinionIsRememberedAndForgotten(Campaign campaign)
    {
        if (OpinionCatalog.Count == 0) return "common/opinion_modifiers declares nothing";

        var first = campaign.Nations.Find(nation => nation.Population > 0);
        var second = campaign.Nations.Find(nation =>
            nation != first && nation.Population > 0 &&
            !campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)));
        if (first is null || second is null) return "not enough countries to test with";

        var relation = CampaignDiplomacy.Get(campaign, first.Id, second.Id);
        relation.Reasons.Clear();
        OpinionSystem.Settle(campaign, relation);
        if (Math.Abs(relation.Opinion) > 0.01f)
            return $"a country with no history of another has an opinion of {relation.Opinion:0.0}";

        OpinionSystem.Remember(campaign, relation, "declared_war_on_us");
        var remembered = relation.Opinion;
        if (remembered > -50f)
            return $"a declaration of war moved opinion to {remembered:0.0}, which is barely a grudge";

        // What the screen shows has to equal what it is made of.
        var parts = 0f;
        foreach (var entry in relation.OpinionDrivers.Entries) parts += entry.Value;
        if (Math.Abs(parts - relation.Opinion) > 0.5f)
            return $"the opinion reads {relation.Opinion:0.0} and its parts add to {parts:0.0}";

        // And it fades. Nine years on, the same grudge has to be worth less.
        var reason = relation.Reasons.Find(item =>
            string.Equals(item.Id, "declared_war_on_us", StringComparison.OrdinalIgnoreCase));
        if (reason is null) return "the declaration of war was not recorded as a reason";
        reason.GrantedDay = campaign.ElapsedDays - 3285;
        reason.ExpiresDay = campaign.ElapsedDays + 365;
        OpinionSystem.Settle(campaign, relation);
        var faded = relation.Opinion;
        if (faded <= remembered + 5f)
            return $"nine years on the grudge is still {faded:0.0}, having started at {remembered:0.0}";

        // Expired reasons are gone rather than lingering at zero weight.
        reason.ExpiresDay = campaign.ElapsedDays - 1;
        OpinionSystem.Settle(campaign, relation);
        if (relation.Reasons.Any(item =>
                string.Equals(item.Id, "declared_war_on_us", StringComparison.OrdinalIgnoreCase)))
            return "a reason that ran out is still on the books";

        relation.Reasons.Clear();
        OpinionSystem.Settle(campaign, relation);

        var held = 0;
        foreach (var entry in campaign.Relations.Values) held += entry.Reasons.Count;
        Console.WriteLine($"  opinion: {OpinionCatalog.Count:N0} kind(s) of reason; the world is holding " +
                          $"{held:N0} of them across {campaign.Relations.Count:N0} relation(s).");
        return "";
    }

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

        var peoples = EconomySystem.Cultures(taken.State);
        if (peoples.Count == 0) return "the annexed region has nobody in it";

        var radicalismBefore = taken.State.Radicalism;
        var foreignBefore = taken.Foreign;
        campaign.IsPaused = false;
        for (var day = 0; day < 400; day++) campaign.Tick();
        var foreignAfter = EconomySystem.ForeignShare(taken.State, taken.Owner.Tag);

        Console.WriteLine($"  nationality: {taken.Owner.Tag} took {taken.State.Name}, " +
                          $"{foreignBefore:P0} foreign at the treaty and {foreignAfter:P0} a year later " +
                          $"({string.Join(", ", peoples.Take(3).Select(entry => $"{DemographyCatalog.CultureName(entry.Culture)} {entry.Size:N0}"))}); " +
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
            .Where(state => !LakeRegions.IsWaterState(state))
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

    /// <summary>
    /// A lake is a place, and nothing walks on it.
    ///
    /// Water used to be paint over land: a province could span half a reservoir and a column
    /// could march across Lake Michigan without anything noticing. Now every lake above the
    /// threshold is a region, so three things have to hold together - it belongs to somebody,
    /// nobody lives on it, and no route on foot ever enters one. The third is the one that
    /// matters and the easiest to lose: it is enforced in one place each for movement, fronts
    /// and supply, and any of those three could be edited back out without a symptom until
    /// somebody watched an army walk over Baikal.
    /// </summary>
    private static string LakesArePlacesArmiesCannotWalkOn(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "the map has no provinces";

        var lakes = campaign.States.Where(LakeRegions.IsWaterState).ToList();
        if (lakes.Count == 0) return "the world has no lakes at all, which no map of Earth manages";

        var faults = new List<string>();
        foreach (var lake in lakes)
        {
            if (lake.NationMapIndex <= 0) faults.Add($"{lake.Name} belongs to nobody");
            if (lake.Population > 0) faults.Add($"{lake.Name} has {Fmt.People(lake.Population)} living on the water");
        }

        var water = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.IsWater(province)) water++;
        if (water == 0) faults.Add("no province is marked as water, so the lakes are decoration");

        // Walk out of a lakeside province and see whether anything offers to cross.
        var controllers = campaign.ProvinceController;
        var crossings = 0;
        var sampled = 0;
        for (var province = 1; province <= topology.ProvinceCount && sampled < 400; province++)
        {
            if (topology.IsWater(province) || controllers is null) continue;
            var touchesWater = false;
            foreach (var neighbour in topology.Neighbors[province])
                if (topology.IsWater(neighbour)) { touchesWater = true; break; }
            if (!touchesWater) continue;
            sampled++;

            var nation = controllers[province];
            foreach (var neighbour in topology.Neighbors[province])
            {
                if (!topology.IsWater(neighbour)) continue;
                var route = topology.FindRoute(province, neighbour, nation);
                if (route.Length > 0) crossings++;
            }
        }
        if (crossings > 0) faults.Add($"{crossings:N0} route(s) march straight into open water");

        if (faults.Count > 0) return string.Join("; ", faults.Take(6));
        Console.WriteLine($"  lakes: {lakes.Count:N0} region(s) over {water:N0} province(s) of water, " +
            $"every one owned, empty of people and closed to marching ({sampled:N0} shoreline(s) tried).");
        return "";
    }

    /// <summary>
    /// The compiled map still matches the region layer this build draws.
    ///
    /// Provinces are cut inside regions, so the raster on disk is an output of the code that
    /// draws them. Change one without rebuilding the other and the game loses its whole map
    /// at boot - which is what it looks like from the outside, rather than what it is, and it
    /// cost an afternoon once already. Cheaper to be told here.
    /// </summary>
    private static string TheCompiledMapMatchesTheCodeThatDrewIt()
    {
        if (string.IsNullOrWhiteSpace(MapRasterCatalog.StampPath))
            return "game/map_data/compiled.json is missing - recompile with Tools/MapDataCompiler";
        var fingerprint = MapRasterCatalog.RegionFingerprint(Globality.EarthData.States);
        var stamped = System.Text.Json.JsonDocument.Parse(File.ReadAllText(MapRasterCatalog.StampPath))
            .RootElement.GetProperty("regions").GetUInt64();
        if (stamped != fingerprint)
            return $"provinces.png was cut from region layer {stamped}, this build draws {fingerprint}";
        Console.WriteLine($"  map stamp: provinces.png and this build agree on region layer {fingerprint}.");
        return "";
    }

    /// <summary>
    /// Every region knows who lives in it, and the minorities are where they actually live.
    ///
    /// Three things have to hold at once and only the first is obvious. The head-count has
    /// to add up, or the population screen and the economy disagree about the same region.
    /// Every people, faith and trade has to be one the catalogues define, or the screen
    /// renders an id. And the peoples have to be in the right provinces: a census that is
    /// right nationally and smeared evenly over the country puts Tibetans in Shanghai and
    /// Chechens in Kaliningrad, which is worse than no census at all, and it is the failure
    /// that no total will ever catch.
    /// </summary>
    private static string EveryRegionKnowsWhoLivesInIt(Campaign campaign)
    {
        if (DemographyCatalog.Cultures.Count == 0) return "no peoples are defined at all";
        if (DemographyCatalog.Professions.Count == 0) return "no trades are defined at all";

        var faults = new List<string>();
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var empty = 0;
        long counted = 0;
        long stated = 0;
        foreach (var state in campaign.States)
        {
            if (LakeRegions.IsWaterState(state)) continue;
            var total = state.PopulationGroups.Sum(group => group.Size);
            stated += state.Population;
            counted += total;
            if (state.Population > 0 && total == 0) empty++;
            else if (state.Population > 0 && Math.Abs(total - state.Population) > Math.Max(4, state.Population / 1000))
                faults.Add($"{state.Name} holds {total:N0} people and says it holds {state.Population:N0}");
            foreach (var group in state.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                if (DemographyCatalog.Culture(group.Culture) is null) unknown.Add($"culture {group.Culture}");
                if (DemographyCatalog.Religion(group.Religion) is null) unknown.Add($"faith {group.Religion}");
                if (DemographyCatalog.Profession(group.Profession) is null) unknown.Add($"trade {group.Profession}");
            }
            if (faults.Count > 4) break;
        }
        if (empty > 0) faults.Add($"{empty:N0} inhabited region(s) have nobody in them");
        if (unknown.Count > 0) faults.Add("undefined: " + string.Join(", ", unknown.Take(4)));
        if (faults.Count > 0) return string.Join("; ", faults.Take(5));

        // The peoples who named the regions they live in are actually in them. Each of these
        // is a place where a national average is a lie, and the whole point of the "where"
        // line is that the map shows it.
        var expected = new (string Region, string Culture, float AtLeast)[]
        {
            ("Tatarstan", "tatar", 0.40f),
            ("Chechen", "chechen", 0.70f),
            ("Xinjiang", "uyghur", 0.30f),
            ("Tibet", "tibetan", 0.60f),
            ("Quebec", "quebecois", 0.55f),
            ("Kurdistan", "kurdish", 0.60f)
        };
        var reported = new List<string>();
        foreach (var (name, culture, atLeast) in expected)
        {
            var state = campaign.States.FirstOrDefault(candidate =>
                candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (state is null) continue;
            var here = EconomySystem.Cultures(state);
            var share = here.Where(entry => string.Equals(entry.Culture, culture, StringComparison.OrdinalIgnoreCase))
                .Sum(entry => entry.Size) / (double)Math.Max(1, state.Population);
            if (share < atLeast)
                faults.Add($"{state.Name} is {share:P0} {culture} and should be at least {atLeast:P0}");
            reported.Add($"{state.Name} {share:P0} {culture}");
        }
        if (faults.Count > 0) return string.Join("; ", faults.Take(4));

        var sample = campaign.States.FirstOrDefault(state => state.Name.Contains("Bavaria", StringComparison.OrdinalIgnoreCase))
                     ?? campaign.States.OrderByDescending(state => state.Population).First();
        var trades = EconomySystem.Professions(sample).Take(3)
            .Select(entry => $"{DemographyCatalog.ProfessionName(entry.Profession)} {entry.Size / (double)Math.Max(1, sample.Population):P0}");
        Console.WriteLine($"  people: {DemographyCatalog.Cultures.Count:N0} peoples, {DemographyCatalog.Religions.Count} faiths, " +
                          $"{DemographyCatalog.Professions.Count} trades over {DemographyCatalog.CountryCount} countries and " +
                          $"{DemographyCatalog.RegionOverrideCount} region correction(s); {counted:N0} of {stated:N0} placed.");
        Console.WriteLine($"    minorities where they live: {string.Join(", ", reported)}.");
        Console.WriteLine($"    {sample.Name} works as: {string.Join(", ", trades)}.");
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
    private static string ContentPacksAreLockedUntilOwned()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "dlc");
        if (!Directory.Exists(root)) return "there is no dlc/ folder; the content pack framework is gone";

        var packs = Directory.GetDirectories(root)
            .Where(directory => File.Exists(Path.Combine(directory, "dlc.json")))
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .ToList();
        if (packs.Count == 0) return "dlc/ holds no content pack, so nothing proves the pipeline works";

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
        Console.WriteLine($"  content packs: {string.Join(", ", reported)} - absent while locked, complete once owned.");
        return "";
    }

    /// <summary>
    /// A country that states a figure about itself ends up with that figure.
    ///
    /// The population screen is only worth reading if its numbers are the real ones. Two
    /// of them are looked up rather than guessed - how urban a country is and how much of
    /// it works the land - and both are consumed a long way from where they are written:
    /// they set the weights of ten trades, in every region separately, and the regions are
    /// then normalised. There are several honest-looking ways for a figure to be quietly
    /// halved on that journey, and none of them would look like anything on screen.
    ///
    /// Regions vary around the national average on purpose, so this checks the country as a
    /// whole rather than any one region.
    /// </summary>
    private static string TheStatedStatisticsCameTrue(Campaign campaign)
    {
        var stated = campaign.Nations.Where(nation => nation.AgricultureShare > 0f).ToList();
        if (stated.Count < 50) return $"only {stated.Count} countries state what they do for a living";

        var faults = new List<string>();
        var worst = 0.0;
        var worstTag = "";
        var worstPair = (Stated: 0.0, Actual: 0.0);
        foreach (var nation in stated)
        {
            long total = 0, farming = 0;
            foreach (var state in campaign.StatesOf(nation))
            foreach (var group in state.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                total += group.Size;
                if (group.Class == SocialClass.Farmers) farming += group.Size;
            }
            if (total < 100_000) continue;
            var actual = farming / (double)total;
            var slip = Math.Abs(actual - nation.AgricultureShare);
            if (slip > worst)
            {
                worst = slip;
                worstTag = nation.Tag;
                worstPair = (nation.AgricultureShare, actual);
            }
            // Regions vary around the average on purpose, so a few points of slack is the
            // spread and not a fault. Ten would be the bug this caught: aiming at the
            // midpoint of a bent curve instead of averaging across the real regions.
            if (slip > 0.05)
                faults.Add($"{nation.Tag} says {nation.AgricultureShare:P0} of it farms and {actual:P0} does");
        }
        if (faults.Count > 0) return string.Join("; ", faults.Take(5));

        var sample = new[] { "DEU", "USA", "IND", "ETH", "CHN" }
            .Select(tag => campaign.NationByTag(tag))
            .Where(nation => nation is not null && nation.AgricultureShare > 0f)
            .Select(nation =>
            {
                long total = 0, farming = 0;
                foreach (var state in campaign.StatesOf(nation))
                foreach (var group in state.PopulationGroups)
                {
                    if (group.Size <= 0) continue;
                    total += group.Size;
                    if (group.Class == SocialClass.Farmers) farming += group.Size;
                }
                return $"{nation.Tag} {farming / (double)Math.Max(1, total):P0} (said {nation.AgricultureShare:P0})";
            });
        Console.WriteLine($"  statistics: {stated.Count:N0} countries state how urban they are and how much of " +
                          $"them farms, and get it back within {worst:P1} at worst " +
                          $"({worstTag} {worstPair.Stated:P0} -> {worstPair.Actual:P0}).");
        Console.WriteLine($"    on the land: {string.Join(", ", sample)}.");
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
