using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyDoctrineComfort(DealOrForceGame game, string article, string language, int width)
    {
        var caption = language + width;
        var panel = game.FindChild("DoctrineWorkbench", true, false) as Control;
        var rail = game.FindChild("CampaignNavigation", true, false) as Control;
        var list = game.FindChild("DoctrineBookList", true, false) as Control;
        Check(panel is not null && rail is not null && list is not null, "doctrine comfort uses actual workspace controls: " + caption);
        if (panel is null || rail is null || list is null) return;
        Rect2 Rect(Control c) => c.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, c.Size);
        CheckFits(panel, "doctrine readable workspace " + caption);
        Check(Rect(panel).Position.X > Rect(rail).End.X + 5, "doctrine workspace clears translated navigation: " + caption);
        var subtitle = panel.FindChild("ScreenHeaderSubtitle", true, false) as Label;
        Check(subtitle is not null && subtitle.Text == Loc.Get("What each service is taught to be, what it costs to change its mind, and what the country pays it in.") &&
            (language != "russian" || subtitle.Text.StartsWith("Чему", StringComparison.Ordinal)),
            "purpose follows the active language, including RU: " + caption);
        foreach (var name in new[] { "DoctrineTacticalView", "DoctrineCourseView", "DoctrineGeneralsView", "CloseDoctrineScreen", "DoctrineZoomIn", "DoctrineZoomOut" })
        {
            var button = game.FindChild(name, true, false) as Button;
            CheckFits(button, "readable doctrine pointer target " + name + caption);
            var pixels = (GetViewport().GetStretchTransform() * button.GetGlobalTransformWithCanvas()).Scale.Abs();
            Check(button.Size.Y * MathF.Min(pixels.X, pixels.Y) >= 31.9f,
                "doctrine target keeps a physical height floor: " + name + caption);
        }
        var buttons = list.FindChildren("ScreenBook_*", "Button", true, false).OfType<Button>().ToArray();
        var bookScroll = game.FindChild("DoctrineBookScroll", true, false) as Control;
        Check(Rect(bookScroll).Size.Y >= 100, "book catalogue retains a usable visible viewport: " + caption);
        var currencyHeads = panel.FindChildren("DoctrineCurrencyHead_*", "Label", true, false).OfType<Label>().ToArray();
        Check(currencyHeads.Length >= 3 && currencyHeads.All(label => label.GetLineCount() == 1),
            "currency headings never become a vertical column of letters: " + caption);
        Check(buttons.Length == TacticalDoctrineCatalog.All.Count(), "catalogue keeps every base/modded book: " + caption);
        Check(buttons.All(b => b.AutowrapMode == TextServer.AutowrapMode.WordSmart &&
            b.Text.Contains(Loc.Get(TacticalDoctrineCatalog.Find(b.Name.ToString()[11..]).Name), StringComparison.Ordinal)),
            "book names stay complete and wrap instead of ellipsising: " + caption);
        foreach (var name in new[] { "ScreenHeaderSubtitle", "Title_" + article, "Price_" + article, "StudyArticle_" + article })
        {
            var control = name == "ScreenHeaderSubtitle" ? subtitle : game.FindChild(name, true, false) as Control;
            var pixels = (GetViewport().GetStretchTransform() * control.GetGlobalTransformWithCanvas()).Scale.Abs();
            var floor = name.StartsWith("Price_", StringComparison.Ordinal) || control == subtitle ? 12 : 14;
            Check(control.GetThemeFontSize("font_size") * MathF.Min(pixels.X, pixels.Y) >= floor - .1f,
                "doctrine content keeps a physical font floor: " + name + caption);
            if (control is Label label)
            {
                var lines = Math.Max(1, label.GetLineCount());
                var height = lines * label.GetLineHeight() + (lines - 1) * label.GetThemeConstant("line_spacing");
                Check(label.Size.Y >= height - 1, "wrapped doctrine text is not vertically cropped: " + name + caption);
            }
        }
        var world = game.VerificationWorld; var before = CampaignFingerprint.Of(world); var rng = world.Random.State;
        var ids = buttons.Select(b => b.GetInstanceId()).ToArray();
        for (var i = 0; i < 10; i++) game.VerificationRefreshPausedDoctrines();
        await Wait(.2);
        Check(list.FindChildren("ScreenBook_*", "Button", true, false).OfType<Button>().Select(b => b.GetInstanceId()).SequenceEqual(ids),
            "ten doctrine refreshes retain book controls: " + caption);
        Check(CampaignFingerprint.Of(world) == before && world.Random.State == rng,
            "doctrine comfort layout spends nothing and consumes no randomness: " + caption);
        foreach (var name in new[] { "StudyArticle_" + article, "RushArticle_" + article })
            VerifyDoctrineActionCaption(game.FindChild(name, true, false) as Button, caption);
        await Capture("doctrine-comfort-" + language + "-" + width);
    }

    private async Task VerifyDoctrineOperationalReading(DealOrForceGame game)
    {
        Loc.Language = "russian"; GetTree().Root.Size = new Vector2I(1280, 720);
        game.Call("RefreshLocalisedText");
        var world = game.VerificationWorld; var before = CampaignFingerprint.Of(world); var rng = world.Random.State;
        foreach (var id in new[] { "elastic_defence", "dispersed_warfare", "air_shield", "convoy_service", "mission_command", "joint_logistics" })
        {
            game.VerificationOpenPausedDoctrines(id); await Wait(.25);
            var branch = TacticalDoctrineCatalog.Find(id);
            foreach (var article in branch.Nodes)
            {
                var card = game.FindChild("DoctrineNode_" + article.Id, true, false) as Control;
                var title = card?.FindChild("Title_" + article.Id, true, false) as Label;
                var price = card?.FindChild("Price_" + article.Id, true, false) as Label;
                var buttons = card?.FindChild("Buttons_" + article.Id, true, false) as Control;
                Check(card is not null && title is not null && price is not null && buttons is not null,
                    "operational article has real reading and action controls: " + article.Id);
                if (card is null || title is null || price is null || buttons is null) continue;
                Check(title.Text == Loc.Get(article.Name) && title.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming,
                    "operational article preserves its complete translated title: " + article.Id);
                foreach (var label in new[] { title, price })
                {
                    var lines = Math.Max(1, label.GetLineCount());
                    var needed = lines * label.GetLineHeight() + (lines - 1) * label.GetThemeConstant("line_spacing");
                    Check(label.Size.Y + 1 >= needed && label.GetGlobalRect().End.Y <= card.GetGlobalRect().End.Y + 1,
                        "operational title/price fits its actual card: " + label.Name);
                }
                Check(buttons.GetGlobalRect().End.Y <= card.GetGlobalRect().End.Y + 1,
                    "operational actions stay inside their card: " + article.Id);
                foreach (var button in buttons.GetChildren().OfType<Button>()) VerifyDoctrineActionCaption(button, article.Id);
            }
            await Capture("doctrine-operational-reading-" + id + "-russian-1280");
        }
        Check(CampaignFingerprint.Of(world) == before && world.Random.State == rng,
            "inspecting all six operational trees is read-only");
    }

    private void VerifyDoctrineActionCaption(Button button, string caption)
    {
        var text = button.GetThemeFont("font").GetStringSize(button.Text, HorizontalAlignment.Left, -1,
            button.GetThemeFontSize("font_size")).X;
        var padding = button.GetThemeStylebox("normal").GetMinimumSize().X;
        var icon = button.Icon is null ? 0 : MathF.Min(button.Icon.GetWidth(), button.GetThemeConstant("icon_max_width")) + button.GetThemeConstant("h_separation");
        Check(text + padding + icon <= button.Size.X + 1,
            "article action keeps its entire caption and price visible: " + button.Name + caption);
    }
}
