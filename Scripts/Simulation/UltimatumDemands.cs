using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int UltimatumMaxRegions { get; set; } = 8;
    public float UltimatumMaxTreasuryShare { get; set; } = .5f;
    public int UltimatumRefusalTruceDays { get; set; } = 3;
    /// <summary>Days a player has to answer an ultimatum. Silence refuses it. Nothing is handed over.</summary>
    public int UltimatumAnswerDays { get; set; } = 21;
    public int UltimatumHistoryLimit { get; set; } = 32;
}

public static partial class Ultimatum
{
    public sealed record Demand
    {
        public int[] Regions { get; init; } = Array.Empty<int>();
        // The campaign's money unit is billions, not raw currency units.
        public float Reparations { get; init; }
        public bool LiftEmbargo { get; init; }
        public bool NonAggression { get; init; }
    }

    public sealed record Assessment(bool Valid, string Reason, Answer Response, float StrengthRatio, float TerritoryShare);

    public static int[] RegionIds(Demand demand) => (demand?.Regions ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray();

    public static Assessment Assess(Campaign c, Nation demander, Nation target, Demand demand)
    {
        if (!Validate(c, demander, target, demand, out var reason)) return new(false, reason, Answer.Refuse, 0, 0);
        var ids = RegionIds(demand).ToHashSet();
        var population = c.States.Where(s => ids.Contains(s.MapIndex)).Sum(s => (double)s.Population);
        var share = (float)Math.Clamp(population / Math.Max(1, target.Population), 0, 1);
        var mine = AiStatecraft.StrengthOf(c, target);
        var theirs = AiStatecraft.StrengthOf(c, demander);
        var defenders = Statecraft.DefendersOf(c, target);
        mine *= 1f + MathF.Min(Math.Max(0, defenders) * .15f, .6f);
        var ratio = theirs / MathF.Max(.01f, mine);
        var moneyShare = demand.Reparations / MathF.Max(.01f, target.Treasury);
        // Larger packages are harder to swallow. A preview explains the actual rule;
        // this is not a fabricated percentage chance or a separate UI calculation.
        var burden = 1f + share + moneyShare * .5f + (demand.NonAggression ? .15f : 0f);
        var relation = CampaignDiplomacy.Peek(c, demander.Id, target.Id);
        if (relation is not null && relation.Opinion <= -c.Defines.UltimatumRefuseGrudge) burden *= 1.25f;
        var yield = mine <= .01f || ratio > c.Defines.UltimatumYieldStrengthRatio * burden ||
            target.Stability < c.Defines.UltimatumYieldStability / burden;
        return new(true, "", yield ? Answer.Yield : Answer.Refuse, ratio, share);
    }

    public static bool Validate(Campaign c, Nation demander, Nation target, Demand demand, out string reason)
        => ValidateCore(c, demander, target, demand, out reason, allowHuman: false);

    /// <summary>The package itself, including one addressed to a player. A player is not answered here.</summary>
    internal static bool ValidateTerms(Campaign c, Nation demander, Nation target, Demand demand, out string reason)
        => ValidateCore(c, demander, target, demand, out reason, allowHuman: true);

    private static bool ValidateCore(Campaign c, Nation demander, Nation target, Demand demand, out string reason, bool allowHuman)
    {
        reason = "";
        if (c?.ProvinceMap is null || demander is null || target is null || demander.Id == target.Id || demand is null)
        { reason = "Invalid target."; return false; }
        if (!CampaignDiplomacy.CanDeclareWar(c, demander.Id, target.Id, out reason)) return false;
        if (!allowHuman && target.IsHuman) return Rejected("Human-controlled countries cannot be forced to answer automatically.", out reason);
        var ids = RegionIds(demand).ToHashSet();
        if (ids.Count > Math.Clamp(c.Defines.UltimatumMaxRegions, 1, 100)) return Rejected("Too many regions in this demand.", out reason);
        if (!float.IsFinite(demand.Reparations) || demand.Reparations < 0 || demand.Reparations >
            Math.Max(0, target.Treasury) * Math.Clamp(c.Defines.UltimatumMaxTreasuryShare, 0, 1))
            return Rejected("Reparations exceed the allowed share of their treasury.", out reason);
        if (ids.Count == 0 && demand.Reparations <= 0 && !demand.LiftEmbargo && !demand.NonAggression)
            return Rejected("Select at least one demand.", out reason);
        var relation = CampaignDiplomacy.Get(c, demander.Id, target.Id);
        if (demand.LiftEmbargo && !relation.EmbargoBy(target.Id)) return Rejected("They have not imposed an embargo on us.", out reason);
        foreach (var id in ids)
            if (!c.States.Any(s => s.MapIndex == id && s.NationMapIndex == target.MapIndex))
                return Rejected("They cannot yield what they do not hold.", out reason);
        var ownedRegions = new HashSet<int>();
        var retained = false;
        for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
        {
            if (ids.Contains(c.ProvinceMap.ProvinceToState[p]) && c.ProvinceMap.ProvinceToNation[p] != target.MapIndex)
                return Rejected("They cannot yield what they do not hold.", out reason);
            if (c.ProvinceMap.ProvinceToNation[p] != target.MapIndex) continue;
            var state = c.ProvinceMap.ProvinceToState[p];
            if (!ids.Contains(state)) { retained = true; continue; }
            ownedRegions.Add(state);
            if ((c.ProvinceController ?? c.ProvinceMap.ProvinceToNation)[p] != target.MapIndex)
                return Rejected("A demand must leave them territory and cannot include occupied land.", out reason);
        }
        if (ids.Count > 0 && (!retained || ownedRegions.Count != ids.Count))
            return Rejected("A demand must leave them territory and cannot include occupied land.", out reason);
        return true;
    }

    private static bool Rejected(string note, out string reason) { reason = note; return false; }

    public static string Summary(Campaign c, Demand demand)
    {
        var ids = RegionIds(demand).ToHashSet();
        var parts = c.States.Where(s => ids.Contains(s.MapIndex)).OrderBy(s => s.MapIndex).Select(s => s.Name).ToList();
        if (demand.Reparations > 0) parts.Add(Loc.Format("Reparations: {0}", Fmt.Money(demand.Reparations)));
        if (demand.LiftEmbargo) parts.Add(Loc.Get("Lift their embargo"));
        if (demand.NonAggression) parts.Add(Loc.Get("Non-aggression pact"));
        return string.Join(" · ", parts);
    }

    public static Outcome Resolve(Campaign c, Nation demander, Nation target, Demand demand, Answer answer)
        => ResolveCore(c, demander, target, demand, answer, allowHuman: false);

    /// <summary>The addressed player has answered. The same package, and still no automatic guess.</summary>
    internal static Outcome ResolveAnswered(Campaign c, Nation demander, Nation target, Demand demand, Answer answer)
        => ResolveCore(c, demander, target, demand, answer, allowHuman: true);

    private static Outcome ResolveCore(Campaign c, Nation demander, Nation target, Demand demand, Answer answer, bool allowHuman)
    {
        // All terms are validated before the first mutation. An invalid second region
        // must not leave a half-applied settlement or take a partial cash payment.
        if (!ValidateCore(c, demander, target, demand, out var reason, allowHuman)) return new(false, reason);
        var summary = Summary(c, demand);
        var relation = CampaignDiplomacy.Get(c, demander.Id, target.Id);
        if (answer == Answer.Yield)
        {
            var ids = RegionIds(demand).ToHashSet();
            var population = c.States.Where(s => ids.Contains(s.MapIndex)).Sum(s => s.Population);
            var provinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p =>
                ids.Contains(c.ProvinceMap.ProvinceToState[p]) && c.ProvinceMap.ProvinceToNation[p] == target.MapIndex).ToArray();
            if (provinces.Length > 0) StatePartition.Annex(c, provinces, demander, asConquest: false);
            target.Treasury -= demand.Reparations;
            demander.Treasury += demand.Reparations;
            if (demand.LiftEmbargo)
            {
                if (relation.A == target.Id) relation.EmbargoByA = false;
                else relation.EmbargoByB = false;
            }
            if (demand.NonAggression) relation.NonAggressionPact = true;
            target.Stability = M.Clamp01(target.Stability - c.Defines.UltimatumYieldStabilityCost);
            if (population > 0) Statecraft.RecordAnnexation(c, demander, population);
        }
        else OpinionSystem.Remember(c, relation, "refused_our_ultimatum");
        relation.TruceUntil = Math.Max(relation.TruceUntil, c.ElapsedDays + Math.Max(1,
            answer == Answer.Yield ? c.Defines.UltimatumSettlementTruceDays : c.Defines.UltimatumRefusalTruceDays));
        relation.LastActionDay = c.ElapsedDays;
        OpinionSystem.Remember(c, relation, "received_our_ultimatum");
        c.Chronicle.Record(c, answer == Answer.Yield ? "ultimatum_accepted" : "ultimatum_refused",
            demander.Id, target.Id, subject: summary);
        c.MarkStrategicChange();
        return new(true, summary);
    }
}
