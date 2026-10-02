using System;
using DealOrForce.Simulation;

/// <summary>
/// Who a government would actually attack (item 98): the restraint its own arrangement puts
/// on a war of choice, the peace between two restrained governments, the friends a target
/// would bring, and whether a country of one region is worth a war at all.
/// </summary>
static class RestraintTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL restraint: " + name);
        Console.WriteLine("PASS restraint: " + name);
    }

    public static void Run(Campaign c)
    {
        // A day of the world, so that strength and regions are counted.
        c.IsPaused = false;
        c.TickDay();
        var threshold = c.Defines.AiWarDesireThreshold;
        float Judge(string attacker, string target) =>
            AiStatecraft.JudgeWarFor(c, c.NationByTag(attacker), c.NationByTag(target));

        Check(AiStatecraft.Restraint(c, c.NationByTag("DEU")) >= c.Defines.AiDemocraticPeaceRestraint,
            "a federal social democracy is a restrained government");
        Check(AiStatecraft.Restraint(c, c.NationByTag("RUS")) < AiStatecraft.Restraint(c, c.NationByTag("USA")),
            $"a presidential republic run by nationalists is less restrained than one run by liberals " +
            $"({AiStatecraft.Restraint(c, c.NationByTag("RUS")):0.00} against {AiStatecraft.Restraint(c, c.NationByTag("USA")):0.00})");
        Check(Judge("DEU", "CHE") < threshold, $"Germany will not attack Switzerland ({Judge("DEU", "CHE"):0.00})");
        Check(Judge("ITA", "VAT") < threshold, $"nor Italy the Vatican ({Judge("ITA", "VAT"):0.00})");
        Check(Judge("ITA", "SMR") < threshold, $"nor San Marino ({Judge("ITA", "SMR"):0.00})");
        Check(Judge("FRA", "BEL") < threshold, $"nor France Belgium ({Judge("FRA", "BEL"):0.00})");

        // The same country as a junta weighs the same war very differently.
        var brazil = c.NationByTag("BRA");
        var target = c.NationByTag("SUR");
        var held = brazil.Government;
        var asRepublic = AiStatecraft.JudgeWarFor(c, brazil, target);
        brazil.Government = "military_junta";
        var asJunta = AiStatecraft.JudgeWarFor(c, brazil, target);
        brazil.Government = held;
        Check(asJunta - asRepublic >= 0.5f, $"a junta weighs a war of choice lighter than a republic ({asRepublic:0.00} as a republic, {asJunta:0.00} as a junta)");
        Check(asRepublic < threshold, $"and Brazil as it is does not invade Suriname ({asRepublic:0.00})");

        // A guarantee from a great power is a reason not to.
        var guarantor = c.NationByTag("USA");
        var bond = CampaignDiplomacy.Get(c, guarantor.Id, target.Id);
        brazil.Government = "military_junta";
        var alone = AiStatecraft.JudgeWarFor(c, brazil, target);
        if (guarantor.Id == bond.A) bond.GuaranteeByA = true; else bond.GuaranteeByB = true;
        c.TickDay();
        var sheltered = AiStatecraft.JudgeWarFor(c, brazil, target);
        brazil.Government = held;
        Check(sheltered < alone && sheltered < threshold,
            $"Suriname under an American guarantee is not worth it even to a junta ({alone:0.00} alone, {sheltered:0.00} guaranteed)");
    }
}
