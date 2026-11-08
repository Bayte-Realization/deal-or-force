using System;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

static class DiplomacyPreviewTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS diplomacy preview: " + label); }

    public static void Run()
    {
        foreach (var seed in new[] { 20260830, 20261016 })
        {
            var c = Campaign.CreateBootstrap(seed);
            var player = c.NationByTag("POL");
            var target = c.NationByTag("DEU");
            c.Relations.Clear();
            foreach (var nation in c.Nations) { nation.AssessedDay = -1; nation.AssessedStrength = 0; }
            var before = CampaignFingerprint.Of(c);
            var neutral = CampaignDiplomacy.ReadOrNeutral(c, target.Id, player.Id);
            Check(neutral.A == Math.Min(player.Id, target.Id) && neutral.B == Math.Max(player.Id, target.Id)
                && neutral.LastActionDay == -30 && neutral.AgreementRefusedDay == -9999,
                "unrecorded baseline preserves ordered ids and real cooldown defaults: " + seed);
            Check(CampaignDiplomacy.ReadOrNeutral(c, -1, player.Id) is null
                && CampaignDiplomacy.ReadOrNeutral(c, player.Id, player.Id) is null,
                "invalid and self pairs remain invalid: " + seed);

            var queries = new (string Name, Func<string> Read)[]
            {
                ("trade eligibility", () => AgreementSystem.CanPropose(c, player, target, AgreementKind.TradeAgreement, out var why, preview: true) + why),
                ("pact eligibility", () => AgreementSystem.CanPropose(c, player, target, AgreementKind.NonAggressionPact, out var why, preview: true) + why),
                ("trade willingness", () => Number(AgreementSystem.Willingness(c, target, player, AgreementKind.TradeAgreement, preview: true))),
                ("pact willingness", () => Number(AgreementSystem.Willingness(c, target, player, AgreementKind.NonAggressionPact, preview: true))),
                ("aid amount", () => Number(AgreementSystem.AidAmount(c, player, target, out var why, preview: true)) + why),
                ("alliance eligibility", () => AllianceSystem.CanPropose(c, player, target, out var why, preview: true) + why),
                ("alliance willingness", () => Number(AllianceSystem.Willingness(c, target, player, refreshAssessment: false))),
                ("war parts preview", () => {
                    var (w, p, vp, pp) = AiStatecraft.JudgeWarParts(c, target, player, refreshAssessment: false);
                    return Number(w) + "|" + Number(p) + "|" + vp.Count + "|" + pp.Count;
                })
            }.Concat(Enum.GetValues<SubmissionRoute>().SelectMany(route => new (string Name, Func<string> Read)[]
            {
                (route + " refusal", () => SubmissionSystem.Refusal(c, player, target, route, refreshAssessment: false)),
                (route + " willingness", () => Number(SubmissionSystem.Willingness(c, target, player, route, refreshAssessment: false)))
            })).ToArray();

            var answers = queries.Select(query =>
            {
                var answer = query.Read();
                Check(before == CampaignFingerprint.Of(c) && c.Relations.Count == 0
                    && c.Nations.All(n => n.AssessedDay == -1), query.Name + " leaves world and intelligence untouched: " + seed);
                return answer;
            }).ToArray();
            var recorded = CampaignDiplomacy.Get(c, player.Id, target.Id);
            Check(queries.Select(query => query.Read()).SequenceEqual(answers),
                "unrecorded and explicitly recorded neutral pairs give identical answers: " + seed);
            recorded.Stance = DiplomaticStance.AtWar;
            recorded.Opinion = -80;
            before = CampaignFingerprint.Of(c);
            Check(ReferenceEquals(CampaignDiplomacy.ReadOrNeutral(c, target.Id, player.Id), recorded)
                && !AgreementSystem.CanPropose(c, player, target, AgreementKind.TradeAgreement, out _, preview: true)
                && AgreementSystem.AidAmount(c, player, target, out _, preview: true) == 0
                && before == CampaignFingerprint.Of(c), "existing war and sanctions data are read, not replaced: " + seed);
            recorded.Stance = DiplomaticStance.Neutral;
            AllianceSystem.Willingness(c, target, player);
            Check(player.AssessedDay == c.ElapsedDays, "actual planner retains its committed intelligence refresh: " + seed);
        }

        var action = Campaign.CreateBootstrap(20261016);
        var giving = action.NationByTag("POL"); var receiving = action.NationByTag("DEU");
        action.Relations.Clear(); receiving.IsHuman = false;
        action.Defines.TradeAgreementWillingness = -100;
        var command = new ProposeAgreementCommand { IssuerId = giving.Id, TargetId = receiving.Id, Kind = AgreementKind.TradeAgreement };
        command.Apply(action);
        Check(command.Result == AgreementResult.Signed
            && CampaignDiplomacy.Peek(action, giving.Id, receiving.Id) is { TradeAgreement: true },
            "an actual agreement command still records and signs the relation");

        CompleteAllianceFactors();
        PurposefulAllianceOffers();
        IgnoredAllianceCooldown();
    }

    private static Campaign AllianceWorld(float askingGdp = 100, float askedGdp = 100, float opinion = 80)
    {
        var c = new Campaign(81763, new Defines());
        c.Defines.IntelEstimateError = 0;
        c.Defines.AllianceIdeologyWeight = 0;
        c.Defines.AllianceIdeologyBonusCap = 0;
        c.Defines.SocialAllianceWeight = 0;
        c.Defines.AllianceDistancePenalty = 0;
        c.Defines.AiGreatPowers = 0;
        c.Defines.AiAllianceThreshold = 0;
        c.Defines.DiplomaticMissionsEnabled = false;
        c.Defines.DiplomaticActWillingness = float.MaxValue;
        c.AddNation(new Nation { Tag = "AP_ASK", MapIndex = 101, Population = 1_000_000,
            Gdp = askingGdp, Stability = .5f, Treasury = 0 });
        c.AddNation(new Nation { Tag = "AP_GET", MapIndex = 102, Population = 1_000_000,
            Gdp = askedGdp, Stability = .5f, Treasury = 0 });
        var relation = CampaignDiplomacy.Get(c, 0, 1);
        relation.Stance = DiplomaticStance.Friendly;
        relation.Opinion = opinion;
        relation.TradeAgreement = relation.NonAggressionPact = true;
        return c;
    }

    private static void CompleteAllianceFactors()
    {
        foreach (var imageValue in new[] { -.005f, .005f })
        {
            var c = AllianceWorld(); var asking = c.Nations[0]; var asked = c.Nations[1];
            c.Defines.SocialAllianceWeight = .1f;
            SocialMemory.Image(asked, "nation:" + asking.Tag, create: true).Esteem = imageValue;
            var before = CampaignFingerprint.Of(c); var random = c.Random.State;
            var why = new Breakdown();
            var score = AllianceSystem.Willingness(c, asked, asking, why, refreshAssessment: false);
            Check(MathF.Abs(score - why.Total) < .0000001f &&
                MathF.Abs(score - why.Entries.Sum(e => e.Value)) < .0000001f &&
                why.Entries.Any(e => MathF.Abs(e.Value - imageValue * .1f) < .0000001f),
                "every signed alliance factor, including a tiny social term, sums to the actual score: " + imageValue);
            _ = AllianceSystem.Willingness(c, asked, asking, why, refreshAssessment: false);
            Check(CampaignFingerprint.Of(c) == before && c.Random.State == random && c.Nations.All(n => n.AssessedDay < 0),
                "complete factor previews do not change campaign state, randomness or assessments");
        }
        foreach (var opinion in new[] { 50f, 51f })
        {
            var c = AllianceWorld(opinion: opinion);
            var score = AllianceSystem.Willingness(c, c.Nations[1], c.Nations[0], refreshAssessment: false);
            var signed = AllianceSystem.Propose(c, c.Nations[0], c.Nations[1], out _);
            Check(signed == (score > c.Defines.AllianceWillingness) && signed == (opinion > 50),
                "real alliance acceptance uses the preview's strict threshold: " + opinion);
        }
    }

    private static void PurposefulAllianceOffers()
    {
        var covered = AllianceWorld();
        var org = InternationalOrganizations.Seed(covered, "Existing defence", "collective_defence", new[] { 0, 1 });
        Check(org is not null && InternationalOrganizations.DefencePartners(covered, 0).Contains(1),
            "fixture already supplies collective defence without a bilateral alliance");
        var coveredRelation = CampaignDiplomacy.Peek(covered, 0, 1);
        var coveredBefore = CampaignFingerprint.Of(covered); var coveredRandom = covered.Random.State;
        Check(!AiStatecraft.TryFriendlyMove(covered, covered.Nations[0], covered.Nations[1]) &&
            coveredRelation.Stance != DiplomaticStance.Allied && coveredRelation.AllianceOfferedDay < 0 &&
            CampaignFingerprint.Of(covered) == coveredBefore && covered.Random.State == coveredRandom,
            "AI does not duplicate collective defence with another bilateral commitment");

        foreach (var strongerAsker in new[] { true, false })
        {
            var c = AllianceWorld(strongerAsker ? 1000 : 100, strongerAsker ? 100 : 1000, 55);
            var asker = c.Nations[0]; var target = c.Nations[1]; var relation = CampaignDiplomacy.Peek(c, 0, 1);
            var own = AllianceSystem.Willingness(c, asker, target, refreshAssessment: false);
            var theirs = AllianceSystem.Willingness(c, target, asker, refreshAssessment: false);
            Check((own > c.Defines.AllianceWillingness) != (theirs > c.Defines.AllianceWillingness),
                "fixture isolates a one-sided alliance benefit: stronger asker=" + strongerAsker);
            var before = CampaignFingerprint.Of(c); var random = c.Random.State;
            Check(!AiStatecraft.TryFriendlyMove(c, asker, target) && relation.Stance != DiplomaticStance.Allied &&
                relation.AllianceOfferedDay < 0 && relation.AllianceRefusedDay < 0 &&
                CampaignFingerprint.Of(c) == before && c.Random.State == random && c.Nations.All(n => n.AssessedDay < 0),
                "AI skips one-sided offers without inventing a refusal or refreshing assessments: stronger asker=" + strongerAsker);
        }

        var shared = AllianceWorld(opinion: 40); shared.Defines.AiGreatPowers = 1;
        var rival = shared.AddNation(new Nation { Tag = "AP_RIVAL", MapIndex = 103,
            Population = 1_000_000, Gdp = 1000, Stability = .5f });
        foreach (var n in shared.Nations.Take(2)) CampaignDiplomacy.Get(shared, n.Id, rival.Id).Opinion = -60;
        Check(AiStatecraft.TryFriendlyMove(shared, shared.Nations[0], shared.Nations[1]) &&
            CampaignDiplomacy.Peek(shared, 0, 1).Stance == DiplomaticStance.Allied,
            "a shared relevant rival still produces a mutually useful AI alliance");

        var human = AllianceWorld(100, 1000, 55); var humanTarget = human.Nations[1]; humanTarget.IsHuman = true;
        Check(AllianceSystem.Willingness(human, humanTarget, human.Nations[0], refreshAssessment: false) <= human.Defines.AllianceWillingness,
            "human fixture would decline under the automatic acceptance score");
        var humanRelation = CampaignDiplomacy.Peek(human, 0, 1);
        Check(AiStatecraft.TryFriendlyMove(human, human.Nations[0], humanTarget) &&
            humanRelation.Stance != DiplomaticStance.Allied && humanRelation.AllianceOfferedBy == 0 && humanRelation.AllianceOfferedDay == 0,
            "a useful AI proposal reaches the human inbox without forcing their acceptance score");
        Check(AllianceSystem.Answer(human, humanTarget, true, out _) && humanRelation.Stance == DiplomaticStance.Allied,
            "a human can freely accept a proposal their automatic score would reject");
    }

    private static void IgnoredAllianceCooldown()
    {
        static void Day(Campaign c, int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        var c = AllianceWorld(); var asking = c.Nations[0]; var asked = c.Nations[1]; asked.IsHuman = true;
        var relation = CampaignDiplomacy.Peek(c, 0, 1);
        _ = AllianceSystem.Propose(c, asking, asked, out _);
        var offeredDay = relation.AllianceOfferedDay;
        Day(c, offeredDay + c.Defines.AllianceOfferDays);
        Check(!AllianceSystem.CanPropose(c, asking, asked, out _, preview: true), "an offer still pending on its final day cannot be repeated");
        Day(c, offeredDay + c.Defines.AllianceOfferDays + 1);
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        for (var i = 0; i < 3; i++)
            Check(!AllianceSystem.CanPropose(c, asking, asked, out var why, preview: true) && why.Length > 0 &&
                !AllianceSystem.CanPropose(c, asking, asked, out _), "an unanswered expired offer has a stable cooldown: " + i);
        Check(!AllianceSystem.Propose(c, asking, asked, out _) && AllianceSystem.OfferedTo(c, asked) is null &&
            relation.AllianceOfferedDay == offeredDay && relation.AllianceRefusedDay == -9999 &&
            CampaignFingerprint.Of(c) == before && c.Random.State == random,
            "expiry eligibility and blocked resend preserve offer dates, saved state and randomness");
        var retryDay = offeredDay + c.Defines.AllianceOfferDays + c.Defines.AllianceRefusalDays;
        Day(c, retryDay - 1);
        Check(!AllianceSystem.CanPropose(c, asking, asked, out _, preview: true), "ignored-offer cooldown lasts its full duration after expiry");
        Day(c, retryDay);
        Check(AllianceSystem.CanPropose(c, asking, asked, out _, preview: true), "ignored offer can be reconsidered when the cooldown ends");
        _ = AllianceSystem.Propose(c, asking, asked, out _);
        Check(relation.AllianceOfferedDay == retryDay && AllianceSystem.OfferedTo(c, asked) == asking,
            "a later AI offer still uses the ordinary human inbox");

        var refused = AllianceWorld(); var refusing = refused.Nations[1]; refusing.IsHuman = true;
        _ = AllianceSystem.Propose(refused, refused.Nations[0], refusing, out _);
        _ = AllianceSystem.Answer(refused, refusing, false, out _);
        Day(refused, refused.Defines.AllianceRefusalDays - 1);
        Check(!AllianceSystem.CanPropose(refused, refused.Nations[0], refusing, out _, preview: true), "explicit refusal retains its existing cooldown");
        Day(refused, refused.Defines.AllianceRefusalDays);
        Check(AllianceSystem.CanPropose(refused, refused.Nations[0], refusing, out _, preview: true),
            "explicitly refused offers do not also acquire an ignored-offer cooldown");
    }

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
