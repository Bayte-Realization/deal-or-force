using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Step 2 of docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md: the flag on its mount and one plate of
/// resources at the top left.
///
/// The plate's rules live in Scripts/ui/HudResourcePlate.cs, which this project does not compile
/// (it is game code beside Godot controls); they read nothing but skin metrics, so this test reads
/// the same metrics with the same rule copied below, and a source check keeps the two in step:
/// each copied member must match the game's word for word, so a change to the game's rule fails
/// the test instead of passing on a stale copy.
///
/// What it pins: all twelve figures of `campaign.top_stats` stay reachable (five on the plate, seven
/// behind "…"); thresholds give the right state token exactly at their boundaries; `hud.top` and
/// `hud.flag_mount` resolve and the mount is a plaque, not a shield; every figure the observer and the
/// movement top bars borrow is on the plate in those modes; a healthy country starts 2024 with no
/// figure in trouble; and none of it touches the world.
/// </summary>
static class HudResourceTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("hud resources: " + label);
        Console.WriteLine("PASS hud resources: " + label);
    }

    private const string PrimaryPrefix = "hud.top_stats.primary.";
    private const string StatePrefix = "hud.stat_state.";

    private static string Repo => Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Repo, relative));

    public static void Run()
    {
        InterfaceSkin.ResetCatalog();
        try
        {
            Plate();
            States();
            Layout();
            BorrowedFiguresStayOnThePlate();
            RulesInStep();
            HealthyStart();
        }
        finally { InterfaceSkin.ResetCatalog(); }
    }

    // ---- the rule, as Scripts/ui/HudResourcePlate.cs writes it ------------------------------
    // Copied verbatim, method by method: RulesInStep compares each body below with the game's
    // (comments and spacing aside), so a change to the game's rule fails here until the copy -
    // and with it every check that runs on the copy - is brought along.

    private static bool Narrow;

    private static int Slots => (int)Math.Clamp(Finite(InterfaceSkin.Metric(Narrow ? "hud.resource_slots_narrow" : "hud.resource_slots", 5f), 5f), 1f, 12f);

    private static List<string> Primary(IReadOnlyList<string> topStats)
    {
        var ranked = new List<(float Place, int Index, string Stat)>();
        for (var index = 0; index < topStats.Count; index++)
        {
            var place = Finite(InterfaceSkin.Metric(PrimaryPrefix + topStats[index], 0f), 0f);
            if (place > 0f) ranked.Add((place, index, topStats[index]));
        }
        return ranked.OrderBy(entry => entry.Place).ThenBy(entry => entry.Index)
            .Take(Slots).Select(entry => entry.Stat).ToList();
    }

    private static (List<string> Plate, List<string> Drawer) Arrange(IReadOnlyList<string> topStats,
        IEnumerable<string> borrowed, IEnumerable<string> troubled)
    {
        var plate = Primary(topStats);
        var extra = new HashSet<string>(borrowed ?? Array.Empty<string>(), StringComparer.Ordinal);
        extra.UnionWith(troubled ?? Array.Empty<string>());
        foreach (var stat in topStats)
            if (extra.Contains(stat) && !plate.Contains(stat)) plate.Add(stat);
        var drawer = topStats.Where(stat => !plate.Contains(stat)).ToList();
        return (plate, drawer);
    }

    private enum State { None, Ok, Warning, Bad }

    private static State StateOf(string stat, double value, double gdp, double population)
    {
        var prefix = StatePrefix + stat;
        var warn = InterfaceSkin.Metric(prefix + ".warn", float.NaN);
        var bad = InterfaceSkin.Metric(prefix + ".bad", float.NaN);
        if (!float.IsFinite(warn) || !float.IsFinite(bad) || !double.IsFinite(value)) return State.None;
        if (InterfaceSkin.Metric(prefix + ".per_gdp", 0f) > 0f)
        {
            if (!(gdp > 0d) || !double.IsFinite(gdp)) return State.None;
            value /= gdp;
        }
        else if (InterfaceSkin.Metric(prefix + ".per_population", 0f) > 0f)
        {
            if (!(population > 0d) || !double.IsFinite(population)) return State.None;
            value /= population;
        }
        var at = (float)value;
        if (bad <= warn)
            return at <= bad ? State.Bad : at <= warn ? State.Warning : State.Ok;
        return at >= bad ? State.Bad : at >= warn ? State.Warning : State.Ok;
    }

    private static string Token(State state) => state switch
    {
        State.Bad => "state.negative",
        State.Warning => "state.warning",
        State.Ok => "state.positive",
        _ => ""
    };

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    /// <summary>
    /// One member of a C# file from its signature to the end of its body (balanced braces, or the
    /// `;` of an expression body without them), with comments and spacing taken out, so two
    /// copies of the same code compare equal however they are indented or annotated.
    /// </summary>
    internal static string MemberText(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        var text = Regex.Replace(source[at..], @"//[^\n]*", "");
        var open = text.IndexOf('{');
        var semicolon = text.IndexOf(';');
        if (semicolon >= 0 && (open < 0 || semicolon < open))
            return Regex.Replace(text[..(semicolon + 1)], @"\s+", " ").Trim();
        if (open < 0) return "";
        var depth = 0;
        var end = open;
        for (; end < text.Length; end++)
        {
            if (text[end] == '{') depth++;
            else if (text[end] == '}' && --depth == 0) { end++; break; }
        }
        // An expression body in braces (`=> state switch { ... };`) ends at its semicolon.
        var rest = text[end..];
        if (rest.TrimStart().StartsWith(';')) end += rest.IndexOf(';') + 1;
        return Regex.Replace(text[..end], @"\s+", " ").Trim();
    }

    // ---- the checks -------------------------------------------------------------------------

    private static void Plate()
    {
        var all = CampaignUiCatalog.TopStats;
        Check(all.Length == 12 && all.Distinct().Count() == 12, $"campaign.top_stats names twelve figures ({all.Length})");
        // A desktop: every figure on the plate, the five that matter most first.
        Narrow = false;
        var (desk, nothing) = Arrange(all, Array.Empty<string>(), Array.Empty<string>());
        Check(desk.Count == 12 && nothing.Count == 0 && desk.Take(5).SequenceEqual(new[] { "TREASURY", "GDP", "STABILITY", "MANPOWER", "POLITICAL CAPITAL" }),
            "on a desktop all twelve figures stand on the plate, treasury, GDP, stability, manpower and political capital first: " + string.Join(", ", desk));
        Check(Slots == 12, "the desktop plate has twelve places (hud.resource_slots)");
        // A phone: five on the plate, the rest behind "…".
        Narrow = true;
        var (plate, drawer) = Arrange(all, Array.Empty<string>(), Array.Empty<string>());
        Check(plate.SequenceEqual(new[] { "TREASURY", "GDP", "STABILITY", "MANPOWER", "POLITICAL CAPITAL" }),
            "a narrow plate carries treasury, GDP, stability, manpower and political capital, in that order: " + string.Join(", ", plate));
        Check(drawer.SequenceEqual(new[] { "DEBT", "BALANCE", "INFLATION", "POPULATION", "ARMY XP", "AIR XP", "NAVY XP" }),
            "the other seven wait behind \"…\" in top_stats order: " + string.Join(", ", drawer));
        Check(plate.Concat(drawer).OrderBy(s => s).SequenceEqual(all.OrderBy(s => s)), "no figure is lost: plate and drawer are all twelve");
        Check(Slots == 5, "a narrow plate has five places of its own (hud.resource_slots_narrow)");

        var promoted = Arrange(all, Array.Empty<string>(), new[] { "INFLATION" });
        Check(promoted.Plate.Last() == "INFLATION" && !promoted.Drawer.Contains("INFLATION") && promoted.Plate.Count == 6,
            "a figure in trouble behind \"…\" comes up onto the plate by itself");
        Check(InterfaceSkin.Metric("hud.promote_bad_stats", 0f) == 1f, "and that is switched on in the shipped data");

        var errors = new List<string>();
        InterfaceSkin.Merge("{\"metrics\":{\"hud.top_stats.primary.DEBT\":0.5,\"hud.top_stats.primary.POLITICAL CAPITAL\":0}}", "mod", errors);
        var modded = Arrange(all, Array.Empty<string>(), Array.Empty<string>());
        Check(errors.Count == 0 && modded.Plate.First() == "DEBT" && !modded.Plate.Contains("POLITICAL CAPITAL") &&
              modded.Drawer.Contains("POLITICAL CAPITAL"), "a mod reorders the plate with one metric and takes a figure off it with another");
        InterfaceSkin.Merge("{\"metrics\":{\"hud.resource_slots_narrow\":3}}", "mod", errors);
        Check(Primary(all).Count == 3, "hud.resource_slots_narrow caps a narrow plate's own places");
        Narrow = false;
        InterfaceSkin.ResetCatalog();
        Check(InterfaceSkin.Metric("hud.spark_height", -1f) == 6f && InterfaceSkin.Metric("hud.portrait_slot", -1f) == 0f &&
              InterfaceSkin.Metric("hud.flag_mount_size", -1f) > 0f,
            "the thread is 6 px, the portrait slot ships hidden and the mount has a flag size");
    }

    private static void States()
    {
        // Low is bad: stability, both thresholds landing exactly on their boundary.
        Check(Token(StateOf("STABILITY", 0.35, 1, 1)) == "state.negative", "stability at 35 % is bad (state.negative)");
        Check(Token(StateOf("STABILITY", 0.3501, 1, 1)) == "state.warning", "just above 35 % it is a warning");
        Check(Token(StateOf("STABILITY", 0.5, 1, 1)) == "state.warning", "at 50 % it is still a warning");
        Check(Token(StateOf("STABILITY", 0.5001, 1, 1)) == "state.positive", "just above 50 % it is fine");
        // High is bad: inflation.
        var warn = InterfaceSkin.Metric(StatePrefix + "INFLATION.warn", float.NaN);
        var bad = InterfaceSkin.Metric(StatePrefix + "INFLATION.bad", float.NaN);
        Check(bad > warn, "inflation is a figure where high is bad");
        Check(StateOf("INFLATION", warn - 0.0001, 1, 1) == State.Ok && StateOf("INFLATION", warn, 1, 1) == State.Warning &&
              StateOf("INFLATION", bad - 0.0001, 1, 1) == State.Warning && StateOf("INFLATION", bad, 1, 1) == State.Bad,
            "inflation turns amber at its warning and red at its bad threshold");
        // Measured against a basis.
        Check(StateOf("TREASURY", 0, 1000, 1) == State.Bad && StateOf("TREASURY", -5, 1000, 1) == State.Bad,
            "an empty or overdrawn treasury is bad");
        Check(StateOf("TREASURY", 120, 1000, 1) == State.Ok && StateOf("TREASURY", 10, 1000, 1) == State.Warning,
            "the treasury is judged as a share of GDP: 12 % is fine, 1 % thin");
        Check(StateOf("TREASURY", 10, 0, 1) == State.None, "with no GDP to measure against there is no state, not a false alarm");
        Check(StateOf("MANPOWER", 2_600_000, 1, 51_700_000) == State.Ok && StateOf("MANPOWER", 100_000, 1, 51_700_000) == State.Bad,
            "manpower is judged as a share of the people");
        Check(StateOf("GDP", 1, 1, 1) == State.None && StateOf("POLITICAL CAPITAL", 0, 1, 1) == State.None &&
              StateOf("POPULATION", 1, 1, 1) == State.None, "a size has no state: GDP, population and political capital are never lit");
        Check(StateOf("STABILITY", double.NaN, 1, 1) == State.None, "a figure that is not a number has no state");
        var errors = new List<string>();
        InterfaceSkin.Merge("{\"metrics\":{\"hud.stat_state.STABILITY.warn\":0.8}}", "mod", errors);
        Check(StateOf("STABILITY", 0.72, 1, 1) == State.Warning, "a mod moves one threshold and keeps the other");
        InterfaceSkin.ResetCatalog();
        foreach (var token in new[] { "state.negative", "state.warning", "state.positive", "text.muted", "text.bright" })
            Check(InterfaceSkin.Has(token), $"the colour {token} is in the skin");
    }

    private static void Layout()
    {
        Check(InterfaceSkin.HasLayout("hud.top") && InterfaceSkin.HasLayout("hud.flag_mount"), "hud.top and hud.flag_mount resolve");
        var top = InterfaceSkin.Layout("hud.top");
        var mount = InterfaceSkin.Layout("hud.flag_mount");
        Check(mount.AnchorLeft == 0 && mount.AnchorTop == 0 && mount.AnchorRight == 0 && mount.AnchorBottom == 0,
            "the mount is pinned to the top left corner");
        Check(mount.OffsetRight <= top.OffsetLeft - 4 && top.AnchorLeft == 0, "the plate starts right of the mount, with a gap");
        Check(mount.OffsetBottom <= 88, "the mount ends above the docks at y = 90");
        var role = InterfaceSkin.DeclaredLayoutMaterial("hud.flag_mount");
        Check(role == "flag.plaque" && InterfaceMaterials.Resolve(role).Shape == "rect",
            "the mount is drawn as a plain plaque, not a heraldic shield");
        Check(InterfaceSkin.LayoutMaterial("hud.flag_mount") is null, "and, like every material, only while ui.materials is on");
        var size = InterfaceSkin.Metric("hud.flag_mount_size", 40f);
        // The flag's frame (2), the mount's margins (12) and its border (2).
        Check(size * InterfaceSkin.Metric("hud.flag_mount_widest", 1.6f) + 16 <= mount.OffsetRight - mount.OffsetLeft + 0.5f,
            "the widest flag the mount allows fits inside it with its margins");
    }

    /// <summary>
    /// The observer's and the movement's top bars borrow places of the country's bar to say other
    /// things. Whatever they borrow must stand on the plate, or "The world: 8.1 billion people"
    /// would be hidden behind "…".
    /// </summary>
    private static void BorrowedFiguresStayOnThePlate()
    {
        var top = Source("Scripts/ui/TopBarHudUI.cs");
        var fields = Regex.Matches(top, "case \"([A-Z ]+)\":\\s*(_\\w+) = Stat\\(")
            .ToDictionary(match => match.Groups[2].Value, match => match.Groups[1].Value);
        Check(fields.Count == 12, $"every figure of the bar is built from top_stats ({fields.Count})");
        foreach (var (mode, file) in new[] { ("observer", "Scripts/ui/ObserverTopBarUI.cs"), ("movement", "Scripts/ui/MovementUI.cs") })
        {
            var borrowed = Regex.Matches(Source(file), "TopBarAs\\((_\\w+),")
                .Select(match => match.Groups[1].Value).Distinct().ToList();
            Check(borrowed.Count >= 5 && borrowed.All(fields.ContainsKey), $"the {mode} bar borrows {borrowed.Count} known figures");
            var stats = borrowed.Select(field => fields[field]).ToList();
            // On a phone, where the plate keeps five of its own, the borrowed figures still stand on it
            // and the plate stays small; on a desktop every figure is on it anyway and the mode hides
            // the ones it has no meaning for (ObserverTopBarUI, MovementUI).
            Narrow = true;
            var (plate, _) = Arrange(CampaignUiCatalog.TopStats, stats, Array.Empty<string>());
            Narrow = false;
            Check(stats.All(plate.Contains), $"every figure the {mode} bar borrows is on the plate: " + string.Join(", ", stats));
            Check(plate.Count <= 8, $"and the {mode} plate on a narrow screen stays at eight cells or fewer ({plate.Count})");
        }
        // The movement's own money is the treasury, which it shows without borrowing it.
        Check(Primary(CampaignUiCatalog.TopStats).Contains("TREASURY"), "the treasury, which a movement keeps for its own money, is on the plate");
    }

    /// <summary>The game's rule and the one above read the same data the same way.</summary>
    private static void RulesInStep()
    {
        var rules = Source("Scripts/ui/HudResourcePlate.cs");
        Check(rules.Contains("\"" + PrimaryPrefix + "\"") && rules.Contains("\"" + StatePrefix + "\"") &&
              rules.Contains("\".per_gdp\"") && rules.Contains("\".per_population\"") && rules.Contains("\"hud.resource_slots\""),
            "the game's rules read the same metrics");
        Check(rules.Contains("at <= bad ? State.Bad : at <= warn ? State.Warning : State.Ok") &&
              rules.Contains("at >= bad ? State.Bad : at >= warn ? State.Warning : State.Ok") &&
              rules.Contains("var at = (float)value;"),
            "and compare at the boundaries the same way");
        // Every check above runs on the copy at the top of this file; it proves the game's rule only
        // while the copy is the game's rule. A regression in HudResourcePlate (Arrange dropping the
        // borrowed figures, Primary ignoring hud.resource_slots, StateOf losing per_gdp) fails here.
        var self = Source("Tools/FormableTests/HudResourceTests.cs");
        foreach (var member in new[] { "int Slots =>", "List<string> Primary(IReadOnlyList<string> topStats)",
                     "(List<string> Plate, List<string> Drawer) Arrange(", "State StateOf(string stat,",
                     "string Token(State state)", "float Finite(float value, float fallback)" })
        {
            var game = MemberText(rules, member);
            Check(game.Length > member.Length + 8 && game == MemberText(self, member),
                $"the test's copy of {member.TrimEnd('(', ' ')} is the game's, word for word");
        }
        var plate = Source("Scripts/ui/ResourcePlateUI.cs");
        Check(plate.Contains("_topBarAsCountry.Keys") && plate.Contains("HudResourcePlate.Arrange("),
            "the plate is arranged with the figures the modes borrowed");
        var refresh = Source("Scripts/ui/TopBarHudUI.cs");
        var write = refresh.IndexOf("WriteMovementTopBar(player);", StringComparison.Ordinal);
        var arrange = refresh.IndexOf("ArrangeResourcePlate();", StringComparison.Ordinal);
        Check(write > 0 && arrange > write, "and arranged after the modes have borrowed their places");
        var restore = Source("Scripts/ui/MovementUI.cs");
        Check(restore.Contains("_topBarAsCountry.Clear();"), "leaving a mode gives the places back, so the country plate returns");

        // A hotseat pass hands the bar to another country on the same day: the week's change must
        // start afresh, not read country B today against country A last week.
        var reset = refresh.IndexOf("if (!ReferenceEquals(_trendsNation, player))", StringComparison.Ordinal);
        var sample = refresh.IndexOf("_stabilityTrend.Sample(", StringComparison.Ordinal);
        Check(reset > 0 && sample > reset && refresh.IndexOf("ResetTrends();", reset, StringComparison.Ordinal) is var call &&
              call > reset && call < sample, "the trends start afresh when another country takes the bar, before they are sampled");

        // The debt and the balance sit behind "…": a keyboard player must still reach them.
        var drawer = MemberText(plate, "void ToggleResourceDrawer()");
        Check(plate.Contains("_hudDrawerByKey = input is InputEventKey") && drawer.Contains("GrabFocus()") &&
              plate.Contains("key.Keycode is Key.Escape") && plate.Contains("GuiFocusChanged += FocusMoved"),
            "\"…\" from the keyboard takes focus into the drawer; Escape and Tab out of it close it");
        // The drawer has no layout key, so a skin reload reaches it only when it is next opened.
        Check(drawer.Contains("DressResourceDrawer();") && MemberText(plate, "void DressResourceDrawer()").Contains("MaterialStyles.Undress("),
            "the drawer is re-dressed (or given its frame back) every time it opens");
        // Narrow windows keep the week and the thread instead of shedding them for one brief row.
        Check(Source("Scripts/ui/HudReadabilityUI.cs").Contains("if (plan.Brief && !_hudAskBrief)"),
            "the plate keeps its long form on a second row whenever the long row does not fit, unless the player asked for brief");
    }

    /// <summary>
    /// The thresholds are for this game's numbers: a settled, solvent country in January 2024 must
    /// open its campaign with nothing lit. Day 0 of the shipped world, read without a tick.
    /// </summary>
    private static void HealthyStart()
    {
        var campaign = Campaign.CreateBootstrap(73);
        var before = CampaignFingerprint.Of(campaign);
        var dump = Environment.GetEnvironmentVariable("DOF_HUD_DUMP") == "1";
        var lit = new Dictionary<string, List<string>>();
        foreach (var nation in campaign.Nations.Where(n => n.Population > 0 && !n.Landless && campaign.HasOwnedLand(n)))
        {
            foreach (var (stat, value) in Readings(nation))
            {
                var state = StateOf(stat, value, nation.Gdp, nation.Population);
                if (state is State.Warning or State.Bad)
                {
                    if (!lit.TryGetValue(stat, out var list)) lit[stat] = list = new();
                    list.Add($"{nation.Tag}:{state}");
                }
            }
            if (dump)
                Console.WriteLine($"HUD_DUMP {nation.Tag} gdp={nation.Gdp:0.###} treasury/gdp={nation.Treasury / Math.Max(1e-6, nation.Gdp):0.###} " +
                    $"debt/gdp={SovereignFinance.Outstanding(nation) / Math.Max(1e-6, nation.Gdp):0.###} stability={nation.Stability:0.###} " +
                    $"inflation={nation.Inflation:0.###} manpower/pop={nation.ManpowerReserve / Math.Max(1d, nation.Population):0.####} pp={nation.PoliticalPoints:0.##} " +
                    $"balance/gdp={(nation.Budget.HasEntries ? nation.WeeklyBalance * 52d / Math.Max(1e-6, nation.Gdp) : 0d):0.###}");
        }
        foreach (var (stat, list) in lit)
            Console.WriteLine($"hud resources: at the 2024 start {stat} is lit for {list.Count} countries: {string.Join(" ", list.Take(20))}");
        foreach (var tag in new[] { "KOR", "USA", "DEU", "JPN", "FRA", "GBR", "CAN", "AUS" })
        {
            var nation = campaign.Nations.FirstOrDefault(n => n.Tag == tag);
            if (nation is null) continue;
            var troubled = Readings(nation).Where(r => StateOf(r.Stat, r.Value, nation.Gdp, nation.Population) is State.Warning or State.Bad)
                .Select(r => r.Stat).ToList();
            Check(troubled.Count == 0, $"{tag} starts 2024 with no figure in trouble" + (troubled.Count == 0 ? "" : ": " + string.Join(", ", troubled)));
        }
        Check(before == CampaignFingerprint.Of(campaign), "reading the plate's states never changes the world fingerprint");
    }

    private static IEnumerable<(string Stat, double Value)> Readings(Nation nation)
    {
        yield return ("TREASURY", nation.Treasury);
        yield return ("DEBT", SovereignFinance.Outstanding(nation));
        if (nation.Budget.HasEntries) yield return ("BALANCE", nation.WeeklyBalance * 52d);
        yield return ("INFLATION", nation.Inflation);
        yield return ("STABILITY", nation.Stability);
        yield return ("MANPOWER", nation.ManpowerReserve);
    }
}
