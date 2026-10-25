using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A coordinated partition, from the player's chair.
///
/// The computer has always been able to do this (AiStatecraft.TryCoordinatePartition): find a
/// weak neighbour nobody outside the alliance stands behind, agree with its allies, and have
/// each of them demand, on the same day, the slice that touches its own border. A player could
/// demand one region of its own (Ultimatum, IssueUltimatumCommand) and then wait for allies to
/// notice - so the agreement itself, the thing that makes several demands a partition instead of
/// several unrelated rows, was the computer's private instrument.
///
/// The instrument is split in two rather than copied. What a slice is, how it is chosen, and what
/// makes a single demand land are the same code for both chairs: <see cref="Slice"/> and
/// <see cref="AiStatecraft.PartitionDemand"/>. What only a human can do - decide to carve up a
/// neighbour - is <see cref="ProposePartitionCommand"/>. What only a computer needs is the die
/// that stands in for that decision, and every ally a player's proposal puts to is answered on
/// that same die (AiStatecraft.PartitionAppetiteFavours). Nothing here is a new bargain: each
/// slice is still the ordinary ultimatum path, and a player victim is still never answered for.
/// </summary>
public static class PartitionCoordination
{
    /// <summary>
    /// The clock a government's partitions run on. The computer's own initiative and a player's
    /// proposal share it, so that whoever proposed the last carve-up is not free to propose
    /// another one the next morning; <see cref="Defines.AiPartitionCadenceDays"/> is that gap.
    /// </summary>
    public const string CadenceKey = "partition";

    /// <summary>
    /// The coalition the computer builds for itself: every government allied to the initiator.
    /// A movement is never one, and neither is the initiator.
    /// </summary>
    public static List<Nation> Allies(Campaign campaign, Nation initiator)
    {
        var allies = new List<Nation>();
        if (campaign is null || initiator is null) return allies;
        foreach (var other in campaign.Nations)
        {
            if (other is null || other.Id == initiator.Id || other.Movement is not null) continue;
            if (CampaignDiplomacy.Peek(campaign, initiator.Id, other.Id) is { Stance: DiplomaticStance.Allied })
                allies.Add(other);
        }
        return allies;
    }

    /// <summary>
    /// The slice one taker would demand: the most populous region of the victim that touches the
    /// taker's own ground and can still be demanded at all. The same answer the computer gives
    /// itself, which is why it lives here and not in either caller.
    /// </summary>
    public static ScenarioState Slice(Campaign campaign, Nation member, Nation victim)
    {
        if (campaign?.ProvinceMap is null || member is null || victim is null || member.Id == victim.Id)
            return null;
        ScenarioState best = null;
        var bestPopulation = 0L;
        foreach (var state in campaign.StatesOf(victim))
        {
            if (state.Population <= 0) continue;
            if (!Ultimatum.CanDemand(campaign, victim, state.MapIndex)) continue;
            if (!TerritorialCoherence.TouchesOwnedGround(campaign, member, state.MapIndex)) continue;
            if (state.Population <= bestPopulation) continue;
            bestPopulation = state.Population;
            best = state;
        }
        return best;
    }


    /// <summary>
    /// Whether one allied government would press a slice beside the initiator today, and if not,
    /// why not - the reason is shown to the player before they send anything.
    ///
    /// The conditions are the ones the computer applies to itself when it is the one proposing,
    /// minus the choice of victim: not a friend of the victim and not already fighting it, ground
    /// touching ground, and the die nobody can argue with. An ally is not more eager because a
    /// human asked, and this is not a second rule written later.
    /// </summary>
    public static bool WillJoin(Campaign campaign, Nation ally, Nation initiator, Nation victim, out string reason)
    {
        reason = "";
        if (campaign is null || ally is null || victim is null || initiator is null)
        {
            reason = Loc.Get("No such country.");
            return false;
        }
        if (ally.Movement is not null || !campaign.HasOwnedLand(ally))
        {
            reason = Loc.Get("They have no ground of their own to hold a slice with.");
            return false;
        }
        if (!CampaignDiplomacy.CanDeclareWar(campaign, ally.Id, victim.Id, out reason))
        {
            if (string.IsNullOrEmpty(reason)) reason = Loc.Get("They cannot move against them today.");
            return false;
        }
        if (!CampaignDiplomacy.SharesLandBorder(campaign, ally.MapIndex, victim.MapIndex) ||
            Slice(campaign, ally, victim) is null)
        {
            reason = Loc.Get("None of their regions touches one of theirs that can still be demanded.");
            return false;
        }
        if (!AiStatecraft.PartitionAppetiteFavours(campaign, ally))
        {
            reason = Loc.Get("They have no appetite for a carve-up this quarter.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The whole proposal: who would take a slice, or the reason there is nothing to propose.
    ///
    /// This is the gate the computer applies before it acts alone - the victim must have nobody
    /// outside the coalition standing behind them, there must be enough takers, and the coalition
    /// must overwhelmingly outweigh the victim - asked about a victim the player chose. The
    /// player's own willingness is not asked for, because the press of the button is that answer;
    /// an ally's is, on the ally's own die.
    /// </summary>
    public static bool Project(Campaign campaign, Nation initiator, Nation victim,
        out List<Nation> takers, out string refusal)
    {
        takers = new List<Nation>();
        refusal = "";
        if (campaign is null || initiator is null || victim is null || initiator.Id == victim.Id)
        {
            refusal = Loc.Get("No such country.");
            return false;
        }
        if (!campaign.Defines.AiMayCoordinatePartitions)
        {
            refusal = Loc.Get("Dividing a neighbour by agreement is not a custom of this era.");
            return false;
        }
        if (!campaign.HasOwnedLand(initiator) || !campaign.HasOwnedLand(victim))
        {
            refusal = Loc.Get("A partition needs two countries with ground of their own.");
            return false;
        }
        var bond = CampaignDiplomacy.Peek(campaign, initiator.Id, victim.Id);
        if (bond is { Stance: DiplomaticStance.Allied })
        {
            refusal = Loc.Get("They are your ally; an ally is not for dividing.");
            return false;
        }
        if (bond is { Stance: DiplomaticStance.AtWar })
        {
            refusal = Loc.Get("You are already at war with them; a war is conquest, not a partition.");
            return false;
        }
        if (!CampaignDiplomacy.CanDeclareWar(campaign, initiator.Id, victim.Id, out var warRefusal))
        {
            refusal = string.IsNullOrEmpty(warRefusal)
                ? Loc.Get("Their demands would be an ultimatum nobody could answer.")
                : warRefusal;
            return false;
        }
        var ready = initiator.DecisionAvailableDay.GetValueOrDefault(CadenceKey);
        if (ready > campaign.ElapsedDays)
        {
            refusal = Loc.Format("Another partition can be proposed in {0} day(s).", ready - campaign.ElapsedDays);
            return false;
        }

        // Nobody outside the coalition may stand behind the victim: an ally or a guarantor turns
        // a carve-up into a general war, which is not what was proposed.
        var allies = Allies(campaign, initiator);
        foreach (var other in campaign.Nations)
        {
            if (other is null || other.Id == victim.Id || other.Id == initiator.Id) continue;
            if (allies.Any(ally => ally.Id == other.Id)) continue;
            var outside = CampaignDiplomacy.Peek(campaign, other.Id, victim.Id);
            if (outside is { Stance: DiplomaticStance.Allied } ||
                (outside is not null && outside.GuaranteedBy(other.Id)))
            {
                refusal = Loc.Get("An ally or guarantor stands behind them; that would be a general war, not a partition.");
                return false;
            }
        }

        if (Slice(campaign, initiator, victim) is null)
        {
            refusal = Loc.Get("None of their regions borders your own ground and can still be demanded.");
            return false;
        }

        takers.Add(initiator);
        foreach (var ally in allies)
            if (WillJoin(campaign, ally, initiator, victim, out _)) takers.Add(ally);
        var needed = M.Max(2, campaign.Defines.AiPartitionMinimumDemanders);
        if (takers.Count < needed)
        {
            refusal = Loc.Format("A partition needs {0} governments demanding slices; {1} would join today.",
                needed, takers.Count);
            takers.Clear();
            return false;
        }

        // The computer's own strength gate, because it is about the victim and not about who is
        // asking: demands the coalition does not overwhelmingly outweigh are several rows the
        // victim can refuse one by one.
        var coalition = AiStatecraft.StrengthOf(campaign, initiator);
        foreach (var ally in allies) coalition += AiStatecraft.StrengthOf(campaign, ally);
        if (coalition < AiStatecraft.StrengthOf(campaign, victim) *
            M.Max(1f, campaign.Defines.AiPartitionStrengthMultiple))
        {
            refusal = Loc.Get("The coalition is not strong enough together to make the demands stick.");
            takers.Clear();
            return false;
        }
        return true;
    }
}



/// <summary>
/// Ask the coalition to divide a neighbour: one ultimatum per taker, on the same day, each for
/// the slice that touches its own border. The player's own demand is one of them - proposing a
/// carve-up and then not taking a slice is not a move anybody makes.
///
/// Every slice is <see cref="AiStatecraft.PartitionDemand"/>, so the victim answers through the
/// ordinary ultimatum path: a computer government by <see cref="Ultimatum.Assess"/>, a player by
/// a paper on their desk they are free to refuse. What the command adds is the same-day
/// simultaneity, and it is the only thing it adds.
/// </summary>
public sealed class ProposePartitionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int VictimId { get; init; }

    /// <summary>Whether the demands went out together. Reporting for the screen, not saved state.</summary>
    public bool Coordinated { get; private set; }

    /// <summary>How many governments actually pressed a slice.</summary>
    public int Slices { get; private set; }

    /// <summary>What to tell the player: the outcome, or why nothing was sent.</summary>
    public string Note { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Coordinated = false;
        Slices = 0;
        Note = "";
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var initiator = campaign.NationById(IssuerId);
        var victim = campaign.NationById(VictimId);
        if (initiator is null || victim is null)
        {
            Note = Loc.Get("No such country.");
            return;
        }
        if (!PartitionCoordination.Project(campaign, initiator, victim, out var takers, out var refusal))
        {
            Note = refusal;
            return;
        }

        initiator.DecisionAvailableDay[PartitionCoordination.CadenceKey] =
            campaign.ElapsedDays + M.Max(7, campaign.Defines.AiPartitionCadenceDays);
        foreach (var taker in takers)
            if (AiStatecraft.PartitionDemand(campaign, taker, victim)) Slices++;
        Coordinated = Slices >= 2;
        Note = Coordinated
            ? Loc.Format("{0} governments demanded their slices of {1}.", Slices, victim.DisplayName)
            : Loc.Format("Only {0} government(s) pressed a slice; that is a demand, not a partition.", Slices);
        campaign.MarkStrategicChange();
    }
}
