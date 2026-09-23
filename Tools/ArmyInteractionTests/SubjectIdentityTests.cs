using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Who answers to whom, for the one country whose id is the number that used to mean
/// "nobody". The first country in the table has id 0 - Indonesia - and while "no overlord"
/// was an id of zero, every free country in the world counted as its subject.
/// </summary>
static class SubjectIdentityTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260924);
        c.AttachProvinceTopology(map);
        var first = c.Nations[0];
        Check(first.Id == 0, $"the first country in the table has id 0 ({first.Tag})");
        Check(SubjectSystem.SubjectsOf(c, first).Count == 0,
            $"{first.Tag} answers for nobody on the first day, not for every free country");
        var free = c.Nations.Where(n => !n.IsSubject).ToList();
        Check(free.All(n => SubjectSystem.OverlordOf(c, n) is null && !n.AnswersTo(first.Id)),
            $"none of the {free.Count} free countries has an overlord");

        var small = c.Nations.Where(n => n.Id != first.Id && !n.IsSubject && n.Population > 0 &&
                                         c.StatesOf(n).Length > 0)
            .OrderBy(n => n.Population).First();
        Check(SubjectSystem.Subjugate(c, first, small, "protectorate", out var refusal), "subjugate: " + refusal);
        Check(SubjectSystem.SubjectsOf(c, first).Select(n => n.Id).SequenceEqual(new[] { small.Id }),
            $"{first.Tag} can hold a subject ({small.Tag}), and then it has exactly one");
        Check(SubjectSystem.OverlordOf(c, small) == first && small.AnswersTo(first.Id), $"{small.Tag} knows it");
        Check(!SubjectSystem.Subjugate(c, c.Nations[1], small, "puppet", out _),
            "a country that answers to id 0 is not free to be taken by anybody else");

        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "subject-tests"), "subject-tests");
        var back = restored.Campaign?.NationById(small.Id);
        Check(restored.Success && back is not null && back.AnswersTo(first.Id) &&
              SubjectSystem.SubjectsOf(restored.Campaign, restored.Campaign.NationById(first.Id)).Count == 1,
            "and a save keeps it that way");

        SubjectSystem.Release(c, small);
        Check(!small.IsSubject && SubjectSystem.OverlordOf(c, small) is null && SubjectSystem.SubjectsOf(c, first).Count == 0,
            "released, it answers to nobody again");
    }
}
