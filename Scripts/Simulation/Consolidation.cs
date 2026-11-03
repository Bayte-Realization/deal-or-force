using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>The union this country is working to bring together (a formable id), or empty.</summary>
    public string ConsolidationProject = "";
    /// <summary>The country it is working on this month for it, or -1.</summary>
    public int ConsolidationFocusId = -1;
    /// <summary>How hard it is pushing, 0 to 1 (Consolidation.Ambition), as of its last review.</summary>
    public float ConsolidationAmbition;
}

public sealed partial class Defines
{
    /// <summary>How often a country reviews its union project, in days (staggered by country).</summary>
    public int ConsolidationReviewDays { get; set; } = 30;
    /// <summary>No union projects before this day: the world of January 2024 settles first.</summary>
    public int ConsolidationEarliestDay { get; set; } = 120;
    /// <summary>The ambition from which a government counts a member holding out as a reason for war.</summary>
    public float ConsolidationForceAmbition { get; set; } = 0.55f;
    /// <summary>What a member holding out is worth to a war judgement, at full ambition and the whole project.</summary>
    public float AiConsolidationAppetite { get; set; } = 1.4f;
    /// <summary>Opinion above the accession minimum a government courts a partner to before it asks.</summary>
    public float ConsolidationCourtingMargin { get; set; } = 15f;
    /// <summary>The ambition from which a government courts a kindred people (one of the same family, not the same nation).</summary>
    public float ConsolidationCourtingAmbition { get; set; } = 0.30f;
    /// <summary>How kindred, on average, a union's other members must be for a country to take it up as its own project.</summary>
    public float ConsolidationMinimumKinship { get; set; } = 0.25f;
    /// <summary>How often small splinters or cultural shards review potential unifications.</summary>
    public int ShardConsolidationReviewDays { get; set; } = 30;
    /// <summary>No shard consolidation before this day.</summary>
    public int ShardConsolidationEarliestDay { get; set; } = 90;
    /// <summary>Willingness required for two shards of the same culture to merge.</summary>
    public float ShardConsolidationAcceptance { get; set; } = 55f;
    /// <summary>Maximum number of states a nation can hold to be considered a splinter or shard.</summary>
    public int ShardMaxStates { get; set; } = 6;
    /// <summary>Maximum population a nation can hold to be considered a splinter or shard.</summary>
    public long ShardMaxPopulation { get; set; } = 35_000_000;
}

/// <summary>
/// A state that sets out to bring its kin together - Serbia and the lands of Yugoslavia, Russia
/// and the Union, Somalia and the Somali territories - and works at it for years.
///
/// The pieces were already in the game: unions a country may proclaim with peaceful accession
/// (common/formable_nations), the treaty that brings a willing partner in (PeacefulFormation),
/// the willingness a partner has - opinion, kinship, an alliance, a trade agreement, shared
/// politics - and the reasons for war a divided people gives (irredentism, lost land). What was
/// missing was the intention: the accession AI asked only once a founder already held most of
/// the union, so a Serbia holding a third of Yugoslavia never began.
///
/// So once a month the natural leader of each union - the eligible state holding most of it -
/// reviews it. A partner willing to join is invited. One that is not is courted, a step at a
/// time: relations, then a trade agreement, then an alliance, which are exactly the things its
/// willingness is made of. And a government that wants the union badly enough - an
/// authoritarian or nationalist one, settled at home, not held back by its own constitution -
/// also counts a member that holds out as a reason for war (WarValue). It still needs a cause
/// the world would accept: its own people across the border, ground that was once its own, an
/// ultimatum refused. A liberal Croatia is courted; a Republika Srpska's Bosnia may be fought.
/// </summary>
public static class Consolidation
{
    public static void Tick(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(Consolidation))) return;
        if (c.ProvinceMap is null || c.ProvinceController is null) return;
        var defines = c.Defines;
        if (c.ElapsedDays < defines.ConsolidationEarliestDay) return;
        var every = Math.Max(7, defines.ConsolidationReviewDays);
        foreach (var nation in c.Nations.ToArray())
        {
            if ((c.ElapsedDays + (long)nation.Id * 7) % every != 0) continue;
            // A player's country pursues a union only when the player has handed diplomacy over.
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Diplomacy)) continue;
            if (!FormableProclamationAi.EstablishedCountry(nation) || nation.Landless || nation.IsSubject || nation.Population <= 0 || !c.HasOwnedLand(nation))
            {
                Clear(nation);
                continue;
            }
            Review(c, nation);
            if (string.IsNullOrEmpty(nation.ConsolidationProject))
            {
                ReviewShardConsolidation(c, nation);
            }
        }
    }

    private static void Clear(Nation nation)
    {
        nation.ConsolidationProject = "";
        nation.ConsolidationFocusId = -1;
        nation.ConsolidationAmbition = 0f;
    }

    /// <summary>
    /// How hard a government pushes for its union: authoritarian creeds and nationalism push,
    /// a settled country can afford to, and a constitution that holds back wars of choice
    /// holds this back too (AiStatecraft.Restraint). Zero to one.
    /// </summary>
    public static float Ambition(Campaign c, Nation nation)
    {
        var ideology = PoliticsCore.IdeologyOf(nation);
        var nationalist = string.Equals(nation.Ideology, "nationalism", StringComparison.OrdinalIgnoreCase);
        var restraint = AiStatecraft.Restraint(c, nation);
        return Rules.DiplomacyRules.ConsolidationAmbition(ideology.Authority, nationalist, nation.Stability, restraint);
    }

    private static void Review(Campaign c, Nation founder)
    {
        var project = Project(c, founder);
        if (project is null) { Clear(founder); return; }
        var (definition, owners, total) = project.Value;
        founder.ConsolidationProject = definition.Id;
        founder.ConsolidationAmbition = Ambition(c, founder);

        // Diplomacy pursues a project; only the shared situational policy proclaims it.
        // Do not bypass its fiscal reserve, recovery period or identity comparison here.
        if (FormableNations.Evaluate(c, founder, definition).CanForm) return;

        // The members still outside, the most willing first: one step a month, on the one most
        // likely to give. Not one whose foreign policy another capital holds - it cannot sign.
        var partners = owners.Keys.Where(mapIndex => mapIndex != founder.MapIndex && mapIndex != 0)
            .Select(mapIndex => c.Nations.Find(n => n.MapIndex == mapIndex))
            .Where(n => n is { Population: > 0 } && FormableProclamationAi.EstablishedCountry(n) && !n.Landless &&
                        !n.AnswersTo(founder.Id) && !founder.AnswersTo(n.Id) &&
                        !n.IsSubject)
            .OrderByDescending(n => PeacefulFormation.Willingness(c, founder, n))
            .ThenByDescending(n => owners[n.MapIndex]).ThenBy(n => n.Id)
            .ToList();
        foreach (var partner in partners)
        {
            var relation = CampaignDiplomacy.Get(c, founder.Id, partner.Id);
            if (relation is null || relation.Stance == DiplomaticStance.AtWar) continue;
            // Kin only. The same people is courted by any government; a kindred people of the same
            // family only by one that wants the union; a foreign one by nobody - liberal Norway does
            // not set out to absorb Denmark, and a Turkish Byzantium does not court Athens.
            var kinship = PeacefulFormation.CulturalAffinity(founder, partner);
            if (kinship < 0.5f) continue;
            if (kinship < 1f && founder.ConsolidationAmbition < c.Defines.ConsolidationCourtingAmbition) continue;
            founder.ConsolidationFocusId = partner.Id;
            var willing = PeacefulFormation.Willingness(c, founder, partner);
            if (willing >= c.Defines.UnionAiAcceptance && PeacefulFormation.Refusal(c, founder, partner, definition).Length == 0)
            {
                new InviteFormationCommand { IssuerId = founder.Id, PartnerId = partner.Id, FormableId = definition.Id }.Apply(c);
                return;
            }
            // Courted, in the order its willingness is made of.
            if (relation.Opinion < c.Defines.UnionMinimumOpinion + c.Defines.ConsolidationCourtingMargin)
            {
                new ImproveRelationsCommand { IssuerId = founder.Id, TargetId = partner.Id }.Apply(c);
                return;
            }
            if (!relation.TradeAgreement)
            {
                new ProposeAgreementCommand { IssuerId = founder.Id, TargetId = partner.Id, Kind = AgreementKind.TradeAgreement }.Apply(c);
                return;
            }
            if (relation.Stance != DiplomaticStance.Allied)
            {
                new ProposeAllianceCommand { IssuerId = founder.Id, TargetId = partner.Id }.Apply(c);
                return;
            }
            // Allied, trading and friendly and still not willing: nothing more diplomacy can do
            // this month. The next partner may be readier.
        }
    }

    /// <summary>
    /// The union a country leads: one it may proclaim, open to peaceful accession, not yet
    /// proclaimed by anybody, whose politics it already has, of which it holds a part - and more
    /// of it than any other country that could lead it. The one nearest completion wins.
    /// </summary>
    public static (FormableNation Definition, Dictionary<int, int> Owners, int Total)? Project(Campaign c, Nation founder)
    {
        if (c?.ProvinceMap is null || founder is null || !c.FeatureEnabled("formable_nations")) return null;
        var map = c.ProvinceMap;
        (FormableNation, Dictionary<int, int>, int)? best = null;
        var bestShare = 0f;
        foreach (var definition in FormableNations.All)
        {
            if (!definition.AiFoundation || !definition.PeacefulAccession || !CanPursue(c, founder, definition)) continue;
            if (c.Nations.Any(n => n.FormedNationId == definition.Id)) continue;
            var (owners, total) = Owners(c, definition);
            if (total == 0) continue;
            var mine = owners.GetValueOrDefault(founder.MapIndex);
            if (mine == 0 || mine == total) continue;
            // The natural leader only: nobody else who could lead it holds more of it.
            var outranked = false;
            foreach (var (mapIndex, count) in owners)
            {
                if (mapIndex == founder.MapIndex || count <= mine) continue;
                var other = c.Nations.Find(n => n.MapIndex == mapIndex);
                if (other is not null && CanPursue(c, other, definition)) { outranked = true; break; }
            }
            if (outranked) continue;
            // A union of kin: how kindred the rest of it is to us, weighted by how much each holds.
            var kinWeight = 0f;
            var otherCount = 0;
            foreach (var (mapIndex, count) in owners)
            {
                if (mapIndex == founder.MapIndex) continue;
                var other = c.Nations.Find(n => n.MapIndex == mapIndex);
                if (other is null) continue;
                kinWeight += PeacefulFormation.CulturalAffinity(founder, other) * count;
                otherCount += count;
            }
            var kinship = otherCount > 0 ? kinWeight / otherCount : 0f;
            if (kinship < c.Defines.ConsolidationMinimumKinship) continue;
            var share = mine / (float)total * (0.5f + kinship);
            if (share <= bestShare) continue;
            bestShare = share;
            best = (definition, owners, total);
        }
        return best;
    }

    // A larger holder only has precedence if it could actually lead this project. A
    // subject or a country with an incompatible prior identity must not freeze it forever.
    private static bool CanPursue(Campaign c, Nation n, FormableNation d) => n.Population > 0 && !n.IsSubject &&
        !n.Landless && !c.Fallen.Contains(n.Id) && c.HasOwnedLand(n) && FormableProclamationAi.EstablishedCountry(n) &&
        FormableNations.Eligible(c, n, d) && FormableNations.CompatibleProclamation(n, d) &&
        !FormableNations.SmallerThanCountry(c, n, d) &&
        FormableNations.ReligionMandate(c, n, d) &&
        (d.RequiredIdeology.Length == 0 || d.RequiredIdeology.Contains(n.Ideology, StringComparer.OrdinalIgnoreCase)) &&
        (d.RequiredGovernment.Length == 0 || d.RequiredGovernment.Contains(n.Government, StringComparer.OrdinalIgnoreCase));

    private static Campaign _ownersFor;
    private static int _ownersDay = int.MinValue;
    private static int _ownersRevision = int.MinValue;
    private static readonly Dictionary<string, (Dictionary<int, int> Owners, int Total)> OwnersCache = new(StringComparer.Ordinal);

    /// <summary>Who holds the union's ground, province by province, by map index. Kept for the day.</summary>
    private static (Dictionary<int, int> Owners, int Total) Owners(Campaign c, FormableNation definition)
    {
        if (!ReferenceEquals(_ownersFor, c) || _ownersRevision != c.MapRevision)
        {
            OwnersCache.Clear();
            _ownersFor = c;
            _ownersDay = c.ElapsedDays;
            _ownersRevision = c.MapRevision;
        }
        if (OwnersCache.TryGetValue(definition.Id, out var cached)) return cached;
        var map = c.ProvinceMap;
        var owners = new Dictionary<int, int>();
        var total = 0;
        foreach (var province in PeacefulFormation.Territory(c, definition))
        {
            if (province <= 0 || province > map.ProvinceCount || map.WaterByProvince[province]) continue;
            var owner = map.ProvinceToNation[province];
            if (owner == 0) continue;
            owners[owner] = owners.GetValueOrDefault(owner) + 1;
            total++;
        }
        return OwnersCache[definition.Id] = (owners, total);
    }

    /// <summary>
    /// What a member of our union holding out is worth to a war judgement: nothing for a
    /// government that does not want the union badly enough, and for one that does, its
    /// ambition times the share of the union the other holds.
    /// </summary>
    public static float WarValue(Campaign c, Nation nation, Nation other)
    {
        if (c is null || nation is null || other is null || !c.FeatureEnabled("formable_nations") ||
            string.IsNullOrEmpty(nation.ConsolidationProject)) return 0f;
        if (nation.ConsolidationAmbition < c.Defines.ConsolidationForceAmbition) return 0f;
        var definition = FormableNations.Find(nation.ConsolidationProject);
        if (definition is null || !definition.AiFoundation || !FormableNations.Eligible(c, nation, definition) ||
            !FormableNations.CompatibleProclamation(nation, definition) || !FormableNations.ReligionMandate(c, nation, definition) ||
            (definition.RequiredIdeology.Length > 0 && !definition.RequiredIdeology.Contains(nation.Ideology, StringComparer.OrdinalIgnoreCase)) ||
            (definition.RequiredGovernment.Length > 0 && !definition.RequiredGovernment.Contains(nation.Government, StringComparer.OrdinalIgnoreCase))) return 0f;
        var (owners, total) = Owners(c, definition);
        if (total == 0) return 0f;
        var theirs = owners.GetValueOrDefault(other.MapIndex) / (float)total;
        // Kindred holders only: a union is never a reason to fight a foreign people.
        var kinship = PeacefulFormation.CulturalAffinity(nation, other);
        return Rules.DiplomacyRules.ConsolidationWarValue(
            nation.ConsolidationAmbition, c.Defines.ConsolidationForceAmbition,
            theirs, kinship, c.Defines.AiConsolidationAppetite);
    }

    /// <summary>Checks whether a nation is an emergent splinter, rebel shard, or micro-state.</summary>
    public static bool IsSplinterOrShard(Campaign c, Nation n)
    {
        if (n is null || n.Population <= 0 || n.Movement is not null || n.IsSubject || c.Fallen.Contains(n.Id)) return false;
        if (!c.HasOwnedLand(n)) return false;
        if (n.Tag.StartsWith("R_") || n.Tag.StartsWith("M_")) return true;
        // A shard is a piece the game's own risings and releases made. A small country of 2024 is
        // a nation, not a shard: counted as one, Switzerland "consolidated" Austria on day 143.
        if (!CultureUnion.BornInPlay(n)) return false;
        var states = c.StatesOf(n);
        return states.Length <= c.Defines.ShardMaxStates && n.Population <= c.Defines.ShardMaxPopulation;
    }

    /// <summary>Checks if nation a shares a direct land border with nation b.</summary>
    public static bool ShareBorder(Campaign c, Nation a, Nation b) =>
        c?.ProvinceMap is not null && a is not null && b is not null && a.Id != b.Id &&
        CampaignDiplomacy.SharesLandBorder(c, a.MapIndex, b.MapIndex);

    /// <summary>Willingness of a kindred shard to unify into a coherent state.</summary>
    public static float ShardWillingness(Campaign c, Nation founder, Nation partner)
    {
        var relation = CampaignDiplomacy.Peek(c, founder.Id, partner.Id);
        var opinion = relation?.Opinion ?? 0f;
        var culture = PeacefulFormation.CulturalAffinity(founder, partner);
        var distance = PoliticsCore.IdeologicalDistance(founder, partner);
        var allied = relation?.Stance == DiplomaticStance.Allied ? 20f : 0f;
        var trade = relation?.TradeAgreement == true ? 15f : 0f;
        var nonAggression = relation?.NonAggressionPact == true ? 10f : 0f;
        var govMatch = founder.Government == partner.Government ? 10f : 0f;
        var stability = (founder.Stability - 0.5f) * 20f;
        var relativeSize = Math.Clamp((float)founder.Population / Math.Max(1, partner.Population), 0.5f, 3.0f);
        var sizeBonus = (relativeSize - 1f) * 10f;

        return M.Clamp(opinion * 0.40f + culture * 30f + allied + trade + nonAggression + govMatch + stability + sizeBonus - distance * 25f, 0f, 100f);
    }

    /// <summary>Reviews adjacent kindred splinters and initiates courting or organic unification.</summary>
    public static void ReviewShardConsolidation(Campaign c, Nation founder)
    {
        if (c.ElapsedDays < c.Defines.ShardConsolidationEarliestDay) return;
        if (!IsSplinterOrShard(c, founder)) return;
        if (c.Wars.Any(w => !w.Ended && w.Involves(founder.Id))) return;

        var neighbors = c.Nations.Where(other => other.Id != founder.Id &&
            !c.Fallen.Contains(other.Id) && other.Movement is null && !other.IsSubject &&
            IsSplinterOrShard(c, other) && !c.Wars.Any(w => !w.Ended && w.Involves(other.Id)) &&
            PeacefulFormation.CulturalAffinity(founder, other) >= 0.5f &&
            PoliticsCore.IdeologicalDistance(founder, other) <= 0.65f &&
            ShareBorder(c, founder, other))
            .OrderByDescending(other => ShardWillingness(c, founder, other))
            .ToList();

        if (neighbors.Count == 0) return;

        var partner = neighbors[0];
        // If partner is substantially larger than founder and not a rebel tag, let the larger one initiate
        if (partner.Population > founder.Population * 1.5f && !partner.Tag.StartsWith("R_")) return;

        var offerKey = "shard_offer/" + founder.Id + "/" + partner.Id;
        if (partner.DecisionAvailableDay.GetValueOrDefault(offerKey) > c.ElapsedDays) return;

        var willing = ShardWillingness(c, founder, partner);
        if (willing >= c.Defines.ShardConsolidationAcceptance)
        {
            partner.DecisionAvailableDay[offerKey] = c.ElapsedDays + 60;
            if (!partner.IsHuman || c.NobodyPlays)
            {
                ExecuteShardConsolidation(c, founder, partner);
                return;
            }

            var id = c.PendingEvents.Count == 0 ? 1 : c.PendingEvents.Max(e => e.Id) + 1;
            c.PendingEvents.Add(new PendingCampaignEvent
            {
                Id = id,
                NationId = partner.Id,
                CreatedDay = c.ElapsedDays,
                ExpiresDay = c.ElapsedDays + 30,
                Title = Loc.Format("Unification Proposal from {0}", founder.DisplayName),
                Description = Loc.Format("{0} invites our kindred peoples to merge our lands, armed forces, and institutions into a single unified sovereign state.", founder.DisplayName),
                Options = new[]
                {
                    new CampaignEventOption
                    {
                        Title = "Agree to national unification",
                        Consequence = "Integrate our territory and army into the union.",
                        ShardConsolidateFounderId = founder.Id
                    },
                    new CampaignEventOption
                    {
                        Title = "Remain an independent state",
                        Consequence = "Decline the unification proposal."
                    }
                }
            });
            c.MarkStrategicChange();
            return;
        }

        // Court them if willingness is not yet high enough
        var relation = CampaignDiplomacy.Get(c, founder.Id, partner.Id);
        if (relation is null || relation.Stance == DiplomaticStance.AtWar) return;

        if (relation.Opinion < 35f)
        {
            new ImproveRelationsCommand { IssuerId = founder.Id, TargetId = partner.Id }.Apply(c);
            return;
        }
        if (!relation.TradeAgreement)
        {
            new ProposeAgreementCommand { IssuerId = founder.Id, TargetId = partner.Id, Kind = AgreementKind.TradeAgreement }.Apply(c);
            return;
        }
        if (relation.Stance != DiplomaticStance.Allied)
        {
            new ProposeAllianceCommand { IssuerId = founder.Id, TargetId = partner.Id }.Apply(c);
            return;
        }
    }

    /// <summary>Executes the diplomatic consolidation of two kindred splinter nations.</summary>
    public static void ExecuteShardConsolidation(Campaign c, Nation founder, Nation partner)
    {
        if (c is null || founder is null || partner is null || founder.Id == partner.Id) return;
        if (c.Fallen.Contains(founder.Id) || c.Fallen.Contains(partner.Id)) return;

        // If founder is a generic rebel tag while partner is an established nation, let partner absorb founder
        if (founder.Tag.StartsWith("R_") && !partner.Tag.StartsWith("R_"))
        {
            (founder, partner) = (partner, founder);
        }

        PeacefulFormation.IntegratePartner(c, founder, partner);
        c.Chronicle.Record(c, "shard_consolidated", founder.Id, partner.Id, subject: partner.DisplayName);
        c.MarkStrategicChange();
    }
}

public sealed class ConsolidateSplintersCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int PartnerId { get; init; }
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        Consolidation.ExecuteShardConsolidation(campaign, campaign.NationById(IssuerId), campaign.NationById(PartnerId));
    }
}
