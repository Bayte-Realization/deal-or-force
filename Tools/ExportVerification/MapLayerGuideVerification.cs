using System;
using System.Collections.Generic;
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
            var baselinePanelRect = (game.FindChild("MapLayerDrawerPanel", true, false) as Control).GetGlobalRect();
            var baselineButtonRects = new Dictionary<StrategicMapMode, Rect2>();
            foreach (var m in Enum.GetValues<StrategicMapMode>())
            {
                if (game.FindChild("MapMode" + m, true, false) is Button btn)
                    baselineButtonRects[m] = btn.GetGlobalRect();
            }
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
                var currentPanelRect = panel.GetGlobalRect();
                Check(currentPanelRect.Position.DistanceTo(baselinePanelRect.Position) < 0.5f &&
                      currentPanelRect.Size.DistanceTo(baselinePanelRect.Size) < 0.5f,
                    $"map layer drawer panel stays stationary on hover ({mode}/{language}/{size.X})");
                foreach (var (m, baseRect) in baselineButtonRects)
                {
                    if (game.FindChild("MapMode" + m, true, false) is Button btn)
                    {
                        var curRect = btn.GetGlobalRect();
                        Check(curRect.Position.DistanceTo(baseRect.Position) < 0.5f &&
                              curRect.Size.DistanceTo(baseRect.Size) < 0.5f,
                            $"map mode button {m} does not dodge cursor when explaining {mode} ({language}/{size.X})");
                    }
                }
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
            // Specifically oscillate between Economy (3 legend keys) and Relief (0 legend keys)
            var econBtn = game.FindChild("MapModeEconomy", true, false) as Button;
            var reliefBtn = game.FindChild("MapModeRelief", true, false) as Button;
            for (var bounce = 0; bounce < 4; bounce++)
            {
                econBtn.EmitSignal(Control.SignalName.MouseEntered);
                await Wait(.05);
                Check(econBtn.GetGlobalRect().Position.DistanceTo(baselineButtonRects[StrategicMapMode.Economy].Position) < 0.5f &&
                      reliefBtn.GetGlobalRect().Position.DistanceTo(baselineButtonRects[StrategicMapMode.Relief].Position) < 0.5f,
                    $"Economy hover bounce {bounce} does not shift Economy or Relief buttons ({language}/{size.X})");
                reliefBtn.EmitSignal(Control.SignalName.MouseEntered);
                await Wait(.05);
                Check(econBtn.GetGlobalRect().Position.DistanceTo(baselineButtonRects[StrategicMapMode.Economy].Position) < 0.5f &&
                      reliefBtn.GetGlobalRect().Position.DistanceTo(baselineButtonRects[StrategicMapMode.Relief].Position) < 0.5f,
                    $"Relief hover bounce {bounce} does not shift Economy or Relief buttons ({language}/{size.X})");
            }
            (game.FindChild("MapModeStability", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.2);
            Check(!(game.FindChild("MapLayerDrawer", true, false) as Control).Visible, "applying a layer closes the guide");
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng, "reading and switching map layers changes no campaign state");
        }
        await VerifyFleetAndInspectorEscape(game);
    }

    private async Task VerifyFleetAndInspectorEscape(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var before = CampaignFingerprint.Of(c);
        var rng = c.Random.State;
        var fleet = c.Fleets.FirstOrDefault(f => f.NationId == c.Player?.Id) ?? c.Fleets.FirstOrDefault();
        Check(fleet is not null, "campaign has at least one fleet to inspect");
        if (fleet is not null)
        {
            game.VerificationPickFleet(fleet);
            await Wait(.15);
            var fleetPanel = game.FindChild("FleetPanel", true, false) as Control;
            var pauseMenu = game.FindChild("PauseMenu", true, false) as Control;
            Check(fleetPanel is { Visible: true }, "picking a fleet opens FleetPanel");
            // Even when a button inside FleetPanel holds focus, Escape closes FleetPanel without opening PauseMenu
            var anyFleetButton = fleetPanel?.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.Visible && !b.Disabled);
            anyFleetButton?.GrabFocus();
            await Wait(.05);
            using (var esc = new InputEventKey { Pressed = true, Keycode = Key.Escape })
            {
                game._Input(esc);
                if (!game.GetViewport().IsInputHandled())
                    game._UnhandledInput(esc);
            }
            await Wait(.15);
            Check(fleetPanel is { Visible: false }, "pressing Escape closes FleetPanel");
            Check(pauseMenu is null || !pauseMenu.Visible, "closing FleetPanel with Escape does not open PauseMenu");
        }
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng, "opening and closing FleetPanel on Escape changes no campaign state");
    }
}

public partial class DealOrForceGame
{
    internal StrategicMapMode VerificationLayerMode => _globe.MapMode;
    internal void VerificationPickFleet(Fleet fleet) => OnFleetPicked(fleet);
}
