using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Ground a country lost, and the question it keeps asking about it (item 103): a region that
/// seceded from a played country and was let go is still that country's lost homeland, the AI
/// weighs it, and the player is asked - take it back (a war over that region, as a
/// reconquest), not yet, or let it go for good.
/// </summary>
static class LostLandTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL lost land: " + name);
        Console.WriteLine("PASS lost land: " + name);
    }

    public static void Run(Campaign c)
    {
        var spain = c.NationByTag("ESP");
        foreach (var nation in c.Nations) nation.IsHuman = false;
        spain.IsHuman = true;
        MovementFall.RememberHomelands(c);
        Check(c.Homelands.TryGetValue(spain.Id, out var homeland) && homeland.Count > 1, "Spain's homeland is the ground it opens with");
        Check(LostLands.Of(c, spain).Count == 0, "and on the first day none of it is lost");

        // A region goes, and the cabinet lets it go.
        var region = c.StatesOf(spain).OrderBy(state => state.Population).First();
        var report = SecessionEvents.Force(c, region);
        Check(report?.Seceded is not null, $"{region.Name} secedes as {report?.Seceded?.Tag}");
        var born = report.Seceded;
        var asked = c.PendingEvents.FirstOrDefault(item => item.NationId == spain.Id);
        Check(asked is not null, "the player is asked about the secession, as before");
        EventSystem.Resolve(c, asked, 0);
        Check(!CampaignDiplomacy.AtWar(c, spain.Id, born.Id), "and recognising the border starts no war");

        var lost = LostLands.Of(c, spain);
        Check(lost.Count == 1 && lost[0].Region.MapIndex == region.MapIndex && lost[0].Holder.Id == born.Id,
            $"{region.Name} is Spain's lost ground now, held by {born.Tag}");
        Check(LostLands.Weight(c, spain, born) > 0f && LostLands.Weight(c, born, spain) == 0f,
            "the AI weighs it as a reason against the holder, and only that way round");

        // Asked, and answered: take it back.
        CampaignDiplomacy.Get(c, spain.Id, born.Id).TruceUntil = 0;
        var question = LostLands.Ask(c, spain);
        Check(question is not null && question.Options.Length == 3, "the player is asked what to do about it");
        Check(question.Options[0].DeclareWarOn == born.Id && question.Options[0].WarGoalStateMapIndex == region.MapIndex,
            "taking it back is a war on the holder over that region");
        EventSystem.Resolve(c, question, 0);
        var war = c.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Opposes(spain.Id, born.Id));
        Check(war is not null && war.ClaimedStateMapIndex == region.MapIndex,
            $"the war is declared over {region.Name}");
        Check(spain.LastWarJustification == "reconquest", $"as a reconquest, not an unprovoked war ({spain.LastWarJustification})");

        // Asked again, and answered: let it go.
        c.Wars.Remove(war);
        CampaignDiplomacy.Get(c, spain.Id, born.Id).TruceUntil = 0;
        var again = LostLands.Ask(c, spain);
        Check(again is not null, "with the war gone, the question can come back");
        EventSystem.Resolve(c, again, 2);
        Check(!c.Homelands[spain.Id].Contains(region.MapIndex) && LostLands.Of(c, spain).Count == 0 &&
              LostLands.Weight(c, spain, born) == 0f,
            "letting it go strikes it off the homeland, and nobody weighs it any more");

        // An event file says the same with a tag and a region id - and nation 0, Indonesia, is
        // somebody an event can mean.
        var idn = c.NationByTag("IDN");
        var mys = c.NationByTag("MYS");
        var target = c.StatesOf(idn).OrderBy(state => state.Population).First();
        CampaignDiplomacy.Get(c, mys.Id, idn.Id).TruceUntil = 0;
        var written = new PendingCampaignEvent
        {
            Id = 987654, NationId = mys.Id, Title = "test", Description = "test",
            CreatedDay = c.ElapsedDays, ExpiresDay = c.ElapsedDays + 30,
            Options = new[] { new CampaignEventOption { Title = "war", DeclareWarOnTag = "IDN", WarGoalRegion = target.Id } }
        };
        c.PendingEvents.Add(written);
        EventSystem.Resolve(c, written, 0);
        var fileWar = c.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Opposes(mys.Id, idn.Id));
        Check(idn.Id == 0 && fileWar is not null && fileWar.ClaimedStateMapIndex == target.MapIndex,
            $"an event file's declare_war_on \"IDN\" and war_goal \"{target.Id}\" start that war over that region");
        c.Wars.Remove(fileWar);

        // Never twice inside the cooldown, and never for a world nobody plays.
        var pending = c.PendingEvents.Count;
        spain.LostLandAskedDay = c.ElapsedDays;
        for (var day = 0; day < c.Defines.LostLandCheckDays; day++)
        {
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)(c.ElapsedDays + 1) * 24);
            LostLands.Tick(c);
        }
        Check(c.PendingEvents.Count == pending, "inside the cooldown the player is not asked again");
    }
}
