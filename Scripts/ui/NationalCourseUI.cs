using System.Linq;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ShowNationalCourse(Nation n, VBoxContainer target)
    {
        if (!_campaign.FeatureEnabled("national_courses"))
        { target.AddChild(Title("Mechanic unavailable", 14, Ui.Colour("text.caption"))); return; }
        var m = n.NationalCourse;
        PolicyHeading(target, "NATIONAL AGENDA");
        if (!m.Initialized)
        { target.AddChild(Title("National experience is still being assessed.", 14, Ui.Colour("text.caption"))); return; }
        PolicyText(target, "Effects are weighted by accumulated conviction, not granted at full strength.");
        var targets = NationalCourses.Targets(_campaign, n);
        foreach (var conviction in m.Convictions.OrderByDescending(p => p.Value).ThenBy(p => p.Key))
        {
            if (NationalCourseCatalog.Find(conviction.Key) is not { } d) continue;
            var card = new PanelContainer { Name = "Course_" + d.Id };
            card.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 8));
            var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 5); card.AddChild(body);
            var heading = new HBoxContainer();
            var icon = new TextureRect { Texture = Art("icon_" + d.Icon, "res://game/gfx/interface/icons/icon_" + d.Icon + ".png"),
                CustomMinimumSize = new Vector2(24, 24), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = ArtColour("icon_" + d.Icon, "text.accent") };
            heading.AddChild(icon);
            var title = Title(d.Name, 14, Ui.Colour(conviction.Key == m.Dominant ? "text.accent" : "text.bright"));
            title.AutowrapMode = TextServer.AutowrapMode.WordSmart; title.SizeFlagsHorizontal = SizeFlags.ExpandFill; heading.AddChild(title);
            var percentage = Title(Fmt.Percent(conviction.Value), 13, Ui.Colour("text.bright"));
            percentage.Name = "ConvictionShare";
            percentage.AutowrapMode = TextServer.AutowrapMode.Off;
            percentage.HorizontalAlignment = HorizontalAlignment.Right;
            percentage.CustomMinimumSize = new Vector2(54, 20);
            percentage.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            heading.AddChild(percentage); body.AddChild(heading);
            var meter = new ProgressBar { MinValue = 0, MaxValue = 1, Value = conviction.Value, ShowPercentage = false,
                CustomMinimumSize = new Vector2(0, 7), MouseFilter = MouseFilterEnum.Ignore };
            meter.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.inset") });
            meter.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour("text.accent") });
            body.AddChild(meter);
            var direction = targets.GetValueOrDefault(d.Id) - conviction.Value;
            body.AddChild(Title(direction > .02f ? "Gaining ground" : direction < -.02f ? "Losing ground" : "Broadly settled", 12, Ui.Colour("text.caption")));
            card.TooltipText = Ui.Tip(Tr(d.Description) + "\n\n" + NationalModifiers.Block(d.Modifiers) + "\n" +
                Tr("Effects are weighted by accumulated conviction, not granted at full strength."));
            target.AddChild(card);
        }
    }
}
