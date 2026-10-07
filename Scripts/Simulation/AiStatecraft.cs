using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// What a country does when nobody is playing it.
///
/// Until now: nothing. The operational AI moved divisions once a war existed, but no war
/// ever started - `ai_war_chance` was declared in the defines and read by nothing at all,
/// so two hundred and sixty countries sat at peace forever, never raised a tax, never
/// built anything, never improved or soured a relationship, and never signed a peace they
/// were winning. The scenario's one war was the only thing that ever happened.
///
/// This is the other half of an AI: the half that decides. Every country forms opinions of
/// its neighbours and acts on them; keeps its budget solvent; builds what it is short of;
/// and, when a neighbour is weak, hostile and worth taking, declares war on it and later
/// writes a peace out of what it won.
///
/// It is deliberately cheap. Countries are staggered across the week and the month so no
/// single day pays for all of them, and the border graph every decision leans on is built
/// once per change to the map rather than once per question.
/// </summary>
public static partial class AiStatecraft
{
    /// <summary>
    /// A running count of what the AI decided and why it stopped deciding. Not campaign
    /// state and not saved: it exists so "the AI never does anything" can be answered with
    /// the step it actually fails at rather than with a shrug.
    /// </summary>
    public static readonly Dictionary<string, int> Tally = new(StringComparer.Ordinal);

    private static void Count(string what) => Tally[what] = Tally.GetValueOrDefault(what) + 1;

    /// <summary>Military strength and a map-index lookup, both good for exactly one day.</summary>
    private static readonly Dictionary<int, float> StrengthCache = new();
    private static readonly Dictionary<int, Nation> NationByMapIndex = new();

    /// <summary>Regions each country holds, by map index. Rebuilt with the strength table.</summary>
    private static readonly Dictionary<int, int> RegionCount = new();

    /// <summary>
    /// The countries everybody has an opinion about, and who has already signed what.
    ///
    /// Both are here rather than worked out inside the weekly pass because both are
    /// questions about the whole world: the great powers are one sort over three hundred
    /// countries, and the signatures are one walk of every relationship there is. Asked once
    /// a day they are free; asked by each country for each of its neighbours they were the
    /// most expensive thing the AI did, which is the mistake the tables above already fixed
    /// once.
    /// </summary>
    private static readonly List<Nation> GreatPowers = new();
    private static readonly Dictionary<int, List<int>> SignedWith = new();
    private static int _cacheDay = -1;

    /// <summary>
    /// Which campaign the caches below describe.
    ///
    /// They were stamped with the day alone, which is correct for exactly one world at a
    /// time. Two are not a hypothetical: a multiplayer client rebuilding itself from a host
    /// snapshot holds the new campaign and the old one at once, and whichever ticks first
    /// stamps the day - after which the other reads a table of some other world's armies and
    /// some other world's map indices, agrees with nobody, and desynchronises without an
    /// error anywhere. The same is true of every bench check that works on a campaign of its
    /// own, which is how this was found.
    /// </summary>
    private static Campaign _cacheCampaign;

    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(AiStatecraft))) return;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;

        // An offer nobody answered is withdrawn rather than left standing forever, so a
        // player who ignored one is asked again later instead of never.
        PeaceOffers.Expire(campaign);

        using (SimProfiler.Measure("  ai:foundations")) PeacefulFormationAi.Tick(campaign);
        EnsureDailyTables(campaign);
        ThinkAboutTheNeighbours(campaign);
    }

    /// <summary>
    /// What the AI needs to know about the world today, worked out once for everybody.
    ///
    /// Military strength is a sum over every division in the world, and it was being
    /// recomputed for each neighbour of each country every week - which made the AI the
    /// most expensive system in the simulation by a factor of three. It changes once a day
    /// at most, so it is worked out once a day.
    ///
    /// The same is true of how much ground a country holds, which is how anyone without
    /// intelligence on a stranger guesses at its strength. That was a walk of the world's
    /// three thousand regions, with a predicate closing over the stranger, for every
    /// neighbour of every country every week.
    ///
    /// Stamped with the campaign as well as the day: a client rebuilding from a host's
    /// snapshot holds two campaigns at once, and a table that only knew what day it was
    /// would answer one of them with the other's world.
    /// </summary>
    private static void EnsureDailyTables(Campaign campaign)
    {
        if (_cacheDay == campaign.ElapsedDays && ReferenceEquals(_cacheCampaign, campaign)) return;
        _cacheDay = campaign.ElapsedDays;
        _cacheCampaign = campaign;
        StrengthCache.Clear();
        NationByMapIndex.Clear();
        OverseasCache.Clear();
        RegionCount.Clear();
        foreach (var candidate in campaign.Nations)
        {
            NationByMapIndex[candidate.MapIndex] = candidate;
            StrengthCache[candidate.Id] = candidate.Gdp * campaign.Defines.AiEconomicWeight;
        }
        foreach (var state in campaign.States)
            RegionCount[state.NationMapIndex] = RegionCount.GetValueOrDefault(state.NationMapIndex) + 1;
        foreach (var army in campaign.Armies)
            if (StrengthCache.ContainsKey(army.NationId))
                StrengthCache[army.NationId] += army.Divisions * army.Strength *
                                                M.Max(0.15f, army.Organisation);

        GreatPowers.Clear();
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Population <= 0) continue;
            GreatPowers.Add(candidate);
        }
        GreatPowers.Sort((left, right) =>
        {
            var difference = StrengthCache.GetValueOrDefault(right.Id)
                .CompareTo(StrengthCache.GetValueOrDefault(left.Id));
            return difference != 0 ? difference : left.Id.CompareTo(right.Id);
        });
        if (GreatPowers.Count > campaign.Defines.AiGreatPowers)
            GreatPowers.RemoveRange(campaign.Defines.AiGreatPowers,
                GreatPowers.Count - campaign.Defines.AiGreatPowers);

        SignedWith.Clear();
        foreach (var relation in campaign.Relations.Values)
        {
            var signed = relation.Stance == DiplomaticStance.Allied ||
                         relation.NonAggressionPact || relation.TradeAgreement ||
                         relation.GuaranteeByA || relation.GuaranteeByB;
            if (!signed) continue;
            if (!SignedWith.TryGetValue(relation.A, out var forA))
                SignedWith[relation.A] = forA = new List<int>();
            forA.Add(relation.B);
            if (!SignedWith.TryGetValue(relation.B, out var forB))
                SignedWith[relation.B] = forB = new List<int>();
            forB.Add(relation.A);
        }
    }

    /// <summary>
    /// The countries this one has an opinion about this week.
    ///
    /// Four reasons, in the order a foreign ministry would give them: we share a border, we
    /// have already signed something, they are large enough that everybody has a view, and
    /// they are fighting the country we like least. A country with none of the four - and
    /// there are a few, mostly islands - has no foreign policy this week, which is honest.
    /// </summary>
    private static readonly List<Nation> Circle = new();
    private static readonly HashSet<int> CircleSeen = new();

    private static void BuildCircle(Campaign campaign, Nation nation, Nation worstRival)
    {
        Circle.Clear();
        CircleSeen.Clear();
        CircleSeen.Add(nation.Id);

        void Consider(Nation other)
        {
            if (other is null || other.Population <= 0 || !CircleSeen.Add(other.Id)) return;
            Circle.Add(other);
        }

        foreach (var mapIndex in NeighboursOf(campaign, nation.MapIndex)) Consider(ByMapIndex(mapIndex));
        if (SignedWith.TryGetValue(nation.Id, out var partners))
            foreach (var partner in partners) Consider(campaign.NationById(partner));
        foreach (var power in GreatPowers) Consider(power);
        if (worstRival is not null)
            foreach (var war in campaign.Wars)
            {
                if (war.Ended || !war.Involves(worstRival.Id)) continue;
                Consider(campaign.NationById(war.AttackerId == worstRival.Id ? war.DefenderId : war.AttackerId));
            }
    }

    /// <summary>How much ground a country holds, from the day's table.</summary>
    internal static int RegionsHeldBy(Campaign campaign, int mapIndex)
    {
        if (campaign is null) return 0;
        EnsureDailyTables(campaign);
        return RegionCount.GetValueOrDefault(mapIndex);
    }

    private static void ThinkAboutTheNeighbours(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0) continue;

            // A player's country is governed by its player - except for the portfolios that
            // player has handed to its own civil service, which are run here on the same
            // monthly beat as everybody else's. See `Delegation`.
            if (nation.IsHuman)
            {
                if ((campaign.ElapsedDays + nation.Id) % 30 == 0)
                    using (SimProfiler.Measure("  ai:delegated")) Delegation.Tick(campaign, nation);
                continue;
            }

            // Staggered: a nation thinks about its neighbours once a week and about its
            // budget once a month, on its own day, so the cost is spread rather than spiked.
            if ((campaign.ElapsedDays + nation.Id) % 7 == 0)
                using (SimProfiler.Measure("  ai:diplomacy")) Diplomacy(campaign, nation);
            if ((campaign.ElapsedDays + nation.Id) % 30 == 0)
                using (SimProfiler.Measure("  ai:economy")) Economy(campaign, nation);
            if ((campaign.ElapsedDays + nation.Id) % 11 == 0)
                using (SimProfiler.Measure("  ai:warmaking")) Warmaking(campaign, nation);
        }
    }

    /// <summary>Countries this one can walk to. One shared index, built once per map change.</summary>
    public static IReadOnlyCollection<int> NeighboursOf(Campaign campaign, int nationMapIndex) =>
        CampaignDiplomacy.Neighbours(campaign, nationMapIndex);

    private static readonly List<int> ReachableTargets = new();
    private static readonly HashSet<int> ReachableSeen = new();
    private static readonly Dictionary<int, float> OverseasDistance = new();

    // The overseas half of the candidate list only changes when the map does, or when a
    // navy is built or sunk. Working it out afresh for every country every eleven days -
    // two hundred and sixty distance tests and a linear search of a list of the same
    // length for each of them - was measured at a millisecond and a quarter per country in
    // the running game, which is most of what a campaign day cost.
    private static readonly Dictionary<int, (int Hulls, List<int> Targets)> OverseasCache = new();

    /// <summary>
    /// Countries this one could actually fight: everyone it can walk to, and - if it has a
    /// navy and its enemies' shores are within that navy's reach - the ones it could sail
    /// to as well.
    ///
    /// The overseas half is new, and it is the whole reason the naval invasion exists. It
    /// is bounded on purpose: a country will not declare on the far side of the planet
    /// because it owns a frigate, and the reach is the same one its admiralty will use when
    /// it looks for a beach - so a war it declares is a war it can prosecute.
    /// </summary>
    private static IReadOnlyList<int> Reachable(Campaign campaign, Nation nation)
    {
        ReachableTargets.Clear();
        ReachableSeen.Clear();
        foreach (var mapIndex in CampaignDiplomacy.Neighbours(campaign, nation.MapIndex))
            if (ReachableSeen.Add(mapIndex)) ReachableTargets.Add(mapIndex);

        var hulls = 0;
        var fromLatitude = 0f;
        var fromLongitude = 0f;
        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.NationId != nation.Id || fleet.Hulls <= 0) continue;
            if (hulls == 0) { fromLatitude = fleet.HomeLatitude; fromLongitude = fleet.HomeLongitude; }
            hulls += fleet.Hulls;
        }
        if (hulls < campaign.Defines.AiInvasionMinimumHulls) return ReachableTargets;

        // Which shores are within reach is a fact about where the capitals and the home
        // port are, not about today's borders, so it is worked out once and kept until the
        // fleet itself changes size.
        //
        // And it is the nearest handful rather than everything inside the radius. Offering
        // two hundred and fifty candidates meant two hundred and fifty CanDeclareWar calls
        // and two hundred and fifty appraisals of desire, per country, every eleven days -
        // and CanDeclareWar creates a diplomatic relation for any pair that has never had
        // one, so merely thinking about the world was allocating a relation with all of it.
        // A country contemplates its nearest overseas neighbours, which is also all its
        // navy could reach in a reasonable time.
        if (!OverseasCache.TryGetValue(nation.Id, out var cached) || cached.Hulls != hulls)
        {
            var targets = cached.Targets ?? new List<int>();
            targets.Clear();
            var reach = campaign.Defines.AiInvasionSearchDegrees;
            var wanted = Math.Max(1, campaign.Defines.AiOverseasTargetsConsidered);
            foreach (var other in campaign.Nations)
            {
                if (other.Id == nation.Id || other.Population <= 0) continue;
                var distance = NavalOperations.Distance(fromLatitude, fromLongitude,
                    other.CapitalLatitude, other.CapitalLongitude);
                if (distance > reach) continue;
                targets.Add(other.MapIndex);
                OverseasDistance[other.MapIndex] = distance;
            }
            targets.Sort((left, right) =>
            {
                var order = OverseasDistance.GetValueOrDefault(left, float.MaxValue)
                    .CompareTo(OverseasDistance.GetValueOrDefault(right, float.MaxValue));
                return order != 0 ? order : left.CompareTo(right);
            });
            if (targets.Count > wanted) targets.RemoveRange(wanted, targets.Count - wanted);
            OverseasDistance.Clear();
            cached = (hulls, targets);
            OverseasCache[nation.Id] = cached;
        }

        foreach (var mapIndex in cached.Targets)
            if (ReachableSeen.Add(mapIndex)) ReachableTargets.Add(mapIndex);
        return ReachableTargets;
    }

    // --------------------------------------------------------------- diplomacy ---------

    /// <summary>
    /// One country's weekly view of its neighbourhood. Opinions drift toward what the two
    /// countries actually are to each other - a shared border is friction, a shared trade
    /// agreement is not - and then the country does one thing about the neighbour it feels
    /// most strongly about, in either direction.
    /// </summary>
    private static void Diplomacy(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;

        Nation bestFriend = null;
        Nation worstRival = null;
        var bestOpinion = float.MinValue;
        var worstOpinion = float.MaxValue;

        // Last week's worst relationship decides who counts as "fighting our enemy" this
        // week, which is the one term of the circle that needs an answer before it is built.
        BuildCircle(campaign, nation, campaign.NationById(nation.WarPlanTargetId));
        if (Circle.Count == 0) return;
        var neighbours = NeighboursOf(campaign, nation.MapIndex);

        foreach (var other in Circle)
        {
            if (other is null || other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
            if (relation.Stance == DiplomaticStance.AtWar) continue;

            // Where this relationship naturally sits, written as the same named reasons a
            // war or a denunciation is written in - so the standing part of an opinion can
            // be explained to the player and retuned by a mod, rather than being five
            // numbers in Defines.cs that nothing on any screen accounts for.
            if (neighbours.Contains(other.MapIndex))
                OpinionSystem.Remember(campaign, relation, "shared_border");
            else OpinionSystem.Forget(campaign, relation, "shared_border");
            if (relation.Stance == DiplomaticStance.Allied)
                OpinionSystem.Remember(campaign, relation, "alliance");
            else OpinionSystem.Forget(campaign, relation, "alliance");

            var strength = CampaignIntel.EstimatedStrength(campaign, nation.Id, other) / M.Max(0.01f, Strength(campaign, nation));
            var fear = M.Clamp(strength - 1f, 0f, 2f);
            if (fear > 0.05f) OpinionSystem.Remember(campaign, relation, "fear_of_strength", fear);
            else OpinionSystem.Forget(campaign, relation, "fear_of_strength");

            if (relation.Opinion > bestOpinion) { bestOpinion = relation.Opinion; bestFriend = other; }
            if (relation.Opinion < worstOpinion) { worstOpinion = relation.Opinion; worstRival = other; }
        }

        // One action a week. Try the most promising friends in opinion order: the warmest
        // partner may be human (and cannot be signed for), cooling down, or already bound
        // by every treaty. None of those should freeze the rest of foreign policy.
        if (bestFriend is not null && bestOpinion >= defines.AiFriendshipThreshold &&
            MathF.Abs(bestOpinion) >= MathF.Abs(worstOpinion))
        {
            FriendlyCandidates.Clear();
            foreach (var other in Circle)
            {
                if (other.Id == nation.Id) continue;
                var bond = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
                if (bond.Stance != DiplomaticStance.AtWar &&
                    bond.Opinion >= defines.AiFriendshipThreshold)
                    FriendlyCandidates.Add(other);
            }
            FriendlyCandidates.Sort((left, right) =>
            {
                var byOpinion = CampaignDiplomacy.Get(campaign, nation.Id, right.Id).Opinion
                    .CompareTo(CampaignDiplomacy.Get(campaign, nation.Id, left.Id).Opinion);
                return byOpinion != 0 ? byOpinion : left.Id.CompareTo(right.Id);
            });
            foreach (var friend in FriendlyCandidates)
                if (TryFriendlyMove(campaign, nation, friend)) return;
        }

        // Frightened, and somebody large enough to be worth asking: ask for a guarantee.
        //
        // Not courtship. Courting a distant great power costs a week now and pays in five
        // months for a pact and two years for an alliance, which is past the end of most
        // campaigns - two versions of that were measured and both cost seventeen pacts and
        // bought nothing. A guarantee is the instrument that fits: it commits nothing until
        // somebody attacks, it is decided in one week rather than two years, and it makes
        // the country asking measurably less worth invading, because the war judgement
        // counts a guarantor exactly as it counts an ally.
        if (worstRival is not null && worstOpinion <= defines.AiRivalryThreshold &&
            CampaignIntel.EstimatedStrength(campaign, nation.Id, worstRival) >
            Strength(campaign, nation) * defines.AiCounterweightFear &&
            AskForShelter(campaign, nation, worstRival))
            return;

        // Nothing has crossed a threshold yet, which is where every relationship starts:
        // neighbours dislike each other by default, so warmth has to be worked at. A
        // country spends the week courting whichever neighbour it is least unhappy with.
        // Left to drift alone, no two countries in the world would ever sign anything.
        if (bestFriend is not null && (worstRival is null || bestOpinion > defines.AiRivalryThreshold))
        {
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, bestFriend.Id);
            if (relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Friendly)
            {
                // Through the opinion system, not around it. This used to write the number
                // directly, and the weekly settle - which computes an opinion as the sum of
                // its named reasons - erased it every seven days. A country could court a
                // neighbour for a decade and end exactly where it started, which is why in
                // two years of a world running itself not one alliance was ever signed.
                new ImproveRelationsCommand { IssuerId = nation.Id, TargetId = bestFriend.Id }.Apply(campaign);
                return;
            }
        }

        if (worstRival is not null && worstOpinion <= defines.AiRivalryThreshold)
        {
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, worstRival.Id);
            if (relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Friendly)
            {
                // A denunciation is a visible, remembered diplomatic action, not an
                // unexplained stance switch. It observes the same mission cooldown.
                var lastAction = relation.LastActionDay;
                new DenounceCommand { IssuerId = nation.Id, TargetId = worstRival.Id }.Apply(campaign);
                if (relation.LastActionDay != lastAction) Count("denounced");
                return;
            }

            // Naming a rival used to be the end of the AI's imagination. There are things
            // between a stiff note and an invasion, and now that trade is bilateral and a
            // guarantee brings an army, they are worth doing: sanction a rival who needs us
            // more than we need them, or shelter whoever is next on their list.
            ActShortOfWar(campaign, nation, worstRival);
        }
    }

    // ---------------------------------------------------------------- economy ----------

    /// <summary>
    /// A month of governing. Nothing clever - a treasury that is emptying gets taxed, a
    /// country at war spends on its army, an angry country spends on its people, and
    /// whatever is left over is built with. It is the difference between a country that
    /// develops and one that is a fixed set of numbers with an army standing on it.
    /// </summary>
    private static void Economy(Campaign campaign, Nation nation)
    {
        GovernTheBudget(campaign, nation);
        using (SimProfiler.Measure("    eco:cabinet")) Cabinet(campaign, nation);
    }

    /// <summary>
    /// Tax, the army's share, what is spent on people, and what is built with the rest.
    ///
    /// Split out of <see cref="Economy"/> so a player can hand this portfolio to their own
    /// civil service and get exactly what every other country in the world gets - see
    /// <see cref="Delegation"/>. Deliberately NOT including the cabinet: a decision is a
    /// thing a country does once and cannot undo, and delegating the budget is not consent
    /// to have your government reformed while you were looking at the map.
    /// </summary>
    public static void GovernTheBudget(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return;
        var defines = campaign.Defines;
        // Any() with a closure over the nation, on a list of a hundred and forty wars,
        // for every country that thinks about its budget.
        var atWar = false;
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
        var reserves = nation.Gdp <= 0f ? 0f : nation.Treasury / nation.Gdp;

        // Tax. Broke countries tax more; comfortable ones with an unhappy population tax
        // less, because the alternative to a rebate is a riot.
        var taxTarget = nation.TaxRate;
        if (reserves < defines.AiTreasuryFloor) taxTarget += defines.AiTaxStep;
        else if (reserves > defines.AiTreasuryComfort && nation.Stability < 0.6f) taxTarget -= defines.AiTaxStep;
        nation.TaxRate = M.Clamp(taxTarget, defines.AiMinimumTaxRate, defines.AiMaximumTaxRate);

        // The army costs what the situation is worth.
        bool threatened;
        using (SimProfiler.Measure("    eco:threat"))
            threatened = !atWar && (nation.WarPlanTargetId >= 0 || Threatened(campaign, nation));
        var militaryTarget = atWar
            ? defines.AiWartimeMilitarySpending
            : threatened ? defines.AiTenseMilitarySpending : defines.AiPeacetimeMilitarySpending;
        nation.MilitarySpending = M.Lerp(nation.MilitarySpending, militaryTarget, 0.35f);

        // And the war industry follows the budget it is part of. A country at war runs
        // its plants harder; a country at peace spins them mostly back down. The AI sets
        // the same capacity pool the player uses, by the same rule it sets the military budget
        // by - so the world's economies behave like the player's rather than around it.
        var focusTarget = atWar
            ? defines.AiWartimeArmsFocus
            : threatened ? defines.AiWartimeArmsFocus * 0.5f : defines.AiPeacetimeArmsFocus;
        nation.ArmsFocus = M.Lerp(nation.ArmsFocus, focusTarget, 0.20f);
        // AI policy is quantised by the same capacity allocator as player orders.
        nation.ConvertedCivilianUnits = -1;

        // An unhappy country buys quiet, when it can afford it.
        if ((nation.Stability < defines.AiStabilityConcern || RadicalismCore.NeedsAttention(campaign, nation)) &&
            reserves > defines.AiTreasuryFloor)
        {
            nation.WelfareSpending = M.Clamp(nation.WelfareSpending + defines.AiSocialStep, 0f, 0.18f);
            nation.EducationSpending = M.Clamp(nation.EducationSpending + defines.AiSocialStep * 0.6f, 0f, 0.15f);
        }
        else if (reserves < defines.AiTreasuryFloor * 0.5f)
        {
            nation.WelfareSpending = M.Clamp(nation.WelfareSpending - defines.AiSocialStep, 0.01f, 0.20f);
        }

        using (SimProfiler.Measure("    eco:build")) Build(campaign, nation, atWar, reserves);
    }

    /// <summary>
    /// One decision a month, if the country wants one enough.
    ///
    /// Decisions existed and no AI had ever taken one: two hundred and sixty governments
    /// sat on the same five levers for the whole campaign while the player pulled them.
    /// How much a country wants a decision is a scripted score - `ai_will_do` in the
    /// decision, resolved out of `common/scripted_modifiers` - so tuning an AI that
    /// mobilises too eagerly is a line in a file rather than a rebuild, and a mod's own
    /// decisions are weighed by the same rule as the shipped ones.
    ///
    /// A decision with no score declared is never taken. That is deliberate: an AI that
    /// takes anything a modder forgot to weigh is worse than one that waits to be told.
    /// </summary>
    private static void Cabinet(Campaign campaign, Nation nation)
    {
        var bestScore = campaign.Defines.AiDecisionThreshold;
        var best = "";
        foreach (var definition in DecisionCatalog.All)
        {
            if (string.IsNullOrEmpty(definition.AiWillDo)) continue;
            if (DecisionCatalog.IsBlocked(campaign, nation, definition)) continue;
            var score = ScriptLibrary.Score(campaign, nation, definition.AiWillDo);
            if (score <= bestScore) continue;
            bestScore = score;
            best = definition.Id;
        }
        if (best.Length == 0) return;
        new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = best }.Apply(campaign);
    }

    /// <summary>
    /// One project a month, in the region that most needs it. What a country builds says
    /// what it thinks is wrong with itself: an army that cannot be fed builds roads, a
    /// country at war builds guns, and a country at peace builds the thing that makes it
    /// richer next year.
    /// </summary>
    private static void Build(Campaign campaign, Nation nation, bool atWar, float reserves)
    {
        var defines = campaign.Defines;
        if (reserves < defines.AiBuildTreasuryFloor) return;
        var running = 0;
        foreach (var project in campaign.Construction)
            if (project.NationId == nation.Id && ++running >= defines.AiMaxConcurrentProjects) return;

        var states = campaign.StatesOf(nation);
        if (states.Length == 0) return;

        var congested = false;
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id && army.SupplyCongestion < 0.75f) { congested = true; break; }
        var wanted =
            congested ? BuildingKind.Infrastructure :
            atWar ? BuildingKind.ArmsIndustry :
            nation.Literacy < 0.72f ? BuildingKind.University :
            nation.StandardOfLiving < 0.55f ? BuildingKind.Agriculture :
            BuildingKind.CivilianIndustry;

        // Put it where there is most to gain: the biggest region that has fewest of these.
        ScenarioState target = null;
        var bestScore = float.MinValue;
        foreach (var state in states)
        {
            var already = state.Buildings.GetValueOrDefault(wanted);
            var score = state.Population / 1_000_000f - already * defines.AiBuildSaturation;
            if (score <= bestScore) continue;
            bestScore = score;
            target = state;
        }
        if (target is null) return;

        new QueueConstructionCommand
        {
            IssuerId = nation.Id,
            StateMapIndex = target.MapIndex,
            Building = wanted
        }.Apply(campaign);
    }

    // ------------------------------------------------------------- war and peace -------

    /// <summary>
    /// Whether to start a war, and whether to end one. Both questions are asked of the
    /// same country on the same day, because a country that is already fighting should not
    /// be shopping for a second front.
    /// </summary>
    private static void Warmaking(Campaign campaign, Nation nation)
    {
        // By hand: Find's predicate closes over `nation`, so asking whether we are already
        // at war allocated a closure and a delegate every time we asked - which is once a
        // week for every country in the world.
        StrategicWar ownWar = null;
        foreach (var candidate in campaign.Wars)
            if (!candidate.Ended && candidate.Involves(nation.Id)) { ownWar = candidate; break; }
        if (ownWar is not null) { Negotiate(campaign, nation, ownWar); return; }
        if (!campaign.Defines.AiMayDeclareWar) return;

        var defines = campaign.Defines;

        // First, whatever we already decided to do about somebody. A country with a plan
        // spends its week on the plan rather than on shopping for a different war.
        PursueWarPlan(campaign, nation);

        if (nation.WarSupport < defines.AiWarSupportFloor) return;
        if (nation.Stability < defines.AiWarStabilityFloor) return;

        Nation target = null;
        var bestJudgement = float.MinValue;
        foreach (var mapIndex in Reachable(campaign, nation))
        {
            var other = ByMapIndex(mapIndex);
            if (other is null || other.Id == nation.Id) continue;
            Count("considered");
            if (other.IsHuman && !defines.AiMayAttackPlayer) continue;
            if (!CampaignDiplomacy.CanDeclareWar(campaign, nation.Id, other.Id, out _)) { Count("blocked"); continue; }

            // What it is worth, minus what it costs. Subtracted rather than multiplied,
            // because the price has to be able to win the argument outright - and under the
            // old multiplicative rule nothing ever could.
            var judgement = WarJudgement(campaign, nation, other);
            if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, other.MapIndex))
                judgement -= defines.AiOverseasCaution;
            if (judgement > 0f) Count("wanted");
            if (judgement <= bestJudgement) continue;
            if (judgement > 0f && !CanStageWar(campaign, nation, other)) { Count("unreachable_or_unready"); continue; }
            bestJudgement = judgement;
            target = other;
        }
        if (target is null || bestJudgement <= 0f)
        {
            ClearWarPlan(nation);
            return;
        }

        // Worth having, but not yet worth having now. A government that reaches this point
        // does not forget the idea - it keeps the target, builds, and starts prising the
        // target's friends away. That is the whole of the difference between an AI that is
        // less aggressive and one that is simply slower.
        if (bestJudgement < defines.AiWarDesireThreshold)
        {
            if (nation.WarPlanTargetId == target.Id) return;
            nation.WarPlanTargetId = target.Id;
            nation.WarPlanSinceDay = campaign.ElapsedDays;
            Count("planned");
            return;
        }
        Count("targeted");
        if (!PreparationComplete(campaign, nation, target)) { Count("preparing"); return; }

        // Even a country that wants a war does not start one on a particular Tuesday.
        // The die keeps the map from lurching every time a balance of power tips.
        //
        // It is drawn from the country and the date rather than from the campaign's shared
        // stream on purpose. Sharing it meant that adding or removing one AI decision
        // reshuffled every later roll in the world - so a change to how countries build
        // roads moved the Russo-Ukrainian front by twenty provinces, and no measurement of
        // one system could be trusted after a change to any other.
        var roll = Roll(campaign.Seed, nation.Id, campaign.ElapsedDays);
        if (roll > defines.AiWarChance * bestJudgement) return;

        // A country that has been preparing for this one goes sooner; the preparation is the
        // point of having made the plan.
        if (nation.WarPlanTargetId == target.Id) ClearWarPlan(nation);
        if (CampaignDiplomacy.DeclareWar(campaign, nation.Id, target.Id) is not null) Count("declared");
    }


    /// <summary>
    /// A country at war asks, periodically, whether it is done. Winning it writes a peace
    /// out of the ground it actually holds - through the same conference a player uses -
    /// and losing it offers to stop.
    /// </summary>
    internal static void Negotiate(Campaign campaign, Nation nation, StrategicWar war)
    {
        var defines = campaign.Defines;
        // Overrun: one side's leader has lost all its ground in this war and nobody on that side
        // has a formation left. There is nothing to fight with or bargain with, so it is decided,
        // lock or no lock, and it is settled from whichever side sits down first - the beaten
        // country may have no people left in its hands, and a country with none never
        // negotiates. Such wars used to go on for years with the winner's army standing about on
        // a front that no longer existed: India held the whole of Siachen for two years with a
        // hundred and twenty divisions assigned to it, Germany all of Liechtenstein, Italy San
        // Marino. One province was not war score enough to write a treaty with, and the white
        // peace the beaten side could offer would have handed it back.
        if (war.PendingOfferFromId < 0 &&
            campaign.ElapsedDays - war.LastPeaceProposalDay >= defines.AiPeaceProposalInterval)
            foreach (var attackers in new[] { true, false })
            {
                if (!SideOverrun(campaign, war, attackers)) continue;
                var beaten = campaign.NationById(attackers ? war.AttackerId : war.DefenderId);
                var victor = campaign.NationById(attackers ? war.DefenderId : war.AttackerId);
                if (beaten is null || victor is null) break;
                PeaceOffers.Put(campaign, war, beaten, victor, PeaceTerms.Capitulate);
                return;
            }
        // A breakaway beaten in the field gives itself up, lock or no lock: most of its ground
        // held by the state it left, or nobody left in it who believes in the war (Reintegration).
        if (war.PendingOfferFromId < 0 && Reintegration.ShouldSurrender(campaign, war, nation) &&
            campaign.NationById(war.EnemyLeaderOf(nation.Id)) is { } government)
        {
            PeaceOffers.Put(campaign, war, nation, government, PeaceTerms.Capitulate);
            return;
        }
        // A decided war is negotiated whatever the scenario's opening lock says: the lock keeps the
        // wars on the map from ending in their first weeks, not a beaten country from surrendering.
        if (CivilWarPersistence.NegotiationLocked(campaign, war)) return;
        if (campaign.ElapsedDays - war.StartedDay < defines.AiMinimumWarDays) return;
        if (campaign.ElapsedDays - war.LastPeaceProposalDay < defines.AiPeaceProposalInterval) return;

        var opponentId = war.EnemyLeaderOf(nation.Id);
        var opponent = campaign.NationById(opponentId);
        if (opponent is null) return;

        // A settlement already on their table is a settlement being thought about. Asking
        // again over the top of it is how a foreign ministry loses an argument it was
        // winning, and it is also how a player gets three modal dialogs in a row.
        if (war.PendingOfferFromId >= 0) return;

        var ledger = new PeaceLedger(campaign, war, nation.Id);
        if (ledger.Budget < defines.AiPeaceMinimumBudget)
        {
            // A state that is ahead against its own breakaway does not offer to stop: the
            // ground is its own, and a white peace would hand back what it has retaken.
            var ownScore = war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore;
            if (ownScore > 0f && Reintegration.Reclaiming(campaign, war, nation.Id)) return;
            // Not winning - which is not the same as wanting to stop. A country only walks
            // away from a war it started or is defending when the country itself has had
            // enough, and that takes years rather than a bad season. Otherwise the map
            // settles every border it has within a year of the campaign opening.
            // Or when the war has stood still long enough, or simply gone on too long: then the
            // peace of a deadlock - the line as the border where anyone holds anything, a white
            // peace where nobody does (WarWeariness).
            if (nation.WarSupport < defines.AiCapitulationWarSupport &&
                campaign.ElapsedDays - war.StartedDay > defines.AiMinimumWarDays * 3)
                PeaceOffers.Put(campaign, war, nation, opponent, PeaceTerms.WhitePeace);
            else if (WarWeariness.TimeToStop(campaign, war, winning: false))
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
            return;
        }

        // Winning. Write the peace a country would actually write.
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = nation.Id };
        var decisive = WriteDecisiveSettlement(campaign, nation, war, ledger, demand);
        // A state winning against its own breakaway is not selling it anything - an indemnity, a
        // client government - for the ground. It writes all of it back once the war is decided,
        // and until then it fights for the rest (Reintegration).
        if (!decisive && Reintegration.Reclaiming(campaign, war, nation.Id)) return;
        if (!decisive)
        {
            WriteDemand(campaign, nation, war, ledger, demand, ledger.Budget * defines.AiPeaceBudgetShare);
            ConsiderSubjugation(campaign, nation, war, ledger, demand, ledger.Budget * defines.AiPeaceBudgetShare);
        }

        if (demand.Provinces.Count == 0 && demand.Subjugate.Length == 0)
        {
            // Ahead on points and holding none of their ground. A country in that position
            // does not stop - it pushes, because stopping converts a war it is winning into
            // nothing at all. Only a genuinely broken opponent is worth an indemnity.
            var opponentSupport = 0.5f; // Unknown; do not read the opponent's internal morale.
            if (ledger.Budget >= defines.AiPeaceIndemnityBudget &&
                opponentSupport <= defines.AiCapitulationWarSupport)
                PeaceOffers.Put(campaign, war, nation, opponent, PeaceTerms.WarReparations);
            // Ahead on points for years and holding nothing: it is over, and it is time to say so.
            else if (WarWeariness.TimeToStop(campaign, war, winning: true))
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
            return;
        }

        PeaceOffers.Put(campaign, war, nation, opponent, demand);
    }

    /// <summary>
    /// Whether one side of a war has been overrun: its leader lost ground in this war and holds
    /// none of its own anywhere, and no country on that side has a formation left. A government
    /// in exile with an army, or an ally still in the field, is fighting on and is not this.
    /// </summary>
    internal static bool SideOverrun(Campaign campaign, StrategicWar war, bool attackers)
    {
        var control = campaign.ProvinceController;
        var leader = campaign.NationById(attackers ? war.AttackerId : war.DefenderId);
        if (control is null || leader is null) return false;
        var own = (ushort)leader.MapIndex;
        var lostHere = false;
        foreach (var (_, was) in war.OriginalController)
            if (was == own) { lostHere = true; break; }
        if (!lostHere) return false;
        var side = new HashSet<int>(war.Side(attackers));
        foreach (var army in campaign.Armies)
            if (side.Contains(army.NationId)) return false;
        for (var province = 1; province < control.Length; province++)
            if (control[province] == own) return false;
        return true;
    }

    /// <summary>
    /// Whether to ask for the country instead of asking for its provinces.
    ///
    /// A winner that can afford to annex a country annexes it. This is the other case, and
    /// it is the commoner one: a war won against somebody too large to swallow, where the
    /// choice used to be a strip of border provinces or nothing. What a state actually did
    /// there was leave the country standing and take its decisions - and until the AI could
    /// reach that, every dependency in the game had to be typed into the console by hand.
    ///
    /// Only for a war that is not already eating them. Half a country annexed and the other
    /// half made to answer is not a settlement any foreign ministry has written; it is a
    /// player with a budget left over.
    /// </summary>
    internal static void ConsiderSubjugation(Campaign campaign, Nation nation, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand, float budget)
    {
        var loser = campaign.NationById(war.EnemyLeaderOf(nation.Id));
        if (loser is null || loser.IsSubject) return;

        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        var claimable = PeaceConference.Claimable(campaign, war, nation.Id)
            .Count(province => topology.ProvinceToNation[province] == loser.MapIndex);
        var takenFromSubject = demand.Provinces.Count(province => topology.ProvinceToNation[province] == loser.MapIndex);
        if (claimable == 0 || takenFromSubject * 2 >= claimable) return;

        var left = budget - ledger.Cost(demand);
        SubjectType best = null;
        var bestCost = 0f;
        foreach (var kind in SubjectCatalog.Imposable)
        {
            var cost = PeaceConference.SubjugationCost(campaign, nation, loser, kind.Id);
            if (cost > left || cost <= bestCost) continue;
            best = kind;
            bestCost = cost;
        }
        if (best is not null) demand.Subjugate = best.Id;
    }

    /// <summary>
    /// Write a peace out of the ground a country is standing on.
    ///
    /// The rule this replaces was "cheapest first", which is what a spreadsheet would do
    /// and nothing a country has ever done. It produced the same treaty every time: a
    /// scatter of the emptiest provinces on the map, chosen because empty ground is cheap,
    /// leaving the winner with a border like a torn edge and none of what it went to war
    /// for.
    ///
    /// Four things decide instead, in the order a foreign ministry would weigh them.
    ///
    /// The war goal comes first. A country that declared for a province and then annexed
    /// three others because they were going cheap did not win its war; Paradox games make
    /// the war goal the one thing the AI will always take, and so does this.
    ///
    /// Then contiguity. Ground that touches what you already hold is worth far more than
    /// the same ground in the middle of theirs, because an enclave has to be garrisoned
    /// forever and cannot be defended at all. A province touching nothing of yours is
    /// nearly refused outright rather than merely discouraged.
    ///
    /// Then finishing what you started. Half a region is the worst of both: the loser keeps
    /// a grievance and a salient, and the winner gets neither the whole nor a straight
    /// border. Each province already taken from a region makes the rest of it cheaper to
    /// want.
    ///
    /// And only then value for money - people per point of war score - which is what the
    /// old rule used alone.
    /// </summary>
    internal static void WriteDemand(Campaign campaign, Nation nation, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand, float budget)
    {
        var topology = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (topology is null || controllers is null || budget <= 0f) return;

        var candidates = PeaceConference.Claimable(campaign, war, nation.Id)
            .Where(province => controllers[province] == (ushort)nation.MapIndex)
            .ToList();
        if (candidates.Count == 0)
        {
            // There is no enemy ground to take - the winner may be standing on ten provinces
            // and every one of them its own, retaken. That is a settlement with no land in
            // it, not the absence of a settlement: an indemnity, a truce and the rest are
            // still on the table and still paid for out of the same war score.
            //
            // Returning here without writing them was how a country that won a defensive war
            // signed a piece of paper with nothing on it.
            WriteTerms(campaign, nation, war, ledger, demand, budget);
            return;
        }

        // Region by region, from the border outwards, and settled so neither side is left in
        // pieces (PeaceShape). The province-by-province greedy that stood here wrote treaties out
        // of wherever the front had stopped, then rounded them into whole regions - no cuts
        // inside a region, and a patchwork of regions dropped anywhere in the loser's country.
        PeaceShape.Write(campaign, nation, war, ledger, demand, budget);

        WriteTerms(campaign, nation, war, ledger, demand, budget);
    }

    /// <summary>
    /// Spend what the land did not on the rest of the treaty.
    ///
    /// A settlement is not only a border. A winner with war score left over and nothing
    /// worth annexing with it was, until now, simply throwing it away - it signed for the
    /// provinces and walked out, leaving an indemnity it had earned on the table. These are
    /// taken in the order a foreign ministry would take them, which is not the order of
    /// their price.
    ///
    /// Money first, because an indemnity costs the winner nothing and is spent the day it
    /// arrives. Then a decade of quiet rather than five years, because a country that has
    /// just taken land is a country that expects to be asked for it back. Then trade, then
    /// their army - and humiliation last and only against an opponent already broken, since
    /// against anyone else it buys nothing but a grievance that outlives the treaty.
    /// </summary>
    private static void WriteTerms(Campaign campaign, Nation nation, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand, float budget)
    {
        var defines = campaign.Defines;
        var spent = ledger.Cost(demand);

        bool Afford(float price) => spent + price <= budget;

        if (Afford(defines.PeaceCostReparations))
        {
            demand.Reparations = true;
            spent += defines.PeaceCostReparations;
        }
        if (demand.Provinces.Count > 0 && Afford(defines.PeaceCostLongTruce))
        {
            demand.LongTruce = true;
            spent += defines.PeaceCostLongTruce;
        }
        if (Afford(defines.PeaceCostTradeConcessions))
        {
            demand.TradeConcessions = true;
            spent += defines.PeaceCostTradeConcessions;
        }
        if (Afford(defines.PeaceCostDemilitarise))
        {
            demand.Demilitarise = true;
            spent += defines.PeaceCostDemilitarise;
        }

        var opponentSupport = 0.5f; // Unknown under first-stage fog of war.
        if (opponentSupport <= defines.AiCapitulationWarSupport && Afford(defines.PeaceCostHumiliate))
            demand.Humiliate = true;
    }

    // ------------------------------------------------------------------ helpers --------

    /// <summary>
    /// What a country is worth in a fight: the divisions it has, weighted by what state
    /// they are in, plus what its economy could raise if the war lasted.
    /// </summary>
    /// <summary>
    /// The same figure, for the parts of statecraft that are not the AI's own decisions -
    /// whether an ally can afford to honour a promise, most of all.
    /// </summary>
    public static float StrengthOf(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0;
        // Diplomacy/UI can query before the first AI tick or between commands. Never
        // read another campaign's daily cache, or a stale pre-settlement army total.
        var strength = nation.Gdp * campaign.Defines.AiEconomicWeight;
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id)
                strength += army.Divisions * army.Strength * M.Max(.15f, army.Organisation);
        return strength;
    }

    /// <summary>A country's strength from today's cache when it is this campaign's, and counted afresh when it is not.</summary>
    internal static float CachedStrength(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0f;
        if (ReferenceEquals(_cacheCampaign, campaign) && _cacheDay == campaign.ElapsedDays &&
            StrengthCache.TryGetValue(nation.Id, out var value)) return value;
        return StrengthOf(campaign, nation);
    }

    private static float Strength(Campaign campaign, Nation nation) =>
        nation is not null && StrengthCache.TryGetValue(nation.Id, out var value)
            ? value
            : nation?.Gdp * campaign.Defines.AiEconomicWeight ?? 0f;

    /// <summary>A nation by its map index, without walking the whole list to find it.</summary>
    private static Nation ByMapIndex(int mapIndex) =>
        NationByMapIndex.TryGetValue(mapIndex, out var nation) ? nation : null;

    /// <summary>
    /// A deterministic value in [0, 1) for one country on one day. Same campaign, same
    /// answer, every time - and independent of everything else that draws randomness.
    /// </summary>
    private static float Roll(int seed, int nationId, int day)
    {
        var hash = (uint)seed * 2654435761u ^ (uint)nationId * 2246822519u ^ (uint)day * 3266489917u;
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        hash *= 3266489917u;
        hash ^= hash >> 16;
        return (hash >> 8) * (1f / 16777216f);
    }

    /// <summary>Whether anybody next door is at war, which is reason enough to spend.</summary>
    /// <summary>
    /// Ask somebody large to stand behind us against the country we are afraid of.
    ///
    /// Who is worth asking is not who is heaviest. Nepal, frightened of India, could reach
    /// China, the United States and Russia; the first version of this asked Russia, because
    /// Russia is the largest thing within reach - and Russia is on good terms with India and
    /// fighting a war of its own, so Russia said no, and the country that would have said
    /// yes was never asked. Measured, in a probe, after three guesses that were not.
    ///
    /// So the choice and the answer are one question. Each candidate is scored by what it
    /// would actually answer - what it thinks of us, whether it is steady, whether it is
    /// already fighting, and above all whether it is wary of the same country - and the
    /// request goes to the best of them, if that is good enough at all.
    /// </summary>
    internal static bool AskForShelter(Campaign campaign, Nation nation, Nation rival)
    {
        var defines = campaign.Defines;
        var rivalStrength = CampaignIntel.EstimatedStrength(campaign, nation.Id, rival);

        // Somebody already stands behind us against this: stop shopping.
        //
        // Without this a country asked again every week it was frightened, and since it only
        // ever skipped the ONE country it had already asked, it collected protectors: 483
        // guarantees stood in a world of 280 countries, and a world where everybody is
        // guaranteed is a world where nothing can be attacked - wars fell from 29 to 24 and
        // annexations from 61 to 38, which is not peace, it is a mechanic overreaching.
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != nation.Id && relation.B != nation.Id) continue;
            var patronId = relation.Other(nation.Id);
            if (!relation.GuaranteedBy(patronId) && relation.Stance != DiplomaticStance.Allied) continue;
            var patron = campaign.NationById(patronId);
            if (patron is null) continue;
            if (CampaignIntel.EstimatedStrength(campaign, nation.Id, patron) > rivalStrength) return false;
        }

        // Its own circle, built here rather than read out of the shared one: this used to
        // read whatever the weekly pass had left in the static list, which is right exactly
        // when that pass is the caller and silently wrong the moment anybody else asks.
        BuildCircle(campaign, nation, rival);

        Nation best = null;
        var bestAnswer = float.MinValue;
        foreach (var other in Circle)
        {
            // A human decides whether their country guarantees somebody. An AI plea
            // cannot create that commitment on the player's behalf.
            if (other.Id == rival.Id || other.Id == nation.Id || other.IsSubject || other.IsHuman) continue;

            var ours = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
            if (ours.GuaranteedBy(other.Id) || ours.Stance == DiplomaticStance.AtWar) continue;
            // A country you merely mistrust is a country you can still ask; one you have
            // named a rival is not.
            if (ours.Stance == DiplomaticStance.Rival) continue;

            var theirs = CampaignDiplomacy.Peek(campaign, other.Id, rival.Id);
            if (theirs is { Stance: DiplomaticStance.Allied }) continue;
            if (CampaignIntel.EstimatedStrength(campaign, nation.Id, other) <= rivalStrength) continue;

            // What they would say. Their opinion of us counts for half of what it would in
            // an alliance - a great power does not shelter a small country because it is
            // fond of it - and what the shelter is against counts for most of it.
            var answer = ours.Opinion / 200f + M.Clamp(other.Stability - 0.5f, -0.3f, 0.3f);
            if (theirs is not null && theirs.Opinion <= defines.AiRivalryThreshold)
                answer += defines.AiShelterSharedRival;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(other.Id))
                {
                    // Not a refusal: a guarantee commits nothing today, and a power already
                    // fighting is if anything more interested in who else is drawn in. It
                    // makes them harder to ask, not impossible.
                    answer -= defines.AiShelterWarPenalty;
                    break;
                }

            if (answer <= bestAnswer) continue;
            bestAnswer = answer;
            best = other;
        }

        if (best is null || bestAnswer < defines.AiShelterWillingness) return false;

        new GuaranteeCommand { IssuerId = best.Id, TargetId = nation.Id, Guarantee = true }
            .Apply(campaign);
        Count("shelter_granted");
        CampaignDiplomacy.Get(campaign, nation.Id, best.Id).LastActionDay = campaign.ElapsedDays;
        DiagnosticLogs.Write("simulation",
            $"SHELTER {best.Tag} guarantees {nation.Tag} against {rival.Tag} " +
            $"answer={bestAnswer:0.00} day={campaign.ElapsedDays}");
        return true;
    }

    private static bool Threatened(Campaign campaign, Nation nation)
    {
        foreach (var mapIndex in NeighboursOf(campaign, nation.MapIndex))
        {
            var other = ByMapIndex(mapIndex);
            if (other is null) continue;
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(other.Id)) return true;
            if (CampaignDiplomacy.Get(campaign, nation.Id, other.Id).Stance == DiplomaticStance.Rival) return true;
        }
        return false;
    }
}
