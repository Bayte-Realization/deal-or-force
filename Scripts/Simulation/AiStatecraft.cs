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

    /// <summary>
    /// One campaign's daily statecraft tables - military strength, a map-index lookup,
    /// regions held, the great powers and who has signed what.
    ///
    /// They used to be static scratch stamped with the last campaign that asked, which is
    /// correct for exactly one world at a time: a second campaign stepping beside the first
    /// flipped the stamp every time it ticked, so the first world's tables were rebuilt
    /// from its own mid-day state every hour where the solo run kept the morning's answer.
    /// Same inputs, different freshness, different wars - the march-pace divergence one
    /// table over. Per-campaign tables make "worked out once a day" mean the same thing in
    /// both runs.
    /// </summary>
    internal sealed class DailyTables
    {
        internal int Day = int.MinValue;
        /// <summary>False until the first <see cref="EnsureDailyTables"/> has filled the maps.</summary>
        internal bool Built;
        internal readonly Dictionary<int, float> StrengthCache = new();
        internal readonly Dictionary<int, Nation> NationByMapIndex = new();
        internal readonly Dictionary<int, int> RegionCount = new();
        internal readonly List<Nation> GreatPowers = new();
        internal readonly Dictionary<int, List<int>> SignedWith = new();
        internal readonly Dictionary<int, (int Hulls, List<int> Targets)> Overseas = new();
        internal int OverseasNationCount = -1;
        internal readonly Dictionary<int, List<int>> FriendsOf = new();
        internal int FriendsDay = int.MinValue;
    }

    private static DailyTables TablesOf(Campaign campaign) =>
        campaign.AiStatecraftTables ??= new DailyTables();

    /// <summary>
    /// The countries everybody has an opinion about, and who has already signed what, are
    /// part of <see cref="DailyTables"/> - questions about the whole world that are free
    /// once a day and ruinous asked by each country for each of its neighbours.
    /// </summary>

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The day's statecraft in stages, a country at a time, for the smoothed step.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(AiStatecraft))) yield break;
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) yield break;

        using (SimProfiler.Measure("statecraft"))
        {
            // An offer nobody answered is withdrawn rather than left standing forever, so a
            // player who ignored one is asked again later instead of never.
            PeaceOffers.Expire(campaign);

            using (SimProfiler.Measure("  ai:foundations"))
            {
                FormableProclamationAi.Tick(campaign);
                PeacefulFormationAi.Tick(campaign);
            }
            EnsureDailyTables(campaign);
        }
        yield return 0;
        foreach (var _ in ThinkAboutTheNeighbours(campaign)) yield return 0;
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
        var tables = TablesOf(campaign);
        if (tables.Day == campaign.ElapsedDays) return;
        tables.Day = campaign.ElapsedDays;
        tables.Built = true;
        tables.Overseas.Clear();
        tables.OverseasNationCount = -1;
        var strength = tables.StrengthCache;
        var byMapIndex = tables.NationByMapIndex;
        var regions = tables.RegionCount;
        strength.Clear();
        byMapIndex.Clear();
        regions.Clear();
        foreach (var candidate in campaign.Nations)
        {
            byMapIndex[candidate.MapIndex] = candidate;
            strength[candidate.Id] = candidate.Gdp * campaign.Defines.AiEconomicWeight;
        }
        foreach (var state in campaign.States)
            regions[state.NationMapIndex] = regions.GetValueOrDefault(state.NationMapIndex) + 1;
        foreach (var army in campaign.Armies)
            if (strength.ContainsKey(army.NationId))
                strength[army.NationId] += ArmyStrengthTerm(army);

        var powers = tables.GreatPowers;
        powers.Clear();
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Population <= 0) continue;
            powers.Add(candidate);
        }
        powers.Sort((left, right) =>
        {
            var difference = strength.GetValueOrDefault(right.Id)
                .CompareTo(strength.GetValueOrDefault(left.Id));
            return difference != 0 ? difference : left.Id.CompareTo(right.Id);
        });
        if (powers.Count > campaign.Defines.AiGreatPowers)
            powers.RemoveRange(campaign.Defines.AiGreatPowers,
                powers.Count - campaign.Defines.AiGreatPowers);

        var signed = tables.SignedWith;
        signed.Clear();
        foreach (var relation in campaign.Relations.Values)
        {
            var isSigned = relation.Stance == DiplomaticStance.Allied ||
                         relation.NonAggressionPact || relation.TradeAgreement ||
                         relation.GuaranteeByA || relation.GuaranteeByB;
            if (!isSigned) continue;
            if (!signed.TryGetValue(relation.A, out var forA))
                signed[relation.A] = forA = new List<int>();
            forA.Add(relation.B);
            if (!signed.TryGetValue(relation.B, out var forB))
                signed[relation.B] = forB = new List<int>();
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
        EnsureDailyTables(campaign);

        void Consider(Nation other)
        {
            if (other is null || other.Population <= 0 || !CircleSeen.Add(other.Id)) return;
            Circle.Add(other);
        }

        var tables = TablesOf(campaign);
        foreach (var mapIndex in NeighboursOf(campaign, nation.MapIndex)) Consider(ByMapIndex(campaign, mapIndex));
        foreach (var org in InternationalOrganizations.Memberships(campaign, nation.Id))
            foreach (var member in org.Members) Consider(campaign.NationById(member));
        if (tables.SignedWith.TryGetValue(nation.Id, out var partners))
            foreach (var partner in partners) Consider(campaign.NationById(partner));
        foreach (var power in tables.GreatPowers) Consider(power);
        if (worstRival is not null)
        {
            var live = Warfare.LiveWars(campaign);
            for (var index = 0; index < live.Count; index++)
            {
                var war = live[index];
                if (war.Ended || !war.Involves(worstRival.Id)) continue;
                Consider(campaign.NationById(war.AttackerId == worstRival.Id ? war.DefenderId : war.AttackerId));
            }
        }
    }

    /// <summary>How much ground a country holds, from the day's table.</summary>
    internal static int RegionsHeldBy(Campaign campaign, int mapIndex)
    {
        if (campaign is null) return 0;
        EnsureDailyTables(campaign);
        return TablesOf(campaign).RegionCount.GetValueOrDefault(mapIndex);
    }

    private static IEnumerable<int> ThinkAboutTheNeighbours(Campaign campaign)
    {
        for (var index = 0; index < campaign.Nations.Count; index++)
        {
            var nation = campaign.Nations[index];
            if (nation.Population <= 0) continue;
            // A stage ends after each country that had something to think about today.
            if (!ThinksToday(campaign, nation)) continue;
            using (SimProfiler.Measure("statecraft")) ThinkToday(campaign, nation);
            yield return 0;
        }
    }

    private static bool ThinksToday(Campaign campaign, Nation nation)
    {
        var day = campaign.ElapsedDays + nation.Id;
        if (nation.IsHuman) return day % 30 == 0 || (day % 7 == 0 && Delegation.Holds(nation, Delegation.Diplomacy));
        return day % 7 == 0 || day % 30 == 0 || day % 11 == 0;
    }

    private static void ThinkToday(Campaign campaign, Nation nation)
    {
        {

            // A player's country is governed by its player - except for the portfolios that
            // player has handed to its own civil service, which are run here on the same
            // monthly beat as everybody else's. See `Delegation`.
            if (nation.IsHuman)
            {
                if ((campaign.ElapsedDays + nation.Id) % 30 == 0)
                    using (SimProfiler.Measure("  ai:delegated")) Delegation.Tick(campaign, nation);
                // The foreign ministry keeps the same weekly round as everybody else's.
                if ((campaign.ElapsedDays + nation.Id) % 7 == 0 && Delegation.Holds(nation, Delegation.Diplomacy))
                    using (SimProfiler.Measure("  ai:delegated")) Diplomacy(campaign, nation);
                return;
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
        var tables = TablesOf(campaign);
        if (tables.OverseasNationCount != campaign.Nations.Count)
        {
            tables.Overseas.Clear();
            tables.OverseasNationCount = campaign.Nations.Count;
        }
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
        if (!tables.Overseas.TryGetValue(nation.Id, out var cached) || cached.Hulls != hulls)
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
            tables.Overseas[nation.Id] = cached;
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
        using (SimProfiler.Measure("    dip:circle"))
            BuildCircle(campaign, nation, campaign.NationById(nation.WarPlanTargetId));
        // A patron with nobody next door can still be the only friend of a government
        // that is coming apart. The empty circle used to end the week before that was asked.
        if (Circle.Count == 0)
        {
            if (TryOfferCrisisGrant(campaign, nation)) return;
            TryHedgeSupply(campaign, nation);
            return;
        }
        var neighbours = NeighboursOf(campaign, nation.MapIndex);

        foreach (var other in Circle)
        {
            using var opinionSample = SimProfiler.Measure("    dip:opinions");
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
            if (campaign.FeatureEnabled("state_identity") && StateIdentity.ShareDeclaredFaith(nation, other))
                OpinionSystem.Remember(campaign, relation, "shared_state_faith");
            else OpinionSystem.Forget(campaign, relation, "shared_state_faith");

            var strength = CampaignIntel.EstimatedStrength(campaign, nation.Id, other) / M.Max(0.01f, Strength(campaign, nation));
            // And only as far as the stronger one can reach. Fear of strength was the same at
            // every distance, so the strongest country in the world was feared at full weight
            // by every government in its circle - France and Brazil held minus thirty-two
            // against China for being large - and that one line was most of everything the
            // world held against anybody. A neighbour fears it in full, a region less, the far
            // side of the world hardly at all; an ally a quarter as much, because a strong
            // friend is a worry and not a threat.
            var reach = OpinionWitnesses.FearReach(campaign, nation, other);
            if (relation.Stance == DiplomaticStance.Allied) reach *= 0.25f;
            var fear = M.Clamp(strength - 1f, 0f, 2f) * reach;
            // The pair shares one relation, and only the weaker of the two is afraid. The
            // stronger one leaves the entry alone: when each wrote its own view in turn, the
            // weaker remembered the fear and the stronger forgot it, every week, and the
            // reason flickered on and off the diplomacy page.
            if (fear > 0.05f) OpinionSystem.Remember(campaign, relation, "fear_of_strength", fear);
            else if (strength >= 0.95f) OpinionSystem.Forget(campaign, relation, "fear_of_strength");

            if (relation.Opinion > bestOpinion) { bestOpinion = relation.Opinion; bestFriend = other; }
            if (relation.Opinion < worstOpinion) { worstOpinion = relation.Opinion; worstRival = other; }
        }

        // One action a week. Try the most promising friends in opinion order: the warmest
        // partner may be human (and cannot be signed for), cooling down, or already bound
        // by every treaty. None of those should freeze the rest of foreign policy.
        if (bestFriend is not null && bestOpinion >= defines.AiFriendshipThreshold &&
            MathF.Abs(bestOpinion) >= MathF.Abs(worstOpinion))
        {
            using var friendlySample = SimProfiler.Measure("    dip:friendly");
            FriendlyCandidates.Clear();
            FriendlyOpinion.Clear();
            foreach (var other in Circle)
            {
                if (other.Id == nation.Id) continue;
                // Read, not created: no relation means no friendship to build on yet.
                if (CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is not { } bond) continue;
                if (bond.Stance != DiplomaticStance.AtWar &&
                    bond.Opinion >= defines.AiFriendshipThreshold)
                {
                    FriendlyCandidates.Add(other);
                    FriendlyOpinion[other.Id] = bond.Opinion;
                }
            }
            FriendlyCandidates.Sort((left, right) =>
            {
                var byOpinion = FriendlyOpinion[right.Id].CompareTo(FriendlyOpinion[left.Id]);
                return byOpinion != 0 ? byOpinion : left.Id.CompareTo(right.Id);
            });
            // Failed candidates cannot start a mission. A successful action exits this
            // scan immediately; the command itself still rechecks the live mission limit.
            var missionScan = new DiplomaticOutreach.OfferScan(campaign, nation.Id);
            var allianceScan = new AllianceSystem.OfferScan(campaign);
            // The warmest eight: past them a week's diplomacy is not going anywhere new.
            var tries = 0;
            foreach (var friend in FriendlyCandidates)
            {
                if (++tries > 8) break;
                if (TryFriendlyMove(campaign, nation, friend, missionScan, allianceScan)) return;
            }
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
        using (SimProfiler.Measure("    dip:shelter"))
        if (worstRival is not null && worstOpinion <= defines.AiRivalryThreshold &&
            CampaignIntel.EstimatedStrength(campaign, nation.Id, worstRival) >
            Strength(campaign, nation) * defines.AiCounterweightFear &&
            AskForShelter(campaign, nation, worstRival))
            return;

        // Still frightened, and no patron came: buy the bully's peace outright. A weak
        // country cannot out-build the gap, but it can pay for a pact with the neighbour
        // it fears - the deal table's own rule that the strong side does not sell what it
        // could take, so the cheque only ever lands where the fear was real.
        // And only from somebody who could actually come - a neighbour, or a power that can put
        // an army ashore - and is a real danger rather than merely disliked. Canada was paying
        // Germany eight billion for a pact across the Atlantic.
        // Check the cheap refusal gates first. Naval feasibility performs real route
        // searches; an existing pact or an insufficient threat makes that work pointless.
        // The weekly opinion pass has already refreshed this rival's intelligence estimate.
        using (SimProfiler.Measure("    dip:buy_peace"))
        if (worstRival is not null && worstOpinion <= defines.AiRivalryThreshold &&
            nation.Treasury > 0.01f &&
            CampaignDiplomacy.Peek(campaign, nation.Id, worstRival.Id) is { NonAggressionPact: false, Stance: not DiplomaticStance.AtWar } &&
            Strength(campaign, nation) < CampaignIntel.EstimatedStrength(campaign, nation.Id, worstRival) &&
            ThreatPosture.ThreatOf(campaign, nation, worstRival) >= defines.AiBuyPeaceThreat &&
            (neighbours.Contains(worstRival.MapIndex) || CanStageWar(campaign, worstRival, nation)) &&
            BuyPeace(campaign, nation, worstRival))
            return;

        // A week spent on our own people across the fence, before a week spent on being liked.
        // The cheque is the deal table's. A player on the other side of it still has to sign.
        using (SimProfiler.Measure("    dip:kindred"))
        if (TryBuyKindredLand(campaign, nation))
            return;

        // A client whose institutions are failing, after our own fear and our own people.
        // The cheque is the ordinary deal table. A player still has to sign.
        using (SimProfiler.Measure("    dip:crisis_grant"))
        if (TryOfferCrisisGrant(campaign, nation))
            return;

        // A scarce good that arrives from one partner, before a week spent only on being liked.
        // The paper is a trade agreement. A player who is asked still has to sign. No land moves.
        using (SimProfiler.Measure("    dip:supply_hedge"))
        if (TryHedgeSupply(campaign, nation))
            return;

        // Nothing has crossed a threshold yet, which is where every relationship starts:
        // neighbours dislike each other by default, so warmth has to be worked at. A
        // country spends the week courting whichever neighbour it is least unhappy with.
        // Left to drift alone, no two countries in the world would ever sign anything.
        using (SimProfiler.Measure("    dip:courting"))
        {
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
    }

    /// <summary>
    /// Offer one cash grant to the ally or guarantor-client whose institutions are furthest
    /// gone. No land is on the table. An AI client answers by the deal table; a player
    /// gets the paper and nothing moves until they sign.
    /// </summary>
    internal static bool TryOfferCrisisGrant(Campaign campaign, Nation nation)
    {
        var defines = campaign?.Defines;
        if (defines is null || nation is null || nation.IsHuman || nation.Movement is not null) return false;
        if (!defines.AiCrisisGrants || !campaign.FeatureEnabled("deals")) return false;
        if (!float.IsFinite(nation.Stability) || nation.Stability < defines.CivilWarStabilityThreshold) return false;

        Nation client = null;
        var worst = float.MaxValue;
        foreach (var other in campaign.Nations)
        {
            if (other.Id == nation.Id || other.Movement is not null) continue;
            if (nation.DecisionAvailableDay.GetValueOrDefault("crisis_grant/" + other.Id) > campaign.ElapsedDays) continue;
            if (!StandsBehind(campaign, nation, other) || !InstitutionsFailing(campaign, other)) continue;
            if (CampaignDiplomacy.AtWar(campaign, nation.Id, other.Id)) continue;
            if (other.Stability < worst || (other.Stability == worst && client is not null && other.Id < client.Id))
            {
                worst = other.Stability;
                client = other;
            }
        }
        if (client is null) return false;
        if (!Ultimatum.CanNegotiateDeal(campaign, nation, client, out _)) return false;
        var package = AgreementSystem.AidAmount(campaign, nation, client, out _);
        if (!float.IsFinite(package) || package < 0.01f) return false;
        var fiscal = AiFiscalPolicy.Assess(campaign, nation, defines.AiTreasuryFloor);
        if (!fiscal.CanSpend) return false;
        package = MathF.Min(package, fiscal.DiscretionaryCash);
        if (package < 0.01f) return false;

        var deal = new Ultimatum.Deal(new Ultimatum.Side { Cash = package }, new Ultimatum.Side());
        var outcome = DealNegotiations.Submit(campaign, nation, client, deal);
        var pending = campaign.DealOffers.Any(offer => offer.Pending && offer.FromId == nation.Id && offer.ToId == client.Id);
        if (!outcome.Accepted && !pending) return false;
        nation.DecisionAvailableDay["crisis_grant/" + client.Id] =
            campaign.ElapsedDays + Math.Max(30, defines.AiCrisisGrantRetryDays);
        Count("crisis_grant");
        return true;
    }

    private static bool StandsBehind(Campaign campaign, Nation patron, Nation client)
    {
        var relation = CampaignDiplomacy.Peek(campaign, patron.Id, client.Id);
        if (relation is null || relation.Stance == DiplomaticStance.AtWar) return false;
        return relation.Stance == DiplomaticStance.Allied || relation.GuaranteedBy(patron.Id);
    }

    /// <summary>
    /// The same strain a civil war reads, including in a player's country when random
    /// outbreaks are switched off. A subject is somebody else's crisis.
    /// </summary>
    private static bool InstitutionsFailing(Campaign campaign, Nation client)
    {
        if (client.IsSubject || campaign.Fallen.Contains(client.Id) || client.Population <= 0) return false;
        var civil = Warfare.LiveWars(campaign);
        for (var index = 0; index < civil.Count; index++)
        {
            var war = civil[index];
            if (!war.Ended && war.CivilConflict && war.Involves(client.Id)) return true;
        }
        var defines = campaign.Defines;
        if (!float.IsFinite(client.Stability) || client.Stability >= defines.CivilWarStabilityThreshold) return false;
        if (client.Population < defines.CivilWarMinimumPopulation) return false;
        var regions = 0;
        foreach (var state in campaign.StatesOf(client))
            if (state.Population > 0) regions++;
        return regions >= defines.CivilWarMinimumRegions;
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
    ///
    /// This is the cabinet's monthly sitting, including for a player who handed it over
    /// (Delegation).
    /// </summary>
    public static void RunTheCabinet(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return;
        Cabinet(campaign, nation);
    }

    private static void Cabinet(Campaign campaign, Nation nation)
    {
        var bestScore = campaign.Defines.AiDecisionThreshold;
        var best = "";
        foreach (var definition in DecisionCatalog.All)
        {
            if (string.IsNullOrEmpty(definition.AiWillDo)) continue;
            if (DecisionCatalog.IsBlocked(campaign, nation, definition)) continue;
            if (!DecisionResources.CanAfford(nation, definition, keepAiReserve: true)) continue;
            var score = ScriptLibrary.Score(campaign, nation, definition.AiWillDo);
            if (score <= bestScore) continue;
            bestScore = score;
            best = definition.Id;
        }
        if (best.Length == 0) return;
        new TakeDecisionCommand { IssuerId = nation.Id, DecisionId = best }.Apply(campaign);
    }


    // ------------------------------------------------------------- war and peace -------

    /// <summary>
    /// Whether to start a war, and whether to end one. Both questions are asked of the
    /// same country on the same day, because a country that is already fighting should not
    /// be shopping for a second front.
    /// </summary>
    private static void Warmaking(Campaign campaign, Nation nation)
    {
        // Every war it is in, not just the first one in the world's list. A country fighting three
        // wars asked about whichever war the enumeration happened to reach first and never spoke
        // about the other two: the bench found them at minus twenty with LastNegotiation empty and
        // no offer ever made, for years, while the same country's first war was negotiated normally.
        // A snapshot, because a settlement can end a war and a list being read must not change.
        var negotiating = false;
        var live = Warfare.LiveWars(campaign);
        var open = new StrategicWar[live.Count];
        for (var index = 0; index < live.Count; index++) open[index] = live[index];
        foreach (var existing in open)
        {
            if (existing.Ended || !existing.Involves(nation.Id)) continue;
            // In trouble, ask somebody in before deciding whether to ask the enemy for terms.
            SeekRescue(campaign, nation, existing);
            Negotiate(campaign, nation, existing);
            negotiating = true;
        }
        if (negotiating) return;
        if (!campaign.Defines.AiMayDeclareWar) return;
        var defines = campaign.Defines;
        var fiscal = AiFiscalPolicy.Assess(campaign, nation, defines.AiTreasuryFloor);
        if (!fiscal.CanSpend)
        {
            if (fiscal.Reason != AiFiscalReason.ShortRunway || nation.PaymentArrears > 0 || nation.Treasury < nation.Gdp * defines.AiTreasuryFloor * 0.25f)
            {
                Count("war_fiscal_" + fiscal.Reason);
                return;
            }
        }

        // A partition agreed with allies is this week's business when it happens: the same
        // government cannot go shopping for a private war on top of carving up a neighbour.
        if (TryCoordinatePartition(campaign, nation)) return;

        // First, whatever we already decided to do about somebody. A country with a plan
        // spends its week on the plan rather than on shopping for a different war.
        PursueWarPlan(campaign, nation);

        if (nation.WarSupport < defines.AiWarSupportFloor) return;
        if (nation.Stability < defines.AiWarStabilityFloor) return;

        Nation target = null;
        var bestJudgement = float.MinValue;
        foreach (var mapIndex in Reachable(campaign, nation))
        {
            var other = ByMapIndex(campaign, mapIndex);
            if (other is null || other.Id == nation.Id) continue;
            Count("considered");
            if (other.IsHuman && !defines.AiMayAttackPlayer) continue;

            // An ally is the one target whose refusal has nothing to do with appetite -
            // CanDeclareWar answers "break the alliance first" - so for an ally the sum has to be
            // worked out first, or every ally in the world stays hidden from the arithmetic and an
            // alliance remains a permanent fact rather than an arrangement. For everybody else the
            // cheap refusal gates stay exactly where they were: a truce, a pact or a missing route
            // makes the sum pointless work, and this loop asks about every neighbour of every
            // country every week.
            var allied = CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied };
            if (!allied && !CampaignDiplomacy.CanDeclareWar(campaign, nation.Id, other.Id, out _))
            { Count("blocked"); continue; }

            // What it is worth, minus what it costs. Subtracted rather than multiplied,
            // because the price has to be able to win the argument outright - and under the
            // old multiplicative rule nothing ever could.
            var judgement = WarJudgement(campaign, nation, other);
            // A small, kindred or simply helpless neighbour is worth more than the ordinary
            // border arithmetic says. This is appetite, not a free cause: WarPrice still
            // charges the notoriety and the restraint, and CasusBelli still has to name it.
            var appetite = AnnexationAmbition.Appetite(campaign, nation, other);
            if (appetite > 0f) judgement += defines.AiAnnexationTargetBonus * appetite;
            // And an ally is let past only when that sum cleared the nerve inside WarJudgement,
            // which is the one path that returns a positive number for one.
            if (allied && judgement <= 0f) { Count("blocked"); continue; }
            if (!CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, other.MapIndex))
                judgement -= defines.AiOverseasCaution;
            if (judgement > 0f) Count("wanted");
            if (!TerritorialClaims.PreferTarget(campaign, nation, other, judgement, target, bestJudgement)) continue;
            if (judgement > 0f && !CanStageWar(campaign, nation, other)) { Count("unreachable_or_unready"); continue; }
            bestJudgement = judgement;
            target = other;
        }
        if (target is null || bestJudgement <= 0f)
        {
            // Counted, because this is where the pair-level `wanted` becomes a nation-level
            // nothing: a country can judge several neighbours worth fighting and still leave the
            // loop with no target it would rather have than its current one. Without this line
            // the per-pair and per-nation counts cannot be reconciled at all, and "the AI wants
            // 104 wars and declares 2" reads as cowardice when part of it is arithmetic.
            Count("no_target");
            ClearWarPlan(nation);
            return;
        }

        // A country that can swallow this neighbour outright does not ask for one region
        // first. The aim is the whole state, and the peace writer is told so by the same
        // word the declaration carries.
        var annexation = AnnexationAmbition.Aim(campaign, nation, target);

        // Worth having, but not yet worth having now. A government that reaches this point
        // does not forget the idea - it keeps the target, builds, and starts prising the
        // target's friends away. That is the whole of the difference between an AI that is
        // less aggressive and one that is simply slower.
        //
        // The threshold is not a fixed number, because a government is not a thermostat. It is
        // drawn from the country and the week, so it holds steady inside a week and is identical
        // on every machine playing this seed, and it is swung by that country's volatility: a
        // staid government's threshold barely moves, an ordinary one's moves by a quarter of the
        // swing, and a mercurial one's by nearly all of it. The swing is symmetric, so it does
        // not make the world more warlike - it makes it less schedulable, which is the thing a
        // player actually notices.
        var mercurial = 1f + (Roll(campaign.Seed, nation.Id, campaign.ElapsedDays / 7) - 0.5f) * 2f *
                        defines.AiVolatilitySwing * M.Clamp01(AiTemperament.Of(campaign, nation).Volatility);
        if (bestJudgement < defines.AiWarDesireThreshold * mercurial)
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
        // How readily this country actually moves, as opposed to how good its case is: the
        // doctrine's aggression multiplies the chance, so a cautious state waits for a better
        // hand and a reckless one acts on a worse one. Everything else - the cause, the
        // readiness, the reach and the ultimatum - still has to be in place before this line.
        //
        // Counted, because it was the one gate in the funnel with no tally, and the gap it
        // leaves is not small: a year reports wanted=104 against declared=2, and without this
        // line the difference cannot be attributed between the die, PreparationComplete and
        // the plan book - which is precisely the question AI-05 asks first.
        var prepDays = nation.WarPlanSinceDay >= 0 ? campaign.ElapsedDays - nation.WarPlanSinceDay : 0;
        var prepBonus = Math.Min(1.0f, Math.Max(0, prepDays - defines.AiWarPreparationDays) / 60f);
        var warChance = defines.AiWarChance * bestJudgement * AiStrategyCatalog.Of(campaign, nation).Aggression * (1.25f + prepBonus);
        if (roll > warChance)
        { Count("rolled_down"); return; }

        // An ultimatum threatens the same war, so it needs the same considered appetite,
        // preparation and seeded commitment. Previously this ran before all three gates:
        // every fresh world handed over the same weak neighbours on their first AI day,
        // even when the government would never have committed to the threatened war.
        // A player's own papers and the recipient's assessment are unchanged.
        if (annexation is null && TryUltimatumFirst(campaign, nation, target))
        {
            Count("asked_before_war");
            return;
        }

        // A legitimate territorial demand may supply a cause if refused, but merely
        // preparing to threaten somebody still does not permit an unjustified declaration.
        if (annexation is null && defines.AiNeedsCauseForWar &&
            string.IsNullOrEmpty(Statecraft.CasusBelli(campaign, nation, target)))
        {
            Count("no_cause");
            return;
        }

        // A country that has been preparing for this one goes sooner; the preparation is the
        // point of having made the plan.
        if (nation.WarPlanTargetId == target.Id) ClearWarPlan(nation);

        // Turning on an ally is done openly and in two acts: the promise ends, then the war
        // begins. Folding the break into the declaration would let a country attack a friend
        // while still shown as its friend, and would rob the second act of the thing that makes
        // it mean anything. Everybody watching learns what this signature is worth, and pays
        // attention to the next one this country signs.
        if (CampaignDiplomacy.Peek(campaign, nation.Id, target.Id) is { Stance: DiplomaticStance.Allied })
        {
            // Ending a friendship is not a thing to do and then discover was pointless. Breaking
            // the alliance clears the alliance and nothing else: a shared collective-defence
            // organisation still forbids the war whatever the two countries signed, and a
            // government that breaks first and finds that out afterwards has spent everybody's
            // opinion of it on a war it cannot start. Ask that one question before the act that
            // cannot be taken back.
            if (InternationalOrganizations.DefencePartners(campaign, nation.Id).Contains(target.Id))
            { Count("blocked"); return; }
            new BreakAllianceCommand { IssuerId = nation.Id, TargetId = target.Id }.Apply(campaign);
            nation.Notoriety += defines.AiBetrayalNotoriety;
            Count("betrayed");
            // And whatever is left - a truce nobody remembered, a subject that answers to the
            // target - is still refused here rather than declared through.
            if (!CampaignDiplomacy.CanDeclareWar(campaign, nation.Id, target.Id, out _)) { Count("blocked"); return; }
        }

        var recovery = TerritorialClaims.RecoveryGoal(campaign, nation, target);
        var cause = annexation?.Cause ?? (recovery is null ? "" : "reconquest");
        var claimed = recovery?.MapIndex ?? annexation?.ClaimedState ?? -1;
        // A union is about the whole state, so its capital is the objective even when an
        // older recovery claim names one region. A border reconquest keeps its own goal.
        if (annexation is { Reunification: true } union) claimed = union.ClaimedState;
        if (CampaignDiplomacy.DeclareWar(campaign, nation.Id, target.Id, claimed, cause) is not null)
            Count(annexation is null ? "declared" : "annexation_declared");
    }


    /// <summary>
    /// The demand before the war: one region of the target's on our border - our own people's
    /// if there is one, else its most populous - once a year at most for each target, and only
    /// to a government nobody plays and when we have no reason of our own already.
    /// </summary>
    internal static bool TryUltimatumFirst(Campaign campaign, Nation nation, Nation target)
    {
        var defines = campaign.Defines;
        if (!defines.AiUltimatumsBeforeWar) return false;
        if (Ultimatum.PendingBetween(campaign, nation.Id, target.Id)) return true;
        var cause = Statecraft.CasusBelli(campaign, nation, target);
        // A territorial claim is still negotiable. Only an already refused demand (or
        // another non-territorial cause) skips this last chance to settle without war.
        if (!string.IsNullOrEmpty(cause) && cause is not ("irredentism" or "reconquest")) return false;
        var key = "ultimatum/" + target.Id;
        if (nation.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays) return false;
        if (campaign.ProvinceMap is null) return false;

        // Only for ground there is a case for: ours once, or our own people's. China asking
        // Mongolia for Dornod because Mongolia could not say no is not a demand anybody makes.
        var lost = new HashSet<int>();
        foreach (var (region, holder) in LostLands.Of(campaign, nation))
            if (holder?.Id == target.Id) lost.Add(region.MapIndex);

        ScenarioState best = null;
        var bestScore = 0f;
        foreach (var state in campaign.StatesOf(target))
        {
            if (state.Population <= 0) continue;
            if (!Ultimatum.CanDemand(campaign, target, state.MapIndex)) continue;
            // Adjacency is a region-graph question, not a walk of all thirty-five thousand
            // provinces for every region of every neighbour, once a week for every country.
            if (!TerritorialCoherence.TouchesOwnedGround(campaign, nation, state.MapIndex)) continue;
            long ours = 0;
            foreach (var group in state.PopulationGroups)
                if (DemographyCatalog.IsPrimary(nation.Tag, group.Culture)) ours += group.Size;
            var kin = ours / (float)Math.Max(1, state.Population);
            if (kin < 0.5f && !lost.Contains(state.MapIndex)) continue;
            var score = state.Population * (1f + 4f * kin) * (lost.Contains(state.MapIndex) ? 2f : 1f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = state;
        }
        if (best is null)
        {
            Count("ultimatum_no_region");
            return TryAnnexUltimatum(campaign, nation, target, key);
        }

        var demand = new Ultimatum.Demand { Regions = new[] { best.MapIndex } };
        if (target.IsHuman)
        {
            if (!Ultimatum.ValidateTerms(campaign, nation, target, demand, out var humanReason))
            {
                Count("ultimatum_invalid");
                DiagnosticLogs.Write("ai", $"AI_ULTIMATUM_INVALID {nation.Tag}->{target.Tag} region={best.Id} reason={humanReason}");
                return false;
            }
            nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, defines.AiUltimatumIntervalDays);
            var delivered = Ultimatum.Deliver(campaign, nation, target, demand);
            if (!delivered.Accepted) return false;
            Count("ultimatum_delivered");
            return true;
        }
        var assessment = Ultimatum.Assess(campaign, nation, target, demand);
        if (!assessment.Valid)
        {
            Count("ultimatum_invalid");
            DiagnosticLogs.Write("ai", $"AI_ULTIMATUM_INVALID {nation.Tag}->{target.Tag} region={best.Id} reason={assessment.Reason}");
            return false;
        }
        nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, defines.AiUltimatumIntervalDays);
        Ultimatum.Resolve(campaign, nation, target, demand, assessment.Response);
        Count(assessment.Response == Ultimatum.Answer.Yield ? "ultimatum_yielded" : "ultimatum_refused");
        DiagnosticLogs.Write("ai", $"AI_ULTIMATUM {nation.Tag}->{target.Tag} region={best.Id} answer={assessment.Response}");
        return true;
    }

    /// <summary>
    /// One annexation, and only when the same assessment that answers a region would yield.
    /// A human is never swallowed here.
    /// </summary>
    static bool TryAnnexUltimatum(Campaign campaign, Nation nation, Nation target, string key)
    {
        if (target.IsHuman) return false;
        var demand = new Ultimatum.Demand { Annex = true };
        var assessment = Ultimatum.Assess(campaign, nation, target, demand);
        if (!assessment.Valid || assessment.Response != Ultimatum.Answer.Yield) return false;
        nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, campaign.Defines.AiUltimatumIntervalDays);
        Ultimatum.Resolve(campaign, nation, target, demand, Ultimatum.Answer.Yield);
        Count("ultimatum_yielded");
        DiagnosticLogs.Write("ai", $"AI_ULTIMATUM {nation.Tag}->{target.Tag} annex answer=Yield");
        return true;
    }

    /// <summary>
    /// How much fight the other side of this war still has, as far as its own score lets us measure.
    ///
    /// The simulation does not read an opponent's internal morale on purpose, but the score is the
    /// measure of exactly that: a side a hundred points down has nothing left to hold out with. The
    /// constant that used to stand here (0,5, "their morale is not ours to read") was above every
    /// capitulation threshold, so the indemnity branch that tested it could never fire at any score.
    /// </summary>
    internal static float OpponentMoraleEstimate(StrategicWar war, int ourId)
    {
        if (war is null) return 1f;
        var lead = MathF.Max(0f, war.IsAttacking(ourId) ? war.WarScore : -war.WarScore);
        return M.Clamp01(1f - lead / 100f);
    }

    /// <summary>
    /// Whether this country has lost the war on points, whatever its untouched home front still
    /// says. Waiting for the home front to agree is how a defender at -100 sat out nine hundred
    /// days with the fighting long decided.
    /// </summary>
    internal static bool DecisivelyBeaten(Campaign campaign, StrategicWar war, int nationId)
    {
        if (campaign?.Defines is null || war is null || war.Ended) return false;
        var score = war.IsAttacking(nationId) ? war.WarScore : -war.WarScore;
        return score <= -campaign.Defines.AiDecisiveWarScore;
    }

    /// <summary>
    /// Whether a civil war that has stood still may be offered the status quo now.
    ///
    /// Civil wars wait longer than other wars before a deadlock is called: seven hundred and
    /// thirty days on top of the ordinary stalemate, and a government does not hand its own
    /// country back because the score says so. That is what kept a moving reconquest alive.
    /// Signing those on points cost the bench eleven of fifteen recoveries. The same wait left
    /// a war on the map whose status quo the signature would already accept, and nobody asked.
    ///
    /// Once the front has not moved and that acceptance already clears, the extra wait has
    /// nothing left to protect. A front that is still moving is not this, whatever the score.
    /// </summary>
    internal static bool CivilFrozenOfferReady(Campaign campaign, StrategicWar war, int nationId)
    {
        if (campaign?.Defines is null || war is null || war.Ended || !war.CivilConflict || !war.Stalled)
            return false;
        if (!war.Involves(nationId)) return false;
        if (campaign.ElapsedDays - war.StartedDay < campaign.Defines.AiStalemateDays) return false;
        return PeaceNegotiation.BlockReason(campaign, war, nationId, PeaceTerms.StatusQuo).Length == 0;
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

        // Asked before the reclaiming return below. That return keeps a government fighting
        // for the rest of its own country, and it is also how a stalled civil war never put
        // a paper down: the side that was ahead left, and the side that was behind was still
        // inside the extra civil delay. The status quo is the term the acceptance already
        // priced. It is not a white peace, and it is not a surrender on points.
        if (CivilFrozenOfferReady(campaign, war, nation.Id))
        {
            PeaceOffers.Put(campaign, war, nation, opponent, PeaceTerms.StatusQuo);
            return;
        }

        var ledger = new PeaceLedger(campaign, war, nation.Id);
        if (ledger.Budget < WarAims.PeaceFloor(campaign, nation))
        {
            // A state that is ahead against its own breakaway does not offer to stop: the
            // ground is its own, and a white peace would hand back what it has retaken.
            // Unless the war has dragged into an intractable multi-year stalemate, in which case
            // both parties realistically accept freezing the line of contact (frozen conflict / status-quo).
            var ownScore = war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore;
            var reclaiming = Reintegration.Reclaiming(campaign, war, nation.Id);
            var duration = campaign.ElapsedDays - war.StartedDay;
            var staleReclaim = reclaiming && (WarWeariness.TimeToStop(campaign, war, winning: false) ||
                                              (war.Stalled && duration >= defines.AiStalemateDays) ||
                                              (duration >= defines.AiWearyDays));
            if (ownScore > 0f && reclaiming && !staleReclaim) return;
            // Beaten on points, not merely tired. A government that is eighty points down has lost
            // the war by every measure its own army has, whatever its untouched home front still
            // says; waiting for its morale to fall is how a defender at -100 sat out nine hundred
            // days with the fighting long decided (WarWeariness.AiDecisiveWarScore).
            //
            // Not for a civil war: a state does not concede its own ground by points, and the
            // breakaway rules (Reintegration, CivilWarPersistence) already carry that case with
            // their own resolve. Ending those here cost the bench eleven of fifteen recoveries.
            if (!war.CivilConflict && DecisivelyBeaten(campaign, war, nation.Id) &&
                duration > defines.AiMinimumWarDays)
            {
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
                return;
            }
            // Not winning - which is not the same as wanting to stop. A country only walks
            // away from a war it started or is defending when the country itself has had
            // enough, and that takes years rather than a bad season. Otherwise the map
            // settles every border it has within a year of the campaign opening.
            // Or when the war has stood still long enough, or simply gone on too long: then the
            // peace of a deadlock - the line as the border where anyone holds anything, a white
            // peace where nobody does (WarWeariness).
            if (nation.WarSupport < defines.AiCapitulationWarSupport &&
                campaign.ElapsedDays - war.StartedDay > defines.AiMinimumWarDays * 3)
                // A government tired of its civil war cannot ask the side that is winning it to
                // hand the ground back: it concedes the line, and the line is the de facto border.
                PeaceOffers.Put(campaign, war, nation, opponent,
                    // Any tired government, not only a civil war's: the line is the border. A white
                    // peace handed back everything the war had changed - Russia, worn down after three
                    // years, gave Ukraine back the Donbas it had held since 2014.
                    WarWeariness.StalemateTerms(campaign, war));
            else if (WarWeariness.TimeToStop(campaign, war, winning: false) || staleReclaim ||
                (nation.Parliament is { PeaceMandateActive: true } && campaign.ElapsedDays - war.StartedDay > defines.AiMinimumWarDays))
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
            var treatyBudget = war.Justification is "irredentism" or "reconquest" or "hegemony" or "annexation" or "reunification" or "conquest" or "imperialism"
                ? ledger.Budget : ledger.Budget * defines.AiPeaceBudgetShare;
            WriteDemand(campaign, nation, war, ledger, demand, treatyBudget);
            ConsiderSubjugation(campaign, nation, war, ledger, demand, treatyBudget);
        }

        if (demand.Provinces.Count == 0 && demand.Subjugate.Length == 0)
        {
            // Ahead on points and holding none of their ground. A country in that position
            // does not stop - it pushes, because stopping converts a war it is winning into
            // nothing at all. Only a genuinely broken opponent is worth an indemnity - and
            // "genuinely broken" is not unknowable: the score is the measure of it. The constant
            // that stood here (0,5, "their morale is not ours to read") was above every
            // capitulation threshold, so the branch could never fire: a war won on points and
            // holding no ground could not be cashed in at all, it ran to nine hundred days.
            var opponentSupport = OpponentMoraleEstimate(war, nation.Id);
            if (ledger.Budget >= defines.AiPeaceIndemnityBudget &&
                opponentSupport <= defines.AiCapitulationWarSupport)
                PeaceOffers.Put(campaign, war, nation, opponent, PeaceTerms.WarReparations);
            // Ahead on points for years and holding nothing: it is over, and it is time to say so.
            else if (WarWeariness.TimeToStop(campaign, war, winning: true))
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
            // Nothing to take and nothing moving. A front that has stood still for a year and a
            // half is the border whatever the escalation ladder thinks about giving up: waiting
            // for the weariness date is how a war ahead on points and holding nothing stayed on
            // the map for two years with no offer ever made.
            else if (war.Stalled && campaign.ElapsedDays - war.StartedDay >= defines.AiStalemateDays)
                PeaceOffers.Put(campaign, war, nation, opponent, WarWeariness.StalemateTerms(campaign, war));
            return;
        }

        // Pressing a war needs a front that can move. A demand that has stood unchanged on a front
        // that has not moved for a year and a half is not a plan, it is a war nobody is fighting:
        // the line is the answer, whatever the local balance says.
        var frozenFront = war.Stalled && campaign.ElapsedDays - war.StartedDay >= defines.AiStalemateDays;
        if (!decisive && !frozenFront && ShouldPressWarAim(campaign, nation, war, demand)) return;
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
        // A civil war ends with one state, not with the government's own rebels as a client.
        if (WarAims.IsRebel(nation) || AnnexationAmbition.IsCivilWarBetween(nation, loser)) return;

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
        if (demand.Provinces.Count > 0 && WantsLongTruce(campaign, nation, war, demand) && Afford(defines.PeaceCostLongTruce))
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

        var opponentSupport = OpponentMoraleEstimate(war, nation.Id);
        if (opponentSupport <= defines.AiCapitulationWarSupport && Afford(defines.PeaceCostHumiliate))
            demand.Humiliate = true;
    }

    // ------------------------------------------------------------------ helpers --------

    /// <summary>
    /// What a country is worth in a fight: the divisions it has, weighted by what state
    /// they are in, plus what its economy could raise if the war lasted.
    ///
    /// Not only the AI's own decisions read it - whether an ally can afford to honour a
    /// promise is the same figure, and answering that with a different yardstick is how a
    /// guarantee starts meaning two things at once.
    /// </summary>
    public static float StrengthOf(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0;
        // Diplomacy/UI can query before the first AI tick or between commands. Never
        // read another campaign's daily cache, or a stale pre-settlement army total.
        var strength = nation.Gdp * campaign.Defines.AiEconomicWeight;
        foreach (var army in campaign.Armies)
            if (army.NationId == nation.Id)
                strength += ArmyStrengthTerm(army);
        return strength;
    }

    /// <summary>One formation's share of a country's strength. Shared by the daily table and
    /// the cold-cache fallback on purpose: the two answers must be the same number, or a
    /// client whose tables are cold reads a different army than the host whose are warm.</summary>
    private static float ArmyStrengthTerm(Army army) =>
        army.Divisions * army.Strength * M.Max(.15f, army.Organisation) *
        // A warehouse war is a war: the same divisions count for less when the
        // depot is empty, and the staff pricing a neighbour reads that too.
        (0.62f + army.Equipment * 0.38f);

    /// <summary>A country's strength from today's cache when it is this campaign's, and counted afresh when it is not.</summary>
    internal static float CachedStrength(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0f;
        var tables = TablesOf(campaign);
        if (tables.Day == campaign.ElapsedDays &&
            tables.StrengthCache.TryGetValue(nation.Id, out var value)) return value;
        return StrengthOf(campaign, nation);
    }

    private static float Strength(Campaign campaign, Nation nation) => CachedStrength(campaign, nation);

    /// <summary>A nation by its map index, without walking the whole list to find it.</summary>
    private static Nation ByMapIndex(Campaign campaign, int mapIndex)
    {
        // Attrition asks before today's statecraft assessment. Looking up a neighbour
        // must not also pin strength, treaties and region counts: a restored client has
        // cold daily tables where the host still has yesterday's, so warming them here
        // froze different military figures on the two peers. Identity has its own index,
        // including countries founded since the last daily assessment.
        return campaign.NationsByMapIndex().GetValueOrDefault(mapIndex);
    }

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
            // A human decides whether their country guarantees somebody, so a plea to a player
            // is a request that waits for them (Guarantee.Ask) rather than a promise made on
            // their behalf - and it is not put to a player already holding as many as they
            // should. This used to skip players altogether, which meant the countries best able
            // to shelter anybody could never be asked.
            if (other.Id == rival.Id || other.Id == nation.Id || other.IsSubject) continue;
            if (other.IsHuman && Guarantee.Refusal(campaign, nation, other).Length != 0) continue;

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
            var live = Warfare.LiveWars(campaign);
            for (var index = 0; index < live.Count; index++)
            {
                var war = live[index];
                if (!war.Ended && war.Involves(other.Id))
                {
                    // Not a refusal: a guarantee commits nothing today, and a power already
                    // fighting is if anything more interested in who else is drawn in. It
                    // makes them harder to ask, not impossible.
                    answer -= defines.AiShelterWarPenalty;
                    break;
                }
            }

            if (answer <= bestAnswer) continue;
            bestAnswer = answer;
            best = other;
        }

        if (best is null || bestAnswer < defines.AiShelterWillingness) return false;

        if (best.IsHuman)
        {
            // The question is the move. Nothing is promised on a player's behalf; it lands on
            // their table, it is answered there or it lapses, and the country that asked has
            // still spent its week asking (Guarantee.Ask bounds the repeat).
            Guarantee.Ask(campaign, nation, best, out _);
            if (CampaignDiplomacy.Peek(campaign, nation.Id, best.Id) is not { } asked ||
                !Guarantee.Pending(campaign, asked)) return false;
            Count("shelter_asked");
            DiagnosticLogs.Write("simulation",
                $"SHELTER_ASK {nation.Tag} asks {best.Tag} against {rival.Tag} " +
                $"answer={bestAnswer:0.00} day={campaign.ElapsedDays}");
            return true;
        }

        new GuaranteeCommand { IssuerId = best.Id, TargetId = nation.Id, Guarantee = true }
            .Apply(campaign);
        Count("shelter_granted");
        CampaignDiplomacy.Get(campaign, nation.Id, best.Id).LastActionDay = campaign.ElapsedDays;
        DiagnosticLogs.Write("simulation",
            $"SHELTER {best.Tag} guarantees {nation.Tag} against {rival.Tag} " +
            $"answer={bestAnswer:0.00} day={campaign.ElapsedDays}");
        return true;
    }

    /// <summary>
    /// Buy a neighbour's peace with the treasury instead of waiting for the war.
    ///
    /// A weak country beside a strong one has one currency the stronger side values: cash.
    /// It offers a slice of its treasury for a pact, and the answer comes from the same
    /// deal table the player bargains on - the strong side does not sell a peace it could
    /// take, so the offer only ever lands where fear or fondness already did the work.
    /// Returns whether the bargain was struck.
    /// </summary>
    internal static bool BuyPeace(Campaign campaign, Nation buyer, Nation seller)
    {
        if (campaign is null || buyer is null || seller is null || buyer == seller) return false;
        if (buyer.IsHuman || !MechanicFeatures.SystemEnabled(campaign, nameof(AiStatecraft))) return false;
        if (!Ultimatum.CanNegotiateDeal(campaign, buyer, seller, out _)) return false;
        var relation = CampaignDiplomacy.Peek(campaign, buyer.Id, seller.Id);
        if (relation is null || relation.NonAggressionPact || relation.Stance == DiplomaticStance.AtWar) return false;

        // The strong side does not buy what it could take for free: the offer only makes
        // sense from the weaker chair, and only while something is still worth defending.
        var mine = Strength(campaign, buyer);
        var theirs = CampaignIntel.EstimatedStrength(campaign, buyer.Id, seller);
        if (mine >= theirs) return false;

        var fiscal = AiFiscalPolicy.Assess(campaign, buyer, campaign.Defines.AiTreasuryFloor);
        var offer = Rules.DiplomacyRules.BuyPeaceOffer(buyer.Treasury, fiscal.DiscretionaryCash, mine, theirs);
        if (offer <= 0f) return false;

        var command = new ProposeDealCommand
        {
            IssuerId = buyer.Id, ToId = seller.Id, FromCash = offer, FromNonAggression = true
        };
        command.Apply(campaign);
        if (command.Outcome.PendingOfferId > 0)
        {
            Count("peace_offered");
            return true; // This week's action was delivery, not a signed pact.
        }
        if (!command.Outcome.Accepted) return false;
        Count("peace_bought");
        DiagnosticLogs.Write("simulation",
            $"DEAL {buyer.Tag} buys a pact from {seller.Tag} for {offer:0} day={campaign.ElapsedDays}");
        return true;
    }

    internal static bool Threatened(Campaign campaign, Nation nation)
    {
        foreach (var mapIndex in NeighboursOf(campaign, nation.MapIndex))
        {
            var other = ByMapIndex(campaign, mapIndex);
            if (other is null) continue;
            var live = Warfare.LiveWars(campaign);
            for (var index = 0; index < live.Count; index++)
            {
                var war = live[index];
                if (!war.Ended && war.Involves(other.Id)) return true;
            }
            if (CampaignDiplomacy.Get(campaign, nation.Id, other.Id).Stance == DiplomaticStance.Rival) return true;
        }
        return false;
    }

    // ------------------------------------------------------- coordinated partition ------------

    /// <summary>
    /// What allied great powers did in every imperial age and what the post-UN world
    /// outlawed: agree on a weak common neighbour and divide it by simultaneous demands,
    /// each taking the slice that touches its own ground.
    ///
    /// No new instrument: every slice is the existing ultimatum path (Assess, then
    /// Resolve), and "they cannot yield what they do not hold" makes the same-day demands
    /// disjoint by themselves - what one taker received, the next cannot ask for. The
    /// victim either yields to each demander in turn (a partition without a war, which is
    /// what Poland got) or refuses each and hands them all a reason (a partition by war,
    /// which is also what Poland got, a century earlier). Assess counts the demander alone
    /// against the victim and its defenders, so what makes the demands land is the
    /// coalition gate below, not a new arithmetic.
    ///
    /// Off in the shipped scenario, because a treaty partition of a sovereign is a pre-UN
    /// norm and the base world is 2024: <see cref="Defines.AiMayCoordinatePartitions"/>.
    /// A mod for an imperial age switches it on with one row of data, which is exactly the
    /// room-for-modders rule this repository runs on. The same switch lights the player's button
    /// (PartitionCoordination.ProposePartitionCommand), because a rule only the computer may use
    /// is not a rule.
    /// </summary>
    internal static bool TryCoordinatePartition(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        if (!defines.AiMayCoordinatePartitions) return false;
        if (nation.IsHuman || nation.Movement is not null) return false;
        if (nation.DecisionAvailableDay.GetValueOrDefault("partition") > campaign.ElapsedDays) return false;

        // The coalition: ourselves and every allied government the computer plays. A human
        // ally counts towards the weight of the agreement but is not handed a slice - a human's
        // demands are their own to make (PartitionCoordination.ProposePartitionCommand), so the
        // instrument is one rule with two callers and not two rules.
        var coalition = new List<Nation> { nation };
        foreach (var other in campaign.Nations)
        {
            if (other is null || other.Id == nation.Id || other.Movement is not null) continue;
            if (CampaignDiplomacy.Peek(campaign, nation.Id, other.Id) is { Stance: DiplomaticStance.Allied })
                coalition.Add(other);
        }

        var coalitionStrength = 0f;
        foreach (var member in coalition) coalitionStrength += StrengthOf(campaign, member);

        Nation victim = null;
        var bestTakers = 0;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate is null || candidate.Movement is not null) continue;
            var inCoalition = false;
            foreach (var member in coalition) if (member.Id == candidate.Id) { inCoalition = true; break; }
            if (inCoalition || !campaign.HasOwnedLand(candidate)) continue;

            // Nobody in the coalition may be the victim's friend or already fighting it:
            // an ally is not for dividing, and a war is conquest, not a partition.
            var disqualified = false;
            foreach (var member in coalition)
            {
                var bond = CampaignDiplomacy.Peek(campaign, member.Id, candidate.Id);
                if (bond is { Stance: DiplomaticStance.Allied } or { Stance: DiplomaticStance.AtWar }) { disqualified = true; break; }
            }
            if (disqualified) continue;

            // And nobody outside it may stand behind the victim - an ally or a guarantor
            // turns a carve-up into a general war, which is not what was proposed.
            foreach (var other in campaign.Nations)
            {
                if (other is null || other.Id == candidate.Id) continue;
                var outside = true;
                foreach (var member in coalition) if (member.Id == other.Id) { outside = false; break; }
                if (!outside) continue;
                var bond = CampaignDiplomacy.Peek(campaign, other.Id, candidate.Id);
                if (bond is { Stance: DiplomaticStance.Allied } || (bond is not null && bond.GuaranteedBy(other.Id)))
                { disqualified = true; break; }
            }
            if (disqualified) continue;

            var takers = 0;
            foreach (var member in coalition)
                if (!member.IsHuman && CampaignDiplomacy.SharesLandBorder(campaign, member.MapIndex, candidate.MapIndex))
                    takers++;
            if (takers < M.Max(2, defines.AiPartitionMinimumDemanders)) continue;
            if (coalitionStrength < StrengthOf(campaign, candidate) * M.Max(1f, defines.AiPartitionStrengthMultiple)) continue;
            if (takers <= bestTakers) continue;
            bestTakers = takers;
            victim = candidate;
        }
        if (victim is null) return false;

        // A government does not carve up a neighbour every time it could. The die is the
        // campaign's own: same seed, same day, same answer on every machine.
        nation.DecisionAvailableDay[PartitionCoordination.CadenceKey] =
            campaign.ElapsedDays + M.Max(7, defines.AiPartitionCadenceDays);
        Count("partition_attempted");
        if (!PartitionAppetiteFavours(campaign, nation)) return false;

        var slices = 0;
        foreach (var member in coalition)
        {
            if (member.IsHuman) continue;
            if (!CampaignDiplomacy.SharesLandBorder(campaign, member.MapIndex, victim.MapIndex)) continue;
            if (PartitionDemand(campaign, member, victim)) slices++;
        }
        if (slices < 2) return false;
        Count("partition_coordinated");
        DiagnosticLogs.Write("ai",
            $"AI_PARTITION coalition={coalition.Count} slices={slices} victim={victim.Tag} day={campaign.ElapsedDays}");
        return true;
    }

    /// <summary>
    /// The die a government throws before it agrees to carve up a neighbour.
    ///
    /// Thrown for itself when it is the one proposing, and thrown again for every ally a player's
    /// proposal puts to - one place, so that an invitation from a human is answered on the same
    /// arithmetic the computer uses for its own plans and not on a rule written later beside it.
    /// Same campaign, same day, same answer on every machine.
    /// </summary>
    internal static bool PartitionAppetiteFavours(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return false;
        return Roll(campaign.Seed, nation.Id, campaign.ElapsedDays) <
            M.Clamp(campaign.Defines.AiPartitionAppetite, 0f, 1f);
    }

    /// <summary>
    /// One taker's slice: the most populous of the victim's regions that touches the
    /// taker's own ground and can still be demanded. No case is required - the agreement
    /// is the case, and the notoriety of a demand nobody can justify is priced by the same
    /// ultimatum path as any other. The slice is chosen by PartitionCoordination, the player's
    /// half of the same instrument, because a distinct rule for a human's slice would be a
    /// second partition.
    /// </summary>
    internal static bool PartitionDemand(Campaign campaign, Nation member, Nation victim)
    {
        if (Ultimatum.PendingBetween(campaign, member.Id, victim.Id)) return false;
        var key = "ultimatum/" + victim.Id;
        if (member.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays) return false;
        var best = PartitionCoordination.Slice(campaign, member, victim);
        if (best is null) return false;

        var demand = new Ultimatum.Demand { Regions = new[] { best.MapIndex } };
        // A player is never answered for: the demand is delivered rather than assessed, so a
        // victim who plays keeps the same right to refuse that a computer government exercises
        // here. This used to be the one hole in the instrument - an AI coalition could divide
        // every neighbour except one with a human in it.
        if (victim.IsHuman)
        {
            if (!Ultimatum.ValidateTerms(campaign, member, victim, demand, out _)) return false;
            member.DecisionAvailableDay[key] = campaign.ElapsedDays + M.Max(30, campaign.Defines.AiUltimatumIntervalDays);
            if (!Ultimatum.Deliver(campaign, member, victim, demand).Accepted) return false;
            Count("partition_slice_delivered");
            return true;
        }
        var assessment = Ultimatum.Assess(campaign, member, victim, demand);
        if (!assessment.Valid) return false;
        member.DecisionAvailableDay[key] = campaign.ElapsedDays + M.Max(30, campaign.Defines.AiUltimatumIntervalDays);
        Ultimatum.Resolve(campaign, member, victim, demand, assessment.Response);
        Count(assessment.Response == Ultimatum.Answer.Yield ? "partition_slice_yielded" : "partition_slice_refused");
        return true;
    }
}

public sealed partial class Defines
{
    /// <summary>
    /// An ally or guarantor may offer cash when a client's institutions are failing.
    /// The money moves through the ordinary deal table. A player still has to sign.
    /// Off: that week is spent on courtship instead.
    /// </summary>
    public bool AiCrisisGrants { get; set; } = true;

    /// <summary>Days before the same patron offers that client another stabilization grant.</summary>
    public int AiCrisisGrantRetryDays { get; set; } = 360;

    /// <summary>
    /// Whether allied powers may agree to divide a weak common neighbour by simultaneous
    /// demands. The practice of every imperial age and outlawed by the post-UN world: off
    /// in the shipped 2024 scenario, and one row of data for a mod of an older one.
    /// </summary>
    public bool AiMayCoordinatePartitions { get; set; } = false;

    /// <summary>How many takers must be able to demand a slice before it is a partition and not acession.</summary>
    public int AiPartitionMinimumDemanders { get; set; } = 2;

    /// <summary>How overwhelmingly stronger the coalition must be than the victim before anybody proposes to divide it.</summary>
    public float AiPartitionStrengthMultiple { get; set; } = 3f;

    /// <summary>Days between one government's thoughts about partitioning anybody.</summary>
    public int AiPartitionCadenceDays { get; set; } = 90;

    /// <summary>The share of such thoughts that become a proposal. A government does not carve up a neighbour every time it could.</summary>
    public float AiPartitionAppetite { get; set; } = 0.5f;
}
