using System;
using DealOrForce.Simulation;

/// <summary>
/// Wars that nobody wins on the map, only on the score.
///
/// Ten years of the bench showed what that costs: a war at a hundred points - a total victory by
/// every measure the game keeps - ran for five hundred days without a peace, and one at minus
/// thirty-six ran six hundred and forty-six days over six hundred and seventy-eight casualties.
/// Two holes made it possible, and both are pinned here:
///
/// - the losing side could not sue for peace until its home front fell apart, even when the score
///   already said the war was lost (<see cref="AiStatecraft.DecisivelyBeaten"/>);
/// - the winning side's indemnity branch tested an "unknown" opponent morale typed in as 0,5 - a
///   value above the capitulation threshold, so the branch could never fire at any score
///   (<see cref="AiStatecraft.OpponentMoraleEstimate"/>).
/// </summary>
static class DecisivePeaceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("decisive peace: " + label);
        Console.WriteLine("PASS decisive peace: " + label);
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(947986505);
        var a = c.Nations[0];
        var d = c.Nations[1];
        var war = new StrategicWar
        {
            Id = 9001, AttackerId = a.Id, DefenderId = d.Id,
            StartedDay = c.ElapsedDays - 300, WarScore = 100f
        };

        // ---- what the other side still has, measured rather than guessed ----------------
        Check(Math.Abs(AiStatecraft.OpponentMoraleEstimate(war, a.Id) - 0f) < 1e-4f,
            "a total victory reads the other side as finished");
        Check(Math.Abs(AiStatecraft.OpponentMoraleEstimate(war, d.Id) - 1f) < 1e-4f,
            "and the side that is losing reads the winner as it is: unchanged");
        war.WarScore = 20f;
        Check(Math.Abs(AiStatecraft.OpponentMoraleEstimate(war, a.Id) - 0.8f) < 1e-4f,
            "a narrow lead leaves most of their morale in place");
        Check(AiStatecraft.OpponentMoraleEstimate(war, a.Id) > c.Defines.AiCapitulationWarSupport,
            "and is not enough to demand an indemnity of them");
        war.WarScore = -40f;
        Check(Math.Abs(AiStatecraft.OpponentMoraleEstimate(war, a.Id) - 1f) < 1e-4f,
            "being behind is not a reason to believe they are broken");
        Check(c.Defines.AiCapitulationWarSupport < 0.5f,
            "the constant that used to stand there (0,5) could never pass this threshold at all");

        // ---- a war lost on points is a war lost ----------------------------------------
        war.WarScore = 100f;
        Check(AiStatecraft.DecisivelyBeaten(c, war, d.Id), "the defender a hundred points down has lost the war");
        Check(!AiStatecraft.DecisivelyBeaten(c, war, a.Id), "the winner has not");
        war.WarScore = 50f;
        Check(!AiStatecraft.DecisivelyBeaten(c, war, d.Id), "fifty points down is a bad war, not a lost one");
        war.WarScore = -c.Defines.AiDecisiveWarScore;
        Check(AiStatecraft.DecisivelyBeaten(c, war, a.Id), "the attacker at the decisive line is beaten by the same measure");
        war.WarScore = -c.Defines.AiDecisiveWarScore + 1f;
        Check(!AiStatecraft.DecisivelyBeaten(c, war, a.Id), "one point above it, still fighting");
        war.Ended = true;
        Check(!AiStatecraft.DecisivelyBeaten(c, war, d.Id), "a finished war decides nothing");

        // ---- a frozen front is the border, for the side that is ahead too --------------
        // The stalemate exit used to be open only to the side that was behind, so a war ahead on
        // points with nothing left to demand and no ground changing hands waited for the weariness
        // date at nine hundred days. The bench found those wars at a hundred points, five hundred
        // days old, with no offer ever made.
        var stalled = new StrategicWar
        {
            Id = 9002, AttackerId = a.Id, DefenderId = d.Id,
            StartedDay = c.ElapsedDays - 700, WarScore = 30f, Stalled = true
        };
        Check(WarWeariness.TimeToStop(c, stalled, winning: true),
            "a front that has not moved calls a halt even for the side ahead");
        Check(WarWeariness.TimeToStop(c, stalled, winning: false),
            "and for the side behind, as it always could");
        stalled.Stalled = false;
        Check(!WarWeariness.TimeToStop(c, stalled, winning: true),
            "a front still moving does not, however long the war has run");

        // ---- an army standing about is not a battle -----------------------------------
        // The rule that ends a declaration nobody ever fought in used to be blocked by the mere
        // existence of a front: troops assigned to a frontier the two sides do not share kept a war
        // on the map for a year with no battle, no casualty and no captured province.
        var idle = new StrategicWar
        {
            Id = 9003, AttackerId = a.Id, DefenderId = d.Id,
            StartedDay = c.ElapsedDays - (c.Defines.AiUnreachableWarPeaceDays + 5)
        };
        Check(WarWeariness.NoContactPeaceReady(c, idle),
            "a declaration nobody has fought in ends once it is old enough");
        idle.AttackerFront.Add(1);
        idle.DefenderFront.Add(2);
        Check(WarWeariness.NoContactPeaceReady(c, idle),
            "and troops waiting on a frontier nobody shares do not keep it alive");
        idle.AttackerLosses = 5_000f;
        Check(!WarWeariness.NoContactPeaceReady(c, idle),
            "but a war that has cost somebody something is not a declaration nobody fought");
    }
}
