using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Rare, deterministic diplomatic projects, not an hourly annexation lottery.
/// No static campaign cache or random draws: save/load and lockstep use the same ledger.</summary>
public static class PeacefulFormationAi
{
    private const string ReviewKey = "union_ai_review";

    private sealed class TickScratch
    {
        public readonly List<FormableNation> Definitions = new();
        public readonly List<Nation> DueFounders = new();
        public readonly HashSet<int> Leaders = new();
        public readonly HashSet<int> AtWar = new();
        public readonly HashSet<string> Formed = new(StringComparer.Ordinal);
        public readonly Dictionary<int, int> Owners = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, TickScratch> _tickScratches = new();

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>
    /// The same project review, a founding country at a time.
    ///
    /// One indivisible call was 65 ms of a single frame on a fifty-year world - and roughly
    /// five megabytes of garbage while it ran - because smooth time can only return a frame
    /// at a stage boundary. The order, the stamps and the one-invitation rule are untouched.
    /// </summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(PeacefulFormationAi))) yield break;
        var interval = campaign.Defines.UnionAiReviewDays;
        if (interval <= 0 || campaign.ElapsedDays < campaign.Defines.UnionAiEarliestDay ||
            !float.IsFinite(campaign.Defines.UnionAiMinimumOwnedShare) ||
            !float.IsFinite(campaign.Defines.UnionAiTreasuryReserveShare) ||
            campaign.ProvinceMap is null || campaign.ProvinceController is null) yield break;
        var scratch = _tickScratches.GetOrCreateValue(campaign);
        var definitions = scratch.Definitions;
        definitions.Clear();
        foreach (var def in FormableNations.AvailableIn(campaign))
            if (def.AiFoundation && (def.PeacefulAccession || def.SubjectIntegration))
                definitions.Add(def);
        if (definitions.Count == 0) yield break;

        var dueFounders = scratch.DueFounders;
        dueFounders.Clear();
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var founder = nations[i];
            if (founder.IsHuman || (campaign.ElapsedDays + (long)founder.Id) % interval != 0 ||
                (founder.DecisionAvailableDay.Count > 0 && founder.DecisionAvailableDay.GetValueOrDefault(ReviewKey) > campaign.ElapsedDays) ||
                !CanLead(campaign, founder)) continue;
            dueFounders.Add(founder);
        }
        if (dueFounders.Count == 0) yield break;
        if (dueFounders.Count > 1) dueFounders.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        // Three questions the reviews below ask of every candidate, answered once for the pass
        // instead of once per candidate partner: who may lead at all, what identities already
        // exist, and whether a country is at peace. Nothing a review does here changes any of
        // them - an invitation writes a cooldown and a consent key, never a border or a war -
        // so the answers are the same before the first founder as after the last.
        var leaders = scratch.Leaders;
        var atWar = scratch.AtWar;
        var formed = scratch.Formed;
        FillLeaderIds(campaign, leaders);
        FillNationsAtWar(campaign, atWar);
        formed.Clear();
        for (var i = 0; i < nations.Count; i++)
            if (!string.IsNullOrEmpty(nations[i].FormedNationId)) formed.Add(nations[i].FormedNationId);

        var minOwnedShare = M.Clamp01(campaign.Defines.UnionAiMinimumOwnedShare);
        var reserveShare = Math.Max(0f, campaign.Defines.UnionAiTreasuryReserveShare);
        var owners = scratch.Owners;

        for (var fi = 0; fi < dueFounders.Count; fi++)
        {
            var founder = dueFounders[fi];
            // Stamp even an unsuccessful review: repeated calls today must be idempotent.
            founder.DecisionAvailableDay[ReviewKey] = campaign.ElapsedDays + interval;
            for (var di = 0; di < definitions.Count; di++)
            {
                var definition = definitions[di];
                if (!FormableNations.Eligible(campaign, founder, definition) ||
                    !FormableNations.CompatibleProclamation(founder, definition) ||
                    FormableNations.SmallerThanCountry(campaign, founder, definition) ||
                    !FormableNations.ReligionMandate(campaign, founder, definition) ||
                    founder.Stability < definition.MinimumStability ||
                    (definition.RequiredIdeology.Length > 0 && !ContainsIgnoreCase(definition.RequiredIdeology, founder.Ideology)) ||
                    (definition.RequiredGovernment.Length > 0 && !ContainsIgnoreCase(definition.RequiredGovernment, founder.Government)) ||
                    formed.Contains(definition.Id)) continue;
                var reserve = founder.Gdp * reserveShare;
                if (founder.Treasury < Math.Max(.05f, founder.Gdp * definition.CostShare) + reserve) continue;

                var map = campaign.ProvinceMap;
                owners.Clear();
                var total = 0;
                var complete = true;
                var is2024 = campaign.ScenarioId == "2024";
                for (var ti = 0; ti < definition.RequiredTags.Length; ti++)
                {
                    var tag = definition.RequiredTags[ti];
                    var provs = is2024 ? ProvinceOwnership.ProvincesOf(tag) : campaign.ScenarioTerritories.GetValueOrDefault(tag, Array.Empty<int>());
                    if (((IReadOnlyCollection<int>)provs).Count == 0) { complete = false; }
                }
                foreach (var province in PeacefulFormation.Territory(campaign, definition))
                {
                    if (province <= 0 || province > map.ProvinceCount) { complete = false; break; }
                    if (map.WaterByProvince[province]) continue;
                    if (province >= campaign.ProvinceController.Length || map.ProvinceToNation[province] == 0 ||
                        campaign.ProvinceController[province] != map.ProvinceToNation[province]) { complete = false; break; }
                    var owner = map.ProvinceToNation[province];
                    owners[owner] = owners.GetValueOrDefault(owner) + 1;
                    total++;
                }
                if (!complete || total == 0 || owners.GetValueOrDefault(founder.MapIndex) / (float)total < minOwnedShare) continue;

                // The largest eligible, viable founding country has precedence, including
                // humans. Two AI founders must not approach the same partner in parallel.
                var leader = LeaderOf(campaign, definition, owners, leaders);
                if (leader?.Id != founder.Id) continue;

                if (FormableNations.EvaluateCanForm(campaign, founder, definition, out _))
                {
                    // A ready identity is chosen by the common situational proclamation
                    // policy, never by this invitation catalogue's alphabetical order.
                    continue;
                }
                var partner = PartnerOf(campaign, founder, definition, owners, leaders, atWar);
                if (partner is null) continue;
                PeacefulFormation.Invite(campaign, founder, partner, definition);
                // At most one invitation OR proclamation per founding country per review.
                // Acceptance is a treaty; proclamation waits for a later review.
                break;
            }
            yield return 0;
        }
    }

    private static bool ContainsIgnoreCase(string[] values, string target)
    {
        for (var i = 0; i < values.Length; i++)
            if (string.Equals(values[i], target, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool CanLead(Campaign campaign, Nation nation)
    {
        if (nation.Population <= 0 || !FormableProclamationAi.EstablishedCountry(nation) || nation.Landless || nation.IsSubject)
            return false;
        var live = Warfare.LiveWars(campaign);
        for (var i = 0; i < live.Count; i++)
        {
            var w = live[i];
            if (!w.Ended && w.Involves(nation.Id)) return false;
        }
        return true;
    }

    /// <summary>Every country that may lead a foundation today, worked out in one walk.</summary>
    private static void FillLeaderIds(Campaign campaign, HashSet<int> ids)
    {
        ids.Clear();
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
            if (CanLead(campaign, nations[i])) ids.Add(nations[i].Id);
    }

    /// <summary>
    /// The founding country with precedence over this definition's ground: most of the ground,
    /// then the largest economy, then the first id.
    ///
    /// This was a filter, three sorts and a materialised list per definition, built for every
    /// founder - the largest single source of garbage in this pass. The ranking is a comparison,
    /// so it is done as one: a country that cannot beat the current best is not even asked the
    /// questions that would let it try, and the winner is the same country in every tie.
    /// </summary>
    private static Nation LeaderOf(Campaign campaign, FormableNation definition, Dictionary<int, int> owners, HashSet<int> leaders)
    {
        Nation best = null;
        var bestOwned = 0;
        foreach (var nation in campaign.Nations)
        {
            if (!owners.TryGetValue(nation.MapIndex, out var owned)) continue;
            var reaches = best is null || owned > bestOwned ||
                (owned == bestOwned && (nation.Gdp > best.Gdp || (nation.Gdp == best.Gdp && nation.Id < best.Id)));
            if (!reaches) continue;
            if (!leaders.Contains(nation.Id) ||
                !FormableNations.Eligible(campaign, nation, definition) ||
                !FormableNations.CompatibleProclamation(nation, definition) ||
                FormableNations.SmallerThanCountry(campaign, nation, definition) ||
                !FormableNations.ReligionMandate(campaign, nation, definition) ||
                nation.Stability < definition.MinimumStability ||
                nation.Treasury < Math.Max(.05f, nation.Gdp * definition.CostShare) +
                    nation.Gdp * Math.Max(0f, campaign.Defines.UnionAiTreasuryReserveShare) ||
                (definition.RequiredIdeology.Length > 0 && !definition.RequiredIdeology.Contains(nation.Ideology, StringComparer.OrdinalIgnoreCase)) ||
                (definition.RequiredGovernment.Length > 0 && !definition.RequiredGovernment.Contains(nation.Government, StringComparer.OrdinalIgnoreCase))) continue;
            best = nation;
            bestOwned = owned;
        }
        return best;
    }

    /// <summary>
    /// The partner worth the single invitation this founder may send: highest willingness,
    /// then the largest share of the ground, then the first id - the order the sorted filter
    /// produced. Willingness is worked out once per candidate instead of once to filter and
    /// again to rank.
    /// </summary>
    private static Nation PartnerOf(Campaign campaign, Nation founder, FormableNation definition,
        Dictionary<int, int> owners, HashSet<int> leaders, HashSet<int> atWar)
    {
        var acceptance = campaign.Defines.UnionAiAcceptance;
        Nation best = null;
        var bestWillingness = 0f;
        var bestOwned = 0;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Id == founder.Id) continue;
            if (!owners.TryGetValue(nation.MapIndex, out var owned)) continue;
            // A country at war cannot join: both filters below would refuse it, and asking them
            // means walking every live war - three hundred of them - once per candidate partner.
            if (atWar.Contains(nation.Id)) continue;
            // Willingness first, and only then the refusal sentence. Both are pure, so the order
            // cannot change the answer; it changes how often the answer is built. Building one
            // localized sentence per candidate of every definition was the whole of this pass's
            // garbage - four to five megabytes a day, every day, on a late world.
            var willingness = PeacefulFormation.Willingness(campaign, founder, nation);
            if (willingness < acceptance) continue;
            if (PeacefulFormation.Participates(campaign, founder, nation, definition)) continue;
            if (PromisedElsewhere(campaign, founder.Id, nation, leaders)) continue;
            if (PeacefulFormation.Refusal(campaign, founder, nation, definition).Length != 0) continue;
            var reaches = best is null || willingness > bestWillingness ||
                (willingness == bestWillingness && (owned > bestOwned || (owned == bestOwned && nation.Id < best.Id)));
            if (!reaches) continue;
            best = nation;
            bestWillingness = willingness;
            bestOwned = owned;
        }
        return best;
    }

    /// <summary>Every country currently fighting somebody, worked out once from the wars.</summary>
    private static void FillNationsAtWar(Campaign campaign, HashSet<int> atWar)
    {
        atWar.Clear();
        var live = Warfare.LiveWars(campaign);
        for (var i = 0; i < live.Count; i++)
        {
            var war = live[i];
            if (war.Ended) continue;
            atWar.Add(war.AttackerId);
            atWar.Add(war.DefenderId);
            foreach (var ally in war.AttackerAllies) atWar.Add(ally);
            foreach (var ally in war.DefenderAllies) atWar.Add(ally);
        }
    }

    private static bool PromisedElsewhere(Campaign campaign, int founderId, Nation partner, HashSet<int> leaders)
    {
        // A country cannot sign overlapping foundations with competing governments. The ledger
        // keeps one key per government that ever approached it, so reading the partner's keys
        // once and asking each key's own government whether it may lead answers exactly what
        // walking every country in the world looking for its prefix answered - and that walk,
        // per candidate partner, was the whole cost of this pass.
        foreach (var (key, until) in partner.DecisionAvailableDay)
        {
            if (until <= campaign.ElapsedDays) continue;
            var other = LeaderFromConsentKey(key);
            if (other < 0 || other == founderId || !leaders.Contains(other)) continue;
            return true;
        }
        var pending = campaign.PendingEvents;
        for (var i = 0; i < pending.Count; i++)
        {
            var e = pending[i];
            if (e.NationId != partner.Id || e.ExpiresDay <= campaign.ElapsedDays) continue;
            var opts = e.Options;
            for (var j = 0; j < opts.Length; j++)
            {
                var o = opts[j];
                if (o.UnionFounderId != founderId && !string.IsNullOrEmpty(o.UnionFormableId)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The leading country named by a consent key, or -1.
    ///
    /// <c>PeacefulFormation.ConsentKey</c> writes <c>union_consent/&lt;founder&gt;/&lt;formable&gt;</c>.
    /// The digits must end at a separator: the prefix test this replaces could not match
    /// <c>union_consent/1/</c> against <c>union_consent/12/...</c>, and neither does this.
    /// </summary>
    private static int LeaderFromConsentKey(string key)
    {
        const string prefix = "union_consent/";
        if (key is null || key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.Ordinal)) return -1;
        var value = 0;
        var digits = 0;
        for (var index = prefix.Length; index < key.Length; index++)
        {
            var character = key[index];
            if (character is < '0' or > '9') break;
            if (digits == 9) return -1;
            value = value * 10 + (character - '0');
            digits++;
        }
        var end = prefix.Length + digits;
        return digits > 0 && end < key.Length && key[end] == '/' ? value : -1;
    }
}
