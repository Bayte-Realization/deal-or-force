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

    /// <summary>The share of the armed forces' weekly pay a raised alert's surcharge applies to (DefconSystem.WeeklyUpkeep).</summary>
    public float AlertUpkeepArmyShare { get; set; } = 1f;

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
    public float DefconTradeConfidence2 { get; set; } = 0.95f;

    /// <summary>Trade confidence scaling during maximum DEFCON 1 alert.</summary>
    public float DefconTradeConfidence1 { get; set; } = 0.80f;
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
///    • TreasuryDynamics: WeeklyUpkeep() — надбавка (upkeepMult - 1) к недельному жалованью армии
///      (строка бюджета armed_forces). Ядерная держава платит за свою готовность (NuclearPosture),
///      остальные — часть мировой, только пока сами воюют (с 15.11.2026; раньше — доля ВВП для всех).
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

    private sealed class TierEntry
    {
        public readonly int Day;
        public readonly int Flags;
        public readonly int TechCount;
        public readonly string Tag;
        public readonly int Tier;

        public TierEntry(int day, int flags, int techCount, string tag, int tier)
        {
            Day = day;
            Flags = flags;
            TechCount = techCount;
            Tag = tag;
            Tier = tier;
        }
    }

    private sealed class TierCache
    {
        public readonly object Gate = new();
        public TierEntry[] Entries = Array.Empty<TierEntry>();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, TierCache> TierCaches = new();

    private static int TierFlags(Campaign campaign) =>
        (campaign.Defines.MutualDeterrenceEnabled ? 1 : 0) |
        (campaign.FeatureEnabled("defcon") ? 2 : 0) |
        (campaign.FeatureEnabled("nuclear_posture") ? 4 : 0) |
        (campaign.FeatureEnabled("nuclear_strikes") ? 8 : 0) |
        (TechnologyCatalog.Revision << 4);

    /// <summary>Thread-safe cached deterrent tier for hot DEFCON/military paths.</summary>
    internal static int CachedDeterrenceTier(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !campaign.Defines.MutualDeterrenceEnabled) return 0;
        var flags = TierFlags(campaign);
        if ((flags & 14) == 0) return 0;
        var id = nation.Id;
        if (id < 0) return Deterrence.TierOf(campaign, nation);
        var cache = TierCaches.GetOrCreateValue(campaign);
        var day = campaign.ElapsedDays;
        var techCount = nation.Technologies.Count;
        var tag = nation.Tag ?? "";
        var arr = System.Threading.Volatile.Read(ref cache.Entries);
        if ((uint)id < (uint)arr.Length)
        {
            var entry = System.Threading.Volatile.Read(ref arr[id]);
            if (entry is not null && entry.Day == day && entry.Flags == flags && entry.TechCount == techCount &&
                string.Equals(entry.Tag, tag, StringComparison.Ordinal))
                return entry.Tier;
        }
        var tier = Deterrence.TierOf(campaign, nation);
        lock (cache.Gate)
        {
            var current = cache.Entries;
            if ((uint)id >= (uint)current.Length)
            {
                var nextLen = Math.Max(id + 64, current.Length == 0 ? 512 : current.Length * 2);
                var grown = new TierEntry[nextLen];
                Array.Copy(current, grown, current.Length);
                System.Threading.Volatile.Write(ref cache.Entries, grown);
                current = grown;
            }
            System.Threading.Volatile.Write(ref current[id], new TierEntry(day, flags, techCount, tag, tier));
        }
        return tier;
    }

    [ThreadStatic] private static HashSet<int> _holdersScratch;
    [ThreadStatic] private static List<int> _holderIdsScratch;
    [ThreadStatic] private static HashSet<int> _strikeHoldersScratch;
    [ThreadStatic] private static List<DiplomaticRelation> _holderTensionScratch;

    /// <summary>
    /// Evaluate the target DEFCON readiness required by the current world geopolitical state.
    /// </summary>
    public static int EvaluateTarget(Campaign campaign, out string primaryHotspot)
    {
        primaryHotspot = "";
        if (campaign is null || !Enabled(campaign)) return 5;

        var target = 5;
        var holders = _holdersScratch ??= new HashSet<int>();
        var holderIds = _holderIdsScratch ??= new List<int>();
        holders.Clear();
        holderIds.Clear();
        using (SimProfiler.Measure("    defcon:holders"))
        {
            var nations = campaign.ActiveNations;
            for (var i = 0; i < nations.Count; i++)
            {
                var nation = nations[i];
                if (nation.Population > 0 && CachedDeterrenceTier(campaign, nation) >= 1 && holders.Add(nation.Id))
                    holderIds.Add(nation.Id);
            }
        }

        if (holders.Count == 0) return 5;

        // 1. Direct wars involving deterrent holders
        HashSet<int> worldPowers = null;
        using (SimProfiler.Measure("    defcon:wars"))
        {
            var openWars = Warfare.OpenWars(campaign);
            var strikeHolders = _strikeHoldersScratch ??= new HashSet<int>();
            strikeHolders.Clear();
            var checkedStrikeHolders = false;
            var controllers = campaign.ProvinceController;
            for (var wi = 0; wi < openWars.Count; wi++)
            {
                var war = openWars[wi];
                if (war.Ended) continue;
                var attackerHolder = holders.Contains(war.AttackerId);
                var defenderHolder = holders.Contains(war.DefenderId);

                if (attackerHolder && defenderHolder)
                {
                    var attacker = campaign.NationById(war.AttackerId);
                    var defender = campaign.NationById(war.DefenderId);
                    var nameA = attacker?.DisplayName ?? "Holder";
                    var nameB = defender?.DisplayName ?? "Holder";

                    if (!war.CivilConflict)
                    {
                        var needsCheckA = attacker is not null && ((controllers is not null && attacker.CapitalProvinceId > 0 && attacker.CapitalProvinceId < controllers.Length && controllers[attacker.CapitalProvinceId] != attacker.MapIndex) || war.WarScore < -60f);
                        var needsCheckB = defender is not null && ((controllers is not null && defender.CapitalProvinceId > 0 && defender.CapitalProvinceId < controllers.Length && controllers[defender.CapitalProvinceId] != defender.MapIndex) || -war.WarScore < -60f);
                        if ((needsCheckA || needsCheckB) && !checkedStrikeHolders)
                        {
                            checkedStrikeHolders = true;
                            NuclearPosture.FillHoldersWithStrikeTarget(campaign, openWars, holders, strikeHolders);
                        }
                        if ((needsCheckA && strikeHolders.Contains(attacker.Id)) ||
                            (needsCheckB && strikeHolders.Contains(defender.Id)))
                        {
                            primaryHotspot = Loc.Format("{0} — {1}: nuclear powers in open war", nameA, nameB);
                            return 1; // Lowest possible
                        }
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
                    else if (enemy is not null && target > 3 && (worldPowers ??= WorldWar.WorldPowers(campaign)).Contains(enemy.Id))
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
            strikeHolders.Clear();
        }

        // 2. Active-tension standoffs between deterrent holders (ordered by pair id, stops early at DEFCON 2).
        using (SimProfiler.Measure("    defcon:relations"))
        {
            if (target > 2)
            {
                holderIds.Sort();
                var tensePairs = _holderTensionScratch ??= new List<DiplomaticRelation>();
                NuclearPosture.FillActiveHolderTensionRelations(campaign, holders, holderIds, tensePairs);
                for (var pi = 0; pi < tensePairs.Count; pi++)
                {
                    var relation = tensePairs[pi];
                    if (relation.TensionStage == (int)TensionStage.War && target > 2)
                    {
                        var a = campaign.NationById(relation.A);
                        var b = campaign.NationById(relation.B);
                        target = 2;
                        primaryHotspot = Loc.Format("{0} — {1}: war", a?.DisplayName ?? "", b?.DisplayName ?? "");
                        break;
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
                tensePairs.Clear();
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
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>Staged weekly DEFCON tick so nuclear posture and escalation spread cleanly across frames.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || !Enabled(campaign)) yield break;

        campaign.DefconDaysInStage++;

        // Weekly review on day remainder 4 (harmonized with Escalation/CrisisLadder)
        if (campaign.ElapsedDays % 7 != 4) yield break;
        // The holders' own week first - their alerts, tests and calls move the target below.
        using (var posture = NuclearPosture.WeekStages(campaign).GetEnumerator())
        {
            while (true)
            {
                bool more;
                using (SimProfiler.Measure("  defcon:posture-week")) more = posture.MoveNext();
                if (!more) break;
                yield return 0;
            }
        }

        int target;
        string hotspot;
        using (SimProfiler.Measure("  defcon:evaluate"))
        {
            target = EvaluateTarget(campaign, out hotspot);
        }
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
        var active = campaign.ActiveNations;
        for (var i = 0; i < active.Count; i++)
        {
            var n = active[i];
            if (n.Population > 0 && CachedDeterrenceTier(campaign, n) >= 1) return n.Id;
        }
        return campaign.Player?.Id ?? 0;
    }

    /// <summary>
    /// Military readiness organization recovery bonus factor.
    /// </summary>
    public static float ReadinessRecoveryMultiplier(Campaign campaign, int nationId) =>
        campaign is null || !Enabled(campaign) ? 1f : ReadinessRecoveryWhenEnabled(campaign, nationId);

    /// <summary>
    /// The same multiplier, for a caller that has asked <see cref="Enabled"/> itself and got
    /// yes: the march loop asks for every formation every hour, and the switch is one answer
    /// for all of them.
    ///
    /// NuclearPosture.Effective is written out here with what is already known. The ladder is
    /// on, so the world's level is the campaign's own; whether the country is a holder is the
    /// tier read below. Asking Effective asked both again - the switch by name, the tier with
    /// the research sum under it - which made this 210 ns a formation, the largest single
    /// piece of the military step on a save from 2050. And when the world stands at four or
    /// five and so does the country's own posture, its forces stand at four or five whether it
    /// holds a deterrent or not, and the answer is one without asking.
    /// </summary>
    internal static float ReadinessRecoveryWhenEnabled(Campaign campaign, int nationId)
    {
        var nation = campaign.NationById(nationId);
        var world = Math.Clamp(campaign.DefconLevel, 1, 5);
        if (nation is not null && world >= 4 && Math.Clamp(nation.NuclearPosture, 1, 5) >= 4) return 1f;
        var isHolder = nation is not null && CachedDeterrenceTier(campaign, nation) >= 1;
        // A holder's forces stand at its own alert when that is higher than the world's.
        var level = nation is null ? campaign.DefconLevel
            : isHolder ? Math.Min(world, Math.Clamp(nation.NuclearPosture, 1, 5)) : world;
        if (level >= 4) return 1f;

        return level switch
        {
            3 => isHolder ? 1.08f : 1.04f,
            2 => isHolder ? 1.15f : 1.08f,
            1 => isHolder ? 1.25f : 1.15f,
            _ => 1f
        };
    }

    private static bool AtWar(Campaign campaign, Nation nation)
    {
        foreach (var war in Warfare.OpenWars(campaign))
            if (!war.Ended && war.Involves(nation.Id)) return true;
        return false;
    }

    /// <summary>
    /// What a raised alert costs this country a week: its armed forces' weekly pay (the budget line
    /// "armed_forces", last closed week) times the surcharge, times AlertUpkeepArmyShare. The panel
    /// has always called it "readiness maintenance burden +N %": now it is N % of the army's pay, as
    /// it says, rather than a share of the whole economy that cost a holder at DEFCON 2 three
    /// quarters of its budget income. A country with no army pays nothing to keep it on alert.
    /// </summary>
    public static float WeeklyUpkeep(Campaign campaign, Nation nation)
    {
        var surcharge = UpkeepMultiplier(campaign, nation) - 1f;
        if (!(surcharge > 0f)) return 0f;
        var share = campaign.Defines.AlertUpkeepArmyShare;
        if (!float.IsFinite(share) || share <= 0f) return 0f;
        var pay = ArmedForcesWeekly(nation);
        return float.IsFinite(pay) && pay > 0f ? surcharge * share * pay : 0f;
    }

    /// <summary>The armed forces' pay in the last closed week; before the first, the budget as it stands.</summary>
    internal static float ArmedForcesWeekly(Nation nation)
    {
        var lines = BudgetCore.All;
        var closed = nation?.LedgerBudgetLastWeek;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Store != BudgetStore.MilitarySpending) continue;
            if (closed is not null && closed.Length == lines.Count) return MathF.Max(0f, -closed[index]);
            return MathF.Max(0f, BudgetCore.Amount(nation, lines[index], nation.Gdp / 52f, false));
        }
        return 0f;
    }

    /// <summary>
    /// Readiness upkeep multiplier on the country's armed forces (see <see cref="WeeklyUpkeep"/>).
    ///
    /// Each country pays for its own alert. A holder pays for the posture it ordered
    /// (<see cref="Nation.NuclearPosture"/>), which a computer raises for a war or a standoff of its
    /// own and stands down two months after; a country without the weapon pays a part of the world's
    /// alert only while it is itself at war. Until 15 November 2026 every holder paid the world's
    /// level and every other country 40 % of it, whoever had raised it: one standoff between two
    /// holders put the whole world on a war footing, and SimBench showed the United States paying
    /// 32-56 % of its income for an alert it never ordered, China 41-71 %.
    /// </summary>
    public static float UpkeepMultiplier(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !Enabled(campaign)) return 1f;
        var isHolder = CachedDeterrenceTier(campaign, nation) >= 1;
        var level = isHolder ? Math.Clamp(nation.NuclearPosture, 1, 5) : CurrentLevel(campaign);
        if (level >= 5) return 1f;
        if (!isHolder && !AtWar(campaign, nation)) return 1f;
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
