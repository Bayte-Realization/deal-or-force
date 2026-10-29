using System;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

/// <summary>Language-neutral queued operations. True means accepted, not yet applied.
/// Campaign commands recheck prices, cooldowns and territory at the execution boundary.</summary>
public static class ModOperations
{
    public static bool QueuePolicy(Campaign c, string issuerTag, string id, string target = "", int region = 0, bool promise = false)
    {
        var n = c?.NationByTag(issuerTag); var policy = PoliticalPolicyCatalog.Find(id, promise);
        if (n is null || !PoliticalPolicies.CanAdopt(c, n, policy, target, region, promise, out _)) return false;
        c.Commands.Enqueue(new AdoptPoliticalPolicyCommand {
            IssuerId = n.Id, PolicyId = id, TargetTag = target, Region = region, Agenda = promise });
        return true;
    }
    public static bool QueueDebt(Campaign c, string issuerTag, string action, double amount = 0)
    {
        var n = c?.NationByTag(issuerTag);
        if (n is null || !c.FeatureEnabled("public_debt") || !double.IsFinite(amount) || amount < 0 || amount > float.MaxValue ||
            action is not ("borrow" or "repay" or "restructure")) return false;
        if (action is "borrow" or "repay" && amount <= 0) return false;
        c.Commands.Enqueue(new ManageDebtCommand { IssuerId = n.Id, Action = action, Amount = (float)amount });
        return true;
    }
    public static bool QueueCourseShock(Campaign c, string issuerTag, double strength, string reason)
    {
        var n = c?.NationByTag(issuerTag);
        if (n is null || c.HumanSeats.Count > 1 || !c.FeatureEnabled("national_courses") ||
            !double.IsFinite(strength) || strength <= 0 || strength > 1 || reason is null || reason.Length > 256) return false;
        c.Commands.Enqueue(new ModCourseShockCommand { IssuerId = n.Id, Strength = (float)strength, Reason = reason });
        return true;
    }

    public static bool QueueStudyDoctrine(Campaign c, string issuerTag, string branchId, string nodeId, bool rush = false)
    {
        var n = c?.NationByTag(issuerTag);
        if (n is null || string.IsNullOrWhiteSpace(branchId) || string.IsNullOrWhiteSpace(nodeId) ||
            TacticalDoctrines.Refusal(c, n, branchId, nodeId, rush).Length > 0) return false;
        c.Commands.Enqueue(new StudyDoctrineCommand { IssuerId = n.Id, BranchId = branchId, NodeId = nodeId, Rush = rush });
        return true;
    }

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
