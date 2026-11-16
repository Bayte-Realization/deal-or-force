using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyHudSurfaceSafety(DealOrForceGame game, string language, Vector2I size)
    {
        game.Call("SetDeskSide", false); game.Call("SetDeskOpen", false);
        game.Call("OpenScreen", "Nation"); await Wait(.35);
        var top = game.FindChild("CampaignTopBar", true, false) as Control;
        var report = game.FindChild("StrategicOutliner", true, false) as Control;
        var tab = game.FindChild("SituationDeskTab", true, false) as Control;
        var desk = game.FindChild("SituationDesk", true, false) as Control;
        CheckFits(top, "top instruments " + language + " " + size.X);
        Check(report is not null && top is not null && !report.GetGlobalRect().Intersects(top.GetGlobalRect()),
            "right report is below the instrument strip");
        Check(report is not null && tab is not null && report.GetGlobalRect().Encloses(tab.GetGlobalRect()),
            "folded desk button is laid out inside the report header");
        // Step 2 of the interface plan: on a desktop all twelve figures stand on the plate; on a
        // phone-narrow screen five do and the rest wait in the drawer behind "…" (HudMoreFigures).
        var all = game.FindChildren("HudStat_*", "Control", true, false).OfType<Control>().ToArray();
        var stats = all.Where(s => s.IsVisibleInTree()).ToArray();
        Check(stats.Length >= 5 && stats.All(s => top.GetGlobalRect().Encloses(s.GetGlobalRect())), "all telemetry cells on the plate remain inside the top bar");
        var more = game.FindChild("HudMoreFigures", true, false) as Control;
        Check(stats.Length == all.Length || (more is { Visible: true } && top.GetGlobalRect().Encloses(more.GetGlobalRect())),
            "every figure is on the plate, or the rest are reachable through \"…\" inside the top bar");
        // Step 3: the clock has its own plate at the top right. It stays on screen, holds its
        // buttons, and covers neither a figure nor the report under it.
        var plate = game.FindChild("DatePlate", true, false) as Control;
        CheckFits(plate, "date plate " + language + " " + size.X);
        Check(plate is not null && stats.All(s => !plate.GetGlobalRect().Intersects(s.GetGlobalRect())),
            "the date plate covers no figure " + language + " " + size.X);
        Check(plate is not null && report is not null && !plate.GetGlobalRect().Intersects(report.GetGlobalRect()),
            "the report starts below the date plate " + language + " " + size.X);
        foreach (var name in new[] { "DatePlatePause", "DatePlateSlower", "DatePlateFaster", "DatePlateSpeed", "OpenPauseMenu" })
            Check(game.FindChild(name, true, false) is Control { Visible: true } part && plate is not null &&
                plate.GetGlobalRect().Encloses(part.GetGlobalRect()), name + " stands on the date plate " + language + " " + size.X);
        // Step 4: the alert list stands under the date plate, the report docks under the list, and
        // the report's own inbox button is gone (the plate's "Deals (N)" chip is the one way there).
        var alerts = game.FindChild("CampaignAlerts", true, false) as Control;
        if (alerts is { Visible: true })
        {
            CheckFits(alerts, "alert list " + language + " " + size.X);
            Check(plate is not null && !alerts.GetGlobalRect().Intersects(plate.GetGlobalRect()) &&
                alerts.GetGlobalRect().Position.Y >= plate.GetGlobalRect().End.Y - .5f, "the alert list starts under the date plate " + language + " " + size.X);
            Check(report is not { Visible: true } || !alerts.GetGlobalRect().Intersects(report.GetGlobalRect()),
                "the report docks under the alert list " + language + " " + size.X);
            Check(stats.All(s => !alerts.GetGlobalRect().Intersects(s.GetGlobalRect())), "the alert list covers no figure " + language + " " + size.X);
        }
        Check(game.FindChild("OpenDiplomaticInbox", true, false) is null && game.FindChild("HudDefconBadge", true, false) is null,
            "the report has no inbox button and the plate no DEFCON badge: both live elsewhere now " + language + " " + size.X);
        // At 1920 the bar is one row and ends before the plate; narrower windows take a second row
        // that runs on below it.
        if (size.X >= 1920)
            Check(plate is not null && !plate.GetGlobalRect().Intersects(top.GetGlobalRect()), "at 1920 the figures stand in one row beside the date plate");
        for (var i = 1; i < stats.Length; i++)
            Check(!stats[i - 1].GetGlobalRect().Intersects(stats[i].GetGlobalRect()), "adjacent telemetry cells do not overlap " + i);
        var meters = game.FindChildren("DossierShareTrack", "ProgressBar", true, false).OfType<ProgressBar>().Where(m => m.IsVisibleInTree()).ToArray();
        Check(meters.Length > 0, "country dossier has real percentage tracks");
        foreach (var meter in meters)
        {
            var row = meter.GetParent().GetChildren().OfType<ExplainedRow>().FirstOrDefault();
            GD.Print("HUD_TRACK_LAYOUT row=" + row?.GetGlobalRect() + " track=" + meter.GetGlobalRect() + " value=" + meter.Value);
            Check(row is not null && row.GetGlobalRect().End.Y <= meter.GetGlobalRect().Position.Y + .01f,
                "percentage track occupies a separate slot below all names and figures");
            Check(row is not null && System.Math.Abs(meter.Value - row.Share) < .00011,
                "fractional percentage track preserves its exact share instead of rounding to zero");
        }
        await Capture("hud-clean-" + language + "-" + size.X);
        (game.FindChild("DeskTabButton", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        Check(desk is { Visible: true } && report is { Visible: false }, "open right desk replaces the report, not overlays it");
        CheckFits(desk, "open desk " + language + " " + size.X);
        Check(!desk.GetGlobalRect().Intersects(top.GetGlobalRect()), "desk is below the instrument strip");
        Check(alerts is not { Visible: true } || !desk.GetGlobalRect().Intersects(alerts.GetGlobalRect()),
            "the open desk docks under the alert list " + language + " " + size.X);
        await Capture("hud-desk-" + language + "-" + size.X);
        game.Call("SetDeskSide", true); await Wait(.1);
        Check(!desk.Visible && report.Visible, "left desk yields to the open country dossier");
        game.Call("SetDeskOpen", false); game.Call("SetDeskSide", false);
    }

    /// <summary>
    /// Open the drawer behind "…" when the given figure waits in it (step 2 of the interface plan);
    /// true when it had to be opened. A figure on the plate needs nothing.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> OpenFiguresDrawerFor(DealOrForceGame game, Control cell)
    {
        if (cell is null || cell.IsVisibleInTree()) return false;
        (game.FindChild("HudMoreFigures", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.1);
        return cell.IsVisibleInTree();
    }
}
