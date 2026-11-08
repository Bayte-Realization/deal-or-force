using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyDiplomacyComfort(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id); world.IsPaused = true;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale");
            game.Call("RefreshLocalisedText"); game.VerificationOpenDiplomacy("DEU"); await Wait(.55);
            var caption = language + size.X;
            var before = CampaignFingerprint.Of(world); var rng = world.Random.State;
            var panel = game.FindChild("DiplomacyWorkspacePanel", true, false) as Control;
            var rail = game.FindChild("CampaignNavigation", true, false) as Control;
            var country = game.FindChild("DiplomacySearch", true, false) as LineEdit;
            var action = game.FindChild("DiplomacyActionSearch", true, false) as LineEdit;
            var pool = game.FindChild("DiplomacyListRows", true, false) as Control;
            Check(panel is not null && rail is not null && country is not null && action is not null && pool is not null,
                "comfort proof finds actual diplomacy controls: " + caption);
            if (panel is null || rail is null || country is null || action is null || pool is null) return;
            Rect2 Rect(Control c) => c.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, c.Size);
            Check(Rect(panel).Position.X >= Rect(rail).End.X + 7,
                "diplomacy panel clears the real navigation rail: " + caption);
            GD.Print($"DIPLOMACY_BOUNDS {caption} panel_x={Rect(panel).Position.X} rail_end={Rect(rail).End.X}");
            CheckFits(panel, "comfort diplomacy " + caption);
            Check(Rect(panel).Encloses(Rect(country)) && Rect(panel).Encloses(Rect(action)),
                "both search fields remain inside the workspace: " + caption);
            var ids = pool.GetChildren().OfType<Button>().Select(b => b.GetInstanceId()).ToArray();
            Check(ids.Length > 200, "actual warmed country pool exists: " + caption);
            foreach (var field in new[] { country, action })
            {
                var t = game.GetViewport().GetStretchTransform() * field.GetGlobalTransformWithCanvas();
                var scale = MathF.Min(MathF.Abs(t.Scale.X), MathF.Abs(t.Scale.Y));
                Check(field.Size.Y * scale >= 31.9f && field.GetThemeFontSize("font_size") * scale >= 11.9f,
                    "search has a physical text/target floor: " + caption + field.Name);
            }
            foreach (var row in pool.GetChildren().OfType<Button>().Where(b => b.Visible).Take(4))
            {
                Check(row.Modulate == Colors.White, "country selection never tints the flag: " + caption + row.Name);
                foreach (var label in row.FindChildren("*", "Label", true, false).OfType<Label>())
                {
                    var t = game.GetViewport().GetStretchTransform() * label.GetGlobalTransformWithCanvas();
                    var scale = MathF.Min(MathF.Abs(t.Scale.X), MathF.Abs(t.Scale.Y));
                    var minimum = label.Name == "DiplomacyCountryName" || label.Name == "DiplomacyCountryOpinion" ? 14 : 12;
                    Check(label.GetThemeFontSize("font_size") * scale >= minimum - .1f,
                        "country list typography keeps physical size: " + caption + label.Name);
                }
            }
            var standingCaptions = game.FindChildren("DiplomacyStandingCaption", "Label", true, false)
                .OfType<Label>().Where(label => label.IsVisibleInTree()).ToArray();
            Check(standingCaptions.Length > 0 && standingCaptions.All(label => label.GetLineCount() == 1),
                "world-standing captions stay whole above country chips: " + caption);
            await Capture("diplomacy-comfort-" + language + "-" + size.X);
            await ComfortSearchKey(shift: false);
            Check(GetViewport().GuiGetFocusOwner() == country, "Ctrl+F reaches country search: " + caption);
            Search(country, "DEU"); await Wait(.2);
            Check(pool.GetChildren().OfType<Button>().Count(b => b.Visible) == 1,
                "country tag search has one real result: " + caption);
            await AllianceReviewKey(Key.Enter);
            Check((game.FindChild("DiplomacyDossierName", true, false) as Label)?.Text == world.NationByTag("DEU").DisplayName,
                "Enter opens the first matching country dossier: " + caption);
            Search(country, "__missing_country__"); await Wait(.1);
            Check(pool.GetChildren().OfType<Button>().All(b => !b.Visible), "empty search leaves no stale clickable countries: " + caption);
            Search(country, ""); await Wait(.2);
            Check(pool.GetChildren().OfType<Button>().Select(b => b.GetInstanceId()).SequenceEqual(ids),
                "typing and clearing reuse the actual country/flag controls: " + caption);
            await ComfortSearchKey(shift: true);
            Check(GetViewport().GuiGetFocusOwner() == action, "Ctrl+Shift+F reaches action search: " + caption);
            Search(action, "__missing_action__"); await Wait(.1); Search(action, "");
            await AllianceReviewKey(Key.Escape);
            Check(!panel.IsVisibleInTree() && world.IsPaused, "Escape closes diplomacy without unpausing: " + caption);
            Check(CampaignFingerprint.Of(world) == before && world.Random.State == rng,
                "comfort navigation changes neither campaign nor RNG: " + caption);
            game.VerificationOpenDiplomacy("DEU"); await Wait(.2);
            var reopenedX = Rect(panel).Position.X;
            game.Call("CloseDiplomacyScreen"); game.VerificationOpenDiplomacy("DEU"); await Wait(.2);
            Check(MathF.Abs(Rect(panel).Position.X - reopenedX) < 1,
                "reopening does not accumulate workspace offsets: " + caption);
        }
    }

    private async Task ComfortSearchKey(bool shift)
    {
        GetViewport().PushInput(new InputEventKey { Keycode=Key.F, PhysicalKeycode=Key.F, Pressed=true, CtrlPressed=true, ShiftPressed=shift }, true);
        GetViewport().PushInput(new InputEventKey { Keycode=Key.F, PhysicalKeycode=Key.F, Pressed=false, CtrlPressed=true, ShiftPressed=shift }, true);
        await Wait(.12);
    }
}
