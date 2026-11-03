using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyMapLayerGuide(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        game.VerificationSelectPlayerCountry(); game.Call("CloseSidePanel");
        var before = CampaignFingerprint.Of(c); var rng = c.Random.State;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText"); await Wait(.2);
            (game.FindChild("MapLayerSelector", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.2);
            var readingMode = game.VerificationLayerMode;
            foreach (var mode in Enum.GetValues<StrategicMapMode>())
            {
                var button = game.FindChild("MapMode" + mode, true, false) as Button;
                button.GrabFocus(); await Wait(.15);
                Check(game.VerificationLayerMode == readingMode, "focus does not silently switch the map mode");
                var title = game.FindChild("MapLayerGuideTitle", true, false) as Label;
                Check(title.Text == Loc.Format("Layer guide: {0}", Loc.Get(mode == StrategicMapMode.Diplomatic ? "Diplomacy" : mode.ToString())),
                    "keyboard focus explains layer without applying it: " + mode + "/" + language);
                var panel = game.FindChild("MapLayerDrawerPanel", true, false) as Control;
                CheckFits(panel, "map guide " + mode + " " + size.X);
                var legend = game.FindChild("MapLayerLegend", true, false) as HFlowContainer;
                var keys = GlobePresentation.LayerLegend(mode);
                Check(legend.GetChildCount() == keys.Count, "only real palette keys are shown: " + mode);
                for (var i = 0; i < keys.Count; i++)
                {
                    var row = legend.GetChild<HBoxContainer>(i);
                    Check(row.GetChild<ColorRect>(0).Color == keys[i].Colour && row.GetChild<Label>(1).Text == Loc.Get(keys[i].Label),
                        "legend uses renderer palette and translated meaning: " + mode + "/" + i);
                    Check(panel.GetGlobalRect().Encloses(row.GetGlobalRect()), "legend row stays inside the drawer");
                    var caption = row.GetChild<Label>(1);
                    Check(caption.Size.X > 10 && caption.Size.X >= caption.GetMinimumSize().X - 1 &&
                        caption.Size.Y <= caption.GetThemeFontSize("font_size") * 2,
                        "legend caption has a natural width, not a one-pixel wrapped column");
                }
                if (mode is StrategicMapMode.Stability or StrategicMapMode.Diplomatic)
                    await Capture("map-layer-guide-" + mode.ToString().ToLowerInvariant() + "-" + language + "-" + size.X);
            }
            (game.FindChild("MapModeStability", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.2);
            Check(!(game.FindChild("MapLayerDrawer", true, false) as Control).Visible, "applying a layer closes the guide");
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng, "reading and switching map layers changes no campaign state");
        }
    }
}

public partial class DealOrForceGame
{
    internal StrategicMapMode VerificationLayerMode => _globe.MapMode;
}
