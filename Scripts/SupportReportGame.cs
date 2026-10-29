using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// The player-facing bug report: writes `reports/DealOrForce-report-YYYYMMDD-HHMMSS.zip` with
    /// the build, the machine, the logs, the settings and the newest save, and returns the path
    /// (empty when it could not be written).
    ///
    /// It lives behind a button and behind the `report` console command, and the two use this one
    /// method so a report from a player and a report from us are the same file.
    /// </summary>
    public string CreateSupportReport()
    {
        var version = Engine.GetVersionInfo();
        var facts = new SupportReport.Facts
        {
            GameVersion = SaveSystem.GameVersion,
            MapDataVersion = SaveSystem.MapDataVersion,
            Engine = $"{version["major"]}.{version["minor"]}.{version["patch"]} {version["string"]}",
            Renderer = RenderingServer.GetCurrentRenderingMethod(),
            Gpu = RenderingServer.GetVideoAdapterName(),
            Cpu = OS.GetProcessorName(),
            System = OS.GetName() + " " + OS.GetVersion(),
            Language = Loc.Language,
            Screen = $"{GetViewport().GetVisibleRect().Size} window {DisplayServer.WindowGetSize()}",
            World = _campaign is null
                ? "no campaign open"
                : $"{_campaign.Player?.DisplayName ?? "no country chosen"} · day {_campaign.ElapsedDays}",
            SettingsFile = GamePaths.SettingsFile,
            LogsFolder = GamePaths.Logs,
            SavesFolder = GamePaths.Saves,
            ReportsFolder = GamePaths.Reports,
        };
        if (_modLoader is not null && _modLoader.EnabledCount > 0)
            facts.Notes.Add($"{_modLoader.EnabledCount} mod(s) enabled");

        var path = SupportReport.Create(facts);
        if (path.Length > 0)
        {
            DiagnosticLogs.Write("system", $"SUPPORT_REPORT path={path}");
            GD.Print("[report] written to " + path);
        }
        else
        {
            DiagnosticLogs.Write("system", $"SUPPORT_REPORT failed reason={SupportReport.LastFailure}");
            GD.PushWarning("A report could not be written: " + SupportReport.LastFailure);
        }
        return path;
    }
}
