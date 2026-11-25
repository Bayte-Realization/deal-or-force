using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Defines governing AI peace negotiation thresholds and decisive war settlement logic.
/// </summary>
public sealed partial class Defines
{
    /// <summary>War score threshold for an aggressor to enforce decisive total peace terms.</summary>
    public float AiPeaceDecisiveScore { get; set; } = 75f;

    /// <summary>Fraction of enemy national territory that must be secured for decisive annexation.</summary>
    public float AiPeaceDecisiveOccupationShare { get; set; } = .65f;

    /// <summary>
    /// How long a war score may sit at a decisive level over ground the winner holds before
    /// the score itself is the verdict. Measured wars (WAR_CALIBRATION_2026_10_26) ran for
    /// years at a score of a hundred without the occupation map ever converting to "secure",
    /// and a finished war that never ends is not a war any more: it is a bug with casualties.
    /// </summary>
    public int AiPeaceDecisivePatienceDays { get; set; } = 540;

    /// <summary>
    /// The score at which a war the aggressor is plainly losing becomes the defender's
    /// victory: liberation of its own ground and reparations, never annexation. The
    /// attacker's own decisive bar stays where it is; this is the defender's, and it is
    /// lower because a repelled invasion is a verdict the whole world has already watched.
    /// </summary>
    public float AiPeaceRepelledScore { get; set; } = 35f;

    /// <summary>
    /// Below this share of the beaten country's people being the victor's own (by people, or
    /// living in regions it holds title to), a decisive victory ends in a partition rather than
    /// an annexation: the victor takes what it claims and leaves the rest a dependent state.
    /// 0 turns it off.
    /// </summary>
    public float AiPartitionKinShare { get; set; } = 0.35f;
}

/// <summary>
/// AI Statecraft peace outcomes, decisive settlements, and truce length calculations.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// PEACE DECISION PIPELINE & 33/33/33 CALIBRATION:
/// ═══════════════════════════════════════════════════════════════════════════════
///
///                       [ War Under Review ]
///                                │
///           ┌────────────────────┴────────────────────┐
///           ▼                                         ▼
///    [ Civil / Reclaim ]                       [ Inter-State War ]
///    Reintegration into one                           │
///                                    ┌────────────────┴────────────────┐
///                                    ▼                                 ▼
///                            [ Nuclear / DEFCON<=2 ]           [ Conventional War ]
///                            Limited Peace / Cuba Rule                 │
///                            (No Homeland Annexation)   ┌──────────────┴──────────────┐
///                                                       ▼                             ▼
///                                              [ Repelled Defense ]          [ Decisive Offensive ]
///                                              Score <= -35                  Score >= 75 or 540 Days
///                                              (Defender wins status quo     (Annexation / Subjugation /
///                                               + enemy pays reparations)     dynastic union)
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// ДЛЯ МОДДЕРОВ (FOR MODDERS) — Логика завершения войн в Deal or Force:
/// ═══════════════════════════════════════════════════════════════════════════════
/// В отличие от механик Paradox, где мирные конференции часто приводят к нереалистичным
/// «лоскутным» аннексиям или бесконечным войнам с застрявшим счётом 100%, здесь действует
/// иерархия из 5 реалистичных сценариев завершения войны, откалиброванная по правилу 33/33/33:
/// (~33% решительных побед агрессора, ~33% отражённой агрессии обороны, ~33% компромиссов):
///
/// 1. ВОССТАНОВЛЕНИЕ ГОСУДАРСТВА / ГРАЖДАНСКАЯ ВОЙНА (Reclaiming / Civil War):
///    Если сторона подавляет мятеж или претендует на верховную власть в расколе, победа
///    целиком собирает страну воедино (Unconditional = true), как только занята столица
///    или две трети национальной территории.
///
/// 2. ВОССОЕДИНЕНИЕ (Reunification):
///    Войны национального объединения (общий народ, родина, повод reconquest/unification)
///    завершаются полным воссоединением без дробления на мелкие провинции.
///
/// 3. КУБИНСКОЕ ПРАВИЛО / ЯДЕРНОЕ СДЕРЖИВАНИЕ (The Cuba Rule / DEFCON <= 2):
///    Державы с ядерным оружием (Deterrence.TierOf >= 1) не могут аннексировать
///    территорию друг друга. Порог победы снижается пропорционально фактору грани фола
///    (brink), а условия мира ограничиваются освобождением оккупированных земель и
///    репарациями. При DEFCON 2 и 1 это правило распространяется и на межгосударственные
///    конфликты с участием одной ядерной державы, но не на все войны мира.
///
/// 4. ОТРАЖЁННАЯ АГРЕССИЯ (Repelled Aggression):
///    Если обороняющийся успешно отразил вторжение и ведёт по очкам (AiPeaceRepelledScore),
///    война завершается его победой: агрессор выплачивает репарации и уходит со всех
///    захваченных земель. Обороняющийся не претендует на исконные земли агрессора.
///
/// 5. РЕШИТЕЛЬНАЯ ПОБЕДА / ДОЛГОЕ ТЕРПЕНИЕ / КЛИЕНТСКОЕ ГОСУДАРСТВО:
///    Если победитель удерживает 65%+ территории врага или война затянулась с подавляющим
///    счётом дольше AiPeaceDecisivePatienceDays (540 дней), враг капитулирует. Если
///    полная аннексия слишком дорога по бюджету очков, создаётся марионеточное государство
///    или династическая уния (при dynastic_claim).
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static partial class AiStatecraft
{
    /// <summary>
    /// Evaluates whether a nation should request a prolonged truce decade instead of an ordinary truce.
    /// Returns true only when exhaustion outweighs unfinished war claims or when all strategic goals were achieved.
    /// </summary>
    [ThreadStatic] private static HashSet<int> _aimEnemiesScratch;
    [ThreadStatic] private static HashSet<int> _aimUnresolvedScratch;
    [ThreadStatic] private static List<int> _decisiveTerritoryScratch;
    [ThreadStatic] private static HashSet<int> _partitionClaimedScratch;

    internal static bool WantsLongTruce(Campaign campaign, Nation nation, StrategicWar war, PeaceDemand demand)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || nation is null || war is null || demand is null) return true;
        // Exhaustion can outweigh an unfinished claim. The ordinary truce still applies
        // in every other case; declining the paid extension never permits an immediate war.
        if (nation.Stability < campaign.Defines.AiWarStabilityFloor ||
            nation.WarSupport < campaign.Defines.AiWarSupportFloor) return true;
        var enemies = _aimEnemiesScratch ??= new HashSet<int>();
        enemies.Clear();
        foreach (var id in war.EnemiesOf(nation.Id))
        {
            var n = campaign.NationById(id);
            if (n is not null) enemies.Add(n.MapIndex);
        }
        var subject = demand.Subjugate.Length > 0 ? campaign.NationById(war.EnemyLeaderOf(nation.Id))?.MapIndex ?? 0 : 0;
        var unresolved = _aimUnresolvedScratch ??= new HashSet<int>();
        unresolved.Clear();
        var states = campaign.States;
        for (var i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (enemies.Contains(s.NationMapIndex) && s.NationMapIndex != subject &&
                StrategicWarAim(campaign, nation, war, s))
                unresolved.Add(s.MapIndex);
        }
        if (unresolved.Count == 0) return true;
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (!map.IsWater(p) && enemies.Contains(map.ProvinceToNation[p]) && map.ProvinceToNation[p] != subject &&
                unresolved.Contains(map.ProvinceToState[p]) && !demand.Provinces.Contains(p)) return false;
        return true;
    }
    /// <summary>
    /// A decisive victory over a country that is not the victor's own ends the way such wars end
    /// in the world this game draws: the victor takes what it holds title to or whose people are
    /// its own, and the rest stays a state - under a government that answers to it.
    ///
    /// The old settlement annexed the whole country whenever the score could pay for it, so a
    /// won war swallowed a foreign people of thirty million into the map like a province; the
    /// overstretch and the risings came after. Nothing here names a country: the line is the
    /// share of the beaten people who are the victor's (AiPartitionKinShare). A beaten country of
    /// another ideology becomes a satellite (its regime is changed); one of the same, a
    /// protectorate. Returns false where annexation is the plausible end.
    /// </summary>
    private static SubjectType PickSubjectKind(Campaign campaign, Nation winner, Nation loser, float? maxCost = null)
    {
        var preferredId = loser.Ideology == winner.Ideology ? "protectorate" : "satellite";
        SubjectType preferred = null;
        SubjectType fallback = null;
        foreach (var k in SubjectCatalog.Imposable)
        {
            if (maxCost.HasValue && PeaceConference.SubjugationCost(campaign, winner, loser, k.Id) > maxCost.Value)
                continue;
            if (k.Id == preferredId) preferred = k;
            if (fallback is null || k.DemandCost > fallback.DemandCost ||
                (k.DemandCost == fallback.DemandCost && string.CompareOrdinal(k.Id, fallback.Id) < 0))
                fallback = k;
        }
        return preferred ?? fallback;
    }

    private static bool Partition(Campaign campaign, Nation winner, Nation loser, List<int> territory,
        PeaceDemand demand, PeaceLedger ledger)
    {
        var threshold = campaign.Defines.AiPartitionKinShare;
        if (threshold <= 0f || loser.IsSubject || loser.Movement is not null) return false;
        var map = campaign.ProvinceMap;
        long people = 0, ours = 0;
        var claimed = _partitionClaimedScratch ??= new HashSet<int>();
        claimed.Clear();
        var states = campaign.StatesOf(loser);
        for (var si = 0; si < states.Length; si++)
        {
            var region = states[si];
            var title = TerritorialClaims.TitleOf(campaign, winner, region) != TerritorialTitle.None;
            var kin = 1f - EconomySystem.ForeignShare(region, winner.Tag);
            people += region.Population;
            if (title || kin >= 0.5f)
            {
                ours += region.Population;
                claimed.Add(region.MapIndex);
            }
            else ours += (long)(region.Population * kin);
        }
        if (people <= 0 || ours / (double)people >= threshold) return false;
        demand.Provinces.Clear();
        for (var pi = 0; pi < territory.Count; pi++)
        {
            var province = territory[pi];
            if (claimed.Contains(map.ProvinceToState[province])) demand.Provinces.Add(province);
        }
        demand.Unconditional = false;
        var preferred = PickSubjectKind(campaign, winner, loser);
        if (preferred is not null)
        {
            demand.Subjugate = preferred.Id;
            if (ledger.Cost(demand) <= ledger.Budget) return true;
        }
        SubjectType bestAffordable = null;
        foreach (var k in SubjectCatalog.Imposable)
        {
            demand.Subjugate = k.Id;
            if (ledger.Cost(demand) > ledger.Budget) continue;
            if (bestAffordable is null || k.DemandCost > bestAffordable.DemandCost ||
                (k.DemandCost == bestAffordable.DemandCost && string.CompareOrdinal(k.Id, bestAffordable.Id) < 0))
                bestAffordable = k;
        }
        if (bestAffordable is not null)
        {
            demand.Subjugate = bestAffordable.Id;
            return true;
        }
        demand.Subjugate = "";
        demand.Provinces.Clear();
        return false;
    }

    /// <summary>
    /// Unconditional surrender is not a ceasefire. Forty-seven of eighty-six wars in a ten-year
    /// world ended in a capitulation, and every capitulated country walked away independent, its
    /// government untouched, with nothing on it but the land the victor's army stood on. The
    /// victor of a surrender dictates: a beaten country that is not its own people (the same line
    /// as Partition) and is not too large for the victor to hold (its economy at most twice the
    /// victor's) becomes a dependent state - a satellite where the regimes differ, a protectorate
    /// where they agree. The same for a player as for anybody. Off with AiPartitionKinShare 0.
    /// </summary>
    internal static void ImposeAfterCapitulation(Campaign campaign, Nation winner, Nation beaten)
    {
        if (campaign is null || winner is null || beaten is null || winner.Id == beaten.Id) return;
        if (campaign.Defines.AiPartitionKinShare <= 0f || winner.IsSubject || beaten.IsSubject) return;
        if (beaten.Movement is not null || beaten.Population <= 0 || campaign.StatesOf(beaten).Length == 0) return;
        if (winner.Gdp < beaten.Gdp * 0.5f) return;
        long people = 0, ours = 0;
        var beatenStates = campaign.StatesOf(beaten);
        for (var si = 0; si < beatenStates.Length; si++)
        {
            var region = beatenStates[si];
            people += region.Population;
            var kin = 1f - EconomySystem.ForeignShare(region, winner.Tag);
            ours += TerritorialClaims.TitleOf(campaign, winner, region) != TerritorialTitle.None
                ? region.Population : (long)(region.Population * kin);
        }
        if (people <= 0 || ours / (double)people >= campaign.Defines.AiPartitionKinShare) return;
        var kind = PickSubjectKind(campaign, winner, beaten);
        if (kind is null) return;
        if (SubjectSystem.Subjugate(campaign, winner, beaten, kind.Id, out _))
            PeaceConference.Tally["capitulation:subject_" + kind.Id] =
                PeaceConference.Tally.TryGetValue("capitulation:subject_" + kind.Id, out var n) ? n + 1 : 1;
    }

    /// <summary>
    /// Drafts a decisive settlement treaty (unconditional capitulation, nuclear limited peace,
    /// repelled aggression settlement, or subjugation client state) before spending war score budget.
    /// Returns true if a decisive peace treaty was drafted and placed into <paramref name="demand"/>.
    /// </summary>
    internal static bool WriteDecisiveSettlement(Campaign campaign, Nation winner, StrategicWar war,
        PeaceLedger ledger, PeaceDemand demand)
    {
        var map = campaign.ProvinceMap;
        var loser = campaign.NationById(war.EnemyLeaderOf(winner.Id));
        if (map is null || campaign.ProvinceController is null || campaign.ProvinceOccupation is null ||
            loser is null || loser.IsSubject) return false;
        // A state facing its own breakaway, or a civil-war contender claiming the state, writes
        // the whole country back into one once the war is decided.
        var reclaiming = Reintegration.ClaimsStateInCivilWar(campaign, war, winner, loser);
        // A war between a government and its rebels, or between two factions of one civil
        // war, may end with either side taking the state. The seat is the strongest claim,
        // not the only one: a side that has overrun the country has won it.
        var civilWar = war.CivilConflict || AnnexationAmbition.IsCivilWarBetween(winner, loser);
        // A war declared to absorb a small neighbour is not priced like a border correction
        // once the winner plainly holds the country. These are the words Warmaking writes.
        var annexationWar = war.Justification is "hegemony" or "annexation" or "conquest" or "imperialism";
        var rebelContender = !reclaiming && WarAims.IsRebel(winner);
        var seatTaken = false;
        if (rebelContender)
        {
            var incumbent = campaign.NationById(war.EnemyLeaderOf(winner.Id));
            seatTaken = incumbent is not null && incumbent.CapitalProvinceId > 0 &&
                        campaign.ProvinceController[incumbent.CapitalProvinceId] == winner.MapIndex;
        }
        // A war of reunification — shared people, kindred homeland across the border, or an
        // authored national unification — ends, when won decisively, with the union itself.
        var reunification = Reintegration.IsReunificationTarget(campaign, war, winner, loser) ||
            AnnexationAmbition.IsReunification(campaign, winner, loser) ||
            (war.Justification == "reconquest" && TerritorialClaims.HoldsOurHomeland(campaign, winner, loser));
        var score = war.IsAttacking(winner.Id) ? war.WarScore : -war.WarScore;
        var territory = _decisiveTerritoryScratch ??= new List<int>(256);
        territory.Clear();
        var controllers = campaign.ProvinceController;
        var occupations = campaign.ProvinceOccupation;
        var ptn = map.ProvinceToNation;
        var loserMap = (ushort)loser.MapIndex;
        var winnerMap = (ushort)winner.MapIndex;
        var secThreshold = campaign.Defines.OccupationSecureThreshold;
        var held = 0;
        var secured = 0;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (ptn[p] != loserMap || map.IsWater(p)) continue;
            territory.Add(p);
            if (controllers[p] == winnerMap)
            {
                held++;
                if (occupations[p] >= secThreshold) secured++;
            }
        }
        if (territory.Count == 0) return false;
        var heldShare = held / (float)territory.Count;
        var securedShare = secured / (float)territory.Count;
        // Either side of a civil war may take the state once it has plainly won it. The
        // capital is the cleanest proof, but a government that has lost most of its ground
        // and its army has lost the country whether or not the last office still stands.
        // Ground is its own proof: a side holding two-thirds of the country has settled the
        // question whatever the war-score ledger says. The old AND let a government that
        // had reconquered all but one region sign a peace that left that region the war's
        // whole point - a rump state neither side could use and nobody wanted.
        var civilDecisive = civilWar && !reclaiming &&
            (SideOverrun(campaign, war, war.IsAttacking(loser.Id)) ||
             heldShare >= .65f ||
             (score >= campaign.Defines.AiCivilAnnexationScore &&
              securedShare >= campaign.Defines.AiCivilAnnexationOccupationShare));
        var annexationDecisive = annexationWar &&
            score >= campaign.Defines.AiAnnexationDecisiveScore &&
            securedShare >= campaign.Defines.AiAnnexationOccupationShare;
        var absorbing = reclaiming || seatTaken || reunification || civilDecisive;
        if (!absorbing && !PeaceShape.Near(campaign, winner, loser)) return false;

        // The Cuba rule: one holder of the deterrent does not conquer another. The wall's
        // door has no handle on this side. A war between holders still ends - on limited
        // terms, and sooner the higher the shadow, because the world (and each side's own
        // cities) is doing the pushing: the decisive bar comes down by the brink, and the
        // settlement is liberation and money, never the aggressor's homeland on the map.
        var brink = global::DealOrForce.Simulation.Deterrence.Brinkmanship(campaign, war);
        // At DEFCON 2 and 1 a war a nuclear power is fighting is settled in the shadow too, not
        // only one between two of them. Not every war on earth: applied to all of them, one
        // standoff between India and Pakistan ended the Sahel's wars as well, and a ten-year world
        // had half the wars and a third of the new states.
        if (campaign.DefconLevel <= 2 && !absorbing && !war.CivilConflict)
        {
            var hasNuclearHolder = false;
            foreach (var id in war.Side(true))
                if (NuclearPosture.IsHolder(campaign, campaign.NationById(id))) { hasNuclearHolder = true; break; }
            if (!hasNuclearHolder)
                foreach (var id in war.Side(false))
                    if (NuclearPosture.IsHolder(campaign, campaign.NationById(id))) { hasNuclearHolder = true; break; }
            if (hasNuclearHolder)
                brink = MathF.Max(brink, (3 - campaign.DefconLevel) * 1.5f);
        }
        if (!absorbing && brink > 0f)
        {
            var bar = MathF.Max(campaign.Defines.AiPeaceRepelledScore,
                campaign.Defines.AiPeaceDecisiveScore - brink * 10f);
            if (score < bar) return false;
            demand.Unconditional = false;
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (ptn[province] == winnerMap && !map.IsWater(province) &&
                    controllers[province] == loserMap)
                    demand.Provinces.Add(province);
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }

        // A repelled aggression is its own victory, and it is not a conquest. Measured
        // (WAR_CALIBRATION_2026_10_26): defenders led wars by as much as 47 points and
        // still took nothing, because the only decisive verdict was the attacker's
        // annexation. When the side that was attacked has plainly won on points, its
        // settlement is liberation and money: every province of its own still under the
        // aggressor's boot (ground already held, priced as nothing to retake), reparations
        // from the verdict's budget, and no claim on the aggressor's homeland at all.
        // The absorbing cases above keep their own, harsher roads.
        if (!absorbing && !civilWar && !war.IsAttacking(winner.Id) &&
            score >= campaign.Defines.AiPeaceRepelledScore &&
            campaign.ElapsedDays - war.StartedDay >= campaign.Defines.AiPeaceDecisivePatienceDays / 2)
        {
            demand.Unconditional = false;
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (ptn[province] == winnerMap && !map.IsWater(province) &&
                    controllers[province] == loserMap)
                    demand.Provinces.Add(province);
            // The verdict's money half is written by the terms from the ledger's budget;
            // the demand here carries only the ground.
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        if (absorbing)
        {
            var scoreFloor = reunification
                ? campaign.Defines.AiReunificationDecisiveScore
                : Math.Min(campaign.Defines.ReclaimDecisiveScore, campaign.Defines.AiCivilAnnexationScore);
            var occupationFloor = reunification
                ? campaign.Defines.AiReunificationOccupationShare
                : Math.Min(campaign.Defines.ReclaimSecuredShare, campaign.Defines.AiCivilAnnexationOccupationShare);
            var rebelArmiesLeft = 0;
            if (reclaiming)
            {
                var armies = campaign.Armies;
                for (var ai = 0; ai < armies.Count; ai++)
                    if (armies[ai].NationId == loser.Id && armies[ai].Strength > 0.05f) { rebelArmiesLeft++; break; }
            }
            var rebelBroken = reclaiming && (rebelArmiesLeft == 0 ||
                                             heldShare >= 0.75f || (heldShare >= 0.50f && score >= 40f));
            if (!rebelBroken && score < scoreFloor &&
                securedShare < occupationFloor &&
                heldShare < 0.65f &&
                !SideOverrun(campaign, war, war.IsAttacking(loser.Id))) return false;
            for (var pi = 0; pi < territory.Count; pi++) demand.Provinces.Add(territory[pi]);
            // The war is decided: what is left of the government capitulates with the rest.
            // A civil war or a reunification is not a border bargain, and a war score that
            // could not buy every province must not leave the loser standing as a rump.
            demand.Unconditional = true;
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        // A verdict that has stood for a season of the war is its own proof of conquest. The
        // civil branch already says ground is its own proof; an interstate war that has held
        // two-thirds of a country at a score of ninety for the patience of the world does not
        // need the occupation paperwork to finish converting before the loser capitulates.
        // Measured before this gate: wars of 618 and 730 days at a score of 100, unresolved.
        var longDecisive = score >= MathF.Min(90f, campaign.Defines.AiPeaceDecisiveScore + 15f) &&
                           campaign.ElapsedDays - war.StartedDay >= campaign.Defines.AiPeaceDecisivePatienceDays &&
                           heldShare >= .65f;
        var fullConquest = Reintegration.WantsFullConquestAnnexation(campaign, war, winner, loser, score, securedShare, heldShare) ||
            annexationDecisive || longDecisive;
        if (!fullConquest && (score < campaign.Defines.AiPeaceDecisiveScore || securedShare < campaign.Defines.AiPeaceDecisiveOccupationShare))
            return false;

        // The full treaty may spend the full earned score; the ordinary safety margin
        // must not make an affordable victory impossible. No free territory is created:
        // the ground is already held or the enemy is overrun.
        for (var pi = 0; pi < territory.Count; pi++) demand.Provinces.Add(territory[pi]);
        // A war declared to take the country (annexation, conquest, hegemony, empire) ends as declared.
        if (!absorbing && !annexationWar && Partition(campaign, winner, loser, territory, demand, ledger))
        {
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        demand.Subjugate = "";
        if (demand.Provinces.Count != territory.Count)
        {
            demand.Provinces.Clear();
            for (var pi = 0; pi < territory.Count; pi++) demand.Provinces.Add(territory[pi]);
        }
        if (fullConquest)
        {
            demand.Unconditional = true;
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        if (ledger.Cost(demand) <= ledger.Budget)
        {
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }
        demand.Provinces.Clear();

        if (loser.IsSubject || loser.Movement is not null)
            return false;

        // A war begun to restore a union ends by restoring one. The personal-union subject
        // kind is dormant for ordinary peaces (imposable: false in data) and wakes only for
        // the war whose stated reason was the dynastic claim.
        if (war.Justification == "dynastic_claim" && SubjectCatalog.Knows("personal_union") &&
            PeaceConference.SubjugationCost(campaign, winner, loser, "personal_union") <= ledger.Budget)
        {
            demand.Subjugate = "personal_union";
            WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
            return true;
        }

        // Annexation can be too expensive. Leaving a dependent state intact is a genuine
        // alternative even when forces hold enemy territory.
        var kind = PickSubjectKind(campaign, winner, loser, ledger.Budget);
        if (kind is null) return false;
        demand.Subjugate = kind.Id;
        WriteTerms(campaign, winner, war, ledger, demand, ledger.Budget);
        return true;
    }
}
