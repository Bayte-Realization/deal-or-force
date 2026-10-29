using System;
using DealOrForce.Simulation;

static class CombatTempoTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("combat tempo: " + label);
        Console.WriteLine("PASS combat tempo: " + label);
    }

    public static void Run()
    {
        var defines = new Defines();
        var prepared = CombatTempo.Breakthrough(defines, 3.0f, 0.8f, 0.9f);
        var unprepared = CombatTempo.Breakthrough(defines, 3.0f, 0.0f, 0.9f);
        var weak = CombatTempo.Breakthrough(defines, 0.5f, 0.8f, 0.9f);
        Check(prepared > 0.5f, "local superiority, planning and supply produce a breakthrough");
        Check(unprepared == 0f, "a breakthrough needs a prepared plan, not only numbers");
        Check(weak == 0f, "an attack that is not actually pressuring the line cannot break it");
        Check(prepared <= 1f && prepared >= 0f, "the breakthrough is a bounded fraction");

        var counter = CombatTempo.Counterattack(defines, 3.0f, 0.9f, 0.9f);
        var noLine = CombatTempo.Counterattack(defines, 3.0f, 0.1f, 0.9f);
        var spent = CombatTempo.Counterattack(defines, 0.5f, 0.9f, 0.9f);
        Check(counter > 0.5f, "a dug-in, supplied defence can counterattack a spent assault");
        Check(noLine == 0f, "counterattack needs a line to counter from");
        Check(spent == 0f, "counterattack is not free when the attacker is not under pressure");
        Check(counter <= 1f && counter >= 0f, "the counterattack is a bounded fraction");
        Check(CombatTempo.Breakthrough(null, 3f, 1f, 1f) == 0f &&
              CombatTempo.Counterattack(null, 3f, 1f, 1f) == 0f,
            "missing defines never create a tempo bonus");

        // Advanced multi-factor tests
        var entrenchedBreakthrough = CombatTempo.Breakthrough(defines, 2.0f, 0.8f, 0.9f, 0.9f, 1, 1.0f);
        var openBreakthrough = CombatTempo.Breakthrough(defines, 2.0f, 0.8f, 0.9f, 0.0f, 1, 1.0f);
        Check(entrenchedBreakthrough < openBreakthrough, "deep entrenchment drastically resists frontal breakthroughs");

        var flankedBreakthrough = CombatTempo.Breakthrough(defines, 2.2f, 0.8f, 0.9f, 0.8f, 3, 1.0f);
        var frontalBreakthrough = CombatTempo.Breakthrough(defines, 2.2f, 0.8f, 0.9f, 0.8f, 1, 1.0f);
        Check(flankedBreakthrough > frontalBreakthrough, "multi-axis flanking reduces entrenchment resistance");

        var spentAttackerCounter = CombatTempo.Counterattack(defines, 0.5f, 0.8f, 0.9f, 0.25f, 0.65f, false);
        Check(spentAttackerCounter > 0.35f, "punitive counterattack strikes an exhausted, spent assault");

        var desperateCounter = CombatTempo.Counterattack(defines, 0.5f, 0.8f, 0.9f, 0.45f, 0.25f, true);
        Check(desperateCounter > 0.20f, "desperate homeland defense mounts tenacious counter-thrusts");

        // Cauldron / encirclement combat penalty tests
        Check(CombatTempo.EncirclementFactor(defines, false, 10) == 1f,
            "supplied formation suffers no encirclement combat penalty");
        Check(CombatTempo.EncirclementFactor(defines, true, 0) == 1f,
            "zero days cut off suffers no encirclement combat penalty yet");
        var halfCutOff = CombatTempo.EncirclementFactor(defines, true, Math.Max(1, defines.SupplyCutoffDays / 2));
        var fullCutOff = CombatTempo.EncirclementFactor(defines, true, defines.SupplyCutoffDays * 2);
        Check(halfCutOff < 1f && halfCutOff > fullCutOff,
            "encirclement combat penalty deepens as pocket isolation matures");
        Check(MathF.Abs(fullCutOff - (1f - defines.EncirclementMaxCombatPenalty)) < 0.001f,
            "mature encirclement reaches the configured cauldron combat penalty cap");
        Check(CombatTempo.EncirclementFactor(null, true, 10) == 1f,
            "null defines never NaN or zero out encirclement factor");

        // Multi-directional envelopment bonus tests
        Check(CombatTempo.EnvelopmentFactor(defines, 1) == 1f && CombatTempo.EnvelopmentFactor(defines, 0) == 1f,
            "single-direction frontal attack gets no multi-axis envelopment bonus");
        var twoAxes = CombatTempo.EnvelopmentFactor(defines, 2);
        var threeAxes = CombatTempo.EnvelopmentFactor(defines, 3);
        var cappedAxes = CombatTempo.EnvelopmentFactor(defines, defines.MaxAttackDirections + 5);
        Check(twoAxes > 1f && threeAxes > twoAxes,
            "attacking from multiple provinces grants an increasing envelopment bonus");
        Check(MathF.Abs(cappedAxes - CombatTempo.EnvelopmentFactor(defines, defines.MaxAttackDirections)) < 0.0001f,
            "envelopment bonus caps cleanly at MaxAttackDirections");
        Check(CombatTempo.EnvelopmentFactor(null, 3) == 1f,
            "null defines return neutral envelopment factor");
    }
}
