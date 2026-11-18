using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What a quiet week adds to a conquered region's acceptance of the state that holds it, before
    /// anything speeds it up. 0.001 is about nineteen years from nothing to home: a generation, and
    /// half of the fifty years HomelandIntegration asked of foreign ground before. Zero switches the
    /// digestion of conquered ground off.
    /// </summary>
    public float AcceptanceWeekly { get; set; } = 0.001f;
    /// <summary>How much faster a people of the holder's own family of peoples accepts it (cultures "group").</summary>
    public float AcceptanceKindredFactor { get; set; } = 1.5f;
    /// <summary>Radicalism at or below which a region is calm enough to accept at the full rate.</summary>
    public float AcceptanceCalmRadicalism { get; set; } = 0.15f;
    /// <summary>Radicalism at or above which acceptance stops: the same line that stops BorderHabit.</summary>
    public float AcceptanceStallRadicalism { get; set; } = 0.50f;
    /// <summary>The share of the minority's cost (stability and radicalism) a fully accepted region no longer pays.</summary>
    public float AcceptanceMinorityRelief { get; set; } = 0.5f;
    /// <summary>The share of a region's weight in imperial overstretch that full acceptance takes away.</summary>
    public float AcceptanceOverstretchRelief { get; set; } = 0.6f;
    /// <summary>
    /// The share of how settled a region was under the state that gave it up that crosses with it when
    /// the two governments agree the transfer (a deal, a union, a grant to a subject): its old holder's
    /// homeland arrives at this, a region its old holder was still digesting at this share of what it
    /// had, and ground the old holder had only just taken at nothing.
    /// </summary>
    public float AcceptancePeacefulStart { get; set; } = 0.5f;
    /// <summary>The share of its acceptance a region keeps when a rising in it is crushed.</summary>
    public float AcceptanceRisingKeep { get; set; } = 0.5f;
    /// <summary>The share of its radicalism a region keeps when the breakaway that held it is crushed.</summary>
    public float RisingCrushedRadicalismCut { get; set; } = 0.5f;
    /// <summary>How long a region taken back from a crushed rising stays exhausted. Zero: never.</summary>
    public int RisingExhaustionDays { get; set; } = 1095;
    /// <summary>The share of grievance, overstretch and occupation radicalism an exhausted region still takes in.</summary>
    public float RisingExhaustionInflow { get; set; } = 0.35f;
}

public sealed partial class ScenarioState
{
    /// <summary>
    /// How far the people of this region have accepted the state that governs them, 0..1. Grows
    /// only on ground the holder has no title to and whose people are not its own (ConquestAcceptance),
    /// so it is 0 everywhere a 2024 world starts and the stability calibration sees the formula it
    /// always saw. At 1 the region becomes part of its holder's homeland.
    /// </summary>
    public float Acceptance;

    /// <summary>
    /// The country (Nation.Id) whose rule <see cref="Acceptance"/> measures, or -1 for "whoever held
    /// it when the campaign began". Acceptance is the people's, of one state: it was earned under that
    /// state and means nothing about the next one. Without the record a region a country had fully
    /// digested passed its 1 to whoever it was ceded to, who made it homeland a month later, and a
    /// fresh conquest handed to a subject and annexed back kept the half a treaty starts at.
    /// Not named "...Id": the save pins every such member (SaveDefaults), and this one should cost
    /// nothing in a world nobody has conquered in.
    /// </summary>
    public int AcceptanceOf = -1;

    /// <summary>
    /// The last day this region is still exhausted after a crushed rising (RisingAftermath), or -1.
    /// While it lasts, grievance, overstretch and occupation feed its radicalism a third as fast.
    /// </summary>
    public int ExhaustedUntilDay = -1;
}

/// <summary>
/// Conquered ground digested by years of quiet.
///
/// A region taken from someone else used to stay exactly as foreign as the day it was taken: its
/// minority cost the full thirty points of stability for ever (assimilation moves 0.6 % a year), its
/// weight in the empire's overstretch rose with the anger the overstretch itself fed, and the only
/// road home was fifty years of calm. Serbia holding Croatia and Bosnia lost a third of its
/// stability to them and never got it back; that is the trap the owner described on 17 November
/// 2026 (docs/design/gameplay-2026-11-17/conquest-aftermath.md).
///
/// Acceptance is the thing that was missing: people get used to a government that leaves them in
/// peace. It grows a little each quiet week, faster among a kindred people and in a region that
/// governs itself, not at all above half radicalism or under a foreign army, and it takes the edge
/// off what the minority and the overstretch cost. It never makes conquest free - the first years
/// cost what they always did - it makes the cost end for whoever governs well enough to let it.
/// </summary>
public static class ConquestAcceptance
{
    /// <summary>Foreign people at or above this share make ground the holder has no title to a strain - the overstretch test.</summary>
    public const float UnentitledForeignShare = 0.35f;

    /// <summary>Stall reasons, as interface keys.</summary>
    public const string StalledByRadicalism = "Stalled: radicalism above {0}";
    public const string StalledByOccupation = "Stalled: a foreign army holds part of it";

    /// <summary>
    /// Ground the holder has no right to in its people's eyes: not its homeland, and a third or
    /// more of the people not its own. The same test StabilityCore.Overstretch weighs, so the
    /// regions that strain an empire are exactly the regions that can come to accept it.
    /// </summary>
    public static bool Unentitled(Campaign c, Nation owner, ScenarioState state) =>
        c is not null && owner is not null && state is not null && !state.Unclaimed && state.Population > 0 &&
        TerritorialClaims.TitleOf(c, owner, state) != TerritorialTitle.Homeland &&
        EconomySystem.ForeignShare(state, owner.Tag) >= UnentitledForeignShare;

    /// <summary>
    /// Whether the region's largest people is of the same family of peoples as any the holder is for,
    /// or one the union the holder proclaimed names. A union's promise to a people of another family
    /// does not make it the state's own (FormableNations.DeclareUnionPeoples), but it is a promise:
    /// such a people comes to terms at the kindred pace rather than at a stranger's.
    /// </summary>
    public static bool Kindred(Nation owner, ScenarioState state, Campaign c = null)
    {
        if (owner is null || state is null) return false;
        var cultures = EconomySystem.Cultures(state);
        if (cultures.Count == 0) return false;
        var majority = cultures[0].Culture;
        var group = DemographyCatalog.Culture(majority)?.Group;
        if (!string.IsNullOrEmpty(group))
            foreach (var own in DemographyCatalog.PrimaryCultures(owner.Tag))
                if (string.Equals(DemographyCatalog.Culture(own)?.Group, group, StringComparison.OrdinalIgnoreCase)) return true;
        return c is not null && !string.IsNullOrEmpty(owner.FormedNationId) &&
               FormableNations.Find(c, owner.FormedNationId) is { Restores.Length: 0 } union &&
               FormableNations.NamesPeople(union, majority);
    }

    /// <summary>
    /// The acceptance this region has of this holder: what it has earned under it, and nothing it
    /// earned under somebody else (<see cref="ScenarioState.AcceptanceOf"/>). With no holder named,
    /// what the region has.
    /// </summary>
    public static float Of(ScenarioState state, Nation owner)
    {
        if (state is null || state.Acceptance <= 0f) return 0f;
        if (owner is not null && state.AcceptanceOf >= 0 && state.AcceptanceOf != owner.Id) return 0f;
        return M.Clamp01(state.Acceptance);
    }

    /// <summary>
    /// What one quiet week adds, and if nothing, why. Read by the weekly pass, the region card and
    /// the tests alike, so the figure a player is shown is the one that is applied.
    /// </summary>
    public static float WeeklyGain(Campaign c, Nation owner, ScenarioState state, out string stall)
    {
        stall = "";
        if (!Unentitled(c, owner, state)) return 0f;
        var d = c.Defines;
        var gain = MathF.Max(0f, d.AcceptanceWeekly);
        if (gain <= 0f) return 0f;
        if (Kindred(owner, state, c)) gain *= MathF.Max(0f, d.AcceptanceKindredFactor);
        if (RegionalAutonomy.Autonomous(state)) gain *= MathF.Max(0f, d.AutonomyAcceptanceFactor);
        // Calm, not quiet by force: a region still half radical is not getting used to anybody.
        var span = d.AcceptanceStallRadicalism - d.AcceptanceCalmRadicalism;
        var calm = span > 0f
            ? M.Clamp01(1f - (state.Radicalism - d.AcceptanceCalmRadicalism) / span)
            : state.Radicalism < d.AcceptanceStallRadicalism ? 1f : 0f;
        if (calm <= 0f) { stall = StalledByRadicalism; return 0f; }
        // Nobody gets used to a state whose writ does not run where they live.
        if (StabilityCore.OccupiedShare(c, state, owner) > 0.05f) { stall = StalledByOccupation; return 0f; }
        return gain * calm;
    }

    /// <summary>Once a week, on the economic week, for every region a country holds.</summary>
    internal static void Week(Campaign c, Nation owner, ScenarioState state)
    {
        var gain = WeeklyGain(c, owner, state, out _);
        if (gain <= 0f) return;
        // A region still counting what it had of the state a breakaway took it from starts its own
        // count for the one that governs it now.
        if (state.AcceptanceOf != owner.Id)
        {
            if (state.AcceptanceOf >= 0) state.Acceptance = 0f;
            state.AcceptanceOf = owner.Id;
        }
        state.Acceptance = MathF.Min(1f, state.Acceptance + gain);
    }

    /// <summary>
    /// What the minority still costs, as a share of its full cost: all of it at no acceptance, half
    /// of it at full acceptance. Not nothing - Wales is still Wales - and exactly one at zero, so the
    /// stability of a world nobody has conquered anything in is the same to the last bit.
    /// </summary>
    public static float MinorityFactor(Defines d, ScenarioState state, Nation owner = null)
    {
        var accepted = Of(state, owner);
        if (accepted <= 0f) return 1f;
        return 1f - M.Clamp01(d?.AcceptanceMinorityRelief ?? 0.5f) * accepted;
    }

    /// <summary>
    /// What one region the holder has no title to weighs in its imperial overstretch.
    ///
    /// A conquered people strains an empire by its anger, not by being there: a calm annexed region
    /// weighs 0.4, a seething one the full 1. Measured: Russia, after taking a dozen Ukrainian
    /// regions, sat at the overstretch cap for good. Acceptance lightens it - a region that has come
    /// to terms with its holder is not a garrison - and a region that runs itself (RegionalAutonomy)
    /// weighs as little as the calmest one does. Tools/SimBench/ConquestReport reads this same sum.
    /// </summary>
    public static float StrainWeight(Campaign c, ScenarioState state, Nation owner = null)
    {
        var weight = 0.4f + 0.6f * M.Clamp01(state.Radicalism / 0.4f);
        var d = c?.Defines;
        var accepted = Of(state, owner);
        if (accepted > 0f)
            weight *= 1f - M.Clamp01(d?.AcceptanceOverstretchRelief ?? 0.6f) * accepted;
        if (RegionalAutonomy.Autonomous(state)) weight *= MathF.Max(0f, d?.AutonomyOverstretchWeight ?? 0.4f);
        return weight;
    }

    /// <summary>
    /// The region changed hands inside StatePartition.Annex. One place decides what the people of it
    /// carry across, because there are twenty roads into Annex and each would otherwise decide alone.
    /// Acceptance is of one state, so in every case but a return it starts again for the new holder:
    ///   taken back, by force, from a breakaway that took it from this state - RisingAftermath.Crushed;
    ///   the same ground rejoining of its own accord - a reunion, and it keeps what it had;
    ///   left with a breakaway or a movement - kept, still counted toward the state it left, so that
    ///     crushing it later halves what it had and a reunion keeps it;
    ///   handed over by agreement of both governments (a deal, a union, a grant to a subject) -
    ///     AcceptancePeacefulStart of how settled it was under the state that gave it up;
    ///   anything else - a war, an ultimatum yielded to, a release - from nothing. Ground given up
    ///     under a threat of force is ground taken by force: were it to arrive half accepted, the
    ///     ultimatum would be the cheap way to conquer that this slice was told must not exist.
    /// Autonomy is the old holder's grant, and never survives a change of owner.
    /// </summary>
    internal static void ChangedHands(Campaign c, ScenarioState region, Nation former, Nation owner,
        bool asConquest, bool byConsent)
    {
        if (c is null || region is null || owner is null) return;
        region.AutonomousSinceDay = -1;
        region.AutonomyRevokedDay = -1;
        // Only ground that was this state's before its breakaway took it is a rising put down or a
        // reunion. The breakaway's own conquests are an ordinary war's spoils, and a country this
        // state released of its own accord is not a rising: taking either is a conquest like any other.
        if (former is not null && Reintegration.IsBreakawayOf(former, owner) && former.ReleasedFromId != owner.Id &&
            WasHeldBy(c, region, owner))
        {
            if (byConsent) region.AcceptanceOf = owner.Id;
            else RisingAftermath.Crushed(c, owner, region);
            return;
        }
        if (former is not null && (owner.Movement is not null || Reintegration.IsBreakawayOf(owner, former)))
        {
            if (region.AcceptanceOf < 0) region.AcceptanceOf = former.Id;
            return;
        }
        var carried = !asConquest && byConsent && former is not null
            ? Settled(c, region, former) * M.Clamp01(c.Defines.AcceptancePeacefulStart)
            : 0f;
        region.Acceptance = carried;
        region.AcceptanceOf = owner.Id;
    }

    /// <summary>
    /// Whether this region was this state's before the breakaway now holding it took it: the record
    /// ChangedHands keeps while a breakaway holds it, or the state's own homeland title - the
    /// breakaway may have started a count of its own there, and the homeland is still the homeland.
    /// </summary>
    internal static bool WasHeldBy(Campaign c, ScenarioState region, Nation nation) =>
        region.AcceptanceOf == nation.Id || TerritorialClaims.TitleOf(c, nation, region) == TerritorialTitle.Homeland;

    /// <summary>
    /// How settled the region was under the state giving it up: its homeland is settled ground; a
    /// region it was still digesting is as settled as its people had come to accept it.
    /// </summary>
    static float Settled(Campaign c, ScenarioState region, Nation former) =>
        TerritorialClaims.TitleOf(c, former, region) == TerritorialTitle.Homeland ? 1f : Of(region, former);

    /// <summary>What the region card shows about how far a region is digested, and why it is not moving.</summary>
    public readonly record struct Outlook(bool Shown, float Acceptance, float WeeklyGain, int YearsLeft,
        string StallKey, int AutonomySince, int ExhaustedDaysLeft);

    /// <summary>
    /// The card's figures, from the same rule the weekly pass applies. Years are the whole years
    /// left at this week's pace, 31 meaning "more than thirty".
    /// </summary>
    public static Outlook Read(Campaign c, Nation owner, ScenarioState state)
    {
        if (c is null || owner is null || state is null) return default;
        var unentitled = Unentitled(c, owner, state);
        var gain = WeeklyGain(c, owner, state, out var stall);
        var acceptance = Of(state, owner);
        var years = acceptance >= 1f ? 0
            : gain <= 0f ? 31
            : (int)Math.Min(31, Math.Ceiling((1f - acceptance) / gain / 52f));
        return new Outlook(unentitled || acceptance > 0f, acceptance, gain, years, stall,
            state.AutonomousSinceDay, RisingAftermath.DaysLeft(c, state));
    }
}

/// <summary>
/// A crushed rising exhausts its region.
///
/// A region taken back from the breakaway that held it used to come back exactly as angry as it left
/// - radicalism 0.43 to 0.99 in the measured runs - into the same minority grievance and the same
/// overstretch, and the two years of RevoltRecovery only decided the day it rose again: Zlatibor
/// crushed on day 1388 rose on day 2118, Zadar 1795 and 2789. Beaten rebels are years of quiet, not
/// a queue: Poland after 1863, Ireland after 1798, Hungary after 1849. So the rising's defeat halves
/// the anger it had, and for three years what feeds anger there arrives a third as fast. It is not a
/// new immunity - RevoltRecovery stays the only guard against a new rising - it is what makes the
/// pot not full when that guard ends. Nor is it reconciliation: what acceptance the region had is
/// halved, because being crushed is not the same as agreeing.
/// </summary>
public static class RisingAftermath
{
    public static bool Exhausted(Campaign c, ScenarioState state) =>
        c is not null && state is not null && state.ExhaustedUntilDay >= 0 && c.ElapsedDays < state.ExhaustedUntilDay;

    public static int DaysLeft(Campaign c, ScenarioState state) =>
        Exhausted(c, state) ? state.ExhaustedUntilDay - c.ElapsedDays : 0;

    /// <summary>The region was taken back from the breakaway that held it, by force.</summary>
    public static void Crushed(Campaign c, Nation owner, ScenarioState region)
    {
        if (c is null || region is null) return;
        var d = c.Defines;
        region.Radicalism = M.Clamp01(region.Radicalism * M.Clamp01(d.RisingCrushedRadicalismCut));
        region.Acceptance = M.Clamp01(ConquestAcceptance.Of(region, owner) * M.Clamp01(d.AcceptanceRisingKeep));
        if (owner is not null) region.AcceptanceOf = owner.Id;
        if (d.RisingExhaustionDays > 0)
            region.ExhaustedUntilDay = Math.Max(region.ExhaustedUntilDay,
                (int)Math.Min(int.MaxValue, (long)c.ElapsedDays + d.RisingExhaustionDays));
        if (owner is not null) c.Chronicle.Record(c, "rising_exhausted", owner.Id, -1, place: region.Name);
    }

    /// <summary>
    /// How much of this week's inflow of grievance an exhausted region does not take in: the
    /// difference between the full inflow and RisingExhaustionInflow of it. Zero when not exhausted.
    /// </summary>
    internal static float Relief(Campaign c, ScenarioState state, float inflow)
    {
        if (inflow <= 0f || !Exhausted(c, state)) return 0f;
        return -inflow * (1f - M.Clamp01(c.Defines.RisingExhaustionInflow));
    }
}
