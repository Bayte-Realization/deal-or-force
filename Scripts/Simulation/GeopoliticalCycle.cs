using System;

namespace DealOrForce.Simulation;

/// <summary>Macro-historical international regime characterizing a seeded decade or century.</summary>
public enum GlobalEraRegime
{
    MultipolarConcert = 0,
    RevisionistTide = 1,
    ArmedDetente = 2,
    PostImperialTurbulence = 3
}

/// <summary>
/// Natural macro-historical waves of international tension, generational leadership drift,
/// bloc realignments, and détente (~6y, 10y, 35y, and 100y+ seeded cycles).
/// All randomness is routed through deterministic zero-allocation FNV-1a hashing so identical
/// seeds replay bit-identically across 10-, 100-, and 1000-year horizons.
/// </summary>
public static class GeopoliticalCycle
{
    public const int DaysPerYear = 365;
    public const int DaysPerDecade = 3650;
    public const int DaysPerCentury = 36500;

    private const uint FnvOffsetBasis = 2166136261u;
    private const uint FnvPrime = 16777619u;

    public static int DecadeIndex(int elapsedDays) => elapsedDays <= 0 ? 0 : elapsedDays / DaysPerDecade;
    public static int CenturyIndex(int elapsedDays) => elapsedDays <= 0 ? 0 : elapsedDays / DaysPerCentury;

    internal static uint Fnv1aInit() => FnvOffsetBasis;

    internal static uint Fnv1aMixInt(uint hash, int value)
    {
        unchecked
        {
            var u = (uint)value;
            hash ^= u & 0xFFu; hash *= FnvPrime;
            hash ^= (u >> 8) & 0xFFu; hash *= FnvPrime;
            hash ^= (u >> 16) & 0xFFu; hash *= FnvPrime;
            hash ^= (u >> 24) & 0xFFu; hash *= FnvPrime;
            return hash;
        }
    }

    internal static uint Fnv1aMixTag(uint hash, string text)
    {
        unchecked
        {
            if (string.IsNullOrEmpty(text))
            {
                hash ^= 0xFFu;
                hash *= FnvPrime;
                return hash;
            }

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch is >= 'a' and <= 'z') ch = (char)(ch - 32);
                hash ^= (uint)(ch & 0xFF);
                hash *= FnvPrime;
                if (ch > 0xFF)
                {
                    hash ^= (uint)((ch >> 8) & 0xFF);
                    hash *= FnvPrime;
                }
            }
            hash ^= 0x5Au;
            hash *= FnvPrime;
            return hash;
        }
    }

    internal static float HashToUnit(uint hash)
    {
        unchecked
        {
            // Final avalanche mix for uniform low-bit dispersion across small tag strings.
            hash ^= hash >> 16;
            hash *= 0x85EBCA6Bu;
            hash ^= hash >> 13;
            hash *= 0xC2B2AE35u;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) / 16777216f;
        }
    }

    internal static float HashToSigned(uint hash) => HashToUnit(hash) * 2f - 1f;

    internal static uint HashNationEpoch(int seed, Nation nation, int generation, int salt)
    {
        var hash = Fnv1aInit();
        hash = Fnv1aMixInt(hash, seed);
        hash = Fnv1aMixTag(hash, nation?.Tag);
        if (string.IsNullOrEmpty(nation?.Tag))
            hash = Fnv1aMixInt(hash, nation?.Id ?? -1);
        hash = Fnv1aMixInt(hash, generation);
        hash = Fnv1aMixInt(hash, salt);
        return hash;
    }

    internal static uint HashPairEpoch(int seed, Nation a, Nation b, int epoch, int salt)
    {
        var first = a;
        var second = b;
        if (a is not null && b is not null)
        {
            var cmp = string.Compare(a.Tag, b.Tag, StringComparison.OrdinalIgnoreCase);
            if (cmp > 0 || (cmp == 0 && a.Id > b.Id))
            {
                first = b;
                second = a;
            }
        }
        var hash = Fnv1aInit();
        hash = Fnv1aMixInt(hash, seed);
        hash = Fnv1aMixTag(hash, first?.Tag);
        hash = Fnv1aMixTag(hash, second?.Tag);
        if (string.IsNullOrEmpty(first?.Tag) || string.IsNullOrEmpty(second?.Tag))
        {
            hash = Fnv1aMixInt(hash, first?.Id ?? -1);
            hash = Fnv1aMixInt(hash, second?.Id ?? -1);
        }
        hash = Fnv1aMixInt(hash, epoch);
        hash = Fnv1aMixInt(hash, salt);
        return hash;
    }

    private static float Smoothstep(float t)
    {
        var c = Math.Clamp(t, 0f, 1f);
        return c * c * (3f - 2f * c);
    }

    /// <summary>
    /// Global drift offset in points (-8f .. +10f) for international diplomatic tension.
    /// Zero at day 0; blends 6-year short cycles with seeded multi-decade and multi-century harmonics
    /// plus feedback from active major wars.
    /// </summary>
    public static float GlobalTensionOffset(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays <= 0) return 0f;

        var days = campaign.ElapsedDays;
        var phaseShift6y = HashToUnit(Fnv1aMixInt(Fnv1aMixInt(Fnv1aInit(), campaign.Seed), 0x0610)) * (2.0 * Math.PI);
        var phase6y = days * (2.0 * Math.PI / 2190.0) + phaseShift6y;
        var wave = (float)Math.Sin(phase6y);
        var secondaryWave = (float)Math.Sin(phase6y * 2.3 + 1.2) * 0.35f;

        // Multi-decade (35y) and multi-century (137y) harmonics anchored deterministically per seed.
        var phaseShift35y = HashToUnit(Fnv1aMixInt(Fnv1aMixInt(Fnv1aInit(), campaign.Seed), 0x3510)) * (2.0 * Math.PI);
        var phaseShift137y = HashToUnit(Fnv1aMixInt(Fnv1aMixInt(Fnv1aInit(), campaign.Seed), 0x1370)) * (2.0 * Math.PI);
        var macroWave = (float)Math.Sin(days * (2.0 * Math.PI / 12775.0) + phaseShift35y) * 0.32f
                      + (float)Math.Sin(days * (2.0 * Math.PI / 50005.0) + phaseShift137y) * 0.24f;

        // Smooth generational decade tide so different seeds traverse distinct geopolitical chapters.
        var decade = DecadeIndex(days);
        var century = CenturyIndex(days);
        var decadeT = Smoothstep((days % DaysPerDecade) / (float)DaysPerDecade);
        var tideNow = HashToSigned(Fnv1aMixInt(Fnv1aMixInt(Fnv1aMixInt(Fnv1aInit(), campaign.Seed), decade), century));
        var nextCentury = (days + DaysPerDecade) / DaysPerCentury;
        var tideNext = HashToSigned(Fnv1aMixInt(Fnv1aMixInt(Fnv1aMixInt(Fnv1aInit(), campaign.Seed), decade + 1), nextCentury));
        var decadeTide = M.Lerp(tideNow, tideNext, decadeT) * 0.30f;

        // Feedback from global major conflicts: if the world is engulfed in large-scale wars, global tension rises.
        var activeMajorWars = 0;
        foreach (var war in Warfare.OpenWars(campaign))
        {
            if (!war.Ended && (war.Intensity >= 1 || war.AttackerLosses + war.DefenderLosses > 50_000))
                activeMajorWars++;
        }
        var warFeedback = MathF.Min(5f, activeMajorWars * 1.5f);

        return Math.Clamp((wave + secondaryWave + macroWave + decadeTide) * 5.2f + warFeedback, -8.0f, 10.0f);
    }

    /// <summary>Macro-historical international regime for the current seed, century, decade, and conflict load.</summary>
    public static GlobalEraRegime CurrentEraRegime(Campaign campaign)
    {
        if (campaign is null) return GlobalEraRegime.MultipolarConcert;
        var days = Math.Max(0, campaign.ElapsedDays);
        var decade = DecadeIndex(days);
        var century = CenturyIndex(days);
        var rollHash = Fnv1aMixInt(Fnv1aMixInt(Fnv1aMixInt(Fnv1aMixInt(Fnv1aInit(), campaign.Seed), decade), century), 0xE4A1);
        var bucket = (int)(rollHash % 4u);
        if (days <= 0) return (GlobalEraRegime)bucket;

        var offset = GlobalTensionOffset(campaign);
        if (offset >= 4.2f) return bucket % 2 == 0 ? GlobalEraRegime.RevisionistTide : GlobalEraRegime.PostImperialTurbulence;
        if (offset <= -3.5f) return bucket % 2 == 0 ? GlobalEraRegime.MultipolarConcert : GlobalEraRegime.ArmedDetente;
        return (GlobalEraRegime)bucket;
    }

    /// <summary>
    /// Seeded generational character pivot for a nation (-1 .. +1). Zero on unaged day-0 bootstrap states
    /// so day-0 temperament baselines remain centered; drifts smoothly over decades, centuries, and ruler turnovers.
    /// </summary>
    public static float GenerationalPivotBias(Campaign campaign, Nation nation, int salt = 0)
    {
        if (campaign is null || nation is null) return 0f;
        var days = Math.Max(0, campaign.ElapsedDays);
        if (days <= 0 && nation.RulerGeneration <= 0) return 0f;

        var decade = DecadeIndex(days);
        var century = CenturyIndex(days);
        var gen = decade + nation.RulerGeneration;
        var decadeT = Smoothstep((days % DaysPerDecade) / (float)DaysPerDecade);

        var cur = HashToSigned(HashNationEpoch(campaign.Seed, nation, gen, 0x6E00 ^ salt));
        var next = HashToSigned(HashNationEpoch(campaign.Seed, nation, gen + 1, 0x6E00 ^ salt));
        var decadeBlend = M.Lerp(cur, next, decadeT);

        var centuryAnchor = HashToSigned(HashNationEpoch(campaign.Seed, nation, century, 0xCE00 ^ salt));
        var ramp = days > 0 ? MathF.Min(1f, days / 120f) : 1f;
        return Math.Clamp((decadeBlend * 0.65f + centuryAnchor * 0.35f) * ramp, -1f, 1f);
    }

    /// <summary>
    /// Symmetric seeded bloc affinity / friction drift (-1 .. +1) between two countries over time.
    /// Zero at day 0; rotates smoothly across decades and centuries so multi-century runs yield distinct alliance blocs.
    /// </summary>
    public static float BilateralRealignmentBias(Campaign campaign, Nation a, Nation b)
    {
        if (campaign is null || a is null || b is null || a.Id == b.Id || campaign.ElapsedDays <= 0) return 0f;

        var days = campaign.ElapsedDays;
        var decade = DecadeIndex(days);
        var century = CenturyIndex(days);
        var decadeT = Smoothstep((days % DaysPerDecade) / (float)DaysPerDecade);

        var cur = HashToSigned(HashPairEpoch(campaign.Seed, a, b, decade, 0xB10C));
        var next = HashToSigned(HashPairEpoch(campaign.Seed, a, b, decade + 1, 0xB10C));
        var decadeBias = M.Lerp(cur, next, decadeT);
        var centuryBias = HashToSigned(HashPairEpoch(campaign.Seed, a, b, century, 0xCE47));

        var eraShift = CurrentEraRegime(campaign) switch
        {
            GlobalEraRegime.MultipolarConcert => 0.12f,
            GlobalEraRegime.ArmedDetente => 0.04f,
            GlobalEraRegime.RevisionistTide => -0.10f,
            GlobalEraRegime.PostImperialTurbulence => -0.14f,
            _ => 0f
        };

        var ramp = MathF.Min(1f, days / 180f);
        return Math.Clamp((decadeBias * 0.68f + centuryBias * 0.32f + eraShift) * ramp, -1f, 1f);
    }

    /// <summary>
    /// Seeded succession and post-imperial fragmentation pressure multiplier (1f at day 0; 0.72 .. 1.38 over time).
    /// </summary>
    public static float SystemicFragmentationPressure(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || campaign.ElapsedDays <= 0) return 1f;

        var days = campaign.ElapsedDays;
        var decade = DecadeIndex(days);
        var century = CenturyIndex(days);
        var wave = HashToSigned(HashNationEpoch(campaign.Seed, nation, decade + century * 11, 0xFA65));
        var eraBias = CurrentEraRegime(campaign) switch
        {
            GlobalEraRegime.PostImperialTurbulence => 0.14f,
            GlobalEraRegime.RevisionistTide => 0.05f,
            GlobalEraRegime.MultipolarConcert => -0.10f,
            _ => 0f
        };
        var ramp = MathF.Min(1f, days / 365f);
        return Math.Clamp(1f + (wave * 0.24f + eraBias) * ramp, 0.72f, 1.38f);
    }

    /// <summary>Whether the international system is currently in a relaxed détente phase.</summary>
    public static bool IsDetenteEra(Campaign campaign) => GlobalTensionOffset(campaign) < -2.0f;

    /// <summary>Whether the international system is currently in an era of heightened geopolitical friction.</summary>
    public static bool IsFrictionEra(Campaign campaign) => GlobalTensionOffset(campaign) > 2.0f;
}
