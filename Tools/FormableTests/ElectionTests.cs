using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class ElectionTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("elections: " + text); Console.WriteLine("PASS elections: " + text); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261019); var n = c.NationByTag("GBR");
        n.IsHuman = true; n.PoliticalPoints = 1000; n.Stability = .1f; n.WarSupport = .1f; n.ShortageIndex = 1;
        ElectoralPolitics.Tick(c);
        Check(n.Elections is not null && n.NextElectionDay > 0, "constitutional monarchy schedules an elected government");
        var crown = Rulers.HeadOfState(c, n).Name; var premier = Rulers.HeadOfGovernment(c, n).Name;
        n.NextElectionDay = 0;
        ElectoralPolitics.Tick(c);
        Check(n.Elections.Round == 1 && n.Elections.TransferOnDay >= 0 && n.Elections.IncumbentVote < .5f,
            "unpopular incumbent loses a real contest and a named handover is pending");
        Check(Rulers.HeadOfGovernment(c, n).Name == premier, "a human incumbent is not silently removed before the handover deadline");
        var fp = CampaignFingerprint.Of(c); _ = ElectoralPolitics.Approval(c, n);
        _ = ElectoralPolitics.MayAct(c, n, ElectionAction.AcceptResult, out _, out _);
        Check(CampaignFingerprint.Of(c) == fp, "electoral UI inspection does not mutate the campaign");
        var oldJson = JsonSerializer.Serialize(n.Elections, new JsonSerializerOptions { IncludeFields = true });
        var copy = JsonSerializer.Deserialize<ElectionState>(oldJson, new JsonSerializerOptions { IncludeFields = true });
        Check(copy.Winner == n.Elections.Winner && copy.TransferOnDay == n.Elections.TransferOnDay,
            "pending named winner survives field serialization");
        new ElectionCommand { IssuerId = n.Id, Action = ElectionAction.AcceptResult }.Apply(c);
        Check(Rulers.HeadOfGovernment(c, n).Name == copy.Winner && Rulers.HeadOfState(c, n).Name == crown,
            "parliamentary transfer installs the winner, preserving the monarch");
        ElectoralPolitics.Tick(c);
        Check(n.Elections.Round == 1, "a new premier does not cause overdue elections every month");
        var points = n.PoliticalPoints;
        new ElectionCommand { IssuerId = n.Id, Action = ElectionAction.Postpone }.Apply(c);
        Check(n.PoliticalPoints == points - c.Defines.ElectionPostponePoliticalCost,
            "postponement pays the shared political-point price");
        Check(!ElectoralPolitics.MayAct(c, n, ElectionAction.Postpone, out _, out _), "one term cannot be postponed repeatedly forever");
        new ElectionCommand { IssuerId = n.Id, Action = ElectionAction.Suspend }.Apply(c);
        n.NextElectionDay = 0; ElectoralPolitics.Tick(c);
        Check(n.Elections.Suspended && n.Elections.Round == 1, "cancelled calendar does not produce an election in the background");
        new ElectionCommand { IssuerId = n.Id, Action = ElectionAction.Resume }.Apply(c);
        Check(!n.Elections.Suspended && n.NextElectionDay > c.ElapsedDays, "restoring elections schedules a genuine contest");
        n.NextElectionDay = 0; n.Elections.IncumbentTerms = 8; n.Elections.Control = 0;
        ElectoralPolitics.Tick(c);
        var incumbent = Rulers.HeadOfGovernment(c, n).Name;
        Check(n.Elections.TransferOnDay >= 0, "a tired unpopular government can lose again");
        points = n.PoliticalPoints; var stability = n.Stability;
        new ElectionCommand { IssuerId = n.Id, Action = ElectionAction.RefuseResult }.Apply(c);
        Check(Rulers.HeadOfGovernment(c, n).Name == incumbent && n.Elections.TransferOnDay < 0 &&
            n.PoliticalPoints == points - c.Defines.ElectionRefusePoliticalCost && n.Stability < stability,
            "refusal retains the incumbent but pays points and damages legitimacy");
        fp = CampaignFingerprint.Of(c);
        new ElectionCommand { IssuerId = n.Id, Action = (ElectionAction)999 }.Apply(c);
        Check(fp == CampaignFingerprint.Of(c), "unknown commands do nothing");
        n.Government = "absolute_monarchy"; ElectoralPolitics.Tick(c);
        Check(n.NextElectionDay == -1 && n.Elections.Suspended && n.Elections.TransferOnDay < 0,
            "abolishing the elective constitution cannot leave a stale pending president");
        n.Government = "constitutional_monarchy"; ElectoralPolitics.Tick(c);
        Check(!n.Elections.Suspended && n.NextElectionDay > c.ElapsedDays,
            "restored elected government starts a fresh calendar, not a decades-old pending handover");
        c.SetFeature("rulers", false); fp = CampaignFingerprint.Of(c);
        new ElectionCommand { IssuerId = n.Id, Action = ElectionAction.Suspend }.Apply(c);
        ElectoralPolitics.Tick(c);
        Check(fp == CampaignFingerprint.Of(c), "feature-disabled elections neither tick nor accept commands");
        Check(!ElectoralPolitics.Eligible(c.NationByTag("SAU")), "absolute monarchy is not forced into presidential elections");
        var legacy = JsonSerializer.Deserialize<Nation>("{\"Tag\":\"OLD\"}", new JsonSerializerOptions { IncludeFields = true });
        Check(legacy.Elections is null && legacy.NextElectionDay == -1, "old-save missing fields retain safe defaults");
        var wire = new ElectionCommand { IssuerId=n.Id, Action=ElectionAction.RefuseResult };
        Check(NetworkCommandCodec.TryEncode(wire,out var type,out var json) && NetworkCommandCodec.TryDecode(type,json,out var decoded) &&
            decoded is ElectionCommand { Action:ElectionAction.RefuseResult }, "electoral action survives the existing command codec");
    }
}
