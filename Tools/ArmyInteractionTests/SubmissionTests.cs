using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Dependency without a war (item 66): protection, money and politics, decided by the smaller
/// country on terms it can name.
/// </summary>
static class SubmissionTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260925);
        c.AttachProvinceTopology(map);
        var usa = c.NationByTag("USA");
        var isl = c.NationByTag("ISL");
        c.ConfigureSinglePlayer(isl.Id);
        usa.Treasury = 10_000f;

        // A small country, free, at peace with the United States, and much weaker.
        var small = c.Nations
            .Where(n => n.Id != usa.Id && !n.IsHuman && !n.IsSubject && n.Population > 100_000 && c.StatesOf(n).Length > 0 &&
                        !CampaignDiplomacy.AtWar(c, usa.Id, n.Id) &&
                        SubmissionSystem.StrengthRatio(c, usa, n) > c.Defines.SubmissionMinimumStrengthRatio * 5)
            .OrderBy(n => n.Population).First();
        Console.WriteLine($"  asking {small.Tag} ({small.Population:N0} people)");

        // --- A: who may be asked -------------------------------------------------------------
        Check(SubmissionSystem.Refusal(c, usa, isl, SubmissionRoute.Protection).Length > 0,
            "a player is never made a subject by somebody else's offer");
        Check(SubmissionSystem.Refusal(c, small, usa, SubmissionRoute.Protection).Length > 0,
            "a small country cannot ask a great power to answer to it");
        foreach (var route in Enum.GetValues<SubmissionRoute>())
            Check(SubmissionSystem.Refusal(c, usa, small, route).Length == 0 &&
                  SubjectCatalog.Knows(SubmissionSystem.KindOf(c, route)),
                $"{route}: the United States may ask {small.Tag}, and the kind it makes ({SubmissionSystem.KindOf(c, route)}) exists");

        // --- B: the terms are named ---------------------------------------------------------
        var relation = CampaignDiplomacy.Get(c, usa.Id, small.Id);
        relation.Opinion = -90f;
        var why = new Breakdown();
        var cold = SubmissionSystem.Willingness(c, small, usa, SubmissionRoute.Protection, why);
        Console.WriteLine("  terms: " + string.Join("; ", why.Entries.Select(e => $"{e.Source} {e.Value:+0.00;-0.00}")));
        Check(why.Entries.Count >= 3 && MathF.Abs(why.Total - cold) < 0.0001f,
            "the answer is the sum of terms the interface can show");
        relation.Opinion = 90f;
        var warm = SubmissionSystem.Willingness(c, small, usa, SubmissionRoute.Protection);
        Check(warm > cold + 1.5f, "a friend is readier to answer to us than an enemy");
        var paid = SubmissionSystem.Willingness(c, small, usa, SubmissionRoute.Economic);
        Check(paid > SubmissionSystem.Willingness(c, small, usa, SubmissionRoute.Economic) - 0.0001f,
            "the same question gets the same answer");

        // --- C: refused, and remembered -------------------------------------------------------
        relation.Opinion = -90f;
        var before = usa.Treasury;
        var notoriety = usa.Notoriety;
        var cost = SubmissionSystem.Cost(c, small, SubmissionRoute.Political);
        var bribe = new OfferSubmissionCommand { IssuerId = usa.Id, TargetId = small.Id, Route = SubmissionRoute.Political };
        bribe.Apply(c);
        Check(!bribe.Accepted && !small.IsSubject, $"an enemy turns the bribe down ({bribe.Answer})");
        Check(MathF.Abs(before - cost - usa.Treasury) < 0.01f && usa.Notoriety > notoriety,
            "and the money is spent anyway, and the world noticed");
        Check(relation.SubmissionAskedDay == c.ElapsedDays &&
              SubmissionSystem.Refusal(c, usa, small, SubmissionRoute.Protection).Length > 0,
            "nobody asks again the same year");

        // --- D: accepted --------------------------------------------------------------------
        relation.SubmissionAskedDay = -9999;
        relation.Opinion = 90f;
        before = usa.Treasury;
        var smallTreasury = small.Treasury;
        var payment = SubmissionSystem.Cost(c, small, SubmissionRoute.Economic);
        var offer = new OfferSubmissionCommand { IssuerId = usa.Id, TargetId = small.Id, Route = SubmissionRoute.Economic };
        offer.Apply(c);
        Check(offer.Accepted && small.AnswersTo(usa.Id) && small.SubjectType == c.Defines.SubmissionEconomicKind,
            $"a friend takes the money and becomes a {small.SubjectType} ({offer.Answer})");
        Check(MathF.Abs(before - payment - usa.Treasury) < 0.01f && MathF.Abs(smallTreasury + payment - small.Treasury) < 0.01f,
            "and the money changes hands");
        Check(SubmissionSystem.Refusal(c, usa, small, SubmissionRoute.Protection).Length > 0,
            "a subject is not asked a second time");

        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "submission-tests"), "submission-tests");
        Check(restored.Success && restored.Campaign.NationById(small.Id).AnswersTo(usa.Id) &&
              CampaignDiplomacy.Get(restored.Campaign, usa.Id, small.Id).SubmissionAskedDay == relation.SubmissionAskedDay,
            "a save keeps the arrangement and the memory of the refusal");

        // --- E: the countries nobody plays ----------------------------------------------------
        var subjectsBefore = c.Nations.Count(n => n.IsSubject);
        c.Defines.SubmissionAiChancePerMonth = 1f;
        c.Defines.SubmissionAiMargin = 0f;
        foreach (var n in c.Nations)
            foreach (var other in c.Nations)
                if (n.Id < other.Id && CampaignDiplomacy.Peek(c, n.Id, other.Id) is { } r && !CampaignDiplomacy.AtWar(c, n.Id, other.Id))
                    r.Opinion = MathF.Max(r.Opinion, 60f);
        var randomBefore = c.Random.State;
        for (var day = 0; day < 30; day++)
        {
            // A month's scheduling cannot be tested by repeating the same calendar day.
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);
            SubmissionSystem.Tick(c);
        }
        var gained = c.Nations.Count(n => n.IsSubject) - subjectsBefore;
        Console.WriteLine($"  in a month of eager asking, {gained} country(ies) chose an overlord");
        Check(gained > 0, "a country nobody plays does ask, and is sometimes told yes");
        Check(c.Nations.Where(n => n.IsSubject).All(n => !c.NationById(n.OverlordId).IsSubject),
            "nobody answers to a country that itself answers to somebody");
        Check(c.Random.State == randomBefore, "asking draws nothing from the campaign's random stream");

        // --- F: a protector is one of the powers nearest to it ----------------------------------
        // In the 25-year probe a Jersey that had left Britain went to Estonia, 2 000 km off and
        // inside SubmissionReachKm, past Britain, France and the Low Countries.
        var jersey = c.NationByTag("JEY");
        var full = -c.Defines.SubmissionDistancePenalty;
        Check(SubmissionSystem.Reach(c, jersey, c.NationByTag("FRA")) == 0f,
            "France, next door to Jersey, is within its reach");
        Check(SubmissionSystem.Reach(c, jersey, c.NationByTag("EST")) <= full * 0.99f &&
              SubmissionSystem.Reach(c, jersey, c.NationByTag("PRT")) <= full * 0.99f,
            "Estonia and Portugal, past a crowd of nearer powers, are as good as across the world to Jersey");
        c.Defines.SubmissionNearerProtectors = 0;
        Check(SubmissionSystem.Reach(c, jersey, c.NationByTag("EST")) == 0f,
            "SubmissionNearerProtectors 0 turns the crowd off");
        c.Defines.SubmissionNearerProtectors = 3;
    }
}
