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
        Check(NewsFeed.Lines(c, take: 1).Single().Sequence == lines[0].Sequence,
            "row limit keeps exactly the newest public line");
        Check(NewsFeed.Lines(c, take: 0).Count() == 2 && NewsFeed.Lines(c, take: -1).Count() == 2,
            "non-positive limits keep the unbounded API contract");
        Check(NewsFeed.Lines(c, take: 6).Select(l => l.Sequence).SequenceEqual(lines.Select(l => l.Sequence)),
            "repeated snapshot does not consume or duplicate news");
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
        var language = Loc.Language;
        var snapshot = c.Chronicle.Entries.First();
        try
        {
            Loc.Language = "russian";
            var translated = NewsFeed.Lines(c).First(line => line.Kind == "war_started");
            Check(translated.Text.Contains(kor.DisplayName) && translated.Text.Contains(prk.DisplayName),
                "country names follow the selected language, not the recorded English caption");
            Check(c.Chronicle.Entries.First() == snapshot,
                "translation leaves historical chronicle data unchanged");
        }
        finally { Loc.Language = language; }

        NewsFeed.ResetCatalog();
        Check(NewsFeed.Importance("world_war_began") >= NewsFeed.RailMinimum
              && NewsFeed.Importance("organization_dissolved") >= NewsFeed.RailMinimum
              && NewsFeed.Importance("political_scandal") >= NewsFeed.RailMinimum,
            "a world war, a dead alliance and a political scandal reach the map rail");
        Check(NewsFeed.Importance("aid_sent") < NewsFeed.RailMinimum,
            "routine aid stays in the reports and off the rail");
        var usa = c.NationByTag("USA");
        var chn = c.NationByTag("CHN");
        c.Chronicle.Record(c, "world_war_began", usa.Id, chn.Id, subject: "The Pacific War");
        c.Chronicle.Record(c, "aid_sent", usa.Id, chn.Id);
        var headlines = NewsFeed.Headlines(c, 8).Select(line => line.Kind).ToArray();
        Check(headlines.Contains("world_war_began") && !headlines.Contains("aid_sent"),
            "the rail shows the war between two other powers and skips the aid");
    }
}
