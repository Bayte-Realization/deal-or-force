using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>What a headline is to the player: about them, a friend, a neighbour, an enemy, or the world.</summary>
public enum HeadlineTie { World, Neighbour, Enemy, Ally, Own }

/// <summary>
/// Which of the world's news is worth putting in front of the player as it happens.
///
/// The rail beside the map lists the news; nobody reads a list while a war is being fought. What
/// a player missed was the one line that mattered - their ally attacked, a coup next door, their
/// own region declaring independence - scrolling past among a hundred others. A headline that
/// concerns them now comes up on its own, with a line saying why it concerns them. The rest of
/// the world speaks up only when a great power is in it, and never often: the rules here are
/// the whole of what decides, and the interface only paces what they let through.
/// </summary>
public static class HeadlinePopups
{
    /// <summary>The setting that chooses how much comes up (common/settings "headline_popups").</summary>
    public const string Setting = "headline_popups";

    public static HeadlineTie TieOf(Campaign campaign, NewsFeed.Line line) => Judge(campaign, line).Tie;

    /// <summary>
    /// The tie, and the country it is through: the other party when the headline names the
    /// player, otherwise whichever of actor and target is closest to them (the actor for the world).
    /// </summary>
    public static (HeadlineTie Tie, Nation Through) Judge(Campaign campaign, NewsFeed.Line line)
    {
        var player = campaign?.Player;
        if (player is null || line is null) return (HeadlineTie.World, null);
        var actor = campaign.NationById(line.ActorId);
        var target = campaign.NationById(line.TargetId);
        if (line.ActorId == player.Id || line.TargetId == player.Id)
            return (HeadlineTie.Own, line.ActorId == player.Id ? target ?? actor : actor);
        var best = HeadlineTie.World;
        Nation through = actor ?? target;
        foreach (var id in new[] { line.ActorId, line.TargetId })
        {
            var other = campaign.NationById(id);
            if (other is null || other.Id == player.Id) continue;
            HeadlineTie tie;
            if (other.AnswersTo(player.Id) || player.AnswersTo(other.Id) ||
                CampaignDiplomacy.Peek(campaign, player.Id, other.Id) is { Stance: DiplomaticStance.Allied })
                tie = HeadlineTie.Ally;
            else if (campaign.Wars.Any(war => !war.Ended && war.Involves(player.Id) && war.Involves(other.Id) &&
                                              war.EnemiesOf(player.Id).Contains(other.Id)))
                tie = HeadlineTie.Enemy;
            else if (CampaignDiplomacy.SharesLandBorder(campaign, player.MapIndex, other.MapIndex))
                tie = HeadlineTie.Neighbour;
            else continue;
            if (tie <= best) continue;
            best = tie;
            through = other;
        }
        return (best, through);
    }

    /// <summary>Whether a headline of this importance and tie comes up under this setting.</summary>
    public static bool Worth(string mode, int importance, HeadlineTie tie, bool greatPower)
    {
        switch (mode)
        {
            case "off": return false;
            case "mine": return tie == HeadlineTie.Own && importance >= 1;
            case "all":
                return tie == HeadlineTie.Own && importance >= 1 ||
                       tie != HeadlineTie.World && importance >= 2 ||
                       importance >= 3;
            default: // "important"
                // A neighbour's quiet news (a tension eased, an exercise held) is the rail's, not a
                // card's: next door comes up for what could reach across the border.
                return tie == HeadlineTie.Own && importance >= 1 ||
                       tie is HeadlineTie.Ally or HeadlineTie.Enemy && importance >= 2 ||
                       tie == HeadlineTie.Neighbour && importance >= 3 ||
                       importance >= 3 && greatPower;
        }
    }

    /// <summary>Whether a great power is the actor or the target of a headline.</summary>
    public static bool InvolvesGreatPower(Campaign campaign, NewsFeed.Line line)
    {
        var powers = AllianceSystem.GreatPowers(campaign);
        return powers.Contains(line.ActorId) || powers.Contains(line.TargetId);
    }

    /// <summary>The line under a headline that says why it came up.</summary>
    public static string Reason(HeadlineTie tie, string otherName) => tie switch
    {
        HeadlineTie.Own => Loc.Get("Your country"),
        HeadlineTie.Ally => Loc.Format("Your ally or dependency: {0}", otherName),
        HeadlineTie.Enemy => Loc.Format("Your enemy: {0}", otherName),
        HeadlineTie.Neighbour => Loc.Format("Your neighbour: {0}", otherName),
        _ => Loc.Get("A great power")
    };
}

/// <summary>
/// The pace headlines come up at, so a busy week is a few cards rather than a wall of them.
///
/// Headlines of one kind waiting together become one ("three coups"); at most a handful wait,
/// the least important dropped first; a new card comes up no sooner than MinimumGap after the
/// last, and the world's own news (no tie to the player) no sooner than WorldGap. Times are the
/// player's real seconds, not the campaign's, so the pace is the same at every speed.
/// </summary>
public sealed class HeadlineQueue
{
    public sealed class Card
    {
        public NewsFeed.Line Line;
        public HeadlineTie Tie;
        public int Importance;
        public int Count = 1;
    }

    public const int MaxWaiting = 6;
    public const double MinimumGap = 3.5;
    public const double WorldGap = 20.0;

    private readonly List<Card> _waiting = new();
    private double _lastShown = double.NegativeInfinity;
    private double _lastWorld = double.NegativeInfinity;

    public int Waiting => _waiting.Count;

    public void Offer(NewsFeed.Line line, HeadlineTie tie, int importance)
    {
        if (line is null) return;
        var same = _waiting.Find(card => card.Line.Kind == line.Kind && card.Tie == tie);
        if (same is not null)
        {
            same.Count++;
            same.Line = line;
            same.Importance = Math.Max(same.Importance, importance);
            return;
        }
        _waiting.Add(new Card { Line = line, Tie = tie, Importance = importance });
        if (_waiting.Count <= MaxWaiting) return;
        var least = _waiting.OrderBy(card => card.Tie).ThenBy(card => card.Importance).ThenBy(card => card.Line.Sequence).First();
        _waiting.Remove(least);
    }

    /// <summary>The next card to show now, or null: the closest tie and most important first.</summary>
    public Card Next(double now, int onScreen, int maxOnScreen = 3)
    {
        if (_waiting.Count == 0 || onScreen >= maxOnScreen || now - _lastShown < MinimumGap) return null;
        foreach (var card in _waiting.OrderByDescending(card => card.Tie).ThenByDescending(card => card.Importance)
                     .ThenByDescending(card => card.Line.Sequence))
        {
            if (card.Tie == HeadlineTie.World && now - _lastWorld < WorldGap) continue;
            _waiting.Remove(card);
            _lastShown = now;
            if (card.Tie == HeadlineTie.World) _lastWorld = now;
            return card;
        }
        return null;
    }

    public void Clear() => _waiting.Clear();
}
