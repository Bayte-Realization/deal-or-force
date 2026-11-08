using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyRecruitmentDepotLabelsAndReleaseEscape(DealOrForceGame game, string language, int width)
    {
        var c = game.VerificationWorld; var fp = CampaignFingerprint.Of(c); var random = c.Random.State;
        game.VerificationOpenRecruitmentCatalogue(-1); await Wait(.4);
        await ClickRecruitmentControl(game, "RecruitmentTabDepots", "strategic depots " + language + width);
        var grid = game.FindChild("RecruitmentDepots", true, false) as Control;
        Check(grid is not null && grid.IsVisibleInTree(), "recruitment depot dashboard exists: " + language + width);
        if (grid is null) return;
        var names = grid.FindChildren("RecruitmentDepotName_*", "Label", true, false).OfType<Label>().ToArray();
        var (_, _, _, _, navigation, _) = game.VerificationDossierLayout;
        var rail = navigation.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, navigation.Size);
        Check(names.All(label => (label.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, label.Size)).Position.X > rail.End.X),
            "resource names are not hidden beneath the translated navigation rail: " + language + width);
        Check(names.Length == StrategicStockpiles.TrackedGoods.Length && names.All(label => label.Text.Length > 0 &&
            label.Size.X > 80 && label.Size.Y > 0 && label.GetVisibleLineCount() > 0 && !label.ClipText),
            "every strategic depot has an explicit visible resource name: " + language + width);
        Check(names.All(label =>
        {
            var scale = (GetViewport().GetStretchTransform() * label.GetGlobalTransformWithCanvas()).Scale.Abs();
            return label.GetThemeFontSize("font_size") * MathF.Min(scale.X, scale.Y) >= 13.9f;
        }), "resource names keep a readable physical font: " + language + width);
        var ids = names.Select(label => label.GetInstanceId()).ToArray();
        for (var i = 0; i < 10; i++) game.Call("RefreshRecruitment");
        Check(ids.SequenceEqual(grid.FindChildren("RecruitmentDepotName_*", "Label", true, false).OfType<Label>()
            .Select(label => label.GetInstanceId())), "ten dashboard refreshes reuse all resource labels instead of rebuilding them");
        await Capture("recruitment-depot-names-" + language + "-" + width);
        (game.FindChild("Recruitment", true, false) as Control)?.Hide();
        game.OpenReleaseCountryModal(); await Wait(.3);
        var release = game.FindChild("ReleaseCountryModal", true, false) as Control;
        Check(release is { Visible: true }, "release-country modal opens before Escape proof");
        release?.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().FirstOrDefault()?.GrabFocus();
        await RankingEscape();
        Check(release is { Visible: false } && c.IsPaused,
            "Escape closes release-country modal even from a text field without unpausing: " + language + width);
        Check(CampaignFingerprint.Of(c) == fp && c.Random.State == random,
            "depot inspection and cancelling country release do not spend stocks, create countries or change randomness");
    }
}
