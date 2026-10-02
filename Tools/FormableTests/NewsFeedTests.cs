using System;
using System.Linq;
using DealOrForce.Simulation;

static class NewsFeedTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        Console.WriteLine("PASS feed: " + message);
    }

    private static Campaign Setup()
    {
        var campaign = Campaign.CreateBootstrap(7);
        campaign.ConfigureSinglePlayer(campaign.NationByTag("KOR").Id);
        return campaign;
    }

    public static void Run()
    {
        var c = Setup();
        var kor = c.NationByTag("KOR");
        var prk = c.NationByTag("PRK");
        c.Chronicle.Record(c, "war_started", kor.Id, prk.Id);
        c.Chronicle.Record(c, "embargo_started", prk.Id, kor.Id);
        // An internal decision is a fact, but it is the country's own; the world feed must
        // not carry it even though it sits in the same buffer.
        c.Chronicle.Record(c, "decision_taken", kor.Id, -1, subject: "test decision", isPublic: false);
        var lines = NewsFeed.Lines(c, since: 0).ToArray();
        Check(lines.Length == 2, "the feed carries public events only");
        Check(lines[0].Kind == "embargo_started" && lines[1].Kind == "war_started" &&
              lines[0].Sequence > lines[1].Sequence, "newest first");
        Check(lines[1].Text.Contains("declares war", StringComparison.OrdinalIgnoreCase) &&
              lines[1].Text.Contains(kor.Name, StringComparison.OrdinalIgnoreCase),
            "a war is a sentence a stranger can read");
        var since = lines[0].Sequence;
        c.Chronicle.Record(c, "peace_signed", kor.Id, prk.Id);
        var fresh = NewsFeed.Lines(c, since: since).ToArray();
        Check(fresh.Length == 1 && fresh[0].Kind == "peace_signed", "since-sequence returns only what is new");
        // A kind with no template in any language must not blank the line: the raw kind is
        // the fallback, the way Loc.Get already behaves everywhere else.
        c.Chronicle.Record(c, "kind_nobody_declared", kor.Id, prk.Id);
        var fallback = NewsFeed.Lines(c, since: 0).First();
        Check(fallback.Kind == "kind_nobody_declared" && fallback.Text.Length > 0,
            "an unknown kind still reads as something");
    }
}
