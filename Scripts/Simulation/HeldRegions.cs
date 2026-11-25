using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Whether a region held by another country works for the one holding it.</summary>
    public bool HeldRegionsWorkForHolder { get; set; } = true;

    /// <summary>The share of a region's provinces a country must control for the region to be under its administration.</summary>
    public float HeldRegionControlShare { get; set; } = 0.5f;

    /// <summary>Days a country must have held a region before its economy answers to it rather than to the owner.</summary>
    public int HeldRegionSettleDays { get; set; } = 14;

    /// <summary>
    /// How much of a held region's output its administration gets out of it: from the first
    /// day's, to the fully integrated, over HeldRegionIntegrationDays - an occupation run by the
    /// army, then a military administration, then a region run as one's own.
    /// </summary>
    public float HeldRegionEfficiencyStart { get; set; } = 0.35f;
    public float HeldRegionEfficiencyFull { get; set; } = 0.85f;
    public int HeldRegionIntegrationDays { get; set; } = 365;

    /// <summary>
    /// Days a country must have administered a region before it may hold a vote there on joining
    /// it - in the middle of the war, as Russia did in four Ukrainian regions in September 2022;
    /// and the computer's: the days it waits, and its monthly chance once it may.
    /// </summary>
    public int HeldReferendumDays { get; set; } = 180;
    public int AiHeldReferendumDays { get; set; } = 365;
    public float AiHeldReferendumChance { get; set; } = 0.04f;
}

public sealed partial class ScenarioState
{
    /// <summary>The country that administers the region although another owns it (nation id), or -1; and since when.</summary>
    public int HeldById = -1;
    public int HeldSinceDay;
}

/// <summary>
/// Held ground is ground. A region another country controls - by an advance, by troops sent in
/// without a war - works for the one holding it: its output, its workers and its people's demand
/// are in the holder's market, its GDP and its taxes, at the efficiency of an administration that
/// is new and becomes settled, and the owner has none of it. What the owner keeps is the title -
/// the law, the claim, the peace table - which is the one real difference between holding and
/// owning (a vote under the troops or a treaty settles that too). A lease is not held ground: the
/// tenant has a base, the region's economy stays its owner's.
/// </summary>
public static class HeldRegions
{
    private sealed class Board
    {
        public int Revision = int.MinValue;
        public readonly Dictionary<int, List<ScenarioState>> HeldBy = new();
    }

    private static readonly ConditionalWeakTable<Campaign, Board> Boards = new();
    [ThreadStatic] private static List<ScenarioState> _scratch;

    /// <summary>
    /// Work out who administers what, once per map change: a region whose provinces are mostly
    /// controlled by one country other than its owner, and not by a lease, is that country's.
    /// The day it changed hands is kept on the region, so the settling-in period survives a save.
    /// </summary>
    public static void Refresh(Campaign c)
    {
        if (c?.ProvinceMap is null || c.ProvinceController is null) return;
        var board = Boards.GetOrCreateValue(c);
        if (board.Revision == c.MapRevision) return;
        board.Revision = c.MapRevision;
        foreach (var list in board.HeldBy.Values) list.Clear();
        var map = c.ProvinceMap;
        var controllers = c.ProvinceController;
        var share = Math.Clamp(c.Defines.HeldRegionControlShare, 0.01f, 1f);
        // The holder of each region: count, region by region, who controls its provinces.
        var counts = new Dictionary<int, Dictionary<ushort, int>>();
        for (var province = 1; province <= map.ProvinceCount && province < controllers.Length; province++)
        {
            if (map.IsWater(province)) continue;
            var region = map.ProvinceToState[province];
            if (!counts.TryGetValue(region, out var byController)) counts[region] = byController = new Dictionary<ushort, int>();
            byController[controllers[province]] = byController.GetValueOrDefault(controllers[province]) + 1;
        }
        foreach (var state in c.States)
        {
            var holder = -1;
            if (counts.TryGetValue(state.MapIndex, out var byController))
            {
                var all = 0;
                foreach (var count in byController.Values) all += count;
                foreach (var (controller, count) in byController)
                {
                    if (controller == state.NationMapIndex || controller == 0 || count < all * share) continue;
                    var nation = c.NationByMapIndex(controller);
                    if (nation is null || nation.Movement is not null) continue;
                    if (LandArrangementSystem.Covering(c, state.MapIndex, nation.Id) is { Kind: LandArrangementSystem.Lease }) continue;
                    holder = nation.Id;
                    break;
                }
            }
            if (holder != state.HeldById)
            {
                state.HeldById = holder;
                state.HeldSinceDay = c.ElapsedDays;
            }
            if (holder < 0) continue;
            if (!board.HeldBy.TryGetValue(holder, out var held)) board.HeldBy[holder] = held = new List<ScenarioState>();
            held.Add(state);
        }
    }

    /// <summary>Whether this region's economy answers to the country holding it rather than to its owner, today.</summary>
    public static bool Administered(Campaign c, ScenarioState state) =>
        c.Defines.HeldRegionsWorkForHolder && state.HeldById >= 0 &&
        c.ElapsedDays - state.HeldSinceDay >= Math.Max(0, c.Defines.HeldRegionSettleDays);

    /// <summary>How much of a held region's output its administration gets out of it.</summary>
    public static float Efficiency(Campaign c, ScenarioState state)
    {
        var d = c.Defines;
        var days = Math.Max(0, c.ElapsedDays - state.HeldSinceDay - Math.Max(0, d.HeldRegionSettleDays));
        var progress = Math.Clamp(days / (float)Math.Max(1, d.HeldRegionIntegrationDays), 0f, 1f);
        return d.HeldRegionEfficiencyStart + (d.HeldRegionEfficiencyFull - d.HeldRegionEfficiencyStart) * progress;
    }

    /// <summary>
    /// The regions whose economy answers to this country: its own, less those another holds,
    /// and those it holds of others. A thread-local list reused between calls - read it before asking again.
    /// </summary>
    public static List<ScenarioState> EconomyOf(Campaign c, Nation nation, bool refresh = true) =>
        FillEconomyOf(c, nation, _scratch ??= new List<ScenarioState>(), refresh);

    /// <summary>Index the committed administrations once for a whole market pass, in region order.</summary>
    internal static Dictionary<int, List<ScenarioState>> CommittedAdministrations(Campaign c)
    {
        var heldByNation = new Dictionary<int, List<ScenarioState>>();
        if (!c.Defines.HeldRegionsWorkForHolder) return heldByNation;
        foreach (var state in c.States)
        {
            if (!Administered(c, state)) continue;
            if (!heldByNation.TryGetValue(state.HeldById, out var held))
                heldByNation[state.HeldById] = held = new List<ScenarioState>();
            held.Add(state);
        }
        return heldByNation;
    }

    internal static List<ScenarioState> FillEconomyOf(Campaign c, Nation nation, List<ScenarioState> into,
        bool refresh = true, IReadOnlyDictionary<int, List<ScenarioState>> administrations = null)
    {
        into.Clear();
        if (refresh) Refresh(c);
        var working = c.Defines.HeldRegionsWorkForHolder;
        foreach (var state in c.StatesOf(nation))
            if (!working || !Administered(c, state)) into.Add(state);
        // A UI projection reads the last committed administration, not Refresh's
        // cached lists: building a tooltip must not set HeldById/HeldSinceDay.
        if (working && refresh && Boards.TryGetValue(c, out var board) &&
            board.HeldBy.TryGetValue(nation.Id, out var held))
            foreach (var state in held)
                if (Administered(c, state)) into.Add(state);
        if (working && !refresh)
        {
            if (administrations is null)
            {
                foreach (var state in c.States)
                    if (state.HeldById == nation.Id && state.NationMapIndex != nation.MapIndex && Administered(c, state)) into.Add(state);
            }
            else if (administrations.TryGetValue(nation.Id, out var committed))
            {
                foreach (var state in committed)
                    if (state.NationMapIndex != nation.MapIndex) into.Add(state);
            }
        }
        return into;
    }

    /// <summary>Why this country may not hold a vote on joining in a region it holds, or empty.</summary>
    public static string ReferendumRefusal(Campaign c, Nation holder, ScenarioState state)
    {
        if (holder is null || state is null || state.HeldById != holder.Id || !Administered(c, state))
            return Loc.Get("We do not administer this region.");
        if (c.NationByMapIndex(state.NationMapIndex) is { } owner && owner.CapitalStateMapIndex == state.MapIndex)
            return Loc.Get("A capital is not annexed by a vote.");
        var days = c.ElapsedDays - state.HeldSinceDay;
        if (days < c.Defines.HeldReferendumDays)
            return Loc.Format("Too soon: a vote can be held in {0} days.", c.Defines.HeldReferendumDays - days);
        return "";
    }

    /// <summary>
    /// The vote under the troops in a held region, and the annexation it was held for - the
    /// last step from occupation to ownership short of a treaty. The war goes on; the owner does
    /// not recognise it; the world counts it against the holder.
    /// </summary>
    public static bool HoldReferendum(Campaign c, Nation holder, ScenarioState state, out string result)
    {
        result = ReferendumRefusal(c, holder, state);
        if (result.Length > 0) return false;
        var owner = c.NationByMapIndex(state.NationMapIndex);
        var map = c.ProvinceMap;
        if (owner is null || map is null) return false;
        var kin = WarAims.CultureShare(state, WarAims.Cultures(holder));
        var declared = M.Clamp(kin + 0.4f + c.Random.NextFloat() * 0.08f, 0.6f, 0.99f);
        var provinces = new List<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (!map.IsWater(province) && map.ProvinceToState[province] == state.MapIndex && map.ProvinceToNation[province] == owner.MapIndex)
                provinces.Add(province);
        StatePartition.Annex(c, provinces, holder, asConquest: true);
        holder.Notoriety += c.Defines.PresenceNotoriety;
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, holder.Id, owner.Id), "annexed_by_referendum");
        state.HeldById = -1;
        var share = $"{declared * 100f:0.0} %";
        c.Chronicle.Record(c, "annexed_by_referendum", holder.Id, owner.Id, subject: share, place: state.DisplayName);
        c.MarkMapChanged();
        result = Loc.Format("{0} votes to join us: {1} in favour, by our count.", state.DisplayName, share);
        return true;
    }

    /// <summary>The computer: an unrestrained government a year into holding a region holds its vote now and then.</summary>
    public static void AiMonth(Campaign c)
    {
        if (c.ElapsedDays % 28 != 12 || !c.Defines.HeldRegionsWorkForHolder) return;
        var d = c.Defines;
        // A copy: an annexation may split a region and change the list being walked.
        foreach (var state in c.States.ToArray())
        {
            if (state.HeldById < 0 || c.ElapsedDays - state.HeldSinceDay < d.AiHeldReferendumDays) continue;
            var holder = c.NationById(state.HeldById);
            if (holder is null || holder.IsHuman || AiStatecraft.Restraint(c, holder) > d.AiPresenceRestraintMax) continue;
            if (c.Random.NextFloat() >= d.AiHeldReferendumChance) continue;
            HoldReferendum(c, holder, state, out _);
        }
    }

    /// <summary>The factor a region's output is worked at for this country: one for its own, the administration's for one it holds.</summary>
    public static float WorkedAt(Campaign c, Nation nation, ScenarioState state)
    {
        if (state.NationMapIndex == nation.MapIndex) return 1f;
        // And at what the region's work is worth where it is, not where its holder is: GDP is
        // gross output times the holder's calibration, and Russia's is worth several times
        // Ukraine's, so a held Ukrainian region counted at Russia's scale tripled what it made -
        // Russia's GDP rose from 1.8 to 3.2 trillion on taking a country of 0.15.
        var owner = c.NationByMapIndex(state.NationMapIndex);
        var scale = owner is null || owner.OutputScale <= 0f || nation.OutputScale <= 0f
            ? 1f : Math.Clamp(owner.OutputScale / nation.OutputScale, 0.05f, 4f);
        return Efficiency(c, state) * scale;
    }
}
