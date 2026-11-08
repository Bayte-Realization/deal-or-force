using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private static string RecruitmentDepotName(string good) => Tr(good == "MotorVehicles" ? "Motor vehicles" : good);

    private void BuildRecruitmentDepotRows()
    {
        var grid = new GridContainer { Name = "RecruitmentDepots", Columns = 2,
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 6);
        _recruitmentStockpileHost.AddChild(grid);
        foreach (var good in StrategicStockpiles.TrackedGoods)
        {
            var card = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.AddThemeConstantOverride("separation", 1);
            var caption = Title(RecruitmentDepotName(good), 14, Ui.Colour("text.body"));
            caption.Name = "RecruitmentDepotName_" + good.Replace(" ", "_");
            caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            // A clipped ExpandFill caption had a zero intrinsic width and could
            // disappear beside the stock figure. Name and value get their own rows.
            caption.ClipText = false;
            var value = Title("", 14, Ui.Colour("text.body_soft"));
            value.Name = "RecruitmentDepotValue_" + good.Replace(" ", "_");
            value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            value.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            card.AddChild(caption); card.AddChild(value); grid.AddChild(card);
            _recruitmentDepotLabels[good] = (caption, value);
        }
        _recruitmentStockpileHost.Resized += FitRecruitmentDepotReading;
        FitRecruitmentDepotReading();
    }

    private void FitRecruitmentDepotReading()
    {
        var pixels = (GetViewport().GetStretchTransform() * _recruitmentStockpileHost.GetGlobalTransformWithCanvas()).Scale.Abs();
        var font = DecisionReadability.FontSize(14, MathF.Max(.01f, MathF.Min(pixels.X, pixels.Y)),
            Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f),
            Math.Clamp(Ui.Metric("recruitment.minimum_font_pixels", 14), 8, 36));
        if (_recruitmentDepotLabels.Count > 0 && _recruitmentDepotLabels.Values.First().Caption.GetThemeFontSize("font_size") == font) return;
        foreach (var (caption, value) in _recruitmentDepotLabels.Values)
        {
            caption.AddThemeFontSizeOverride("font_size", font);
            value.AddThemeFontSizeOverride("font_size", font);
        }
    }
}
