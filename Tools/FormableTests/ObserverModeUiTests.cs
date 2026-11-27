using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

public static class ObserverModeUiTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL observer UI: " + label);
        Console.WriteLine("PASS observer UI: " + label);
    }

    private static string Repo => Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
    private static string Source(string relative) => File.ReadAllText(Path.Combine(Repo, relative));

    [TestSuite("--observer-ui-only", Description = "Observer mode Landless Movements, world-observer HUD rules, panel null/read-only guards, and EN/RU localization.")]
    public static void Run()
    {
        RulesInStep();
        TestLandlessMovementsInObserver();
        TestWorldObserverSummaryAndOutliner();
        TestAuditedUiContracts();
        TestLocalizationParity();
    }

    // ---- Verbatim mirror of Scripts/ui/ObserverUiRules.cs ---------------------------------
    // FormableTests compiles simulation + test sources without Godot; RulesInStep() keeps
    // every member below byte-equivalent (modulo comments/whitespace) to Scripts/ui/ObserverUiRules.cs.

    internal readonly record struct ObserverWorldSummary(
        int CountryCount,
        double WorldGdp,
        long WorldPopulation,
        float AverageStability,
        int OpenWarCount,
        int FlashpointCount,
        int LandlessMovementCount,
        float TotalDivisions);

    internal static bool IsObserving(Campaign campaign, bool uiObserverFlag) =>
        campaign is not null && (uiObserverFlag || campaign.ObserverMode || campaign.Player is null);

    internal static bool CanIssuePlayerOrders(Campaign campaign, bool uiObserverFlag) =>
        campaign is not null && !IsObserving(campaign, uiObserverFlag) && campaign.Player is not null;

    internal static bool CanIssueMovementOrders(Campaign campaign, bool uiObserverFlag, Nation movement) =>
        CanIssuePlayerOrders(campaign, uiObserverFlag) &&
        movement is not null &&
        campaign!.Player is { } player &&
        player.Id == movement.Id;

    internal static bool CanInspectAsViewer(Campaign campaign, bool uiObserverFlag, int nationId)
    {
        if (campaign is null || nationId < 0) return false;
        return IsObserving(campaign, uiObserverFlag) || CampaignIntel.PlayerCanInspect(campaign, nationId);
    }

    internal static bool CanSeeArmyAsViewer(Campaign campaign, bool uiObserverFlag, Army army) =>
        campaign is not null && army is not null &&
        (IsObserving(campaign, uiObserverFlag) || CampaignIntel.PlayerCanSeeArmy(campaign, army));

    internal static bool CanSeeBattleAsViewer(Campaign campaign, bool uiObserverFlag, LandBattle battle) =>
        campaign is not null && battle is not null &&
        (IsObserving(campaign, uiObserverFlag) || CampaignIntel.PlayerCanSeeBattle(campaign, battle));

    internal static Nation DefaultObservedNation(Campaign campaign)
    {
        if (campaign is null) return null;
        Nation bestLanded = null;
        Nation bestPopulated = null;
        for (var i = 0; i < campaign.Nations.Count; i++)
        {
            var n = campaign.Nations[i];
            if (n is null || n.Landless) continue;
            if (n.Population > 0 && campaign.HasOwnedLand(n))
            {
                if (bestLanded is null || n.Gdp > bestLanded.Gdp ||
                    (Math.Abs(n.Gdp - bestLanded.Gdp) < 0.001f &&
                     (n.Population > bestLanded.Population || (n.Population == bestLanded.Population && n.Id < bestLanded.Id))))
                    bestLanded = n;
            }
            else if (bestPopulated is null || n.Gdp > bestPopulated.Gdp ||
                     (Math.Abs(n.Gdp - bestPopulated.Gdp) < 0.001f && n.Id < bestPopulated.Id))
                bestPopulated = n;
        }
        return bestLanded ?? bestPopulated ?? campaign.Nations.FirstOrDefault();
    }

    internal static Nation ResolveInspectedNation(Campaign campaign, Nation selectedNation = null, bool preferLanded = false)
    {
        if (campaign is null) return null;
        if (campaign.Player is { } player && (!preferLanded || !player.Landless))
            return player;
        if (selectedNation is not null && campaign.NationById(selectedNation.Id) == selectedNation &&
            (!preferLanded || !selectedNation.Landless))
            return selectedNation;
        return DefaultObservedNation(campaign);
    }

    internal static ScenarioState DefaultObservedState(Campaign campaign, Nation inspectedNation = null)
    {
        if (campaign is null) return null;
        var host = inspectedNation ?? ResolveInspectedNation(campaign, preferLanded: true);
        if (host is not null)
        {
            if (host.CapitalStateMapIndex >= 0)
            {
                var cap = campaign.StateByMapIndex(host.CapitalStateMapIndex);
                if (cap is not null && !LakeRegions.IsWaterState(cap)) return cap;
            }
            var own = campaign.StatesOf(host).FirstOrDefault(s => !LakeRegions.IsWaterState(s));
            if (own is not null) return own;
        }
        return campaign.States.FirstOrDefault(s => s.Population > 0 && !LakeRegions.IsWaterState(s))
               ?? campaign.States.FirstOrDefault();
    }

    internal static List<Nation> ObservedLandlessMovements(Campaign campaign)
    {
        if (campaign is null) return new List<Nation>();
        return Movements.Active(campaign)
            .Where(n => n?.Movement is { Dissolved: false, Landless: true })
            .OrderByDescending(n => n.Movement.Legitimacy)
            .ThenByDescending(n => n.Movement.Cadres)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id)
            .ToList();
    }

    internal static Nation PickMovementForPanel(Campaign campaign, Nation currentShown, bool observing)
    {
        if (campaign is null) return null;
        if (!observing && campaign.Player?.Movement is not null)
            return campaign.Player;

        var validCurrent = currentShown is not null &&
                           currentShown.Movement is not null &&
                           campaign.NationById(currentShown.Id) == currentShown;
        if (validCurrent && (!observing || !currentShown.Movement.Dissolved))
            return currentShown;

        var activeLandless = ObservedLandlessMovements(campaign);
        if (activeLandless.Count > 0) return activeLandless[0];

        if (observing) return null;

        return Movements.Active(campaign).OrderBy(m => m.Id).FirstOrDefault()
               ?? Movements.Of(campaign).OrderBy(m => m.Id).FirstOrDefault();
    }

    internal static ObserverWorldSummary ComputeWorldSummary(Campaign campaign)
    {
        if (campaign is null) return default;
        var countries = 0;
        double gdp = 0;
        long pop = 0;
        double stabSum = 0;
        for (var i = 0; i < campaign.Nations.Count; i++)
        {
            var n = campaign.Nations[i];
            if (n is null || n.Landless || n.Population <= 0 || !campaign.HasOwnedLand(n)) continue;
            countries++;
            gdp += n.Gdp;
            pop += n.Population;
            stabSum += n.Stability;
        }
        var avgStab = countries > 0 ? (float)(stabSum / countries) : 0f;
        var openWars = Warfare.OpenWars(campaign).Count;
        var flashpoints = Escalation.Hottest(campaign, 25f).Count();
        var movements = ObservedLandlessMovements(campaign).Count;
        float divs = 0f;
        for (var i = 0; i < campaign.Armies.Count; i++)
            divs += campaign.Armies[i].Divisions;
        return new ObserverWorldSummary(countries, gdp, pop, avgStab, openWars, flashpoints, movements, divs);
    }

    internal static ShellAlertSnapshot BuildWorldObserverSnapshot(Campaign campaign, bool outlinerOpen = true)
    {
        if (campaign is null)
            return new(Array.Empty<ShellAlertEntry>(), 0, Array.Empty<ShellOutlinerGroup>(), false);

        var alerts = new List<ShellAlertEntry>();
        var wars = Warfare.OpenWars(campaign)
            .OrderByDescending(w => w.AttackerLosses + w.DefenderLosses)
            .ThenBy(w => w.Id)
            .ToList();
        var maxAlerts = outlinerOpen ? 3 : 4;
        foreach (var war in wars.Take(Math.Max(1, maxAlerts - 1)))
        {
            alerts.Add(new ShellAlertEntry(
                "obs_war:" + war.Id,
                "obs_war:" + war.Id,
                WarNames.Of(campaign, war),
                Loc.Format("obs.ui.alert.war_score_fmt", $"{war.WarScore:+0;-0;0}"),
                war.Intensity >= 2 ? ShellAlertSeverity.Critical : ShellAlertSeverity.Warning,
                CampaignAlertRoute.War,
                war.Id,
                -1,
                "",
                false,
                false,
                false));
        }

        var movements = ObservedLandlessMovements(campaign);
        if (movements.Count > 0 && alerts.Count < maxAlerts)
        {
            var lead = movements[0];
            alerts.Add(new ShellAlertEntry(
                "obs_movements",
                "obs_movements",
                Loc.Format("obs.ui.alert.movements_fmt", movements.Count),
                Loc.Format("obs.ui.alert.movements_lead_fmt", lead.DisplayName, Fmt.Percent(lead.Movement.Legitimacy)),
                ShellAlertSeverity.Opportunity,
                CampaignAlertRoute.Situation,
                lead.Id,
                -1,
                "",
                false,
                false,
                false));
        }

        var groups = new List<ShellOutlinerGroup>();
        if (wars.Count > 0)
        {
            var rows = new List<ShellOutlinerRow>();
            foreach (var war in wars.Take(4))
            {
                rows.Add(new ShellOutlinerRow(
                    "obs_outliner:war:" + war.Id,
                    "obs_war:" + war.Id,
                    WarNames.Of(campaign, war),
                    $"{war.WarScore:+#;-#;0}%",
                    war.Intensity >= 2 ? ShellAlertSeverity.Critical : ShellAlertSeverity.Warning,
                    "Wars",
                    war.Id));
            }
            groups.Add(new ShellOutlinerGroup("obs_wars", Loc.Get("Wars"), rows));
        }

        if (movements.Count > 0)
        {
            var mRows = new List<ShellOutlinerRow>();
            foreach (var mov in movements.Take(4))
            {
                var host = Movements.HostOf(campaign, mov);
                var label = host is not null ? $"{mov.DisplayName} ({host.Tag})" : mov.DisplayName;
                mRows.Add(new ShellOutlinerRow(
                    "obs_outliner:mov:" + mov.Id,
                    "obs_mov:" + mov.Id,
                    label,
                    Fmt.Percent(mov.Movement.Legitimacy),
                    ShellAlertSeverity.Opportunity,
                    "Movements",
                    mov.Id));
            }
            groups.Add(new ShellOutlinerGroup("obs_movements", Loc.Get("obs.ui.movements.short_heading"), mRows));
        }

        var standoffs = Escalation.Hottest(campaign, 45f)
            .Where(r => !CampaignDiplomacy.AtWar(campaign, r.A, r.B))
            .Take(3)
            .ToList();
        if (standoffs.Count > 0)
        {
            var sRows = new List<ShellOutlinerRow>();
            foreach (var rel in standoffs)
            {
                var a = campaign.NationById(rel.A);
                var b = campaign.NationById(rel.B);
                if (a is null || b is null) continue;
                sRows.Add(new ShellOutlinerRow(
                    $"obs_outliner:standoff:{rel.A}_{rel.B}",
                    $"obs_standoff:{rel.A}_{rel.B}",
                    $"{a.Tag} — {b.Tag}",
                    $"{rel.Tension:0}",
                    rel.Tension >= 75f ? ShellAlertSeverity.Warning : ShellAlertSeverity.Important,
                    "Conflicts",
                    a.Id));
            }
            if (sRows.Count > 0)
                groups.Add(new ShellOutlinerGroup("obs_standoffs", Loc.Get("obs.ui.outliner.standoffs"), sRows));
        }

        return new ShellAlertSnapshot(alerts, Math.Max(0, wars.Count - (maxAlerts - 1)), groups, false);
    }

    private static string ExtractMember(string source, string signatureStart)
    {
        var idx = source.IndexOf(signatureStart, StringComparison.Ordinal);
        if (idx < 0) return "";
        var pos = idx + signatureStart.Length;
        while (pos < source.Length && source[pos] != '{' && !(pos + 1 < source.Length && source[pos] == '=' && source[pos + 1] == '>'))
            pos++;
        if (pos >= source.Length) return "";
        string raw;
        if (source[pos] == '{')
        {
            var depth = 0;
            var end = pos;
            for (; end < source.Length; end++)
            {
                if (source[end] == '{') depth++;
                else if (source[end] == '}' && --depth == 0) { end++; break; }
            }
            raw = source.Substring(idx, end - idx);
        }
        else
        {
            var semi = source.IndexOf(';', pos);
            raw = semi >= 0 ? source.Substring(idx, semi - idx + 1) : source.Substring(idx);
        }
        var noComments = Regex.Replace(raw, @"//[^\r\n]*", "");
        return Regex.Replace(noComments, @"\s+", " ").Trim();
    }

    private static void RulesInStep()
    {
        var gameRules = Source("Scripts/ui/ObserverUiRules.cs");
        var testRules = Source("Tools/FormableTests/ObserverModeUiTests.cs");
        var members = new[]
        {
            "bool IsObserving(",
            "bool CanIssuePlayerOrders(",
            "bool CanIssueMovementOrders(",
            "bool CanInspectAsViewer(",
            "bool CanSeeArmyAsViewer(",
            "bool CanSeeBattleAsViewer(",
            "Nation DefaultObservedNation(",
            "Nation ResolveInspectedNation(",
            "ScenarioState DefaultObservedState(",
            "List<Nation> ObservedLandlessMovements(",
            "Nation PickMovementForPanel(",
            "ObserverWorldSummary ComputeWorldSummary(",
            "ShellAlertSnapshot BuildWorldObserverSnapshot("
        };
        foreach (var m in members)
        {
            var gameBody = ExtractMember(gameRules, m);
            var testBody = ExtractMember(testRules, m);
            Check(gameBody.Length > m.Length + 4 && gameBody == testBody,
                $"ObserverUiRules parity for {m.TrimEnd('(', ' ')}");
        }
    }

    private static void TestLandlessMovementsInObserver()
    {
        var c = Campaign.CreateBootstrap(20261126);
        Movements.Found(c);
        c.SetPlayer(-1);
        c.ObserverMode = true;
        var fpBefore = CampaignFingerprint.Of(c);

        Check(IsObserving(c, false) && IsObserving(c, true), "ObserverMode campaign detects IsObserving even with uiObserverFlag=false");
        Check(c.Player is null, "World Observer yields Player == null");
        Check(!CanIssuePlayerOrders(c, false), "World Observer cannot issue player orders");

        var list = ObservedLandlessMovements(c);
        Check(list.Count > 0, $"Bootstrap observer campaign exposes active landless movements (count={list.Count})");
        Check(list.All(m => m.Movement is { Dissolved: false, Landless: true }), "all enumerated observer movements are active and landless");

        // Sorting invariant: legitimacy desc -> cadres desc -> DisplayName ordinal CI -> Id asc.
        for (var i = 1; i < list.Count; i++)
        {
            var prev = list[i - 1];
            var cur = list[i];
            var ordered = prev.Movement.Legitimacy > cur.Movement.Legitimacy
                || (Math.Abs(prev.Movement.Legitimacy - cur.Movement.Legitimacy) < 1e-6f &&
                    (prev.Movement.Cadres > cur.Movement.Cadres ||
                     (prev.Movement.Cadres == cur.Movement.Cadres &&
                      (string.Compare(prev.DisplayName, cur.DisplayName, StringComparison.OrdinalIgnoreCase) < 0 ||
                       (string.Equals(prev.DisplayName, cur.DisplayName, StringComparison.OrdinalIgnoreCase) && prev.Id <= cur.Id)))));
            Check(ordered, $"observed movement sort invariant between #{i - 1} ({prev.Tag}) and #{i} ({cur.Tag})");
        }

        // PickMovementForPanel never leaves blank panel when movements exist.
        var picked = PickMovementForPanel(c, null, observing: true);
        Check(picked is not null && picked.Id == list[0].Id, "PickMovementForPanel picks top active landless movement on null current");

        if (list.Count > 1)
        {
            var second = list[1];
            var kept = PickMovementForPanel(c, second, observing: true);
            Check(kept is not null && kept.Id == second.Id, "PickMovementForPanel preserves clicked observed movement across refreshes");

            // Stale movement from another campaign or dissolved movement falls back cleanly.
            var foreign = new Nation { Id = 999999, Tag = "Z99", Movement = new MovementState { Id = "z99", Dissolved = false } };
            var fallbackForeign = PickMovementForPanel(c, foreign, observing: true);
            Check(fallbackForeign is not null && fallbackForeign.Id == list[0].Id, "PickMovementForPanel replaces foreign-campaign movement reference");
        }

        // Order gating in World Observer AND WatchCountry Observer (even if Player == movement).
        var lead = list[0];
        Check(!CanIssueMovementOrders(c, true, lead), "CanIssueMovementOrders blocked in World Observer");
        c.ViewAs(lead.Id);
        Check(IsObserving(c, true) && c.Player?.Id == lead.Id, "WatchCountry on movement sets Player while staying ObserverMode");
        Check(!CanIssueMovementOrders(c, false, lead), "WatchCountry on a movement forbids issuing movement orders");
        Check(ResolveInspectedNation(c, null, preferLanded: true) is { Landless: false },
            "preferLanded fallback avoids returning landless movement for national fiscal screens");

        // Non-observer direct player of movement IS allowed.
        var single = Campaign.CreateBootstrap(20261126);
        Movements.Found(single);
        var mList = ObservedLandlessMovements(single);
        Check(mList.Count > 0, "second bootstrap campaign has movements");
        var ownedMovement = mList[0];
        single.ConfigureSinglePlayer(ownedMovement.Id);
        Check(CanIssueMovementOrders(single, false, ownedMovement), "owning non-observer movement can issue orders");
        Check(!CanIssueMovementOrders(single, true, ownedMovement), "uiObserverFlag=true blocks movement orders");

        // Dissolving all movements results in clean null fallback instead of throwing.
        foreach (var mov in Movements.Of(single))
            mov.Movement.Dissolved = true;
        single.SetPlayer(-1);
        single.ObserverMode = true;
        Check(ObservedLandlessMovements(single).Count == 0, "dissolved movements excluded from ObservedLandlessMovements");
        Check(PickMovementForPanel(single, ownedMovement, observing: true) is null,
            "PickMovementForPanel falls back to active/null without throwing when all movements are dissolved");

        Check(fpBefore == CampaignFingerprint.Of(c), "Observer movement UI queries never mutate CampaignFingerprint");
    }

    private static void TestWorldObserverSummaryAndOutliner()
    {
        var c = Campaign.CreateBootstrap(20261126);
        Movements.Found(c);
        c.SetPlayer(-1);
        c.ObserverMode = true;
        var fpBefore = CampaignFingerprint.Of(c);

        var summary = ComputeWorldSummary(c);
        Check(summary.CountryCount > 20, $"WorldSummary counts landed countries ({summary.CountryCount})");
        Check(summary.WorldGdp > 0 && double.IsFinite(summary.WorldGdp), $"WorldSummary GDP finite & positive ({summary.WorldGdp})");
        Check(summary.WorldPopulation > 0, $"WorldSummary population positive ({summary.WorldPopulation})");
        Check(summary.AverageStability is >= 0f and <= 1f && float.IsFinite(summary.AverageStability), "AverageStability normalized");
        Check(summary.LandlessMovementCount == ObservedLandlessMovements(c).Count, "WorldSummary matches active landless movement count");
        Check(summary.TotalDivisions > 0 && float.IsFinite(summary.TotalDivisions), "WorldSummary TotalDivisions positive & finite");

        var snap = BuildWorldObserverSnapshot(c, outlinerOpen: true);
        Check(snap.OutlinerGroups.Any(g => g.TopicKey == "obs_movements" && g.Rows.Count > 0 && g.Rows[0].RouteKind == "Movements"),
            "World Observer outliner exposes active Landless Movements routed to Movements panel");
        Check(snap.Alerts.Any(a => a.Id == "obs_movements") || snap.Alerts.Count > 0,
            "World Observer alert feed populates without Player");

        var defNation = DefaultObservedNation(c);
        Check(defNation is not null && !defNation.Landless && c.HasOwnedLand(defNation),
            $"DefaultObservedNation picks top landed country ({defNation?.Tag})");
        var defState = DefaultObservedState(c, defNation);
        Check(defState is not null && !LakeRegions.IsWaterState(defState),
            $"DefaultObservedState picks valid non-water region ({defState?.Id})");

        // Verify CampaignIntel returns false when Player == null while ObserverUiRules grants full read visibility.
        Check(!CampaignIntel.PlayerCanInspect(c, defNation.Id), "baseline simulation fog hides when Player == null");
        Check(CanInspectAsViewer(c, false, defNation.Id) && CanInspectAsViewer(c, true, defNation.Id),
            "ObserverUiRules.CanInspectAsViewer grants omniscient observer visibility when Player == null");
        if (c.Armies.Count > 0)
        {
            var army = c.Armies[0];
            Check(!CampaignIntel.PlayerCanSeeArmy(c, army), "baseline CampaignIntel hides army when Player == null");
            Check(CanSeeArmyAsViewer(c, false, army), "ObserverUiRules.CanSeeArmyAsViewer reveals armies in Observer mode");
        }

        Check(fpBefore == CampaignFingerprint.Of(c), "Observer summary/outliner/visibility queries preserve CampaignFingerprint");
    }

    private static void TestAuditedUiContracts()
    {
        // 1. ObserverUiRules.cs.uid validity & uniqueness
        var uidPath = Path.Combine(Repo, "Scripts", "ui", "ObserverUiRules.cs.uid");
        Check(File.Exists(uidPath), "Scripts/ui/ObserverUiRules.cs.uid exists");
        var uid = File.ReadAllText(uidPath).Trim();
        Check(uid.StartsWith("uid://", StringComparison.Ordinal) && uid.Length >= 10, $"ObserverUiRules UID valid ({uid})");

        // 2. Shell line-count (< 800) & zero raw Color constructors across edited Shell views
        var shellDir = Path.Combine(Repo, "Scripts", "ui", "Shell");
        foreach (var file in Directory.EnumerateFiles(shellDir, "*.cs"))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file).Length;
            Check(lines < 800, $"Shell/{name} stays under 800-line ceiling ({lines})");
            var src = File.ReadAllText(file);
            if (!string.Equals(name, "ShellContext.cs", StringComparison.OrdinalIgnoreCase))
                Check(!src.Contains("new Color(", StringComparison.Ordinal) && !src.Contains("Colors.Black", StringComparison.Ordinal),
                    $"Shell/{name} uses Ui.Colour tokens without raw Color literals");
        }

        // 3. MovementUI preserves UiClarity contracts AND adds Observer support
        var movementUi = Source("Scripts/ui/MovementUI.cs");
        Check(movementUi.Contains("CloseMovementPanel"), "MovementUI preserves CloseMovementPanel");
        var incomeIdx = movementUi.IndexOf("MovementFigure(\"income\"", StringComparison.Ordinal);
        var moneyIdx = movementUi.IndexOf("MovementHeading(\"MONEY IN AND OUT\")", StringComparison.Ordinal);
        var landlessIdx = movementUi.LastIndexOf("if (!state.Landless)", Math.Max(0, incomeIdx), StringComparison.Ordinal);
        Check(landlessIdx >= 0 && landlessIdx < incomeIdx && incomeIdx < moneyIdx,
            "MovementUI preserves !state.Landless income guard");
        Check(movementUi.Contains("ObserverUiRules.PickMovementForPanel") &&
              movementUi.Contains("ShowEmptyObserverMovementPanel") &&
              movementUi.Contains("AddObservedMovementsSelector") &&
              movementUi.Contains("OpenObservedMovement") &&
              movementUi.Contains("CanIssueMovementOrders") &&
              movementUi.Contains("obs.ui.movements.readonly_note"),
            "MovementUI implements observed world list, empty-state fallback, and read-only gating");

        // 4. Critical audited Player==null and WatchCountry read-only guards
        Check(Source("Scripts/ui/Shell/NewShellUI.cs").Contains("ctx.OpenObservedMovement =") &&
              Source("Scripts/ui/Shell/NewShellUI.cs").Contains("ObserverUiRules.ResolveInspectedNation"),
            "NewShellUI routes OpenObservedMovement and ResolveInspectedNation");
        Check(Source("Scripts/ui/Shell/ShellCornerView.cs").Contains("RefreshWorldResources") &&
              !Source("Scripts/ui/Shell/ShellCornerView.cs").Contains("if (world?.Player is null) return;"),
            "ShellCornerView renders world resources cleanly when Player is null");
        Check(Source("Scripts/ui/Shell/ShellAlertColumnView.cs").Contains("BuildWorldObserverSnapshot") &&
              Source("Scripts/ui/Shell/ShellAlertColumnView.cs").Contains("OpenObservedMovement"),
            "ShellAlertColumnView renders world observer outliner/alerts and opens movements");
        Check(Source("Scripts/ui/Shell/ShellXpStripView.cs").Contains("RefreshObserverBadge") &&
              Source("Scripts/ui/Shell/ShellXpStripView.cs").Contains("player?.Id ?? -1"),
            "ShellXpStripView safely renders observer badge when Player is null");
        Check(Source("Scripts/ui/SaveLoadUI.cs").Contains("obs.ui.save.world_name") &&
              !Source("Scripts/ui/SaveLoadUI.cs").Contains("if (_campaign?.Player is null) return;"),
            "SaveLoadUI supports saving Observer campaigns without Player");
        Check(Source("Scripts/ui/MapSelectionUI.cs").Contains("if (_campaign is null || pick.Nation is null) return;"),
            "MapSelectionUI permits clicking countries/regions in Observer mode when Player is null");
        Check(Source("Scripts/ui/DealInboxUI.cs").Contains("var playerId = _campaign?.Player?.Id ?? -1;") &&
              Source("Scripts/ui/DealInboxUI.cs").Contains("obs.ui.inbox.no_player"),
            "DealInboxUI avoids per-frame NRE and shows clear observer message when Player is null");
        Check(Source("Scripts/ui/BattleDossierUI.cs").Contains("CanSeeBattleAsViewer"),
            "BattleDossierUI uses ObserverUiRules.CanSeeBattleAsViewer");
        Check(Source("Scripts/ui/FogOfWarUI.cs").Contains("CanInspectAsViewer"),
            "FogOfWarUI uses ObserverUiRules.CanInspectAsViewer");
        Check(Source("Scripts/ui/DiplomacyScreenUI.cs").Contains("obs.ui.diplomacy.compare_need_player") &&
              !Source("Scripts/ui/DiplomacyScreenUI.cs").Contains("if (player is null) return;"),
            "DiplomacyScreenUI renders world country dossiers read-only when Player is null");
        Check(Source("Scripts/ui/NavigatorUI.cs").Contains("ToggleMovementPanel"),
            "NavigatorUI includes direct Landless movements shortcut");

        // 5. #939 & #942 regression guards (bilateral reset, Staff/Build lock, Speed 4-5 lock/hotkey, null-safe inbox/battle/social)
        var workPanel = Source("Scripts/ui/Shell/ShellWorkPanelView.cs");
        Check(workPanel.Contains("var isOwn = !observerMode && world.Player?.Id == nation.Id;") &&
              workPanel.Contains("var isOwn = owner is not null && !observerMode && world.Player?.Id == owner.Id;") &&
              workPanel.Contains("nation.Movement is { Landless: true }"),
            "ShellWorkPanelView locks Staff/Build tabs in Observer mode and routes landless movements to Movement panel");
        var pickerUi = Source("Scripts/ui/CountrySelectionUI.cs");
        Check(pickerUi.Contains("_campaign.SetPlayer(-1);") && pickerUi.Contains("_observerWatched = null;"),
            "BeginObserving clears Player seat and _observerWatched so no bilateral phantom card renders");
        var shellDip = Source("Scripts/ui/Shell/ShellCountryDiplomacyView.cs");
        Check(shellDip.Contains("else if (!observing && world.Player is { } player)") &&
              shellDip.Contains("if (!isOwn && !observing && world.Player is { } actingPlayer)"),
            "ShellCountryDiplomacyView suppresses bilateral player card and direct actions while observing");
        Check(movementUi.Contains("var needsInitialBuild = _game._movementPanel.Visible && _game._movementBuiltFrom.Length == 0;") &&
              movementUi.Contains("requestWindowOnMiss: needsInitialBuild") &&
              movementUi.Contains("_movementPanel.MoveToFront()") &&
              movementUi.Contains("TryEnterPriorityRead") &&
              movementUi.Contains("!key.ShiftPressed || key.CtrlPressed") &&
              Source("Scripts/ui/Shell/HotkeyMap.cs").Contains("ctx.OpenObservedMovement?.Invoke(-1)"),
            "MovementUI fixes Speed 4-5 lock starvation, brings panel to front, and moves hotkey to Shift+O without Diplomacy collision");
        Check(Source("Scripts/ui/ArmsShipmentUI.cs").Contains("_campaign?.Player is not { } player") &&
              Source("Scripts/ui/ForeignAssistanceUI.cs").Contains("_campaign?.Player is not { } player") &&
              Source("Scripts/ui/SocialMemoryUI.cs").Contains("player is not null && nation.Id == player.Id") &&
              Source("Scripts/ui/StrategicReportsUI.cs").Contains("_campaign?.Player is { } currentPlayer") &&
              Source("Scripts/ui/BattleOrderSafetyUI.cs").Contains("_campaign?.Player?.Id == issuer") &&
              Source("Scripts/ui/ResearchScreenUI.cs").Contains("_campaign?.Player ?? ObserverUiRules.ResolveInspectedNation") &&
              Source("Scripts/ui/ArmyRosterUI.cs").Contains("_campaign?.Player is { } player && candidate.NationId == player.Id"),
            "ArmsShipmentUI, ForeignAssistanceUI, SocialMemoryUI, StrategicReportsUI, BattleOrderSafetyUI, ResearchScreenUI, and ArmyRosterUI guard Player == null");

        foreach (var uiFile in new[]
        {
            "Scripts/ui/CountryRankingsUI.cs",
            "Scripts/ui/PauseMenuUI.cs",
            "Scripts/ui/TopBarHudUI.cs",
            "Scripts/ui/DatePlateUI.cs",
            "Scripts/ui/ObserverTopBarUI.cs",
            "Scripts/ui/StrategicHudUI.cs",
            "Scripts/ui/SituationDeskUI.cs",
            "Scripts/ui/SituationRoomUI.cs",
            "Scripts/ui/ArmyBarUI.cs",
            "Scripts/ui/PlanningBarUI.cs",
            "Scripts/ui/ArmyPanelUI.cs",
            "Scripts/ui/ArmyOperationsUI.cs",
            "Scripts/ui/FleetPanelUI.cs",
            "Scripts/ui/LedgerUI.cs",
            "Scripts/ui/WarPeaceUI.cs",
            "Scripts/ui/ConflictsScreenUI.cs",
            "Scripts/ui/ConflictWorkspaceUI.cs",
            "Scripts/ui/CrisisDiplomacyUI.cs",
            "Scripts/ui/OrganizationsUI.cs",
            "Scripts/ui/TerritorialClaimsUI.cs",
            "Scripts/ui/WorldInteractionUI.cs",
            "Scripts/ui/CampaignShellUI.cs",
            "Scripts/ui/RegionInteractionUI.cs",
            "Scripts/ui/RegionBuildListUI.cs",
            "Scripts/ui/SubjectsAndReleaseUI.cs",
            "Scripts/ui/StateAffairsUI.cs",
            "Scripts/ui/PolicyWorkspaceUI.cs",
            "Scripts/ui/PolicyWorkspaceDetails.cs",
            "Scripts/ui/PolicyWorkspacePresentation.cs",
            "Scripts/ui/DomesticCrisisUI.cs",
            "Scripts/ui/CentralBankUI.cs",
            "Scripts/ui/PoliticsUI.cs",
            "Scripts/ui/DecisionUI.cs",
            "Scripts/ui/FormableNationsUI.cs",
            "Scripts/ui/TradeScreenUI.cs",
            "Scripts/ui/TradeInstructionsUI.cs",
            "Scripts/ui/ProductionScreenUI.cs",
            "Scripts/ui/ResearchScreenUI.cs",
            "Scripts/ui/ResearchUI.cs",
            "Scripts/ui/DoctrineScreenUI.cs",
            "Scripts/ui/DoctrineServicesUI.cs",
            "Scripts/ui/DoctrineUI.cs",
            "Scripts/ui/DoctrineTreeUI.cs",
            "Scripts/ui/SupportScreenUI.cs",
            "Scripts/ui/EconomyUI.cs",
            "Scripts/ui/EconomyWorkbenchUI.cs",
            "Scripts/ui/DefconOverviewUI.cs",
            "Scripts/ui/CountryDossierCardUI.cs",
            "Scripts/ui/SideLinksUI.cs"
        })
        {
            Check(File.Exists(Path.Combine(Repo, uiFile)), $"Audited file present: {uiFile}");
        }
    }

    private static Dictionary<string, string> ReadLocFile(string relative)
    {
        var json = Source(relative);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static string[] Placeholders(string text) =>
        Regex.Matches(text ?? "", @"\{\d+(?::[^}]*)?\}")
            .Select(m => Regex.Replace(m.Value, @"^\{(\d+).*$", "{$1}"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    private static void TestLocalizationParity()
    {
        var en = ReadLocFile("game/localization/english/99_observer_ui.json");
        var ru = ReadLocFile("game/localization/russian/99_observer_ui.json");
        Check(en.Count >= 23, $"EN observer loc populated ({en.Count} keys)");
        Check(en.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(ru.Keys.OrderBy(k => k, StringComparer.Ordinal)),
            "EN and RU 99_observer_ui.json have identical key sets");

        // Verify every referenced obs.ui.* key across Scripts/ui/** exists in both files.
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo, "Scripts", "ui"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"(obs\\.ui\\.[^\"]+)\""))
                referenced.Add(m.Groups[1].Value);
        }
        foreach (var key in referenced)
            Check(en.ContainsKey(key) && ru.ContainsKey(key), $"obs.ui key localized in EN & RU: {key}");

        // Verify zero empty strings and matching placeholder signatures.
        foreach (var (key, enVal) in en)
        {
            Check(!string.IsNullOrWhiteSpace(enVal), $"EN value non-empty for '{key}'");
            var ruVal = ru[key];
            Check(!string.IsNullOrWhiteSpace(ruVal), $"RU value non-empty for '{key}'");
            Check(Placeholders(enVal).SequenceEqual(Placeholders(ruVal)),
                $"EN/RU placeholder parity for '{key}': [{string.Join(',', Placeholders(enVal))}]");
        }

        // Verify zero duplicate keys between 99_observer_ui.json and all other EN JSON files.
        var enDir = Path.Combine(Repo, "game", "localization", "english");
        var seenElsewhere = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(enDir, "*.json"))
        {
            if (string.Equals(Path.GetFileName(file), "99_observer_ui.json", StringComparison.OrdinalIgnoreCase)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in doc.RootElement.EnumerateObject())
                seenElsewhere.Add(prop.Name);
        }
        var collisions = en.Keys.Where(seenElsewhere.Contains).ToArray();
        Check(collisions.Length == 0, $"99_observer_ui.json has zero duplicate EN keys ({string.Join(", ", collisions)})");

        // Live Loc resolution smoke check in EN & RU.
        var prevLang = Loc.Language;
        try
        {
            Loc.Language = "english";
            var enRendered = Loc.Format("obs.ui.movements.world_list_fmt", 3);
            Check(enRendered.Contains("3") && enRendered != "obs.ui.movements.world_list_fmt", $"EN Loc.Format resolves ({enRendered})");

            Loc.Language = "russian";
            var ruRendered = Loc.Format("obs.ui.movements.world_list_fmt", 3);
            Check(ruRendered.Contains("3") && ruRendered != "obs.ui.movements.world_list_fmt" && ruRendered != enRendered,
                $"RU Loc.Format resolves distinct Russian copy ({ruRendered})");
        }
        finally
        {
            Loc.Language = string.IsNullOrEmpty(prevLang) ? Loc.DefaultLanguage : prevLang;
        }
    }
}
