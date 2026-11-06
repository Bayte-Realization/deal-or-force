using System;
using DealOrForce;

internal static class DecisionReadabilityTests
{
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }

    public static void Run()
    {
        foreach (var scale in new[] { .5f, 2f / 3, 1, 1.25f, 2 })
        foreach (var preference in new[] { .75f, 1, 1.25f, 1.75f })
        {
            Check(DecisionReadability.FontSize(12, scale, preference) * scale >= 14 * MathF.Max(1, preference) - .001f,
                $"document paragraph has physical size floor at {scale}/{preference}");
            Check(DecisionReadability.TargetHeight(24, scale, preference) * scale >= 32 * MathF.Max(1, preference) - .001f,
                $"document action has physical hit floor at {scale}/{preference}");
            var rect = DecisionReadability.Fit(1280 / scale, 720 / scale, scale, 400, 140, 750, 830);
            Check(rect.X >= 0 && rect.Y >= 0 && rect.X + rect.Width <= 1280 / scale + .01f &&
                rect.Y + rect.Height <= 720 / scale + .01f, $"decision shell stays in the window at {scale}/{preference}");
        }
        Check(DecisionReadability.FontSize(28, 1, 1) == 28, "large authored headings stay at their original size");
        Check(DecisionReadability.FontSize(12, 2f / 3, 1) == 21, "720p floor is not snapped back down to a 20-unit typography step");
        Check(DecisionReadability.FontSize(12, float.NaN, 1) == 14 &&
            DecisionReadability.FontSize(12, 0, 1) == 14, "invalid transforms have a bounded one-to-one fallback");
        Check(DecisionReadability.FontSize(12, 1, 1) == 14 && DecisionReadability.FontSize(12, 2f / 3, 1) == 21 &&
            DecisionReadability.FontSize(12, 1, 1) == 14, "resize fitting uses authored input, never accumulates zoom");
    }
}
