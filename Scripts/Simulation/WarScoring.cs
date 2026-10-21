using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class StrategicWar
{
    /// <summary>The share of the defending side's worth the attackers stand on, and the other way round.</summary>
    public float DefenderOccupiedValue;
    public float AttackerOccupiedValue;

    /// <summary>How much of each leader's capital region the other side holds.</summary>
    public float CapitalScore;

    /// <summary>What is left of each side's field army against what it went to war with.</summary>
    public float ArmyScore;
    public float AttackerStartStrength;
    public float DefenderStartStrength;
    public float AttackerStrengthLeft = 1f;
    public float DefenderStrengthLeft = 1f;

    /// <summary>
    /// The war's momentum: a point a month to whoever holds the war goal, and to the defender
    /// once the attacker has held nothing of it for half a year. Kept between the bounds.
    /// </summary>
    public float TickingScore;
    public int LastTickDay = -1;

    /// <summary>+1 when the defending side has collapsed, -1 when the attacking side has, 0 otherwise.</summary>
    public int Collapsed;
}

public sealed partial class Defines
{
    public float WarScoreOccupation { get; set; } = 50f;
    public float WarScoreCapital { get; set; } = 12f;
    public float WarScoreArmies { get; set; } = 15f;
    public float WarScoreBattles { get; set; } = 12f;
    public float WarScoreObjectiveMin { get; set; } = -10f;
    public float WarScoreObjectiveMax { get; set; } = 20f;
    public float WarScoreHomeFront { get; set; } = 10f;
    public float WarScoreTickingPerMonth { get; set; } = 1f;
    public float WarScoreTickingMax { get; set; } = 15f;
    public int WarScoreTickingGraceDays { get; set; } = 180;

    /// <summary>A side whose worth is occupied at least this far has collapsed.</summary>
    public float WarScoreCollapseOccupation { get; set; } = 0.9f;

    /// <summary>Or occupied this far, with no more than <see cref="WarScoreCollapseArmy"/> of its army left.</summary>
    public float WarScoreCollapseOccupationBroken { get; set; } = 0.6f;

    public float WarScoreCollapseArmy { get; set; } = 0.15f;

    /// <summary>How a region's worth is split between its people and its industry.</summary>
    public float WarScorePeopleWeight { get; set; } = 0.5f;

    /// <summary>The public floor and ceiling of a war score, the same shape players know from other games.</summary>
    public float WarScoreMin { get; set; } = -100f;
    public float WarScoreMax { get; set; } = 100f;
}

/// <summary>
/// The war score, from everything a war is decided by.
///
/// It used to be the share of provinces taken (up to 65), the casualty ratio (25), the war goal
/// and home support: every province counted the same, a capital was a province, and a country
/// that had lost all its ground and most of its army was 71 points down rather than beaten. Now
/// it is seven parts, each worked out for both sides and netted, each one a define in
/// common/defines/16_war_score.json:
///
/// - occupation, by worth: a region counts by its people and its industry, not its province
///   count, so Donetsk outweighs a stretch of steppe, and in a coalition each member counts by
///   its GDP;
/// - the capitals: how much of each leader's capital region the other side holds;
/// - the armies: what is left of each side's field army against what it went to war with;
/// - the battles: the casualty ratio;
/// - the war goal: whoever holds the region the war was declared for;
/// - the home front: the two sides' war support;
/// - momentum: a point a month to whoever holds the war goal, and to the defender once the
///   attacker has held nothing of it for half a year, as in Europa Universalis.
///
/// A side whose worth is nearly all occupied - or much of it, with its army gone - has
/// collapsed, and the score goes to a hundred: the country that took all of Ukraine is not
/// seventy-one points ahead, it has won.
/// </summary>
public static class WarScoring
{
    private static readonly Dictionary<int, float> ValuePerProvince = new();
    private static readonly Dictionary<int, int> ProvincesOf = new();
    private static readonly Dictionary<ushort, int> SideOf = new();
    private static readonly Dictionary<ushort, float> Occupied = new();
    private static readonly Dictionary<ushort, float> Worth = new();

    public static void Update(Campaign campaign, StrategicWar war)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var attacker = campaign.NationById(war.AttackerId);
        var defender = campaign.NationById(war.DefenderId);
        if (topology is null || controllers is null || attacker is null || defender is null) return;
        var defines = campaign.Defines;

        // ---- occupation, by worth ------------------------------------------------------
        SideOf.Clear();
        foreach (var id in war.Side(true)) if (campaign.NationById(id) is { } n) SideOf[(ushort)n.MapIndex] = 1;
        foreach (var id in war.Side(false)) if (campaign.NationById(id) is { } n) SideOf[(ushort)n.MapIndex] = -1;

        // What each member's regions are worth to it, per province.
        ProvincesOf.Clear();
        for (var province = 1; province <= topology.ProvinceCount && province < controllers.Length; province++)
        {
            if (!SideOf.ContainsKey(topology.ProvinceToNation[province])) continue;
            var region = topology.ProvinceToState[province];
            ProvincesOf[region] = ProvincesOf.GetValueOrDefault(region) + 1;
        }
        ValuePerProvince.Clear();
        foreach (var (map, _) in SideOf)
        {
            var member = campaign.Nations.Find(nation => nation.MapIndex == map);
            if (member is null) continue;
            var states = campaign.StatesOf(member);
            long people = 0;
            var industry = 0;
            foreach (var state in states)
            {
                people += Math.Max(0, state.Population);
                foreach (var count in state.Buildings.Values) industry += Math.Max(0, count);
            }
            var peopleWeight = industry > 0 ? M.Clamp01(defines.WarScorePeopleWeight) : 1f;
            foreach (var state in states)
            {
                var provinces = ProvincesOf.GetValueOrDefault(state.MapIndex);
                if (provinces <= 0) continue;
                var built = 0;
                foreach (var count in state.Buildings.Values) built += Math.Max(0, count);
                var worth = (people > 0 ? peopleWeight * state.Population / (float)people : 0f) +
                            (industry > 0 ? (1f - peopleWeight) * built / (float)industry : 0f);
                ValuePerProvince[state.MapIndex] = worth / provinces;
            }
        }

        Occupied.Clear();
        Worth.Clear();
        for (var province = 1; province <= topology.ProvinceCount && province < controllers.Length; province++)
        {
            var owner = topology.ProvinceToNation[province];
            if (!SideOf.TryGetValue(owner, out var side)) continue;
            var value = ValuePerProvince.GetValueOrDefault(topology.ProvinceToState[province]);
            Worth[owner] = Worth.GetValueOrDefault(owner) + value;
            var holder = controllers[province];
            if (holder != owner && SideOf.TryGetValue(holder, out var holderSide) && holderSide != side)
                Occupied[owner] = Occupied.GetValueOrDefault(owner) + value;
        }
        war.DefenderOccupiedValue = SideShare(campaign, war.Side(false));
        war.AttackerOccupiedValue = SideShare(campaign, war.Side(true));
        war.OccupationScore = (war.DefenderOccupiedValue - war.AttackerOccupiedValue) * defines.WarScoreOccupation;

        // ---- the capitals ---------------------------------------------------------------
        war.CapitalScore = (CapitalHeld(campaign, defender, SideOf, 1) - CapitalHeld(campaign, attacker, SideOf, -1)) *
                           defines.WarScoreCapital;

        // ---- the armies -----------------------------------------------------------------
        var attackerStrength = FieldStrength(campaign, war.Side(true));
        var defenderStrength = FieldStrength(campaign, war.Side(false));
        if (war.AttackerStartStrength <= 0f) war.AttackerStartStrength = MathF.Max(1f, attackerStrength);
        if (war.DefenderStartStrength <= 0f) war.DefenderStartStrength = MathF.Max(1f, defenderStrength);
        war.AttackerStrengthLeft = M.Clamp01(attackerStrength / war.AttackerStartStrength);
        war.DefenderStrengthLeft = M.Clamp01(defenderStrength / war.DefenderStartStrength);
        war.ArmyScore = (war.AttackerStrengthLeft - war.DefenderStrengthLeft) * defines.WarScoreArmies;

        // ---- the battles ----------------------------------------------------------------
        var totalLosses = war.AttackerLosses + war.DefenderLosses;
        war.BattleScore = totalLosses < 1f ? 0f :
            (war.DefenderLosses - war.AttackerLosses) / totalLosses * defines.WarScoreBattles;

        // ---- the war goal and the momentum -----------------------------------------------
        war.ObjectiveScore = 0f;
        var goalHeld = -1f;
        if (war.ClaimedStateMapIndex >= 0)
        {
            var total = 0;
            var held = 0;
            for (var province = 1; province <= topology.ProvinceCount && province < controllers.Length; province++)
            {
                if (topology.ProvinceToState[province] != war.ClaimedStateMapIndex) continue;
                total++;
                if (SideOf.TryGetValue(controllers[province], out var side) && side > 0) held++;
            }
            if (total > 0)
            {
                goalHeld = held / (float)total;
                war.ObjectiveScore = M.Lerp(defines.WarScoreObjectiveMin, defines.WarScoreObjectiveMax, goalHeld);
            }
        }
        Tick(campaign, war, goalHeld);

        // ---- the home front ---------------------------------------------------------------
        war.SupportScore = (war.AttackerWarSupport - war.DefenderWarSupport) * defines.WarScoreHomeFront;

        var score = war.OccupationScore + war.CapitalScore + war.ArmyScore + war.BattleScore +
                    war.ObjectiveScore + war.TickingScore + war.SupportScore;

        // ---- collapse ---------------------------------------------------------------------
        war.Collapsed = HasCollapsed(defines, war.DefenderOccupiedValue, war.DefenderStrengthLeft) ? 1 :
                        HasCollapsed(defines, war.AttackerOccupiedValue, war.AttackerStrengthLeft) ? -1 : 0;
        // A collapse is the ceiling or the floor, whichever the campaign declares. Ordinary
        // scores stay inside the same bounds, so a peace budget and the interface agree.
        var low = MathF.Min(defines.WarScoreMin, defines.WarScoreMax);
        var high = MathF.Max(defines.WarScoreMin, defines.WarScoreMax);
        war.WarScore = war.Collapsed > 0 ? high : war.Collapsed < 0 ? low : ClampScore(defines, score);
    }

    /// <summary>The same bound the score display and a demand budget read, exposed for the rule tests.</summary>
    public static float ClampScore(Defines defines, float score)
    {
        if (defines is null || !float.IsFinite(score)) return 0f;
        var low = MathF.Min(defines.WarScoreMin, defines.WarScoreMax);
        var high = MathF.Max(defines.WarScoreMin, defines.WarScoreMax);
        return M.Clamp(score, low, high);
    }

    /// <summary>The parts of a war's score, attacker-positive, as a localisation format and its value.</summary>
    public static IEnumerable<(string Name, float Value)> Parts(StrategicWar war)
    {
        yield return ("occupation {0}", war.OccupationScore);
        yield return ("capitals {0}", war.CapitalScore);
        yield return ("armies {0}", war.ArmyScore);
        yield return ("battles {0}", war.BattleScore);
        yield return ("objective {0}", war.ObjectiveScore);
        yield return ("momentum {0}", war.TickingScore);
        yield return ("home support {0}", war.SupportScore);
    }

    private static bool HasCollapsed(Defines defines, float occupied, float strengthLeft) =>
        occupied >= defines.WarScoreCollapseOccupation ||
        (occupied >= defines.WarScoreCollapseOccupationBroken && strengthLeft <= defines.WarScoreCollapseArmy);

    /// <summary>The share of a side's worth under the other side's flag, each member weighted by its GDP.</summary>
    private static float SideShare(Campaign campaign, IEnumerable<int> side)
    {
        var weight = 0f;
        var taken = 0f;
        foreach (var id in side)
        {
            if (campaign.NationById(id) is not { } member) continue;
            var map = (ushort)member.MapIndex;
            var worth = Worth.GetValueOrDefault(map);
            if (worth <= 0f) continue;
            var gdp = MathF.Max(0.01f, member.Gdp);
            weight += gdp;
            taken += gdp * M.Clamp01(Occupied.GetValueOrDefault(map) / worth);
        }
        return weight <= 0f ? 0f : taken / weight;
    }

    /// <summary>How much of this leader's capital region the side opposite to it holds.</summary>
    private static float CapitalHeld(Campaign campaign, Nation leader, Dictionary<ushort, int> sides, int enemySide)
    {
        if (leader is null || leader.CapitalStateMapIndex <= 0) return 0f;
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var total = 0;
        var held = 0;
        for (var province = 1; province <= topology.ProvinceCount && province < controllers.Length; province++)
        {
            if (topology.ProvinceToState[province] != leader.CapitalStateMapIndex) continue;
            total++;
            if (sides.TryGetValue(controllers[province], out var side) && side == enemySide) held++;
        }
        return total == 0 ? 0f : held / (float)total;
    }

    /// <summary>A side's field army: divisions times their strength, militia left out.</summary>
    private static float FieldStrength(Campaign campaign, IEnumerable<int> side)
    {
        var members = new HashSet<int>(side);
        var total = 0f;
        foreach (var army in campaign.Armies)
            if (members.Contains(army.NationId) && army.TemplateId != MilitiaCore.TemplateId)
                total += MathF.Max(0f, army.Divisions) * M.Clamp01(army.Strength);
        return total;
    }

    /// <summary>A point a month to whoever holds the war goal, and to the defender once the attacker has held nothing for long.</summary>
    private static void Tick(Campaign campaign, StrategicWar war, float goalHeld)
    {
        var defines = campaign.Defines;
        if (war.LastTickDay < 0) { war.LastTickDay = campaign.ElapsedDays; return; }
        var months = (campaign.ElapsedDays - war.LastTickDay) / 30;
        if (months <= 0) return;
        war.LastTickDay += months * 30;
        var step = 0f;
        if (goalHeld >= 0.5f) step = defines.WarScoreTickingPerMonth;
        else if (goalHeld >= 0f && goalHeld < 0.1f && war.DefenderOccupiedValue < 0.02f &&
                 campaign.ElapsedDays - war.StartedDay >= defines.WarScoreTickingGraceDays)
            step = -defines.WarScoreTickingPerMonth;
        else if (goalHeld < 0f && campaign.ElapsedDays - war.StartedDay >= defines.WarScoreTickingGraceDays)
            step = war.DefenderOccupiedValue < 0.02f ? -defines.WarScoreTickingPerMonth :
                   war.DefenderOccupiedValue > 0.25f ? defines.WarScoreTickingPerMonth : 0f;
        war.TickingScore = M.Clamp(war.TickingScore + step * months, -defines.WarScoreTickingMax, defines.WarScoreTickingMax);
    }
}
