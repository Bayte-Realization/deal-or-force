using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// A named reason one country holds an opinion of another: `common/opinion_modifiers`.
///
/// Opinion was a single number that two buttons moved by twelve and eighteen, and a
/// declaration of war slammed to minus sixty. It had no memory of why, so nothing could
/// explain it to the player, and nothing forgot: a war fought in 2024 held a country at
/// minus sixty in 2124, while a decade of patient diplomacy read exactly the same as a
/// bribe paid last Tuesday.
///
/// Paradox settled this a long time ago and the answer is worth copying. An opinion is the
/// sum of remembered reasons; a reason has a name, a size, a lifetime and whether it fades.
/// The number the player sees is then explicable - and, being data, a mod can decide that
/// its world holds grudges longer.
/// </summary>
public readonly record struct OpinionModifierDefinition(
    string Id, string Name, float Opinion, int Days, bool Decays, int MaxStacks, bool ThirdParty = false);

public static class OpinionCatalog
{
    private static readonly Dictionary<string, OpinionModifierDefinition> Definitions =
        new(StringComparer.OrdinalIgnoreCase);

    static OpinionCatalog() => ResetCatalog();

    public static int Count => Definitions.Count;

    public static IEnumerable<OpinionModifierDefinition> All => Definitions.Values;

    public static OpinionModifierDefinition? Find(string id) =>
        !string.IsNullOrEmpty(id) && Definitions.TryGetValue(id, out var definition)
            ? definition
            : null;

    public static void ResetCatalog()
    {
        Definitions.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "opinion_modifiers"))
            using (document)
                Merge(document.RootElement, null);
    }

    /// <summary>Layer one package's opinion modifiers over what is already loaded.</summary>
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add("opinion_modifiers: expected an object of named reasons");
            return;
        }

        foreach (var entry in root.EnumerateObject())
        {
            var value = entry.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"opinion_modifiers: '{entry.Name}' is not a declaration");
                continue;
            }
            if (GameDataLoader.IsRemoved(value, $"common/opinion_modifiers:{entry.Name}"))
            {
                Definitions.Remove(entry.Name);
                continue;
            }
            if (!value.TryGetProperty("opinion", out var opinion) || !opinion.TryGetSingle(out var weight))
            {
                errors?.Add($"opinion_modifiers: '{entry.Name}' says nothing about opinion");
                continue;
            }

            var known = Find(entry.Name);
            Definitions[entry.Name] = new OpinionModifierDefinition(
                entry.Name,
                value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : known?.Name ?? entry.Name,
                weight,
                value.TryGetProperty("days", out var days) && days.TryGetInt32(out var lifetime)
                    ? Math.Max(0, lifetime)
                    : known?.Days ?? 0,
                value.TryGetProperty("decays", out var fades) &&
                fades.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? fades.GetBoolean()
                    : known?.Decays ?? true,
                value.TryGetProperty("max_stacks", out var stacks) && stacks.TryGetInt32(out var limit)
                    ? Math.Max(1, limit)
                    : known?.MaxStacks ?? 1,
                value.TryGetProperty("third_party", out var watched) &&
                watched.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? watched.GetBoolean()
                    : known?.ThirdParty ?? false);
        }
    }
}

/// <summary>One reason, as this pair of countries currently holds it.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(OpinionReasonConverter))]
public sealed class OpinionReason
{
    public string Id = "";
    public int GrantedDay;

    /// <summary>Zero or less means it is held for good.</summary>
    public int ExpiresDay;

    /// <summary>How many times it has happened - a second denunciation stings again.</summary>
    public int Stacks = 1;

    /// <summary>
    /// How strongly, for reasons that come in degrees. Fear of a neighbour twice your size
    /// is the same reason as fear of one slightly bigger, felt harder; a declaration of war
    /// is not, and leaves this at one.
    /// </summary>
    public float Scale = 1f;
}

public static class OpinionSystem
{
    /// <summary>
    /// Remember something. A reason that is already held is refreshed rather than doubled,
    /// up to whatever the declaration allows - so a country that denounces you five times
    /// in a week has said one thing five times, not five things.
    /// </summary>
    public static void Remember(Campaign campaign, DiplomaticRelation relation, string id,
        float scale = 1f) =>
        Remember(campaign, relation, id, scale, settle: true);

    internal static void Remember(Campaign campaign, DiplomaticRelation relation, string id,
        float scale, bool settle)
    {
        var definition = OpinionCatalog.Find(id);
        if (definition is null || relation is null) return;
        var item = definition.Value;

        OpinionReason held = null;
        foreach (var reason in relation.Reasons)
            if (string.Equals(reason.Id, id, StringComparison.OrdinalIgnoreCase)) { held = reason; break; }

        // Whether this is the first time it has been held - asked of the list, not of the day.
        // The old test was `GrantedDay == 0`, which is also exactly what "never granted" looks
        // like, so a reason first remembered on the opening day of the campaign carried a
        // GrantedDay of zero and its first repetition was silently swallowed. Forgetting a reason
        // and remembering it again is a first time as well, which is what makes a pact broken,
        // forgiven and broken again count as twice.
        var fresh = held is null;
        if (fresh)
        {
            held = new OpinionReason { Id = item.Id };
            relation.Reasons.Add(held);
        }
        // A standing condition is refreshed, not repeated: a border shared for ten years is one
        // border. Only reasons the declaration says can happen again count up at all.
        held.GrantedDay = campaign.ElapsedDays;
        held.ExpiresDay = item.Days > 0 ? campaign.ElapsedDays + item.Days : 0;
        held.Scale = scale;
        if (item.MaxStacks > 1 && !fresh) held.Stacks = Math.Min(item.MaxStacks, held.Stacks + 1);
        if (held.Stacks < 1) held.Stacks = 1;
        if (settle) Settle(campaign, relation);
    }

    /// <summary>Forget a reason outright - a pact torn up stops being worth anything at once.</summary>
    public static void Forget(Campaign campaign, DiplomaticRelation relation, string id) =>
        Forget(campaign, relation, id, settle: true);

    internal static bool Forget(Campaign campaign, DiplomaticRelation relation, string id, bool settle)
    {
        if (relation is null) return false;
        // By hand, and only settling when something actually went. RemoveAll takes a
        // predicate that captures the id, so it allocated a closure every time - and every
        // country tells the diplomacy pass to forget two or three things about each of its
        // neighbours every week, which came to eight megabytes of closures a simulated
        // year for a list that is usually empty.
        var removed = false;
        for (var index = relation.Reasons.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(relation.Reasons[index].Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            relation.Reasons.RemoveAt(index);
            removed = true;
        }
        if (removed && settle) Settle(campaign, relation);
        return removed;
    }

    /// <summary>Whether a relation currently holds a specific opinion modifier reason.</summary>
    public static bool Has(DiplomaticRelation relation, string id)
    {
        if (relation?.Reasons is null) return false;
        for (var index = 0; index < relation.Reasons.Count; index++)
            if (string.Equals(relation.Reasons[index].Id, id, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>What one reason is worth today, having faded for as long as it has been held.</summary>
    public static float Weight(Campaign campaign, OpinionReason reason)
    {
        var definition = OpinionCatalog.Find(reason.Id);
        if (definition is null) return 0f;
        return WeightOf(campaign, reason, definition.Value);
    }

    private static float WeightOf(Campaign campaign, OpinionReason reason, in OpinionModifierDefinition item)
    {
        var value = item.Opinion * Math.Max(1, reason.Stacks) * (reason.Scale <= 0f ? 1f : reason.Scale);
        if (!item.Decays || item.Days <= 0 || reason.ExpiresDay <= 0) return value;

        var life = Math.Max(1, reason.ExpiresDay - reason.GrantedDay);
        var left = Math.Max(0, reason.ExpiresDay - campaign.ElapsedDays);
        return value * (left / (float)life);
    }

    /// <summary>
    /// Add up what a country currently holds against another, and record the parts.
    ///
    /// The breakdown is filled at the same time, because an opinion nobody can account for
    /// is the thing this replaced - the player used to read a number with no story behind
    /// it and no way to find one.
    /// </summary>
    internal static float GrievanceWeight(Campaign campaign, DiplomaticRelation relation)
    {
        if (campaign is null || relation is null || relation.Reasons.Count == 0) return 0f;
        var sum = 0f;
        for (var i = 0; i < relation.Reasons.Count; i++)
        {
            var reason = relation.Reasons[i];
            var def = OpinionCatalog.Find(reason.Id);
            if (def is null) continue;
            var w = WeightOf(campaign, reason, def.Value);
            if (w < 0f) sum += -w;
        }
        return sum;
    }

    internal static void StandingClaimAndCycle(Campaign campaign, Nation leftNation, Nation rightNation,
        out float claimFriction, out float geopolitics)
    {
        claimFriction = 0f;
        geopolitics = 0f;
        if (campaign is null || leftNation is null || rightNation is null || leftNation.Id == rightNation.Id) return;

        var leftClaim = LostLands.HasClaimedLand(campaign, leftNation, rightNation);
        var rightClaim = LostLands.HasClaimedLand(campaign, rightNation, leftNation);
        var leftHome = TerritorialClaims.HoldsOurHomeland(campaign, leftNation, rightNation);
        var rightHome = TerritorialClaims.HoldsOurHomeland(campaign, rightNation, leftNation);
        if (leftClaim || rightClaim || leftHome || rightHome)
        {
            var wLeft = leftClaim ? MathF.Max(0.25f, LostLands.Weight(campaign, leftNation, rightNation)) : (leftHome ? 0.50f : 0f);
            var wRight = rightClaim ? MathF.Max(0.25f, LostLands.Weight(campaign, rightNation, leftNation)) : (rightHome ? 0.50f : 0f);
            var claimBonus = (leftClaim || rightClaim) ? Math.Clamp(campaign.Defines.TerritorialClaimWeight, 0f, 1f) * 0.35f : 0f;
            var intensity = Math.Clamp(MathF.Max(wLeft, wRight) + MathF.Min(wLeft, wRight) * 0.5f + claimBonus, 0f, 1f);
            claimFriction = -(22f + 13f * intensity);
        }

        var ramp = M.Clamp01((campaign.ElapsedDays - 365f) / 1095f);
        if (ramp > 0f)
        {
            geopolitics = GeopoliticalCycle.BilateralRealignmentBias(campaign, leftNation, rightNation) * 18f * ramp;
            var greatPowers = AllianceSystem.GreatPowers(campaign);
            if (greatPowers.Contains(leftNation.Id) && greatPowers.Contains(rightNation.Id))
            {
                var distance = PoliticsCore.IdeologicalDistance(leftNation, rightNation);
                geopolitics -= (8f + 10f * MathF.Min(1f, distance)) * ramp;
            }
        }
    }

    public static void Settle(Campaign campaign, DiplomaticRelation relation) =>
        Settle(campaign, relation, out _, out _, out _);

    /// <summary>
    /// The same settle, handing back the three terms it read from the two countries as they
    /// stand - politics, declared identity, public reputation. The weekly diplomacy scan
    /// stamps each pair with exactly these, and used to work all three out a second time
    /// the moment this returned.
    /// </summary>
    internal static void Settle(Campaign campaign, DiplomaticRelation relation,
        out float politics, out float identity, out float social) =>
        Settle(campaign, relation, true, out politics, out identity, out social, out _, out _);

    internal static void Settle(Campaign campaign, DiplomaticRelation relation,
        out float politics, out float identity, out float social, out float claims, out float geopolitics) =>
        Settle(campaign, relation, true, out politics, out identity, out social, out claims, out geopolitics);

    internal static void Settle(Campaign campaign, DiplomaticRelation relation, bool explain) =>
        Settle(campaign, relation, explain, out _, out _, out _, out _, out _);

    private static void Settle(Campaign campaign, DiplomaticRelation relation, bool explain,
        out float politics, out float identity, out float social, out float claims, out float geopolitics)
    {
        politics = identity = social = claims = geopolitics = 0f;
        if (relation is null) return;
        PruneInertReasons(campaign, relation);
        Explain(campaign, relation, true, out politics, out identity, out social, out claims, out geopolitics, explain);
    }

    internal static void PruneInertReasons(Campaign campaign, DiplomaticRelation relation)
    {
        var reasons = relation.Reasons;
        var today = campaign.ElapsedDays;
        for (var index = reasons.Count - 1; index >= 0; index--)
        {
            var reason = reasons[index];
            if (reason.ExpiresDay > 0 && reason.ExpiresDay <= today)
            {
                reasons.RemoveAt(index);
                continue;
            }
            var definition = OpinionCatalog.Find(reason.Id);
            if (definition is null || !float.IsFinite(reason.Scale))
            {
                reasons.RemoveAt(index);
                continue;
            }
            var item = definition.Value;
            if (item.Opinion == 0f ||
                (item.Decays && item.Days > 0 && reason.ExpiresDay > 0 &&
                 Math.Abs(WeightOf(campaign, reason, item)) < 0.05f))
                reasons.RemoveAt(index);
        }
    }

    public static void Explain(Campaign campaign, DiplomaticRelation relation, bool updateOpinion = false) =>
        Explain(campaign, relation, updateOpinion, out _, out _, out _, out _, out _, true);

    private static void Explain(Campaign campaign, DiplomaticRelation relation, bool updateOpinion,
        out float politics, out float declared, out float reputation, out float claims, out float geopolitics,
        bool explain = true)
    {
        politics = declared = reputation = claims = geopolitics = 0f;
        if (relation is null) return;
        if (explain) relation.OpinionDrivers.Begin("Opinion", "share");
        var total = 0f;
        var witnessedAgainst = 0f;
        var witnessedFor = 0f;
        for (var index = 0; index < relation.Reasons.Count; index++)
        {
            var reason = relation.Reasons[index];
            var definition = OpinionCatalog.Find(reason.Id);
            if (definition is null) continue;
            var item = definition.Value;
            var weight = WeightOf(campaign, reason, item);
            if (Math.Abs(weight) < 0.05f) continue;
            total += weight;
            if (item.ThirdParty)
            {
                if (weight < 0f) witnessedAgainst += weight; else witnessedFor += weight;
            }
            if (explain)
            {
                var name = item.Name ?? reason.Id;
                relation.OpinionDrivers.Add(reason.Stacks > 1 ? $"{name} (x{reason.Stacks})" : name, weight);
            }
        }

        // What the two of them hold against each other as bystanders - a test, a coup, a bad
        // name, an attack on somebody else (modifiers declared `third_party`, OpinionWitnesses) -
        // adds up to so much and no further, either way. Each is already weighed by how much it
        // concerned the country that saw it; this is the ceiling over all of them together, so a
        // decade of watching somebody misbehave elsewhere ends in distrust, not in the hatred the
        // countries it actually wronged are entitled to. The correction is a line of its own on
        // the panel, so the player can see both what was remembered and what it came to.
        var floor = MathF.Min(0f, campaign.Defines.ThirdPartyOpinionFloor);
        if (witnessedAgainst < floor)
        {
            total += floor - witnessedAgainst;
            if (explain) relation.OpinionDrivers.Add(Loc.Get("Other countries' quarrels, beyond what concerns us"), floor - witnessedAgainst);
        }
        var ceiling = MathF.Max(0f, campaign.Defines.ThirdPartyOpinionCeiling);
        if (witnessedFor > ceiling)
        {
            total -= witnessedFor - ceiling;
            if (explain) relation.OpinionDrivers.Add(Loc.Get("Other countries' quarrels, beyond what concerns us"), ceiling - witnessedFor);
        }

        var leftNation = campaign.NationById(relation.A);
        var rightNation = campaign.NationById(relation.B);

        // What the two of them make of each other's politics.
        //
        // Not a remembered reason like the rest: nobody has to be told that a country
        // changed its government, because this is recomputed from what both countries are
        // right now. A reason would have to be issued on every change by every observer,
        // and would be wrong for everybody who was not watching.
        var affinity = PoliticsCore.Affinity(campaign, leftNation, rightNation);
        politics = affinity;
        if (MathF.Abs(affinity) >= 0.05f)
        {
            total += affinity;
            if (explain) relation.OpinionDrivers.Add(
                Loc.Get(affinity >= 0f ? "Politics we recognise" : "Politics we do not share"), affinity);
        }

        // What the two states say they are, over what their peoples are: one declared faith
        // or one declared people, read from both countries as they stand. Continuous, like
        // the politics above - a declaration made yesterday is already in today's number.
        var identity = StateIdentity.Affinity(campaign, leftNation, rightNation);
        declared = identity;
        if (identity >= 0.05f)
        {
            total += identity;
            if (explain) relation.OpinionDrivers.Add(Loc.Get("The same church, the same people"), identity);
        }

        var memory = SocialMemory.OpinionEffect(campaign, leftNation, rightNation);
        reputation = memory;
        if (MathF.Abs(memory) >= 0.05f)
        {
            total += memory;
            if (explain) relation.OpinionDrivers.Add(Loc.Get("Accumulated public reputation"), memory);
        }
        StandingClaimAndCycle(campaign, leftNation, rightNation, out claims, out geopolitics);
        if (MathF.Abs(claims) >= 0.05f)
        {
            total += claims;
            if (explain) relation.OpinionDrivers.Add(Loc.Get("Unresolved territorial claims"), claims);
        }
        if (MathF.Abs(geopolitics) >= 0.05f)
        {
            total += geopolitics;
            if (explain) relation.OpinionDrivers.Add(
                Loc.Get(geopolitics >= 0f ? "Geopolitical alignment" : "Geopolitical rivalry"), geopolitics);
        }
        if (updateOpinion) relation.Opinion = M.Clamp(total, -100f, 100f);
        if (explain) relation.OpinionDrivers.Settle(relation.Opinion, Loc.Get("aaa.ui.opinion_clamped"));
    }

    /// <summary>
    /// Let time work on every grudge in the world.
    ///
    /// Once a week rather than once a day: a linear fade over years does not move enough in
    /// twenty-four hours to be worth walking thirty thousand relations for, and the reasons
    /// that matter - a war, a pact - are settled the moment they happen anyway.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>
    /// How many of the day's relations are settled in one loop on the thread pool. Large on
    /// purpose: the pool's workers take a few hundred microseconds to arrive, and a loop of a
    /// thousand settles is over before they do (measured: no faster than one thread). The late
    /// save's nine thousand a day are one loop of some nine milliseconds' work.
    /// </summary>
    private const int SettleBatch = 16384;

    /// <summary>
    /// The relations of the day that are waiting to be settled, and the loop body that settles
    /// them, kept per campaign so that the pass allocates nothing for it.
    /// </summary>
    private sealed class SettleQueue
    {
        public readonly Campaign Campaign;
        public readonly List<DiplomaticRelation> Waiting = new();
        public readonly Action<int, int> SettleRange;

        public SettleQueue(Campaign campaign)
        {
            Campaign = campaign;
            SettleRange = (from, to) =>
            {
                for (var index = from; index < to; index++)
                {
                    var relation = Waiting[index];
                    var explain = Campaign.NationById(relation.A)?.IsHuman == true ||
                                  Campaign.NationById(relation.B)?.IsHuman == true;
                    Settle(Campaign, relation, explain);
                }
            };
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, SettleQueue> SettleQueues = new();

    /// <summary>
    /// Settle everything waiting, a relation a worker.
    ///
    /// A settle reads the two countries, their images of each other, the catalogue and the
    /// language tables, and writes one relation: its reasons, its opinion and the lines of its
    /// panel. No relation reads another, so the order they are settled in cannot be seen, and
    /// on the calling thread alone it is the order of the table as before. The one thing a
    /// settle could write outside its relation is a country's remembered social key, so those
    /// are asked for here first.
    /// </summary>
    private static void SettleWaiting(SettleQueue queue)
    {
        var waiting = queue.Waiting;
        if (waiting.Count == 0) return;
        var campaign = queue.Campaign;
        for (var index = 0; index < waiting.Count; index++)
        {
            if (campaign.NationById(waiting[index].A) is { } left) SocialMemory.NationKey(left);
            if (campaign.NationById(waiting[index].B) is { } right) SocialMemory.NationKey(right);
        }
        DeterministicParallel.ForRange(campaign, 0, waiting.Count, queue.SettleRange);
        waiting.Clear();
    }

    /// <summary>The same weekly fade, yielded between batches of relationships.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(OpinionSystem))) yield break;
        // A seventh of them each day, every relation still once a week. All on one day made
        // that day's frame walk thirty thousand relations at once.
        //
        // The day's relations are gathered as the walk meets them and settled together on the
        // thread pool, not one by one: each costs over a microsecond of fetching scattered
        // memory - the relation, its reasons, two countries' images - which is work that waits,
        // and waiting is what several threads do well (opinion 165 -> 92 ms over the 14 late
        // days). Nothing else in this pass reads what a settle writes, so settling a relation a
        // little later than the walk met it cannot be seen. A relation with a mission is ticked
        // and settled where the walk meets it, as before: the mission's week writes that
        // relation, its sender's purse and a private line of the chronicle, none of which the
        // relations still waiting read.
        _ = AllianceSystem.GreatPowers(campaign);
        var queue = SettleQueues.GetValue(campaign, static c => new SettleQueue(c));
        queue.Waiting.Clear();
        var scanned = 0;
        var today = campaign.ElapsedDays;
        foreach (var relation in campaign.Relations.Values)
        {
            if (++scanned % 512 == 0) yield return 0;
            if (relation.Stance == DiplomaticStance.Truce && today >= relation.TruceUntil)
                CampaignDiplomacy.RefreshExpiredTruce(campaign, relation);
            var due = (today + (relation.A + relation.B) % 7) % 7 == 0;
            if (relation.MissionByA is not null || relation.MissionByB is not null)
            {
                DiplomaticOutreach.Tick(campaign, relation);
                if (due)
                {
                    var explain = campaign.NationById(relation.A)?.IsHuman == true ||
                                  campaign.NationById(relation.B)?.IsHuman == true;
                    Settle(campaign, relation, explain);
                }
                continue;
            }
            if (!due) continue;
            queue.Waiting.Add(relation);
            if (queue.Waiting.Count >= SettleBatch) SettleWaiting(queue);
        }
        SettleWaiting(queue);
    }
}
