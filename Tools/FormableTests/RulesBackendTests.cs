using System;
using DealOrForce.Rules;

/// <summary>
/// The F# rule kernels that carry the game's rule boundaries without a C# fallback.
///
/// The first three kernels (assault readiness, war score, peace rules) have both
/// implementations and a differential test holding them to the bit. The later six are
/// called directly from C# with no fallback: if the Rules assembly is missing the game
/// does not degrade, it stops - and a formula that quietly returns NaN or flips a sign
/// would surface only as a strange world months later. This suite is the loud version of
/// that: every kernel loads, every entry point answers, and the answers are finite and
/// pointed the right way at a handful of representative inputs.
/// </summary>
static class RulesBackendTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("rules backends: " + label);
        Console.WriteLine("PASS rules backends: " + label);
    }

    public static void Run()
    {
        // Combat tempo: the cauldron bites, the envelopment widens, the gates stay shut
        // without a plan. Mirrors the C#-side expectations the tempo suite already states.
        Check(float.IsFinite(CombatTempoRules.EncirclementFactor(true, true, 14, 0.3f, 14)),
            "encirclement factor is finite");
        Check(CombatTempoRules.EncirclementFactor(true, true, 14, 0.3f, 14) <
              CombatTempoRules.EncirclementFactor(true, false, 0, 0.3f, 14),
            "a cut-off formation fights worse than a supplied one");
        Check(CombatTempoRules.EncirclementFactor(false, true, 14, 0.3f, 14) == 1f,
            "no defines, no penalty");
        Check(CombatTempoRules.EnvelopmentFactor(true, 3, 4, 0.15f) >
              CombatTempoRules.EnvelopmentFactor(true, 1, 4, 0.15f),
            "more axes envelop better");
        Check(float.IsFinite(CombatTempoRules.Breakthrough(true, 3f, 0.8f, 0.9f, 0.1f, 1, 1f,
                              0.3f, 0.4f, 1.2f, 2.5f, 0.35f)),
            "breakthrough is finite");
        Check(float.IsFinite(CombatTempoRules.Counterattack(true, 3f, 0.9f, 0.9f, 0.8f, 0.5f, false,
                              0.25f, 0.4f, 0.9f, 2.0f, 0.4f, 0.3f)),
            "counterattack is finite");

        // Economy: credit terms answer for a solvent and a drowning state, and the
        // drowning one pays more.
        var solvent = EconomyRules.EvaluateCreditTerms(0.2f, 1000f, 0.7f, 0.03f, 0f, 0f, false, 0.5f, 1f, 0.2f);
        var drowning = EconomyRules.EvaluateCreditTerms(2500f, 1000f, 0.2f, 0.30f, 40f, 20f, true, 0.5f, 1f, 0.2f);
        Check(float.IsFinite(solvent.AnnualSpread) && float.IsFinite(drowning.AnnualSpread),
            "credit spreads are finite");
        Check(drowning.AnnualSpread >= solvent.AnnualSpread,
            "a drowning state pays at least the solvent rate");
        Check(float.IsFinite(EconomyRules.DebtCapacity(1000f, 1f, 0.5f, 0.8f, 0.2f)) &&
              EconomyRules.DebtCapacity(1000f, 1f, 0.5f, 0.8f, 0.2f) >= 0f,
            "debt capacity is finite and never negative");

        // Demography: cohorts move the way demography says.
        Check(DemographyRules.WorkingShare(0.3f, 0.1f) > DemographyRules.WorkingShare(0.3f, 0.4f),
            "more old people, fewer workers per head");
        Check(DemographyRules.FertilityFactor(0.4f) > DemographyRules.FertilityFactor(0.1f),
            "more young cohorts, higher fertility");
        Check(DemographyRules.MortalityFactor(0.4f, 0.15f, 0.1f) > DemographyRules.MortalityFactor(0.15f, 0.15f, 0.1f),
            "an older population dies more");

        // Diplomacy: ambition grows with authority and nationalism; a holdout matters
        // only to a kindred people; offers are finite.
        Check(DiplomacyRules.ConsolidationAmbition(0.8f, true, 0.7f, 0.1f) >
              DiplomacyRules.ConsolidationAmbition(-0.5f, false, 0.3f, 0.6f),
            "an authorised nationalist state is hungrier for union");
        Check(DiplomacyRules.ConsolidationWarValue(0.2f, 0.5f, 0.5f, 0.8f, 0.8f) == 0f,
            "no war value below the force-ambition threshold");
        Check(float.IsFinite(DiplomacyRules.ConsolidationWarValue(0.9f, 0.5f, 0.5f, 0.8f, 0.8f)),
            "war value above the threshold is finite");

        // Diplomatic acts: kindness has a threshold and hostility a negative one; a friend
        // is readier than an enemy; an act's price is never negative.
        Check(DiplomaticActRules.MinimumOpinion(11) == 15f && DiplomaticActRules.MinimumOpinion(13) == -40f,
            "kindness asks for opinion and disaster relief works against it");
        Check(DiplomaticActRules.Willingness(40f, 1, 1) > DiplomaticActRules.Willingness(-40f, 1, 1),
            "a friend is readier than an enemy");
        Check(DiplomaticActRules.Price(1, 10f, 5f) >= 0f && DiplomaticActRules.Price(8, 10f, 5f) == 15f,
            "an act's price is never negative and a state visit costs half again");

        // Political policy: the adoption gate refuses an invalid nation with the right code.
        Check(PoliticalPolicyRules.EvaluateAdoption(
                false, true, true, false, false, 0, false, false, false, false, false, 0,
                false, 0, 0, 0f, 0f, 0.001f) == PolicyAdoptRefusal.MechanicUnavailable,
            "an adoption without a nation refuses as mechanic unavailable");
        Check(PoliticalPolicyRules.EvaluateAdoption(
                true, true, true, false, false, 0, false, false, false, true, false, 0,
                false, 0, 100, 1000f, 1000f, 0.001f) == PolicyAdoptRefusal.Allowed,
            "a plain adoption at peace with cash is allowed");

        // The dual kernels stay installed: the boots ran, and the game answers through
        // them rather than around them. (The peace hook keeps no public flag; its
        // behaviour is covered by the peace suites either way.)
        Check(DealOrForce.Simulation.WarScoreRulesHook.IsInstalled,
            "the war score kernel is installed and answering");
    }
}
