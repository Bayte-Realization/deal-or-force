using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyDoctrineServices(DealOrForceGame game, string language, int width)
    {
        var world = game.VerificationWorld; var fingerprint = CampaignFingerprint.Of(world); var random = world.Random.State;
        var all = new HashSet<string>(StringComparer.Ordinal);
        var list = game.FindChild("DoctrineBookList", true, false);
        var originalIds = list.FindChildren("ScreenBook_*", "Button", true, false).OfType<Button>().Select(button => button.GetInstanceId()).ToArray();
        foreach (var slot in DoctrineSlots.All)
        {
            var caption = language + width + " " + slot;
            var tab = game.FindChild("DoctrineService_" + slot, true, false) as Button;
            CheckFits(tab, "separate service tab " + caption);
            ClickPausedDoctrine(tab, "service " + caption); await Wait(.2);
            var shown = list.FindChildren("ScreenBook_*", "Button", true, false).OfType<Button>().Where(button => button.IsVisibleInTree()).ToArray();
            var expected = TacticalDoctrineCatalog.OnShelf(slot).Select(book => book.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            var actual = shown.Select(button => button.Name.ToString()[11..]).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Check(actual.SequenceEqual(expected) && game.VerificationDoctrineService == slot &&
                TacticalDoctrineCatalog.Find(game.VerificationDoctrineBook)?.Slot == slot,
                "service tab displays only its own complete book catalogue and tree: " + caption);
            foreach (var id in actual) all.Add(id);
            Check((game.FindChild("DoctrineCatalogueHeading", true, false) as Label)?.Text.Contains(expected.Length.ToString(), StringComparison.Ordinal) == true,
                "service heading shows its actual book count: " + caption);
            await Capture("doctrine-service-" + slot + "-" + language + "-" + width);
        }
        Check(all.SetEquals(TacticalDoctrineCatalog.All.Select(book => book.Id)), "four visible service groups cover every book: " + language + width);
        Check(originalIds.SequenceEqual(list.FindChildren("ScreenBook_*", "Button", true, false).OfType<Button>().Select(button => button.GetInstanceId())),
            "switching service filters retains all catalogue controls: " + language + width);
        Check(CampaignFingerprint.Of(world) == fingerprint && world.Random.State == random,
            "service navigation is read-only: " + language + width);
    }

    private async Task VerifyAdvancedDoctrineMouse(DealOrForceGame game)
    {
        Loc.Language = "russian"; GetTree().Root.Size = new(1280, 720); game.Call("RefreshLocalisedText");
        var c = game.VerificationWorld; var n = c.Player;
        var original = (n.TacticalDoctrine, n.ArmyExperience, n.AirExperience, n.NavalExperience, n.PoliticalPoints);
        var clock = c.ElapsedHours; var rng = c.Random.State;
        try
        {
            foreach (var slot in DoctrineSlots.All)
            {
                var branch = TacticalDoctrineCatalog.OnShelf(slot).First();
                var choice = branch.Nodes.First(node => node.Id.StartsWith(branch.Id + "_adv_", StringComparison.Ordinal) && node.Excludes.Count > 0);
                var follow = branch.Nodes.Single(node => node.Requires.Contains(choice.Id));
                n.TacticalDoctrine = new TacticalDoctrineMemory();
                var track = n.TacticalDoctrine.Track(slot); track.Branch = branch.Id;
                var prerequisites = new HashSet<string>(StringComparer.Ordinal);
                void Earlier(DoctrineNode node) { foreach (var id in node.Requires) if (prerequisites.Add(id)) Earlier(branch.Find(id)); }
                Earlier(choice); track.Known = prerequisites.ToList();
                n.ArmyExperience = n.AirExperience = n.NavalExperience = n.PoliticalPoints = 10000;
                NationalModifiers.Invalidate(n); game.VerificationOpenPausedDoctrines(branch.Id); await Wait(.25);
                foreach (var node in branch.Nodes.Where(node => node.Id.StartsWith(branch.Id + "_adv_", StringComparison.Ordinal)))
                {
                    var title = game.FindChild("Title_" + node.Id, true, false) as Label;
                    Check(title is not null && title.Text == Loc.Get(node.Name) && title.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming,
                        "new advanced card preserves its entire translated name: " + node.Id);
                    foreach (var prefix in new[] { "StudyArticle_", "RushArticle_" })
                        VerifyDoctrineActionCaption(game.FindChild(prefix + node.Id, true, false) as Button, node.Id);
                }
                var button = PausedDoctrineButton(game, "RushArticle_" + choice.Id);
                game.VerificationRevealDoctrineButton(game.FindChild("DoctrineNode_" + choice.Id, true, false) as Control); await Wait(.2);
                await Capture("doctrine-advanced-" + slot + "-russian-1280");
                var experience = TacticalDoctrines.Experience(n, branch.Experience);
                ClickPausedDoctrine(button, "advanced choice " + slot);
                Check(TacticalDoctrines.Known(n, choice.Id) &&
                    MathF.Abs(TacticalDoctrines.Experience(n, branch.Experience) - experience + choice.RushCost) < .02f &&
                    PausedDoctrineButton(game, "RushArticle_" + choice.Excludes[0]) is { Disabled: true },
                    "real advanced click pays once and locks the opposite specialisation: " + slot);
                button = PausedDoctrineButton(game, "RushArticle_" + follow.Id);
                game.VerificationRevealDoctrineButton(button); await Wait(.2); ClickPausedDoctrine(button, "advanced continuation " + slot);
                Check(TacticalDoctrines.Known(n, follow.Id), "real click learns the chosen continuation: " + slot);
            }
            Check(c.ElapsedHours == clock && c.Random.State == rng, "advanced mouse study preserves the paused clock and random stream");
        }
        finally
        {
            (n.TacticalDoctrine, n.ArmyExperience, n.AirExperience, n.NavalExperience, n.PoliticalPoints) = original;
            NationalModifiers.Invalidate(n);
        }
    }
}

public partial class DealOrForceGame
{
    internal string VerificationDoctrineService => _doctrineService;
    internal string VerificationDoctrineBook => _doctrineBook;
}
