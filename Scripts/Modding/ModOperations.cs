using System;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

/// <summary>
/// Language-neutral queued operations shared by GDScript, Luau, C#, and F#.
/// True means the request was accepted into <c>Campaign.Commands</c>, not that it has
/// been paid for. Each command rechecks price, cooldown, and territory in <c>Apply</c>,
/// and the world may have moved while the request waited. Do not also deduct cash beside
/// a <c>Queue*</c> call: that charges twice, or charges for a refusal.
///
/// These methods only reach systems the game already runs. They do not open a second
/// economy, a second diplomacy, or a second battle. A human country's land is not
/// transferred here; acts that need their consent wait for a click.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// ДЛЯ МОДДЕРОВ (FOR MODDERS) — Архитектура командной очереди и безопасные действия:
/// ═══════════════════════════════════════════════════════════════════════════════
/// 1. АСИНХРОННОСТЬ И ВАЛИДАЦИЯ (Queue vs Apply):
///    Методы Queue* возвращают true, когда запрос синтаксически корректен и принят в
///    очередь Campaign.Commands. Они НЕ выполняют действие немедленно. Исполнение происходит
///    в методе Apply(Campaign) в такте симуляции. Внутри Apply повторно проверяются:
///    наличие казны, права на землю, дипломатические ограничения и кулдауны.
///
/// 2. ЗАПРЕТ ПАРАЛЛЕЛЬНОГО СПИСАНИЯ:
///    Никогда не списывайте деньги, политическую власть или рекрутов рядом с вызовом
///    Queue*! Сама команда спишет стоимость при успешном выполнении. Преждевременное
///    списание спишет средства дважды либо снимет плату за отклонённое действие.
///
/// 3. СЕТЕВОЙ ДЕТЕРМИНИЗМ И ICampaignCommand:
///    Любое действие, влияющее на авторитетное состояние мира, должно упаковываться
///    в ICampaignCommand. Команды детерминированно воспроизводятся на клиентах,
///    не нарушая CampaignFingerprint.
///
/// 4. СОГЛАСИЕ ИГРОКА:
///    Земли человека не передаются без явного подтверждения в интерфейсе. Команды
///    дипломатических актов (QueueDiplomaticAct) создают предложение (Propose),
///    требующее ответа адресата, если тот управляется человеком.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class ModOperations
{
    // МОДДЕРАМ: true — запрос поставлен в очередь, не обещание успешной покупки.
    // Пока он ждёт, казна/цель/кулдаун могут измениться. Именно Apply команды решает
    // окончательно. Не имитируйте успех списанием денег рядом с Queue...().

    /// <summary>
    /// Queue <see cref="AdoptPoliticalPolicyCommand"/> when <c>PoliticalPolicies.CanAdopt</c>
    /// already agrees. <paramref name="id"/> is a law or, when <paramref name="promise"/> is set, an agenda.
    /// <paramref name="region"/> is a state id the policy names, or 0.
    /// </summary>
    public static bool QueuePolicy(Campaign c, string issuerTag, string id, string target = "", int region = 0, bool promise = false)
    {
        var n = c?.NationByTag(issuerTag); var policy = PoliticalPolicyCatalog.Find(id, promise);
        if (n is null || !PoliticalPolicies.CanAdopt(c, n, policy, target, region, promise, out _)) return false;
        c.Commands.Enqueue(new AdoptPoliticalPolicyCommand {
            IssuerId = n.Id, PolicyId = id, TargetTag = target, Region = region, Agenda = promise });
        return true;
    }

    /// <summary>
    /// Queue <see cref="ManageDebtCommand"/>. <paramref name="action"/> is <c>borrow</c>, <c>repay</c>, or <c>restructure</c>.
    /// Borrow and repay need a positive finite amount, in billions. Requires <c>public_debt</c>.
    /// </summary>
    public static bool QueueDebt(Campaign c, string issuerTag, string action, double amount = 0)
    {
        var n = c?.NationByTag(issuerTag);
        if (n is null || !c.FeatureEnabled("public_debt") || !double.IsFinite(amount) || amount < 0 || amount > float.MaxValue ||
            action is not ("borrow" or "repay" or "restructure")) return false;
        if (action is "borrow" or "repay" && amount <= 0) return false;
        c.Commands.Enqueue(new ManageDebtCommand { IssuerId = n.Id, Action = action, Amount = (float)amount });
        return true;
    }

    /// <summary>
    /// Queue a shock on the existing national course. Strength is in (0, 1], reason at most 256 characters.
    /// Refused in multiplayer and when <c>national_courses</c> is off. IssuerId stored on the command is the list index.
    /// </summary>
    public static bool QueueCourseShock(Campaign c, string issuerTag, double strength, string reason)
    {
        var n = c?.NationByTag(issuerTag);
        if (n is null || c.HumanSeats.Count > 1 || !c.FeatureEnabled("national_courses") ||
            !double.IsFinite(strength) || strength <= 0 || strength > 1 || reason is null || reason.Length > 256) return false;
        c.Commands.Enqueue(new ModCourseShockCommand { IssuerId = n.Id, Strength = (float)strength, Reason = reason });
        return true;
    }

    /// <summary>
    /// Queue <see cref="StudyDoctrineCommand"/> for branch and node ids from <c>common/</c>, not button captions.
    /// Uses the same refusal string the doctrine screen shows, so a mod cannot buy a node the player cannot.
    /// </summary>
    public static bool QueueStudyDoctrine(Campaign c, string issuerTag, string branchId, string nodeId, bool rush = false)
    {
        // Идентификаторы ветки/узла из common/, а не переведённые подписи кнопок.
        // Этот путь сохраняет те же цены и ограничения, что у интерфейса доктрин.
        var n = c?.NationByTag(issuerTag);
        if (n is null || string.IsNullOrWhiteSpace(branchId) || string.IsNullOrWhiteSpace(nodeId) ||
            TacticalDoctrines.Refusal(c, n, branchId, nodeId, rush).Length > 0) return false;
        c.Commands.Enqueue(new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branchId, NodeId = nodeId, Rush = rush });
        return true;
    }

    /// <summary>
    /// Queue a proposal of an existing <see cref="DiplomaticAct"/>. Underscores in <paramref name="actName"/> are ignored
    /// so <c>state_visit</c> matches the enum. Refused when <c>DiplomaticActs.Refusal</c> is non-empty, including a bad pair.
    /// This proposes; it does not sign over a human's provinces.
    /// </summary>
    public static bool QueueDiplomaticAct(Campaign c, string issuerTag, string targetTag, string actName)
    {
        var issuer = c?.NationByTag(issuerTag);
        var target = c?.NationByTag(targetTag);
        if (issuer is null || target is null || issuer.Id == target.Id ||
            !Enum.TryParse<DiplomaticAct>(actName?.Replace("_", "", StringComparison.Ordinal), true, out var act) ||
            DiplomaticActs.Refusal(c, issuer, target, act).Length > 0) return false;
        c.Commands.Enqueue(new DiplomaticActCommand {
            IssuerId = issuer.Id, TargetId = target.Id, Act = act, Step = DiplomaticActStep.Propose });
        return true;
    }
}

/// <summary>
/// The command behind <see cref="ModOperations.QueueCourseShock"/>. Apply checks the same bounds again,
/// because the queue can sit across a day. It calls <c>NationalCourses.Shock</c> and does not keep a private course.
/// </summary>
public sealed class ModCourseShockCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float Strength { get; init; }
    public string Reason { get; init; } = "";
    public void Apply(Campaign c)
    {
        if (c.HumanSeats.Count <= 1 && c.FeatureEnabled("national_courses") && c.NationById(IssuerId) is { } n &&
            float.IsFinite(Strength) && Strength > 0 && Strength <= 1 && Reason.Length <= 256)
            NationalCourses.Shock(c, n, Strength, Reason);
    }
}
