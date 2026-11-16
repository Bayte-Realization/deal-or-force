using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Step 4 of docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md: the alert list under the date plate.
///
/// The list's rules live in Scripts/ui/AlertListRules.cs, which this project does not compile (it is
/// game code beside Godot controls). As in DatePlateTests, they are copied below and a source check
/// holds the copy to the game's, word for word, and requires the game to call every one of them.
///
/// What it pins: must before should before can do, and inside one of them what is happening before
/// what waits, the sooner deadline first; the world's lines lead their rank and reach an observer; a hidden (snoozed) alert takes no row; "+N more"
/// counts what the rows leave out; "Next" is the first row and never repeated below it; DEFCON has a
/// row only while its mechanic runs, and world tension only from "tense"; hud.alerts resolves, sits
/// under the date plate as wide as it, is placed through Ui.Place (the skin_layout meta the release
/// probe reads) and holds a full list; the report and the desk dock under it; the strip of pictures,
/// the blue "Next" bar, the DEFCON badge and the report's inbox button are gone, and one way to the
/// inbox is left. None of it touches the world.
/// </summary>
static class AlertListTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("alert list: " + label);
        Console.WriteLine("PASS alert list: " + label);
    }

    private static string Repo => Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Repo, relative));

    public static void Run()
    {
        InterfaceSkin.ResetCatalog();
        try
        {
            RulesInStep();
            Ordering();
            UrgencyInsideARank();
            SnoozedTakeNoRow();
            MoreCountsTheRest();
            WorldRows();
            Layout();
            NextIsFirst();
            WhatWent();
            Words();
        }
        finally { InterfaceSkin.ResetCatalog(); }
    }

    // ---- the rules, as Scripts/ui/AlertListRules.cs writes them -----------------------------
    // Copied verbatim; RulesInStep compares each member with the game's.

    internal const int Calm = 4;

    internal static int Rank(CampaignAlertGroup group) => group switch
    {
        CampaignAlertGroup.Must => 0, CampaignAlertGroup.Should => 1, CampaignAlertGroup.Important => 2, _ => 3
    };

    internal static (int Tier, int Deadline) Urgency(CampaignAlert alert) =>
        (alert.Situation?.Priority == SituationPriority.Awaiting || alert.Route is CampaignAlertRoute.Event or CampaignAlertRoute.Elections ? 1 : 0,
            alert.Situation is not null && alert.Situation.DeadlineDay >= 0 ? alert.Situation.DeadlineDay : int.MaxValue);

    internal static string Dot(CampaignAlertGroup group) => group switch
    {
        CampaignAlertGroup.Must => "state.negative", CampaignAlertGroup.Should => "state.warning",
        CampaignAlertGroup.Could => "state.positive", _ => "state.info"
    };

    internal static int DefconRank(int level) => level switch { <= 2 => 0, 3 => 1, 4 => 2, _ => Calm };

    internal static int TensionRank(float tension) => tension >= 80f ? 0 : tension >= 60f ? 1 : tension >= 40f ? 2 : -1;

    internal static string TensionDot(float tension) => tension >= 80f ? "state.critical" : tension >= 60f ? "state.warning" : "state.info";

    internal static int MaxRows(float metric) => float.IsFinite(metric) ? Math.Clamp((int)MathF.Round(metric), 1, 12) : 6;

    internal static (T Next, T[] Rows, int More) Arrange<T>(IEnumerable<T> lines, Func<T, int> rank, Func<T, bool> canLead, int maxRows)
        where T : class
    {
        var ordered = lines.Select((line, index) => (line, index)).OrderBy(pair => rank(pair.line)).ThenBy(pair => pair.index)
            .Select(pair => pair.line).ToList();
        var next = ordered.FirstOrDefault(canLead);
        if (next is not null) ordered.Remove(next);
        var shown = Math.Min(ordered.Count, Math.Max(0, maxRows));
        return (next, ordered.Take(shown).ToArray(), ordered.Count - shown);
    }

    // ---- the checks -------------------------------------------------------------------------

    private static void RulesInStep()
    {
        var rules = Source("Scripts/ui/AlertListRules.cs");
        var self = Source("Tools/FormableTests/AlertListTests.cs");
        foreach (var member in new[] { "const int Calm", "int Rank(CampaignAlertGroup group)", "(int Tier, int Deadline) Urgency(CampaignAlert alert)",
                     "string Dot(CampaignAlertGroup group)",
                     "int DefconRank(int level)", "int TensionRank(float tension)", "string TensionDot(float tension)",
                     "int MaxRows(float metric)", "(T Next, T[] Rows, int More) Arrange<T>(" })
        {
            var game = HudResourceTests.MemberText(rules, member);
            Check(game.Length > member.Length && game == HudResourceTests.MemberText(self, member),
                $"the test's copy of {member} is the game's, word for word");
        }
        // A rule the game does not call is a test of the test's own copy.
        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        var members = Regex.Matches(rules, @"internal static [\w\(\), \[\]]+? (\w+)(<T>)?\(").Select(match => match.Groups[1].Value).ToList();
        Check(members.Count == 8, $"the rules have the eight members copied above ({string.Join(", ", members)})");
        foreach (var name in members)
            Check(ui.Contains("AlertListRules." + name + "(", StringComparison.Ordinal), $"the game calls AlertListRules.{name}");
        Check(ui.Contains("AlertListRules.Calm"), "and tells a calm world line by AlertListRules.Calm");
    }

    /// <summary>The player's hints, read the way the game reads them, for a country with one of each kind.</summary>
    private static (Campaign World, Nation Country, IReadOnlyList<CampaignAlert> Alerts) Hints()
    {
        var c = Campaign.CreateBootstrap(20261115);
        var n = c.NationByTag("POL");
        c.ConfigureSinglePlayer(n.Id); c.PendingEvents.Clear(); c.Wars.Clear(); c.Battles.Clear();
        SituationItem Item(string id, SituationPriority priority) => new(id, priority, SituationArea.Economy, id, "x",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SituationFact>(), SituationRoute.Finance, "x");
        // Given out of order on purpose: the list must sort them, not inherit the reader's order.
        var items = new[]
        {
            Item("z_can", SituationPriority.Opportunity), Item("y_should", SituationPriority.Warning),
            Item("x_must", SituationPriority.Critical), Item("w_should", SituationPriority.Warning),
            Item("v_news", SituationPriority.Awaiting), Item("u_can", SituationPriority.Opportunity)
        };
        return (c, n, CampaignAlerts.Read(c, n, items));
    }

    private static void Ordering()
    {
        var (c, _, alerts) = Hints();
        var before = CampaignFingerprint.Of(c);
        // The game feeds Arrange in the reader's order; shuffle it to be sure the order is Arrange's own.
        var shuffled = alerts.Reverse().ToArray();
        var (next, rows, more) = Arrange(shuffled, a => Rank(a.Group), _ => true, 64);
        var order = new[] { next }.Concat(rows).Select(a => a.Group).ToList();
        Check(next.Group == CampaignAlertGroup.Must, $"the most pressing hint leads: {next.Id}");
        var firstShould = order.IndexOf(CampaignAlertGroup.Should);
        var lastMust = order.LastIndexOf(CampaignAlertGroup.Must);
        var firstCould = order.IndexOf(CampaignAlertGroup.Could);
        var lastShould = order.LastIndexOf(CampaignAlertGroup.Should);
        Check(firstShould > lastMust && firstCould > lastShould && lastMust >= 0 && firstShould >= 0 && firstCould >= 0,
            "must before should before can do: " + string.Join(", ", order));
        var important = order.IndexOf(CampaignAlertGroup.Important);
        Check(important < 0 || important > lastShould && important < firstCould, "news (a war, an election coming) sits between should and can do");
        // Lines of one rank keep the order they came in, so the list does not shuffle between refreshes.
        var shoulds = rows.Where(a => a.Group == CampaignAlertGroup.Should).Select(a => a.Id).ToList();
        var given = shuffled.Where(a => a.Group == CampaignAlertGroup.Should).Select(a => a.Id).ToList();
        Check(shoulds.SequenceEqual(given), "alerts of one kind keep their order: " + string.Join(", ", shoulds));
        Check(more == 0 && rows.Length == alerts.Count - 1, "with room for all, every hint has a row");
        Check(Dot(CampaignAlertGroup.Must) == "state.negative" && Dot(CampaignAlertGroup.Should) == "state.warning" &&
              Dot(CampaignAlertGroup.Could) == "state.positive" && Dot(CampaignAlertGroup.Important) == "state.info",
            "the dot: red must, amber should, green can do, blue news");
        foreach (var token in new[] { "state.negative", "state.warning", "state.positive", "state.info", "state.critical", "button.primary_border" })
            Check(InterfaceSkin.Tokens.Any(entry => entry.Token == token), $"the skin defines {token}");
        Check(before == CampaignFingerprint.Of(c), "arranging the list never changes the world fingerprint");
    }

    /// <summary>
    /// Inside one rank the reader's order is the id's. The list keeps six rows, so that order decided
    /// what was seen: trade offers ("deal:") took the rows and a nuclear strike, a critical item and the
    /// peace terms went behind "+N more". The game feeds the hints in urgency order.
    /// </summary>
    private static void UrgencyInsideARank()
    {
        var c = Campaign.CreateBootstrap(20261115);
        var n = c.NationByTag("POL");
        c.ConfigureSinglePlayer(n.Id); c.PendingEvents.Clear(); c.Wars.Clear(); c.Battles.Clear();
        var today = c.ElapsedDays;
        SituationItem Item(string id, SituationPriority priority, int deadline) => new(id, priority, SituationArea.Foreign, id, "x",
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SituationFact>(), SituationRoute.Inbox, "x", DeadlineDay: deadline);
        var items = new[]
        {
            Item("deal:1", SituationPriority.Awaiting, today + 20), Item("deal:2", SituationPriority.Awaiting, today + 10),
            Item("deal:3", SituationPriority.Awaiting, today + 30), Item("deal:4", SituationPriority.Awaiting, today + 40),
            Item("deal:5", SituationPriority.Awaiting, today + 50), Item("deal:6", SituationPriority.Awaiting, today + 60),
            Item("peace:1", SituationPriority.Awaiting, today + 5), Item("x_cut_off", SituationPriority.Critical, -1)
        };
        // A strike on our forces is not a report item; it is built the way CampaignAlerts.Read builds it,
        // and the whole set is put back in the reader's order (group, then id).
        var alerts = CampaignAlerts.Read(c, n, items)
            .Append(new CampaignAlert("nuclear_struck", CampaignAlertGroup.Must, "x", "x", CampaignAlertRoute.Defcon))
            .OrderBy(a => a.Group).ThenBy(a => a.Id, StringComparer.Ordinal).ToArray();
        var ids = alerts.Select(a => a.Id).ToList();
        Check(ids.IndexOf("deal:1") < ids.IndexOf("nuclear_struck") && ids.IndexOf("deal:6") < ids.IndexOf("peace:1"),
            "the reader puts the offers first among the musts, by id (the order the list must not keep)");

        // The game's order: hints sorted by urgency, then Arrange by rank.
        var (next, rows, more) = Arrange(alerts.OrderBy(a => Urgency(a)), a => Rank(a.Group), _ => true, 6);
        var shown = new[] { next }.Concat(rows).Select(a => a.Id).ToList();
        Check(next.Id is "nuclear_struck" or "x_cut_off", $"what is happening now leads, not an offer: {next.Id}");
        Check(shown.Contains("nuclear_struck") && shown.Contains("x_cut_off") && shown.Contains("peace:1"),
            $"the strike, the critical item and the peace terms are on screen, not behind \"+{more} more\": {string.Join(", ", shown)}");
        Check(shown.IndexOf("peace:1") < shown.IndexOf("deal:2") && shown.IndexOf("deal:2") < shown.IndexOf("deal:1"),
            "what waits for an answer goes by its deadline, the soonest first");
        var firstWaiting = shown.FindIndex(id => id.StartsWith("deal:") || id.StartsWith("peace:"));
        Check(shown.IndexOf("x_cut_off") < firstWaiting && shown.IndexOf("nuclear_struck") < firstWaiting,
            "and after what is happening to us now");
        Check(Urgency(new CampaignAlert("event:1", CampaignAlertGroup.Must, "x", "x", CampaignAlertRoute.Event)).Tier == 1 &&
              Urgency(new CampaignAlert("unassigned_divisions", CampaignAlertGroup.Must, "x", "x", CampaignAlertRoute.Army)).Tier == 0,
            "an event waits for an answer; divisions without orders are happening now");

        var lines = HudResourceTests.MemberText(Source("Scripts/ui/CampaignAlertsUI.cs"), "List<AlertLine> AlertLines()");
        Check(lines.Contains(".OrderBy(a => AlertListRules.Urgency(a))"), "the game feeds the list its hints in urgency order");
    }

    private static void SnoozedTakeNoRow()
    {
        var (c, _, alerts) = Hints();
        var snoozes = new CampaignAlertSnoozes();
        var hidden = alerts.First(a => a.Group == CampaignAlertGroup.Should);
        snoozes.Hide(hidden, c.ElapsedDays, c.Defines.CampaignAlertSnoozeDays);
        // The game's filter, written the way AlertLines writes it.
        var visible = alerts.Where(a => !snoozes.Hidden(a, c.ElapsedDays)).ToArray();
        var (next, rows, _) = Arrange(visible, a => Rank(a.Group), _ => true, 64);
        Check(next.Id != hidden.Id && rows.All(a => a.Id != hidden.Id), $"a hidden hint ({hidden.Id}) takes no row");
        Check(rows.Length == alerts.Count - 2, "and nothing else is lost with it");
        var lines = HudResourceTests.MemberText(Source("Scripts/ui/CampaignAlertsUI.cs"), "List<AlertLine> AlertLines()");
        Check(lines.Contains("_campaignAlerts.Where(a => !_campaignAlertSnoozes.Hidden(a, _campaign.ElapsedDays))"),
            "the list reads only hints the snoozes do not hide");
        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        Check(ui.Contains("HideCampaignAlert(id)") && ui.Contains("ButtonIndex: MouseButton.Right") &&
              ui.Contains("_campaignAlertSnoozes.Hide(current, _campaign.ElapsedDays, _campaign.Defines.CampaignAlertSnoozeDays)"),
            "a right click on a row hides it for the campaign's snooze days, as the strip's icons did");
        Check(ui.Contains("if (!id.StartsWith(\"world:\", StringComparison.Ordinal)) HideCampaignAlert(id);"),
            "a world line (DEFCON, tension) cannot be hidden: it is not a task");
        // Escalation still brings a hidden hint back, whatever list shows it.
        Check(!snoozes.Hidden(hidden with { Group = CampaignAlertGroup.Must }, c.ElapsedDays), "a hidden hint that turns urgent comes back");
    }

    private static void MoreCountsTheRest()
    {
        var max = MaxRows(InterfaceSkin.Metric("hud.alerts.max_rows", -1f));
        Check(max == 6, $"hud.alerts.max_rows is data and is 6 ({max})");
        Check(MaxRows(0f) == 1 && MaxRows(40f) == 12 && MaxRows(float.NaN) == 6, "a modded row count is kept between 1 and 12");
        var lines = Enumerable.Range(0, 11).Select(i => "line" + i).ToArray();
        var (next, rows, more) = Arrange(lines, _ => 0, _ => true, max);
        Check(next == "line0" && rows.Length == 6 && more == 4, $"eleven lines: Next, six rows and \"+4 more\" (got {rows.Length} rows, +{more})");
        (next, rows, more) = Arrange(lines.Take(7), _ => 0, _ => true, max);
        Check(rows.Length == 6 && more == 0, "seven lines: Next and six rows, nothing behind \"more\"");
        (next, rows, more) = Arrange(lines.Take(8), _ => 0, _ => true, max);
        Check(rows.Length == 6 && more == 1, "eight: one more");
        (next, rows, more) = Arrange(Array.Empty<string>(), _ => 0, _ => true, max);
        Check(next is null && rows.Length == 0 && more == 0, "nothing: no Next, no rows, no \"more\"");
        // A world line never leads, so with only world lines there is no "Next" and they are all rows.
        (next, rows, more) = Arrange(new[] { "world:defcon", "world:tension" }, _ => 0, line => !line.StartsWith("world:"), max);
        Check(next is null && rows.Length == 2, "a world line is never \"Next\"");
        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        Check(ui.Contains("AlertListRules.MaxRows(Ui.Metric(\"hud.alerts.max_rows\", 6f))") &&
              ui.Contains("Loc.Format(\"+{0} more\", more)") && ui.Contains("_alertMore.Pressed += ToggleCampaignAlertList;"),
            "the game cuts at hud.alerts.max_rows and \"+N more\" opens the full list in one click");
        Check(ui.Contains("_alertMore.Visible = more > 0 || hidden > 0;"), "and the same row brings hidden hints back when nothing is cut");
        var full = HudResourceTests.MemberText(ui, "void RefreshCampaignAlertRows()");
        Check(full.Contains("AlertListRules.Arrange(_alertLines, line => line.Rank, _ => false, int.MaxValue).Rows"),
            "the full list holds every line, the cut ones and \"Next\" included");
    }

    private static void WorldRows()
    {
        Check(DefconRank(1) == 0 && DefconRank(2) == 0 && DefconRank(3) == 1 && DefconRank(4) == 2 && DefconRank(5) == Calm,
            "DEFCON 1-2 rank with must, 3 with should, 4 as news, 5 calm and last");
        Check(TensionRank(39.9f) == -1 && TensionRank(40f) == 2 && TensionRank(60f) == 1 && TensionRank(85f) == 0,
            "world tension takes a row from 40 (\"tense\") and climbs at 60 and 80");
        Check(TensionDot(85f) == "state.critical" && TensionDot(65f) == "state.warning" && TensionDot(45f) == "state.info",
            "its dot follows the conflicts screen's colours");

        var lines = HudResourceTests.MemberText(Source("Scripts/ui/CampaignAlertsUI.cs"), "List<AlertLine> AlertLines()");
        var guard = lines.IndexOf("if (DefconSystem.Enabled(_campaign))", StringComparison.Ordinal);
        var defcon = lines.IndexOf("Id = \"world:defcon\"", StringComparison.Ordinal);
        var tensionGuard = lines.IndexOf("if (MechanicFeatures.SystemEnabled(_campaign, nameof(WorldWar)))", StringComparison.Ordinal);
        var tension = lines.IndexOf("Id = \"world:tension\"", StringComparison.Ordinal);
        Check(guard >= 0 && defcon > guard && tensionGuard > defcon && tension > tensionGuard && lines.Contains("if (rank >= 0)"),
            "the DEFCON row is built only inside DefconSystem.Enabled, the tension row only inside its system and from \"tense\"");
        Check(lines.Contains("DefconSystem.LevelColourToken(level)") && lines.Contains("Glyph = \"alert_battle\""),
            "the DEFCON row's dot is the ladder's own colour");

        // The world's lines come before the hints: a rank keeps its input order, so a DEFCON 2 added
        // last stood under every must hint and was the first thing "+N more" cut.
        var hints = lines.IndexOf("foreach (var alert in _campaignAlerts", StringComparison.Ordinal);
        Check(hints > tension, "the world's lines are added before the player's hints");
        var crisis = new[] { "world:defcon" }.Concat(Enumerable.Range(0, 12).Select(i => "hint" + i)).ToArray();
        var (_, crisisRows, _) = Arrange(crisis, line => line.StartsWith("world:") ? DefconRank(2) : 0, line => !line.StartsWith("world:"), 6);
        Check(crisisRows.FirstOrDefault() == "world:defcon", "so DEFCON 2 heads the must rows with twelve must hints waiting, under \"Next\" only");
        var (_, calmRows, calmMore) = Arrange(new[] { "world:defcon", "hint0", "hint1" }, line => line.StartsWith("world:") ? DefconRank(5) : 0,
            line => !line.StartsWith("world:"), 6);
        Check(calmRows.LastOrDefault() == "world:defcon" && calmMore == 0, "while DEFCON 5 still keeps to the foot of the list");

        // An observer runs no country, so has no hints, but watches the world: the DEFCON badge this
        // row replaced was on the observer's top bar too, and it was the only door to the ladder.
        Check(!lines.Contains("_observerMode"), "the world's lines are built for an observer too");
        var alertsUi = Source("Scripts/ui/CampaignAlertsUI.cs");
        var refresh = HudResourceTests.MemberText(alertsUi, "void RefreshCampaignAlerts(IReadOnlyList<SituationItem> report)");
        Check(refresh.Contains("_alertListPanel.Visible = nextShown || rows.Length > 0 || _alertMore.Visible;"),
            "and the list is shown to an observer whenever it has a line");
        Check(refresh.Contains("_campaignAlertsShown = !_observerMode"), "while an observer's hints stay off");
        // The tension figure is cached by day; the world itself is part of the key, so a quick-load on
        // the same day and map revision does not show the old world's figure.
        var cache = HudResourceTests.MemberText(alertsUi, "float WorldTensionToday()");
        Check(cache.Contains("_worldTensionToday.World != _campaign"), "the tension cache is keyed on the world as well as the day");

        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        Check(ui.Contains("case \"world:defcon\": OpenDefconOverview();") &&
              ui.Contains("case \"world:tension\": _conflictTab = 5; _conflictPair = -1; _conflictWar = -1; OpenConflictsScreen();"),
            "the DEFCON row opens the ladder's screen (as the badge did) and the tension row the conflicts screen's \"The world\", not the last rivalry opened there");
        Check(Source("Scripts/ui/ConflictsScreenUI.cs").Contains("else if (_conflictTab == 5) ListWorldOrder();"), "tab 5 is the world's page");
        Check(lines.Contains("line => !line.World") || ui.Contains("line => !line.World"), "world lines are never \"Next\"");
    }

    private static (float Left, float Top, float Right, float Bottom) Pixels(InterfaceSkin.PanelRect rect, float width, float height) =>
        (rect.AnchorLeft * width + rect.OffsetLeft, rect.AnchorTop * height + rect.OffsetTop,
            rect.AnchorRight * width + rect.OffsetRight, rect.AnchorBottom * height + rect.OffsetBottom);

    private static void Layout()
    {
        Check(InterfaceSkin.HasLayout("hud.alerts"), "hud.alerts resolves");
        var list = InterfaceSkin.Layout("hud.alerts");
        var plate = InterfaceSkin.Layout("hud.date_plate");
        Check(list.AnchorLeft == 1 && list.AnchorRight == 1 && list.AnchorTop == 0 && list.AnchorBottom == 0,
            "the list is pinned to the top right, under the date plate");
        Check(list.OffsetLeft == plate.OffsetLeft && list.OffsetRight == plate.OffsetRight, "as wide as the date plate, edge to edge");
        Check(list.OffsetTop >= plate.OffsetBottom + 4 && list.OffsetTop <= plate.OffsetBottom + 16, "starting just under it");
        Check(InterfaceSkin.DeclaredLayoutMaterial("hud.alerts") == "plate" && InterfaceSkin.LayoutMaterial("hud.alerts") is null,
            "it is a plate while ui.materials is on, and keeps the HUD frame while it is off");

        // A full list fits the height the data gives it: frame 2, margins 8, "Next" and the rows at
        // hud.alerts.row_height with 2 between them, and the 22-pixel "+N more".
        var row = InterfaceSkin.Metric("hud.alerts.row_height", -1f);
        Check(row is >= 22f and <= 44f, $"hud.alerts.row_height is data ({row})");
        var rows = MaxRows(InterfaceSkin.Metric("hud.alerts.max_rows", 6f));
        var full = 2 + 8 + row * (rows + 1) + 22 + 2 * (rows + 1);
        Check(full <= list.OffsetBottom - list.OffsetTop + .5f, $"a full list needs {full:0} px and hud.alerts is {list.OffsetBottom - list.OffsetTop:0} tall");

        foreach (var (w, h) in new[] { (1280f, 720f), (1920f, 1080f) })
        {
            var mine = Pixels(list, w, h);
            Check(mine.Left >= 0 && mine.Right <= w && mine.Bottom <= h, $"the list is on screen at {w:0}x{h:0}");
            foreach (var other in new[] { "hud.top", "hud.date_plate", "hud.flag_mount", "hud.navigation", "hud.outliner", "hud.map_controls" })
            {
                if (!InterfaceSkin.HasLayout(other)) continue;
                var theirs = Pixels(InterfaceSkin.Layout(other), w, h);
                var wide = Math.Min(mine.Right, theirs.Right) - Math.Max(mine.Left, theirs.Left);
                var tall = Math.Min(mine.Bottom, theirs.Bottom) - Math.Max(mine.Top, theirs.Top);
                Check(wide <= 0 || tall <= 0, $"a full list leaves {other} its room at {w:0}x{h:0}");
            }
        }
        var report = InterfaceSkin.Layout("hud.outliner");
        Check(report.OffsetTop >= list.OffsetBottom + 4, "the report's data stands under a full list");

        // Placed by name, so the panel carries the skin_layout meta the editor and the probes read.
        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        Check(ui.Contains("Ui.Place(_alertListPanel, \"hud.alerts\");"), "the list is placed through Ui.Place by hud.alerts");
        Check(Source("Scripts/ui/UiTheme.cs").Contains("control.SetMeta(\"skin_layout\", key);"), "and Ui.Place marks it with the skin_layout meta");
        Check(Source("Tools/ExportVerification/CampaignAlertsVerification.cs").Contains("list.GetMeta(\"skin_layout\").AsString()==\"hud.alerts\""),
            "the release probe reads that meta off the live panel");
        var layout = HudResourceTests.MemberText(ui, "void LayoutCampaignAlerts()");
        Check(layout.Contains("plateBottom + DatePlateGap") && layout.Contains("_hudHeaderExtra") && layout.Contains("GetCombinedMinimumSize().Y"),
            "at run time the list starts under the plate as it really is (and under a two-row top bar), as tall as its rows");
        Check(ui.Contains("ApplyHudFrame(_alertListPanel);"), "with materials off it wears the HUD frame of its neighbours");

        // The report and the desk dock under the list wherever it ends.
        Check(Source("Scripts/ui/CampaignShellUI.cs").Contains("DockUnderAlertList(_outliner);"), "the report docks under the list");
        var desk = HudResourceTests.MemberText(Source("Scripts/ui/SituationDeskUI.cs"), "void FitSituationDesk()");
        Check(desk.Contains("DockUnderAlertList(_deskPanel);"), "the desk docked on the right docks under it too (its data top is inside the list's column)");
        Check(Source("Tools/ExportVerification/HudSurfaceSafetyVerification.cs").Contains("the open desk docks under the alert list"),
            "and the release probe checks both on the live screen at 1920 and 1280");
        var preview = Source("Scripts/ui/LayoutPreview.cs");
        foreach (var context in new[] { "HudContext", "MapSelectionContext" })
        {
            var line = preview.Split('\n').FirstOrDefault(text => text.Contains("string[] " + context + " =")) ?? "";
            Check(line.Contains("\"hud.alerts\""), $"the layout editor draws the list ({context})");
        }
    }

    private static void NextIsFirst()
    {
        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        // MemberText folds runs of spaces into one, so the two spaces before "→" read as one here.
        var lead = HudResourceTests.MemberText(ui, "bool RefreshAlertNext(AlertLine next)");
        Check(lead.Contains("_alertListRows.MoveChild(_alertNext, 0);") && lead.Contains("Loc.Format(\"Next: {0} →\", next.Caption)"),
            "\"Next: …\" is the list's first row");
        Check(lead.Contains("StyleAlertRow(_alertNext, \"button.primary_border\", lead: true"), "and the one accented row, in the ink-blue accent");
        var rows = HudResourceTests.MemberText(ui, "void RefreshAlertListRows(AlertLine[] rows)");
        Check(rows.Contains("var index = 1;") && rows.Contains("_alertListRows.MoveChild(button, index++);"), "the other rows follow it, worst first");
        Check(ui.Contains("_alertListRows.MoveChild(_alertMore, _alertListRows.GetChildCount() - 1);"), "\"+N more\" closes the list");
        Check(lead.Contains("Tr(\"Nothing is waiting for you. Let time run [Space]\")"), "with nothing waiting on a paused game, it offers to let time run");
        Check(Regex.IsMatch(Source("game/interface/00_skin.json"), "\"button.primary_border\":\\s*\"#6da7e6\""), "the accent is the ink blue #6da7e6");
    }

    private static void WhatWent()
    {
        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        foreach (var gone in new[] { "\"HintCluster\"", "\"CampaignAlertGroup\" +", "\"CampaignBrief\"", "\"CampaignIcon_\"", "_campaignAlertStrip" })
            Check(!ui.Contains(gone, StringComparison.Ordinal), "the strip of pictures and the blue bar are gone: no " + gone);
        Check(!Source("Scripts/ui/TeletypeUI.cs").Contains("_campaignAlertStrip"), "the stop-press band no longer borrows the strip's slot");
        Check(!Source("Scripts/ui/TopBarHudUI.cs").Contains("OpenDefconOverview"), "DEFCON left the resource plate");
        Check(!Source("Scripts/ui/StrategicHudUI.cs").Contains("\"OpenDiplomaticInbox\"") && !Source("Scripts/ui/DealInboxUI.cs").Contains("_dealInboxHudButton"),
            "the report's own \"Diplomatic inbox (N)\" button is gone");
        var plate = Source("Scripts/ui/DatePlateUI.cs");
        Check(plate.Contains("ActionButton(\"Deals\", OpenDealInbox") && Source("Scripts/ui/DealInboxUI.cs").Contains("RefreshDatePlateDeals(count);"),
            "the date plate's \"Deals (N)\" chip is the one way to the inbox while offers wait");
        var probe = Source("Tools/ExportVerification/CampaignAlertsVerification.cs");
        Check(!probe.Contains("\"CampaignAlertGroupMust\"") && !probe.Contains("\"CampaignIcon_") && probe.Contains("\"CampaignAlertRow_*\""),
            "the release probe checks the list, not the strip it replaced");
    }

    private static void Words()
    {
        var ui = Source("Scripts/ui/CampaignAlertsUI.cs");
        var keys = Regex.Matches(ui, @"(?:Tr|Loc\.Format)\(""((?:[^""\\]|\\.)+)""").Select(match => Regex.Unescape(match.Groups[1].Value)).Distinct().ToList();
        Check(keys.Count >= 10, $"the list's captions are read from the source ({keys.Count})");
        var english = Directory.GetFiles(Path.Combine(Repo, "game", "localization", "english"), "*.json").Select(File.ReadAllText).ToArray();
        var russian = Directory.GetFiles(Path.Combine(Repo, "game", "localization", "russian"), "*.json").Select(File.ReadAllText).ToArray();
        foreach (var key in keys)
        {
            var quoted = "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\":";
            Check(english.Any(text => text.Contains(quoted)) && russian.Any(text => text.Contains(quoted)), $"\"{key}\" is in English and Russian");
        }
    }
}
