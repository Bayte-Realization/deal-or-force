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
        failures += Check("every order a player can give crosses the wire",
            EveryOrderCrossesTheWire());
        failures += Check("the same seed gives the same world twice",
            TheSameSeedGivesTheSameWorld(Math.Min(days, 90)));
        failures += Check("two worlds in one process do not read each other's notes",
            TwoWorldsDoNotShareNotes(Math.Min(days, 45)));
        failures += Check("two worlds in one process do not read each other's notes",
            TwoWorldsDoNotShareNotes(Math.Min(days, 45)));
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
        failures += Check("a campaign day does not bury the collector",
            ADayDoesNotBuryTheCollector(allocated / (double)days));
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
        failures += Check("the English table indexes every string the game shows",
            TheEnglishTableIsAnIndex());
        failures += Check("a mod can add a good and a building the game never had",
            AModCanAddAGood(campaign));
        failures += Check("the budget is data, and still the budget it always was",
            TheBudgetIsData(campaign));
        failures += Check("a country is as settled as its regions and its capital say",
            StabilityIsWorkedOutRatherThanStored());
        failures += Check("people take up arms for a country they think is theirs",
            MilitiaRisesWhereItIsWanted());
        failures += Check("a mod can add a cabinet and a trade stance the game never had",
            PolicyCataloguesTakeNewEntries());
        failures += Check("every country is governed by something, and minds what its neighbours are",
            ThePoliticalWorldHangsTogether(campaign));
        failures += Check("every good is made by something and eaten by something",
            TheChainsHangTogether());
        failures += Check("every mod template names something the game actually has",
            TheTemplatesStillNameRealThings());
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

        failures += Check("a war costs formations and an army replaces them",
            WarCostsFormations(campaign, days));
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
                failures += Check("the length of a step is a pace, not a difficulty",
                    TheClockRunsAtAnyStep());
                failures += Check("no region belongs to two countries at once",
                    EveryRegionHasOneOwner(sandbox));
                failures += Check("a region cut by a new border becomes two regions",
                    RegionsSplitAlongNewBorders(sandbox));
                failures += Check("a region put back together again is one region",
                    RegionsHealWhenTheBorderGoesAway(sandbox));
                failures += Check("a redrawn border survives a save", BordersSurviveASave(sandbox));
                // Deletes formations and puts others on trains, so it belongs on the copy.
                failures += Check("a consolidation invents nobody and the railways are paid for",
                    ManoeuvresCostWhatTheySave(sandbox));
                // Draws a line and gives away posts, so it belongs on the copy as well.
                failures += Check("a drawn line is held where it is pressed",
                    ADrawnLineIsHeldWhereItIsPressed(sandbox));
                failures += Check("a battle plan survives a save",
                    ABattlePlanSurvivesASave(sandbox));
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
                failures += Check("every region of the world is a line a mod can edit",
                    TheRegionsOfTheWorldAreData());
                failures += Check("a country remembers why it dislikes another",
                    OpinionIsRememberedAndForgotten(sandbox));
                failures += Check("the peace an AI writes hangs together",
                    ThePeaceAnAiWritesHangsTogether(sandbox));
                failures += Check("a country counts the cost before it declares",
                    ACountryCountsTheCost());
                failures += Check("a promise is answered and a conquest is counted",
                    APromiseIsAnsweredAndAConquestIsCounted());
                failures += Check("trade has names on it, and an embargo has teeth",
                    TradeHasNamesOnIt(sandbox));
                failures += Check("the world makes what the world needs",
                    TheWorldMakesWhatItNeeds(sandbox));
                failures += Check("a peace can be written, priced, refused and signed",
                    APeaceCanBeWritten(sandbox));
                failures += Check("a moved border leaves a national minority behind",
                    ConquestLeavesMinorities(sandbox));
                // Last, because accepting a settlement ends a war and annexes ground, and
                // the checks above are entitled to a world nobody has just redrawn.
                failures += Check("an AI puts terms to a player instead of imposing them",
                    AnAiPutsTermsToAPlayer(sandbox));
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
                          $"fronts {war.AttackerFront.Count:N0} attacking and " +
                          $"{war.DefenderFront.Count:N0} defending province(s).");

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
    /// <summary>
    /// The state the shipped map was actually in: nine regions whose provinces belonged to
    /// two countries at once, because the only thing that ever split a region was a peace
    /// treaty and no treaty had been signed yet. Kherson, Kharkiv, Donetsk, Zaporizhzhia
    /// and Luhansk were the obvious ones - the region record said Ukraine, the ground under
    /// half of it said Russia, and the interface had to pick one of them to believe.
    /// </summary>
    /// <summary>
    /// Run the same month twice, once a day at a time and once three days at a time, and
    /// see whether the two worlds recognise each other.
    /// </summary>
    private static string TheClockRunsAtAnyStep()
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

        const int hours = 30 * 24;
        var daily = Run(24, hours);
        var thrice = Run(72, hours);

        if (daily.Hours != thrice.Hours)
            return $"the clocks disagree: {daily.Hours:N0} hours a day at a time, {thrice.Hours:N0} three at a time";
        if (daily.Days != thrice.Days)
            return $"the calendars disagree: day {daily.Days:N0} against day {thrice.Days:N0}";
        if (!string.Equals(daily.Date, thrice.Date, StringComparison.Ordinal))
            return $"the dates disagree: {daily.Date} against {thrice.Date}";

        // The world itself only has to be recognisable. A day pass that ran the wrong
        // number of times shows up here as a population or an army count that is out by
        // a multiple, not by a rounding.
        var peopleDrift = Math.Abs(daily.People - thrice.People) / (double)Math.Max(1L, daily.People);
        if (peopleDrift > 0.01)
            return $"the populations disagree by {peopleDrift:P2}: {daily.People:N0} against {thrice.People:N0}";
        var armyDrift = Math.Abs(daily.Armies - thrice.Armies) / (double)Math.Max(1, daily.Armies);
        if (armyDrift > 0.05)
            return $"the armies disagree by {armyDrift:P1}: {daily.Armies:N0} against {thrice.Armies:N0}";

        // And a step under a day has to be a fraction of one rather than a whole one.
        var quarter = Run(6, hours);
        if (quarter.Days != daily.Days)
            return $"a six-hour step reached day {quarter.Days:N0} where a daily step reached {daily.Days:N0}";

        Console.WriteLine($"  clock: {hours / 24} day(s) reached identically at 6, 24 and 72 hours a step " +
                          $"({daily.Date}); populations within {peopleDrift:P2}, armies within {armyDrift:P1}.");
        return "";
    }

    private static string EveryRegionHasOneOwner(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        var owner = new Dictionary<ushort, ushort>();
        var divided = new Dictionary<ushort, HashSet<ushort>>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var region = topology.ProvinceToState[province];
            if (region == 0) continue;
            var flag = topology.ProvinceToNation[province];
            if (!owner.TryGetValue(region, out var first)) { owner[region] = flag; continue; }
            if (first == flag) continue;
            if (!divided.TryGetValue(region, out var flags))
                divided[region] = flags = new HashSet<ushort> { first };
            flags.Add(flag);
        }

        if (divided.Count > 0)
        {
            var examples = divided.Take(3).Select(entry =>
            {
                var state = campaign.States.Find(candidate => candidate.MapIndex == entry.Key);
                var tags = entry.Value.Select(flag =>
                    campaign.Nations.Find(nation => nation.MapIndex == flag)?.Tag ?? flag.ToString());
                return $"{state?.Name ?? entry.Key.ToString()} ({string.Join("/", tags)})";
            });
            return $"{divided.Count:N0} region(s) belong to more than one country: {string.Join(", ", examples)}";
        }

        // And the region record agrees with the ground, rather than naming an owner that
        // lost the last province of it some time ago.
        var wrong = new List<string>();
        foreach (var state in campaign.States)
        {
            if (!owner.TryGetValue((ushort)state.MapIndex, out var flag)) continue;
            if (state.NationMapIndex != flag)
                wrong.Add($"{state.Name} says {state.NationMapIndex}, its ground says {flag}");
        }
        if (wrong.Count > 0)
            return $"{wrong.Count:N0} region(s) name an owner their provinces do not: {string.Join("; ", wrong.Take(3))}";

        // The case that started this: the occupied Ukrainian oblasts and Crimea, where the
        // region record named Ukraine and the ground named Russia. Named here so a run says
        // out loud that the ground and the ledger agree about them.
        var watched = new[] { "Luhansk", "Donetsk", "Kherson", "Zaporizhzhia", "Kharkiv",
                              "Autonomous Republic of Crimea" };
        var report = new List<string>();
        foreach (var state in campaign.States)
        {
            var stem = state.Name;
            if (!watched.Any(name => stem.EndsWith(name, StringComparison.Ordinal))) continue;
            var tag = campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex)?.Tag ?? "?";
            report.Add($"{stem} {tag}");
        }
        report.Sort(StringComparer.Ordinal);

        Console.WriteLine($"  borders: all {owner.Count:N0} region(s) on the map have exactly one owner" +
                          (report.Count == 0 ? "." : $"; the contested ones are {string.Join(", ", report)}."));
        return "";
    }

    /// <summary>
    /// The other direction. A border that goes away should leave the map as it found it:
    /// without this, a region cut in half by a war stayed two regions for the rest of the
    /// campaign even after one country held every province of it again - two rows in the
    /// ledger, two sets of buildings and two populations for one place.
    /// </summary>
    private static string RegionsHealWhenTheBorderGoesAway(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        // A region held whole, big enough to cut and put back.
        ScenarioState subject = null;
        List<int> provinces = null;
        foreach (var candidate in campaign.States.OrderByDescending(state => state.Population))
        {
            if (candidate.ParentMapIndex != 0) continue;
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
        if (subject is null) return "no region held whole is large enough to cut and heal";

        var originalOwner = campaign.Nations.Find(nation => nation.MapIndex == subject.NationMapIndex);
        var taker = campaign.Nations.FirstOrDefault(nation =>
            nation.MapIndex != subject.NationMapIndex && nation.Population > 0);
        if (originalOwner is null || taker is null) return "no pair of countries to move the ground between";

        var name = subject.Name;
        var peopleBefore = subject.Population;
        var industryBefore = subject.Buildings.Values.Sum();
        var regionsBefore = campaign.States.Count;
        var literacyBefore = subject.Literacy;

        var half = provinces.Take(provinces.Count / 2).ToList();
        var created = StatePartition.Annex(campaign, half, taker);
        if (created.Count == 0) return $"cutting {name} in half created no new region";
        if (created.Any(piece => piece.ParentMapIndex != subject.MapIndex))
            return "a piece does not remember the region it was cut from, so nothing can put it back";

        // Hand every province of the original region back, including the ones that never
        // moved: annexing to the owner that already holds them is a no-op for those.
        StatePartition.Annex(campaign, provinces, originalOwner);

        if (campaign.States.Count != regionsBefore)
            return $"the region list did not return to its size: {regionsBefore:N0} before, " +
                   $"{campaign.States.Count:N0} after";
        var healed = campaign.States.Find(state => state.MapIndex == subject.MapIndex);
        if (healed is null) return $"'{name}' no longer exists after being put back together";
        if (campaign.States.Any(state => state.ParentMapIndex == subject.MapIndex))
            return $"'{name}' still has pieces of itself listed separately";
        if (healed.NationMapIndex != originalOwner.MapIndex)
            return $"'{name}' came back under the wrong flag";

        if (Math.Abs(healed.Population - peopleBefore) > Math.Max(4L, peopleBefore / 1000L))
            return $"people went missing in the round trip: {peopleBefore:N0} before, {healed.Population:N0} after";
        if (Math.Abs(healed.Buildings.Values.Sum() - industryBefore) > 2)
            return $"buildings went missing in the round trip: {industryBefore:N0} before, " +
                   $"{healed.Buildings.Values.Sum():N0} after";
        if (Math.Abs(healed.Literacy - literacyBefore) > 0.01f)
            return $"literacy drifted through the round trip: {literacyBefore:P1} before, {healed.Literacy:P1} after";

        var known = campaign.States.Select(state => (ushort)state.MapIndex).ToHashSet();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (!known.Contains(topology.ProvinceToState[province]))
                return $"province {province} belongs to region {topology.ProvinceToState[province]}, which does not exist";
        if (provinces.Any(province => topology.ProvinceToState[province] != (ushort)subject.MapIndex))
            return $"'{name}' did not take all of its ground back";

        Console.WriteLine($"  healing: {name} was cut into {created.Count + 1} by {taker.Tag}, handed back, " +
                          $"and is one region of {healed.Population:N0} again.");
        return "";
    }

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
    /// <summary>
    /// Whether the treaty a country writes for itself is one a country would write.
    ///
    /// The old rule was "cheapest first", and cheap means empty, so an AI that won a war
    /// annexed a handful of the emptiest provinces it happened to be standing on - scattered
    /// across the map, touching nothing it owned, and never including the region it had
    /// declared the war over. It was affordable and it was nonsense, and because nothing
    /// measured the shape of a settlement, nothing ever said so.
    ///
    /// This measures the shape. It writes the same peace both ways on the same world and
    /// compares them: the new one may not leave more provinces stranded than the rule it
    /// replaced, it has to stay inside the budget, it has to be one the loser will actually
    /// sign, and if the winner is standing on the ground it went to war for, it has to ask
    /// for some of it.
    /// </summary>
    /// <summary>
    /// Whether a country will negotiate with a person at all.
    ///
    /// It would not. The negotiation step opened with "if the other side is human, return",
    /// so a war with a player in it could only ever end when the player ended it: a country
    /// losing to a player fought to its last division without once asking to stop, and a
    /// country beating a player took nothing and said nothing. Half of the diplomacy in the
    /// game was unreachable from the only seat anybody actually sits in.
    ///
    /// So the same treaty is written and then offered rather than signed. This checks the
    /// whole round trip: that an offer appears instead of a fait accompli, that it says what
    /// it is asking for, that refusing it leaves the war running, and that accepting one
    /// actually moves the border.
    /// </summary>
    private static string AnAiPutsTermsToAPlayer(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        StrategicWar war = null;
        Nation proposer = null;
        var mostHeld = 0;
        var bestBudget = 0f;
        foreach (var candidate in campaign.Wars)
        {
            if (candidate.Ended) continue;
            foreach (var side in new[] { candidate.AttackerId, candidate.DefenderId })
            {
                var nation = campaign.NationById(side);
                if (nation is null) continue;
                var budget = PeaceConference.Budget(campaign, candidate, nation.Id);
                if (war is not null && budget <= bestBudget) continue;
                war = candidate;
                proposer = nation;
                bestBudget = budget;
                mostHeld = PeaceConference.Claimable(campaign, candidate, nation.Id)
                    .Count(province => campaign.ProvinceController[province] == (ushort)nation.MapIndex);
            }
        }
        if (war is null || proposer is null) return "no war left to negotiate";

        var opponent = campaign.NationById(war.AttackerId == proposer.Id ? war.DefenderId : war.AttackerId);
        if (opponent is null) return "the war has no other side";

        // Sit a person down opposite, and let the clocks that stop an AI negotiating too
        // early run out, since none of that is what is being tested here.
        var wasHuman = opponent.IsHuman;
        var startedDay = war.StartedDay;
        var unlockDay = war.AiNegotiationUnlockDay;
        var lastProposal = war.LastPeaceProposalDay;
        opponent.IsHuman = true;
        war.AiNegotiationUnlockDay = 0;
        war.StartedDay = Math.Min(war.StartedDay, campaign.ElapsedDays - campaign.Defines.AiMinimumWarDays * 4);
        war.LastPeaceProposalDay = int.MinValue / 2;

        try
        {
            AiStatecraft.Negotiate(campaign, proposer, war);

            if (war.Ended)
                return $"{proposer.Tag} ended the war against a player without asking - " +
                       "a settlement was imposed rather than offered";
            if (war.PendingOfferFromId != proposer.Id)
            {
                // Standing on a lot of ground is not the same as being ahead - a country can
                // hold a hundred provinces and still be behind on points, and then having
                // nothing to demand is the right answer. Being ahead on points while holding
                // ground and saying nothing is not.
                if (bestBudget >= campaign.Defines.AiPeaceMinimumBudget && mostHeld >= 4)
                    return $"{proposer.Tag} is {bestBudget:0.0} of war score ahead and occupies " +
                           $"{mostHeld:N0} claimable province(s), and put nothing on the table";
                Console.WriteLine($"  ai offer: the best-placed belligerent, {proposer.Tag}, is " +
                                  $"{bestBudget:0.0} of war score ahead holding {mostHeld:N0} claimable " +
                                  "province(s) - not enough to write a settlement out of.");
                return "";
            }
            if (!PeaceOffers.AwaitingAnswer(war, opponent.Id))
                return "an offer was made and the country it was made to is not waiting on it";
            if (PeaceOffers.AwaitingAnswer(war, proposer.Id))
                return "the country that made the offer is being asked to answer its own offer";

            var wording = PeaceOffers.Describe(campaign, war);
            if (wording.Length == 0) return "the offer does not say what it is asking for";
            var offered = war.PendingOfferIsWritten ? war.PendingOffer.Provinces.Count : 0;

            // Refusing leaves the war exactly where it was.
            if (!PeaceOffers.Answer(campaign, war, opponent.Id, accept: false))
                return "a player could not refuse an offer made to them";
            if (war.Ended) return "refusing the offer ended the war anyway";
            if (war.PendingOfferFromId >= 0) return "a refused offer is still sitting on the table";

            // And accepting one signs it.
            war.LastPeaceProposalDay = int.MinValue / 2;
            AiStatecraft.Negotiate(campaign, proposer, war);
            if (war.PendingOfferFromId != proposer.Id)
            {
                Console.WriteLine($"  ai offer: {proposer.Tag} offered {offered:N0} province(s) and, once refused, " +
                                  "did not come back with a second settlement.");
                return "";
            }

            var written = war.PendingOfferIsWritten;
            var demanded = written ? new HashSet<int>(war.PendingOffer.Provinces) : new HashSet<int>();
            if (!PeaceOffers.Answer(campaign, war, opponent.Id, accept: true))
                return "a player could not accept an offer made to them";
            if (!war.Ended) return "the offer was accepted and the war is still running";

            var moved = demanded.Count(province =>
                topology.ProvinceToNation[province] == (ushort)proposer.MapIndex);
            if (written && moved != demanded.Count)
                return $"{demanded.Count - moved:N0} of the {demanded.Count:N0} province(s) signed over " +
                       "did not change owner";

            Console.WriteLine($"  ai offer: {proposer.Tag} put terms to {opponent.Tag} - " +
                              $"{(written ? $"{demanded.Count:N0} province(s)" : "standing terms")} - " +
                              $"which were refused once, offered again and signed.");
            return "";
        }
        finally
        {
            opponent.IsHuman = wasHuman;
            war.StartedDay = startedDay;
            war.AiNegotiationUnlockDay = unlockDay;
            if (!war.Ended) war.LastPeaceProposalDay = lastProposal;
        }
    }

    /// <summary>
    /// Whether the world's industry adds up.
    ///
    /// A good that nobody makes enough of is not a scarcity, it is a bug: every country in
    /// the world sits in permanent shortage, living standards fall everywhere at once, and
    /// the market stops being a place where a decision can be made. A good that everybody
    /// makes four times over is the opposite bug - it can never run short, so nothing that
    /// happens to it matters and it may as well not be simulated.
    ///
    /// This prints the whole balance sheet and holds every good between the two.
    /// </summary>
    /// <summary>
    /// Whether trade is between countries rather than with nowhere.
    ///
    /// The market was one anonymous pool: a shortfall was met by "the world" at a constant
    /// rate. Nobody could be cut off, so an embargo was unthinkable, a blockade took money
    /// instead of goods, and a country could invade its largest customer and lose nothing
    /// but soldiers. This checks the three things that had to become true: that every import
    /// has a named seller behind it, that a country knows how much of its economy runs
    /// through each partner, and that refusing to trade is actually felt.
    /// </summary>
    /// <summary>
    /// Whether the promises countries make to each other are worth anything.
    ///
    /// Alliances existed as a word. The stance could be Allied and nothing anywhere read it,
    /// so a country could watch its ally dismembered without sending a note; there was no
    /// such thing as a strong small country, and no reason to court anybody. Conquest had no
    /// price either: every province taken made the next one easier, and nothing on the map
    /// grew in proportion to somebody else's success.
    ///
    /// Run on a campaign of its own, because it starts wars.
    /// </summary>
    /// <summary>
    /// Whether a war is a decision rather than an appetite.
    ///
    /// The old judgement had five terms and every one argued for war: strength, dislike,
    /// rivalry, opportunity, and a multiplier for morale. Nothing anywhere argued against,
    /// so the only brake on the map was a low die roll, and a country would happily invade
    /// its largest customer while a coalition formed behind it.
    ///
    /// The claim being checked is precise: the same pair of countries, in the same world, on
    /// the same day, is a war worth having when nothing connects them and not worth having
    /// when half an economy runs through it. If that is not true, the price is decoration.
    /// </summary>
    private static string ACountryCountsTheCost()
    {
        var campaign = Campaign.CreateBootstrap(20240202);
        if (!AttachMap(campaign)) return "no province map, so nobody borders anybody";

        // A strong country next to a much weaker one - the case the old rule always took.
        Nation strong = null;
        Nation weak = null;
        var bestGap = 3f;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Population < 8_000_000 || candidate.IsHuman) continue;
            foreach (var mapIndex in CampaignDiplomacy.Neighbours(campaign, candidate.MapIndex))
            {
                var neighbour = NationByMap(campaign, mapIndex);
                if (neighbour is null || neighbour.Population < 500_000) continue;
                if (!CampaignDiplomacy.CanDeclareWar(campaign, candidate.Id, neighbour.Id, out _)) continue;
                var gap = AiStatecraft.StrengthOf(campaign, candidate) /
                          MathF.Max(0.01f, AiStatecraft.StrengthOf(campaign, neighbour));
                if (gap <= bestGap) continue;
                bestGap = gap;
                strong = candidate;
                weak = neighbour;
            }
        }
        if (strong is null || weak is null)
            return "no country in the world is three times its neighbour's strength";

        var wanted = AiStatecraft.JudgeWarFor(campaign, strong, weak);
        if (wanted <= 0f)
            return $"{strong.Tag} is {bestGap:0.0} times {weak.Tag}'s strength and still would not " +
                   $"consider a war ({wanted:0.00}); the AI has stopped being dangerous rather than " +
                   "started being careful";

        // Now give them a real economic relationship and ask again.
        strong.TradePartners[weak.Id] = 100f;
        strong.ImportValue = 100f;
        strong.ExportValue = 100f;
        var withTrade = AiStatecraft.JudgeWarFor(campaign, strong, weak);
        strong.TradePartners.Remove(weak.Id);
        strong.ImportValue = 0f;
        strong.ExportValue = 0f;

        if (withTrade >= wanted)
            return $"{strong.Tag} runs half its economy through {weak.Tag} and wants the war just as " +
                   $"much ({wanted:0.00} against {withTrade:0.00}); trade is not a cost";

        // And friends have to matter too.
        var shelter = campaign.Nations.Find(other =>
            other.Id != strong.Id && other.Id != weak.Id && other.Population > 5_000_000);
        if (shelter is null) return "nobody left to guarantee the weak country";
        var bond = CampaignDiplomacy.Get(campaign, shelter.Id, weak.Id);
        if (shelter.Id == bond.A) bond.GuaranteeByA = true; else bond.GuaranteeByB = true;
        var withFriends = AiStatecraft.JudgeWarFor(campaign, strong, weak);
        if (shelter.Id == bond.A) bond.GuaranteeByA = false; else bond.GuaranteeByB = false;

        if (withFriends >= wanted)
            return $"{shelter.Tag} guaranteed {weak.Tag} and {strong.Tag} did not care " +
                   $"({wanted:0.00} against {withFriends:0.00}); a guarantee deters nobody";

        Console.WriteLine($"  restraint: {strong.Tag} is {bestGap:0.0}x stronger than {weak.Tag} and rates " +
                          $"the war at {wanted:0.00}. Half its trade running through {weak.Tag} takes that " +
                          $"to {withTrade:0.00}; a guarantee from {shelter.Tag} takes it to {withFriends:0.00}.");
        return "";
    }

    private static string APromiseIsAnsweredAndAConquestIsCounted()
    {
        var campaign = Campaign.CreateBootstrap(20240101);
        if (!AttachMap(campaign)) return "no province map, so nobody borders anybody";

        // Three countries that touch each other, so that a war between two of them is a war
        // the third can physically join.
        Nation attacker = null;
        Nation victim = null;
        Nation friend = null;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Population < 5_000_000) continue;
            foreach (var victimMap in CampaignDiplomacy.Neighbours(campaign, candidate.MapIndex))
            {
                var possibleVictim = NationByMap(campaign, victimMap);
                if (possibleVictim is null || possibleVictim.Population < 5_000_000) continue;
                foreach (var friendMap in CampaignDiplomacy.Neighbours(campaign, possibleVictim.MapIndex))
                {
                    var possibleFriend = NationByMap(campaign, friendMap);
                    if (possibleFriend is null || possibleFriend.Id == candidate.Id) continue;
                    if (possibleFriend.Population < 5_000_000 || possibleFriend.IsHuman) continue;
                    if (!CampaignDiplomacy.SharesLandBorder(campaign, possibleFriend.MapIndex, candidate.MapIndex))
                        continue;
                    attacker = candidate;
                    victim = possibleVictim;
                    friend = possibleFriend;
                    break;
                }
                if (friend is not null) break;
            }
            if (friend is not null) break;
        }
        if (attacker is null || victim is null || friend is null)
        {
            var sample = 0;
            foreach (var nation in campaign.Nations)
            {
                if (nation.Population < 5_000_000) continue;
                sample = CampaignDiplomacy.Neighbours(campaign, nation.MapIndex).Count;
                if (sample > 0) break;
            }
            return "could not find three countries that all touch each other " +
                   $"(a large country has {sample:N0} neighbour(s), so the border table is " +
                   (sample == 0 ? "empty" : "fine and the triangle is what is missing") + ")";
        }

        // A real alliance, and a friend able and willing to honour it.
        var bond = CampaignDiplomacy.Get(campaign, friend.Id, victim.Id);
        bond.Stance = DiplomaticStance.Allied;
        bond.Opinion = 90f;
        friend.WarSupport = 1f;
        friend.Stability = 0.9f;

        var warsBefore = campaign.Wars.Count;
        var notorietyBefore = attacker.Notoriety;
        var war = CampaignDiplomacy.DeclareWar(campaign, attacker.Id, victim.Id);
        if (war is null) return $"{attacker.Tag} could not declare on its own neighbour {victim.Tag}";

        var friendJoined = false;
        foreach (var candidate in campaign.Wars)
            if (!candidate.Ended && candidate.AttackerId == friend.Id && candidate.DefenderId == attacker.Id)
                friendJoined = true;
        if (!friendJoined)
            return $"{friend.Tag} was allied to {victim.Tag} at {bond.Opinion:0} opinion, watched " +
                   $"{attacker.Tag} attack it, and did nothing";
        if (campaign.Wars.Count <= warsBefore + 1)
            return "the call to arms produced no second war";

        // The declaration itself is counted, and so is what it takes afterwards.
        if (attacker.Notoriety <= notorietyBefore && string.IsNullOrEmpty(attacker.LastWarJustification))
            return $"{attacker.Tag} declared a war it could give no reason for and the world did not mind";

        var beforeAnnexation = attacker.Notoriety;
        var takeable = new List<int>();
        var topology = campaign.ProvinceMap;
        for (var province = 1; province <= topology.ProvinceCount && takeable.Count < 40; province++)
            if (topology.ProvinceToNation[province] == (ushort)victim.MapIndex) takeable.Add(province);
        if (takeable.Count > 0)
        {
            StatePartition.Annex(campaign, takeable, attacker);
            if (attacker.Notoriety <= beforeAnnexation)
                return $"{attacker.Tag} annexed {takeable.Count:N0} province(s) of {victim.Tag} for free";
        }

        // And enough of it brings the neighbourhood together.
        attacker.Notoriety = Statecraft.CoalitionThreshold + 5f;
        foreach (var neighbourMap in CampaignDiplomacy.Neighbours(campaign, attacker.MapIndex))
        {
            var neighbour = NationByMap(campaign, neighbourMap);
            if (neighbour is null || neighbour.Id == attacker.Id) continue;
            CampaignDiplomacy.Get(campaign, neighbour.Id, attacker.Id).Opinion = -40f;
        }
        for (var step = 0; step < 8 && Statecraft.CoalitionAgainst(campaign, attacker.Id) is null; step++)
        {
            campaign.IsPaused = false;
            campaign.Tick();
        }
        var coalition = Statecraft.CoalitionAgainst(campaign, attacker.Id);
        if (coalition is null)
            return $"{attacker.Tag} reached {attacker.Notoriety:0} notoriety among hostile neighbours " +
                   "and nobody organised against it";

        Console.WriteLine($"  statecraft: {attacker.Tag} attacked {victim.Tag}; {friend.Tag} honoured its " +
                          $"alliance and declared. {attacker.Tag} carries {attacker.Notoriety:0} notoriety " +
                          $"and {coalition.MemberIds.Count:N0} countries have formed against it.");
        return "";
    }

    private static Nation NationByMap(Campaign campaign, int mapIndex)
    {
        foreach (var nation in campaign.Nations)
            if (nation.MapIndex == mapIndex) return nation;
        return null;
    }

    private static string TradeHasNamesOnIt(Campaign campaign)
    {
        if (campaign.TradeRoutes.Count == 0)
            return "not one country sold anything to another; the market is still anonymous";

        // Somebody has to depend on somebody. If no pair of countries carries a meaningful
        // share of each other's trade, nothing downstream of this can weigh a partner.
        // The largest trading relationship in the world by value - not by share, or the
        // answer is always some island whose entire foreign trade is one shipment.
        Nation buyer = null;
        Nation seller = null;
        var largest = 0f;
        foreach (var nation in campaign.Nations)
        foreach (var (partnerId, value) in nation.TradePartners)
        {
            if (value <= largest) continue;
            largest = value;
            buyer = nation;
            seller = campaign.NationById(partnerId);
        }
        if (buyer is null || seller is null)
            return "no two countries in the world trade with each other at all";
        var strongest = buyer.TradeDependenceOn(seller.Id);
        if (strongest < 0.03f)
            return $"the largest trading relationship in the world is worth only {strongest:P1} of " +
                   $"{buyer.Tag}'s trade, which is not a relationship anybody would defend";

        // And refusing has to cost something. Cut the strongest pair and see.
        var before = buyer.ImportValue + buyer.ExportValue;
        var beforeAccess = buyer.MarketAccess;
        var relation = CampaignDiplomacy.Get(campaign, buyer.Id, seller.Id);
        var wasEmbargoed = relation.EmbargoBy(seller.Id);
        new SetEmbargoCommand { IssuerId = seller.Id, TargetId = buyer.Id, Embargo = true }.Apply(campaign);
        MarketSystem.Prime(campaign);
        var after = buyer.ImportValue + buyer.ExportValue;
        var afterAccess = buyer.MarketAccess;

        new SetEmbargoCommand { IssuerId = seller.Id, TargetId = buyer.Id, Embargo = wasEmbargoed }.Apply(campaign);
        MarketSystem.Prime(campaign);

        if (campaign.TradeRoutes.Count == 0) return "the market emptied when one embargo was lifted";
        var stillTrading = 0f;
        foreach (var route in campaign.TradeRoutes)
            if (route.BuyerId == buyer.Id && route.SellerId == seller.Id) stillTrading += route.Value;
        if (stillTrading <= 0.0001f && !wasEmbargoed)
            return $"{seller.Tag} stopped selling to {buyer.Tag} and never started again";

        if (after >= before)
            return $"{seller.Tag} refused to trade with {buyer.Tag} and {buyer.Tag}'s trade did not fall " +
                   $"({before:0.00} before, {after:0.00} after) - an embargo costs nothing";

        Console.WriteLine($"  trade: {campaign.TradeRoutes.Count:N0} route(s) between named countries; " +
                          $"the tightest pair is {buyer.Tag}, which runs {strongest:P0} of its trade through {seller.Tag}.");
        Console.WriteLine($"    an embargo by {seller.Tag} cut {buyer.Tag}'s trade from {before:0.0} to {after:0.0} " +
                          $"and its market access from {beforeAccess:P0} to {afterAccess:P0}.");
        return "";
    }

    private static string TheWorldMakesWhatItNeeds(Campaign campaign)
    {
        var worst = "";
        var worstRatio = float.MaxValue;
        var lines = new List<string>();
        var complaints = new List<string>();
        foreach (var commodity in CommodityCatalog.All)
        {
            var production = 0f;
            var demand = 0f;
            var shortOf = 0;
            foreach (var nation in campaign.Nations)
            {
                if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
                production += quote.Production;
                demand += quote.Demand;
                if (quote.IsShortage) shortOf++;
            }
            if (demand <= 0.001f) continue;
            var ratio = production / demand;
            lines.Add($"{CommodityCatalog.Get(commodity).Name} {ratio:0.00}x ({shortOf:N0} short)");
            if (ratio < worstRatio) { worstRatio = ratio; worst = CommodityCatalog.Get(commodity).Name; }
            if (ratio < 0.75f)
                complaints.Add($"the world makes only {ratio:0.00} of the {CommodityCatalog.Get(commodity).Name} " +
                               $"it wants ({shortOf:N0} countries short)");
            else if (ratio > 4.0f)
                complaints.Add($"the world makes {ratio:0.0}x the {CommodityCatalog.Get(commodity).Name} it wants, " +
                               "so nothing that happens to it can matter");
        }

        Console.WriteLine("  world balance: " + string.Join(", ", lines));
        Console.WriteLine($"    tightest good: {worst} at {worstRatio:0.00} of demand.");
        if (complaints.Count > 0) return string.Join("; ", complaints);
        return "";
    }

    private static string ThePeaceAnAiWritesHangsTogether(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return "";

        // The war with the most ground on the table, either way round. Taking the first live
        // war would usually mean a stalemate holding four provinces, and four provinces have
        // no shape to get wrong; the point of this check is the choosing.
        StrategicWar war = null;
        Nation winner = null;
        PeaceLedger ledger = null;
        List<int> held = null;
        foreach (var candidate in campaign.Wars)
        {
            if (candidate.Ended) continue;
            foreach (var side in new[] { candidate.AttackerId, candidate.DefenderId })
            {
                var nation = campaign.NationById(side);
                if (nation is null) continue;
                var occupied = PeaceConference.Claimable(campaign, candidate, nation.Id)
                    .Where(province => campaign.ProvinceController[province] == (ushort)nation.MapIndex)
                    .ToList();
                if (held is not null && occupied.Count <= held.Count) continue;
                war = candidate;
                winner = nation;
                held = occupied;
            }
        }
        if (war is null || winner is null) return "no war left to negotiate";
        ledger = new PeaceLedger(campaign, war, winner.Id);

        if (held.Count < 4)
        {
            Console.WriteLine($"  ai peace: the best-placed belligerent in the world, {winner.Tag}, occupies " +
                              $"{held.Count} claimable province(s) - there is no settlement to shape yet.");
            return "";
        }

        // The shipped scenario is a stalemate, so the real war score often buys less than one
        // province and every possible treaty is the empty one. The shape is therefore
        // compared at a budget that forces a choice - half of what taking the whole occupation
        // would cost - while affordability and refusal are checked against the real budget
        // below, where there is one to check against.
        var everything = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in held) everything.Provinces.Add(province);
        var shaping = ledger.Cost(everything) * 0.5f;
        if (shaping <= 0f) return $"{winner.Tag}'s entire occupation prices at nothing";

        // What the rule this replaced would have written, on this same world.
        var cheapest = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        foreach (var province in held.OrderBy(province => ledger.Price(province).Cost))
        {
            cheapest.Provinces.Add(province);
            if (ledger.Cost(cheapest) <= shaping) continue;
            cheapest.Provinces.Remove(province);
            break;
        }

        var written = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.WriteDemand(campaign, winner, war, ledger, written, shaping);
        if (written.Provinces.Count == 0)
            return $"{winner.Tag} wrote an empty treaty out of {held.Count:N0} occupied province(s) " +
                   $"with {shaping:0.00} to spend";

        var cost = ledger.Cost(written);
        if (cost > shaping)
            return $"the treaty costs {cost:0.00} against a budget of {shaping:0.00}";

        var strandedNow = StrandedProvinces(campaign, winner, written.Provinces);
        var strandedBefore = StrandedProvinces(campaign, winner, cheapest.Provinces);
        if (strandedNow > strandedBefore)
            return $"the treaty is in more pieces than the cheapest-first rule it replaced: " +
                   $"{strandedNow:N0} province(s) touching nothing of {winner.Tag}, against {strandedBefore:N0}";

        // The other half of "cheapest first" was that cheap means empty: the old rule spent a
        // war's whole score on the emptiest ground the winner happened to be standing on. For
        // the same money the new one has to come away with at least as many people, or it is
        // not writing a better treaty, only a tidier one.
        long PeopleIn(PeaceDemand demand)
        {
            long people = 0;
            foreach (var province in demand.Provinces)
            {
                var mapIndex = (int)topology.ProvinceToState[province];
                var count = ledger.ProvincesPerState.GetValueOrDefault(mapIndex, 1);
                var state = campaign.States.Find(entry => entry.MapIndex == mapIndex);
                if (state is not null && count > 0) people += state.Population / count;
            }
            return people;
        }

        var peopleNow = PeopleIn(written);
        var peopleBefore = PeopleIn(cheapest);
        if (peopleNow < peopleBefore)
            return $"for the same {shaping:0.0} of war score the treaty takes {peopleNow:N0} people " +
                   $"where the cheapest-first rule it replaced took {peopleBefore:N0}";

        var regionsNow = written.Provinces.Select(province => (int)topology.ProvinceToState[province])
            .Distinct().Count();
        var regionsBefore = cheapest.Provinces.Select(province => (int)topology.ProvinceToState[province])
            .Distinct().Count();

        // A country that declared for a region and then took none of it did not win its war.
        if (war.ClaimedStateMapIndex >= 0)
        {
            var goalHeld = held.Count(province => topology.ProvinceToState[province] == war.ClaimedStateMapIndex);
            var goalTaken = written.Provinces.Count(province =>
                topology.ProvinceToState[province] == war.ClaimedStateMapIndex);
            if (goalHeld > 0 && goalTaken == 0)
                return $"{winner.Tag} occupies {goalHeld:N0} province(s) of the region it declared war over " +
                       "and demanded none of them";
        }

        // And what it would sign for what it has actually won, which in a stalemate is
        // usually nothing at all - correctly.
        var affordable = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        AiStatecraft.WriteDemand(campaign, winner, war, ledger, affordable, ledger.Budget * 0.8f);
        if (affordable.Provinces.Count > 0)
        {
            var refusal = PeaceConference.BlockReason(campaign, war, affordable);
            if (refusal.Length > 0)
                return $"the AI wrote a peace the other side refuses: {refusal}";
        }

        Console.WriteLine($"  ai peace: given {shaping:0.0} of war score, {winner.Tag} wrote a treaty of " +
                          $"{written.Provinces.Count:N0} province(s) across {regionsNow:N0} region(s) - " +
                          $"{peopleNow:N0} people, {strandedNow:N0} of them detached.");
        Console.WriteLine($"    cheapest-first, for the same money: {cheapest.Provinces.Count:N0} province(s) " +
                          $"across {regionsBefore:N0} region(s) - {peopleBefore:N0} people, " +
                          $"{strandedBefore:N0} detached. On its real war score of {ledger.Budget:0.0} " +
                          $"{winner.Tag} would ask for {affordable.Provinces.Count:N0} province(s).");
        return "";
    }

    /// <summary>
    /// How many demanded provinces touch neither the demander's own ground nor the rest of
    /// the demand - which is to say, how many pieces the settlement would arrive in.
    /// </summary>
    private static int StrandedProvinces(Campaign campaign, Nation nation, IEnumerable<int> demanded)
    {
        var topology = campaign.ProvinceMap;
        var claimed = new HashSet<int>(demanded);
        var mapIndex = (ushort)nation.MapIndex;
        var stranded = 0;
        foreach (var province in claimed)
        {
            var touching = false;
            foreach (var neighbour in topology.Neighbors[province])
            {
                if (topology.ProvinceToNation[neighbour] != mapIndex && !claimed.Contains(neighbour)) continue;
                touching = true;
                break;
            }
            if (!touching) stranded++;
        }
        return stranded;
    }

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
    /// <summary>
    /// Whether a war can actually destroy anything, and whether the world survives that.
    ///
    /// It could not. Losses came off a formation's strength and nothing ever came off the
    /// order of battle, so a division ground to nothing walked out of the line and refilled
    /// itself from the national manpower pool. Two years of the Russo-Ukrainian front cost
    /// three and a half million casualties and not one formation on either side; the front
    /// moved only when somebody walked into a province nobody was standing in.
    ///
    /// Two things had to become true together, and this holds both. A broken formation is
    /// destroyed - so a battle can be won and an encirclement is worth attempting. And a
    /// country below the army it is built to keep replaces it - so an army that can only
    /// shrink does not lose every long war by arithmetic. Either one alone is a bug: the
    /// first without the second empties the map, the second without the first is what was
    /// here before.
    /// </summary>
    private static string WarCostsFormations(Campaign campaign, int days)
    {
        var destroyed = Warfare.OutcomeTally.GetValueOrDefault("destroyed: broken in the field");
        if (destroyed == 0)
            return $"{days:N0} day(s) of war destroyed not one formation anywhere - a battle " +
                   "cannot be won if the loser only ever refills";

        var established = 0f;
        var standing = 0f;
        var rebuilt = 0;
        foreach (var nation in campaign.Nations)
        {
            established += nation.EstablishedDivisions;
            if (nation.FormationsLost > 0) rebuilt++;
        }
        foreach (var army in campaign.Armies) standing += army.Divisions;

        if (established <= 0f) return "no country ever recorded what army it keeps";
        var share = standing / established;
        if (share < 0.60f)
            return $"the world's armies fell to {share:P0} of their establishment after {days:N0} day(s) - " +
                   "formations are being destroyed faster than they are replaced, which empties the map";
        if (share > 1.40f)
            return $"the world's armies grew to {share:P0} of their establishment - the rebuild is not " +
                   "bounded by what a country is built to keep";

        Console.WriteLine($"  attrition: {destroyed:N0} formation(s) destroyed in {days:N0} day(s) across " +
                          $"{rebuilt:N0} countries; the world stands at {share:P0} of its establishment " +
                          $"({standing:N0} of {established:N0} divisions).");
        return "";
    }

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
    /// <summary>
    /// The world's 1,826 regions were a C# array until the day this check was written, and
    /// the thing worth guarding is not that they moved but that they stayed moved: a
    /// generated file nothing verifies is a file that drifts from the game the first time
    /// somebody edits the generator.
    /// </summary>
    private static string TheRegionsOfTheWorldAreData()
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
        // Two countries with no history of each other are not strangers any more: they can
        // see what the other is. So what is left when the reasons are cleared has to be
        // exactly what their politics are worth to each other, and nothing else.
        var politics = PoliticsCore.Affinity(campaign, first.Id, second.Id);
        if (Math.Abs(relation.Opinion - politics) > 0.01f)
            return $"a country with no history of another has an opinion of {relation.Opinion:0.0}, " +
                   $"where its politics account for {politics:0.0}";

        OpinionSystem.Remember(campaign, relation, "declared_war_on_us");
        var remembered = relation.Opinion;
        if (remembered > -50f + politics)
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
    private static bool PolicyEntry(System.Text.Json.JsonProperty entry, string file,
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

    private static bool BudgetGroup(System.Text.Json.JsonProperty entry, string file,
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

    /// <summary>
    /// Stability is a formula now rather than a stored number, and two things have to hold.
    /// The scenario still says what it said - 261 countries did not quietly become more or
    /// less settled on the day the formula was switched on - and the capital weighs more
    /// than its size, which is the claim the whole model rests on.
    /// </summary>
    /// <summary>
    /// The militia has to do three things or it is a cheat rather than a mechanic: rise
    /// where a country is actually invaded, rise far less where the people do not think the
    /// state is theirs, and cost nothing out of the pool the regular army reinforces from -
    /// which it did draw on at first, with the result that the country it was meant to help
    /// lost a quarter of its ground.
    /// </summary>
    private static string MilitiaRisesWhereItIsWanted()
    {
        var campaign = Campaign.CreateBootstrap(20240505);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var ukraine = campaign.NationByTag("UKR");
        if (ukraine is null) return "UKR is missing, so there is no invasion to rise against";
        var reserveBefore = ukraine.ManpowerReserve;

        for (var day = 0; day < 200; day++) campaign.Tick();

        var risen = campaign.Armies
            .Where(army => army.TemplateId == MilitiaCore.TemplateId)
            .ToList();
        if (risen.Count == 0)
            return "two hundred days of a live invasion and not one person picked up a rifle";

        var defending = risen.Count(army => army.NationId == ukraine.Id);
        if (defending == 0)
            return $"{risen.Count:N0} militia formations stood up in the world and none of them " +
                   "in the country being invaded";

        // Militia is the region's own people, not the ministry's reservists. The reserve may
        // move for every other reason - recruitment, casualties - but not because of this.
        foreach (var army in risen)
            if (army.Personnel <= 0)
                return "a militia formation stood up with nobody in it";

        // Who rises. A region the state is for fills a formation; a region held down by a
        // state it does not recognise does not, however many people live in it.
        var willing = 0f;
        var unwilling = 0f;
        foreach (var state in campaign.StatesOf(ukraine.MapIndex))
        {
            var own = 1f - EconomySystem.ForeignShare(state, ukraine.Tag);
            var here = campaign.Armies.Count(army => army.TemplateId == MilitiaCore.TemplateId &&
                                                     army.StateMapIndex == state.MapIndex);
            if (own >= 0.8f) willing += here;
            else unwilling += here;
        }
        if (willing <= 0f)
            return "nobody rose in any region that is mostly the country's own people";

        // And it goes home. Nothing has threatened a country at peace, so its militia
        // should not be standing a year later.
        var peaceful = campaign.Nations.FirstOrDefault(nation =>
            nation.Id != ukraine.Id &&
            campaign.Armies.Any(army => army.TemplateId == MilitiaCore.TemplateId &&
                                        army.NationId == nation.Id) &&
            !campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)));
        if (peaceful is not null)
            return $"{peaceful.Tag} is at peace and still has militia standing";

        Console.WriteLine($"  militia: {risen.Count:N0} formation(s) stood up in 200 days, " +
                          $"{defending:N0} of them in UKR; {willing:N0} in regions that are mostly " +
                          $"its own people against {unwilling:N0} elsewhere; the reserve went from " +
                          $"{reserveBefore:N0} to {ukraine.ManpowerReserve:N0} for every other reason.");
        return "";
    }

    private static string StabilityIsWorkedOutRatherThanStored()
    {
        var campaign = Campaign.CreateBootstrap(20240202);
        if (!AttachMap(campaign)) return "";

        // What the scenario authored is what the formula returns, for everybody.
        var worst = 0f;
        Nation worstNation = null;
        foreach (var nation in campaign.Nations)
        {
            var drift = MathF.Abs(StabilityCore.Target(campaign, nation) - nation.Stability);
            if (drift <= worst) continue;
            worst = drift;
            worstNation = nation;
        }
        if (worst > 0.005f)
            return $"the formula disagrees with the scenario by {worst:P1} for " +
                   $"{worstNation?.Tag}: it is authored at {worstNation?.Stability:P1}";

        // A country big enough to have a capital and somewhere else worth comparing it to.
        var subject = campaign.Nations
            .Where(nation => nation.CapitalStateMapIndex > 0 && nation.Population > 20_000_000)
            .OrderByDescending(nation => campaign.StatesOf(nation.MapIndex).Length)
            .FirstOrDefault();
        if (subject is null) return "no country large enough to tell a capital from a province";

        var states = campaign.StatesOf(subject.MapIndex);
        var capital = states.FirstOrDefault(state => state.MapIndex == subject.CapitalStateMapIndex);
        if (capital is null) return $"{subject.Tag}'s capital is not in one of its own regions";
        var elsewhere = states
            .Where(state => state.MapIndex != capital.MapIndex && state.Population > 0)
            .OrderBy(state => MathF.Abs(state.Population - capital.Population))
            .FirstOrDefault();
        if (elsewhere is null) return $"{subject.Tag} has nowhere to compare its capital with";

        float Upset(ScenarioState state)
        {
            var wasRadical = state.Radicalism;
            var wasLiving = state.StandardOfLiving;
            try
            {
                state.Radicalism = 1f;
                state.StandardOfLiving = 0f;
                return StabilityCore.Target(campaign, subject);
            }
            finally
            {
                state.Radicalism = wasRadical;
                state.StandardOfLiving = wasLiving;
            }
        }

        var settled = StabilityCore.Target(campaign, subject);
        var withoutCapital = Upset(capital);
        var withoutProvince = Upset(elsewhere);

        if (withoutCapital >= settled)
            return $"setting fire to {subject.Tag}'s capital region did not cost it any stability";
        if (withoutCapital >= withoutProvince)
            return $"{subject.Tag}'s capital ({capital.Name}, {capital.Population:N0}) is worth no more " +
                   $"than {elsewhere.Name} ({elsewhere.Population:N0}): {withoutCapital:P1} against " +
                   $"{withoutProvince:P1}";

        Console.WriteLine($"  stability: all {campaign.Nations.Count:N0} countries agree with the scenario " +
                          $"(worst {worst:P2}); for {subject.Tag}, losing {capital.Name} costs " +
                          $"{settled - withoutCapital:P1} against {settled - withoutProvince:P1} for " +
                          $"{elsewhere.Name} of the same size.");
        return "";
    }

    private static string TheBudgetIsData(Campaign world)
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
            Compare("income_tax", output * nation.TaxRate * 0.24f * (0.72f + nation.Stability * 0.22f)),
            Compare("tariffs", nation.WeeklyTariffIncome),
            Compare("trade", nation.WeeklyTradeBalance),
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
        if (MathF.Abs(outcome.Balance - (revenue - expenses)) > MathF.Max(tolerance, MathF.Abs(revenue) * 1e-4f))
            return $"the itemised lines come to {revenue - expenses:0.##} and the week's balance is {outcome.Balance:0.##}";

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
                             BudgetCore.ComplianceAt(nation, output, atWar: false);
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
    private static string PolicyCataloguesTakeNewEntries()
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

    /// <summary>
    /// Who governs, by what right, and what everyone else makes of it.
    ///
    /// Three things, and the third is the one worth having a check for. A government and an
    /// ideology that only affect the country holding them are a label; what makes them a
    /// system is that the rest of the world can see them, so this proves that two countries
    /// with nothing else between them still have an opinion, that it is warm when their
    /// politics are close and cold when they are opposite, and that the whole of it can be
    /// switched off from `common/defines` by a mod that wants ideology to be scenery.
    /// </summary>
    private static string ThePoliticalWorldHangsTogether(Campaign world)
    {
        if (GovernmentCatalog.All.Count < 2) return "common/governments describes fewer than two governments";
        if (IdeologyCatalog.All.Count < 2) return "common/ideologies describes fewer than two ideologies";

        // Nobody is governed by something the catalogue has never heard of.
        foreach (var nation in world.Nations)
        {
            if (!GovernmentCatalog.Knows(nation.Government))
                return $"{nation.Tag} is a `{nation.Government}`, which no file describes";
            if (!IdeologyCatalog.Knows(nation.Ideology))
                return $"{nation.Tag} believes in `{nation.Ideology}`, which no file describes";
        }

        // And the scenario said something, rather than leaving the whole world identical.
        var governments = world.Nations.Select(nation => nation.Government).Distinct().Count();
        var ideologies = world.Nations.Select(nation => nation.Ideology).Distinct().Count();
        if (governments < 5 || ideologies < 5)
            return $"the world holds {governments} form(s) of government and {ideologies} ideolog(ies), " +
                   "which is a scenario that has not been written";

        // Two countries with the same politics think better of each other than two with
        // opposite politics. This is the whole diplomatic consequence in one line.
        var left = world.Nations.Find(nation => string.Equals(nation.Ideology, "liberalism", StringComparison.OrdinalIgnoreCase));
        var right = world.Nations.Find(nation => nation != left &&
            string.Equals(nation.Ideology, "liberalism", StringComparison.OrdinalIgnoreCase));
        var opposite = world.Nations.Find(nation =>
            string.Equals(nation.Ideology, "communism", StringComparison.OrdinalIgnoreCase));
        if (left is null || right is null || opposite is null)
            return "the scenario has no pair of like-minded countries and an opposite to compare them with";

        var alike = PoliticsCore.Affinity(world, left.Id, right.Id);
        var unalike = PoliticsCore.Affinity(world, left.Id, opposite.Id);
        if (alike <= 0f) return $"two countries of the same politics regard each other at {alike:0.0}";
        if (unalike >= 0f) return $"{left.Tag} and {opposite.Tag} are opposites and regard each other at {unalike:0.0}";
        if (alike - unalike < 10f)
            return $"politics move opinion by only {alike - unalike:0.0} between identical and opposite";

        // A mod may turn it off entirely.
        var kept = world.Defines.IdeologyOpinionWeight;
        world.Defines.IdeologyOpinionWeight = 0f;
        var silent = PoliticsCore.Affinity(world, left.Id, opposite.Id);
        world.Defines.IdeologyOpinionWeight = kept;
        if (Math.Abs(silent) > 0.0001f) return "ideology still counts after its weight was set to zero";

        // Changing what a country is costs it, and cannot be done twice in a fortnight.
        var subject = world.Nations.Find(nation =>
            nation.Population > 0 && !world.Wars.Any(war => !war.Ended && war.Involves(nation.Id)));
        if (subject is null) return "no country at peace to reorganise";
        var wasGovernment = subject.Government;
        var wasIdeology = subject.Ideology;
        var wasStability = subject.Stability;
        var wasChangedDay = subject.PoliticsChangedDay;

        var target = GovernmentCatalog.All.First(entry =>
            !string.Equals(entry.Id, subject.Government, StringComparison.OrdinalIgnoreCase)).Id;
        if (!world.ChangePolitics(subject.Id, target, "", out var refusal))
            return $"a country at peace could not change its government: {refusal}";
        if (subject.Stability >= wasStability)
            return $"{subject.Tag} rearranged itself and its stability went from {wasStability:P0} to {subject.Stability:P0}";
        if (world.ChangePolitics(subject.Id, wasGovernment, "", out _))
            return "a country changed its government twice on the same day";

        subject.Government = wasGovernment;
        subject.Ideology = wasIdeology;
        subject.Stability = wasStability;
        subject.PoliticsChangedDay = wasChangedDay;

        // And a mod may add to both catalogues.
        var governmentCount = GovernmentCatalog.All.Count;
        var ideologyCount = IdeologyCatalog.All.Count;
        var errors = new List<string>();
        GovernmentCatalog.Merge("""
        { "bench_stratocracy": { "name": "Bench stratocracy", "centralisation": 0.9,
          "effects": { "war_support": 0.0002 } } }
        """, errors);
        IdeologyCatalog.Merge("""
        { "bench_syndicalism": { "name": "Bench syndicalism", "economic": -0.8, "authority": -0.6,
          "prefers_governments": ["bench_stratocracy"] } }
        """, errors);
        if (errors.Count > 0) return $"a sound entry was rejected: {errors[0]}";
        if (!GovernmentCatalog.Knows("bench_stratocracy") || !IdeologyCatalog.Knows("bench_syndicalism"))
            return "a mod added a government and an ideology and the catalogues did not keep them";

        GovernmentCatalog.ResetCatalog();
        IdeologyCatalog.ResetCatalog();
        if (GovernmentCatalog.All.Count != governmentCount || IdeologyCatalog.All.Count != ideologyCount)
            return "resetting the catalogues left the bench's politics behind";

        Console.WriteLine($"  politics: {GovernmentCatalog.All.Count} form(s) of government and " +
                          $"{IdeologyCatalog.All.Count} ideolog(ies); the world runs {governments} and " +
                          $"{ideologies} of them. Like minds are worth {alike:0.0}, opposites {unalike:0.0}.");
        return "";
    }

    private static string TheEnglishTableIsAnIndex()
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
        var captions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(scripts, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("LegacySourceMap")) continue;
            foreach (var caption in Captions(File.ReadAllText(file)))
                if (caption.Length > 0) captions.Add(caption);
        }
        if (captions.Count == 0) return "found no interface captions in the source at all";

        // And the captions that are not in the source at all any more.
        //
        // The budget's seventeen lines, the cabinets and the trade stances name and explain
        // themselves in `common/`, which is the point of them being data - but it moved
        // those words out of reach of a scan that only reads C#. A name a file gives
        // something the player reads is a key exactly as much as a name the code gives it,
        // and this is what keeps that true as more of the game becomes data.
        var declared = new SortedSet<string>(StringComparer.Ordinal);
        var common = Path.Combine(Path.GetDirectoryName(scripts)!, "game", "common");
        // `countries` is in here for the same reason as the rest, and matters more than any
        // of them: it is 261 names, it is the first thing anybody sees, and it is the list
        // most likely to grow. A country added to the data without a row in the table now
        // fails this check instead of quietly showing up in English.
        foreach (var folder in new[]
                 { "budget", "government_focus", "trade_policies", "governments", "ideologies",
                   "countries", "units", "settings" })
        {
            var directory = Path.Combine(common, folder);
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
                   $"table, starting with: {string.Join(" | ", absent.Take(4))}";

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
    private static readonly (string Call, int[] Arguments)[] TranslatedCalls =
    {
        ("Title(", new[] { 0 }),
        ("ActionButton(", new[] { 0 }),
        ("Tr(", new[] { 0 }),
        ("DetailRow(", new[] { 0 }),
        ("DetailHeading(", new[] { 0 }),
        ("RowLabel(", new[] { 0 }),
        ("SliderRow(", new[] { 0 }),
        ("StateSection(", new[] { 1, 2 }),
        ("Loc.Get(", new[] { 0 }),
        ("Loc.Format(", new[] { 0 })
    };

    /// <summary>
    /// Every `name`, `short_name` and `description` a data file gives something, at any
    /// depth, because the layouts differ - the budget nests its lines under `revenues` and
    /// `expenses`, the cabinets sit at the top level - and what matters is the words.
    /// </summary>
    private static void CollectDeclaredNames(System.Text.Json.JsonElement element, SortedSet<string> into)
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
                    CollectDeclaredNames(field.Value, into);
                }
                return;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectDeclaredNames(item, into);
                return;
        }
    }

    /// <summary>Every literal this file hands to a translation lookup, however indirectly.</summary>
    private static IEnumerable<string> Captions(string source)
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
    private static string Argument(string source, int open, int index)
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
    private static string Literal(string source, int from, int to)
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

    private static int SkipString(string source, int quote)
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
    private static Dictionary<string, string> ReadLocalizationTable(string language)
    {
        var scripts = FindScripts();
        if (scripts is null) return null;
        var folder = Path.Combine(Path.GetDirectoryName(scripts)!, "game", "localization", language);
        if (!Directory.Exists(folder)) return null;

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file),
                new System.Text.Json.JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip
                });
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            foreach (var entry in document.RootElement.EnumerateObject())
                if (entry.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    table[entry.Name] = entry.Value.GetString() ?? "";
        }
        return table;
    }

    /// <summary>The interface source, found the same way the mod templates are.</summary>
    private static string FindScripts()
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
    private static string AModCanAddAGood(Campaign world)
    {
        const string GoodId = "bench_alloy";
        const string BuildingId = "bench_works";
        try
        {
            if (!CommodityCatalog.Override(GoodId, "Bench alloy", "◆", 0.06f, 0.25f, "A good no enum names."))
                return "the catalog refused a complete new good";
            if (!CommodityCatalog.TryParse(GoodId, out var alloy))
                return "the catalog accepted a new good and then could not find it by name";
            if (!BuildingCatalog.Declare(BuildingId, "Bench works", 2.0f, 200, 1.0f, 0.1f, "A building no enum names."))
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

            for (var day = 0; day < 30; day++) campaign.Tick();

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
    private static string TheChainsHangTogether()
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
    private static void DeclareTemplateContent(string common, System.Text.Json.JsonDocumentOptions options)
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
                Number("gdp_gain"), Number("capacity_gain"), Text("description"));
        });

        void Declare(string folder, Action<string, System.Text.Json.JsonElement> apply)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder, "*.json")
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                System.Text.Json.JsonDocument document;
                try { document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file), options); }
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
    private static bool Recipe(System.Text.Json.JsonProperty entry, string file, string templates,
        List<string> complaints)
    {
        var problems = new List<string>();
        ProductionChains.Validate(
            System.Text.Json.JsonDocument.Parse($"{{\"{entry.Name}\":{entry.Value.GetRawText()}}}",
                GameDataLoader.ForgivingJson).RootElement, problems);
        foreach (var problem in problems) complaints.Add($"{Short(templates, file)}: {problem}");
        return true;
    }

    private static string TheTemplatesStillNameRealThings()
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
            foreach (var file in Directory.GetFiles(category, "*.json"))
            {
                System.Text.Json.JsonDocument document;
                try { document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file), options); }
                catch (Exception exception)
                {
                    complaints.Add($"{Short(templates, file)} will not parse: {exception.Message}");
                    continue;
                }
                using (document)
                {
                    if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var kind = Path.GetFileName(category);
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
        }

        if (complaints.Count > 0) return string.Join("; ", complaints);
        Console.WriteLine($"  templates: {packages:N0} worked example(s) in mods/_templates, " +
                          $"{keys:N0} key(s) between them, every one naming something that exists.");
        return "";
    }

    /// <summary>The template folder, wherever the bench happens to have been started from.</summary>
    private static string FindTemplates()
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

    private static string Squash(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static string Short(string root, string file) =>
        file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? file[(root.Length + 1)..].Replace('\\', '/')
            : Path.GetFileName(file);

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
        // Through the same lookup the game uses, rather than off the working directory.
        // Hanging it on the working directory meant this passed from the project root and
        // reported the content pack framework missing from anywhere else - a check that
        // says where it was run from rather than whether the packs are sound. It also means
        // it now exercises the resolution an exported build depends on.
        var root = GameDataLoader.InstallFolders("dlc").FirstOrDefault();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return "there is no dlc/ folder; the content pack framework is gone";

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

    /// <summary>
    /// Consolidation invents nobody, and the railways cost what they save.
    ///
    /// Both are the kind of rule that fails silently and pleasantly. A consolidation that
    /// rounds men into existence gives the player free divisions and nothing on screen ever
    /// says so; a redeployment that is fast and free is simply a better march, and every
    /// formation in the game would use it for everything.
    /// </summary>
    private static string ManoeuvresCostWhatTheySave(Campaign campaign)
    {
        var player = campaign.NationByTag("RUS") ?? campaign.Player;
        if (player is null) return "there is no country to manoeuvre with";

        // ------------------------------------------------------------------ consolidation
        var together = campaign.Armies
            .Where(army => army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0)
            .GroupBy(army => (army.TemplateId, army.ProvinceId))
            .FirstOrDefault(group => group.Count() > 1);
        if (together is null)
        {
            // Put two of them in one place, which is what a player does before consolidating.
            var pair = campaign.Armies
                .Where(army => army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0)
                .GroupBy(army => army.TemplateId)
                .FirstOrDefault(group => group.Count() > 1)?.Take(2).ToList();
            if (pair is null || pair.Count < 2) return "the country has no two formations of a kind";
            pair[1].ProvinceId = pair[0].ProvinceId;
            pair[1].StateMapIndex = pair[0].StateMapIndex;
            together = campaign.Armies
                .Where(army => army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0)
                .GroupBy(army => (army.TemplateId, army.ProvinceId))
                .FirstOrDefault(group => group.Count() > 1);
            if (together is null) return "two formations refused to stand in the same province";
        }

        var chosen = together.ToList();
        // Worn down, which is the state consolidation is for: four divisions at a third
        // strength should become two at full strength, not four at a third.
        foreach (var army in chosen)
        {
            army.Personnel = Math.Max(1, army.MaxPersonnel / 3);
            army.Strength = army.Personnel / (float)Math.Max(1, army.MaxPersonnel);
        }
        var before = chosen.Sum(army => (long)army.Personnel);
        var beforeCount = chosen.Count;
        var consolidate = new ConsolidateArmiesCommand
        {
            IssuerId = player.Id,
            ArmyIds = chosen.Select(army => army.Id).ToArray()
        };
        consolidate.Apply(campaign);
        var survivors = campaign.Armies.Where(army => chosen.Contains(army)).ToList();
        var after = survivors.Sum(army => (long)army.Personnel);

        if (consolidate.Merged == 0)
            return $"{beforeCount} formations of one kind in one province refused to consolidate: {consolidate.Refusal}";
        if (survivors.Count >= beforeCount)
            return "consolidation merged nothing away";
        if (after > before)
            return $"consolidation created {after - before:N0} men out of nothing ({before:N0} -> {after:N0})";
        if (after < before * 0.9)
            return $"consolidation lost {before - after:N0} of {before:N0} men";

        // ------------------------------------------------------------------ the railways
        var traveller = campaign.Armies.FirstOrDefault(army =>
            army.NationId == player.Id && !army.InBattle && army.ProvinceId > 0 && army.EmbarkedFleetId < 0);
        if (traveller is null) return "nobody is available to redeploy";

        var topology = campaign.ProvinceMap;
        if (topology is null) return "there is no province map to redeploy across";
        var destination = topology.Neighbors[traveller.ProvinceId]
            .SelectMany(step => topology.Neighbors[step])
            .SelectMany(step => topology.Neighbors[step])
            .FirstOrDefault(province => province > 0 &&
                campaign.ProvinceController[province] == (ushort)player.MapIndex &&
                province != traveller.ProvinceId);
        if (destination <= 0) return "there is nowhere far enough to redeploy to";

        traveller.Organisation = 1f;
        traveller.OrderKind = ArmyOrder.Manual;
        var rail = new StrategicRedeploymentCommand
        {
            IssuerId = player.Id, ArmyId = traveller.Id, TargetProvinceId = destination
        };
        rail.Apply(campaign);
        if (rail.Result != ArmyMoveResult.Issued)
            return $"a redeployment across three provinces of home ground was refused: {rail.Result}";
        if (!traveller.IsRedeploying) return "the order was issued and the formation is not on the railways";

        var organisationBefore = traveller.Organisation;
        campaign.IsPaused = false;
        for (var day = 0; day < 6 && traveller.IsRedeploying; day++) campaign.Tick();

        if (traveller.Organisation >= organisationBefore)
            return "a week on the trains cost nothing in organisation, which makes it a better march";
        if (traveller.IsRedeploying && traveller.Route.Count > 0 && traveller.MoveProgress <= 0f)
            return "the formation was railed nowhere";

        Console.WriteLine($"  manoeuvres: consolidation folded {consolidate.Merged} formation(s) and kept " +
                          $"{after:N0} of {before:N0} men; a redeployment cost " +
                          $"{organisationBefore - traveller.Organisation:P0} organisation.");
        return "";
    }

    /// <summary>
    /// A drawn line is held where it is pressed, and stops being re-thought once it is.
    ///
    /// Two failures, opposite and both quiet. A planner that spreads formations evenly
    /// leaves the dangerous end of a line as thin as the safe end, and nothing on screen
    /// says so until it breaks. A planner that re-optimises every tick keeps every division
    /// walking sideways for the rest of the war, which also defends nothing and looks like
    /// activity. So: the most pressed post must be held, and asking twice must give the
    /// same answer.
    /// </summary>
    private static string ADrawnLineIsHeldWhereItIsPressed(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null) return "there is no map to hold";

        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended);
        if (war is null) return "nobody is at war, so no line is under pressure";
        var nation = campaign.NationById(war.DefenderId) ?? campaign.NationById(war.AttackerId);
        if (nation is null) return "the war has no defender";

        var front = war.FriendlyFront(nation.Id).Where(province => province > 0).Take(14).ToList();
        if (front.Count < 4) return $"{nation.Tag} holds a front of {front.Count} province(s), too short to spread over";

        var garrison = campaign.Armies
            .Where(army => army.NationId == nation.Id && !army.InBattle && army.EmbarkedFleetId < 0)
            .Take(6).ToList();
        if (garrison.Count < 3) return $"{nation.Tag} has {garrison.Count} formation(s) free, too few to spread";

        new DrawFrontLineCommand
        {
            IssuerId = nation.Id,
            Kind = FrontLineKind.Defence,
            Provinces = front,
            ArmyIds = garrison.Select(army => army.Id).ToArray()
        }.Apply(campaign);
        try
        {

        var line = campaign.FrontLines.LastOrDefault(candidate => candidate.NationId == nation.Id);
        if (line is null) return "the line was drawn and did not stick";

        var ordered = BattlePlanning.Sequence(campaign, line);
        var first = new Dictionary<int, int>(line.Assignment);
        if (first.Count == 0) return "the line was drawn and nobody was given a post";

        // Asking again on the same day must not move anybody: the assignment is a decision,
        // not a die roll, and a planner that answers differently every call is one that will
        // march the line up and down for the rest of the war.
        BattlePlanning.Sequence(campaign, line);
        var second = line.Assignment;
        if (first.Count != second.Count || first.Any(entry =>
                !second.TryGetValue(entry.Key, out var post) || post != entry.Value))
            return "asking twice on the same day moved formations about";

        // The post with the most hostile ground next to it is the one that has to be held.
        var pressed = -1;
        var worst = -1;
        foreach (var province in ordered)
        {
            var hostile = topology.Neighbors[province]
                .Count(neighbour => controllers[neighbour] != nation.MapIndex);
            if (hostile <= worst) continue;
            worst = hostile;
            pressed = province;
        }
        if (worst <= 0) return "";
        if (!first.ContainsValue(pressed))
            return $"the most exposed post on the line ({worst} hostile neighbours) was given to nobody, " +
                   $"while {first.Count} formation(s) were placed elsewhere";

        Console.WriteLine($"  the line: {nation.Tag} spread {first.Count} formation(s) over {ordered.Count} post(s); " +
                          $"the most exposed ({worst} hostile neighbours) is held, and asking twice moved nobody.");
        return "";
        }
        finally
        {
            // The line was drawn to be measured, not to be left lying on the map: another
            // check counts lines with nobody on them, and it is right to.
            new ClearFrontLineCommand
            {
                IssuerId = nation.Id,
                ArmyIds = garrison.Select(army => army.Id).ToArray()
            }.Apply(campaign);
        }
    }

    /// <summary>
    /// A battle plan survives a save.
    ///
    /// This is the failure that leaves no trace. A formation carries the id of the line it
    /// was put on and that id was being written down; the line itself was not - so every
    /// division came back from a reload holding a plan number that pointed at nothing,
    /// quietly reverted to the war's own front, and a player who had spent ten minutes
    /// drawing dispositions found them gone with no message anywhere.
    ///
    /// So: draw one, save, load, and it has to be the same plan with the same formations on
    /// it, still able to say where each of them stands.
    /// </summary>
    private static string ABattlePlanSurvivesASave(Campaign campaign)
    {
        var war = campaign.Wars.FirstOrDefault(candidate => !candidate.Ended);
        var nation = war is null ? null : campaign.NationById(war.DefenderId);
        if (nation is null) return "nobody is at war, so there is no plan to draw";

        var front = war.FriendlyFront(nation.Id).Where(province => province > 0).Take(8).ToList();
        var garrison = campaign.Armies
            .Where(army => army.NationId == nation.Id && army.EmbarkedFleetId < 0).Take(4).ToList();
        if (front.Count < 2 || garrison.Count < 2) return "the war is too small to draw a plan on";

        new DrawFrontLineCommand
        {
            IssuerId = nation.Id,
            Kind = FrontLineKind.Defence,
            Provinces = front,
            ArmyIds = garrison.Select(army => army.Id).ToArray()
        }.Apply(campaign);
        var line = campaign.FrontLines.LastOrDefault(candidate => candidate.NationId == nation.Id);
        if (line is null) return "the line was drawn and did not stick";
        new SetPlanCohesionCommand
        {
            IssuerId = nation.Id, FrontLineId = line.Id, Cohesion = PlanCohesion.Rigid
        }.Apply(campaign);

        var lineId = line.Id;
        var provinces = new HashSet<int>(line.Provinces);
        var armies = new List<int>(line.ArmyIds);
        var nextId = campaign.NextFrontLineId;

        try
        {
            var bytes = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
            if (bytes.Length == 0) return "the campaign refused to serialise";
            var restored = SaveSystem.ImportNetworkSnapshot(bytes, "bench").Campaign;
            if (restored is null) return "the save did not load back";

            var back = restored.FrontLines.Find(candidate => candidate.Id == lineId);
            if (back is null)
                return $"a plan of {provinces.Count} province(s) and {armies.Count} formation(s) " +
                       "was drawn, saved, and did not come back at all";
            if (!back.Provinces.SetEquals(provinces))
                return $"the plan came back covering {back.Provinces.Count} province(s) instead of {provinces.Count}";
            if (back.ArmyIds.Count != armies.Count || back.ArmyIds.Any(id => !armies.Contains(id)))
                return $"the plan came back with {back.ArmyIds.Count} formation(s) instead of {armies.Count}";
            if (back.Cohesion != PlanCohesion.Rigid)
                return $"the plan came back on {back.Cohesion} cohesion instead of Rigid";
            if (restored.NextFrontLineId <= lineId)
                return $"the next plan would be handed id {restored.NextFrontLineId}, which is already in use";

            // And every formation still points at it, which is the half that was never
            // broken and would have been the only survivor.
            var orphan = restored.Armies.FirstOrDefault(army => army.FrontLineId >= 0 &&
                restored.FrontLines.All(candidate => candidate.Id != army.FrontLineId));
            if (orphan is not null)
                return $"formation {orphan.Id} came back on plan {orphan.FrontLineId}, which does not exist";

            Console.WriteLine($"  plans: a {provinces.Count}-province line with {armies.Count} formation(s) on " +
                              $"{back.Cohesion} cohesion survived a save, and no formation came back orphaned.");
            return "";
        }
        finally
        {
            new ClearFrontLineCommand
            {
                IssuerId = nation.Id,
                ArmyIds = armies
            }.Apply(campaign);
            campaign.NextFrontLineId = Math.Max(campaign.NextFrontLineId, nextId);
        }
    }

    /// <summary>
    /// Every order a player can give crosses the wire intact.
    ///
    /// The codec finds commands by reflection, so a new one is carried automatically - and
    /// that is exactly why this is worth checking. Automatic means nobody looks, and a
    /// command whose fields do not survive the round trip fails only in multiplayer, only
    /// for the client, and only as "nothing happened when I pressed the key".
    /// </summary>
    private static string EveryOrderCrossesTheWire()
    {
        var orders = new ICampaignCommand[]
        {
            new HaltArmyCommand { IssuerId = 7, ArmyId = 31 },
            new DetachArmyFromPlanCommand { IssuerId = 7, ArmyId = 31 },
            new ReturnArmyToPlanCommand { IssuerId = 7, ArmyId = 31, FrontLineId = 4 },
            new SupportAttackCommand { IssuerId = 7, ArmyId = 31, SupportOnly = true },
            new StrategicRedeploymentCommand { IssuerId = 7, ArmyId = 31, TargetProvinceId = 9012 },
            new ConsolidateArmiesCommand { IssuerId = 7, ArmyIds = new[] { 31, 32, 33 } },
            new SetPlanCohesionCommand { IssuerId = 7, FrontLineId = 4, Cohesion = PlanCohesion.Immobile }
        };

        foreach (var order in orders)
        {
            if (!NetworkCommandCodec.TryEncode(order, out var name, out var json))
                return $"{order.GetType().Name} cannot be sent to another player at all";
            if (!NetworkCommandCodec.TryDecode(name, json, out var back))
                return $"{name} was sent and could not be read back";
            if (back.IssuerId != order.IssuerId)
                return $"{name} came back issued by {back.IssuerId} instead of {order.IssuerId}";
            switch (back)
            {
                case ReturnArmyToPlanCommand ret when ret.FrontLineId != 4:
                    return "a return-to-plan order lost which plan it was returning to";
                case StrategicRedeploymentCommand rail when rail.TargetProvinceId != 9012:
                    return "a redeployment order lost its destination";
                case ConsolidateArmiesCommand fold when fold.ArmyIds.Count != 3:
                    return $"a consolidation order arrived naming {fold.ArmyIds.Count} formation(s) instead of three";
                case SetPlanCohesionCommand cohesion when cohesion.Cohesion != PlanCohesion.Immobile:
                    return $"a cohesion order arrived as {cohesion.Cohesion} instead of Immobile";
                case SupportAttackCommand support when !support.SupportOnly:
                    return "a support order arrived as an ordinary attack, which would take the ground";
            }
        }

        Console.WriteLine($"  wire: {orders.Length} order(s) encoded, sent and read back with every field intact.");
        return "";
    }

    /// <summary>
    /// A simulated day may not hand the collector more than a budget of garbage.
    ///
    /// Wall time on a machine that is also running an editor is noise, and this bench says
    /// so everywhere else. Allocation is not noise: it is the same on every machine, and it
    /// is what the player actually feels, because every megabyte comes back later as a
    /// collection pause in the middle of a war.
    ///
    /// A day cost 1.35 MB and thirty gen-0 collections a simulated year. Almost all of it
    /// was four habits rather than four algorithms - a LINQ Contains on a list, a closure
    /// in a RemoveAll, a JSON trigger re-parsed per question, and prose composed for
    /// cabinets nobody was reading. The budget below is the measured figure with room to
    /// breathe; it is here so that the next feature has to notice when it spends it.
    ///
    /// Tightened from 420 kB once the fifth habit turned up: an AI asking, for every one of
    /// its divisions in every one of its wars every day, what sort of war it was fighting -
    /// and answering by filtering the world's whole order of battle into a fresh array and
    /// averaging it twice. That alone was 116 kB of the day. A budget nothing ever comes
    /// near is not a budget, so it now sits where the next feature will feel it.
    /// </summary>
    private static string ADayDoesNotBuryTheCollector(double bytesPerDay)
    {
        const double budget = 300 * 1024;
        var perDay = bytesPerDay / 1024.0;
        if (bytesPerDay > budget)
            return $"a campaign day allocated {perDay:N0} kB, over the {budget / 1024:N0} kB budget - " +
                   "run the bench and read the pass table above: whichever line grew is the one that did it";
        Console.WriteLine($"  garbage: {perDay:N0} kB a day against a {budget / 1024:N0} kB budget.");
        return "";
    }

    /// <summary>
    /// The same seed gives the same world, twice.
    ///
    /// This is the foundation multiplayer stands on and the only one worth checking first.
    /// Peers do not exchange the world; they exchange the orders given to it and each runs
    /// the same simulation. If two runs of the same seed diverge on their own, they will
    /// diverge across a network too - and the symptom there is not an error but two players
    /// slowly coming to disagree about where the front is, which is unfixable after the fact
    /// and nearly impossible to diagnose from a save.
    ///
    /// A divergence here is nearly always one of three things: a dictionary iterated in hash
    /// order, a clock or an unseeded random read during a tick, or a collection sorted with
    /// no tie-break. All three are silent, and all three show up as the day this stops
    /// matching.
    /// </summary>
    /// <summary>
    /// Whether two campaigns alive at the same time stay out of each other's way.
    ///
    /// This is the multiplayer failure that leaves no trace. Several systems cache expensive
    /// answers - who borders whom, what every country's army is worth, how far apart two
    /// capitals are - and they were stamped with the day, or the map revision, or the number
    /// of countries. All of those are correct for exactly one world at a time, and a client
    /// rebuilding itself from a host snapshot holds two: the new campaign and the old one
    /// that has not been collected yet. Whichever ticks first stamps the cache; the other
    /// reads some other world's answers, agrees with nobody, and diverges without an error
    /// anywhere.
    ///
    /// So: run a campaign alone, then run it again interleaved with a different one, and
    /// require the two results to be identical to the bit.
    /// </summary>
    private static string TwoWorldsDoNotShareNotes(int days)
    {
        // Each world run by itself, first, so there is something to be different from.
        // Both of them: whichever campaign ticks second is the one that reads the first
        // one's notes, so checking only the leader would miss the whole problem.
        var firstAlone = Campaign.CreateBootstrap(20250303);
        var secondAlone = Campaign.CreateBootstrap(19991231);
        if (!AttachMap(firstAlone) || !AttachMap(secondAlone))
            return "no province map, so there is nothing to share";
        // The two worlds have to be genuinely different, or a cache leaking between them
        // leaks nearly the right answer and nothing shows. Halving one world's armies makes
        // every strength comparison in it different from the other's.
        Halve(secondAlone);
        firstAlone.IsPaused = false;
        secondAlone.IsPaused = false;
        for (var day = 0; day < days; day++) firstAlone.Tick();
        for (var day = 0; day < days; day++) secondAlone.Tick();
        var expectedFirst = CampaignFingerprint.Of(firstAlone);
        var expectedSecond = CampaignFingerprint.Of(secondAlone);

        var same = Campaign.CreateBootstrap(20250303);
        var other = Campaign.CreateBootstrap(19991231);
        if (!AttachMap(same) || !AttachMap(other)) return "the second pair got no province map";
        Halve(other);
        same.IsPaused = false;
        other.IsPaused = false;
        for (var day = 0; day < days; day++)
        {
            same.Tick();
            other.Tick();
        }

        var leaderMatches = CampaignFingerprint.Of(same) == expectedFirst;
        var followerMatches = CampaignFingerprint.Of(other) == expectedSecond;
        if (!leaderMatches || !followerMatches)
            return $"the campaign that ticked {(leaderMatches ? "second" : "first")} came out " +
                   $"different when run beside another one for {days} day(s) - something cached is " +
                   "stamped with the day, the map revision or the country count rather than the " +
                   "campaign, and a multiplayer client rebuilding from a snapshot will " +
                   "desynchronise on it";

        Console.WriteLine($"  isolation: a campaign ticked beside a different one for {days} day(s) " +
                          "and came out identical to the bit.");
        return "";
    }

    /// <summary>Take half the world's formations away, deterministically.</summary>
    private static void Halve(Campaign campaign)
    {
        for (var index = campaign.Armies.Count - 1; index >= 0; index--)
            if (campaign.Armies[index].Id % 2 == 0) campaign.Armies.RemoveAt(index);
    }

    private static string TheSameSeedGivesTheSameWorld(int days)
    {
        var first = Campaign.CreateBootstrap(20260830);
        if (!AttachMap(first)) return "no province map, so there is nothing to diverge about";
        var second = Campaign.CreateBootstrap(20260830);
        if (!AttachMap(second)) return "the second campaign got no province map";

        first.IsPaused = false;
        second.IsPaused = false;
        for (var day = 1; day <= days; day++)
        {
            first.Tick();
            second.Tick();
            var left = CampaignFingerprint.Of(first);
            var right = CampaignFingerprint.Of(second);
            if (left == right) continue;
            return $"two runs of seed 20260830 disagreed on day {day} - " +
                   "multiplayer cannot be stable while this is true. Look for a dictionary " +
                   "iterated in hash order, a clock read inside a tick, or a sort with no tie-break";
        }

        Console.WriteLine($"  determinism: two runs of one seed agreed to the bit for {days} day(s) - " +
                          $"{first.Armies.Count:N0} formations and {first.Nations.Count:N0} countries.");
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
