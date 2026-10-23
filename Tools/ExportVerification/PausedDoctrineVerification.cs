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
            study.EmitSignal(BaseButton.SignalName.Pressed);
            // No timer or process frame occurs between pressing and verifying.
            Check(n.TacticalDoctrine.LearningBranch == branch.Id && n.TacticalDoctrine.LearningNode == root.Id &&
                n.TacticalDoctrine.LearningDoneDay == c.ElapsedDays + root.Days && c.Commands.Count == 0,
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
            var rush = PausedDoctrineButton(game, "RushArticle_" + root.Id);
            Check(rush is { Disabled: false }, "real crash-course button is available on pause: " + label);
            if (rush is null || rush.Disabled) continue;
            rush.EmitSignal(BaseButton.SignalName.Pressed);
            Check(n.TacticalDoctrine.Branch == branch.Id && TacticalDoctrines.Known(n, root.Id) &&
                n.TacticalDoctrine.LearningNode.Length == 0 && c.Commands.Count == 0,
                "actual crash-course button puts the article in force synchronously: " + label);
            Check(Math.Abs(n.NavalExperience - (1000 - root.RushCost)) < .001f &&
                (game.FindChild("State_" + root.Id, true, false) as Label)?.Text == Loc.Get("in force"),
                "actual crash course pays the quoted price and repaints the card immediately: " + label);
            Check(c.IsPaused && c.ElapsedHours == hours && c.Random.State == random,
                "actual crash course leaves pause, clock and RNG intact: " + label);

            game.VerificationOpenPausedDoctrines(branch.Id, course: true);
            var adopt = PausedDoctrineButton(game, "AdoptStrategy_" + course.Id);
            Check(adopt is { Disabled: false }, "real strategic-course button is available on pause: " + label);
            if (adopt is null || adopt.Disabled) continue;
            adopt.EmitSignal(BaseButton.SignalName.Pressed);
            var inForce = PausedDoctrineButton(game, "AdoptStrategy_" + course.Id);
            Check(n.StrategicDoctrine.Id == course.Id && n.PoliticalPoints == 1000 &&
                inForce is { Disabled: true } && inForce.Text == Loc.Get("in force") && c.Commands.Count == 0,
                "actual strategic button immediately adopts its free first course and shows in force: " + label);
            var change = PausedDoctrineButton(game, "AdoptStrategy_" + alternative.Id);
            Check(change is { Disabled: false }, "real paid strategic-change button is available on pause: " + label);
            if (change is null || change.Disabled) continue;
            change.EmitSignal(BaseButton.SignalName.Pressed);
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
}
