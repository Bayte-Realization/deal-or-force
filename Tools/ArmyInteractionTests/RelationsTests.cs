using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// What the world already thought of itself on the first day (history/diplomacy `rivalries`
/// and `friendships`), and what that does to who will sign an alliance with whom.
/// </summary>
static class RelationsTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL relations: " + name);
        Console.WriteLine("PASS relations: " + name);
    }

    public static void Run(Campaign c)
    {
        DiplomaticRelation Between(string one, string other) =>
            CampaignDiplomacy.Get(c, c.NationByTag(one).Id, c.NationByTag(other).Id);

        var kashmir = Between("IND", "PAK");
        Check(kashmir.Opinion <= c.Defines.AiRivalryThreshold && kashmir.Stance == DiplomaticStance.Rival,
            $"India and Pakistan open as rivals ({kashmir.Opinion:0})");
        var shadow = Between("ISR", "IRN");
        Check(shadow.Opinion <= -60f && shadow.Stance == DiplomaticStance.Rival, $"so do Israel and Iran ({shadow.Opinion:0})");
        var allWeather = Between("CHN", "PAK");
        Check(allWeather.Opinion >= c.Defines.AiFriendshipThreshold && allWeather.Stance == DiplomaticStance.Friendly,
            $"China and Pakistan open as friends ({allWeather.Opinion:0})");
        var nato = Between("USA", "GBR");
        // NATO is an organization now (InternationalOrganizations): its members defend each
        // other through it rather than through forty bilateral alliances.
        var natoBound = nato.Stance == DiplomaticStance.Allied ||
            InternationalOrganizations.DefencePartners(c, c.NationByTag("USA").Id).Contains(c.NationByTag("GBR").Id);
        Check(natoBound && nato.Opinion > Between("USA", "FRA").Opinion,
            "an old friendship inside an alliance leaves the alliance as it was and warms it");
        var war = Between("RUS", "UKR");
        Check(war.Stance == DiplomaticStance.AtWar, "a rivalry between countries at war leaves them at war");
        Check(kashmir.OpinionDrivers.Entries.Any(entry => entry.Source.Contains("enmity")),
            "the reason is named where the player reads an opinion");

        // And the alliance that two rivals would never sign is not signed.
        var india = c.NationByTag("IND");
        var pakistan = c.NationByTag("PAK");
        Check(AllianceSystem.Willingness(c, india, pakistan) < c.Defines.AllianceWillingness,
            "India will not sign an alliance with Pakistan");

        // A row the loader cannot place is reported, not guessed at.
        var errors = new List<string>();
        HistoryDataLoader.ApplyDiplomacy(c, "{ \"rivalries\": [ { \"between\": [\"IND\", \"XXZ\"] } ] }", errors);
        Check(errors.Count == 1 && errors[0].Contains("XXZ"), "an unknown country in a rivalry is reported");

        // A mod can scale one: the row's opinion, not the modifier's.
        var errorsToo = new List<string>();
        var before = Between("ISL", "NZL").Opinion;
        HistoryDataLoader.ApplyDiplomacy(c, "{ \"friendships\": [ { \"between\": [\"ISL\", \"NZL\"], \"opinion\": 60 } ] }", errorsToo);
        var after = Between("ISL", "NZL").Opinion;
        Check(errorsToo.Count == 0 && after - before >= 59f, $"a row's own opinion is the one remembered ({before:0} to {after:0})");
    }
}
