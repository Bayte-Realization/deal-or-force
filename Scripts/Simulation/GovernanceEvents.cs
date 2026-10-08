using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>One way a country can stop being what it was, and what has to be true first.</summary>
public sealed class GovernanceTransition
{
    public string Id = "";
    /// <summary>
    /// The countries the transition can happen in at all, by tag; empty for every country. A
    /// crown can only be restored where there was one to restore: the junta in Ouagadougou
    /// and the government of Palestine were becoming constitutional monarchies.
    /// </summary>
    public readonly List<string> Countries = new();

    /// <summary>What a player is told happened. Blank means the id, tidied up.</summary>
    public string Name = "";

    /// <summary>What it becomes. Either may be empty, meaning "leave that as it is".</summary>
    public string Government = "";

    public string Ideology = "";

    /// <summary>Empty for a weekly political event; "coup" requires sustained institutional strain.</summary>
    public string Crisis = "";

    /// <summary>How likely this is against the others that also apply.</summary>
    public float Weight = 1f;

    // What has to be true. Every one of these is optional, and a transition with none of
    // them set is always available - which is a reasonable thing for a mod to want and a
    // terrible default, hence none of them being set by the game's own file.
    public float StabilityBelow = -1f;
    public float StabilityAbove = -1f;
    public float RadicalismAbove = -1f;
    public float WarSupportBelow = -1f;
    public float LivingBelow = -1f;
    public float LivingAbove = -1f;
    public float LiteracyAbove = -1f;
    public int AtWar = -1;
    public int LosingWar = -1;

    /// <summary>Only from these forms of government, or from any when empty.</summary>
    public readonly List<string> FromGovernment = new();

    public readonly List<string> FromIdeology = new();
}

/// <summary>What a country just became, for whoever draws the interface.</summary>
public sealed class GovernanceReport
{
    public Nation Nation;
    public string TransitionId = "";
    public string Name = "";
    public string WasGovernment = "";
    public string WasIdeology = "";

    /// <summary>
    /// And what it became, recorded here rather than read off the country later.
    ///
    /// A country that changes twice in a campaign has a current state that says nothing
    /// about either change, and a reader comparing "before" against "now" concludes that a
    /// country which changed and changed back never changed at all.
    /// </summary>
    public string NowGovernment = "";

    public string NowIdeology = "";

    /// <summary>The day it happened.</summary>
    public int Day;
}

/// <summary>
/// Countries changing what they are, without being told to.
///
/// A player could rearrange their state at any time - the cost, the cooling-off period and
/// the refusals were all written - and no other country in the world ever did. Two hundred
/// and sixty governments held the same form and the same ideology from 2024 until the heat
/// death of the campaign, through defeat, collapse, revolution and famine. That made the
/// whole political layer decoration for everybody except the player, and it made the flags
/// that answer to a country's politics decoration too: nothing would ever fly a different
/// one.
///
/// The rule is that a state changes when it is failing, not when it would be better off.
/// Every transition names what has to be true - a collapse, a war going badly, a
/// radicalised population, a prosperous and literate one - and the ones whose conditions
/// hold compete by weight. Everything about them is data, so a mod can declare that its
/// world's republics become theocracies and never write a line of code.
/// </summary>
public static class GovernanceEvents
{
    public static float Base { get; private set; } = 0.0045f;

    private static readonly List<GovernanceTransition> Transitions = new();

    /// <summary>What changed today. Cleared daily, the same way secession's list is.</summary>
    public static readonly List<GovernanceReport> Recent = new();

    public static IReadOnlyList<GovernanceTransition> All => Transitions;

    static GovernanceEvents() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Base = 0.0045f;
        Transitions.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "governance"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("base", out var rate) && rate.ValueKind == JsonValueKind.Number)
            Base = rate.GetSingle();

        if (!root.TryGetProperty("transitions", out var transitions) ||
            transitions.ValueKind != JsonValueKind.Object) return;

        foreach (var entry in transitions.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"common/governance: '{entry.Name}' is not a declaration.");
                continue;
            }

            var transition = Find(entry.Name);
            if (transition is null) Transitions.Add(transition = new GovernanceTransition { Id = entry.Name });

            foreach (var field in entry.Value.EnumerateObject())
                switch (field.Name.ToLowerInvariant())
                {
                    case "name": transition.Name = Text(field.Value, transition.Name); break;
                    case "government": transition.Government = Text(field.Value, transition.Government); break;
                    case "ideology": transition.Ideology = Text(field.Value, transition.Ideology); break;
                    case "crisis":
                        var crisis = Text(field.Value, transition.Crisis);
                        if (crisis is "" or "coup") transition.Crisis = crisis;
                        else errors?.Add($"common/governance: '{entry.Name}' has unknown crisis '{crisis}'.");
                        break;
                    case "weight": transition.Weight = Number(field.Value, transition.Weight); break;
                    case "stability_below": transition.StabilityBelow = Number(field.Value, -1f); break;
                    case "stability_above": transition.StabilityAbove = Number(field.Value, -1f); break;
                    case "radicalism_above": transition.RadicalismAbove = Number(field.Value, -1f); break;
                    case "war_support_below": transition.WarSupportBelow = Number(field.Value, -1f); break;
                    case "living_below": transition.LivingBelow = Number(field.Value, -1f); break;
                    case "living_above": transition.LivingAbove = Number(field.Value, -1f); break;
                    case "literacy_above": transition.LiteracyAbove = Number(field.Value, -1f); break;
                    case "at_war": transition.AtWar = Flag(field.Value); break;
                    case "losing_war": transition.LosingWar = Flag(field.Value); break;
                    case "from_government": Fill(transition.FromGovernment, field.Value); break;
                    case "from_ideology": Fill(transition.FromIdeology, field.Value); break;
                    case "countries": Fill(transition.Countries, field.Value); break;
                    default:
                        errors?.Add($"common/governance: '{entry.Name}' reads '{field.Name}', " +
                                    "which is not something this game understands.");
                        break;
                }

            if (transition.Government.Length > 0 && !GovernmentCatalog.Knows(transition.Government))
                errors?.Add($"common/governance: '{entry.Name}' becomes government " +
                            $"'{transition.Government}', which nothing declares.");
            if (transition.Ideology.Length > 0 && !IdeologyCatalog.Knows(transition.Ideology))
                errors?.Add($"common/governance: '{entry.Name}' becomes ideology " +
                            $"'{transition.Ideology}', which nothing declares.");
        }
    }

    private static GovernanceTransition Find(string id)
    {
        foreach (var transition in Transitions)
            if (string.Equals(transition.Id, id, StringComparison.OrdinalIgnoreCase)) return transition;
        return null;
    }

    private static string Text(JsonElement element, string fallback) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : fallback;

    private static float Number(JsonElement element, float fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var value) ? value : fallback;

    private static int Flag(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => -1
    };

    private static void Fill(List<string> into, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return;
        into.Clear();
        foreach (var value in element.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String)
                into.Add(value.GetString() ?? "");
    }

    // ------------------------------------------------------------------------ the week --

    /// <summary>
    /// Runs every day and does its work weekly, for the same reason secession does: the
    /// report list has to be cleared on the days nothing is worked out.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(GovernanceEvents))) return;
        if (campaign is null) return;
        Recent.Clear();
        if (campaign.ElapsedDays % 7 != 0 || Transitions.Count == 0) return;

        var available = new List<GovernanceTransition>();
        foreach (var nation in campaign.Nations)
        {
            // The player's country is theirs to arrange. A cabinet that rearranged the
            // state out from under the person playing it would be the single most annoying
            // thing this game could do.
            if (nation.IsHuman || nation.Population <= 0) continue;
            if (campaign.ElapsedDays - nation.PoliticsChangedDay < PoliticsCore.ChangeCooldownDays) continue;

            var atWar = false;
            var losing = false;
            foreach (var war in campaign.Wars)
            {
                if (war.Ended || !war.Involves(nation.Id)) continue;
                atWar = true;
                var score = war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore;
                if (score < 0f) losing = true;
            }

            var radicalism = Radicalism(campaign, nation);
            available.Clear();
            var total = 0f;
            foreach (var transition in Transitions)
            {
                if (transition.Crisis.Length > 0) continue; // DomesticCrises owns its visible progress.
                if (!Applies(transition, nation, radicalism, atWar, losing)) continue;
                if (transition.Weight <= 0f) continue;
                available.Add(transition);
                total += transition.Weight;
            }
            if (available.Count == 0 || total <= 0f) continue;

            // How hard the week is pushing. A country at the floor of its stability with a
            // furious population changes some weeks; a country merely having a bad year
            // almost never does.
            var pressure = (1f - M.Clamp01(nation.Stability)) + M.Clamp01(radicalism);
            if (!campaign.Random.Chance(Base * pressure)) continue;

            var roll = campaign.Random.NextFloat() * total;
            var chosen = available[^1];
            foreach (var transition in available)
            {
                roll -= transition.Weight;
                if (roll > 0f) continue;
                chosen = transition;
                break;
            }

            ApplyTransition(campaign, nation, chosen);
        }
    }

    public static bool Eligible(Campaign campaign, Nation nation, GovernanceTransition transition)
    {
        if (campaign is null || nation is null || transition is null || nation.Population <= 0 ||
            campaign.ElapsedDays - nation.PoliticsChangedDay < PoliticsCore.ChangeCooldownDays) return false;
        var atWar = false;
        var losing = false;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            atWar = true;
            if ((war.IsAttacking(nation.Id) ? war.WarScore : -war.WarScore) < 0f) losing = true;
        }
        return Applies(transition, nation, Radicalism(campaign, nation), atWar, losing);
    }

    /// <summary>Use the same policy validation, stability cost and report for both ordinary changes and coups.</summary>
    public static bool ApplyTransition(Campaign campaign, Nation nation, GovernanceTransition chosen)
    {
        if (!Eligible(campaign, nation, chosen)) return false;
        var wasGovernment = nation.Government;
        var wasIdeology = nation.Ideology;
        if (!campaign.ChangePolitics(nation.Id, chosen.Government, chosen.Ideology, out _)) return false;
        Recent.Add(new GovernanceReport
        {
            Nation = nation,
            TransitionId = chosen.Id,
            Name = chosen.Name.Length > 0 ? chosen.Name : chosen.Id,
            WasGovernment = wasGovernment,
            WasIdeology = wasIdeology,
            NowGovernment = nation.Government,
            NowIdeology = nation.Ideology,
            Day = campaign.ElapsedDays
        });
        DiagnosticLogs.Write("simulation",
            $"GOVERNANCE nation={nation.Tag} transition={chosen.Id} " +
            $"government={wasGovernment}->{nation.Government} " +
            $"ideology={wasIdeology}->{nation.Ideology}");
        return true;
    }

    /// <summary>How angry the country is, weighted by where its people actually live.</summary>
    private static float Radicalism(Campaign campaign, Nation nation)
    {
        long people = 0;
        var weighted = 0.0;
        foreach (var state in campaign.StatesOf(nation.MapIndex))
        {
            if (state.Population <= 0) continue;
            people += state.Population;
            weighted += state.Radicalism * (double)state.Population;
        }
        return people <= 0 ? 0f : (float)(weighted / people);
    }

    private static bool Applies(GovernanceTransition transition, Nation nation, float radicalism,
        bool atWar, bool losing)
    {
        if (transition.StabilityBelow >= 0f && nation.Stability >= transition.StabilityBelow) return false;
        if (transition.StabilityAbove >= 0f && nation.Stability <= transition.StabilityAbove) return false;
        if (transition.RadicalismAbove >= 0f && radicalism <= transition.RadicalismAbove) return false;
        if (transition.WarSupportBelow >= 0f && nation.WarSupport >= transition.WarSupportBelow) return false;
        if (transition.LivingBelow >= 0f && nation.StandardOfLiving >= transition.LivingBelow) return false;
        if (transition.LivingAbove >= 0f && nation.StandardOfLiving <= transition.LivingAbove) return false;
        if (transition.LiteracyAbove >= 0f && nation.Literacy <= transition.LiteracyAbove) return false;
        if (transition.AtWar >= 0 && (transition.AtWar == 1) != atWar) return false;
        if (transition.LosingWar >= 0 && (transition.LosingWar == 1) != losing) return false;

        if (transition.Countries.Count > 0 && !Holds(transition.Countries, nation.Tag)) return false;
        if (transition.FromGovernment.Count > 0 && !Holds(transition.FromGovernment, nation.Government))
            return false;
        if (transition.FromIdeology.Count > 0 && !Holds(transition.FromIdeology, nation.Ideology))
            return false;

        // A transition that would change nothing is not a transition.
        var movesGovernment = transition.Government.Length > 0 &&
            !string.Equals(transition.Government, nation.Government, StringComparison.OrdinalIgnoreCase);
        var movesIdeology = transition.Ideology.Length > 0 &&
            !string.Equals(transition.Ideology, nation.Ideology, StringComparison.OrdinalIgnoreCase);
        return movesGovernment || movesIdeology;
    }

    private static bool Holds(List<string> list, string value)
    {
        foreach (var entry in list)
            if (string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
