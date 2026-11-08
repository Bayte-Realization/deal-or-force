using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Share of its own ground a country must have lost in a war before it asks foreign
    /// powers to come into it. 0 asks from the first week; above 1 never asks.</summary>
    public float AiRescueLossShare { get; set; } = 0.05f;
    /// <summary>How much stronger the other side must look before a country asks for help without
    /// having lost ground yet.</summary>
    public float AiRescueStrengthFear { get; set; } = 1.5f;
}

public static partial class AiStatecraft
{
    /// <summary>
    /// A country losing a war asks somebody to come into it.
    ///
    /// Allies were called once, on the day the war began, and nobody was asked again: a
    /// country could lose half its provinces over two years while an ally that had been busy
    /// on the first day, a guarantor signed since, and the neighbour the invader was going to
    /// visit next all watched. The player had the plea on the war screen; a government run by
    /// the computer could only send it to a player. It now asks every government, by the same
    /// plea and the same answer (<see cref="WarInterventions"/>): one plea per round, to
    /// whoever is likeliest to say yes, and nobody asked twice inside the plea's cooldown.
    /// A player is still asked by the plea's own review and answers in the inbox.
    /// Returns whether a plea was sent.
    /// </summary>
    internal static bool SeekRescue(Campaign campaign, Nation nation, StrategicWar war)
    {
        if (campaign is null || nation is null || war is null || war.Ended || nation.IsHuman) return false;
        if (!WarInterventions.Enabled(campaign)) return false;
        if (!InTrouble(campaign, nation, war)) return false;
        Count("rescue_considered");

        var nuclearEnemy = false;
        foreach (var id in war.Side(true))
            if (campaign.NationById(id) is { } enemy && Simulation.Deterrence.TierOf(campaign, enemy) > 0) { nuclearEnemy = true; break; }
        Nation best = null;
        var bestWillingness = float.NegativeInfinity;
        foreach (var donor in campaign.Nations)
        {
            // A player is asked through the inbox review, not here: one plea at a time on their table.
            if (donor.IsHuman || donor.Id == nation.Id || donor.Population <= 0 || donor.Movement is not null) continue;
            if (war.Involves(donor.Id)) continue;
            // Against a nuclear power only a sworn ally comes in person - a treaty alliance, a
            // defence charter, an overlord. A guarantor or a worried neighbour sends money and
            // arms instead (ForeignAssistance). Without this a guarantor walked into Russia's war
            // with Ukraine, three great powers made it a world war, and the blocs followed.
            if (nuclearEnemy && !Sworn(campaign, nation, donor)) continue;
            var assessment = WarInterventions.CanSubmit(campaign, nation.Id, donor.Id, war.Id);
            if (!assessment.Allowed || assessment.Willingness <= bestWillingness) continue;
            best = donor;
            bestWillingness = assessment.Willingness;
        }
        if (best is null) { Count("rescue_nobody"); return false; }

        var outcome = WarInterventions.Submit(campaign, nation.Id, best.Id, war.Id);
        Count(outcome.State == WarInterventionState.Joined ? "rescue_joined" : "rescue_refused");
        if (outcome.State == WarInterventionState.Joined)
            DiagnosticLogs.Write("simulation",
                $"RESCUE {best.Tag} comes into {war.Name} for {nation.Tag} day={campaign.ElapsedDays}");
        return true;
    }

    /// <summary>Bound to come by more than goodwill: an alliance, a shared defence charter, or rule over us.</summary>
    private static bool Sworn(Campaign campaign, Nation nation, Nation donor) =>
        nation.AnswersTo(donor.Id) ||
        CampaignDiplomacy.Peek(campaign, nation.Id, donor.Id) is { Stance: DiplomaticStance.Allied } ||
        InternationalOrganizations.DefencePartners(campaign, nation.Id).Contains(donor.Id);

    /// <summary>The defending leader, losing ground to a side no weaker than its own, or facing a side plainly stronger.</summary>
    internal static bool InTrouble(Campaign campaign, Nation nation, StrategicWar war)
    {
        // The country the war was declared on asks. A friend that came in is not itself rescued:
        // when every newcomer asked in turn, one plea for Ukraine became thirty-four countries.
        if (war.DefenderId != nation.Id) return false;
        var defines = campaign.Defines;
        var control = campaign.ProvinceController;
        var losing = false;
        if (control is not null)
        {
            var own = (ushort)nation.MapIndex;
            var lost = 0;
            foreach (var (_, was) in war.OriginalController)
                if (was == own) lost++;
            if (lost > 0)
            {
                var held = 0;
                for (var province = 1; province < control.Length; province++)
                    if (control[province] == own) held++;
                losing = lost >= (held + lost) * defines.AiRescueLossShare;
            }
        }

        var ours = 0f;
        var theirs = 0f;
        foreach (var id in war.Side(false))
            if (campaign.NationById(id) is { } friend) ours += Strength(campaign, friend);
        foreach (var id in war.Side(true))
            if (campaign.NationById(id) is { } enemy) theirs += CampaignIntel.EstimatedStrength(campaign, nation.Id, enemy);
        // Ground falling to a side at least as strong as ours, or a side plainly stronger. Once the
        // friends who came make our side the stronger one, nobody else is asked.
        ours = MathF.Max(0.01f, ours);
        return (losing && theirs >= ours) || theirs > ours * defines.AiRescueStrengthFear;
    }
}
