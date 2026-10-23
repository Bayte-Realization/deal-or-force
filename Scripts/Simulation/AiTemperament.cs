using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How far a country's character may reach from the ordinary case, as a share of it.
    /// Zero makes every government alike; 0.55 lets the most mercurial one run at about one and
    /// a half times the ordinary appetite and the most staid at about half of it. The spread is
    /// centred on one, so adding character to the world does not by itself make it more warlike.
    /// </summary>
    public float AiTemperamentSpread { get; set; } = 0.55f;

    /// <summary>
    /// What an enemy already stretched thin is worth on top of the flat opening bonus, to a
    /// government that watches for openings. Scaled by that government's own opportunism.
    /// </summary>
    public float AiOpportunismStretch { get; set; } = 0.9f;

    /// <summary>
    /// The judgement a war has to reach before a government will turn on a country it is allied
    /// to, at ordinary cunning; a cunning government needs less of it and a staid one more, so the
    /// effective bar runs from about half this to about twice it.
    ///
    /// Calibrated against the rest of the decision rather than picked round: the threshold for
    /// acting on an ordinary war is <c>AiWarDesireThreshold</c> at 0.55, so this asks for a war
    /// the government wants roughly six times more than it wants an ordinary one. Setting it high
    /// enough that it never fires would leave the alliance a permanent fact again, which is the
    /// thing this exists to end; setting it low would make every signature worthless.
    /// </summary>
    public float AiBetrayalNerve { get; set; } = 3.5f;

    /// <summary>What turning on an ally costs in standing with everybody who was watching.</summary>
    public float AiBetrayalNotoriety { get; set; } = 22f;

    /// <summary>How many of a target's friends a far-sighted government works on at once, above the first.</summary>
    public float AiForesightReach { get; set; } = 2f;

    /// <summary>How far a mercurial government moves its own threshold for acting, up or down.</summary>
    public float AiVolatilitySwing { get; set; } = 0.45f;
}

/// <summary>
/// What a government is like, as opposed to what doctrine it follows.
///
/// <see cref="AiStrategyCatalog"/> already gives a country a doctrine - how readily it attacks,
/// how readily it makes friends - but the doctrine is picked from four profiles by size, so every
/// major power in the world played the same way and every small one played the same way. Two
/// countries of the same size with the same neighbours and the same grievances made the same
/// decision on the same day, every time, which is what made the map readable in the wrong sense:
/// a player could learn that it moved on a schedule.
///
/// This is the layer underneath. Each government draws four numbers once, from the campaign seed
/// and its own id, so they are stable for the whole campaign and identical on every machine that
/// plays that seed - and then those numbers are bent by what the country actually is: an
/// authoritarian arrangement seizes openings and plans in one head, an unstable one cannot plan
/// far and changes its mind. Character is therefore partly born and partly earned, and a coup or
/// an election genuinely changes how a country behaves.
///
/// Every one of them is a multiplier centred on one. Nothing here makes the world more or less
/// violent on average; it makes it less alike.
/// </summary>
public static class AiTemperament
{
    /// <summary>
    /// One government's character. Each field is 0..1 with 0.5 the ordinary case; use
    /// <see cref="Character.Scale"/> to turn one into the multiplier the decisions want.
    /// </summary>
    public readonly record struct Character(float Opportunism, float Cunning, float Foresight, float Volatility)
    {
        /// <summary>The ordinary government: no character either way.</summary>
        public static readonly Character Plain = new(0.5f, 0.5f, 0.5f, 0.5f);

        /// <summary>
        /// The multiplier a decision should use: 1 at the ordinary case, and up to
        /// <c>1 ± spread</c> at the extremes. Never zero and never negative, so a trait can
        /// weaken an appetite but cannot invert it.
        /// </summary>
        public static float Scale(float trait, float spread) =>
            M.Max(0.05f, 1f + (M.Clamp01(trait) - 0.5f) * 2f * M.Max(0f, spread));
    }

    private static readonly Dictionary<int, Character> Characters = new();
    private static Campaign _characterCampaign;
    private static int _characterDay = int.MinValue;

    /// <summary>
    /// This government's character, worked out once a day for whoever asks.
    ///
    /// Stamped with the campaign as well as the day, for the reason every other day-cache in the
    /// simulation is: a client rebuilding from a host's snapshot holds two campaigns at once, and
    /// a table that only knows the day would answer one of them with the other's countries.
    /// </summary>
    public static Character Of(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return Character.Plain;
        if (!ReferenceEquals(_characterCampaign, campaign) || _characterDay != campaign.ElapsedDays)
        {
            _characterCampaign = campaign;
            _characterDay = campaign.ElapsedDays;
            Characters.Clear();
        }
        if (Characters.TryGetValue(nation.Id, out var cached)) return cached;
        var drawn = Draw(campaign, nation);
        Characters[nation.Id] = drawn;
        return drawn;
    }

    /// <summary>The four convenience multipliers the war decisions actually ask for.</summary>
    public static float OpportunismOf(Campaign campaign, Nation nation) =>
        Character.Scale(Of(campaign, nation).Opportunism, campaign?.Defines.AiTemperamentSpread ?? 0f);

    public static float CunningOf(Campaign campaign, Nation nation) =>
        Character.Scale(Of(campaign, nation).Cunning, campaign?.Defines.AiTemperamentSpread ?? 0f);

    public static float ForesightOf(Campaign campaign, Nation nation) =>
        Character.Scale(Of(campaign, nation).Foresight, campaign?.Defines.AiTemperamentSpread ?? 0f);

    public static float VolatilityOf(Campaign campaign, Nation nation) =>
        Character.Scale(Of(campaign, nation).Volatility, campaign?.Defines.AiTemperamentSpread ?? 0f);

    /// <summary>
    /// Born, then earned.
    ///
    /// The born part is a roll from the seed and the country's id, with the day left out on
    /// purpose: a character that drifted from week to week would be weather, not a government.
    /// The earned part is the arrangement and the creed running it, and the state of the country
    /// itself - which is why the same country plays differently after a coup, and why a country
    /// falling apart cannot hold a plan.
    /// </summary>
    private static Character Draw(Campaign campaign, Nation nation)
    {
        // Day zero: stable for the campaign. Salted per trait so the four are not the same
        // number four times, which is what reading one roll four ways would give.
        float Born(int salt) => MovementRules.Roll(campaign.Seed, nation.Id, 0, salt);

        var authority = M.Clamp01(PoliticsCore.IdeologyOf(nation).Authority);
        var restraint = M.Clamp01(AiStatecraft.Restraint(campaign, nation));
        var stability = M.Clamp01(nation.Stability);
        var doctrine = AiStrategyCatalog.Of(campaign, nation);

        // Seizing an opening is a thing a government that answers to nobody does readily, and a
        // thing a doctrine already called aggressive does readily.
        var opportunism = M.Lerp(Born(9001),
            M.Max(authority, M.Clamp01((doctrine.Aggression - 0.6f) / 0.9f)), 0.35f);

        // Cunning is patience with an edge: the willingness to keep a promise only as long as it
        // pays. An authoritarian arrangement has fewer constituencies to answer to.
        var cunning = M.Lerp(Born(9002), 1f - restraint * 0.5f + authority * 0.5f, 0.4f);

        // Foresight is the one a country in crisis loses first. A government that may not
        // survive the year does not build for the decade; a restrained one is used to waiting.
        var foresight = M.Lerp(Born(9003), stability * 0.6f + restraint * 0.4f, 0.45f);

        // And volatility is what the player is meant to notice: the same country, on the same
        // map, moving when it was not expected to. Instability and one-man rule both feed it.
        var volatility = M.Lerp(Born(9004), (1f - stability) * 0.6f + authority * 0.4f, 0.4f);

        var (oppShift, cunShift, foreShift, volShift) = Rulers.TemperamentShift(campaign, nation);
        opportunism += oppShift;
        cunning += cunShift;
        foresight += foreShift;
        volatility += volShift;

        return new Character(M.Clamp01(opportunism), M.Clamp01(cunning),
            M.Clamp01(foresight), M.Clamp01(volatility));
    }
}
