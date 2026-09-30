using System;
using System.Globalization;
using System.Security.Cryptography;

namespace DealOrForce.Simulation;

/// <summary>Seeds shown to players use the complete positive 32-bit range.</summary>
public static class CampaignSeedSelection
{
    public static bool TryParse(string text, out int seed) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seed) && seed > 0;

    // Casting Unix milliseconds to int overflows long before applying a modulus.
    public static int Roll() => RandomNumberGenerator.GetInt32(1, int.MaxValue);
}
