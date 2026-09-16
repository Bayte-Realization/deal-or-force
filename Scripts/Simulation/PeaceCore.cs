using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum PeaceTerms
{
    Ceasefire,
    WhitePeace,
    WarReparations,
    TradeConcessions,
    DemilitarizedSettlement,
    EnforceWarGoal,
    EnforceDemands,
    Capitulate
}


/// <summary>
/// What one standing settlement is worth and what it takes to get it.
///
/// Everything here is a number or a sentence; what the term actually does when it is signed
/// is <see cref="PeaceNegotiation.Resolve"/>, and that stays in code because restoring a
/// control map is an operation rather than a figure.
/// </summary>
public readonly record struct PeaceTermDefinition(
    string Description,
    float Cost,
    float AcceptanceBase,
    float ScoreWeight,
    int MinimumDays,
    float MinimumAcceptance);

/// <summary>
/// The standing terms, out of `common/peace_terms`.
///
/// The eight are fixed - each one is an operation the simulation knows how to carry out, and
/// a ninth invented in a file would have nothing to do - but every number and every sentence
/// about them is data. A balance mod that thinks a white peace should be free, or that
/// nobody should be able to propose anything in the first year of a war, writes a file.
/// </summary>
public static class PeaceTermCatalog
{
    private static readonly Dictionary<PeaceTerms, PeaceTermDefinition> Definitions = new();

    private static readonly Dictionary<PeaceTerms, PeaceTermDefinition> Defaults = new()
    {
        [PeaceTerms.Ceasefire] = new(
            "A one-year armistice; all forces return to the pre-war control map.",
            0f, 62f, -0.45f, 30, 50f),
        [PeaceTerms.WhitePeace] = new(
            "Pre-war borders and a five-year truce, with no victor or indemnity.",
            5f, 50f, -0.65f, 120, 50f),
        [PeaceTerms.WarReparations] = new(
            "Pre-war borders plus an immediate economy-scaled indemnity.",
            24f, 14f, 0.90f, 60, 50f),
        [PeaceTerms.TradeConcessions] = new(
            "Pre-war borders, an indemnity and a compelled bilateral trade agreement.",
            30f, 8f, 0.98f, 90, 50f),
        [PeaceTerms.DemilitarizedSettlement] = new(
            "Pre-war borders while the defeated armed forces surrender equipment and cohesion.",
            46f, 0f, 1.04f, 0, 50f),
        [PeaceTerms.EnforceWarGoal] = new(
            "Keep secured provinces inside the declared strategic objective.",
            55f, 6f, 1.08f, 0, 50f),
        [PeaceTerms.EnforceDemands] = new(
            "Keep every secured occupation, evacuating exposed advances.",
            78f, -4f, 1.22f, 0, 50f),
        [PeaceTerms.Capitulate] = new(
            "Unconditional surrender under the current occupation map.",
            100f, 100f, 0f, 0, 0f)
    };

    static PeaceTermCatalog() => ResetCatalog();

    public static IEnumerable<PeaceTerms> All => Defaults.Keys;

    public static PeaceTermDefinition Of(PeaceTerms terms) =>
        Definitions.TryGetValue(terms, out var found) ? found : Defaults[terms];

    public static void ResetCatalog(List<string> errors = null)
    {
        Definitions.Clear();
        foreach (var entry in Defaults) Definitions[entry.Key] = entry.Value;
        foreach (var document in GameDataLoader.DataDocuments("common", "peace_terms"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("terms", out var terms) && terms.ValueKind == JsonValueKind.Object)
            root = terms;

        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!Enum.TryParse<PeaceTerms>(entry.Name, true, out var kind))
            {
                errors?.Add($"common/peace_terms: '{entry.Name}' is not one of the standing terms. " +
                            $"They are: {string.Join(", ", Defaults.Keys)}.");
                continue;
            }

            var current = Of(kind);
            Definitions[kind] = current with
            {
                Description = Text(entry.Value, "description") ?? current.Description,
                Cost = Number(entry.Value, "cost") ?? current.Cost,
                AcceptanceBase = Number(entry.Value, "acceptance_base") ?? current.AcceptanceBase,
                ScoreWeight = Number(entry.Value, "war_score_weight") ?? current.ScoreWeight,
                MinimumDays = Whole(entry.Value, "minimum_days") ?? current.MinimumDays,
                MinimumAcceptance = Number(entry.Value, "minimum_acceptance") ?? current.MinimumAcceptance
            };
        }
    }

    private static string Text(JsonElement value, string key) =>
        value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() : null;

    private static float? Number(JsonElement value, string key) =>
        value.TryGetProperty(key, out var element) && element.TryGetSingle(out var number) ? number : null;

    private static int? Whole(JsonElement value, string key) =>
        value.TryGetProperty(key, out var element) && element.TryGetInt32(out var number) ? number : null;
}

public static class PeaceNegotiation
{
    public static float DemandCost(PeaceTerms terms) => PeaceTermCatalog.Of(terms).Cost;

    public static string Description(PeaceTerms terms) => Loc.Get(PeaceTermCatalog.Of(terms).Description);

    public static float Acceptance(Campaign campaign, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        if (war is null || war.Ended || !war.Involves(proposerId)) return 0f;
        if (terms == PeaceTerms.Capitulate) return 100f;

        // Which side is proposing, not which country started it: in a coalition war an ally
        // proposing terms is proposing them for its whole side.
        var attacking = war.IsAttacking(proposerId);
        var proposerScore = attacking ? war.WarScore : -war.WarScore;
        var responderSupport = attacking ? war.DefenderWarSupport : war.AttackerWarSupport;
        var duration = campaign.ElapsedDays - war.StartedDay;
        var defines = campaign.Defines;
        var exhaustion = (1f - responderSupport) * defines.PeaceExhaustionWeight;
        var timePressure = MathF.Min(defines.PeaceTimePressureCap,
            duration / MathF.Max(1f, defines.PeaceTimePressureDays));

        var definition = PeaceTermCatalog.Of(terms);
        return M.Clamp(definition.AcceptanceBase + proposerScore * definition.ScoreWeight +
                       exhaustion + timePressure, 0f, 100f);
    }

    public static string BlockReason(Campaign campaign, StrategicWar war, int nationId, PeaceTerms terms)
    {
        if (war is null || war.Ended || !war.Involves(nationId)) return Loc.Get("No active war");

        // The same two gates for every term, with the numbers coming off the term rather
        // than out of a switch that stated each pair twice.
        var definition = PeaceTermCatalog.Of(terms);
        if (terms == PeaceTerms.EnforceWarGoal && war.ClaimedStateMapIndex < 0)
            return Loc.Get("This war has no territorial objective");

        var fought = campaign.ElapsedDays - war.StartedDay;
        if (definition.MinimumDays > 0 && fought < definition.MinimumDays)
            return Loc.Format("The war has not lasted {0} days", definition.MinimumDays);

        var acceptance = Acceptance(campaign, war, nationId, terms);
        if (definition.MinimumAcceptance > 0f && acceptance < definition.MinimumAcceptance)
            return Loc.Format("Opponent acceptance {0}% (requires {1}%)",
                acceptance.ToString("0"), definition.MinimumAcceptance.ToString("0"));
        return "";
    }

    public static void Resolve(Campaign campaign, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        var block = BlockReason(campaign, war, proposerId, terms);
        war.LastNegotiation = block.Length == 0 ? "Agreement reached" : block;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        if (block.Length > 0) return;

        if ((terms == PeaceTerms.Ceasefire || terms == PeaceTerms.WhitePeace || terms == PeaceTerms.WarReparations ||
             terms == PeaceTerms.TradeConcessions || terms == PeaceTerms.DemilitarizedSettlement) &&
            campaign.ProvinceController is not null)
            foreach (var pair in war.OriginalController)
                campaign.SetProvinceController(pair.Key, pair.Value);

        if (terms is PeaceTerms.WarReparations or PeaceTerms.TradeConcessions)
        {
            var payer = campaign.NationById(proposerId == war.AttackerId ? war.DefenderId : war.AttackerId);
            var receiver = campaign.NationById(proposerId);
            if (payer is not null && receiver is not null)
            {
                var share = terms == PeaceTerms.TradeConcessions ? 0.015f : 0.03f;
                var payment = MathF.Min(MathF.Max(0f, payer.Treasury), MathF.Max(0.10f, payer.Gdp * share));
                payer.Treasury -= payment;
                receiver.Treasury += payment;
                war.Reparations = payment;
            }
        }

        if (terms == PeaceTerms.TradeConcessions)
        {
            var tradeRelation = CampaignDiplomacy.Get(campaign, war.AttackerId, war.DefenderId);
            tradeRelation.TradeAgreement = true;
            OpinionSystem.Remember(campaign, tradeRelation, "trade_agreement");
        }

        if (terms == PeaceTerms.DemilitarizedSettlement)
        {
            var winnerId = proposerId;
            var loserId = proposerId == war.AttackerId ? war.DefenderId : war.AttackerId;
            foreach (var division in campaign.Armies.Where(division => division.NationId == loserId))
            {
                division.Equipment = MathF.Min(division.Equipment, 0.45f);
                division.Cohesion = MathF.Min(division.Cohesion, 0.55f);
                division.Strength = MathF.Min(division.Strength, 0.72f);
            }
            var winner = campaign.NationById(winnerId);
            if (winner is not null) winner.WarSupport = MathF.Min(1f, winner.WarSupport + 0.04f);
        }

        // A peace that keeps ground has to keep it properly. Holding a province used to
        // mean occupying it forever while the loser's economy went on counting everyone in
        // it; kept provinces now change owner, and any region the new border runs through
        // is split into the regions the border actually left behind.
        if (terms is PeaceTerms.EnforceWarGoal or PeaceTerms.EnforceDemands or PeaceTerms.Capitulate &&
            campaign.ProvinceController is not null && campaign.ProvinceOccupation is not null)
        {
            var winnerId = terms == PeaceTerms.Capitulate
                ? (proposerId == war.AttackerId ? war.DefenderId : war.AttackerId)
                : proposerId;
            var winner = campaign.NationById(winnerId);
            var kept = new List<int>();

            foreach (var pair in war.OriginalController)
            {
                var secured = campaign.ProvinceOccupation[pair.Key] >= campaign.Defines.OccupationSecureThreshold;
                var inGoal = terms != PeaceTerms.EnforceWarGoal ||
                             campaign.ProvinceMap?.ProvinceToState[pair.Key] == war.ClaimedStateMapIndex;
                var held = campaign.ProvinceController[pair.Key] == (ushort)(winner?.MapIndex ?? -1);
                if (secured && inGoal && held) kept.Add(pair.Key);
                else campaign.SetProvinceController(pair.Key, pair.Value);
            }

            if (winner is not null && kept.Count > 0)
            {
                // Rounded to regions before it is applied. These terms take whatever the
                // front happened to have secured, which is a line drawn by two armies and
                // not a border anybody would draw on purpose - and every region it cuts in
                // half becomes a new region that every later war starts from.
                var tidied = PeaceConference.Tidy(campaign, war, kept, winner.MapIndex);
                foreach (var province in kept)
                    if (!tidied.Contains(province) && war.OriginalController.TryGetValue(province, out var was))
                        campaign.SetProvinceController(province, was);
                kept = tidied;
                var created = StatePartition.Annex(campaign, kept, winner);
                war.AnnexedProvinces = kept.Count;
                war.CreatedStates = created.Count;
            }
        }

        war.Ended = true;
        war.EndedDay = campaign.ElapsedDays;
        var sideScore = war.IsAttacking(proposerId) ? war.WarScore : -war.WarScore;
        war.WinnerId = terms == PeaceTerms.Capitulate
            ? war.EnemyLeaderOf(proposerId)
            : sideScore > 0f ? proposerId : -1;
        war.PeaceSummary = terms switch
        {
            PeaceTerms.Ceasefire => "A one-year ceasefire restored the pre-war control map.",
            PeaceTerms.WhitePeace => "White peace restored the pre-war control map.",
            PeaceTerms.WarReparations => $"Pre-war borders were restored and ${war.Reparations:0.00}B in reparations changed hands.",
            PeaceTerms.TradeConcessions => $"Pre-war borders were restored; a trade agreement and ${war.Reparations:0.00}B concession were imposed.",
            PeaceTerms.DemilitarizedSettlement => "Pre-war borders were restored and the defeated armed forces were partially demobilised.",
            PeaceTerms.EnforceWarGoal => "Secured provinces inside the declared war goal were retained; all other advances were evacuated.",
            PeaceTerms.EnforceDemands => "Secured occupations were retained; unsecured advances were evacuated.",
            _ => $"{campaign.NationById(proposerId)?.Name ?? "A belligerent"} capitulated."
        };
        war.AttackerFront.Clear();
        war.DefenderFront.Clear();
        war.UnopposedProgress.Clear();
        // Everybody who was fighting everybody. A coalition war ends for all of it at
        // once - that is what makes it one war rather than a folder of them - so every pair
        // across the two sides gets the truce, not just the two that started it.
        var truceUntil = campaign.ElapsedDays + (terms == PeaceTerms.Ceasefire ? 365 : 365 * 5);
        foreach (var attackingId in war.Side(true))
            foreach (var defendingId in war.Side(false))
            {
                if (attackingId == defendingId) continue;
                var relation = CampaignDiplomacy.Get(campaign, attackingId, defendingId);
                relation.Stance = DiplomaticStance.Truce;
                relation.TruceUntil = truceUntil;
                relation.LastActionDay = campaign.ElapsedDays;
                relation.Opinion = Math.Min(relation.Opinion, -35f);
            }
        foreach (var army in campaign.Armies.Where(army => army.AssignedWarId == war.Id))
        {
            army.AssignedWarId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0f;
        }
        foreach (var battle in campaign.Battles.Where(battle => battle.WarId == war.Id && !battle.Ended))
        {
            battle.Ended = true;
            battle.Outcome = "Peace agreement";
        }
        campaign.MarkMapChanged();
    }
}

public sealed class ProposePeaceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId { get; init; }
    public PeaceTerms Terms { get; init; }
    public void Apply(Campaign campaign)
    {
        var war = campaign.Wars.FirstOrDefault(candidate => candidate.Id == WarId);
        if (war is not null) PeaceNegotiation.Resolve(campaign, war, IssuerId, Terms);
    }
}
