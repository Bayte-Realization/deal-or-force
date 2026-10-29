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
    Capitulate,
    // Last, so every term a save already holds keeps its number.
    StatusQuo
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
            "An armistice on the line: troops stay in the regions they hold, no border moves - a frozen conflict.",
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
            100f, 100f, 0f, 0, 0f),
        [PeaceTerms.StatusQuo] = new(
            "Each side keeps the enemy ground it holds, drawn into a border, and a five-year truce: the peace of a war nobody is winning.",
            12f, 50f, -0.35f, 365, 50f)
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
        if (terms == PeaceTerms.WhitePeace && WarWeariness.NoContactPeaceReady(campaign, war)) return 100f;

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
        // A civil war does not tire the way a war between states does, and the side that is
        // winning it does not settle for the line: every civil war of 2024 was ending in a white
        // peace around its nine-hundredth day, the junta at -40 included, because two years of
        // time pressure buy any compromise. The winner's resolve grows with its lead.
        var winnerResolve = 0f;
        if (war.CivilConflict)
        {
            timePressure *= defines.CivilWarTimePressureShare;
            // Only against terms that hand ground back: conceding the line is not.
            if (proposerScore < 0f && terms is not (PeaceTerms.Capitulate or PeaceTerms.StatusQuo))
                winnerResolve = -proposerScore * defines.CivilWarWinnerResolve;
        }
        // And the weariness of a long war, on both sides of it (WarWeariness) - less, for a state
        // winning back its own breakaway, whatever it is offered short of the ground (Reintegration).
        return M.Clamp(definition.AcceptanceBase + proposerScore * definition.ScoreWeight - winnerResolve +
                       exhaustion + timePressure + WarWeariness.Weariness(campaign, war) -
                       Reintegration.CompromisePenalty(campaign, war, proposerId, terms) -
                       Escalation.SunkCost(campaign, war) - WarAimResolve(campaign, war, proposerId, terms), 0f, 100f);
    }

    private static float WarAimResolve(Campaign campaign, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        // Only a compromise proposed by the LOSING side asks the winning AI to give up
        // an achievable aim. A human still decides explicitly; capitulation is never blocked.
        if (terms is not (PeaceTerms.Ceasefire or PeaceTerms.WhitePeace or PeaceTerms.StatusQuo)) return 0;
        var score = war.IsAttacking(proposerId) ? war.WarScore : -war.WarScore;
        var responder = campaign.NationById(war.EnemyLeaderOf(proposerId));
        if (score >= 0 || responder is null || responder.IsHuman ||
            !AiStatecraft.ShouldPressWarAim(campaign, responder, war)) return 0;
        var penalty = campaign.Defines.AiWarAimResolvePenalty;
        if (!float.IsFinite(penalty)) penalty = 70f;
        penalty = Math.Clamp(penalty, 0f, 100f);

        var duration = campaign.ElapsedDays - war.StartedDay;
        var stalemateDiscount = 0f;
        if (duration > 365)
        {
            var years = (duration - 365f) / 365f;
            stalemateDiscount = terms == PeaceTerms.StatusQuo ? MathF.Min(0.85f, years * 0.35f) : MathF.Min(0.60f, years * 0.20f);
        }
        if (war.Stalled)
            stalemateDiscount = MathF.Max(stalemateDiscount, terms == PeaceTerms.StatusQuo ? 0.65f : 0.40f);

        var wearinessDiscount = WarWeariness.Weariness(campaign, war) / 100f * 0.5f;
        stalemateDiscount = MathF.Min(0.90f, stalemateDiscount + wearinessDiscount);

        if (terms == PeaceTerms.StatusQuo)
        {
            var responderOccupied = war.IsAttacking(responder.Id) ? war.DefenderOccupiedValue : war.AttackerOccupiedValue;
            if (responderOccupied > 0.02f)
                stalemateDiscount = MathF.Max(stalemateDiscount, 0.50f + MathF.Min(0.40f, responderOccupied));
        }

        return MathF.Max(0f, penalty * (1f - stalemateDiscount));
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

    /// <summary>
    /// Whether a province belongs to somebody this country fought in this war - the only
    /// ground a peace can hand it.
    ///
    /// A treaty that lets each side keep what it holds let it keep whatever it held,
    /// including its own allies' land: a province retaken from the enemy by a friend's army
    /// was that friend's to keep. That is how Brazil came out of a war fought beside Austria
    /// owning Lower Austria. What a country holds of a friend's goes back to the friend.
    /// </summary>
    internal static bool EnemyGround(Campaign campaign, StrategicWar war, int province, int nationId)
    {
        var owner = NavalOperations.NationAt(campaign, campaign.ProvinceMap.ProvinceToNation[province]);
        return owner is not null && war.Opposes(nationId, owner.Id);
    }

    public static void Resolve(Campaign campaign, StrategicWar war, int proposerId, PeaceTerms terms)
    {
        var block = BlockReason(campaign, war, proposerId, terms);
        war.LastNegotiation = block.Length == 0 ? "Agreement reached" : block;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        if (block.Length > 0) return;

        // A ceasefire is an armistice on the line - Korea 1953, Karabakh 1994, Minsk: the troops
        // stay where they stand and no border moves. It used to hand every advance back, so a
        // winning Russia signed away all it held because a ceasefire was easy to accept. What a
        // side holds region by region stays held as a frozen conflict (LandArrangementSystem);
        // the scraps of advances go back.
        var armistice = terms == PeaceTerms.Ceasefire && campaign.FeatureEnabled("land_arrangements")
            ? LandArrangementSystem.FreezeLine(campaign, war) : null;
        if ((terms == PeaceTerms.Ceasefire || terms == PeaceTerms.WhitePeace || terms == PeaceTerms.WarReparations ||
             terms == PeaceTerms.TradeConcessions || terms == PeaceTerms.DemilitarizedSettlement) &&
            campaign.ProvinceController is not null)
            foreach (var pair in war.OriginalController)
                if (armistice is null || !armistice.Contains(campaign.ProvinceMap.ProvinceToState[pair.Key]))
                    campaign.SetProvinceController(pair.Key, pair.Value);

        if (terms is PeaceTerms.WarReparations or PeaceTerms.TradeConcessions)
        {
            var payer = campaign.NationById(war.EnemyLeaderOf(proposerId));
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
            var tradeRelation = CampaignDiplomacy.Get(campaign, proposerId, war.EnemyLeaderOf(proposerId));
            tradeRelation.TradeAgreement = true;
            OpinionSystem.Remember(campaign, tradeRelation, "trade_agreement");
        }

        if (terms == PeaceTerms.DemilitarizedSettlement)
        {
            var winnerId = proposerId;
            var loserId = war.EnemyLeaderOf(proposerId);
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
                ? war.EnemyLeaderOf(proposerId)
                : proposerId;
            var winner = campaign.NationById(winnerId);
            var kept = new List<int>();

            // Direct breakaways return whole. A successor rebel state may have broken
            // from an intervening polity instead: unconditional surrender still returns
            // the winner's real homeland, not just one secured province. No foreign
            // acquisitions, renounced titles or third-party occupations are invented.
            var beaten = terms == PeaceTerms.Capitulate ? campaign.NationById(proposerId) : null;
            var recovered = Reintegration.CapitulationGround(campaign, war, beaten, winner);
            var recoveredSet = recovered.ToHashSet();
            if (recovered.Count > 0)
            {
                foreach (var province in recovered) campaign.SetProvinceController(province, (ushort)winner.MapIndex);
                var rejoined = StatePartition.Annex(campaign, recovered, winner, asConquest: false);
                war.AnnexedProvinces = recovered.Count;
                war.CreatedStates = rejoined.Count;
            }
            foreach (var pair in war.OriginalController)
            {
                if (recoveredSet.Contains(pair.Key)) continue;
                var secured = campaign.ProvinceOccupation[pair.Key] >= campaign.Defines.OccupationSecureThreshold;
                var inGoal = terms != PeaceTerms.EnforceWarGoal ||
                             campaign.ProvinceMap?.ProvinceToState[pair.Key] == war.ClaimedStateMapIndex;
                var held = campaign.ProvinceController[pair.Key] == (ushort)(winner?.MapIndex ?? -1);
                if (secured && inGoal && held && EnemyGround(campaign, war, pair.Key, winnerId)) kept.Add(pair.Key);
                else campaign.SetProvinceController(pair.Key, pair.Value);
            }

            if (winner is not null && kept.Count > 0)
            {
                // Rounded to regions before it is applied. These terms take whatever the
                // front happened to have secured, which is a line drawn by two armies and
                // not a border anybody would draw on purpose - and every region it cuts in
                // half becomes a new region that every later war starts from.
                var tidied = PeaceConference.Tidy(campaign, war, kept, winner.MapIndex);
                // And settled: joined to the winner, no pocket of the loser cut off (PeaceShape).
                tidied = PeaceShape.SettleProvinces(campaign, war, tidied, winner);
                foreach (var province in kept)
                    if (!tidied.Contains(province) && war.OriginalController.TryGetValue(province, out var was))
                        campaign.SetProvinceController(province, was);
                kept = tidied;
                var created = StatePartition.Annex(campaign, kept, winner);
                war.AnnexedProvinces += kept.Count;
                war.CreatedStates += created.Count;
            }
        }

        // A deadlock: each side keeps the enemy ground it controls. Security used to be the
        // gate, so a finished occupation was handed back except the one region that had
        // crossed the line. The border is still drawn afterwards (PeaceShape): a pocket
        // that does not touch the holder does not become a country.
        if (terms == PeaceTerms.StatusQuo && campaign.ProvinceController is not null && campaign.ProvinceOccupation is not null)
        {
            var heldBy = new Dictionary<int, List<int>>();
            foreach (var pair in war.OriginalController)
            {
                var holder = campaign.Nations.Find(nation => nation.MapIndex == campaign.ProvinceController[pair.Key]);
                var controlled = holder is not null && campaign.ProvinceController[pair.Key] != pair.Value;
                if (!controlled || !war.Involves(holder.Id) || !EnemyGround(campaign, war, pair.Key, holder.Id))
                {
                    campaign.SetProvinceController(pair.Key, pair.Value);
                    continue;
                }
                if (!heldBy.TryGetValue(holder.Id, out var list)) heldBy[holder.Id] = list = new List<int>();
                list.Add(pair.Key);
            }
            var annexed = 0;
            var created = 0;
            foreach (var (holderId, held) in heldBy)
            {
                var holder = campaign.NationById(holderId);
                var tidied = PeaceShape.SettleProvinces(campaign, war, PeaceConference.Tidy(campaign, war, held, holder.MapIndex), holder);
                foreach (var province in held)
                    if (!tidied.Contains(province) && war.OriginalController.TryGetValue(province, out var was))
                        campaign.SetProvinceController(province, was);
                if (tidied.Count == 0) continue;
                created += StatePartition.Annex(campaign, tidied, holder).Count;
                annexed += tidied.Count;
            }
            war.AnnexedProvinces = annexed;
            war.CreatedStates = created;
        }

        war.Ended = true;
        campaign.MarkWarsChanged();
        war.EndedDay = campaign.ElapsedDays;
        war.PeaceProposerId = proposerId;
        var sideScore = war.IsAttacking(proposerId) ? war.WarScore : -war.WarScore;
        war.WinnerId = terms == PeaceTerms.Capitulate
            ? war.EnemyLeaderOf(proposerId)
            : terms is PeaceTerms.WhitePeace or PeaceTerms.Ceasefire ? -1
            : sideScore > 0f ? proposerId : -1;

        Factions.SettleCivilWarSurvivors(campaign, war);
        war.PeaceSummary = terms switch
        {
            PeaceTerms.Ceasefire => "An armistice froze the line: each side keeps the regions it holds, and no border moves.",
            PeaceTerms.WhitePeace => "White peace restored the pre-war control map.",
            PeaceTerms.WarReparations => $"Pre-war borders were restored and ${war.Reparations:0.00}B in reparations changed hands.",
            PeaceTerms.TradeConcessions => $"Pre-war borders were restored; a trade agreement and ${war.Reparations:0.00}B concession were imposed.",
            PeaceTerms.DemilitarizedSettlement => "Pre-war borders were restored and the defeated armed forces were partially demobilised.",
            PeaceTerms.EnforceWarGoal => "Secured provinces inside the declared war goal were retained; all other advances were evacuated.",
            PeaceTerms.EnforceDemands => "Secured occupations were retained; unsecured advances were evacuated.",
            PeaceTerms.StatusQuo => Loc.Get("A deadlock: each side kept the ground it held, and the line became the border."),
            _ => $"{campaign.NationById(proposerId)?.Name ?? "A belligerent"} capitulated."
        };
        war.AttackerFront.Clear();
        war.DefenderFront.Clear();
        war.UnopposedProgress.Clear();
        // What the war leaves between them: the grievance of the side that lost (Escalation).
        Escalation.Settle(campaign, war);
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
            Warfare.EndBattleWithoutVictory(campaign, battle, "Peace agreement");
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
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var war = campaign.Wars.FirstOrDefault(candidate => candidate.Id == WarId);
        if (war is not null) PeaceNegotiation.Resolve(campaign, war, IssuerId, Terms);
    }
}
