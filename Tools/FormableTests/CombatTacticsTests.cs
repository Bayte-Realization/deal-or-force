using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class CombatTacticsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("combat tactics: " + label);
        Console.WriteLine("PASS combat tactics: " + label);
    }

    static int Slot(CombatTactic tactic) => Array.IndexOf(CombatTactics.All, tactic);

    public static void Run()
    {
        var defines = new Defines();

        // The same morning, twice: the draw is from the seed, not from state, so a replayed
        // war picks the same pair on the same day.
        var weights = CombatTactics.Weights(true, 0.6f, 0f, 0.8f, 2, "plains", 0.5f);
        var first = CombatTactics.Pick(1234, 7, true, 10, (float[])weights.Clone());
        var second = CombatTactics.Pick(1234, 7, true, 10, (float[])weights.Clone());
        Check(first is not null && second is not null && first.Id == second.Id,
            "the same morning picks the same manoeuvre");

        var distinct = new HashSet<string>();
        for (var day = 0; day < 365; day++)
        {
            var pick = CombatTactics.Pick(1234, 7, true, day, (float[])weights.Clone());
            if (pick is not null) distinct.Add(pick.Id);
        }
        Check(distinct.Count >= 3, "a year of mornings is not one manoeuvre");

        // The counter-cycle: assault beats ambush, ambush beats blitz, blitz beats delay,
        // delay beats assault - and nothing beats what it merely faces.
        Check(CombatTactic.Assault.Counters(CombatTactic.Ambush) &&
              CombatTactic.Ambush.Counters(CombatTactic.Blitz) &&
              CombatTactic.Blitz.Counters(CombatTactic.Delay) &&
              CombatTactic.Delay.Counters(CombatTactic.Assault),
            "the four manoeuvres counter in a cycle");
        Check(!CombatTactic.Blitz.Counters(CombatTactic.Ambush) &&
              !CombatTactic.Skirmish.Counters(CombatTactic.Skirmish),
            "counters are one-way and never self-inflicted");
        Check(MathF.Abs(CombatTactics.Attack(CombatTactic.Blitz, CombatTactic.Delay, defines) -
                        CombatTactic.Blitz.Attack * defines.CombatCounterTacticBonus) < 0.0001f,
            "countering the enemy's manoeuvre is worth its bonus");
        Check(MathF.Abs(CombatTactics.Attack(CombatTactic.Blitz, CombatTactic.Skirmish, defines) -
                        CombatTactic.Blitz.Attack) < 0.0001f,
            "no counter, no bonus");

        // The book closed: nothing is picked and every multiplier is exactly one, which is
        // the old arithmetic to the bit.
        Check(CombatTactics.Defence(null) == 1f && CombatTactics.OrgDamage(null) == 1f &&
              CombatTactics.EnemyAttack(null) == 1f && CombatTactics.EnemyDefence(null) == 1f &&
              CombatTactics.Attack(null, CombatTactic.Blitz, defines) == 1f,
            "a battle without manoeuvres is the old arithmetic");

        var campaign = new Campaign(20261026) { ScenarioId = "combat_tactics_fixture" };
        campaign.Defines.CombatTacticsEnabled = false;
        Check(CombatTactics.Pick(campaign, 7, true, 10, 0.6f, 0f, 0.8f, 2, "plains", 0.5f) is null,
            "with the book closed no manoeuvre is picked");
        campaign.Defines.CombatTacticsEnabled = true;
        Check(CombatTactics.Pick(campaign, 7, true, 10, 0.6f, 0f, 0.8f, 2, "plains", 0.5f) is not null,
            "with the book open a manoeuvre is picked");

        // What an army is made of decides what its staff reaches for.
        var noArmour = CombatTactics.Weights(true, 0.2f, 0f, 0.8f, 1, "plains", 0f);
        Check(noArmour[Slot(CombatTactic.Blitz)] == 0f, "no armour, no blitz");
        Check(noArmour[Slot(CombatTactic.Encirclement)] == 0f, "one axis cannot encircle");
        var twoAxes = CombatTactics.Weights(true, 0.6f, 0f, 0.8f, 2, "plains", 0f);
        Check(twoAxes[Slot(CombatTactic.Encirclement)] > 0f, "two axes can encircle");
        var dugIn = CombatTactics.Weights(false, 0.2f, 0.9f, 0.8f, 1, "plains", 0f);
        Check(dugIn[Slot(CombatTactic.Delay)] > CombatTactic.Delay.BaseWeight &&
              dugIn[Slot(CombatTactic.Hold)] > CombatTactic.Hold.BaseWeight,
            "a dug-in defence reaches for delay and hold");
        var spent = CombatTactics.Weights(false, 0.2f, 0.1f, 0.3f, 1, "plains", 0f);
        Check(spent[Slot(CombatTactic.Withdrawal)] > CombatTactic.Withdrawal.BaseWeight,
            "a spent force reaches for the planned withdrawal");
        var forest = CombatTactics.Weights(false, 0.2f, 0.5f, 0.8f, 1, "forest", 0f);
        var open = CombatTactics.Weights(false, 0.2f, 0.5f, 0.8f, 1, "plains", 0f);
        Check(forest[Slot(CombatTactic.Ambush)] > open[Slot(CombatTactic.Ambush)],
            "concealing ground recommends the ambush");

        // The find-by-id the report reads, including the empty saves of older campaigns.
        Check(CombatTactics.Find("blitz") == CombatTactic.Blitz && CombatTactics.Find("") is null &&
              CombatTactics.Find(null) is null,
            "the report finds the manoeuvre by its id, or nothing");

        // Expanded HOI4-grade tactical manoeuvres and doctrine/initiative weighting.
        Check(CombatTactics.All.Length >= 15, "at least fifteen tactical manoeuvres in the book");
        Check(CombatTactic.BreakthroughTactic.Counters(CombatTactic.ElasticDefense) &&
              CombatTactic.ElasticDefense.Counters(CombatTactic.ShockAssault) &&
              CombatTactic.ShockAssault.Counters(CombatTactic.TacticalCounterattack) &&
              CombatTactic.TacticalCounterattack.Counters(CombatTactic.BreakthroughTactic),
            "expanded manoeuvres form a second counter-cycle");
        var baseWeights = CombatTactics.Weights(true, 0.7f, 0f, 0.85f, 2, "plains", 0.6f);
        var boostedWeights = (float[])baseWeights.Clone();
        CombatTactics.ApplyDoctrineAndInitiative(boostedWeights, true, null, 0.5f, 0.6f);
        Check(boostedWeights[Slot(CombatTactic.BreakthroughTactic)] > baseWeights[Slot(CombatTactic.BreakthroughTactic)],
            "staff skill and recon initiative boost active tactical manoeuvres over meeting engagements");

        Console.WriteLine($"combat tactics: {CombatTactics.All.Length} manoeuvres in the book");
    }
}
