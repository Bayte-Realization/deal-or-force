using System.Collections.Generic;

namespace DealOrForce.Simulation;

// Who stands in the line and who waits behind it. A province gives a side so much
// room; what does not fit there is not in the fight, and the day is fought by the
// formations that are.
public static partial class Warfare
{
    // Scratch for the split, reused by every battle on every day rather than allocated
    // per side per battle. Cleared before each use.
    private static readonly List<Army> LineCandidates = new();
    private static readonly List<Army> AttackerLine = new();
    private static readonly List<Army> AttackerReserve = new();
    private static readonly List<Army> DefenderLine = new();
    private static readonly List<Army> DefenderReserve = new();

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
    /// </summary>
    private static void Frontage(IReadOnlyCollection<Army> side, float room, List<Army> line, List<Army> reserve)
        => FrontageReady(side, room, line, reserve, 0f);

    private static void FrontageReady(IReadOnlyCollection<Army> side, float room, List<Army> line,
        List<Army> reserve, float breakOrganisation)
    {
        line.Clear();
        reserve.Clear();
        if (side.Count == 0) return;

        LineCandidates.Clear();
        LineCandidates.AddRange(side);
        LineCandidates.Sort(static (left, right) =>
        {
            var byPower = right.Power.CompareTo(left.Power);
            return byPower != 0 ? byPower : left.Id.CompareTo(right.Id);
        });

        var held = 0f;
        foreach (var army in LineCandidates)
        {
            if (army.Organisation <= breakOrganisation) { reserve.Add(army); continue; }
            var width = army.Divisions * UnitTypeCatalog.Get(army.TemplateId).CombatWidth;
            // The first formation always stands in the line, however wide it is: somebody
            // holds the province, and the overcrowding penalty is what it pays for ground
            // it does not have. Everything after it has to fit.
            if (line.Count > 0 && held + width > room + 0.001f) { reserve.Add(army); continue; }
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
    /// How much of a day's fighting reaches a formation waiting behind the line, as a share
    /// of what the formation in the line takes. Shelling that misses the front finds the
    /// queue behind it, and nothing the queue does answers back.
    ///
    /// A quarter, not zero: a reserve is a formation in the same province under the same
    /// guns, and a free bunker would make a stack of nineteen wait out a battle untouched.
    /// </summary>
    public float ReserveExposure { get; set; } = 0.25f;
}
