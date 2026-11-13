using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Occupation is not free.
///
/// SecureOccupation in CombatTerritory already says an occupation only grows while
/// somebody is standing there: garrison power scales the daily progress, normalised so
/// that about three power units is a real garrison. Nothing, however, made an occupation
/// cost anything once secured - a province stayed occupied forever whether or not the
/// occupier left a single soldier in it, and history has no example of that. This is the
/// other half of the same arithmetic: while a province is held against its owner, the
/// hold slips every week in proportion to how radical the people there are and how thin
/// the garrison is. The garrison scale mirrors the growth path, so the two forces meet
/// in an equilibrium rather than one swallowing the other: a calm province with a real
/// garrison consolidates, a radical province with nobody in it melts, and a frontier that
/// needs a man on every road becomes expensive - which is exactly what those frontiers
/// were. And where the hold does slip, the garrison bleeds a little for it: ambushes and
/// saboteurs at the rate an ambush war runs, on the same accounting combat uses.
///
/// Derived, never stored: the slip is computed from the occupation combat already keeps,
/// the radicalism the region already has, and the armies already standing there. No save
/// field, no fingerprint surface, nothing a save written before this existed can get
/// wrong. Off by default; the shape of a world that turns it on is measured in SimBench
/// before it becomes anybody's baseline.
/// </summary>
public static class OccupationResistance
{
    /// <summary>
    /// How much of this province's occupation slips away in one week. Never more than the
    /// occupation itself, and nothing at all when the rule is off or nothing is held.
    /// </summary>
    public static float Slip(float occupation, float radicalism, float garrisonPower, Defines defines)
    {
        if (defines is null || !(defines.OccupationResistanceEnabled || defines.OccupationResistanceInCivilWars)) return 0f;
        if (occupation <= 0f) return 0f;
        var appetite = 0.25f + Math.Clamp(radicalism, 0f, 1f) * Math.Max(0f, defines.OccupationRadicalismWeight);
        var suppression = 1f / (1f + Math.Max(0f, garrisonPower) / Math.Max(0.01f, defines.OccupationGarrisonScale));
        var slip = occupation * Math.Max(0f, defines.OccupationSlipWeekly) * appetite * suppression;
        return Math.Min(occupation, slip);
    }

    /// <summary>
    /// What a week of partisan action costs one garrison formation, in people. Full
    /// intensity is a wholly occupied, wholly radicalised province; scaled by radicalism
    /// times occupation, so a calm province breeds nobody and a lightly held corner of a
    /// furious one bleeds its garrison steadily. Never more men than the formation has.
    /// </summary>
    public static float PartisanCasualties(float personnel, float radicalism, float occupation, Defines defines)
    {
        if (defines is null || !(defines.OccupationResistanceEnabled || defines.OccupationResistanceInCivilWars)) return 0f;
        if (personnel <= 0f) return 0f;
        var intensity = Math.Clamp(radicalism, 0f, 1f) * Math.Clamp(occupation, 0f, 1f);
        return Math.Min(personnel, personnel * Math.Max(0f, defines.OccupationPartisanLossWeekly) * intensity);
    }

    /// <summary>
    /// How much of a region's resistance the occupier's own people damp: the share of its
    /// population that belongs to the occupier's primary peoples, scaled by the
    /// collaboration weight. Co-ethnics are the collaboration base - the region the
    /// occupier's people actually live in is the region that does not rise; a region with
    /// none of them resists at full voice. Derived from the demography the region already
    /// carries, like everything else here.
    /// </summary>
    public static float Collaboration(ScenarioState state, string occupierTag, Defines defines)
    {
        if (defines is null || !(defines.OccupationResistanceEnabled || defines.OccupationResistanceInCivilWars)) return 0f;
        if (state is null || state.PopulationGroups.Count == 0) return 0f;
        var own = 1f - EconomySystem.ForeignShare(state, occupierTag);
        return Math.Clamp(own, 0f, 1f) * Math.Clamp(defines.OccupationCollaborationWeight, 0f, 1f);
    }

    private static string TagOf(Campaign campaign, int mapIndex)
    {
        foreach (var nation in campaign.Nations)
            if (nation is not null && nation.MapIndex == mapIndex) return nation.Tag;
        return "";
    }

    /// <summary>
    /// What one week currently takes off this province's hold - the same number the weekly
    /// pass will act on, so the province cards can show an occupation bleeding rather than
    /// leave the player to infer it. Zero when the rule is off, nothing is held, or the
    /// owner holds it.
    /// </summary>
    public static float WeeklySlip(Campaign campaign, int province)
    {
        if (campaign is null || !campaign.Defines.OccupationResistanceEnabled) return 0f;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var occupation = campaign.ProvinceOccupation;
        if (topology is null || controllers is null || occupation is null) return 0f;
        if (province <= 0 || province >= controllers.Length || province >= occupation.Length) return 0f;
        var held = occupation[province];
        if (held <= 0f || controllers[province] == topology.ProvinceToNation[province]) return 0f;
        ScenarioState region = null;
        var stateIndex = topology.ProvinceToState[province];
        foreach (var state in campaign.States)
            if (state.MapIndex == stateIndex) { region = state; break; }
        var radicalism = region?.Radicalism ?? 0f;
        if (region is not null)
            radicalism *= 1f - Collaboration(region, TagOf(campaign, controllers[province]), campaign.Defines);
        var power = 0f;
        if (GarrisonArmies(campaign, controllers).TryGetValue(province, out var posted))
            foreach (var army in posted) power += Math.Max(0f, army.Power);
        return Slip(held, radicalism, power, campaign.Defines);
    }

    /// <summary>
    /// The weekly pass. Mirrors the ledger SecureOccupation works from - the provinces of
    /// active wars, against the controller recorded when the war began - and runs on its
    /// own weekday, so the daily growth and the weekly slip never race inside one step.
    /// Ground the owner still holds is skipped: this is the cost of holding somebody
    /// else's, not a tax on your own.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same weekly slip, yielded between wars and between the provinces a large
    /// occupation ledger covers.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || !(campaign.Defines.OccupationResistanceEnabled || campaign.Defines.OccupationResistanceInCivilWars)) yield break;
        if (campaign.ElapsedDays % 7 != 5) yield break;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var occupation = campaign.ProvinceOccupation;
        if (topology is null || controllers is null || occupation is null) yield break;

        Dictionary<int, ScenarioState> states = null;
        Dictionary<int, List<Army>> garrisons = null;
        HashSet<int> applied = null;
        // The slip follows wars still going. A war that has ended keeps its ledger and
        // does not bleed a province on the weekly pass.
        var live = Warfare.LiveWars(campaign);
        var slips = 0;
        for (var index = 0; index < live.Count; index++)
        {
            if (index % 4 == 0) yield return 0;
            var war = live[index];
            if (war is null || war.Ended) continue;
            if (!campaign.Defines.OccupationResistanceEnabled && !war.CivilConflict) continue;
            foreach (var pair in war.OriginalController)
            {
                if (++slips % 1024 == 0) yield return 0;
                var province = pair.Key;
                if (province <= 0 || province >= controllers.Length || province >= occupation.Length) continue;
                if (controllers[province] == pair.Value) continue;
                var held = occupation[province];
                if (held <= 0f) continue;
                garrisons ??= GarrisonArmies(campaign, controllers);
                states ??= StatesByMapIndex(campaign);
                var radicalism = 0f;
                ScenarioState region = null;
                if (province < topology.ProvinceToState.Length &&
                    states.TryGetValue(topology.ProvinceToState[province], out region) && region is not null)
                    radicalism = region.Radicalism;
                // The occupier's own people in the region are the collaboration base: where
                // they are numerous, resistance speaks with a smaller voice.
                if (region is not null)
                    radicalism *= 1f - Collaboration(region, TagOf(campaign, controllers[province]), campaign.Defines);
                var power = 0f;
                if (garrisons.TryGetValue(province, out var posted))
                    foreach (var army in posted) power += Math.Max(0f, army.Power);
                var slip = Slip(held, radicalism, power, campaign.Defines);
                if (slip <= 0f) continue;
                applied ??= new HashSet<int>();
                if (!applied.Add(province)) continue; // A coalition may record it in several war ledgers.
                occupation[province] = MathF.Max(0f, held - slip);
                // And the garrison bleeds where the hold is actually slipping: the same
                // accounting combat uses (CombatAttrition) - men leave the formation's
                // strength, the wounded share of them comes back through Recovering, and
                // the national reserve is untouched, because these men were counted out
                // of it the day they enlisted.
                if (posted is null) continue;
                foreach (var army in posted)
                {
                    var casualties = PartisanCasualties(army.Personnel, radicalism, held, campaign.Defines);
                    if (casualties <= 0f) continue;
                    var complement = army.Divisions * MilitaryBootstrap.DivisionSize * army.Strength;
                    if (complement > 1f) army.Strength = Math.Clamp(army.Strength - casualties / complement, 0f, 1f);
                    army.Personnel = Math.Max(0, army.Personnel - (int)MathF.Round(casualties));
                    army.Recovering += casualties * campaign.Defines.WoundedReturningShare;
                }
            }
        }
    }

    /// <summary>
    /// The formations standing in each province on behalf of whoever holds it. An army
    /// already in battle does not count - the same convention the growth path uses, where
    /// only a formation not otherwise committed secures ground, and a unit fighting for
    /// the province is busy not being ambushed.
    /// </summary>
    private static Dictionary<int, List<Army>> GarrisonArmies(Campaign campaign, ushort[] controllers)
    {
        var mapIndexOfNation = new Dictionary<int, int>();
        foreach (var nation in campaign.Nations)
            if (nation is not null && !mapIndexOfNation.ContainsKey(nation.Id)) mapIndexOfNation[nation.Id] = nation.MapIndex;
        var garrisons = new Dictionary<int, List<Army>>();
        foreach (var army in campaign.Armies)
        {
            if (army is null || army.InBattle || army.ProvinceId <= 0 || army.ProvinceId >= controllers.Length) continue;
            if (!mapIndexOfNation.TryGetValue(army.NationId, out var mapIndex)) continue;
            if (mapIndex != controllers[army.ProvinceId]) continue;
            if (!garrisons.TryGetValue(army.ProvinceId, out var posted)) garrisons[army.ProvinceId] = posted = new List<Army>();
            posted.Add(army);
        }
        return garrisons;
    }

    private static Dictionary<int, ScenarioState> StatesByMapIndex(Campaign campaign)
    {
        var states = new Dictionary<int, ScenarioState>();
        foreach (var state in campaign.States)
            if (state is not null) states[state.MapIndex] = state;
        return states;
    }
}

public sealed partial class Defines
{
    /// <summary>Whether unheld occupation slips away week by week instead of lasting forever.</summary>
    public bool OccupationResistanceEnabled { get; set; } = false;

    /// <summary>
    /// The same resistance in civil wars only, whatever OccupationResistanceEnabled says: the war
    /// of the weak is the one where a held town is not a held country (GuerrillaWar).
    /// </summary>
    public bool OccupationResistanceInCivilWars { get; set; } = true;

    /// <summary>
    /// The share of a province's occupation one week takes at baseline (calm people, no
    /// garrison). Tuned against OccupationDailyProgress = 0.012: growth without a garrison
    /// is about 0.021 a week, so a calm province still consolidates slowly, and a province
    /// whose radicalism passes roughly 0.2 melts when nobody is standing in it.
    /// </summary>
    public float OccupationSlipWeekly { get; set; } = 0.06f;

    /// <summary>How much regional radicalism (0..1) amplifies the slip, on top of the 0.25 baseline appetite.</summary>
    public float OccupationRadicalismWeight { get; set; } = 1f;

    /// <summary>
    /// Garrison power at which the slip halves. The same scale the growth path normalises
    /// by (power near 3 is a meaningful garrison there), so one formation standing in a
    /// province is what "holding" it has always meant.
    /// </summary>
    public float OccupationGarrisonScale { get; set; } = 3f;

    /// <summary>
    /// The share of a garrison formation's people one week of partisan action costs at
    /// full intensity (a wholly occupied, wholly radicalised province). Scaled by
    /// radicalism times occupation: a typical furious, firmly held province costs its
    /// garrison a few tenths of a percent a week - steady, unglamorous, and the reason
    /// rear areas need troops at all.
    /// </summary>
    public float OccupationPartisanLossWeekly { get; set; } = 0.006f;

    /// <summary>
    /// How strongly the occupier's own people in a region damp its resistance: at 1, a
    /// region entirely of the occupier's peoples offers no partisan resistance at all.
    /// </summary>
    public float OccupationCollaborationWeight { get; set; } = 0.6f;
}
