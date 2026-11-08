using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    /// <summary>
    /// Global Defense Readiness Condition (5 peacetime down to 1 maximum nuclear alert).
    /// </summary>
    public int DefconLevel { get; set; } = 5;

    /// <summary>
    /// Consecutive days the global posture has remained at the current DEFCON level.
    /// Used for stage dwell time before de-escalation or critical escalation countdowns.
    /// </summary>
    public int DefconDaysInStage { get; set; }

    /// <summary>
    /// Highest alert level reached during this campaign (minimum DEFCON number).
    /// </summary>
    public int DefconPeakLevel { get; set; } = 5;
}

public sealed partial class Defines
{
    /// <summary>Whether the global DEFCON readiness ladder monitors nuclear tensions.</summary>
    public bool DefconEnabled { get; set; } = true;

    /// <summary>Minimum days at calm before DEFCON can de-escalate by one level.</summary>
    public int DefconDeescalationDays { get; set; } = 21;

    /// <summary>Military upkeep surcharge multiplier for deterrent holders at DEFCON 4.</summary>
    public float DefconUpkeepMultiplier4 { get; set; } = 1.05f;

    /// <summary>Military upkeep surcharge multiplier for deterrent holders at DEFCON 3.</summary>
    public float DefconUpkeepMultiplier3 { get; set; } = 1.15f;

    /// <summary>Military upkeep surcharge multiplier for deterrent holders at DEFCON 2.</summary>
    public float DefconUpkeepMultiplier2 { get; set; } = 1.35f;

    /// <summary>Military upkeep surcharge multiplier for deterrent holders at DEFCON 1.</summary>
    public float DefconUpkeepMultiplier1 { get; set; } = 1.75f;

    /// <summary>Trade confidence scaling during severe DEFCON 2 alert.</summary>
    public float DefconTradeConfidence2 { get; set; } = 0.85f;

    /// <summary>Trade confidence scaling during maximum DEFCON 1 alert.</summary>
    public float DefconTradeConfidence1 { get; set; } = 0.70f;
}

/// <summary>
/// The global nuclear posture and readiness condition ladder (DEFCON 5..1).
///
/// In peacetime, world defense readiness stands at DEFCON 5 (Fade Out). When tensions
/// between nuclear deterrent holders (Deterrence.TierOf >= 1) flare into diplomatic crises,
/// border clashes, or open armed conflict, the world descends down the DEFCON scale:
/// - DEFCON 4 (Double Take): Increased intelligence watch and satellite reconnaissance.
/// - DEFCON 3 (Round House): Strategic air and rocket forces on 15-minute alert.
/// - DEFCON 2 (Fast Pace): Armed forces ready to deploy within 6 hours. Emergency summits.
/// - DEFCON 1 (Cocked Pistol): Maximum readiness. Launch-on-warning strategic triad.
///
/// Elevated alert improves division readiness and organization recovery (+5%..+25%),
/// but places standing armed forces under severe round-the-clock maintenance drag
/// (+5%..+75% upkeep) and depresses world trade confidence. At DEFCON 2 and 1, AI powers
/// face overwhelming brinkmanship pressure to mediate compromises or ceasefires before
/// the world slips beyond the point of no return.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// STATE LADDER & ESCALATION FLOWCHART:
/// ═══════════════════════════════════════════════════════════════════════════════
///
///    [ DEFCON 5 ] Peacetime / Fade Out
///         │       ▲
///  Crisis │       │ 21 Days Calm (DefconDeescalationDays)
///  Opened │       │
///         ▼       │
///    [ DEFCON 4 ] Intelligence Watch / Double Take
///         │       ▲
///  Border │       │ 21 Days Calm
///  Clashes│       │
///         ▼       │
///    [ DEFCON 3 ] Strategic Air Alert / Round House
///         │       ▲
///  Active │       │ 21 Days Calm
///  Hot War│       │
///         ▼       │
///    [ DEFCON 2 ] Armed Forces Deployed / Fast Pace (Brinkmanship activated)
///         │       ▲
///  Extreme│       │ 21 Days Calm
///  Clash  │       │
///         ▼       │
///    [ DEFCON 1 ] Maximum Alert / Cocked Pistol (Launch-on-warning)
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// ДЛЯ МОДДЕРОВ (FOR MODDERS) — Архитектура, математика и расширение DEFCON:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// 1. МАТЕМАТИКА И ЕДИНИЦЫ:
///    • DefconLevel: целое число от 5 (мир) до 1 (максимальная готовность триады).
///    • DefconDaysInStage: число дней непрерывного пребывания на текущей ступени.
///    • DefconPeakLevel: максимальный уровень тревоги (минимальный номер), достигнутый в партии.
///    • Деэскалация: ступени понижаются строго по одной после спокойствия в течение
///      DefconDeescalationDays (по умолчанию 21 день).
///
/// 2. СИСТЕМНАЯ СВЯЗКА (COUPLING WITH SIMULATION SYSTEMS):
///    • MilitaryCore: ReadinessRecoveryMultiplier() ускоряет восстановление боевого духа
///      и организации дивизий (+8%..+25% для ядерных держав, +4%..+15% для остальных).
///    • TreasuryDynamics: UpkeepMultiplier() переводится в еженедельный налог на ВВП
///      (alertDrain = nation.Gdp * (upkeepMult - 1f) * 0.002f) при DEFCON < 5.
///    • TradeCore: GlobalTradeMultiplier() масштабирует мировой товарооборот (0.85 при
///      DEFCON 2, 0.70 при DEFCON 1).
///    • CrisisLadder: при DefconLevel <= 2 великие державы-посредники удваивают силу
///      дипломатического снятия напряжённости (CrisisMediationRelief * 2 = 8.0/неделю).
///    • AiPeaceOutcomes: при DefconLevel <= 2 фактор грани фола (brink) принудительно
///      активируется для межгосударственных войн, снижая порог победы и исключая
///      аннексию национальной территории державы (Кубинское правило).
///
/// 3. КАК МОДИФИЦИРОВАТЬ И НАСТРАИВАТЬ:
///    • defines: game/common/defines/25_defcon.json — включение системы, сроки деэскалации,
///      наценки на содержание и коэффициенты торговли.
///    • Ядерный статус: настраивается через game/common/deterrence/*.json или технологии с
///      эффектом "effects": { "deterrence": 1 }.
///    • Хроника: система генерирует события "defcon_escalation" и "defcon_deescalation".
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class DefconSystem
{
    /// <summary>The world's alert ladder runs: the define is the master switch, the "defcon" feature the scenario's.</summary>
    public static bool Enabled(Campaign campaign) => campaign is not null && campaign.Defines.DefconEnabled && campaign.FeatureEnabled("defcon");

    /// <summary>
    /// Returns the current clamped DEFCON level (5 peacetime down to 1 maximum alert).
    /// Returns 5 if campaign is null or the DEFCON subsystem is disabled in defines.
    /// </summary>
    public static int CurrentLevel(Campaign campaign) =>
        campaign is null || !Enabled(campaign) ? 5 : Math.Clamp(campaign.DefconLevel, 1, 5);

    /// <summary>
    /// Evaluate the target DEFCON readiness required by the current world geopolitical state.
    /// </summary>
    public static int EvaluateTarget(Campaign campaign, out string primaryHotspot)
    {
        primaryHotspot = "";
        if (campaign is null || !Enabled(campaign)) return 5;

        var target = 5;
        var holders = new HashSet<int>();
        foreach (var nation in campaign.Nations)
            if (Deterrence.TierOf(campaign, nation) >= 1)
                holders.Add(nation.Id);

        if (holders.Count == 0) return 5;

        // 1. Direct wars involving deterrent holders
        foreach (var war in campaign.Wars)
        {
            if (war.Ended) continue;
            var attackerHolder = holders.Contains(war.AttackerId);
            var defenderHolder = holders.Contains(war.DefenderId);

            if (attackerHolder && defenderHolder)
            {
                var attacker = campaign.NationById(war.AttackerId);
                var defender = campaign.NationById(war.DefenderId);
                var nameA = attacker?.DisplayName ?? "Holder";
                var nameB = defender?.DisplayName ?? "Holder";

                // The brink itself only when one of them is losing on its own soil - its capital
                // taken, its war lost past recovery (NuclearPosture.Existential). Six weeks of
                // fighting used to be enough: Kargil, the Ussuri and every Indian-Pakistani
                // exchange since would each have been a DEFCON 1, a level the real world has
                // never once declared. Two holders at war, however long and however one-sided,
                // is the brink's antechamber (2).
                if (NuclearPosture.Existential(campaign, attacker) || NuclearPosture.Existential(campaign, defender))
                {
                    primaryHotspot = Loc.Format("{0} — {1}: nuclear powers in open war", nameA, nameB);
                    return 1; // Lowest possible
                }
                if (target > 2)
                {
                    target = 2;
                    primaryHotspot = Loc.Format("{0} — {1}: nuclear powers in armed conflict", nameA, nameB);
                }
            }
            else if (attackerHolder || defenderHolder)
            {
                var holderId = attackerHolder ? war.AttackerId : war.DefenderId;
                var enemyId = attackerHolder ? war.DefenderId : war.AttackerId;
                var holder = campaign.NationById(holderId);
                var enemy = campaign.NationById(enemyId);

                // A holder involved in an active World War
                if (war.WorldWarDay >= 0 && target > 2)
                {
                    target = 2;
                    primaryHotspot = Loc.Format("{0}: in a world war", holder?.DisplayName ?? "");
                }
                else if (enemy is not null && WorldWar.WorldPowers(campaign).Contains(enemy.Id) && target > 3)
                {
                    target = 3;
                    primaryHotspot = Loc.Format("{0} — {1}: war with a leading military power", holder?.DisplayName ?? "", enemy.DisplayName);
                }
                else if (target > 4)
                {
                    target = 4;
                    primaryHotspot = Loc.Format("{0} — {1}: war", holder?.DisplayName ?? "", enemy?.DisplayName ?? "");
                }
            }
        }

        // 2. Tensions between deterrent holders
        foreach (var relation in campaign.Relations.Values)
        {
            if (!holders.Contains(relation.A) || !holders.Contains(relation.B)) continue;

            if (relation.TensionStage == (int)TensionStage.War && target > 2)
            {
                var a = campaign.NationById(relation.A);
                var b = campaign.NationById(relation.B);
                target = 2;
                primaryHotspot = Loc.Format("{0} — {1}: war", a?.DisplayName ?? "", b?.DisplayName ?? "");
            }
            else if (relation.TensionStage == (int)TensionStage.Clashes && target > 3)
            {
                var a = campaign.NationById(relation.A);
                var b = campaign.NationById(relation.B);
                target = 3;
                primaryHotspot = Loc.Format("{0} — {1}: border clashes", a?.DisplayName ?? "", b?.DisplayName ?? "");
            }
            else if ((relation.TensionStage == (int)TensionStage.Crisis || relation.CrisisOpenedDay >= 0) && target > 4)
            {
                var a = campaign.NationById(relation.A);
                var b = campaign.NationById(relation.B);
                target = 4;
                primaryHotspot = Loc.Format("{0} — {1}: diplomatic crisis", a?.DisplayName ?? "", b?.DisplayName ?? "");
            }
        }

        // The world is never calmer than its most alarmed holder (NuclearPosture): an alert is a
        // decision a government takes, and the others read it as one.
        var lowest = NuclearPosture.Lowest(campaign, out var alarmed);
        if (lowest < target && alarmed is not null)
        {
            target = lowest;
            primaryHotspot = Loc.Format("{0} has raised its nuclear alert", alarmed.DisplayName);
        }

        // A weapon used is a year at the brink, whatever the arithmetic of the wars says.
        if (NuclearPosture.Shadow(campaign) is { } user && target > 1)
        {
            target = Math.Min(target, 2);
            primaryHotspot = Loc.Format("{0} has used a nuclear weapon", user.DisplayName);
        }

        return target;
    }

    /// <summary>
    /// Periodic tick of the DEFCON system. Advances stage dwell time, evaluates target,
    /// and handles escalation or calibrated de-escalation.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign is null || !Enabled(campaign)) return;

        campaign.DefconDaysInStage++;

        // Weekly review on day remainder 4 (harmonized with Escalation/CrisisLadder)
        if (campaign.ElapsedDays % 7 != 4) return;
        // The holders' own week first - their alerts, tests and calls move the target below.
        NuclearPosture.Week(campaign);

        var target = EvaluateTarget(campaign, out var hotspot);
        var current = campaign.DefconLevel;

        if (target < current)
        {
            // Escalation: readiness steps down by 1 (or 2 if direct hot war outbreak)
            var step = (target <= 2 && current == 5) ? 2 : 1;
            var next = Math.Max(target, current - step);
            campaign.DefconLevel = next;
            campaign.DefconDaysInStage = 0;
            campaign.DefconPeakLevel = Math.Min(campaign.DefconPeakLevel, next);

            // The chronicle's actor is a country; the level goes in the subject. Passing the
            // level as the actor filed DEFCON 2 under whichever country had id 2.
            campaign.Chronicle.Record(campaign, "defcon_escalation", ChronicleActor(campaign), -1, next.ToString(), hotspot);
            campaign.MarkStrategicChange();
        }
        else if (target > current)
        {
            // De-escalation: requires holding calm for DefconDeescalationDays
            if (campaign.DefconDaysInStage >= campaign.Defines.DefconDeescalationDays)
            {
                var next = current + 1;
                campaign.DefconLevel = next;
                campaign.DefconDaysInStage = 0;

                campaign.Chronicle.Record(campaign, "defcon_deescalation", ChronicleActor(campaign), -1, next.ToString());
                campaign.MarkStrategicChange();
            }
        }
    }

    /// <summary>Whose page the world's alert is written on: the most alarmed holder, else the first holder.</summary>
    private static int ChronicleActor(Campaign campaign)
    {
        NuclearPosture.Lowest(campaign, out var alarmed);
        if (alarmed is not null) return alarmed.Id;
        foreach (var n in campaign.Nations)
            if (n.Population > 0 && NuclearPosture.IsHolder(campaign, n)) return n.Id;
        return campaign.Player?.Id ?? 0;
    }

    /// <summary>
    /// Military readiness organization recovery bonus factor.
    /// </summary>
    public static float ReadinessRecoveryMultiplier(Campaign campaign, int nationId)
    {
        if (campaign is null || !Enabled(campaign)) return 1f;
        var nation = campaign.NationById(nationId);
        var isHolder = nation is not null && Deterrence.TierOf(campaign, nation) >= 1;
        // A holder's forces stand at its own alert when that is higher than the world's.
        var level = nation is null ? campaign.DefconLevel : NuclearPosture.Effective(campaign, nation);
        if (level >= 4) return 1f;

        return level switch
        {
            3 => isHolder ? 1.08f : 1.04f,
            2 => isHolder ? 1.15f : 1.08f,
            1 => isHolder ? 1.25f : 1.15f,
            _ => 1f
        };
    }

    /// <summary>
    /// Readiness upkeep multiplier on standing forces and alert infrastructure.
    /// </summary>
    public static float UpkeepMultiplier(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !Enabled(campaign)) return 1f;
        var level = NuclearPosture.Effective(campaign, nation);
        if (level >= 5) return 1f;

        var isHolder = Deterrence.TierOf(campaign, nation) >= 1;
        var defines = campaign.Defines;

        if (isHolder)
        {
            return level switch
            {
                4 => defines.DefconUpkeepMultiplier4,
                3 => defines.DefconUpkeepMultiplier3,
                2 => defines.DefconUpkeepMultiplier2,
                1 => defines.DefconUpkeepMultiplier1,
                _ => 1f
            };
        }

        // Non-nuclear powers experience partial alert spillover
        return level switch
        {
            3 => 1f + (defines.DefconUpkeepMultiplier3 - 1f) * 0.4f,
            2 => 1f + (defines.DefconUpkeepMultiplier2 - 1f) * 0.4f,
            1 => 1f + (defines.DefconUpkeepMultiplier1 - 1f) * 0.4f,
            _ => 1f
        };
    }

    /// <summary>
    /// Trade volume multiplier under global strategic alert.
    /// </summary>
    public static float GlobalTradeMultiplier(Campaign campaign)
    {
        if (campaign is null || !Enabled(campaign)) return 1f;
        return campaign.DefconLevel switch
        {
            2 => campaign.Defines.DefconTradeConfidence2,
            1 => campaign.Defines.DefconTradeConfidence1,
            _ => 1f
        };
    }

    /// <summary>
    /// Short localized DEFCON name (e.g. "DEFCON 5").
    /// </summary>
    public static string LevelName(int level) => level switch
    {
        5 => "DEFCON 5",
        4 => "DEFCON 4",
        3 => "DEFCON 3",
        2 => "DEFCON 2",
        1 => "DEFCON 1",
        _ => "DEFCON 5"
    };

    /// <summary>
    /// Localized badge text with readiness status subtitle (e.g. "DEFCON 3 · Rapid Alert").
    /// </summary>
    public static string LevelBadge(int level) => level switch
    {
        5 => "DEFCON 5 · Peacetime",
        4 => "DEFCON 4 · Intelligence Watch",
        3 => "DEFCON 3 · Rapid Alert",
        2 => "DEFCON 2 · Armed Forces Deployment",
        1 => "DEFCON 1 · Maximum Readiness",
        _ => "DEFCON 5 · Peacetime"
    };

    /// <summary>
    /// Semantic UI color token from Ui.Colour palette matching the alert level severity.
    /// DEFCON 5: status.calm, DEFCON 4: status.neutral, DEFCON 3: status.tense,
    /// DEFCON 2: status.crisis, DEFCON 1: status.emergency.
    /// </summary>
    public static string LevelColourToken(int level) => level switch
    {
        // The skin's own state tokens: "status.*" named colours the skin does not define, and
        // every level was drawn in the fallback colour.
        5 => "state.positive",
        4 => "state.info",
        3 => "state.warning",
        2 => "state.negative",
        1 => "state.critical",
        _ => "state.positive"
    };

    /// <summary>
    /// Localized military and operational description of the posture associated with this DEFCON level.
    /// </summary>
    public static string LevelDescription(int level) => level switch
    {
        5 => "Normal peacetime military posture. Early-warning systems in routine operation.",
        4 => "Increased intelligence watch and satellite reconnaissance. Early-warning radars active.",
        3 => "Air Force and strategic rocket forces placed on 15-minute alert. Rapid-response forces mobilized.",
        2 => "Armed forces ready to deploy within 6 hours. Emergency war cabinets convened at the brink.",
        1 => "Maximum combat readiness. Nuclear triad at launch-on-warning alert. Point of no return.",
        _ => "Normal peacetime readiness."
    };

    /// <summary>
    /// Human-readable summary for tooltips and briefing panels.
    /// </summary>
    public static string FormatSummary(Campaign campaign)
    {
        if (campaign is null) return "";
        var level = CurrentLevel(campaign);
        var badge = Loc.Get(LevelBadge(level));
        var desc = Loc.Get(LevelDescription(level));
        var days = campaign.DefconDaysInStage;
        var dwell = Loc.Format("{0} days in current alert state", days);
        var peak = Loc.Format("Peak campaign readiness: DEFCON {0}", campaign.DefconPeakLevel);

        var player = campaign.Player;
        var surcharge = player is not null ? (int)MathF.Round((UpkeepMultiplier(campaign, player) - 1f) * 100f) : 0;
        var costLine = surcharge > 0
            ? Loc.Format("Readiness maintenance burden: +{0}%", surcharge)
            : Loc.Get("Readiness maintenance burden: normal");

        EvaluateTarget(campaign, out var hotspot);
        var hotspotLine = !string.IsNullOrEmpty(hotspot)
            ? Loc.Format("Active crisis hotspot: {0}", hotspot)
            : Loc.Get("No critical nuclear-power hotspots active");

        return $"{badge}\n{desc}\n\n{dwell}\n{peak}\n{costLine}\n{hotspotLine}";
    }
}
