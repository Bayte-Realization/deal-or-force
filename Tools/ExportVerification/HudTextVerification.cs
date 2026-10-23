using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // Isolated verification world. No existing save, user preference or session is changed.
    private async Task VerifyHudText(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var country = world.NationByTag("ESP");
        world.ConfigureSinglePlayer(country.Id);
        country.WarSupport = 1;
        country.ArmyExperience = 11637.77f;
        country.PoliticalPoints = 11637.77f;
        foreach (var language in new[] { "english", "russian" })
        {
            Loc.Language = language;
            game.Call("RefreshLocalisedText");
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720), new Vector2I(1920, 1080) })
            {
                GetTree().Root.Size = size;
                var before = CampaignFingerprint.Of(world);
                game.VerificationRefreshHud();
                await Wait(.35);
                game.VerificationRefreshHud();
                await Wait(.15);
                var report = game.VerificationHudTextReport(size.X < 1600);
                Check(report.Length == 0, "all HUD captions, values and details fit " + language + " " + size.X + " " + report);
                Check(CampaignFingerprint.Of(world) == before, "HUD fit and history reads change no campaign state");
                await Capture("hud-text-" + language + "-" + size.X);
                country.Inflation = .45f;
                country.DebtArrears = 1;
                game.VerificationRefreshHud();
                await Wait(.15);
                Check(game.VerificationHudTextReport(size.X < 1600).Length == 0,
                    "long inflation and overdue debt details fit " + language + " " + size.X);
                country.Inflation = .018f;
                country.DebtArrears = 0;
            }
            // Movement uses the same readouts for different concepts. Returning to a country
            // must restore its semantic caption, not freeze a fitted movement label in place.
            var movement = world.Nations.First(n => n.Movement is { Landless: true });
            world.ConfigureSinglePlayer(movement.Id);
            game.VerificationShowMovement(movement);
            game.VerificationRefreshHud();
            await Wait(.2);
            GetTree().Root.Size = new(1280, 720);
            await Wait(.2);
            Check(game.VerificationMovementClearsHud(), "movement panel clears two-row header " + language);
            await Capture("hud-movement-" + language + "-1280");
            GetTree().Root.Size = new(1920, 1080);
            world.ConfigureSinglePlayer(country.Id);
            game.VerificationCloseMovement();
            game.VerificationRefreshHud();
            await Wait(.2);
            Check(game.VerificationHudCaptionsRestored(), "country captions restored after movement " + language);
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationRefreshHud()
    {
        // UI-only history samples ensure the graph competes for space as in a running campaign.
        foreach (var stat in _hudStats)
        {
            stat.Spark.Push(-14, 1);
            stat.Spark.Push(-7, 2);
        }
        RefreshTopBar(_campaign.Player);
    }

    internal bool VerificationHudCaptionsRestored() =>
        _hudStats.All(stat => stat.CaptionKey == stat.CountryCaption);

    internal bool VerificationMovementClearsHud() => _movementPanel.Visible &&
        _movementPanel.GetGlobalRect().Position.Y >= _hudTopPanel.GetGlobalRect().End.Y;

    internal string VerificationHudTextReport(bool compact)
    {
        if ((_hudStatRow.GetParent() == _hudTopStack) != compact) return "wrong row layout";
        var panel = _hudTopPanel.GetGlobalRect();
        foreach (var stat in _hudStats)
        foreach (var label in new[] { stat.Caption, stat.Value, stat.Delta })
        {
            if (!label.Visible || label.Text.Length == 0) continue;
            var needed = label.GetThemeFont("font").GetStringSize(label.Text, HorizontalAlignment.Left, -1,
                label.GetThemeFontSize("font_size")).X;
            if (needed > label.Size.X + 1) return stat.CountryCaption + ": " + label.Text +
                $" needs {needed:0.0}, has {label.Size.X:0.0}";
            var rect = label.GetGlobalRect();
            if (rect.Position.Y < panel.Position.Y || rect.End.Y > panel.End.Y + 1 || rect.End.X > panel.End.X)
                return stat.CountryCaption + ": outside top panel";
        }
        var support = Loc.Format("war support {0}", Fmt.Percent(_campaign.Player.WarSupport));
        if (!_stability.Delta.TooltipText.Contains(support) ||
            !_stability.Delta.Text.Contains(Fmt.Percent(_campaign.Player.WarSupport)))
            return "war support missing from text or full tooltip";
        return "";
    }
}
