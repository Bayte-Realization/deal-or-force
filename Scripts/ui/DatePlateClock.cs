using System;

namespace DealOrForce;

/// <summary>
/// The clock's rules on the date plate (docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md, step 3), with
/// nothing of the engine in them: which speed "slower" and "faster" reach, which of the five marks
/// is lit, and which mark a click lands on. DatePlateUI builds the controls; Tools/FormableTests
/// (DatePlateTests) checks a word-for-word copy of these members, since that project does not
/// compile game code that sits beside Godot controls.
/// </summary>
internal static class DatePlateClock
{
    /// <summary>The engine's speeds run 1 to 5 (Defines.SecondsForSpeed); so do the marks.</summary>
    internal const int Slowest = 1;
    internal const int Fastest = 5;

    internal static int Clamp(int speed) => Math.Clamp(speed, Slowest, Fastest);

    /// <summary>One step slower, never below the slowest: "slower" at 1 stays at 1.</summary>
    internal static int Slower(int speed) => Clamp(speed - 1);

    /// <summary>One step faster, never above the fastest: "faster" at 5 stays at 5.</summary>
    internal static int Faster(int speed) => Clamp(speed + 1);

    /// <summary>Mark `pip` (1 to 5) is lit when the speed has reached it.</summary>
    internal static bool Lit(int pip, int speed) => pip >= Slowest && pip <= Clamp(speed);

    /// <summary>
    /// The mark under a click `x` pixels into a row of marks `size` wide with `gap` between them.
    /// A click in a gap belongs to the mark on its left; a click past the end, to the fastest.
    /// </summary>
    internal static int PipAt(float x, float size, float gap)
    {
        var step = Math.Max(1f, size + gap);
        if (!float.IsFinite(x) || x < 0f) return Slowest;
        return Clamp((int)MathF.Floor(x / step) + 1);
    }
}
