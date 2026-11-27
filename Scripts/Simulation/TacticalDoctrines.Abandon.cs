using System;

namespace DealOrForce.Simulation;

public static partial class TacticalDoctrines
{
    private const float AbandonCostEpsilon = 0.001f;

    private static DoctrineTrack FindHeldTrack(Nation nation, DoctrineBranch branch, out DoctrineTrack primary, out bool isCompanion)
    {
        primary = null;
        isCompanion = false;
        if (nation is null || branch is null) return null;
        EnsureShelves(nation);
        primary = nation.TacticalDoctrine.Track(branch.Slot);
        if (string.Equals(primary.Branch, branch.Id, StringComparison.Ordinal) ||
            string.Equals(primary.LearningBranch, branch.Id, StringComparison.Ordinal))
            return primary;
        if (primary.Companion is { } companion &&
            (string.Equals(companion.Branch, branch.Id, StringComparison.Ordinal) ||
             string.Equals(companion.LearningBranch, branch.Id, StringComparison.Ordinal)))
        {
            isCompanion = true;
            return companion;
        }
        return null;
    }

    private static DoctrineTrack FindLearningTrack(Nation nation, DoctrineBranch branch, out DoctrineTrack primary, out bool isCompanion)
    {
        primary = null;
        isCompanion = false;
        if (nation is null || branch is null) return null;
        EnsureShelves(nation);
        primary = nation.TacticalDoctrine.Track(branch.Slot);
        if (string.Equals(primary.LearningBranch, branch.Id, StringComparison.Ordinal) &&
            primary.LearningNode.Length > 0)
            return primary;
        if (primary.Companion is { } companion &&
            string.Equals(companion.LearningBranch, branch.Id, StringComparison.Ordinal) &&
            companion.LearningNode.Length > 0)
        {
            isCompanion = true;
            return companion;
        }
        return null;
    }

    private static void ClearSlotModifierCache(Nation nation, string slot)
    {
        if (nation is null || string.IsNullOrEmpty(slot)) return;
        var secondKey = SecondSlotKey(slot);
        nation.DoctrineModifierCache?.Remove(slot);
        nation.DoctrineModifierCache?.Remove(secondKey);
        nation.DoctrineModifierStamps?.Remove(slot);
        nation.DoctrineModifierStamps?.Remove(secondKey);
    }

    private static void ClearTrackContents(DoctrineTrack track, int rewriteDay)
    {
        track.Branch = "";
        track.Known.Clear();
        track.LearningBranch = "";
        track.LearningNode = "";
        track.LearningDoneDay = -1;
        track.LastRewriteDay = rewriteDay;
    }

    private static void PromoteCompanionIntoPrimary(DoctrineTrack primary, DoctrineTrack companion)
    {
        primary.Branch = companion.Branch;
        primary.Known.Clear();
        for (var i = 0; i < companion.Known.Count; i++) primary.Known.Add(companion.Known[i]);
        primary.LearningBranch = companion.LearningBranch;
        primary.LearningNode = companion.LearningNode;
        primary.LearningDoneDay = companion.LearningDoneDay;
        primary.LastRewriteDay = companion.LastRewriteDay;
        primary.Companion = null;
    }

    /// <summary>Whether the nation currently holds this tactical book (established or pending first study).</summary>
    public static bool IsHeld(Nation nation, string branchId, out bool established)
    {
        established = false;
        var branch = TacticalDoctrineCatalog.Find(branchId);
        if (nation is null || branch is null) return false;
        var track = FindHeldTrack(nation, branch, out _, out _);
        if (track is null) return false;
        established = string.Equals(track.Branch, branch.Id, StringComparison.Ordinal);
        return true;
    }

    /// <summary>Why this tactical doctrine cannot be abandoned now. Empty means it can.</summary>
    public static string AbandonRefusal(Campaign campaign, Nation nation, string branchId)
    {
        if (campaign is null || !campaign.FeatureEnabled("tactical_doctrines"))
            return Loc.Get("This mechanic is disabled in this scenario.");
        var branch = TacticalDoctrineCatalog.Find(branchId);
        if (nation is null || branch is null) return Loc.Get("No such Doctrine is written.");
        var track = FindHeldTrack(nation, branch, out _, out _);
        if (track is null) return Loc.Get("doctrine.err.not_held");
        var established = string.Equals(track.Branch, branch.Id, StringComparison.Ordinal);
        if (!established) return "";
        var cost = campaign.Defines.DoctrineRewritePoliticalCost;
        if (cost > 0f && nation.PoliticalPoints + AbandonCostEpsilon < cost)
            return Loc.Format("doctrine.err.abandon_cost", cost);
        return "";
    }

    /// <summary>
    /// Abandon an active tactical doctrine (paying political cost and entering retraining) or
    /// cancel a pending first study before the book has landed (free, no retraining).
    /// Promotes any active or learning companion book into the primary shelf slot.
    /// </summary>
    public static bool Abandon(Campaign campaign, Nation nation, string branchId)
    {
        if (campaign is null || nation is null) return false;
        var branch = TacticalDoctrineCatalog.Find(branchId);
        if (branch is null || AbandonRefusal(campaign, nation, branchId).Length > 0) return false;
        var track = FindHeldTrack(nation, branch, out var primary, out var isCompanion);
        if (track is null) return false;
        var established = string.Equals(track.Branch, branch.Id, StringComparison.Ordinal);
        if (!established)
        {
            track.LearningBranch = "";
            track.LearningNode = "";
            track.LearningDoneDay = -1;
            if (isCompanion && track.Branch.Length == 0 && track.Known.Count == 0)
                primary.Companion = null;
            SyncLegacyFields(nation, primary);
            campaign.MarkStrategicChange();
            return true;
        }
        var cost = campaign.Defines.DoctrineRewritePoliticalCost;
        if (cost > 0f) nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - cost);
        if (isCompanion)
        {
            primary.Companion = null;
        }
        else if (primary.Companion is { } comp &&
                 (comp.Branch.Length > 0 || comp.LearningBranch.Length > 0 || comp.LearningNode.Length > 0))
        {
            PromoteCompanionIntoPrimary(primary, comp);
        }
        else
        {
            ClearTrackContents(primary, campaign.ElapsedDays);
            primary.Companion = null;
        }
        SyncLegacyFields(nation, primary);
        ClearSlotModifierCache(nation, branch.Slot);
        if (campaign.Defines.DoctrineRetrainingDays > 0)
            ModifierSystem.Grant(campaign, nation, RetrainingModifier, campaign.Defines.DoctrineRetrainingDays);
        NationalModifiers.Invalidate(nation);
        campaign.Chronicle.Record(campaign, "doctrine_abandoned", nation.Id, subject: branch.Name, isPublic: true);
        campaign.MarkStrategicChange();
        return true;
    }

    /// <summary>Why an in-progress article study in this branch cannot be cancelled now. Empty means it can.</summary>
    public static string CancelStudyRefusal(Campaign campaign, Nation nation, string branchId)
    {
        if (campaign is null || !campaign.FeatureEnabled("tactical_doctrines"))
            return Loc.Get("This mechanic is disabled in this scenario.");
        var branch = TacticalDoctrineCatalog.Find(branchId);
        if (nation is null || branch is null) return Loc.Get("No such Doctrine is written.");
        if (FindLearningTrack(nation, branch, out _, out _) is null)
            return Loc.Get("doctrine.err.not_studying");
        return "";
    }

    /// <summary>
    /// Cancel an in-progress article study without dropping already-learned articles of the book.
    /// Spent experience is not refunded. Cleans up an unestablished companion track if empty.
    /// </summary>
    public static bool CancelStudy(Campaign campaign, Nation nation, string branchId)
    {
        if (campaign is null || nation is null) return false;
        var branch = TacticalDoctrineCatalog.Find(branchId);
        if (branch is null || CancelStudyRefusal(campaign, nation, branchId).Length > 0) return false;
        var track = FindLearningTrack(nation, branch, out var primary, out var isCompanion);
        if (track is null) return false;
        track.LearningBranch = "";
        track.LearningNode = "";
        track.LearningDoneDay = -1;
        if (isCompanion && track.Branch.Length == 0 && track.Known.Count == 0)
            primary.Companion = null;
        SyncLegacyFields(nation, primary);
        campaign.MarkStrategicChange();
        return true;
    }
}

public sealed class AbandonDoctrineCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string BranchId { get; init; } = "";

    /// <summary>Why the order could not be carried out, in the words the interface shows.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        Refusal = TacticalDoctrines.AbandonRefusal(campaign, nation, BranchId);
        if (Refusal.Length > 0) return;
        TacticalDoctrines.Abandon(campaign, nation, BranchId);
    }
}

public sealed class CancelDoctrineStudyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string BranchId { get; init; } = "";

    /// <summary>Why the order could not be carried out, in the words the interface shows.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        Refusal = TacticalDoctrines.CancelStudyRefusal(campaign, nation, BranchId);
        if (Refusal.Length > 0) return;
        TacticalDoctrines.CancelStudy(campaign, nation, BranchId);
    }
}
