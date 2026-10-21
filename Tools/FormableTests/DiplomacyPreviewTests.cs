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
    }

    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}
