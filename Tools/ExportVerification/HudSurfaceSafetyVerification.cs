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
        var stats = game.FindChildren("HudStat_*", "Control", true, false).OfType<Control>().ToArray();
        Check(stats.Length > 8 && stats.All(s => top.GetGlobalRect().Encloses(s.GetGlobalRect())), "all telemetry cells remain inside the top bar");
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
        await Capture("hud-desk-" + language + "-" + size.X);
        game.Call("SetDeskSide", true); await Wait(.1);
        Check(!desk.Visible && report.Visible, "left desk yields to the open country dossier");
        game.Call("SetDeskOpen", false); game.Call("SetDeskSide", false);
    }
}
