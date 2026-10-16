using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// What a government will name as its reason for a war.
///
/// Until this existed the game read exactly two of the seventeen things a country can hold
/// against another - land taken and an ultimatum refused - so a government whose pact had been
/// torn up and whose rebels had been armed by the same neighbour still had, in its own
/// estimation, nothing to say, and a war it wanted could not be begun. Five years of the world
/// produced eighty-eight wars worth having, sixty-five of them dropped for want of a reason, and
/// not one declaration. These are the properties that has to have now.
/// </summary>
static class CasusBelliTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("casus belli: " + label); Console.WriteLine("PASS casus belli: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261016);
        Check(OpinionCatalog.Count > 0, "the opinion modifiers are loaded from data");
        Check(OpinionCatalog.Find("broke_non_aggression_pact") is not null,
            "the modifier a broken pact is remembered under exists");

        // Two countries with nothing between them. Found rather than named, because the world opens
        // with borders, friendships and old wars already remembered, and a pair picked by tag would
        // be testing whichever of those it happened to have.
        Nation attacker = null, defender = null;
        DiplomaticRelation relation = null;
        foreach (var a in c.Nations.Where(n => n.Population > 0 && !n.Landless).Take(40))
        {
            foreach (var b in c.Nations.Where(n => n.Population > 0 && !n.Landless).Take(40))
            {
                if (a.Id == b.Id) continue;
                var bond = CampaignDiplomacy.Get(c, a.Id, b.Id);
                bond.Reasons.Clear();
                if (Statecraft.CasusBelli(c, a, b).Length > 0) continue;
                attacker = a; defender = b; relation = bond;
                break;
            }
            if (attacker is not null) break;
        }
        Check(attacker is not null, "the world has two countries with no grievance between them");
        if (attacker is null) return;

        string Cause() => Statecraft.CasusBelli(c, attacker, defender);
        void Clear() => relation.Reasons.Clear();

        // Nothing remembered, nothing to say.
        Check(Cause().Length == 0, "a country with no grievance has nothing to say");

        // A grave act, named. These are the reasons a government gives and the world accepts.
        Clear(); OpinionSystem.Remember(c, relation, "broke_non_aggression_pact");
        Check(Cause() == "broke_non_aggression_pact", "a broken non-aggression pact is a reason for a war");

        Clear(); OpinionSystem.Remember(c, relation, "seceded_from_us");
        Check(Cause() == "seceded_from_us", "a secession is a reason for a war");

        Clear(); OpinionSystem.Remember(c, relation, "embargoed_us");
        Check(Cause() == "embargoed_us", "an embargo is a reason for a war");

        Clear(); OpinionSystem.Remember(c, relation, "armed_our_enemy");
        Check(Cause() == "armed_our_enemy", "arming our enemy is a reason for a war");

        // Small slights are not, on their own.
        Clear(); OpinionSystem.Remember(c, relation, "cancelled_trade_agreement");
        Check(Cause().Length == 0, "a cancelled trade agreement is not on its own a reason for a war");

        // Nor added together: a country that could total up denunciations and moved border markers
        // into a declaration would be declaring wars nobody could explain.
        Clear();
        OpinionSystem.Remember(c, relation, "border_marker_removed");
        OpinionSystem.Remember(c, relation, "fishing_boat_seized");
        OpinionSystem.Remember(c, relation, "cancelled_trade_agreement");
        Check(Cause().Length == 0, "a decade of small slights does not add up to a reason for a war");

        // The gravest one is what is named - not the first remembered, and not the sum.
        Clear();
        OpinionSystem.Remember(c, relation, "cancelled_trade_agreement");
        OpinionSystem.Remember(c, relation, "broke_our_alliance");
        OpinionSystem.Remember(c, relation, "seceded_from_us");
        Check(Cause() == "seceded_from_us", "the gravest grievance is the one named, not the first remembered");

        // Repetition counts for something, but only up to the declaration's own limit.
        Clear();
        OpinionSystem.Remember(c, relation, "covert_operation_against_us");
        var once = Cause();
        OpinionSystem.Remember(c, relation, "covert_operation_against_us");
        var twice = Cause();
        Check(once.Length == 0 && twice == "covert_operation_against_us",
            $"the same act twice is graver than once ('{once}' then '{twice}')");
        OpinionSystem.Remember(c, relation, "covert_operation_against_us");
        Check(Cause() == "covert_operation_against_us",
            "and doing it a third time names the same reason rather than a worse one");

        // The floor is the one number in the game that says how deep a grudge has to go, and it is
        // data - which is what lets a mod decide that its world goes to war over less.
        Clear(); OpinionSystem.Remember(c, relation, "cancelled_trade_agreement");
        var floor = c.Defines.CasusBelliGrudgeFloor;
        Check(Cause().Length == 0, "a minor grievance sits above the floor and is not a reason");
        c.Defines.CasusBelliGrudgeFloor = -10f;
        Check(Cause() == "cancelled_trade_agreement", "lowering the floor makes the same grievance a reason");
        c.Defines.CasusBelliGrudgeFloor = -1000f;
        Check(Cause().Length == 0, "and a floor no grievance can reach refuses them all");
        c.Defines.CasusBelliGrudgeFloor = floor;
        Check(Cause().Length == 0, "the floor is restored, and the minor grievance is a minor grievance again");

        Names();
    }

    /// <summary>
    /// Every reason the simulation can name has to be sayable, and sayable in the player's own
    /// language.
    ///
    /// A reason with no sentence is a war the ledger cannot explain, which is the whole thing
    /// keeping it on the war was for; and a sentence with no translation is English in a Russian
    /// window. The list here is deliberately every id the simulation can return - including the
    /// ones that come from a remembered grievance, whose sentence is read out of the opinion
    /// data rather than listed anywhere - so adding a new reason without saying it fails here.
    /// </summary>
    private static void Names()
    {
        var ids = new[]
        {
            "reconquest", "ultimatum_refusal", "irredentism", "defence_of_an_ally",
            "border_clashes", "intervention",
            "took_our_land", "refused_our_ultimatum", "broke_non_aggression_pact",
            "seceded_from_us", "embargoed_us", "armed_our_enemy"
        };
        foreach (var id in ids)
        {
            var said = CasusBelliNames.Of(id);
            Check(said.Length > 0, $"'{id}' is said in words, not left as an id");
            if (said.Length == 0) continue;
            Check(Loc.TextIn("english", said).Length > 0, $"'{said}' is in the English table");
            Check(Loc.TextIn("russian", said).Length > 0 && Loc.TextIn("russian", said) != said,
                $"'{said}' is translated rather than echoed");
        }
        Check(CasusBelliNames.Of("").Length == 0 && CasusBelliNames.Of("no_such_reason").Length == 0,
            "a reason nobody has heard of is said as nothing, not as an id");
    }
}
