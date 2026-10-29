using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Paused buttons commit orders, not time: studying starts now, rushing lands now,
/// and a course changes now. The normal queue remains the authority for refusals and transport.</summary>
static class PausedDoctrineTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL paused doctrines: " + label);
        Console.WriteLine("PASS paused doctrines: " + label);
    }

    static bool Near(float left, float right) => Math.Abs(left - right) < .001f;

    /// <summary>The shelf a book stands on: what a country is reading there, and what it is learning.</summary>
    static DoctrineTrack Track(Nation n, DoctrineBranch branch) => n.TacticalDoctrine.Track(branch.Slot);

    static (Campaign Campaign, Nation Nation) World()
    {
        var c = Campaign.CreateBootstrap(20261023);
        var n = c.NationByTag("ROU");
        c.ConfigureSinglePlayer(n.Id);
        c.IsPaused = true;
        n.ArmyExperience = n.AirExperience = n.NavalExperience = 1000;
        n.PoliticalPoints = 1000;
        return (c, n);
    }

    // The same authoritative queue sequence the UI helper now uses; never call
    // Tick/TickDay to make a button work, or bypass SubmitInterceptor with Apply.
    static void Submit(Campaign c, ICampaignCommand command)
    {
        c.Commands.Enqueue(command);
        c.Commands.ApplyAll(c);
    }

    public static void Run()
    {
        AllTacticalBooks();
        StrategicCourses();
        RefusalsAreImmediateAndFree();
        SequentialOrdersAndTransport();
        UiDispatchUsesThePausedQueue();
    }

    static void AllTacticalBooks()
    {
        var (c, n) = World();
        foreach (var branch in TacticalDoctrineCatalog.All)
        {
            var root = branch.Nodes.First(node => node.Requires.Count == 0);
            foreach (var rush in new[] { false, true })
            {
                n.TacticalDoctrine = new TacticalDoctrineMemory();
                n.ArmyExperience = n.AirExperience = n.NavalExperience = 1000;
                n.PoliticalPoints = 1000;
                NationalModifiers.Invalidate(n);
                var time = c.ElapsedHours; var random = c.Random.State;
                var command = new StudyDoctrineCommand
                    { IssuerId = n.Id, BranchId = branch.Id, NodeId = root.Id, Rush = rush };
                Submit(c, command);
                var name = branch.Id + (rush ? " rush" : " study");
                Check(command.Refusal.Length == 0 && c.Commands.Count == 0,
                    name + " is applied before the paused UI reads its result");
                Check(Near(TacticalDoctrines.Experience(n, branch.Experience),
                    1000 - (rush ? root.RushCost : root.Cost)), name + " spends the proper service experience now");
                Check((branch.Experience == MilitaryExperience.Army || n.ArmyExperience == 1000) &&
                    (branch.Experience == MilitaryExperience.Air || n.AirExperience == 1000) &&
                    (branch.Experience == MilitaryExperience.Navy || n.NavalExperience == 1000),
                    name + " does not charge the other services");
                var track = Track(n, branch);
                Check(rush
                    ? track.Branch == branch.Id && TacticalDoctrines.Known(n, root.Id) &&
                        track.LearningNode.Length == 0
                    : track.LearningBranch == branch.Id && track.LearningNode == root.Id &&
                        track.LearningDoneDay == c.ElapsedDays + Math.Max(1, root.Days) &&
                        !TacticalDoctrines.Known(n, root.Id),
                    name + (rush ? " is in force immediately" : " starts immediately without skipping its study days"));
                Check(c.IsPaused && c.ElapsedHours == time && !c.MidStep && c.Random.State == random,
                    name + " neither unpauses nor advances time nor consumes extra randomness");
                var fingerprint = CampaignFingerprint.Of(c);
                c.Commands.ApplyAll(c);
                Check(CampaignFingerprint.Of(c) == fingerprint,
                    name + " cannot be billed twice by the next empty queue drain");
            }
        }

        var first = TacticalDoctrineCatalog.All.First();
        var replacement = TacticalDoctrineCatalog.OnShelf(first.Slot).First(branch => branch.Id != first.Id);
        var oldRoot = first.Nodes.First(node => node.Requires.Count == 0);
        var newRoot = replacement.Nodes.First(node => node.Requires.Count == 0);
        var firstTrack = n.TacticalDoctrine.Track(first.Slot);
        firstTrack.Branch = first.Id;
        firstTrack.Known.Add(oldRoot.Id);
        n.ArmyExperience = n.AirExperience = n.NavalExperience = 1000;
        var points = n.PoliticalPoints; var hours = c.ElapsedHours; var rng = c.Random.State;
        Submit(c, new StudyDoctrineCommand
            { IssuerId = n.Id, BranchId = replacement.Id, NodeId = newRoot.Id, Rush = true });
        Check(Track(n, replacement).Branch == replacement.Id &&
            Track(n, replacement).Known.SequenceEqual(new[] { newRoot.Id }), "rewriting replaces the old book while paused");
        Check(Near(n.PoliticalPoints, points - c.Defines.DoctrineRewritePoliticalCost) &&
            n.Modifiers.Any(modifier => modifier.Id == TacticalDoctrines.RetrainingModifier),
            "paused rewrite pays its normal political price and keeps retraining consequences");
        Check(c.IsPaused && c.ElapsedHours == hours && c.Random.State == rng,
            "a rewrite does not manufacture a day or a random draw");
    }

    static void StrategicCourses()
    {
        var (c, n) = World();
        foreach (var course in StrategicDoctrineCatalog.All)
        {
            n.StrategicDoctrine = new StrategicDoctrineMemory();
            var hours = c.ElapsedHours; var random = c.Random.State; var points = n.PoliticalPoints;
            var command = new AdoptStrategicDoctrineCommand { IssuerId = n.Id, DoctrineId = course.Id };
            Submit(c, command);
            Check(command.Refusal.Length == 0 && n.StrategicDoctrine.Id == course.Id && c.Commands.Count == 0,
                course.Id + " takes effect before unpausing");
            Check(n.PoliticalPoints == points && n.StrategicDoctrine.LastChangeDay == c.ElapsedDays,
                course.Id + " keeps the free first choice and the actual decision date");
            Check(c.IsPaused && c.ElapsedHours == hours && c.Random.State == random,
                course.Id + " leaves clock and randomness untouched");
        }
        var next = StrategicDoctrineCatalog.All.First(course => course.Id != n.StrategicDoctrine.Id);
        var before = n.PoliticalPoints;
        Submit(c, new AdoptStrategicDoctrineCommand { IssuerId = n.Id, DoctrineId = next.Id });
        Check(n.StrategicDoctrine.Id == next.Id && Near(n.PoliticalPoints, before - next.PoliticalCost),
            "changing an established strategic course commits and pays immediately on pause");
    }

    static void RefusalsAreImmediateAndFree()
    {
        var (c, n) = World();
        var branch = TacticalDoctrineCatalog.All.First();
        var root = branch.Nodes.First(node => node.Requires.Count == 0);
        n.ArmyExperience = n.AirExperience = n.NavalExperience = 0;
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        var study = new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branch.Id, NodeId = root.Id };
        Submit(c, study);
        Check(study.Refusal.Length > 0 && c.Commands.Count == 0 && CampaignFingerprint.Of(c) == before,
            "insufficient experience is refused now without a hidden queued purchase");
        n.StrategicDoctrine.Id = StrategicDoctrineCatalog.All.First().Id;
        n.PoliticalPoints = 0;
        var next = StrategicDoctrineCatalog.All.First(course => course.Id != n.StrategicDoctrine.Id);
        before = CampaignFingerprint.Of(c);
        var adopt = new AdoptStrategicDoctrineCommand { IssuerId = n.Id, DoctrineId = next.Id };
        Submit(c, adopt);
        Check(adopt.Refusal.Length > 0 && c.Commands.Count == 0 && CampaignFingerprint.Of(c) == before,
            "an unaffordable strategic change is refused immediately without spending anything");
        Check(c.IsPaused && c.ElapsedHours == 0 && c.Random.State == random,
            "refused buttons do not advance or randomise the paused world");
    }

    static void SequentialOrdersAndTransport()
    {
        var (c, n) = World();
        var branch = TacticalDoctrineCatalog.All.First(item => item.Nodes.Any(node => node.Requires.Count > 0));
        var root = branch.Nodes.First(node => node.Requires.Count == 0);
        var child = branch.Nodes.First(node => node.Requires.Count == 1 && node.Requires[0] == root.Id);
        c.Commands.Enqueue(new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branch.Id, NodeId = root.Id, Rush = true });
        c.Commands.Enqueue(new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branch.Id, NodeId = child.Id });
        c.Commands.ApplyAll(c);
        Check(TacticalDoctrines.Known(n, root.Id) && Track(n, branch).LearningNode == child.Id &&
            Near(TacticalDoctrines.Experience(n, branch.Experience), 1000 - root.RushCost - child.Cost),
            "paused queue keeps FIFO prerequisites and charges each order exactly once");
        Check(c.IsPaused && c.ElapsedHours == 0 && c.Commands.Count == 0,
            "consecutive doctrine commands need no simulation tick");

        (c, n) = World();
        var submitted = 0;
        c.Commands.SubmitInterceptor = _ => { submitted++; return true; };
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        Submit(c, new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branch.Id, NodeId = root.Id, Rush = true });
        Submit(c, new AdoptStrategicDoctrineCommand { IssuerId = n.Id, DoctrineId = StrategicDoctrineCatalog.All.First().Id });
        Check(submitted == 2 && c.Commands.Count == 0 && CampaignFingerprint.Of(c) == before,
            "paused actions still defer to a transport owner instead of force-applying locally");
        Check(c.IsPaused && c.ElapsedHours == 0 && c.Random.State == random,
            "transport interception does not advance or randomise the client world");
    }

    static void UiDispatchUsesThePausedQueue()
    {
        var repo = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var tree = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DoctrineTreeUI.cs"));
        var page = File.ReadAllText(Path.Combine(repo, "Scripts", "ui", "DoctrineUI.cs"));
        Check(tree.Contains("ApplyDoctrineCommand(new StudyDoctrineCommand", StringComparison.Ordinal),
            "normal and rushed tactical buttons use the immediate doctrine dispatcher");
        Check(tree.Contains("string.Equals(track?.LearningBranch, branch.Id, StringComparison.Ordinal)", StringComparison.Ordinal),
            "a first article is shown as learning even before any book is in force");
        var refresh = tree[tree.IndexOf("private void RefreshDoctrineTree(", StringComparison.Ordinal)..];
        Check(refresh.Contains("title.Text = Tr(node.Name);", StringComparison.Ordinal),
            "persistent article titles follow a language switch during the paused redraw");
        Check(page.Contains("ApplyDoctrineCommand(new AdoptStrategicDoctrineCommand", StringComparison.Ordinal),
            "strategic course buttons use the same immediate dispatcher");
        var helper = page[page.IndexOf("private void ApplyDoctrineCommand(", StringComparison.Ordinal)..];
        var queued = helper.IndexOf("_campaign.Commands.Enqueue(command);", StringComparison.Ordinal);
        var applied = helper.IndexOf("_campaign.Commands.ApplyAll(_campaign);", StringComparison.Ordinal);
        var redraw = helper.IndexOf("RedrawDoctrinePage();", StringComparison.Ordinal);
        Check(queued >= 0 && applied > queued && redraw > applied,
            "the real UI applies the authoritative queue before redrawing doctrine state");
        Check(helper.Contains("_doctrineScreenDay = -1;", StringComparison.Ordinal) &&
            helper.Contains("_armyDoctrineDay = -1;", StringComparison.Ordinal) &&
            helper.Contains("Refresh();", StringComparison.Ordinal),
            "both doctrine hosts and top-bar currencies are invalidated immediately");
    }
}
