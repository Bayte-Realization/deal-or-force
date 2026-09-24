using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Two things a formation can be doing that are not "marching towards that province".
///
/// They live here rather than beside the rest of the formation because they are additions
/// to a model that already worked, and because saving them costs nothing: the save writes
/// whole Army objects, so a field added here is a field that survives a reload.
/// </summary>
public sealed partial class Army
{
    /// <summary>
    /// Fight for this province and do not walk into it.
    ///
    /// The difference between helping a neighbour break through and abandoning the position
    /// you were holding to do it. Without it, every attack is also a decision to leave where
    /// you stand, which is why a player supporting an offensive from a river line kept
    /// finding their river line empty the next morning.
    /// </summary>
    public bool SupportOnly;

    /// <summary>
    /// Where this formation is being railed to, or -1 when it is marching normally.
    ///
    /// Strategic redeployment is not teleportation and not a way to drive through an enemy:
    /// it is the same route through the same friendly provinces, taken faster because the
    /// formation is on trains and lorries rather than on its feet, and paid for in
    /// organisation - it arrives quickly and in no condition to attack.
    ///
    /// It clears itself. A formation is redeploying while it has a target and something
    /// left to walk; when the route runs out, so does the mode, and no other system has to
    /// remember to switch it off.
    /// </summary>
    public int RedeployTarget = -1;

    public bool IsRedeploying => RedeployTarget >= 0 && Route.Count > 0;
}

/// <summary>Join a battle without taking the ground: Ctrl and the right button.</summary>
public sealed class SupportAttackCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public bool SupportOnly { get; init; } = true;

    public void Apply(Campaign campaign)
    {
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null || army.NationId != IssuerId) return;
        army.SupportOnly = SupportOnly;
    }
}

/// <summary>
/// Put a formation on the railways.
///
/// Refused where it would be a lie: there has to be a route through the country's own
/// ground, which is the same test an ordinary march passes, because the point of this order
/// is speed and not access.
/// </summary>
public sealed class StrategicRedeploymentCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ArmyId { get; init; }
    public int TargetProvinceId { get; init; }

    public ArmyMoveResult Result { get; private set; }

    public void Apply(Campaign campaign)
    {
        Result = ArmyMoveResult.None;
        var army = campaign.Armies.Find(candidate => candidate.Id == ArmyId);
        if (army is null)
        {
            Result = ArmyMoveResult.ArmyNotFound;
            return;
        }
        if (army.NationId != IssuerId)
        {
            Result = ArmyMoveResult.NotControlled;
            return;
        }

        var move = new MoveArmyCommand
        {
            IssuerId = IssuerId,
            ArmyId = ArmyId,
            TargetProvinceId = TargetProvinceId
        };
        move.Apply(campaign);
        Result = move.Result;
        // Only an ordinary march becomes a redeployment. An order that turned into an
        // attack is not eligible: you cannot rail through somebody who is shooting.
        army.RedeployTarget = Result == ArmyMoveResult.Issued ? TargetProvinceId : -1;
        if (army.RedeployTarget >= 0) army.SupportOnly = false;
    }
}

/// <summary>
/// A day of being railed somewhere: faster, and worse when it arrives.
///
/// Called from the march tick, once per step per moving formation. It returns what to
/// multiply the step's progress by and charges the step's share of a day's organisation on
/// the way through - the two belong together because the speed is what is being paid for.
/// </summary>
public static class StrategicRedeployment
{
    // Kept as the fallback for a caller without a campaign, the same arrangement the march
    // rates use. The live numbers are on Defines, where a movement mod can reach them.
    /// <summary>How much faster the railways are than a road march.</summary>
    public const float SpeedMultiplier = 3.2f;

    /// <summary>What a day on the trains costs in organisation.</summary>
    public const float OrganisationPerDay = 0.16f;

    public static float DayOf(Campaign campaign, Army army)
    {
        if (army is null || !army.IsRedeploying) return 1f;
        var defines = campaign?.Defines;
        // The step's share of the day, not the day. This was written when a step was a day,
        // and at the shipped hourly step it charged a whole day's price every hour: a trip of
        // three provinces cost a division 93 % of its organisation, which made the railways
        // the most exhausting way there was of getting anywhere.
        var share = campaign?.TickScale ?? 1f;
        army.Organisation = M.Clamp01(army.Organisation -
            (defines?.RailRedeploymentOrganisationPerDay ?? OrganisationPerDay) * share);
        // Nothing dug in survives being loaded onto a train, and a plan made for one part of
        // the front is not a plan for the other end of it. Halved per day on the trains, the
        // same at any step.
        army.Entrenchment = 0f;
        army.Planning = M.Clamp01(army.Planning * MathF.Pow(0.5f, share));
        return defines?.RailRedeploymentSpeed ?? SpeedMultiplier;
    }
}

/// <summary>
/// Fold worn-down formations into each other.
///
/// Not a way to build one enormous division: the people and the equipment are counted, the
/// survivors are filled to their establishment, and whatever is left over stops existing as
/// a separate formation. Six divisions at a third strength become two at full strength and
/// four fewer things to feed - which is the actual reason to do it, because supply is
/// charged per formation and a hollow division eats like a whole one.
///
/// The conditions are the honest ones: the same template, the same province, and nobody in
/// a battle. A formation cannot be consolidated out of existence while it is fighting.
/// </summary>
public sealed class ConsolidateArmiesCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public IReadOnlyList<int> ArmyIds { get; init; } = Array.Empty<int>();

    /// <summary>How many formations stopped existing, and how many people were kept.</summary>
    public int Merged { get; private set; }
    public int PersonnelKept { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Merged = 0;
        PersonnelKept = 0;
        Refusal = "";

        var chosen = ArmyIds
            .Select(id => campaign.Armies.Find(candidate => candidate.Id == id))
            .Where(army => army is not null && army.NationId == IssuerId)
            .ToList();
        if (chosen.Count < 2)
        {
            Refusal = "Consolidation needs at least two formations.";
            return;
        }
        if (chosen.Any(army => army.InBattle))
        {
            Refusal = "A formation in a battle cannot be consolidated out of it.";
            return;
        }
        if (chosen.Any(army => army.EmbarkedFleetId >= 0))
        {
            Refusal = "Formations at sea cannot be consolidated.";
            return;
        }

        // Same template, same place. Both are what makes the arithmetic honest: units of
        // different establishments hold different equipment, and men in another province
        // are not men who can be handed over.
        foreach (var group in chosen
                     .GroupBy(army => $"{army.TemplateId?.ToLowerInvariant()}@{army.ProvinceId}", StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var members = group.OrderByDescending(army => army.Personnel)
                .ThenBy(army => army.Id)
                .ToList();

            long people = members.Sum(army => (long)army.Personnel);
            var recovering = members.Sum(army => army.Recovering);
            var establishment = Math.Max(1, members[0].MaxPersonnel);
            // Weighted by the men each formation brings, so consolidating a veteran cadre
            // into a mass of replacements does not make the whole thing veteran.
            var experience = Weighted(members, army => army.Experience, people);
            var equipment = Weighted(members, army => army.Equipment, people);
            var organisation = Weighted(members, army => army.Organisation, people);

            var keep = (int)Math.Max(1, Math.Min(members.Count, (people + establishment - 1) / establishment));
            for (var index = 0; index < keep; index++)
            {
                var share = (int)Math.Min(establishment, people);
                people -= share;
                var army = members[index];
                army.Personnel = share;
                army.Strength = M.Clamp01(share / (float)establishment);
                army.Experience = experience;
                army.Equipment = equipment;
                army.Organisation = organisation;
                army.Recovering = recovering / keep;
                // Newly mixed and standing in the open: neither the digging nor the
                // preparation of any of the formations that went into it survives.
                army.Entrenchment = 0f;
                army.Planning = 0f;
                PersonnelKept += share;
            }

            for (var index = keep; index < members.Count; index++)
            {
                var army = members[index];
                DrawFrontLineCommand.Detach(campaign, army);
                campaign.Armies.Remove(army);
                Merged++;
            }
        }

        if (Merged == 0 && string.IsNullOrEmpty(Refusal))
            Refusal = PersonnelKept > 0
                // They matched and they are simply full. Folding full divisions together
                // would have to throw men away, and that is not a thing to do by accident.
                ? "These formations are at strength; there is nothing to fold together."
                : "Nothing to consolidate: formations must share a template and a province.";
    }

    private static float Weighted(List<Army> members, Func<Army, float> of, long people)
    {
        if (people <= 0) return members.Count == 0 ? 0f : members.Average(of);
        double sum = members.Sum(army => (double)of(army) * army.Personnel);
        return (float)(sum / people);
    }

}
