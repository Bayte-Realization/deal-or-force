using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Days before a movement's traits start to follow what it does: the catalogue's
    /// list is the author's word on the first day, not something to overturn in the first week.</summary>
    public int MovementTraitGraceDays { get; set; } = 180;
}

public sealed partial class MovementState
{
    /// <summary>Traits it has taken on since the first day, and those it has shed. The catalogue's
    /// list is where it began; these two are what it has become.</summary>
    public readonly HashSet<string> GainedTraits = new(StringComparer.Ordinal);
    public readonly HashSet<string> LostTraits = new(StringComparer.Ordinal);
    /// <summary>Moves on every change, so the movement's effective traits are rebuilt only then.
    /// Not saved: a loaded game starts at 0 with nothing cached.</summary>
    internal int TraitsVersion;
}

public sealed partial class MovementDefinition
{
    /// <summary>The same movement with another list of traits - what it has become, read by every
    /// rule that asks <see cref="Has"/>.</summary>
    internal MovementDefinition WithTraits(IEnumerable<string> traits)
    {
        var copy = (MovementDefinition)MemberwiseClone();
        copy.Traits = new List<string>(traits);
        return copy;
    }
}

/// <summary>
/// A movement's traits follow what it does.
///
/// They were a list written on the first day and shown as chips for ever: the Catalan campaign
/// stayed nonviolent whatever it did, and a movement that took up arms never had an armed wing
/// unless the file said so. Now each week the movement is read - how far it will go
/// (militancy), whether it has formations in the field, how wide its support runs, who
/// recognises it and who lists it as terrorist - and the traits that answer to those are taken
/// on or shed, with some slack so a trait does not flicker. Every rule that asks whether a
/// movement has a trait reads the result (Movements.DefinitionOf), so an armed wing raised in
/// play opens what an armed wing opens. Nothing here names a movement.
/// </summary>
public static class MovementTraits
{
    public static void Review(Campaign c, Nation movement)
    {
        var state = movement.Movement;
        var basic = Movements.BaseDefinitionOf(movement);
        if (state is null || basic is null || state.Dissolved || c.ElapsedDays < c.Defines.MovementTraitGraceDays) return;
        bool Has(string trait) => state.GainedTraits.Contains(trait) || (basic.Has(trait) && !state.LostTraits.Contains(trait));

        var militancy = MovementMethods.Of(movement);
        var armed = false;
        foreach (var army in c.Armies)
            if (army.NationId == movement.Id) { armed = true; break; }
        var top = 0f;
        var wide = 0;
        foreach (var share in state.Support.Values)
        {
            top = MathF.Max(top, share);
            if (share >= 0.25f) wide++;
        }

        var armedWing = Has("armed_wing") ? armed || militancy >= 0.30f : armed || militancy >= 0.55f;
        Set(c, movement, basic, "armed_wing", armedWing);
        Set(c, movement, basic, "nonviolence", Has("nonviolence")
            ? !armedWing && militancy < 0.45f
            : !armedWing && militancy < 0.12f);
        Set(c, movement, basic, "mass_movement", Has("mass_movement") ? top >= 0.20f : top >= 0.40f || wide >= 3);
        Set(c, movement, basic, "government_partner", Has("government_partner")
            ? state.Legitimacy >= 0.15f && state.DesignatedBy.Count < 3
            : state.RecognisedBy.Count >= 2 && state.Legitimacy >= 0.40f && state.DesignatedBy.Count == 0);
    }

    private static void Set(Campaign c, Nation movement, MovementDefinition basic, string trait, bool want)
    {
        var state = movement.Movement;
        var had = state.GainedTraits.Contains(trait) || (basic.Has(trait) && !state.LostTraits.Contains(trait));
        if (had == want) return;
        if (want)
        {
            state.LostTraits.Remove(trait);
            if (!basic.Has(trait)) state.GainedTraits.Add(trait);
        }
        else
        {
            state.GainedTraits.Remove(trait);
            if (basic.Has(trait)) state.LostTraits.Add(trait);
        }
        state.TraitsVersion++;
        var name = Loc.Get("trait." + trait);
        state.LastReport = Loc.Format(want ? "It has taken on a new character: {0}." : "It is no longer {0}.", name);
        c.Chronicle.Record(c, want ? "movement_trait_gained" : "movement_trait_lost", movement.Id, subject: name);
        c.MarkStrategicChange();
    }
}
