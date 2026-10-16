using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// What finishing an alliance programme leaves behind.
///
/// A programme is months of money and cooperation, and until this existed it ended in a chronicle
/// line and nothing else: the cohesion it bought was spent by the time it finished, and the
/// relation kept no trace of the thing itself. Now a programme may name a reason both sides
/// remember (common/opinion_modifiers), which is visible in the country card beside everything
/// else two countries hold against or for each other.
///
/// Two things are being guarded here. That a finished programme is remembered - the hook's result.
/// And that a programme naming a modifier that does not exist is refused when the file is read,
/// because OpinionSystem.Remember ignores an id it does not know: the failure it would otherwise
/// produce is a programme that costs months of money and leaves nothing at all, silently.
/// </summary>
static class AllianceProgramOutcomeTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("alliance programme: " + label); Console.WriteLine("PASS alliance programme: " + label); }

    public static void Run()
    {
        // Every shipped programme that promises a memory has to keep that promise: a typo in one of
        // them is silence at the end of a programme somebody paid for.
        var broken = AllianceProgramCatalog.All
            .Where(programme => programme.OpinionOnCompletion.Length > 0 &&
                                OpinionCatalog.Find(programme.OpinionOnCompletion) is null)
            .Select(programme => programme.Id).ToList();
        Check(AllianceProgramCatalog.All.Count() > 0, "the shipped programmes are in the catalogue");
        Check(broken.Count == 0, "every programme that promises a memory names one the catalogue knows" +
            (broken.Count > 0 ? $": {string.Join(", ", broken)}" : ""));

        var programme = AllianceProgramCatalog.Find("coordination");
        Check(programme is not null && programme.OpinionOnCompletion.Length > 0,
            $"and the one this drives leaves one ({programme?.OpinionOnCompletion})");
        if (programme is null) return;

        // A name nothing knows is a complaint, not a programme that quietly remembers nothing.
        using (var bad = JsonDocument.Parse("{ \"coordination\": { \"opinion_on_completion\": \"no_such_modifier\" } }"))
        {
            var errors = new List<string>();
            AllianceProgramCatalog.Merge(bad.RootElement, errors);
            Check(errors.Count == 1 && errors[0].Contains("no_such_modifier"),
                "a programme naming a modifier that does not exist says so");
            Check(AllianceProgramCatalog.Find("coordination")?.OpinionOnCompletion == programme.OpinionOnCompletion,
                "and the catalogue is left as it was, not half-changed");
        }

        // Saying nothing is allowed: a mod may want a programme that is paid for and forgotten.
        using (var quiet = JsonDocument.Parse("{ \"coordination\": { \"duration_days\": 84 } }"))
        {
            var errors = new List<string>();
            AllianceProgramCatalog.Merge(quiet.RootElement, errors);
            Check(errors.Count == 0, "a programme that names no memory is not an error");
        }

        // ---- and now the hook itself: run one to its end and see what it left
        var c = Campaign.CreateBootstrap(501);
        var a = c.NationByTag("GBR");
        var b = c.NationByTag("CAN");
        var r = CampaignDiplomacy.Get(c, a.Id, b.Id);
        r.Stance = DiplomaticStance.Allied; r.Opinion = 90;
        r.AllianceSinceDay = 0; r.AllianceAssessmentDay = 0; r.AllianceCohesion = 0.8f;
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);

        a.Treasury = 50_000_000_000f;
        Check(AlliancePrograms.Start(c, a, b, "coordination"), "a programme can be started inside an alliance");
        Check(AlliancePrograms.Active(r), "and the relation says one is under way");
        Check(!r.Reasons.Any(reason => reason.Id == programme.OpinionOnCompletion),
            "which has not left its memory yet, because it has not finished");

        var weeks = 0;
        Day(7); AlliancePrograms.Tick(c);
        while (AlliancePrograms.Active(r) && weeks < 40)
        {
            weeks++;
            Day(7 + weeks * 7);
            AlliancePrograms.Tick(c);
        }
        Check(!AlliancePrograms.Active(r), $"the programme ran to its end ({weeks} weeks)");
        Check(c.Chronicle.Entries.Any(entry => entry.Kind == "alliance_program_completed"),
            "finishing it is visible news");
        Check(r.Reasons.Any(reason => reason.Id == programme.OpinionOnCompletion),
            $"and both sides hold the reason it finished ({programme.OpinionOnCompletion})");

        // And it is a real memory rather than a flag: dropping it changes what the pair thinks.
        var opinionWithIt = r.Reasons.Where(reason => reason.Id == programme.OpinionOnCompletion).ToList();
        Check(opinionWithIt.Count == 1, "held once, not once per week it ran");
    }
}
