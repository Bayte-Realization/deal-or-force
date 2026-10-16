using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// When a part of a country stops being part of it.
///
/// The game could take land off a country at a peace conference and give it to another
/// country, and that was the whole of how borders moved. Every border in the world that was
/// not drawn by a war between two states - and most of them were not - had no mechanism at
/// all. A country could hold a region that was wholly somebody else's people, furious,
/// ungoverned and unpoliced, for a century, and nothing would ever come of it.
///
/// A region secedes when four things are true at once: it is not the state's people, it is
/// angry, it has stopped being governed, and the state holding it looks unable to stop it.
/// None of them alone is enough, which is the point - a poor region is not a secession, an
/// angry region is not a secession, and a foreign region in a country that works is Wales.
/// A region of the state's own people can still leave, by the federal road: its politics
/// have diverged, it has stopped accepting the government, the centre is illegitimate, and
/// an economic stress is already on the books. Size alone never opens that road.
///
/// What comes out is a country: a tag, a name, a flag, a capital, a government, and an
/// army of exactly the kind people who have just decided something have. Whether the state
/// it left accepts that or goes to war about it is <see cref="Response"/>, and it is not a
/// coin flip - it is worked out from what the state is, what it is losing and whether it is
/// in a position to do anything.
/// </summary>
public sealed class SecessionWeights
{
    public float Base = 0.009f;
    public float Nationality = 2.20f;

    public float RadicalismFloor = 0.35f;
    public float Radicalism = 0.65f;

    public float WeaknessFloor = 0.20f;
    public float Weakness = 0.80f;

    public float WarMultiplier = 1.80f;

    public float MinimumForeignShare = 0.55f;
    public long MinimumPopulation = 300_000;
    public int MinimumRegions = 1;

    /// <summary>
    /// What it takes for a region next door to go with them.
    ///
    /// A secession is not tidy. The province that has finally had enough is the one with a
    /// people and a grievance, but the ground that leaves with it is whatever was next to it
    /// and had nothing left to lose - and that ground is often not the same nationality at
    /// all. A region joins on either count: it was nearly there itself, or it is simply
    /// angry and ungoverned enough that when the border moved it did not move back.
    /// </summary>
    public float JoinPressure = 0.55f;

    public float JoinDiscontent = 0.55f;
    public int JoinLimit = 6;

    /// <summary>
    /// And the whole thing is capped, because a country does not cease to exist in one
    /// afternoon. Past this share of the country's people the joiners are dropped, largest
    /// first, until what leaves is a secession rather than a dissolution.
    /// </summary>
    public float MaximumShare = 0.40f;

    public int MilitiaFormations = 4;
    public float Stability = 0.42f;
    public float WarSupport = 0.80f;

    public float Acceptance = 0.10f;

    /// <summary>
    /// How much of a state's grip has to be gone before it stops being able to fight at all,
    /// and how much acceptance that buys when it is. Above the threshold instability changes
    /// nothing: a shaky government is the one most likely to make an example of somebody.
    /// </summary>
    public float CollapseBelow = 0.30f;

    public float Collapse = 0.30f;

    /// <summary>
    /// How much an army in being argues against letting go, and how large an army has to be
    /// before it argues at full strength. A state with divisions to send sends them.
    /// </summary>
    public float ArmyResolve = 0.22f;

    public float ArmyResolveDivisions = 24f;

    public int CooldownDays = 730;
}

/// <summary>What a country did about a part of it leaving.</summary>
public enum SecessionOutcome
{
    /// <summary>Let them go. A border moves and nobody dies.</summary>
    Accepted,

    /// <summary>Refused, and went to war about it.</summary>
    Contested
}

public sealed class SecessionReport
{
    public Nation Seceded;
    public Nation Parent;
    public SecessionOutcome Outcome;
    public string Culture = "";
    public int Regions;
    public long Population;

    /// <summary>
    /// What the parent was on the morning of it, not what it is now.
    ///
    /// A country that comes apart three times is smaller each time, so a share worked out
    /// afterwards says a third secession took half the country when it took a seventh. The
    /// figure that means something is the one taken on the day.
    /// </summary>
    public long ParentPopulation;

    public string Reason = "";

    /// <summary>
    /// `national`, `sectarian`, `federal` or `discontent` - see <see cref="Nation.FoundedInRevolt"/>.
    /// </summary>
    public string Revolt = "";

    /// <summary>
    /// Every region that left, and which of the two reasons put it there. The interface
    /// needs this to be able to say the true thing - that the core went for its people and
    /// the rest went because it was next door and had had enough - rather than implying one
    /// nation neatly walked out.
    /// </summary>
    public IReadOnlyList<SecessionPiece> Pieces = Array.Empty<SecessionPiece>();
}

public static class SecessionCore
{
    public static SecessionWeights Weights { get; private set; } = new();

    static SecessionCore() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Weights = new SecessionWeights();
        foreach (var document in GameDataLoader.DataDocuments("common", "secession"))
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
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Number) continue;
            var value = entry.Value.GetSingle();
            switch (entry.Name.ToLowerInvariant())
            {
                case "base": Weights.Base = value; break;
                case "nationality": Weights.Nationality = value; break;
                case "radicalism_floor": Weights.RadicalismFloor = value; break;
                case "radicalism": Weights.Radicalism = value; break;
                case "weakness_floor": Weights.WeaknessFloor = value; break;
                case "weakness": Weights.Weakness = value; break;
                case "war_multiplier": Weights.WarMultiplier = value; break;
                case "minimum_foreign_share": Weights.MinimumForeignShare = value; break;
                case "minimum_population": Weights.MinimumPopulation = (long)value; break;
                case "minimum_regions": Weights.MinimumRegions = (int)MathF.Round(value); break;
                case "join_pressure": Weights.JoinPressure = value; break;
                case "join_discontent": Weights.JoinDiscontent = value; break;
                case "join_limit": Weights.JoinLimit = (int)MathF.Round(value); break;
                case "maximum_share": Weights.MaximumShare = value; break;
                case "militia_formations": Weights.MilitiaFormations = (int)MathF.Round(value); break;
                case "stability": Weights.Stability = value; break;
                case "war_support": Weights.WarSupport = value; break;
                case "acceptance": Weights.Acceptance = value; break;
                case "collapse_below": Weights.CollapseBelow = value; break;
                case "collapse": Weights.Collapse = value; break;
                case "army_resolve": Weights.ArmyResolve = value; break;
                case "army_resolve_divisions": Weights.ArmyResolveDivisions = value; break;
                case "cooldown_days": Weights.CooldownDays = (int)MathF.Round(value); break;
                default: errors?.Add($"common/secession: '{entry.Name}' is not something this game reads."); break;
            }
        }
    }

    /// <summary>
    /// How much closer one region came to leaving this week. Zero for the overwhelming
    /// majority of regions in the world, which is the intended answer.
    /// </summary>
    public static float Pressure(Campaign campaign, Nation owner, ScenarioState state)
    {
        if (campaign is null || owner is null || state is null) return 0f;
        var weights = Weights;

        var foreign = EconomySystem.ForeignShare(state, owner.Tag);
        var federal = foreign < weights.MinimumForeignShare && IsFederalBreak(campaign, owner, state);
        if (!federal && foreign < weights.MinimumForeignShare) return 0f;
        if (state.Population < weights.MinimumPopulation) return 0f;

        var losing = false;
        var torn = false;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(owner.Id)) continue;
            // A state fighting a civil war of its own cannot hold its minorities as it did:
            // the centre is busy, the army is split, and every grievance sees its chance.
            if (war.CivilConflict && campaign.NationById(war.EnemyLeaderOf(owner.Id)) is { } enemy &&
                (enemy.SecededFromId == owner.Id || enemy.Movement is not null || enemy.FactionId.Length > 0)) torn = true;
            // Losing, not merely fighting. A war being won is a state demonstrating that it
            // can still do things to people.
            var score = war.AttackerId == owner.Id ? war.WarScore : -war.WarScore;
            if (score < 0f) { losing = true; break; }
        }

        var identity = federal ? M.Clamp01(state.PoliticalDivergence) : M.Clamp01(foreign);
        var scale = federal ? MathF.Max(0f, campaign.Defines.FederalPressureScale) : 1f;
        return weights.Base
               * MathF.Pow(identity, MathF.Max(0.01f, weights.Nationality))
               * (weights.RadicalismFloor + weights.Radicalism * M.Clamp01(state.Radicalism))
               * (1f - M.Clamp01(state.Stability))
               * (weights.WeaknessFloor + weights.Weakness * (1f - M.Clamp01(owner.Stability)))
               * (losing ? weights.WarMultiplier : 1f)
               * (torn ? campaign.Defines.SecessionCivilWarMultiplier : 1f)
               * (1f - SocialMemory.BorderRelief(campaign, state))
               * scale;
    }

    /// <summary>
    /// A region of the country's own people, politically finished with the centre.
    ///
    /// The ethnic gate above is what keeps Wales in a Britain that works. It also kept
    /// every state of a union that works on paper and has stopped working in fact: the
    /// people are the country's own, so the pressure was defined to be zero. This is the
    /// other road. It needs the region's politics to have diverged, the region to have
    /// stopped accepting the government, the centre to be illegitimate, and an economic
    /// stress that is already on the books — debt, shortage, or a region much poorer than
    /// the country it is taxed by. None of those is a penalty for being large, and none
    /// of them names a tag.
    /// </summary>
    public static bool IsFederalBreak(Campaign campaign, Nation owner, ScenarioState state)
    {
        if (campaign is null || owner is null || state is null) return false;
        if (!campaign.FeatureEnabled("federal_fracture") || !campaign.FeatureEnabled("political_cohorts")) return false;
        if (!campaign.FeatureEnabled("secession")) return false;
        if (state.MapIndex == owner.CapitalStateMapIndex || state.Population < Weights.MinimumPopulation) return false;
        if (EconomySystem.ForeignShare(state, owner.Tag) >= Weights.MinimumForeignShare) return false;
        if (state.PoliticalDivergence < campaign.Defines.CenterRegionIdeologySplit) return false;
        if (state.GovernmentApproval < 0f || state.GovernmentApproval > campaign.Defines.FederalGovernmentApprovalCeiling) return false;
        if (owner.Stability > campaign.Defines.FederalStabilityCeiling) return false;
        var defines = campaign.Defines;
        if (owner.ShortageIndex >= defines.FederalShortageStress) return true;
        if (campaign.FeatureEnabled("public_debt"))
        {
            var debt = owner.DebtCondition;
            if (debt == DebtCondition.Stressed || debt == DebtCondition.Default
                || debt == DebtCondition.Insolvent || debt == DebtCondition.Restructuring) return true;
        }
        return state.StandardOfLiving + defines.FederalLivingGap < owner.StandardOfLiving;
    }

    /// <summary>
    /// How little this region minds going, whoever is asking - 0 to 1.
    ///
    /// Distinct from <see cref="Pressure"/>, and deliberately so. Pressure is what makes a
    /// region leave: a people, a grievance and a state that cannot hold it. This is only
    /// whether a region that finds itself on the far side of a new border stays there - and
    /// that question has no nationality in it at all.
    ///
    /// It is what makes a secession look like one. A border drawn strictly along a people
    /// is a border no secession in history has ever had; the ground that goes with Tatarstan
    /// is whatever was next to Tatarstan and had had enough, whoever lives on it.
    /// </summary>
    public static float Discontent(Campaign campaign, Nation owner, ScenarioState state)
    {
        if (campaign is null || owner is null || state is null) return 0f;
        var weights = Weights;
        return M.Clamp01(
            (weights.RadicalismFloor + weights.Radicalism * M.Clamp01(state.Radicalism))
            * (1f - M.Clamp01(state.Stability))
            * (weights.WeaknessFloor + weights.Weakness * (1f - M.Clamp01(owner.Stability)))
            * 4f);
    }

    /// <summary>
    /// Whether the state accepts it or fights about it.
    ///
    /// Not a coin flip. A state that is settled, armed and losing something small refuses;
    /// one that is falling apart, at war elsewhere and losing something that was never
    /// really its own lets go - and a federation lets go more easily than a one-party state,
    /// because that is what the two words mean.
    /// </summary>
    public static SecessionOutcome Response(Campaign campaign, Nation parent,
        IReadOnlyList<SecessionPiece> leaving, out string reason)
    {
        var weights = Weights;
        var accept = weights.Acceptance;

        // Collapse, not instability.
        //
        // This used to read "a state that cannot govern itself cannot hold anybody else"
        // and gave a shaky government most of a free pass, which had the mechanic backwards:
        // the state that a region is leaving is by construction a state whose stability has
        // already gone, so the term that was meant to describe an exception described nearly
        // every case, and nine secessions in ten were waved through. A government losing its
        // grip is the one that most needs to be seen not to lose a province.
        //
        // Below the threshold there is genuinely nothing left to send, and that is the case
        // this is for - not a bad year, an absence of a state.
        var collapse = M.Clamp01(parent.Stability) / M.Max(0.01f, weights.CollapseBelow);
        if (collapse < 1f) accept += (1f - collapse) * weights.Collapse;

        // Busy elsewhere.
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(parent.Id)) { accept += 0.20f; break; }

        // How much of the country is going. Losing a tenth is a province; losing half is
        // the country, and no state accepts that quietly.
        long going = 0;
        foreach (var piece in leaving) going += piece.Population;
        var share = parent.Population <= 0 ? 0f : going / (float)parent.Population;
        accept -= share * 0.60f;

        // And what kind of state it is. The centralisation of the form of government is
        // exactly the right number for this: it is already the answer to "how much does the
        // middle insist on deciding".
        var government = PoliticsCore.GovernmentOf(parent);
        accept -= (government.Centralisation - 0.5f) * 0.50f;

        // War support is the appetite for another one.
        accept += (1f - M.Clamp01(parent.WarSupport)) * 0.25f;

        // And an army in being is an argument for using it. A state with two dozen divisions
        // and a province walking out does not hold a press conference about it.
        var divisions = 0f;
        foreach (var army in campaign.Armies)
            if (army.NationId == parent.Id) divisions += army.Divisions;
        accept -= M.Clamp01(divisions / M.Max(1f, weights.ArmyResolveDivisions)) * weights.ArmyResolve;

        accept = M.Clamp01(accept);
        var accepted = campaign.Random.NextFloat() < accept;
        reason = accepted
            ? $"accepted ({accept:P0} likely: stability {parent.Stability:P0}, losing {share:P0}, {divisions:0.#} division(s))"
            : $"contested ({accept:P0} likely: stability {parent.Stability:P0}, losing {share:P0}, {divisions:0.#} division(s))";
        return accepted ? SecessionOutcome.Accepted : SecessionOutcome.Contested;
    }
}
