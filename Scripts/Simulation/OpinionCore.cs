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
        float scale = 1f)
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
        Settle(campaign, relation);
    }

    /// <summary>Forget a reason outright - a pact torn up stops being worth anything at once.</summary>
    public static void Forget(Campaign campaign, DiplomaticRelation relation, string id)
    {
        if (relation is null) return;
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
        if (removed) Settle(campaign, relation);
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
        var item = definition.Value;
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
    public static void Settle(Campaign campaign, DiplomaticRelation relation)
    {
        if (relation is null) return;
        for (var index = relation.Reasons.Count - 1; index >= 0; index--)
        {
            var reason = relation.Reasons[index];
            if (reason.ExpiresDay > 0 && reason.ExpiresDay <= campaign.ElapsedDays)
                relation.Reasons.RemoveAt(index);
        }
        Explain(campaign, relation, updateOpinion: true);
    }

    public static void Explain(Campaign campaign, DiplomaticRelation relation, bool updateOpinion = false)
    {
        if (relation is null) return;
        relation.OpinionDrivers.Begin("Opinion", "share");
        var total = 0f;
        var witnessedAgainst = 0f;
        var witnessedFor = 0f;
        foreach (var reason in relation.Reasons)
        {
            var weight = Weight(campaign, reason);
            if (Math.Abs(weight) < 0.05f) continue;
            total += weight;
            var definition = OpinionCatalog.Find(reason.Id);
            if (definition is { ThirdParty: true })
            {
                if (weight < 0f) witnessedAgainst += weight; else witnessedFor += weight;
            }
            var name = definition?.Name ?? reason.Id;
            relation.OpinionDrivers.Add(reason.Stacks > 1 ? $"{name} (x{reason.Stacks})" : name, weight);
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
            relation.OpinionDrivers.Add(Loc.Get("Other countries' quarrels, beyond what concerns us"), floor - witnessedAgainst);
        }
        var ceiling = MathF.Max(0f, campaign.Defines.ThirdPartyOpinionCeiling);
        if (witnessedFor > ceiling)
        {
            total -= witnessedFor - ceiling;
            relation.OpinionDrivers.Add(Loc.Get("Other countries' quarrels, beyond what concerns us"), ceiling - witnessedFor);
        }

        // What the two of them make of each other's politics.
        //
        // Not a remembered reason like the rest: nobody has to be told that a country
        // changed its government, because this is recomputed from what both countries are
        // right now. A reason would have to be issued on every change by every observer,
        // and would be wrong for everybody who was not watching.
        var affinity = PoliticsCore.Affinity(campaign, relation.A, relation.B);
        if (MathF.Abs(affinity) >= 0.05f)
        {
            total += affinity;
            relation.OpinionDrivers.Add(
                Loc.Get(affinity >= 0f ? "Politics we recognise" : "Politics we do not share"), affinity);
        }

        // What the two states say they are, over what their peoples are: one declared faith
        // or one declared people, read from both countries as they stand. Continuous, like
        // the politics above - a declaration made yesterday is already in today's number.
        var identity = StateIdentity.Affinity(campaign, relation.A, relation.B);
        if (identity >= 0.05f)
        {
            total += identity;
            relation.OpinionDrivers.Add(Loc.Get("The same church, the same people"), identity);
        }

        var memory = SocialMemory.OpinionEffect(campaign, relation);
        if (MathF.Abs(memory) >= 0.05f)
        {
            total += memory;
            relation.OpinionDrivers.Add(Loc.Get("Accumulated public reputation"), memory);
        }
        if (updateOpinion) relation.Opinion = M.Clamp(total, -100f, 100f);
        relation.OpinionDrivers.Settle(relation.Opinion, "clamped");
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

    /// <summary>The same weekly fade, yielded between batches of relationships.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(OpinionSystem))) yield break;
        // A seventh of them each day, every relation still once a week. All on one day made
        // that day's frame walk thirty thousand relations at once.
        var scanned = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (++scanned % 512 == 0) yield return 0;
            if (relation.MissionByA is not null || relation.MissionByB is not null) DiplomaticOutreach.Tick(campaign, relation);
            if ((campaign.ElapsedDays + (relation.A + relation.B) % 7) % 7 == 0)
                Settle(campaign, relation);
        }
    }
}
