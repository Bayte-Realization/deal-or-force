using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Days into a campaign before a movement nobody plays will rise at all.</summary>
    public int MovementAiUprisingAfterDays { get; set; } = 365;
    /// <summary>The chance of success below which it will not risk an uprising.</summary>
    public float MovementAiUprisingChance { get; set; } = 0.55f;
    /// <summary>The chance, in a week it could rise, that it does.</summary>
    public float MovementAiUprisingAppetite { get; set; } = 0.15f;
    public bool MovementAiUseHomelandOpenings { get; set; } = true;
    public float MovementAiHomelandOpeningChance { get; set; } = 0.8f;
    public float MovementAiHomelandOpeningAppetite { get; set; } = 0.45f;
}

/// <summary>
/// What a movement nobody plays does with its week. It gives the same orders a player
/// would (<see cref="MovementActionCommand"/>), at most two a week, in the order a sensible
/// leadership would: keep the host sweet, get out if it has to, rise where it would win, be
/// recognised, use what makes it itself, and otherwise build its following.
/// </summary>
public static class MovementAi
{
    [ThreadStatic] private static List<ScenarioState> _uprisingCandidates;
    /// <summary>A public crisis is an opening, not free support, cadres or a successful
    /// uprising. Think still validates every command, cost, occupation and methods gate.</summary>
    internal static float UprisingAppetite(Campaign c, Nation movement, MovementDefinition definition,
        ScenarioState region, float chance)
    {
        if (c is null || movement is null || definition is null ||
            !MovementMethods.WillRise(c, movement, definition)) return 0f;
        var basic = M.Clamp01(c.Defines.MovementAiUprisingAppetite);
        if (!c.Defines.MovementAiUseHomelandOpenings || region is null ||
            !MovementDefinition.Listed(definition.Homeland,region.Id) ||
            !float.IsFinite(chance) || chance < c.Defines.MovementAiHomelandOpeningChance) return basic;
        var host = MovementRules.OwnerOf(c,region);
        var hostAtWar = false;
        foreach (var w in Warfare.OpenWars(c))
        {
            if (host is not null && !w.Ended && w.Involves(host.Id)) { hostAtWar = true; break; }
        }
        if (host is null || host.Id == movement.Id ||
            (host.Stability >= .4f && !hostAtWar)) return basic;
        return MathF.Max(basic,M.Clamp01(c.Defines.MovementAiHomelandOpeningAppetite));
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The movements' thinking in stages, one movement at a time, for the smoothed step.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MovementAi))) yield break;
        if (campaign.ElapsedDays == 0) yield break;
        var week = Math.Max(1, campaign.Defines.EconomyTickDays);
        // On the same day as its week (MovementRules.Tick), so it acts on what that week made
        // of it. The walk goes by index (Id == index): a think can found a nation, and the
        // founder takes its turn tomorrow rather than stretching this pass.
        for (var id = 0; id < campaign.Nations.Count; id++)
        {
            var nation = campaign.Nations[id];
            if (nation.Movement is not { Dissolved: false } || nation.IsHuman ||
                !MovementRules.OnItsDay(campaign, id, week)) continue;
            if (MovementRules.NeedsPeopleIndex(campaign))
            {
                using var indexStages = MovementRules.EnsurePeopleIndexedStages(campaign, insideMovRules: false).GetEnumerator();
                while (true)
                {
                    bool indexMore;
                    using (SimProfiler.Measure("movements"))
                    using (SimProfiler.Measure("  mov:ai"))
                        indexMore = indexStages.MoveNext();
                    if (!indexMore) break;
                    yield return 0;
                }
            }
            // Host, the region owner, the population stamp and patience are this think's
            // inputs. When they are the ones left by the last think, the orders are the
            // same orders and the search is not run again. The memory is this campaign's.
            if (QuietSinceLastThink(campaign, nation)) continue;
            using var thinkStages = ThinkStages(campaign, nation).GetEnumerator();
            while (true)
            {
                bool hasNext;
                using (SimProfiler.Measure("movements"))
                using (SimProfiler.Measure("  mov:ai"))
                using (SimProfiler.Measure("    mov:think"))
                    hasNext = thinkStages.MoveNext();
                if (!hasNext) break;
                yield return 0;
            }
            RememberQuietThink(campaign, nation);
        }
        // And what the countries do about them.
        using (var dealingStages = MovementDealings.AiStages(campaign).GetEnumerator())
        {
            while (true)
            {
                bool hasNext;
                using (SimProfiler.Measure("movements"))
                using (SimProfiler.Measure("  mov:ai"))
                using (SimProfiler.Measure("    mov:dealings"))
                    hasNext = dealingStages.MoveNext();
                if (!hasNext) break;
                yield return dealingStages.Current;
            }
        }
    }

    private static void Think(Campaign campaign, Nation nation)
    {
        foreach (var _ in ThinkStages(campaign, nation)) { }
    }

    private static IEnumerable<int> ThinkStages(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (definition is null || state.MovingToStateMapIndex >= 0) yield break;
        var defines = campaign.Defines;
        var done = 0;

        // The host first: a movement that is thrown out loses a fifth of its people.
        if (state.Heat >= 0.6f) TryAction(campaign, nation, ref done, "lobby");
        // A government in exile's whole purpose is the way home: it takes it the moment it is open.
        if (state.Exile)
        {
            var home = Restoration.BestRegion(campaign, nation);
            if (home is not null) TryAction(campaign, nation, ref done, "restore", home.MapIndex);
        }
        if (definition.Has("civic_independence"))
            foreach (var id in definition.Homeland)
                if (Movements.StateById(campaign, id) is { } home && TryAction(campaign, nation, ref done, "referendum", home.MapIndex)) break;
        // A civic campaign lives where its voters do: it lobbies its way out of a host's temper,
        // and never packs up for a country on the other side of the world. The Quebec campaign
        // used to move to India when Ottawa grew tired of it.
        if (state.Landless && state.Heat >= 0.72f && !definition.Has("civic_independence"))
        {
            var refuge = MovementRules.BestRefuge(campaign, nation, Movements.HostOf(campaign, nation));
            if (refuge is not null) TryAction(campaign, nation, ref done, "move", refuge.MapIndex);
        }

        // Rise where it would win, and not in the first year of the world - and never, for one
        // that does not take up arms (the humanitarian trait: an order of knights runs hospitals).
        if (campaign.ElapsedDays >= defines.MovementAiUprisingAfterDays && !definition.Has("humanitarian") &&
            MovementMethods.WillRise(campaign, nation, definition))
        {
            // The homeland first, then the likeliest. A normal movement only needs the
            // regions where it actually has support; the old walk asked UprisingChance of
            // all eighteen hundred regions for every movement, every week.
            var forHire = definition.Has("for_hire");
            var uprisingCandidates = _uprisingCandidates ??= new List<ScenarioState>();
            using (SimProfiler.Measure("      mov:think:rise-fill"))
            {
                uprisingCandidates.Clear();
                if (forHire)
                    uprisingCandidates.AddRange(campaign.States);
                else
                    foreach (var (mapIndex, support) in state.Support)
                    {
                        if (support < defines.MovementUprisingSupport) continue;
                        if (MovementPower.StateOf(campaign, mapIndex) is { } candidate)
                            uprisingCandidates.Add(candidate);
                    }
            }
            ScenarioState target = null;
            var targetHome = false;
            var targetChance = 0f;
            for (var ci = 0; ci < uprisingCandidates.Count; ci++)
            {
                if (ci > 0 && (ci % 8 == 0 || Campaign.StageOverdue())) yield return 0;
                var region = uprisingCandidates[ci];
                if (region.NationMapIndex == nation.MapIndex) continue;
                if (forHire ? region.Stability > 0.35f
                            : state.Support.GetValueOrDefault(region.MapIndex) < defines.MovementUprisingSupport) continue;
                using (SimProfiler.Measure("        mov:think:rise-chance"))
                {
                    var chance = MovementRules.UprisingChance(campaign, nation, region);
                    if (chance < defines.MovementAiUprisingChance) continue;
                    var home = MovementDefinition.Listed(definition.Homeland, region.Id);
                    if (target is not null && (targetHome && !home || targetHome == home && chance <= targetChance)) continue;
                    target = region;
                    targetHome = home;
                    targetChance = chance;
                }
            }
            if (target is not null &&
                MovementRules.Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, 17) <
                    UprisingAppetite(campaign,nation,definition,target,targetChance))
            {
                TryAction(campaign, nation, ref done, "rise", target.MapIndex);
                yield return 0;
            }
        }

        yield return 0;

        // Recognition from whoever thinks best of it. The walk stops at the first country
        // that could win on a non-negative opinion; a warmer one is read from this
        // campaign's relation index, which is not the other campaign's.
        yield return 0;
        var readyRecognition = state.ReadyOnDay.GetValueOrDefault("recognition", 0) <= campaign.ElapsedDays;
        if (readyRecognition && done < 2)
        {
            // The index the walk reads is rebuilt after every sweep of the long campaign, and
            // building it is a walk of every relation in the world, awake and asleep - a hundred
            // and thirty thousand in a century-old save, fifty milliseconds in one stage, which
            // was the worst frame at the top speed. Built here in slices instead; the walk then
            // finds it ready.
            foreach (var _ in EnsureOpinionIndexStages(campaign)) yield return 0;
            Nation asked;
            float warmest;
            using (SimProfiler.Measure("      mov:think:recognition"))
                (asked, warmest) = WarmestRecogniser(campaign, nation, state);
            if (asked is not null && warmest >= MovementActions.RecognitionNeeds(defines, state))
                TryAction(campaign, nation, ref done, "recognition", target: asked.Id);
        }

        // The street is its army: at war with ground of its own, it hands out rifles where it is
        // followed, and before a rising it sends fighters in ahead.
        var atWar = InAnyLiveWar(campaign, nation.Id);
        if (!state.Landless && atWar)
            foreach (var held in campaign.StatesOf(nation))
                if (state.Support.GetValueOrDefault(held.MapIndex) >= 0.2f && TryAction(campaign, nation, ref done, "call_to_arms", held.MapIndex)) break;
        if (!definition.Has("humanitarian") && !definition.Has("nonviolence") && !definition.Has("for_hire") &&
            state.Cadres >= defines.MovementInfiltrateFighters * 2f)
        {
            ScenarioState ahead = null;
            var aheadSupport = 0.25f;
            foreach (var id in definition.Homeland)
            {
                var region = Movements.StateById(campaign, id);
                if (region is null || region.NationMapIndex == nation.MapIndex) continue;
                var support = state.Support.GetValueOrDefault(region.MapIndex);
                if (support < aheadSupport || state.Infiltrated.GetValueOrDefault(region.MapIndex) >= defines.MovementInfiltrateFighters * 2f) continue;
                aheadSupport = support;
                ahead = region;
            }
            if (ahead is not null) TryAction(campaign, nation, ref done, "infiltrate", ahead.MapIndex);
        }

        // What makes it itself.
        if (definition.Has("charity_network") && done < 2 && nation.Movement is not null)
        {
            ScenarioState homeland = null;
            var lowestSupport = float.PositiveInfinity;
            foreach (var id in definition.Homeland)
            {
                var region = Movements.StateById(campaign, id);
                if (region is null || region.NationMapIndex == nation.MapIndex) continue;
                var support = state.Support.GetValueOrDefault(region.MapIndex);
                if (homeland is null || support < lowestSupport)
                {
                    homeland = region;
                    lowestSupport = support;
                }
            }
            if (homeland is not null) TryAction(campaign, nation, ref done, "services", homeland.MapIndex);
        }
        if (definition.Has("government_in_exile")) TryAction(campaign, nation, ref done, "appeal");
        if (definition.Has("street_patrols") && state.Heat < 0.45f) TryAction(campaign, nation, ref done, "patrols");
        yield return 0;
        if (definition.Has("hospitallers") && done < 2 && nation.Movement is not null && state.MovingToStateMapIndex < 0)
        {
            ScenarioState bestHospital = null;
            var bestNeed = float.NegativeInfinity;
            var eligibleCount = 0;
            foreach (var region in campaign.States)
            {
                if (region.Population <= 0 || region.WarDamage < .02f && region.StandardOfLiving >= .8f) continue;
                eligibleCount++;
                var need = region.WarDamage * 2f + (1f - region.StandardOfLiving);
                if (bestHospital is null || need > bestNeed)
                {
                    bestHospital = region;
                    bestNeed = need;
                }
            }
            if (bestHospital is not null)
            {
                if (MovementActions.CanDo(campaign, nation, "hospital", bestHospital.MapIndex, -1))
                    TryAction(campaign, nation, ref done, "hospital", bestHospital.MapIndex);
                else if (eligibleCount > 1)
                {
                    var candidates = _hospitalCandidates ??= new List<(float Score, int Order, ScenarioState Region)>();
                    candidates.Clear();
                    foreach (var region in campaign.States)
                    {
                        if (region.Population <= 0 || region.WarDamage < .02f && region.StandardOfLiving >= .8f) continue;
                        if (ReferenceEquals(region, bestHospital)) continue;
                        candidates.Add((region.WarDamage * 2f + (1f - region.StandardOfLiving), candidates.Count, region));
                    }
                    candidates.Sort(static (left, right) =>
                    {
                        var cmp = right.Score.CompareTo(left.Score);
                        return cmp != 0 ? cmp : left.Order.CompareTo(right.Order);
                    });
                    for (var hi = 0; hi < candidates.Count; hi++)
                    {
                        if (hi > 0 && (hi % 8 == 0 || Campaign.StageOverdue())) yield return 0;
                        var candidate = candidates[hi];
                        if (!MovementActions.CanDo(campaign, nation, "hospital", candidate.Region.MapIndex, -1)) continue;
                        TryAction(campaign, nation, ref done, "hospital", candidate.Region.MapIndex);
                        break;
                    }
                }
            }
        }
        yield return 0;
        if (definition.Has("for_hire") && state.ContractNationId < 0 && done < 2 && nation.Movement is not null)
        {
            var fighting = _liveWarNations ??= new HashSet<int>();
            fighting.Clear();
            foreach (var war in Warfare.LiveWars(campaign))
            {
                fighting.Add(war.AttackerId);
                fighting.Add(war.DefenderId);
                foreach (var ally in war.AttackerAllies) fighting.Add(ally);
                foreach (var ally in war.DefenderAllies) fighting.Add(ally);
            }
            Nation client = null;
            foreach (var other in campaign.Nations)
            {
                if (other.Movement is not null || !fighting.Contains(other.Id)) continue;
                if (client is null || other.Treasury > client.Treasury)
                    client = other;
            }
            if (client is not null) TryAction(campaign, nation, ref done, "contract", target: client.Id);
        }

        // Otherwise the following: where most of its people are still to be won over.
        if (nation.Movement is not { Dissolved: false }) yield break;
        if (MovementRules.NeedsPeopleIndex(campaign))
            foreach (var _ in MovementRules.EnsurePeopleIndexedStages(campaign, insideMovRules: false))
                yield return 0;
        yield return 0;
        ScenarioState ground = null;
        var groundScore = 0f;
        var groundVisited = 0;
        foreach (var (region, known) in MovementRules.PeopleIn(campaign, definition))
        {
            if (region.NationMapIndex == nation.MapIndex) continue;
            var people = known.Own;
            if (people <= 0) continue;
            // The cadence of the stage is kept before the prune, so a cheaper loop does not
            // become a longer stage.
            if (++groundVisited % 64 == 0 || Campaign.StageOverdue()) yield return 0;
            // A region's score cannot exceed its own people times the homeland weight, however
            // well everything else falls out - support is a share and the weight is at most
            // three. So a region that cannot beat the best so far by that ceiling does not need
            // the reach question, and the reach question is the expensive half: a great-circle
            // distance to the headquarters and to every remembered homeland. Ties keep the
            // earlier region, as they did, because the ceiling is not strictly greater.
            if (people * 3f <= groundScore) continue;
            using (SimProfiler.Measure("        mov:think:ground-can"))
                if (!MovementReach.CanOperate(campaign, nation, region, definition)) continue;
            var score = people * (1f - state.Support.GetValueOrDefault(region.MapIndex)) *
                        (MovementDefinition.Listed(definition.Homeland, region.Id) ? 3f : 1f);
            if (score <= groundScore) continue;
            groundScore = score;
            ground = region;
        }
        if (ground is not null && state.Heat < 0.6f) TryAction(campaign, nation, ref done, "campaign", ground.MapIndex);

        if (state.Cadres < defines.MovementUprisingCadres * 2f && nation.Treasury > 0.1f) TryAction(campaign, nation, ref done, "recruit");
        if (nation.Treasury < 0.05f) TryAction(campaign, nation, ref done, "fundraise");
    }

    private static bool TryAction(Campaign campaign, Nation nation, ref int done, string action, int region = -1, int target = -1)
    {
        // A vote carried or a rising won this week makes it a state: it has nothing left to try.
        if (done >= 2 || nation.Movement is null) return false;
        if (!MovementActions.CanDo(campaign, nation, action, region, target)) return false;
        new MovementActionCommand { IssuerId = nation.Id, Action = action, StateMapIndex = region, TargetNationId = target }
            .Apply(campaign);
        done++;
        return true;
    }

    [ThreadStatic] private static List<(float Score, int Order, ScenarioState Region)> _hospitalCandidates;
    [ThreadStatic] private static HashSet<int> _liveWarNations;

    private static bool InAnyLiveWar(Campaign campaign, int nationId)
    {
        foreach (var war in Warfare.LiveWars(campaign))
            if (war.Involves(nationId)) return true;
        return false;
    }

    /// <summary>How many recognition searches walked every country. A hit on the relation index does not.</summary>
    internal static int CountryWalks { get; private set; }

    /// <summary>Countries examined by the last recognition search.</summary>
    internal static int LastNationVisits { get; private set; }

    sealed class OpinionIndex
    {
        public bool Built;
        public List<DiplomaticRelation>[] ByNation = Array.Empty<List<DiplomaticRelation>>();
        public readonly HashSet<long> Seen = new();
    }

    static readonly ConditionalWeakTable<Campaign, OpinionIndex> OpinionIndexes = new();

    /// <summary>A relation was created. An index that already exists for this campaign hears it; another campaign does not.</summary>
    internal static void NoteNewRelation(Campaign campaign, DiplomaticRelation relation)
    {
        if (campaign is null || relation is null || !OpinionIndexes.TryGetValue(campaign, out var index) || !index.Built) return;
        AddRelation(index, relation);
    }

    /// <summary>
    /// Relations were dropped from the campaign's tables (LongCampaign): the index is built again
    /// from the tables the next time it is asked, so it holds no relation the campaign has let go.
    /// </summary>
    internal static void ForgetRelationIndex(Campaign campaign)
    {
        if (campaign is not null) OpinionIndexes.Remove(campaign);
    }

    internal static bool SharesOpinionIndex(Campaign left, Campaign right)
    {
        if (left is null || right is null || ReferenceEquals(left, right)) return false;
        OpinionIndexes.TryGetValue(left, out var a);
        OpinionIndexes.TryGetValue(right, out var b);
        return a is not null && ReferenceEquals(a, b);
    }

    /// <summary>
    /// The country this movement would ask, and that country's opinion. Same choice as
    /// walking every country: the earliest eligible opinion wins ties, and only a higher
    /// one replaces it. After the first country whose opinion is not negative, the rest
    /// of the list is not walked; warmer opinions are on the relation index.
    /// </summary>
    internal static (Nation Asked, float Warmest) WarmestRecogniser(Campaign campaign, Nation nation, MovementState state)
    {
        if (campaign is null || nation is null || state is null) return (null, float.MinValue);
        var links = LinksOf(campaign, nation.Id);
        Nation asked = null;
        var warmest = float.MinValue;
        var nations = campaign.Nations;
        var visits = 0;
        var covered = false;
        for (var i = 0; i < nations.Count; i++)
        {
            visits++;
            var other = nations[i];
            if (!CanRecognise(campaign, nation, state, other)) continue;
            var opinion = CampaignDiplomacy.Peek(campaign, other.Id, nation.Id)?.Opinion ?? 0f;
            if (asked is null || opinion > warmest)
            {
                warmest = opinion;
                asked = other;
            }
            if (opinion >= 0f)
            {
                covered = true;
                break;
            }
        }
        LastNationVisits = visits;
        if (!covered) CountryWalks++;
        if (covered && links is not null)
        {
            for (var i = 0; i < links.Count; i++)
            {
                var relation = links[i];
                var opinion = relation.Opinion;
                var other = campaign.NationById(relation.Other(nation.Id));
                if (!CanRecognise(campaign, nation, state, other)) continue;
                // Id is the list index, so the smaller id is the earlier country.
                if (opinion > warmest || (opinion == warmest && other.Id < asked.Id))
                {
                    warmest = opinion;
                    asked = other;
                }
            }
        }
        return (asked, warmest);
    }

    static bool CanRecognise(Campaign campaign, Nation nation, MovementState state, Nation other) =>
        other is not null && other.Movement is null && other.Id != nation.Id &&
        !state.RecognisedBy.Contains(other.Id) && campaign.HasOwnedLand(other);

    /// <summary>
    /// The same index <see cref="LinksOf"/> builds, built a slice at a time. The relations are
    /// taken in the order LinksOf takes them - the awake table, then the sleeping one - from a
    /// copy of both, so a frame between two slices cannot pull the table out from under the walk.
    /// A relation made between two slices (the interface may make one) would be missing from the
    /// copy, so the counts are checked at the end, and a table that changed is indexed again in
    /// one go, as LinksOf would have. What the index holds decides nothing by its order: the walk
    /// that reads it breaks ties by the country's id.
    /// </summary>
    internal static IEnumerable<int> EnsureOpinionIndexStages(Campaign campaign)
    {
        if (campaign is null) yield break;
        var index = OpinionIndexes.GetValue(campaign, static _ => new OpinionIndex());
        if (index.Built) yield break;
        var awake = campaign.Relations.Count;
        var asleep = campaign.DormantRelations.Count;
        var rows = System.Buffers.ArrayPool<DiplomaticRelation>.Shared.Rent(Math.Max(1, awake + asleep));
        try
        {
            campaign.Relations.Values.CopyTo(rows, 0);
            campaign.DormantRelations.Values.CopyTo(rows, awake);
            for (var i = 0; i < awake + asleep; i++)
            {
                if ((i & 1023) == 1023 && Campaign.StageOverdue()) yield return 0;
                // Somebody asked LinksOf between two slices and it built the whole index itself.
                if (index.Built) yield break;
                AddRelation(index, rows[i]);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<DiplomaticRelation>.Shared.Return(rows, clearArray: true);
        }
        if (campaign.Relations.Count != awake || campaign.DormantRelations.Count != asleep)
        {
            foreach (var relation in campaign.Relations.Values) AddRelation(index, relation);
            foreach (var relation in campaign.DormantRelations.Values) AddRelation(index, relation);
        }
        index.Built = true;
    }

    static List<DiplomaticRelation> LinksOf(Campaign campaign, int nationId)
    {
        var index = OpinionIndexes.GetValue(campaign, static _ => new OpinionIndex());
        if (!index.Built)
        {
            foreach (var relation in campaign.Relations.Values) AddRelation(index, relation);
            foreach (var relation in campaign.DormantRelations.Values) AddRelation(index, relation);
            index.Built = true;
        }
        if ((uint)nationId >= (uint)index.ByNation.Length) return null;
        return index.ByNation[nationId];
    }

    static void AddRelation(OpinionIndex index, DiplomaticRelation relation)
    {
        if (relation is null) return;
        var key = ((long)relation.A << 32) | (uint)relation.B;
        if (!index.Seen.Add(key)) return;
        Push(index, relation.A, relation);
        if (relation.B != relation.A) Push(index, relation.B, relation);
    }

    static void Push(OpinionIndex index, int id, DiplomaticRelation relation)
    {
        if (id < 0) return;
        if (id >= index.ByNation.Length)
        {
            var grown = new List<DiplomaticRelation>[Math.Max(id + 1, index.ByNation.Length * 2)];
            Array.Copy(index.ByNation, grown, index.ByNation.Length);
            index.ByNation = grown;
        }
        var list = index.ByNation[id];
        if (list is null) index.ByNation[id] = list = new List<DiplomaticRelation>(4);
        list.Add(relation);
    }

    /// <summary>How many times this campaign has thought a movement. A second campaign has its own count.</summary>
    internal static int ThinksRun(Campaign campaign) =>
        campaign is not null && QuietThinks.TryGetValue(campaign, out var memory) ? memory.Thinks : 0;

    /// <summary>How many thinks this campaign skipped because the four inputs had not moved.</summary>
    internal static int QuietSkips(Campaign campaign) =>
        campaign is not null && QuietThinks.TryGetValue(campaign, out var memory) ? memory.Skips : 0;

    static readonly ConditionalWeakTable<Campaign, QuietMemory> QuietThinks = new();

    static QuietMemory QuietMemoryOf(Campaign campaign) =>
        QuietThinks.GetValue(campaign, static _ => new QuietMemory());

    /// <summary>
    /// True when this movement's host, headquarters owner, homeland owners, population
    /// stamp and patience are the ones recorded after its last think. Reading them does
    /// not issue an order.
    /// </summary>
    static bool QuietSinceLastThink(Campaign campaign, Nation nation)
    {
        var memory = QuietMemoryOf(campaign);
        var key = QuietKey.Read(campaign, nation);
        if (!memory.Keys.TryGetValue(nation.Id, out var prior) || !prior.Equals(key)) return false;
        memory.Skips++;
        return true;
    }

    static void RememberQuietThink(Campaign campaign, Nation nation)
    {
        var memory = QuietMemoryOf(campaign);
        memory.Keys[nation.Id] = QuietKey.Read(campaign, nation);
        memory.Thinks++;
    }

    sealed class QuietMemory
    {
        internal int Thinks;
        internal int Skips;
        internal readonly Dictionary<int, QuietKey> Keys = new();
    }

    readonly struct QuietKey : IEquatable<QuietKey>
    {
        internal readonly int HostId;
        internal readonly int OwnerMap;
        internal readonly ulong Ground;
        internal readonly ulong People;
        internal readonly string Patience;

        QuietKey(int hostId, int ownerMap, ulong ground, ulong people, string patience)
        {
            HostId = hostId;
            OwnerMap = ownerMap;
            Ground = ground;
            People = people;
            Patience = patience ?? "";
        }

        internal static QuietKey Read(Campaign campaign, Nation nation)
        {
            var hostId = -1;
            var ownerMap = -1;
            var hq = nation?.Movement?.HeadquartersStateMapIndex ?? -1;
            var region = Movements.RegionAt(campaign, hq);
            if (region is not null)
            {
                ownerMap = region.NationMapIndex;
                hostId = Movements.NationOwning(campaign, ownerMap)?.Id ?? -1;
            }
            return new QuietKey(hostId, ownerMap, GroundOf(campaign, nation),
                MovementRules.PopulationStamp(campaign), MovementRules.Patience(campaign, nation));
        }

        static ulong GroundOf(Campaign campaign, Nation nation)
        {
            ulong mix = 14695981039346656037UL;
            var definition = Movements.DefinitionOf(nation);
            var homeland = definition?.Homeland;
            if (homeland is null) return mix;
            for (var i = 0; i < homeland.Count; i++)
            {
                var region = Movements.StateById(campaign, homeland[i]);
                mix = Mix(mix, unchecked((ulong)(region?.MapIndex ?? -1)));
                mix = Mix(mix, unchecked((ulong)(region?.NationMapIndex ?? -1)));
            }
            return mix;
        }

        static ulong Mix(ulong sig, ulong value) => unchecked((sig ^ value) * 1099511628211UL);

        public bool Equals(QuietKey other) =>
            HostId == other.HostId && OwnerMap == other.OwnerMap && Ground == other.Ground &&
            People == other.People && string.Equals(Patience, other.Patience, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is QuietKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(HostId, OwnerMap, Ground, People, Patience);
    }
}
