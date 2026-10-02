using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The enemy of my enemy (item 98): a country with a rival backs a movement that claims the
/// rival's ground - and never one that claims its own ground or an ally's, never past its
/// payroll, never with the chance turned off.
/// </summary>
static class IncitementTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL incitement: " + name);
        Console.WriteLine("PASS incitement: " + name);
    }

    public static void Run(Campaign c)
    {
        if (c.NationByTag("CTA") is null) Movements.Found(c);
        var tibet = c.NationByTag("CTA");
        var china = c.NationByTag("CHN");
        var india = c.NationByTag("IND");
        var pakistan = c.NationByTag("PAK");
        Check(tibet?.Movement is not null && china is not null && india is not null && pakistan is not null,
            "the Central Tibetan Administration is in the world, claiming ground China holds");
        foreach (var nation in c.Nations) nation.IsHuman = false;

        // India and China, made rivals; India rich enough to pay for it.
        var rivalry = CampaignDiplomacy.Get(c, india.Id, china.Id);
        rivalry.Stance = DiplomaticStance.Rival;
        OpinionSystem.Remember(c, rivalry, "declared_war_on_us");
        OpinionSystem.Remember(c, rivalry, "declared_war_on_us");
        OpinionSystem.Settle(c, rivalry);
        india.Treasury = 500f;
        Check(rivalry.Opinion <= c.Defines.AiIncitementRivalOpinion, $"India counts China a rival ({rivalry.Opinion:0})");
        Check(MovementDealings.RivalHolding(c, india, tibet) == china, "and China holds what the Tibetans claim");

        // Pakistan dislikes China just as much on paper, but is its ally: no rival to weaken.
        var ally = CampaignDiplomacy.Get(c, pakistan.Id, china.Id);
        ally.Stance = DiplomaticStance.Allied;
        OpinionSystem.Remember(c, ally, "declared_war_on_us");
        OpinionSystem.Remember(c, ally, "declared_war_on_us");
        OpinionSystem.Settle(c, ally);
        Check(MovementDealings.RivalHolding(c, pakistan, tibet) is null, "an ally of the holder backs nobody against it");

        var before = rivalry.Opinion;
        void Month()
        {
            for (var day = 0; day < 28 * Math.Max(1, c.Defines.EconomyTickDays); day++)
            {
                typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)(c.ElapsedDays + 1) * 24);
                MovementDealings.Ai(c);
            }
        }

        // Turned off, nothing happens.
        c.Defines.AiIncitementFundChance = 0f;
        c.Defines.AiIncitementRecogniseChance = 0f;
        Month();
        Check(!tibet.Movement.SponsoredBy.ContainsKey(india.Id), "with both chances at 0, India pays nobody");

        // Payroll full, nothing happens either.
        c.Defines.AiIncitementFundChance = 1f;
        c.Defines.AiIncitementMaxFunded = 0;
        Month();
        Check(!tibet.Movement.SponsoredBy.ContainsKey(india.Id), "with no room on its payroll, India pays nobody");

        // Room, money and a rival: India funds the Tibetans - within a year, since it acts on one
        // movement a month and the Tibetans need not be the first China's rebels it reaches.
        c.Defines.AiIncitementMaxFunded = 50;
        for (var month = 0; month < 12 && !tibet.Movement.SponsoredBy.ContainsKey(india.Id); month++) Month();
        Check(tibet.Movement.SponsoredBy.ContainsKey(india.Id), "with room, money and a rival, India funds the Tibetans");
        Check(!tibet.Movement.SponsoredBy.ContainsKey(pakistan.Id), "and China's ally does not");
        OpinionSystem.Settle(c, rivalry);
        Check(rivalry.Reasons.Any(reason => reason.Id == "funds_our_rebels") && rivalry.Opinion <= before,
            $"China notices: {before:0} before, {rivalry.Opinion:0} after");
    }
}
