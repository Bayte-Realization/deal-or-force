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

        var setup = Stopwatch.StartNew();
        var campaign = Campaign.CreateBootstrap(20260830);
        setup.Stop();
        Console.WriteLine($"Campaign created in {setup.ElapsedMilliseconds} ms: " +
                          $"{campaign.Nations.Count} nations, {campaign.States.Count} states.");

        var mapped = AttachMap(campaign);
        var frontBefore = new FrontSnapshot(campaign);

        var failures = 0;
        failures += Check("day-zero GDP reproduces the authored 2024 figures", CalibrationHolds(campaign));
        failures += Check("every nation has an output calibration", NationsCalibrated(campaign));
        failures += Check("state ownership index agrees with a linear scan", IndexAgrees(campaign));

        Console.WriteLine();
        Console.WriteLine($"Running {days} campaign day(s)…");

        campaign.IsPaused = false;
        var ukraineTrace = new List<int>();
        var ukraineIndex = campaign.NationByTag("UKR")?.MapIndex ?? -1;
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

        var perDay = run.Elapsed.TotalMilliseconds / days;
        Console.WriteLine($"  {run.ElapsedMilliseconds} ms total, {perDay:0.###} ms per campaign day.");
        Console.WriteLine($"  {campaign.Date} after {campaign.ElapsedDays} day(s).");

        Console.WriteLine();
        Console.WriteLine("Slowest simulation passes (average per run):");
        foreach (var sample in SimProfiler.All
                     .OrderByDescending(sample => sample.AverageMilliseconds)
                     .Take(12))
            Console.WriteLine($"  {sample.Name,-22} {sample.AverageMilliseconds,8:0.###} ms  ×{sample.Runs}");

        Console.WriteLine();
        failures += Check("the economy actually moved", EconomyMoved(campaign));
        failures += Check("scripted content parses and applies", ScriptedContentWorks(campaign));
        failures += Check("modifiers grant, tick and expire", ModifiersWork(campaign));
        failures += Check("no nation ran to a nonsense GDP", GdpSane(campaign));
        failures += Check("the shipped skin matches the compiled defaults", SkinIsCoherent());
        failures += Check("headline figures format the way a player reads them", FiguresReadWell());
        failures += Check("every nation on the map owns ground", EveryNationHasTerritory(campaign));
        if (mapped)
            failures += Check("the Russo-Ukrainian front is still a stalemate",
                FrontIsAStalemate(campaign, frontBefore, ukraineTrace, days));

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
            .Take(5)
            .Select(entry => $"{entry.Key} x{entry.Value}"));
        Console.WriteLine($"  battle outcomes worldwide: {(outcomes.Length == 0 ? "none" : outcomes)}");
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
        if (war.Ended) return "the war ended on its own with nobody playing";
        if (opening > 8f)
            return $"the line moved by {opening:N0} provinces during the opening year " +
                   "with nobody playing; the stalemate budget for the first year is 8";
        if (held < 0.55f)
            return $"Ukraine is down to {held:P0} of the ground it started with after {days} day(s) " +
                   "of nobody playing; the war has been decided by the simulation, not by anyone";
        if (casualties < 20_000f * years)
            return $"only {casualties:N0} casualties in {days} day(s) - the front is frozen, not contested";
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
