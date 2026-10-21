using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Relations at or above this may open union talks between kindred peoples.</summary>
    public float CultureUnionMinimumOpinion { get; set; } = 0f;
    /// <summary>How often the kindred map is reviewed for consolidation, in days.</summary>
    public int CultureUnionReviewDays { get; set; } = 30;
    /// <summary>No unions before the bookmark world has settled.</summary>
    public int CultureUnionEarliestDay { get; set; } = 365;
    /// <summary>Relations a partner wants before it signs itself away, on top of the minimum.</summary>
    public float CultureUnionAcceptanceOpinion { get; set; } = 20f;
}

/// <summary>
/// Kindred one-region states joining into one country, by consent.
///
/// Rises and referendums scatter a people across a handful of single-region states that
/// share a flag's people and nothing else - and the map reads as confetti. Where the
/// formable unions cover the classic cases (Germany, the Samoas), this covers the small
/// ones: a state may propose to a KINDRED neighbour - the same people, at peace, relations
/// not hostile - that they become one country. The smaller joins the larger: ground, people
/// and treasury move; the joining state's tag retires the way an absorbed belligerent's does.
///
/// The AI pursues this the way such unions actually happen. A healthy kindred neighbour is
/// courted - it signs when relations are warm. A failing one (stability collapsing, treasury
/// drained) signs at mere neutrality: joining the kin beat is better than rotting alone. The
/// war path is deliberately absent - this is the diplomatic door; conquest of kin is the
/// warmaking system's business and already exists.
/// </summary>
public static class CultureUnion
{
    public static string CooldownKey(int initiator) => "culture_union:" + initiator;

    /// <summary>The same people is the whole premise: primary cultures must overlap exactly.</summary>
    public static bool SamePeople(Nation a, Nation b) =>
        a is not null && b is not null &&
        DemographyCatalog.PrimaryCultures(a.Tag).Any(x =>
            DemographyCatalog.PrimaryCultures(b.Tag).Contains(x, StringComparer.OrdinalIgnoreCase));

    /// <summary>A union of neighbours: any of the partner's regions touching the initiator's ground.</summary>
    public static bool SharesBorder(Campaign c, Nation initiator, Nation partner)
    {
        foreach (var state in c.StatesOf(partner))
            if (TerritorialCoherence.TouchesOwnedGround(c, initiator, state.MapIndex)) return true;
        return false;
    }

    public static string Refusal(Campaign c, Nation initiator, Nation partner)
    {
        if (c is null || initiator is null || partner is null || initiator.Id == partner.Id)
            return Loc.Get("Choose a kindred country to unite with.");
        if (!c.FeatureEnabled("culture_union") || c.ProvinceMap is null) return Loc.Get("Cultural unions are disabled in this scenario.");
        if (!SamePeople(initiator, partner)) return Loc.Get("A union of peoples needs peoples that are the same.");
        if (c.Fallen.Contains(initiator.Id) || c.Fallen.Contains(partner.Id) ||
            initiator.Population <= 0 || partner.Population <= 0)
            return Loc.Get("Both countries must be alive to unite.");
        if (initiator.IsSubject || partner.IsSubject || initiator.Movement is not null || partner.Movement is not null)
            return Loc.Get("Both countries must be sovereign to unite by consent.");
        if (CampaignDiplomacy.AtWar(c, initiator.Id, partner.Id) ||
            c.Wars.Any(w => !w.Ended && (w.Involves(initiator.Id) || w.Involves(partner.Id))))
            return Loc.Get("Union is a peace-time act; neither country may be at war.");
        if (partner.IsHuman && c.HumanSeats.Count > 1) return Loc.Get("Human countries cannot merge in multiplayer.");
        if ((CampaignDiplomacy.Peek(c, initiator.Id, partner.Id)?.Opinion ?? 0f) < c.Defines.CultureUnionMinimumOpinion)
            return Loc.Get("Relations must not be hostile before union talks.");
        if (initiator.Stability < 0.40f || partner.Stability < 0.30f)
            return Loc.Get("Both governments need enough standing to carry a union.");
        if (!SharesBorder(c, initiator, partner))
            return Loc.Get("The countries must share a border to become one.");
        if (partner.DecisionAvailableDay.TryGetValue(CooldownKey(initiator.Id), out var until) && until > c.ElapsedDays)
            return Loc.Get("This country declined recently; give it time.");
        return "";
    }

    /// <summary>The smaller country joins the larger; the tags and books of the joiner retire.</summary>
    public static bool Unite(Campaign c, Nation initiator, Nation partner)
    {
        if (Refusal(c, initiator, partner).Length != 0) return false;
        if (partner.Population > initiator.Population) (initiator, partner) = (partner, initiator);
        var map = c.ProvinceMap;
        var provinces = new List<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == (ushort)partner.MapIndex && !map.IsWater(province))
                provinces.Add(province);
        if (provinces.Count == 0) return false;

        var place = partner.DisplayName;
        StatePartition.Annex(c, provinces, initiator, asConquest: false);
        c.Fallen.Add(partner.Id);
        partner.DecisionAvailableDay.Clear();
        initiator.DecisionAvailableDay.Remove(CooldownKey(initiator.Id));
        foreach (var other in c.Nations)
            other.DecisionAvailableDay.Remove(CooldownKey(partner.Id));
        CapitalCore.Establish(c, initiator,
            c.StatesOf(initiator).OrderByDescending(s => s.Population).FirstOrDefault());
        c.Chronicle.Record(c, "culture_union", initiator.Id, partner.Id, place);
        DiagnosticLogs.Write("simulation",
            $"CULTURE_UNION initiator={initiator.Tag} joined={partner.Tag} regions={provinces.Count} day={c.ElapsedDays}");
        c.MarkMapChanged();
        c.MarkStrategicChange();
        return true;
    }

    /// <summary>Monthly, staggered: the larger of two kindred neighbours proposes to the smaller.
    /// A thriving neighbour signs when relations are genuinely warm; a failing one signs at
    /// mere neutrality, because the kin state is the better fate. Nothing is proposed across
    /// a war or a grudge.</summary>
    public static void Tick(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(CultureUnion))) return;
        if (c.ProvinceMap is null || c.ElapsedDays < c.Defines.CultureUnionEarliestDay) return;
        if (c.ElapsedDays % Math.Max(1, c.Defines.CultureUnionReviewDays) != 0) return;

        foreach (var initiator in Kindred(c).ToArray())
        {
            if (c.Fallen.Contains(initiator.Id)) continue;
            foreach (var partner in Kindred(c))
            {
                if (partner.Id == initiator.Id || c.Fallen.Contains(partner.Id)) continue;
                if (partner.Population > initiator.Population) continue;
                var opinion = CampaignDiplomacy.Peek(c, initiator.Id, partner.Id)?.Opinion ?? 0f;
                if (opinion < c.Defines.CultureUnionMinimumOpinion) continue;
                var wantsIt = opinion >= c.Defines.CultureUnionAcceptanceOpinion ||
                    (partner.Stability < 0.35f && initiator.Stability > 0.55f);
                if (!wantsIt) continue;
                if (Refusal(c, initiator, partner).Length != 0) continue;
                if (Unite(c, initiator, partner)) break;
            }
        }
    }

    /// <summary>Independent AI countries, the largest first - the large propose, the small answer.</summary>
    private static IEnumerable<Nation> Kindred(Campaign c) => c.Nations
        .Where(n => !n.IsHuman && !n.IsSubject && n.Movement is null && n.Population > 0 &&
                    !c.Fallen.Contains(n.Id) && !c.Wars.Any(w => !w.Ended && w.Involves(n.Id)))
        .OrderByDescending(n => n.Population).ThenBy(n => n.Id);
}

/// <summary>Propose - or carry out - the union of two kindred countries, by consent.</summary>
public sealed class CultureUnionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int PartnerId { get; init; }
    public bool Changed { get; private set; }
    public string Result { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var initiator = campaign?.NationById(IssuerId);
        var partner = campaign?.NationById(PartnerId);
        if (initiator is null || partner is null) { Result = "No such country"; return; }
        if (CultureUnion.Refusal(campaign, initiator, partner) is { Length: > 0 } refusal) { Result = refusal; return; }
        Changed = CultureUnion.Unite(campaign, initiator, partner);
        Result = Changed ? "United" : "The union could not be concluded";
    }
}
