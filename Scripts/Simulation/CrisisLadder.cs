using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>The stage ladder's formal crisis: relations at Crisis or above and not at war
    /// are an open diplomatic crisis the great powers can weigh in on.</summary>
    public bool CrisisLadderEnabled { get; set; } = true;
    /// <summary>How many days a crisis may burn before the powers force a compromise.</summary>
    public int CrisisCompromiseDays { get; set; } = 120;
    /// <summary>The opinion a great power needs with BOTH sides to act as a mediator.</summary>
    public float CrisisMediatorOpinion { get; set; } = 15f;
    /// <summary>Weekly tension relief a mediator's conference produces.</summary>
    public float CrisisMediationRelief { get; set; } = 4f;
    /// <summary>Weekly tension a patron great power adds to a crisis against its rival.</summary>
    public float CrisisPatronPressure { get; set; } = 2.5f;
}

public sealed partial class DiplomaticRelation
{
    /// <summary>The day this pair's tension ladder formally opened a crisis, -1 none. The
    /// "point of no return" bookkeeping: from here the powers watch, mediate or fuel.</summary>
    public int CrisisOpenedDay = -1;
}

/// <summary>
/// The poker at the top of the tension ladder. The ladder itself (Calm→War, incidents,
/// drivers) is Escalation's; what it lacked was the table around it: a crisis that has
/// begun is VISIBLE, great powers take sides - one that thinks well of both sides calls
/// conferences that cool the crisis down, one that backs a side against its rival keeps
/// the pot boiling - and a crisis nobody settles for <see cref="Defines.CrisisCompromiseDays"/>
/// is settled for them: a visible compromise, tensions and grievances halved. The deal
/// half of the name, at the scale of the world.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// ДИПЛОМАТИЧЕСКАЯ ЛЕСТНИЦА И МЕХАНИКА КРИЗИСОВ:
/// ═══════════════════════════════════════════════════════════════════════════════
///
///   Tension 0..20   : [ Calm ] ────────── Спокойные добрососедские отношения
///   Tension 21..50  : [ Strained ] ────── Подозрительность, охлаждение торговли
///   Tension 51..75  : [ Friction ] ────── Инциденты, взаимные дипломатические ноты
///   Tension 76..90  : [ Crisis ] ──────── ВХОД В КРИЗИС (CrisisOpenedDay)
///                          │
///           ┌──────────────┴──────────────┐
///           ▼                             ▼
///    [ Посредники (Mediators) ]    [ Покровители (Patrons) ]
///    Мирные конференции            Разжигание противостояния
///    (ударное охлаждение при       (+2.5 напряжённости/неделю)
///     DEFCON <= 2)                        │
///           │                             ▼
///           │                      Tension 91..99 : [ Clashes ] (Пограничные бои)
///           ▼                             │
///    [ Компромисс через 120 дней ]        ▼
///    (Напряжённость/обиды / 2)     Tension 100    : [ War ] (Полномасштабная война)
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// ДЛЯ МОДДЕРОВ (FOR MODDERS) — Архитектура дипломатических кризисов:
/// ═══════════════════════════════════════════════════════════════════════════════
/// 1. ВХОД В КРИЗИС:
///    Когда шкала напряжённости между двумя государствами достигает TensionStage.Crisis
///    (и они не находятся в состоянии открытой войны), фиксируется CrisisOpenedDay.
///    В хронику записывается событие "crisis_opened".
///
/// 2. РОЛИ ВЕЛИКИХ ДЕРЖАВ (Great Powers Roles):
///    Каждую неделю (день % 7 == 3) великие державы оценивают участников кризиса:
///    • Посредник (Mediator): великая держава с мнением &gt;= CrisisMediatorOpinion (15) к
///      ОБЕИМ сторонам. Созывает мирные конференции, снимающие напряжённость.
///    • Покровитель (Patron): держава, имеющая высокое мнение к одной стороне (&gt;= 40)
///      и враждебное к другой (&lt;= -20). Нагнетает давление (CrisisPatronPressure).
///
/// 3. СВЯЗКА С DEFCON:
///    При обострении ядерной готовности (campaign.DefconLevel &lt;= 2) эффект мирных
///    конференций удваивается (CrisisMediationRelief * 2 = 8.0/неделю), моделируя
///    экстренное давление мировых правительств перед угрозой глобальной катастрофы.
///
/// 4. ПРИНУДИТЕЛЬНЫЙ КОМПРОМИСС (Compromise Timer):
///    Если кризис не перерос в войну и не был разрешён за CrisisCompromiseDays (120 дней),
///    великие державы навязывают компромисс: напряжённость и обиды делятся пополам,
///    а счётчик кризиса сбрасывается.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class CrisisLadder
{
    /// <summary>
    /// Weekly simulation tick (runs on day remainder 3).
    /// Updates open diplomatic crises, checks mediator and patron powers,
    /// applies conferences or escalatory pressure, and enforces compromises.
    /// </summary>
    public static void Tick(Campaign c)
    {
        if (c is null || !MechanicFeatures.SystemEnabled(c, nameof(CrisisLadder)) ||
            !c.Defines.CrisisLadderEnabled || c.ElapsedDays <= 0) return;
        if (c.ElapsedDays % 7 != 3) return;
        var powers = AllianceSystem.GreatPowers(c);
        foreach (var relation in c.Relations.Values)
        {
            if (relation.TensionStage < (int)TensionStage.Crisis) { relation.CrisisOpenedDay = -1; continue; }
            var a = c.NationById(relation.A);
            var b = c.NationById(relation.B);
            if (a is null || b is null || CampaignDiplomacy.AtWar(c, a.Id, b.Id)) continue;
            if (relation.CrisisOpenedDay < 0)
            {
                relation.CrisisOpenedDay = c.ElapsedDays;
                c.Chronicle.Record(c, "crisis_opened", a.Id, b.Id);
                continue;
            }
            // The powers take sides. A mediator cools; a patron of one against a rival heats.
            Nation mediator = null, patron = null;
            foreach (var id in powers)
            {
                if (id == a.Id || id == b.Id) continue;
                var power = c.NationById(id);
                if (power is null) continue;
                var toA = CampaignDiplomacy.Peek(c, id, a.Id)?.Opinion ?? 0f;
                var toB = CampaignDiplomacy.Peek(c, id, b.Id)?.Opinion ?? 0f;
                if (mediator is null && toA >= c.Defines.CrisisMediatorOpinion && toB >= c.Defines.CrisisMediatorOpinion)
                    mediator = power;
                // A patron backs the side it likes against the side it calls a rival.
                else if (patron is null && ((toA >= 40f && toB <= -20f) || (toB >= 40f && toA <= -20f)))
                    patron = power;
            }
            if (mediator is not null && Roll(c.Seed, a.Id, b.Id, c.ElapsedDays, 71) < 0.5f)
            {
                var relief = c.Defines.CrisisMediationRelief * (c.DefconLevel <= 2 ? 2f : 1f);
                relation.Tension = MathF.Max(0f, relation.Tension - relief);
                c.Chronicle.Record(c, "crisis_conference", a.Id, b.Id, place: mediator.DisplayName);
            }
            else if (patron is not null)
                relation.Tension = MathF.Min(100f, relation.Tension + c.Defines.CrisisPatronPressure);
            // A crisis nobody settles is settled for them: the powers' compromise, not a
            // winner. Tensions and grievances halved, the ladder walked back down.
            if (c.ElapsedDays - relation.CrisisOpenedDay >= Math.Max(14, c.Defines.CrisisCompromiseDays))
            {
                relation.Tension *= 0.5f;
                relation.GrievanceA *= 0.5f;
                relation.GrievanceB *= 0.5f;
                relation.CrisisOpenedDay = -1;
                c.Chronicle.Record(c, "crisis_compromise", a.Id, b.Id);
                c.MarkStrategicChange();
            }
        }
    }

    /// <summary>
    /// Deterministic 64-bit splitmix-like hash roll yielding a uniform float in [0, 1).
    /// Used for reproducible crisis mediation and diplomatic events without consuming the shared Campaign.Random state.
    /// </summary>
    private static float Roll(int seed, int a, int b, int day, int salt)
    {
        var hash = (uint)seed * 2654435761u ^ (uint)a * 2246822519u ^ (uint)b * 3266489917u ^
                   (uint)day * 668265263u ^ (uint)salt * 374761393u;
        hash ^= hash >> 15; hash *= 2246822519u; hash ^= hash >> 13; hash *= 3266489917u; hash ^= hash >> 16;
        return (hash >> 8) * (1f / 16777216f);
    }
}
