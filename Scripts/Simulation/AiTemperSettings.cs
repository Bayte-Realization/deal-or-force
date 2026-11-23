namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How readily the computer's governments go to war: 1 is the game as tuned, 0.25 a world of
    /// cautious cabinets, 2 one of adventurers. It scales what a government expects a war to be
    /// worth (AiStatecraft.WarValue) - the gains up, the reasons for restraint down - so every
    /// rule behind a war still decides it, only the appetite moves. Set per campaign from the
    /// game options (ai_aggression), or by a mod in defines.
    /// </summary>
    public float AiAggression { get; set; } = 1f;

    /// <summary>
    /// How readily the computer's governments say yes: to a deal, a treaty, a peace offer, a plea
    /// to come into a war. 1 is the game as tuned; below it they are stubborn, above it pliable.
    /// Read through <see cref="AiTemper"/>, which every one of those answers asks.
    /// </summary>
    public float AiAgreeableness { get; set; } = 1f;
}

/// <summary>The two game-option dials on the computer's governments, read in one place.</summary>
public static class AiTemper
{
    public static float Aggression(Campaign c) => M.Clamp(c?.Defines?.AiAggression ?? 1f, 0.1f, 4f);
    public static float Agreeableness(Campaign c) => M.Clamp(c?.Defines?.AiAgreeableness ?? 1f, 0.1f, 4f);

    /// <summary>Share of the way to "yes" (above 1) or to "no" (below 1) the dial moves an answer.</summary>
    private static float Pull(Campaign c, out bool towardsYes)
    {
        var dial = Agreeableness(c);
        towardsYes = dial > 1f;
        return towardsYes ? M.Clamp((dial - 1f) * 0.35f, 0f, 0.7f) : M.Clamp((1f - dial) * 0.4f, 0f, 0.6f);
    }

    /// <summary>
    /// A willingness on the -1..1 scale the treaty and intervention answers use, moved by the
    /// agreeableness dial: pliable governments come part of the way towards yes, stubborn ones
    /// part of the way towards no. At 1 nothing moves.
    /// </summary>
    public static float Willing(Campaign c, float willingness)
    {
        var pull = Pull(c, out var yes);
        return yes ? willingness + (1f - willingness) * pull : willingness - (1f + willingness) * pull;
    }

    /// <summary>The same on a 0-100 acceptance score (peace offers).</summary>
    public static float Accepting(Campaign c, float score)
    {
        var pull = Pull(c, out var yes);
        return M.Clamp(yes ? score + (100f - score) * pull : score - score * pull, 0f, 100f);
    }

    /// <summary>
    /// Generational and era aggression modifier for a specific country (1f at day 0; 0.78..1.25 over
    /// decades and centuries) driven by deterministic FNV-1a historical drift and temperament.
    /// </summary>
    public static float EraAggressionModifier(Campaign c, Nation actor)
    {
        if (c is null || actor is null || c.ElapsedDays <= 0) return 1f;
        var bias = GeopoliticalCycle.GenerationalPivotBias(c, actor, 0xA661);
        var era = GeopoliticalCycle.CurrentEraRegime(c) switch
        {
            GlobalEraRegime.RevisionistTide => 0.10f,
            GlobalEraRegime.PostImperialTurbulence => 0.06f,
            GlobalEraRegime.MultipolarConcert => -0.09f,
            GlobalEraRegime.ArmedDetente => -0.04f,
            _ => 0f
        };
        var oppOffset = (AiTemperament.Of(c, actor).Opportunism - 0.5f) * 0.16f;
        return M.Clamp(1f + bias * 0.14f + era + oppOffset, 0.78f, 1.25f);
    }

    /// <summary>
    /// Pair- and era-modulated agreement willingness (-1..1). Preserves <see cref="Willing"/>
    /// identically at day 0 and bends bilateral diplomacy across seeded decades and centuries.
    /// </summary>
    public static float EraWilling(Campaign c, Nation actor, Nation other, float willingness)
    {
        var baseWilling = Willing(c, willingness);
        if (c is null || actor is null || other is null || c.ElapsedDays <= 0) return baseWilling;
        var alignment = GeopoliticalCycle.BilateralRealignmentBias(c, actor, other);
        var foresightShift = (AiTemperament.Of(c, actor).Foresight - 0.5f) * 0.08f;
        return M.Clamp(baseWilling + alignment * 0.14f + foresightShift, -1f, 1f);
    }
}
