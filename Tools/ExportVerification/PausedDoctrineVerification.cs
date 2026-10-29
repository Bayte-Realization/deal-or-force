using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private static Button PausedDoctrineButton(DealOrForceGame game, string name) =>
        game.FindChildren(name, "Button", true, false).OfType<Button>()
            .FirstOrDefault(button => button.IsVisibleInTree());

    private void ClickPausedDoctrine(Button button, string label)
    {
        // Exercise Godot's real hit test and press/release route, not just a signal.
        var point = button.GetGlobalTransformWithCanvas() * (button.Size * .5f);
        GetViewport().PushInput(new InputEventMouseMotion { Position = point }, true);
        Check(GetViewport().GuiGetHoveredControl() == button, "doctrine mouse hit reaches its button: " + label);
        GetViewport().PushInput(new InputEventMouseButton
            { Position = point, ButtonIndex = MouseButton.Left, ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton
            { Position = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
    }

    // Explicit development-only fixture: a new in-memory campaign, no player save reads/writes.
    // Each post-click check is synchronous, before any await can hide a deferred command.
    private async Task VerifyPausedDoctrines(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var n = c.NationByTag("ROU");
        Check(n is not null, "paused doctrine fixture has an actual country");
        if (n is null) return;
        c.ConfigureSinglePlayer(n.Id);
        c.IsPaused = true;
        var branch = TacticalDoctrineCatalog.Find("coast");
        var root = branch?.Nodes.FirstOrDefault(node => node.Requires.Count == 0);
        var course = StrategicDoctrineCatalog.Find("sea_power");
        var alternative = StrategicDoctrineCatalog.Find("manoeuvre_war");
        Check(branch is not null && root is not null && course is not null && alternative is not null,
            "paused doctrine fixture uses shipped naval articles and strategic courses");
        if (branch is null || root is null || course is null || alternative is null) return;

        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language;
            GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            var label = language + " " + size.X;
            n.TacticalDoctrine = new TacticalDoctrineMemory();
            n.StrategicDoctrine = new StrategicDoctrineMemory();
            n.ArmyExperience = n.AirExperience = n.NavalExperience = 1000;
            n.PoliticalPoints = 1000;
            NationalModifiers.Invalidate(n);
            game.VerificationOpenPausedDoctrines(branch.Id);
            await Wait(.25);

            var study = PausedDoctrineButton(game, "StudyArticle_" + root.Id);
            Check(study is { Disabled: false } && study.IsVisibleInTree(),
                "real naval study button is available while paused: " + label);
            if (study is null || study.Disabled) continue;
            var hours = c.ElapsedHours; var random = c.Random.State;
            ClickPausedDoctrine(study, label + " study");
            // No timer or process frame occurs between pressing and verifying.
            Check(n.TacticalDoctrine.Track(branch.Slot).LearningBranch == branch.Id && n.TacticalDoctrine.Track(branch.Slot).LearningNode == root.Id &&
                n.TacticalDoctrine.Track(branch.Slot).LearningDoneDay == c.ElapsedDays + root.Days && c.Commands.Count == 0,
                "actual study button starts its article before the next frame: " + label);
            Check(Math.Abs(n.NavalExperience - (1000 - root.Cost)) < .001f &&
                n.ArmyExperience == 1000 && n.AirExperience == 1000,
                "actual study button immediately charges naval experience only: " + label);
            var state = game.FindChild("State_" + root.Id, true, false) as Label;
            var afterStudy = PausedDoctrineButton(game, "StudyArticle_" + root.Id);
            Check(state?.Text == Loc.Format("being learned · {0} days left", root.Days) &&
                afterStudy is { Disabled: true } && afterStudy.Text == Loc.Get("learning"),
                "first article immediately displays learning rather than not started: " + label);
            Check((game.FindChild("Title_" + root.Id, true, false) as Label)?.Text == Loc.Get(root.Name),
                "persistent article title follows the active language: " + label);
            Check(c.IsPaused && c.ElapsedHours == hours && c.Random.State == random,
                "actual study button neither advances time nor consumes randomness: " + label);
            await Wait(.4);
            Check(n.TacticalDoctrine.Track(branch.Slot).LearningNode == root.Id &&
                (game.FindChild("State_" + root.Id, true, false) as Label)?.Text ==
                    Loc.Format("being learned · {0} days left", root.Days) && c.IsPaused && c.ElapsedHours == hours,
                "learning survives subsequent paused frames without a manual redraw: " + label);
            var before = CampaignFingerprint.Of(c);
            game.VerificationRefreshPausedDoctrines();
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == random,
                "redrawing a pending doctrine does not mutate the campaign: " + label);
            await Capture("paused-doctrine-study-" + language + "-" + size.X);

            // Reset only this opted-in test fixture, so the actual rush button can be tested too.
            n.TacticalDoctrine = new TacticalDoctrineMemory();
            n.ArmyExperience = n.AirExperience = n.NavalExperience = 1000;
            NationalModifiers.Invalidate(n);
            game.VerificationOpenPausedDoctrines(branch.Id);
            await Wait(.2);
            var rush = PausedDoctrineButton(game, "RushArticle_" + root.Id);
            Check(rush is { Disabled: false }, "real crash-course button is available on pause: " + label);
            if (rush is null || rush.Disabled) continue;
            ClickPausedDoctrine(rush, label + " rush");
            Check(n.TacticalDoctrine.Track(branch.Slot).Branch == branch.Id && TacticalDoctrines.Known(n, root.Id) &&
                n.TacticalDoctrine.Track(branch.Slot).LearningNode.Length == 0 && c.Commands.Count == 0,
                "actual crash-course button puts the article in force synchronously: " + label);
            Check(Math.Abs(n.NavalExperience - (1000 - root.RushCost)) < .001f &&
                (game.FindChild("State_" + root.Id, true, false) as Label)?.Text == Loc.Get("in force"),
                "actual crash course pays the quoted price and repaints the card immediately: " + label);
            Check(c.IsPaused && c.ElapsedHours == hours && c.Random.State == random,
                "actual crash course leaves pause, clock and RNG intact: " + label);

            game.VerificationOpenPausedDoctrines(branch.Id, course: true);
            await Wait(.25);
            var adopt = PausedDoctrineButton(game, "AdoptStrategy_" + course.Id);
            Check(adopt is { Disabled: false }, "real strategic-course button is available on pause: " + label);
            if (adopt is null || adopt.Disabled) continue;
            game.VerificationRevealDoctrineButton(adopt);
            await Wait(.2);
            ClickPausedDoctrine(adopt, label + " strategic course");
            var inForce = PausedDoctrineButton(game, "AdoptStrategy_" + course.Id);
            Check(n.StrategicDoctrine.Id == course.Id && n.PoliticalPoints == 1000 &&
                inForce is { Disabled: true } && inForce.Text == Loc.Get("in force") && c.Commands.Count == 0,
                "actual strategic button immediately adopts its free first course and shows in force: " + label);
            await Wait(.25);
            var change = PausedDoctrineButton(game, "AdoptStrategy_" + alternative.Id);
            Check(change is { Disabled: false }, "real paid strategic-change button is available on pause: " + label);
            if (change is null || change.Disabled) continue;
            // The second course can be below the visible scroll area.
            game.VerificationRevealDoctrineButton(change);
            await Wait(.2);
            ClickPausedDoctrine(change, label + " paid strategic change");
            Check(n.StrategicDoctrine.Id == alternative.Id &&
                Math.Abs(n.PoliticalPoints - (1000 - alternative.PoliticalCost)) < .001f && c.Commands.Count == 0,
                "actual strategic-change button pays and changes course without waiting: " + label);
            Check(c.IsPaused && c.ElapsedHours == hours && c.Random.State == random,
                "all real doctrine buttons kept the same paused hour and random stream: " + label);
            before = CampaignFingerprint.Of(c);
            game.VerificationRefreshPausedDoctrines();
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == random,
                "redrawing an adopted strategic course is read-only: " + label);
            await Capture("paused-doctrine-strategy-" + language + "-" + size.X);
        }
        await VerifyPausedDoctrineBindings(game, n);
    }

    private async Task VerifyPausedDoctrineBindings(DealOrForceGame game, Nation original)
    {
        var c = game.VerificationWorld;
        Loc.Language = "english";
        GetTree().Root.Size = new Vector2I(1920, 1080);
        foreach (var branch in TacticalDoctrineCatalog.All)
        {
            var root = branch.Nodes.First(node => node.Requires.Count == 0);
            original.TacticalDoctrine = new TacticalDoctrineMemory();
            original.ArmyExperience = original.AirExperience = original.NavalExperience = 1000;
            game.VerificationOpenPausedDoctrines(branch.Id);
            await Wait(.2);
            var study = PausedDoctrineButton(game, "StudyArticle_" + root.Id);
            ClickPausedDoctrine(study, branch.Id);
            Check(original.TacticalDoctrine.LearningNode == root.Id && c.Commands.Count == 0 && c.IsPaused,
                "real mouse starts the first article of " + branch.Id + " on pause");
        }

        var book = TacticalDoctrineCatalog.Find("coast");
        var article = book.Nodes.First(node => node.Requires.Count == 0);
        game.VerificationOpenPausedDoctrines(book.Id);
        await Wait(.2); // Retain a card explicitly created for the original country.
        var next = c.NationByTag("POL");
        next.TacticalDoctrine = new TacticalDoctrineMemory();
        next.ArmyExperience = next.AirExperience = next.NavalExperience = 1000;
        c.ConfigureSinglePlayer(next.Id);
        game.VerificationOpenPausedDoctrines(book.Id);
        await Wait(.2);
        ClickPausedDoctrine(PausedDoctrineButton(game, "StudyArticle_" + article.Id), "after changing player country");
        Check(next.TacticalDoctrine.Track(book.Slot).LearningNode == article.Id && next.NavalExperience == 1000 - article.Cost,
            "retained doctrine cards submit for the current country, not their original country");

        next.TacticalDoctrine = new TacticalDoctrineMemory();
        next.NavalExperience = 1000;
        game.VerificationOpenPausedDoctrines(book.Id);
        await Wait(.2);
        c.Commands.Enqueue(new StudyDoctrineCommand { IssuerId = next.Id, BranchId = book.Id, NodeId = article.Id });
        c.Commands.ApplyAll(c);
        game.Call("Refresh"); // Same refresh used for an authoritative command arriving while paused.
        Check((game.FindChild("State_" + article.Id, true, false) as Label)?.Text ==
            Loc.Format("being learned · {0} days left", article.Days),
            "same-day authoritative doctrine changes invalidate the fullscreen presentation");

        next.StrategicDoctrine = new StrategicDoctrineMemory();
        next.PoliticalPoints = 1000;
        game.VerificationOpenArmyDoctrines();
        await Wait(.25);
        var course = StrategicDoctrineCatalog.Find("sea_power");
        var adopt = PausedDoctrineButton(game, "AdoptStrategy_" + course.Id);
        game.VerificationRevealDoctrineButton(adopt);
        await Wait(.2);
        ClickPausedDoctrine(adopt, "Army panel strategy");
        Check(next.StrategicDoctrine.Id == course.Id && c.IsPaused && c.Commands.Count == 0,
            "Army panel strategic course also commits on pause");
        game.Call("RefreshArmyPanel"); // Prime today's cache before the external state change.
        next.StrategicDoctrine = new StrategicDoctrineMemory();
        game.Call("RefreshArmyPanel");
        Check(PausedDoctrineButton(game, "AdoptStrategy_" + course.Id) is { Disabled: false },
            "Army doctrine tab observes an authoritative same-day change");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenPausedDoctrines(string book, bool course = false)
    {
        // UI fixture setup only; never toggle observer mode, which can start the clock.
        _observerMode = false;
        _campaign.ObserverMode = false;
        _campaign.IsPaused = true;
        OpenDoctrineScreen();
        _doctrineBook = book;
        SetDoctrineView(course ? "course" : "tactical");
    }

    internal void VerificationRefreshPausedDoctrines() => RedrawDoctrinePage();

    internal void VerificationOpenArmyDoctrines()
    {
        CloseDoctrineScreen();
        OpenArmyDoctrinePage();
    }

    internal void VerificationRevealDoctrineButton(Button button)
    {
        for (var parent = button?.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll)
            {
                scroll.EnsureControlVisible(button);
                break; // An outer scroller cannot directly reveal a clipped inner descendant.
            }
    }
}
