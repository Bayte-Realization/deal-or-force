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
    }
}
