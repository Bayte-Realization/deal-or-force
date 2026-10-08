using System;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How a movement's methods move: once a week it closes this share of the distance
    /// between how militant it is and how militant its situation makes it - about two years
    /// to go half the way, so a party does not take up arms over one bad month.
    /// </summary>
    public float MovementMilitancyDrift { get; set; } = 0.0065f;
    /// <summary>Where the stages begin: militant, armed struggle, terror.</summary>
    public float MovementMilitantAt { get; set; } = 0.30f;
    public float MovementArmedAt { get; set; } = 0.50f;
    public float MovementTerrorAt { get; set; } = 0.78f;
}

public enum MovementStage { Civic, Militant, Armed, Terror }

/// <summary>
/// What a movement is prepared to do, and how that changes.
///
/// A movement's traits said once and for ever whether it was a party or an army: the SNP
/// could never take up arms, and an armed wing could never put them down. Movements do
/// both. The IRA went from bombs to ballots and the PKK from ballots to bombs and partly
/// back; a peaceful campaign shut out and beaten on the streets radicalises; a militia
/// that wins legitimacy, money and a hearing sheds its gunmen.
///
/// So each movement carries a militancy, 0 to 1, that its traits only start: a civic party
/// at a tenth, an armed wing at two thirds. Every week it moves towards what the movement's
/// situation argues for - the radicalism of the people who follow it, the crackdowns it has
/// suffered this year, the guns its backers send, against the legitimacy the world gives it
/// and the ground it governs - and the traits pull at it as an identity does, without
/// holding it. Four stages are read off it: a civic campaign, a militant one, an armed
/// struggle that can rise, and terror. Crossing one is news.
/// </summary>
public static class MovementMethods
{
    public static float Initial(MovementDefinition definition)
    {
        if (definition is null) return 0.3f;
        if (definition.Has("civic_independence") || definition.Has("nonviolence") || definition.Has("humanitarian")) return 0.1f;
        if (definition.Has("armed_wing") || definition.Has("tunnel_network")) return 0.68f;
        return definition.CanStageUprising ? 0.55f : 0.25f;
    }

    public static float Of(Nation movement)
    {
        var state = movement?.Movement;
        if (state is null) return 0f;
        // Legacy saves have no value yet. Reading a tooltip or checking eligibility
        // must not initialize simulation state differently on two network peers.
        return state.Militancy < 0f ? Initial(Movements.DefinitionOf(movement)) : state.Militancy;
    }

    private static readonly Defines Defaults = new();

    public static MovementStage StageOf(Campaign c, Nation movement)
    {
        var militancy = Of(movement);
        var d = c?.Defines ?? Defaults;
        return militancy >= d.MovementTerrorAt ? MovementStage.Terror
            : militancy >= d.MovementArmedAt ? MovementStage.Armed
            : militancy >= d.MovementMilitantAt ? MovementStage.Militant : MovementStage.Civic;
    }

    /// <summary>Whether it will take up arms now: what its traits allow, or what it has become.</summary>
    public static bool WillRise(Campaign c, Nation movement, MovementDefinition definition) =>
        definition is not null && StageOf(c, movement) >= MovementStage.Armed &&
        (definition.CanStageUprising || Of(movement) >= (c?.Defines ?? Defaults).MovementArmedAt + 0.1f);

    /// <summary>Whether it campaigns peacefully now, whatever it began as.</summary>
    public static bool Peaceful(Campaign c, Nation movement) => StageOf(c, movement) == MovementStage.Civic;

    /// <summary>What its situation argues for, 0 to 1.</summary>
    public static float Target(Campaign c, Nation movement, MovementDefinition definition)
    {
        var state = movement.Movement;
        // The temper of the people who follow it, weighted by how much they follow it.
        float weight = 0f, radical = 0f;
        foreach (var (region, support) in state.Support)
        {
            if (support <= 0.01f) continue;
            var place = c.States.Find(s => s.MapIndex == region);
            if (place is null) continue;
            weight += support;
            radical += support * place.Radicalism;
        }
        var temper = weight > 0f ? radical / weight : 0.3f;
        var year = c.ElapsedDays - 365;
        var crackdowns = c.Chronicle.Entries.Count(e => e.Kind == "movement_crackdown" && e.TargetId == movement.Id && e.Day >= year);
        var listed = state.DesignatedBy.Count;
        var arms = c.Sponsorships.Count(s => !s.Ended && s.ClientId == movement.Id && s.Level >= 2);
        var identity = Initial(definition) - 0.3f;
        var target = 0.2f + identity * 0.6f
                     + (temper - 0.3f) * 0.9f
                     + Math.Min(crackdowns, 4) * 0.06f
                     + Math.Min(listed, 5) * 0.015f
                     + arms * 0.08f
                     - state.Legitimacy * 0.35f
                     - (state.Landless ? 0f : 0.1f);
        return M.Clamp01(target);
    }

    public static void Tick(Campaign c)
    {
        if (c is null || !c.FeatureEnabled("movements") || !c.FeatureEnabled("movement_lifecycle") ||
            c.ElapsedDays % 7 != 5) return;
        foreach (var movement in c.Nations)
        {
            var state = movement.Movement;
            if (state is null || state.Dissolved) continue;
            var definition = Movements.DefinitionOf(movement);
            if (definition is null || definition.Has("for_hire") || definition.Has("humanitarian")) continue;
            var before = StageOf(c, movement);
            state.Militancy = M.Clamp01(Of(movement) + (Target(c, movement, definition) - Of(movement)) * c.Defines.MovementMilitancyDrift);
            var after = StageOf(c, movement);
            if (after == before) continue;
            var kind = after > before ? "movement_radicalised" : "movement_moderated";
            c.Chronicle.Record(c, kind, movement.Id, subject: Loc.Get(StageName(after)));
            state.LastReport = Loc.Format(after > before ? "It has turned to {0}." : "It has turned back to {0}.", Loc.Get(StageName(after)).ToLowerInvariant());
            c.MarkStrategicChange();
        }
    }

    public static string StageName(MovementStage stage) => stage switch
    {
        MovementStage.Civic => "Peaceful campaign",
        MovementStage.Militant => "Militant activism",
        MovementStage.Armed => "Armed struggle",
        _ => "Terror"
    };
}
