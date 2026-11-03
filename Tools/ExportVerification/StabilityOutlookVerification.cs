using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyStabilityOutlook(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var country = world.NationByTag("POL");
        world.ConfigureSinglePlayer(country.Id); world.IsPaused = true;
        game.VerificationSelectPlayerCountry();
        game.Call("CloseSidePanel");
        var initialStability = country.Stability;
        var initialGovernance = country.StabilityGovernance;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        foreach (var recovery in new[] { true, false })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            country.StabilityGovernance = recovery ? .04f : -.18f;
            country.Stability = recovery ? .2f : .95f;
            game.VerificationRefreshHud();
            var expected = StabilityOutlook.Read(world, country);
            var before = CampaignFingerprint.Of(world);
            game.VerificationInspectPick(new GlobePick(country, null, -1, country.CapitalLatitude, country.CapitalLongitude));
            (game.FindChild("CountryPageOverview", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.25);
            var scroll = game.FindChild("CountryFactsScroll", true, false) as ScrollContainer;
            var panel = game.FindChild("CountryStabilityOutlook", true, false) as Control;
            Check(panel is not null && panel.IsVisibleInTree(), "domestic outlook is reachable from country Overview");
            // The dossier is intentionally scrollable at 720p; scroll through the real control.
            scroll.ScrollVertical = (int)panel.Position.Y;
            await Wait(.15);
            var label = game.FindChild("OutlookDirection", true, false) as Label;
            Check(label.Text == Loc.Get(expected.Direction), "outlook direction uses current conditions " + language);
            Check((game.FindChild("OutlookCurrent", true, false) as Label).Text == Fmt.Percent(expected.Current) &&
                (game.FindChild("OutlookTarget", true, false) as Label).Text == Fmt.Percent(expected.Target), "outlook figures match the actual stability rule");
            var meter = game.FindChild("OutlookMeter", true, false) as ProgressBar;
            Check(meter.Value == expected.Current || System.Math.Abs(meter.Value - expected.Current) < .00001, "outlook meter shows current stability");
            foreach (var name in new[] { "OutlookHeading", "OutlookDirection", "OutlookCurrentCaption", "OutlookCurrent", "OutlookTargetCaption", "OutlookTarget", "OutlookMeter" })
            {
                var control = game.FindChild(name, true, false) as Control;
                Check(control.Size.Y >= control.GetMinimumSize().Y - 1 && control.Size.X > 15, "readable outlook element " + name);
                Check(!control.GetGlobalRect().Intersects(meter.GetGlobalRect()) || control == meter,
                    "outlook meter does not obscure " + name);
            }
            CheckFits(panel, "domestic outlook " + language + " " + size.X);
            Check(scroll.GetGlobalRect().Encloses(panel.GetGlobalRect()), "the entire outlook can be scrolled into view at " + size.X);
            Check(before == CampaignFingerprint.Of(world), "reading outlook on pause is fingerprint-neutral");
            await Capture("country-outlook-" + (recovery ? "recovery-" : "pressure-") + language + "-" + size.X);

            var toggle = game.FindChild("OutlookCalculationToggle", true, false) as Button;
            var details = game.FindChild("OutlookCalculation", true, false) as Control;
            Check(toggle is not null && !details.Visible && toggle.Text == Loc.Get("Show calculation"),
                "calculation starts collapsed with a translated invitation");
            toggle.GrabFocus();
            Check(toggle.HasFocus(), "calculation button accepts keyboard focus");
            // Exercise keyboard activation, not only the callback behind the button.
            try
            {
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = true });
                await Wait(.1);
            }
            finally { Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = false }); }
            await Wait(.15);
            Check(details.IsVisibleInTree() && toggle.Text == Loc.Get("Hide calculation"), "Enter reveals the calculation on pause");
            var contributions = details.GetChildren().OfType<HBoxContainer>().ToArray();
            Check(contributions.Length == expected.Drivers.Entries.Count, "calculation includes every real contribution");
            for (var i = 0; i < contributions.Length; i++)
            {
                var labels = contributions[i].GetChildren().OfType<Label>().ToArray();
                var entry = expected.Drivers.Entries[i];
                Check(labels.Length == 2 && labels[0].Text == Loc.Get(entry.Source) &&
                    labels[1].Text == Loc.Format("{0:+0.0;-0.0;0.0} pp", entry.Value * 100f), "contribution agrees with the model " + i);
                Check(!labels[0].GetGlobalRect().Intersects(labels[1].GetGlobalRect()) &&
                    labels.All(l => l.Size.Y >= l.GetMinimumSize().Y - 1), "cause and amount remain separate and readable " + i);
            }
            var total = game.FindChild("OutlookCalculationTotal", true, false) as Label;
            Check(total.Text == Loc.Format("Target under current conditions: {0}", Fmt.Percent(expected.Target)),
                "calculation total is the model target, not a new UI formula");
            scroll.ScrollVertical = (int)panel.Position.Y;
            await Wait(.15);
            await Capture("country-outlook-calculation-" + (recovery ? "recovery-" : "pressure-") + language + "-" + size.X);
            scroll.ScrollVertical = int.MaxValue;
            await Wait(.15);
            var note = game.FindChild("OutlookCalculationNote", true, false) as Control;
            Check(scroll.GetGlobalRect().Encloses(total.GetGlobalRect()) && scroll.GetGlobalRect().Encloses(note.GetGlobalRect()),
                "total and caveat can be reached by scrolling at " + size.X);
            if (size.X == 1280)
                await Capture("country-outlook-calculation-end-" + (recovery ? "recovery-" : "pressure-") + language + "-" + size.X);

            // Repainting the dossier must not turn this disclosure into a one-frame UI state.
            game.VerificationInspectPick(new GlobePick(country, null, -1, country.CapitalLatitude, country.CapitalLongitude));
            await Wait(.15);
            details = game.FindChild("OutlookCalculation", true, false) as Control;
            toggle = game.FindChild("OutlookCalculationToggle", true, false) as Button;
            Check(details.IsVisibleInTree(), "open calculation survives dossier refresh");
            toggle.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.1);
            Check(!details.Visible && toggle.Text == Loc.Get("Show calculation"), "button can collapse the explanation again");
            Check(before == CampaignFingerprint.Of(world) && world.IsPaused, "expanding, scrolling and collapsing never change the campaign");
        }
        // Fog must not expose the target through this new surface, even if the model could read it.
        var foreign = world.NationByTag("JPN");
        Check(!CampaignIntel.PlayerCanInspect(world, foreign.Id), "fixture has no intelligence on distant foreign country");
        game.VerificationInspectPick(new GlobePick(foreign, null, -1, foreign.CapitalLatitude, foreign.CapitalLongitude));
        await Wait(.2);
        Check(game.FindChild("CountryStabilityOutlook", true, false) is null, "unknown foreign outlook remains hidden by intelligence boundary");
        country.Stability = initialStability; country.StabilityGovernance = initialGovernance;
        game.Call("CloseMapSelection");
    }
}
