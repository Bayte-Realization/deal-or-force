using System;
using System.Linq;
using DealOrForce.Simulation;

static class ForeignAssistancePresentationTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS assistance presentation: " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261014);
        var donor = c.NationByTag("GBR"); var requester = c.NationByTag("UKR");
        c.ConfigureSinglePlayer(donor.Id); requester.Treasury = .001f; requester.WeeklyBalance = -1f;
        requester.Population = donor.Population = 1_000_000;
        requester.Gdp = donor.Gdp = 100; donor.Treasury = 40;
        var result = ForeignAssistance.Submit(c, requester.Id, donor.Id, .05f);
        Check(result.RequestId > 0 && result.State == ForeignAssistanceState.Pending, "a real request is pending for the human donor: " + result.Reason);
        var before = CampaignFingerprint.Of(c);
        var letter = c.AssistanceRequests.Single(r => r.Id == result.RequestId);
        var item = SituationReport.Read(c, donor).FirstOrDefault(i => i.Id == "assistance:" + letter.Id);
        Check(item is not null, "the situation room exposes the unanswered financial request");
        Check(item.Route.ToString() == "Assistance" && item.TargetId == letter.Id && item.DeadlineDay == letter.ExpiresDay,
            "its action opens the correct assistance letter, not an unrelated deal with the same numeric ID");
        Check(item.Facts.Length > 0 && item.Consequences.Length > 0, "the letter explains cash and explicit consent");
        Check(CampaignFingerprint.Of(c) == before, "reading the explanation does not spend, answer or initialize relations");
        Check(!SituationReport.Read(c, donor, observer: true).Any(i => i.Id.StartsWith("assistance:")), "an observer is not prompted to sign for the donor");
    }
}
