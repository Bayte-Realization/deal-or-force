using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>The existing secession event, also used by armed movements. The response card has
/// a deadline; the parent's ability to fight its direct breakaway does not.</summary>
public static class SecessionResponse
{
    internal const string Prefix = "secession_response/";
    static string Key(Nation rebel) => Prefix + rebel.Id;

    internal static void Begin(Campaign c, Nation parent, Nation rebel)
    {
        rebel.SecededFromId = parent.Id;
        rebel.LastSecessionDay = c.ElapsedDays;
        parent.DecisionAvailableDay[Key(rebel)] = c.ElapsedDays + Math.Max(1, c.Defines.SecessionResponseDays);
    }

    internal static bool CanContest(Campaign c, Nation parent, Nation rebel) =>
        parent is not null && rebel is not null && rebel.SecededFromId == parent.Id &&
        parent.DecisionAvailableDay.GetValueOrDefault(Key(rebel), -1) > c.ElapsedDays;

    /// <summary>
    /// Whether this state is the one the other broke away from - with no deadline attached.
    ///
    /// The window above decides how long the cabinet is *asked*; it does not decide whether the
    /// question exists. A country that has already taken its own ground back off a breakaway
    /// once must be able to do it again, and the thing that stopped that was the peace treaty
    /// signed while the rebels were still standing on it: a five-year truce that the state did
    /// not choose to give and could not outlast. A treaty is a bargain about what two countries
    /// will do to each other; it is not a bargain about whose ground this is.
    ///
    /// The asymmetry is deliberate and is the same one <see cref="Reintegration"/> already
    /// makes at a peace conference: the breakaway stays bound by the truce it signed, and the
    /// state it left never stops being the state it left. Acknowledging the new administration
    /// closes the immediate question, not the underlying territorial relationship.
    /// </summary>
    internal static bool Reclaiming(Nation attacker, Nation defender) =>
        attacker is not null && defender is not null && defender.SecededFromId == attacker.Id;


    internal static PendingCampaignEvent Ask(Campaign c, Nation parent, Nation rebel, string names, long people, long was)
    {
        var existing = c.PendingEvents.FirstOrDefault(e => e.NationId == parent.Id &&
            e.Options.Any(o => o.SecessionTargetId == rebel.Id));
        if (existing is not null) return existing;
        var item = new PendingCampaignEvent
        {
            Id = c.PendingEvents.Count == 0 ? 1 : c.PendingEvents.Max(e => e.Id) + 1,
            NationId = parent.Id,
            Title = Loc.Format("{0} has declared independence", rebel.DisplayName),
            Description = Loc.Format("{0} has proclaimed itself out of {1}, taking {2} and {3} people - {4} of the country. Militia are already under arms there. The cabinet is waiting.",
                rebel.DisplayName, parent.DisplayName, names, people.ToString("N0"), (was <= 0 ? 0 : people / (float)was).ToString("P0")),
            CreatedDay = c.ElapsedDays,
            ExpiresDay = c.ElapsedDays + Math.Max(1, c.Defines.SecessionResponseDays),
            Options = new[]
            {
                new CampaignEventOption { Title = "Recognise the new border", SecessionTargetId = rebel.Id, SecessionChoice = "recognise",
                    Consequence = "Accept independence without starting a war now. Reunification remains possible later.",
                    Effect = ScriptedEffects.From(("stability", .03f), ("war_support", -.05f)) },
                new CampaignEventOption { Title = "Refuse to recognise it", SecessionTargetId = rebel.Id, SecessionChoice = "dispute",
                    Consequence = "Keep the claim without starting a war now. Reunification remains possible later.",
                    Effect = ScriptedEffects.From(("stability", -.02f), ("war_support", .04f)) },
                new CampaignEventOption { Title = "Send the army", SecessionTargetId = rebel.Id, SecessionChoice = "fight",
                    Consequence = "Fight to reintegrate the breakaway. A previous truce cannot shield a new armed rebellion.",
                    Effect = ScriptedEffects.From(("war_support", .08f), ("stability", -.05f)) }
            }
        };
        c.PendingEvents.Add(item);
        return item;
    }

    internal static bool Resolve(Campaign c, Nation parent, CampaignEventOption option)
    {
        var rebel = c.NationById(option.SecessionTargetId);
        // A stale card cannot declare war on a later unrelated state or on a country already reintegrated.
        if (parent is null || rebel is null || !c.HasOwnedLand(rebel) || !Reintegration.IsBreakawayOf(rebel, parent)) return true;
        if (option.SecessionChoice == "fight")
        {
            if (!CampaignDiplomacy.AtWar(c, parent.Id, rebel.Id) &&
                CampaignDiplomacy.DeclareWar(c, parent.Id, rebel.Id, justification: "secession", onOwnInitiative: false) is null)
                return false; // Never consume a blocked choice and pretend the army was sent.
            parent.DecisionAvailableDay.Remove(Key(rebel));
        }
        else if (option.SecessionChoice == "recognise")
        {
            parent.DecisionAvailableDay.Remove(Key(rebel));
            OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, parent.Id, rebel.Id), "let_us_go");
        }
        return true;
    }
}
