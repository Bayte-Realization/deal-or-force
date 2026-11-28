using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// Allied wartime occupation transfer and request: allows war allies fighting on the same side
/// against a common enemy to transfer occupied provinces in a region between each other, or to
/// request occupation from an AI war ally based on deterministic territorial and diplomatic rules.
/// </summary>
public static class OccupationTransfer
{
    /// <summary>
    /// True unless a scenario/mod explicitly disabled <c>warfare</c> in feature overrides or defines.
    /// </summary>
    public static bool IsEnabled(Campaign campaign)
    {
        if (campaign is null) return false;
        if (campaign.FeatureOverrides.TryGetValue("warfare", out var ov)) return ov;
        if (campaign.Defines.Features is not null &&
            campaign.Defines.Features.TryGetValue("warfare", out var def)) return def;
        return true;
    }

    /// <summary>
    /// Returns an empty string when <paramref name="from"/> may transfer its wartime occupation
    /// of enemy-owned provinces in <paramref name="state"/> to <paramref name="to"/>; otherwise
    /// returns a localised refusal reason.
    /// </summary>
    public static string TransferRefusal(Campaign campaign, Nation from, Nation to, ScenarioState state)
    {
        if (campaign is null || from is null || to is null || state is null)
            return Loc.Get("shell.reg_act.occ_invalid");
        if (!IsEnabled(campaign))
            return Loc.Get("shell.reg_act.occ_disabled");
        if (from.Id == to.Id)
            return Loc.Get("shell.reg_act.occ_same_nation");
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null)
            return Loc.Get("shell.reg_act.occ_no_map");
        if (from.Movement is { Landless: true } || to.Movement is { Landless: true })
            return Loc.Get("shell.reg_act.occ_landless");

        var occupiedByFrom = 0;
        var alliedTransferable = 0;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var provinces = map.ProvincesOfState(state.MapIndex);
        for (var i = 0; i < provinces.Length; i++)
        {
            var p = provinces[i];
            if (p <= 0 || p >= controllers.Length || map.IsWater(p)) continue;
            var ownerMapIdx = map.ProvinceToNation[p];
            if (ownerMapIdx == 0 || ownerMapIdx == from.MapIndex) continue;
            if (controllers[p] != from.MapIndex) continue;
            occupiedByFrom++;
            if (AreWarAlliesAgainstOwner(campaign, from, to, ownerMapIdx))
                alliedTransferable++;
        }

        if (occupiedByFrom == 0)
            return Loc.Get("shell.reg_act.occ_not_occupied");
        if (alliedTransferable == 0)
            return Loc.Get("shell.reg_act.occ_not_allies");
        return "";
    }

    /// <summary>
    /// Transfers wartime occupation of every eligible enemy-owned province in <paramref name="state"/>
    /// controlled by <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public static bool Transfer(Campaign campaign, Nation from, Nation to, ScenarioState state)
    {
        if (TransferRefusal(campaign, from, to, state).Length > 0) return false;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var occupations = campaign.ProvinceOccupation;
        var secureThreshold = Math.Clamp(campaign.Defines.OccupationSecureThreshold, 0.05f, 1f);
        var provinces = map.ProvincesOfState(state.MapIndex);
        var transferred = 0;

        for (var i = 0; i < provinces.Length; i++)
        {
            var p = provinces[i];
            if (p <= 0 || p >= controllers.Length || map.IsWater(p)) continue;
            var ownerMapIdx = map.ProvinceToNation[p];
            if (ownerMapIdx == 0 || ownerMapIdx == from.MapIndex) continue;
            if (controllers[p] != from.MapIndex) continue;
            if (!AreWarAlliesAgainstOwner(campaign, from, to, ownerMapIdx)) continue;

            controllers[p] = (ushort)to.MapIndex;
            if (occupations is not null && p < occupations.Length && occupations[p] < secureThreshold)
                occupations[p] = secureThreshold;
            transferred++;
        }

        if (transferred == 0) return false;

        if (state.HeldById == from.Id)
        {
            state.HeldById = to.Id;
            state.HeldSinceDay = campaign.ElapsedDays;
        }

        campaign.MarkMapChanged();
        campaign.MarkStrategicChange();
        HeldRegions.Refresh(campaign);
        campaign.Chronicle.Record(campaign, "occupation_transferred", from.Id, to.Id, place: state.DisplayName);
        return true;
    }

    /// <summary>
    /// Deterministically lists war allies eligible to receive <paramref name="from"/>'s occupation
    /// of <paramref name="state"/>, ordered by nation id.
    /// </summary>
    public static List<Nation> WarAlliesForTransfer(Campaign campaign, Nation from, ScenarioState state)
    {
        var result = new List<Nation>();
        if (campaign is null || from is null || state is null || !IsEnabled(campaign))
            return result;

        var seen = new HashSet<int>();
        for (var i = 0; i < campaign.Nations.Count; i++)
        {
            var ally = campaign.Nations[i];
            if (ally is null || ally.Id == from.Id || !seen.Add(ally.Id)) continue;
            if (TransferRefusal(campaign, from, ally, state).Length == 0)
                result.Add(ally);
        }
        result.Sort((a, b) => a.Id.CompareTo(b.Id));
        return result;
    }

    /// <summary>
    /// Checks whether <paramref name="requester"/> may request occupation of <paramref name="state"/>
    /// from a war ally currently holding enemy-owned provinces there. Outputs the primary allied
    /// <paramref name="holder"/> when one exists.
    /// </summary>
    public static string RequestRefusal(Campaign campaign, Nation requester, ScenarioState state, out Nation holder)
    {
        holder = null;
        if (campaign is null || requester is null || state is null)
            return Loc.Get("shell.reg_act.occ_invalid");
        if (!IsEnabled(campaign))
            return Loc.Get("shell.reg_act.occ_disabled");
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null)
            return Loc.Get("shell.reg_act.occ_no_map");
        if (requester.Movement is { Landless: true })
            return Loc.Get("shell.reg_act.occ_landless");

        holder = FindAlliedHolder(campaign, requester, state);
        if (holder is null)
            return Loc.Get("shell.reg_act.occ_no_ally_holder");
        if (holder.IsHuman)
            return Loc.Format("shell.reg_act.occ_holder_human_fmt", holder.DisplayName);
        return "";
    }

    /// <summary>
    /// Deterministic acceptance score (&gt;= 0 accepts, &lt; 0 refuses) for an AI war ally
    /// <paramref name="holder"/> deciding whether to hand occupation of <paramref name="state"/>
    /// to <paramref name="requester"/>.
    /// </summary>
    public static int RequestAcceptance(Campaign campaign, Nation requester, Nation holder, ScenarioState state)
    {
        if (campaign is null || requester is null || holder is null || state is null) return -100;
        if (holder.IsHuman) return -100;

        var reqTitle = TerritorialClaims.TitleOf(campaign, requester, state);
        var holdTitle = TerritorialClaims.TitleOf(campaign, holder, state);
        if (holdTitle == TerritorialTitle.Homeland && reqTitle != TerritorialTitle.Homeland)
            return -100;

        var score = 0;
        if (reqTitle == TerritorialTitle.Homeland) score += 60;
        else if (reqTitle == TerritorialTitle.Claim) score += 35;

        if (IsAdjacentToState(campaign, requester, state))
            score += 20;

        var opinion = CampaignDiplomacy.Peek(campaign, holder.Id, requester.Id)?.Opinion ?? 0f;
        if (opinion >= -25f)
            score += 15 + (int)MathF.Round(Math.Clamp(opinion, -25f, 100f) * 0.25f);
        else
            score -= 20;

        var (reqOcc, holdOcc) = CountSharedWarOccupations(campaign, requester, holder);
        if (reqOcc > holdOcc || (reqOcc == holdOcc && requester.Gdp >= holder.Gdp))
            score += 10;

        return score;
    }

    /// <summary>
    /// Evaluates and executes an occupation request by <paramref name="requester"/> for
    /// <paramref name="state"/>. On acceptance, transfers occupation immediately.
    /// </summary>
    public static bool Request(Campaign campaign, Nation requester, ScenarioState state, out string refusal)
    {
        refusal = RequestRefusal(campaign, requester, state, out var holder);
        if (refusal.Length > 0) return false;

        var score = RequestAcceptance(campaign, requester, holder, state);
        if (score < 0)
        {
            var reqTitle = TerritorialClaims.TitleOf(campaign, requester, state);
            var holdTitle = TerritorialClaims.TitleOf(campaign, holder, state);
            refusal = holdTitle == TerritorialTitle.Homeland && reqTitle != TerritorialTitle.Homeland
                ? Loc.Format("shell.reg_act.occ_refused_homeland_fmt", holder.DisplayName)
                : Loc.Format("shell.reg_act.occ_refused_ally_fmt", holder.DisplayName);
            return false;
        }

        return Transfer(campaign, holder, requester, state);
    }

    private static Nation FindAlliedHolder(Campaign campaign, Nation requester, ScenarioState state)
    {
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        var provinces = map.ProvincesOfState(state.MapIndex);
        var counts = new Dictionary<int, int>();

        for (var i = 0; i < provinces.Length; i++)
        {
            var p = provinces[i];
            if (p <= 0 || p >= controllers.Length || map.IsWater(p)) continue;
            var ownerMapIdx = map.ProvinceToNation[p];
            var ctrlMapIdx = controllers[p];
            if (ownerMapIdx == 0 || ctrlMapIdx == 0 || ctrlMapIdx == ownerMapIdx || ctrlMapIdx == requester.MapIndex)
                continue;
            var candidate = campaign.NationByMapIndex(ctrlMapIdx);
            if (candidate is null || candidate.Id == requester.Id || candidate.Movement is { Landless: true })
                continue;
            if (!AreWarAlliesAgainstOwner(campaign, candidate, requester, ownerMapIdx))
                continue;
            counts[candidate.Id] = counts.GetValueOrDefault(candidate.Id) + 1;
        }

        Nation best = null;
        var bestCount = 0;
        foreach (var kv in counts)
        {
            var nation = campaign.NationById(kv.Key);
            if (nation is null) continue;
            if (best is null || kv.Value > bestCount || (kv.Value == bestCount && nation.Id < best.Id))
            {
                best = nation;
                bestCount = kv.Value;
            }
        }
        return best;
    }

    private static bool AreWarAlliesAgainstOwner(Campaign campaign, Nation first, Nation second, ushort ownerMapIndex)
    {
        var owner = campaign.NationByMapIndex(ownerMapIndex);
        if (owner is null) return false;
        for (var i = 0; i < campaign.Wars.Count; i++)
        {
            var war = campaign.Wars[i];
            if (war is null || war.Ended) continue;
            if (war.Opposes(first.Id, owner.Id) && WarCoalition.SameSide(war, first.Id, second.Id))
                return true;
        }
        return false;
    }

    private static bool IsAdjacentToState(Campaign campaign, Nation nation, ScenarioState state)
    {
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null || map.Neighbors is null) return false;
        var provinces = map.ProvincesOfState(state.MapIndex);
        var targetIdx = (ushort)nation.MapIndex;
        for (var i = 0; i < provinces.Length; i++)
        {
            var p = provinces[i];
            if (p <= 0 || p >= map.Neighbors.Length || map.IsWater(p)) continue;
            var nbrs = map.Neighbors[p];
            if (nbrs is null) continue;
            for (var n = 0; n < nbrs.Length; n++)
            {
                var nb = nbrs[n];
                if (nb <= 0 || nb >= controllers.Length || map.IsWater(nb)) continue;
                if (map.ProvinceToState[nb] == state.MapIndex) continue;
                if (controllers[nb] == targetIdx || map.ProvinceToNation[nb] == targetIdx)
                    return true;
            }
        }
        return false;
    }

    private static (int RequesterCount, int HolderCount) CountSharedWarOccupations(
        Campaign campaign, Nation requester, Nation holder)
    {
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null) return (0, 0);
        var reqIdx = (ushort)requester.MapIndex;
        var holdIdx = (ushort)holder.MapIndex;
        int reqCount = 0, holdCount = 0;
        for (var p = 1; p <= map.ProvinceCount && p < controllers.Length; p++)
        {
            if (map.IsWater(p)) continue;
            var ctrl = controllers[p];
            var own = map.ProvinceToNation[p];
            if (own == 0 || ctrl == own) continue;
            if (ctrl == reqIdx) reqCount++;
            else if (ctrl == holdIdx) holdCount++;
        }
        return (reqCount, holdCount);
    }
}

/// <summary>Deterministic command transferring wartime occupation of a region to a war ally.</summary>
public sealed class TransferOccupationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int RecipientId { get; init; }
    public int StateMapIndex { get; init; } = -1;
    public bool Done { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Done = false;
        Refusal = "";
        if (campaign is null) return;
        var from = campaign.NationById(IssuerId);
        var to = campaign.NationById(RecipientId);
        var state = campaign.StateByMapIndex(StateMapIndex);
        Refusal = OccupationTransfer.TransferRefusal(campaign, from, to, state);
        if (Refusal.Length > 0) return;
        Done = OccupationTransfer.Transfer(campaign, from, to, state);
    }
}

/// <summary>Deterministic command requesting wartime occupation of a region from a war ally.</summary>
public sealed class RequestOccupationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; } = -1;
    public bool Done { get; private set; }
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Done = false;
        Refusal = "";
        if (campaign is null) return;
        var requester = campaign.NationById(IssuerId);
        var state = campaign.StateByMapIndex(StateMapIndex);
        Done = OccupationTransfer.Request(campaign, requester, state, out var refusal);
        Refusal = refusal;
    }
}
