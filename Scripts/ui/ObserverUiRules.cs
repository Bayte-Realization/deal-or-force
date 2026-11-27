using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

namespace DealOrForce;

/// <summary>
/// Aggregate global state presented on world-observer top plates when no player nation owns the seat.
/// </summary>
internal readonly record struct ObserverWorldSummary(
    int CountryCount,
    double WorldGdp,
    long WorldPopulation,
    float AverageStability,
    int OpenWarCount,
    int FlashpointCount,
    int LandlessMovementCount,
    float TotalDivisions);

/// <summary>
/// Pure-C# Observer Mode UI rules shared across MovementPanel, NewShell chrome, and shared panels:
/// unifies world-observer detection, omniscient fog-of-war bypass, read-only command gating,
/// active landless movement enumeration, and world outliner / default-subject selection.
/// </summary>
internal static class ObserverUiRules
{
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
}
