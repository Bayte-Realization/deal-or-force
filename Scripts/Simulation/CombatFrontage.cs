using System.Collections.Generic;

namespace DealOrForce.Simulation;

// Who stands in the line and who waits behind it. A province gives a side so much
// room; what does not fit there is not in the fight, and the day is fought by the
// formations that are.
public static partial class Warfare
{
    // The line, the reserve and the candidates live on the campaign's battle buckets.
    // This split clears and refills those lists; it does not allocate a fresh one per side.

    /// <summary>
    /// Splits one side into the formations that fit the ground and the ones that wait
    /// behind them. Fills <paramref name="line"/> and <paramref name="reserve"/>; both are
    /// cleared first.
    ///
    /// Strongest first, and by id where two are equally strong - the same order the
    /// reinforcement pass uses, so two machines draw the same line. Because the split is
    /// redrawn every day, a formation that has been shot to pieces drops out of the line by
    /// itself and a fresh one takes its place, which is how a reserve rotates in without a
    /// rotation rule.
    ///
    /// When <paramref name="campaign"/> and <paramref name="battle"/> are supplied (the live
    /// battle day), HOI4-style reinforcement chance gates whether a reserve formation that was
    /// NOT in the firing line yesterday manages to move into an open slot today, scaled by
    /// signal/recon initiative and <c>reinforcement_rate</c> doctrine modifiers.
    /// </summary>
    private static void Frontage(IReadOnlyCollection<Army> side, float room, List<Army> line, List<Army> reserve)
        => FrontageReady(side, room, line, reserve, 0f, null, null, false);

    private static void FrontageReady(IReadOnlyCollection<Army> side, float room, List<Army> line,
        List<Army> reserve, float breakOrganisation, Campaign campaign = null, LandBattle battle = null,
        bool attacker = false)
    {
        line.Clear();
        reserve.Clear();
        if (side.Count == 0) return;

        LineCandidates.Clear();
        LineCandidates.AddRange(side);
        var prevLine = battle is null ? null : attacker ? battle.LastAttackerLine : battle.LastDefenderLine;
        var hasPrevLine = prevLine is { Count: > 0 };

        LineCandidates.Sort((left, right) =>
        {
            if (hasPrevLine)
            {
                var leftInLine = prevLine.Contains(left.Id) && left.Organisation > breakOrganisation;
                var rightInLine = prevLine.Contains(right.Id) && right.Organisation > breakOrganisation;
                if (leftInLine != rightInLine) return rightInLine.CompareTo(leftInLine);
            }
            var byPower = right.Power.CompareTo(left.Power);
            return byPower != 0 ? byPower : left.Id.CompareTo(right.Id);
        });

        var held = 0f;
        var baseChance = campaign?.Defines.CombatReinforceBaseChance ?? 0.55f;
        foreach (var army in LineCandidates)
        {
            if (army.Organisation <= breakOrganisation) { reserve.Add(army); continue; }
            var width = army.Divisions * UnitTypeCatalog.Get(army.TemplateId).CombatWidth;
            // The first formation always stands in the line, however wide it is: somebody
            // holds the province, and the overcrowding penalty is what it pays for ground
            // it does not have. Everything after it has to fit.
            if (line.Count > 0 && held + width > room + 0.001f) { reserve.Add(army); continue; }
            if (hasPrevLine && line.Count > 0 && !prevLine.Contains(army.Id) && campaign is not null && battle is not null)
            {
                var nation = campaign.NationById(army.NationId);
                var docBonus = nation is null ? 0f : NationalModifiers.Of(nation, "reinforcement_rate") * 0.5f;
                var chance = M.Clamp(baseChance + army.Recon * 0.25f + army.Cohesion * 0.15f + docBonus, 0.18f, 0.96f);
                var roll = Escalation.Roll(campaign.Seed, battle.Id, army.Id, campaign.ElapsedDays, 0x51C);
                if (roll > chance)
                {
                    reserve.Add(army);
                    continue;
                }
            }
            line.Add(army);
            held += width;
        }
        // All spent: retain one line entry so the round can record its final outcome.
        if (line.Count == 0 && reserve.Count > 0)
        { line.Add(reserve[0]); reserve.RemoveAt(0); }
    }
}

public sealed partial class Defines
{
    /// <summary>
    /// Base daily probability (0..1) that a reserve formation in the battle province moves into
    /// an open frontage slot during an active battle (HOI4 combat reinforcement chance).
    /// </summary>
    public float CombatReinforceBaseChance { get; set; } = 0.55f;

    /// <summary>
    /// How much of a day's fighting reaches a formation waiting behind the line, as a share
    /// of what the formation in the line takes. Shelling that misses the front finds the
    /// queue behind it, and nothing the queue does answers back.
    ///
    /// A quarter, not zero: a reserve is a formation in the same province under the same
    /// guns, and a free bunker would make a stack of nineteen wait out a battle untouched.
    /// </summary>
    public float ReserveExposure { get; set; } = 0.25f;
}
