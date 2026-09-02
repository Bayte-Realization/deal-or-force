using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A peace a player writes, rather than one they pick off a menu.
///
/// The fixed terms in <see cref="PeaceTerms"/> are still there and still useful - a white
/// peace is a white peace. But every war they cannot express ends badly: a war fought for
/// two provinces has no button, and a war that took half a country has no way to keep the
/// half that matters and hand back the half that does not.
///
/// So a demand is a set of provinces the winner names, plus whatever else they want out of
/// it, and every part of it costs. War score is the budget. Taking ground you never
/// occupied costs several times what taking ground you hold does; taking the capital
/// costs more than taking a marsh; taking what you actually declared war over is cheaper
/// than taking what you did not. When the bill exceeds what the war was worth, the other
/// side says no - and says why, and by how much, so the answer is a negotiation rather
/// than a refusal.
///
/// This is the Paradox shape: a budget, priced demands, and a counterparty with a number.
/// </summary>
public sealed class PeaceDemand
{
    public int WarId = -1;
    public int ProposerId = -1;

    /// <summary>Provinces the proposer wants to keep, permanently.</summary>
    public readonly HashSet<int> Provinces = new();

    /// <summary>An indemnity scaled to the loser's economy.</summary>
    public bool Reparations;

    /// <summary>A compelled bilateral trade agreement.</summary>
    public bool TradeConcessions;

    /// <summary>The defeated armed forces give up equipment and cohesion.</summary>
    public bool Demilitarise;

    /// <summary>A public humbling: lasting war support and stability damage.</summary>
    public bool Humiliate;

    /// <summary>A long truce instead of the ordinary one.</summary>
    public bool LongTruce;

    public bool IsEmpty => Provinces.Count == 0 && !Reparations && !TradeConcessions &&
                           !Demilitarise && !Humiliate && !LongTruce;
}

/// <summary>Pricing, acceptance and resolution for a written peace.</summary>
public static class PeaceConference
{
    /// <summary>What one province costs to keep, and why it costs that.</summary>
    public readonly record struct ProvinceCost(int Province, float Cost, string Reason);

    /// <summary>
    /// The war score a side may actually spend. A war nobody is winning buys nothing;
    /// exhaustion on the other side and the sheer length of the war both buy a little
    /// more, because a country that has been bleeding for three years signs things it
    /// would not have signed in the first month.
    /// </summary>
    public static float Budget(Campaign campaign, StrategicWar war, int proposerId)
    {
        if (war is null || war.Ended || !war.Involves(proposerId)) return 0f;
        var score = proposerId == war.AttackerId ? war.WarScore : -war.WarScore;
        var opponentSupport = proposerId == war.AttackerId ? war.DefenderWarSupport : war.AttackerWarSupport;
        var exhaustion = (1f - M.Clamp01(opponentSupport)) * 30f;
        var duration = M.Clamp((campaign.ElapsedDays - war.StartedDay) / 30f, 0f, 20f);
        return M.Max(0f, score + exhaustion + duration);
    }

    /// <summary>What one province costs. Builds a ledger; prefer a ledger for many.</summary>
    public static ProvinceCost Price(Campaign campaign, StrategicWar war, int proposerId, int province) =>
        new PeaceLedger(campaign, war, proposerId).Price(province);

    /// <summary>The whole bill. Builds a ledger; prefer a ledger for repeated costing.</summary>
    public static float Cost(Campaign campaign, StrategicWar war, PeaceDemand demand) =>
        demand is null || war is null ? 0f : new PeaceLedger(campaign, war, demand.ProposerId).Cost(demand);

    /// <summary>
    /// Why the other side will not sign this, in words a player can act on. An empty
    /// string means they will.
    /// </summary>
    public static string BlockReason(Campaign campaign, StrategicWar war, PeaceDemand demand)
    {
        if (war is null || war.Ended) return "There is no war to end";
        if (demand is null || demand.IsEmpty) return "The demand is empty; offer a white peace instead";
        if (!war.Involves(demand.ProposerId)) return "You are not a belligerent in this war";

        var topology = campaign.ProvinceMap;
        if (topology is null) return "The map is not ready";

        var loser = campaign.NationById(demand.ProposerId == war.AttackerId ? war.DefenderId : war.AttackerId);
        foreach (var province in demand.Provinces)
            if (province <= 0 || province > topology.ProvinceCount ||
                topology.ProvinceToNation[province] != (ushort)(loser?.MapIndex ?? -1))
                return "The demand includes ground that is not theirs to give";

        var ledger = new PeaceLedger(campaign, war, demand.ProposerId);
        var budget = ledger.Budget;
        var cost = ledger.Cost(demand);
        if (cost > budget)
            return $"They will not sign: the demand is worth {cost:0} and the war is worth {budget:0}";
        return "";
    }

    /// <summary>
    /// Sign it. The provinces change owner through the ordinary annexation path, so every
    /// region the new border cuts is split exactly as it would be by any other border
    /// change - a written peace is not a special case of the map.
    /// </summary>
    public static bool Resolve(Campaign campaign, StrategicWar war, PeaceDemand demand)
    {
        var block = BlockReason(campaign, war, demand);
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        war.LastNegotiation = block.Length == 0 ? "Agreement reached" : block;
        if (block.Length > 0) return false;

        var winner = campaign.NationById(demand.ProposerId);
        var loser = campaign.NationById(demand.ProposerId == war.AttackerId ? war.DefenderId : war.AttackerId);
        if (winner is null || loser is null) return false;

        // Anything not being kept goes back to whoever held it when the war began.
        if (campaign.ProvinceController is not null)
            foreach (var pair in war.OriginalController)
                if (!demand.Provinces.Contains(pair.Key))
                    campaign.SetProvinceController(pair.Key, pair.Value);

        if (demand.Provinces.Count > 0)
        {
            var created = StatePartition.Annex(campaign, demand.Provinces, winner);
            war.AnnexedProvinces = demand.Provinces.Count;
            war.CreatedStates = created.Count;

            // Losing ground is not a one-day event. The country goes on naming the land
            // for years, which is quieter than a humiliation and lasts longer.
            ModifierSystem.Grant(campaign, loser, "ceded_territory", 0);
        }

        if (demand.Reparations || demand.TradeConcessions)
        {
            var share = demand.Reparations ? 0.03f : 0.015f;
            var payment = MathF.Min(MathF.Max(0f, loser.Treasury), MathF.Max(0.10f, loser.Gdp * share));
            loser.Treasury -= payment;
            winner.Treasury += payment;
            war.Reparations = payment;
        }

        if (demand.TradeConcessions)
            CampaignDiplomacy.Get(campaign, war.AttackerId, war.DefenderId).TradeAgreement = true;

        if (demand.Demilitarise)
            foreach (var division in campaign.Armies.Where(division => division.NationId == loser.Id))
            {
                division.Equipment = MathF.Min(division.Equipment, 0.45f);
                division.Cohesion = MathF.Min(division.Cohesion, 0.55f);
                division.Strength = MathF.Min(division.Strength, 0.72f);
            }

        if (demand.Humiliate)
        {
            loser.WarSupport = M.Clamp01(loser.WarSupport - 0.18f);
            loser.Stability = M.Clamp01(loser.Stability - 0.10f);
            winner.WarSupport = M.Clamp01(winner.WarSupport + 0.08f);
            ModifierSystem.Grant(campaign, loser, "humiliated", 365 * 3);
        }

        war.Ended = true;
        war.EndedDay = campaign.ElapsedDays;
        war.WinnerId = winner.Id;
        war.PeaceSummary = Summary(campaign, war, demand, winner, loser);
        war.AttackerFront.Clear();
        war.DefenderFront.Clear();
        war.UnopposedProgress.Clear();

        var relation = CampaignDiplomacy.Get(campaign, war.AttackerId, war.DefenderId);
        relation.Stance = DiplomaticStance.Truce;
        relation.TruceUntil = campaign.ElapsedDays + (demand.LongTruce ? 365 * 10 : 365 * 5);
        relation.LastActionDay = campaign.ElapsedDays;
        relation.Opinion = MathF.Min(relation.Opinion, demand.Provinces.Count > 0 ? -60f : -35f);

        foreach (var army in campaign.Armies.Where(army => army.AssignedWarId == war.Id))
        {
            army.AssignedWarId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.MoveProgress = 0f;
        }
        foreach (var battle in campaign.Battles.Where(battle => battle.WarId == war.Id && !battle.Ended))
        {
            battle.Ended = true;
            battle.Outcome = "Peace agreement";
        }

        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>Every province the loser still owns, for a conference to paint over.</summary>
    public static List<int> Claimable(Campaign campaign, StrategicWar war, int proposerId)
    {
        var topology = campaign.ProvinceMap;
        var loser = campaign.NationById(proposerId == war.AttackerId ? war.DefenderId : war.AttackerId);
        var result = new List<int>();
        if (topology is null || loser is null) return result;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToNation[province] == (ushort)loser.MapIndex)
                result.Add(province);
        return result;
    }

    public static List<int> ProvincesOfState(ProvinceTopology topology, int stateMapIndex)
    {
        var result = new List<int>();
        if (topology is null) return result;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToState[province] == (ushort)stateMapIndex)
                result.Add(province);
        return result;
    }

    private static string Summary(Campaign campaign, StrategicWar war, PeaceDemand demand,
        Nation winner, Nation loser)
    {
        var parts = new List<string>();
        if (demand.Provinces.Count > 0)
        {
            var states = demand.Provinces
                .Select(province => campaign.ProvinceMap.ProvinceToState[province])
                .Distinct()
                .Select(index => campaign.States.Find(state => state.MapIndex == index)?.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .Take(4)
                .ToList();
            var named = states.Count == 0 ? "territory" : string.Join(", ", states);
            parts.Add($"{winner.Name} took {demand.Provinces.Count} province(s) in {named}" +
                      (war.CreatedStates > 0 ? $", splitting {war.CreatedStates} region(s)" : ""));
        }
        if (demand.Reparations) parts.Add($"${war.Reparations:0.00}B in reparations changed hands");
        if (demand.TradeConcessions) parts.Add("a trade agreement was imposed");
        if (demand.Demilitarise) parts.Add($"the {loser.Name} armed forces were partially demobilised");
        if (demand.Humiliate) parts.Add($"{loser.Name} was publicly humbled");
        if (demand.LongTruce) parts.Add("a ten-year truce was signed");
        return parts.Count == 0
            ? $"{winner.Name} and {loser.Name} made peace on no terms at all."
            : char.ToUpperInvariant(parts[0][0]) + parts[0][1..] +
              (parts.Count > 1 ? "; " + string.Join("; ", parts.Skip(1)) : "") + ".";
    }
}

/// <summary>
/// Everything a conference needs to price a demand, worked out once.
///
/// A brush stroke re-prices the whole demand, and the demand can be hundreds of provinces
/// wide, so the parts that do not change between strokes - which region each province is
/// in, how many provinces a region has, where the loser governs from - are found once when
/// the conference opens and read from there. Pricing a province is then a handful of
/// lookups rather than a walk over the whole map.
/// </summary>
public sealed class PeaceLedger
{
    private readonly Campaign _campaign;
    private readonly StrategicWar _war;
    private readonly Nation _proposer;
    private readonly Nation _loser;
    private readonly Dictionary<int, int> _provincesPerState = new();
    private readonly Dictionary<int, long> _statePopulation = new();
    private readonly int _capitalStateMapIndex = -1;

    public PeaceLedger(Campaign campaign, StrategicWar war, int proposerId)
    {
        _campaign = campaign;
        _war = war;
        _proposer = campaign.NationById(proposerId);
        _loser = war is null ? null : campaign.NationById(
            proposerId == war.AttackerId ? war.DefenderId : war.AttackerId);
        Budget = PeaceConference.Budget(campaign, war, proposerId);

        var topology = campaign.ProvinceMap;
        if (topology is null || _loser is null) return;

        var capital = -1;
        var capitalDistance = float.MaxValue;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToNation[province] != (ushort)_loser.MapIndex) continue;
            var state = topology.ProvinceToState[province];
            _provincesPerState[state] = _provincesPerState.GetValueOrDefault(state) + 1;

            var centre = topology.CenterOf(province);
            var latitude = centre.Latitude - _loser.CapitalLatitude;
            var longitude = centre.Longitude - _loser.CapitalLongitude;
            var distance = latitude * latitude + longitude * longitude;
            if (distance >= capitalDistance) continue;
            capitalDistance = distance;
            capital = province;
        }
        if (capital > 0) _capitalStateMapIndex = topology.ProvinceToState[capital];

        foreach (var state in campaign.States)
            if (_provincesPerState.ContainsKey(state.MapIndex))
                _statePopulation[state.MapIndex] = state.Population;
    }

    /// <summary>War score this side may spend on the whole demand.</summary>
    public float Budget { get; }

    public Nation Loser => _loser;

    /// <summary>Provinces the loser still owns - the ground a conference may paint over.</summary>
    public IReadOnlyDictionary<int, int> ProvincesPerState => _provincesPerState;

    public PeaceConference.ProvinceCost Price(int province)
    {
        var topology = _campaign.ProvinceMap;
        if (topology is null || _proposer is null || _loser is null ||
            province <= 0 || province > topology.ProvinceCount)
            return new PeaceConference.ProvinceCost(province, 0f, "not a province");
        if (topology.ProvinceToNation[province] != (ushort)_loser.MapIndex)
            return new PeaceConference.ProvinceCost(province, 0f, "not theirs to give");

        var defines = _campaign.Defines;
        var cost = defines.PeaceCostPerProvince;
        string reason;

        var held = _campaign.ProvinceController is not null &&
                   _campaign.ProvinceController[province] == (ushort)_proposer.MapIndex;
        var secured = _campaign.ProvinceOccupation is not null &&
                      _campaign.ProvinceOccupation[province] >= defines.OccupationSecureThreshold;
        if (held && secured) { cost *= defines.PeaceCostSecuredFactor; reason = "occupied and secure"; }
        else if (held) { cost *= defines.PeaceCostHeldFactor; reason = "occupied but not secure"; }
        else { cost *= defines.PeaceCostUnheldFactor; reason = "never taken"; }

        var stateMapIndex = topology.ProvinceToState[province];
        if (_war.ClaimedStateMapIndex >= 0 && stateMapIndex == _war.ClaimedStateMapIndex)
        {
            cost *= defines.PeaceCostWarGoalFactor;
            reason += ", inside the declared war goal";
        }

        if (_provincesPerState.TryGetValue(stateMapIndex, out var provinces) && provinces > 0 &&
            _statePopulation.TryGetValue(stateMapIndex, out var population))
        {
            var share = population / (float)provinces;
            cost *= 1f + M.Clamp(share / M.Max(1f, defines.PeaceCostPopulationScale), 0f, 2.5f);
        }

        if (stateMapIndex == _capitalStateMapIndex)
        {
            cost *= defines.PeaceCostCapitalFactor;
            reason += ", the seat of government";
        }

        return new PeaceConference.ProvinceCost(province, cost, reason);
    }

    public float Cost(PeaceDemand demand)
    {
        if (demand is null) return 0f;
        var defines = _campaign.Defines;
        var total = 0f;
        foreach (var province in demand.Provinces) total += Price(province).Cost;
        if (demand.Reparations) total += defines.PeaceCostReparations;
        if (demand.TradeConcessions) total += defines.PeaceCostTradeConcessions;
        if (demand.Demilitarise) total += defines.PeaceCostDemilitarise;
        if (demand.Humiliate) total += defines.PeaceCostHumiliate;
        if (demand.LongTruce) total += defines.PeaceCostLongTruce;
        return total;
    }
}
