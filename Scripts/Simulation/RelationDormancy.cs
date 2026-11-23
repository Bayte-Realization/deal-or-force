using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace DealOrForce.Simulation;

public sealed partial class DiplomaticRelation
{
    /// <summary>Set aside because one side has no people left (RelationDormancy). Saved, so a load puts it back where it was.</summary>
    public bool Dormant;
}

public sealed partial class Campaign
{
    /// <summary>
    /// Relations with a country that no longer has people: kept, with all they remember, but out of
    /// the table every daily system walks (RelationDormancy). Read through CampaignDiplomacy.Peek;
    /// CampaignDiplomacy.Get wakes one.
    /// </summary>
    public readonly Dictionary<long, DiplomaticRelation> DormantRelations = new(PairKeyComparer.Instance);
}

public sealed partial class Defines
{
    public bool RelationDormancyEnabled { get; set; } = true;
}

/// <summary>
/// The relations of the dead, set aside.
///
/// A relation is made the first time two countries have anything to do with each other and was
/// never unmade. Every rising, faction, movement and short-lived state of a long campaign left its
/// row behind: a four-year save held 53,018 relations for 257 countries with people, 41 % of them
/// with somebody who no longer existed, and social memory, alliances, opinion, gestures and the
/// AI's diplomacy walked every one of them every day. (Most of those rows are with live movements,
/// which stay awake; the systems' own costs were the larger part of the slowdown and were cut
/// where they grew - AllianceReviewGraph, SocialMemory, the AI's friendly diplomacy.) Such a
/// relation now sleeps: it keeps its memories (a restored country
/// remembers who betrayed it) but leaves the daily table, and wakes the day its country has people
/// again or anything asks to write to it.
/// </summary>
public static class RelationDormancy
{
    [ThreadStatic] private static List<long> _movingScratch;
    [ThreadStatic] private static List<KeyValuePair<long, DiplomaticRelation>> _keptScratch;
    [ThreadStatic] private static bool[] _gone;
    [ThreadStatic] private static bool[] _landlessMovement;

    private static List<long> Moving => _movingScratch ??= new();

    /// <summary>Whether this side of a relation is gone: no country, or no people and no living movement.</summary>
    private static bool Gone(Campaign c, int id) =>
        c.NationById(id) is not { } n || n.Population <= 0 && n.Movement is not { Dissolved: false };

    private static bool LandlessMovement(Campaign c, int id) =>
        c.NationById(id) is { Population: <= 0, Movement: { Dissolved: false, Landless: true } };

    private static bool GoneNow(int id)
    {
        var gone = _gone;
        return gone is null || id < 0 || id >= gone.Length || gone[id];
    }

    private static bool LandlessMovementNow(int id)
    {
        var landless = _landlessMovement;
        return landless is not null && (uint)id < (uint)landless.Length && landless[id];
    }

    private static bool Keep(DiplomaticRelation r) => r.Stance == DiplomaticStance.AtWar || r.LoanOutstanding > 0f;

    private static (FieldInfo Field, object Fresh)[] _unknownInertFieldChecks;

    private static (FieldInfo Field, object Fresh)[] UnknownInertFieldChecks()
    {
        var cached = System.Threading.Volatile.Read(ref _unknownInertFieldChecks);
        if (cached is not null) return cached;
        var fresh = new DiplomaticRelation();
        var checks = new List<(FieldInfo Field, object Fresh)>();
        foreach (var field in typeof(DiplomaticRelation).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.Name is
                "A" or "B" or "Dormant" or "Opinion" or "Reasons" or
                "Tension" or "StructuralTension" or "GrievanceA" or "GrievanceB" or "LoanOutstanding" or
                "AidSentDay" or "LastActionDay" or "AllianceOfferedBy" or "AgreementOfferedBy" or
                "EmbargoByA" or "EmbargoByB" or "TruceUntil" or
                "AllianceOfferedDay" or "AllianceRefusedDay" or "AllianceSinceDay" or "AllianceCohesion" or
                "AllianceAssessmentDay" or "AllianceStrainedDays" or "AllianceReaffirmedDay" or
                "AllianceProgram" or "AllianceProgramSponsor" or "AllianceProgramStarted" or
                "AllianceProgramLastPaid" or "AllianceProgramDuration" or "AllianceProgramPaidWeeks" or
                "AllianceProgramWeeklyCost" or "AllianceProgramTrustBudget" or "AllianceProgramRelief" or
                "AllianceProgramPausedDay" or "SubmissionAskedDay" or "AgreementRefusedDay" or
                "AgreementOfferedKind" or "AgreementOfferedDay" or "AgreementLapsedBy" or "AgreementLapsedDay" or
                "GuaranteeBrokenBy" or "RivalShiftDay" or "RivalShiftPaperBy" or "RivalShiftPaperDay" or
                "RivalShiftReason" or "CrisisOpenedDay" or
                "DiplomaticActBy" or "DiplomaticActDay" or "DiplomaticActKind" or
                "LoanLender" or "LoanWeekly" or "LoanMissedWeeks" or
                "MissionByA" or "MissionByB" or
                "TensionStage" or "TensionStageDay" or "QuarrelRefusalDay" or "QuarrelClaimDay" or
                "QuarrelRivalDay" or "QuarrelBorderDay" or "LastIncidentDay" or "LastIncident" or
                "LastIncidentBy" or "ClashLosses" or "TensionBaseline" or "TensionAnchor" or
                "TalksBy" or "TalksUntilDay" or "LastTalksDay" or "MediatorId" or "LastMediationDay" or
                "InterveningPowerId" or "InterventionTargetId" or "InterventionUntilDay" or
                "GuaranteeRequestedBy" or "GuaranteeRequestedDay" or
                "MilitaryAccessByA" or "MilitaryAccessByB" or
                "MilitaryAccessRequestedBy" or "MilitaryAccessRequestedDay" or
                "Trade" or "AgreementOfferedTrade")
                continue;
            if (field.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null) continue;
            checks.Add((field, field.GetValue(fresh)));
        }
        var built = checks.ToArray();
        System.Threading.Volatile.Write(ref _unknownInertFieldChecks, built);
        return built;
    }

    private static bool WithinAge(int elapsed, int day, int maxAgeDays) =>
        day >= 0 && elapsed - day <= maxAgeDays;

    private static bool PositiveWithinAge(int elapsed, int day, int maxAgeDays) =>
        day > 0 && elapsed - day <= maxAgeDays;

    private static bool OnlyDerivedReasons(DiplomaticRelation r)
    {
        for (var i = 0; i < r.Reasons.Count; i++)
            if (!string.Equals(r.Reasons[i].Id, "fear_of_strength", StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static bool CarriesNothingExceptOpinion(Campaign c, DiplomaticRelation r, bool allowAgedActivityDays)
    {
        if (r is null || r.Stance != DiplomaticStance.Neutral ||
            r.TradeAgreement || r.NonAggressionPact ||
            r.EmbargoByA || r.EmbargoByB ||
            r.GuaranteeByA || r.GuaranteeByB ||
            r.LoanOutstanding > 0f ||
            r.AllianceOfferedBy > 0 || r.AgreementOfferedBy >= 0 ||
            r.Tension > 0.01f || r.StructuralTension > 0.01f ||
            r.GrievanceA > 0.01f || r.GrievanceB > 0.01f)
            return false;

        if (!allowAgedActivityDays)
        {
            if (r.Reasons.Count != 0) return false;
            if (r.TruceUntil != 0 ||
                r.AllianceOfferedDay != -9999 || r.AllianceRefusedDay != -9999 || r.AllianceSinceDay != -9999 ||
                r.AllianceCohesion != 0.75f || r.AllianceAssessmentDay != -9999 || r.AllianceStrainedDays != 0 ||
                r.AllianceReaffirmedDay != -9999 || !string.Equals(r.AllianceProgram, "", StringComparison.Ordinal) ||
                r.AllianceProgramSponsor != -1 || r.AllianceProgramStarted != -1 || r.AllianceProgramLastPaid != -1 ||
                r.AllianceProgramDuration != 0 || r.AllianceProgramPaidWeeks != 0 ||
                r.AllianceProgramWeeklyCost != 0f || r.AllianceProgramTrustBudget != 0f ||
                r.AllianceProgramRelief || r.AllianceProgramPausedDay != -1 ||
                r.SubmissionAskedDay != -9999 || r.AgreementRefusedDay != -9999 ||
                r.AgreementOfferedKind != AgreementKind.TradeAgreement || r.AgreementOfferedDay != -9999 ||
                r.AgreementLapsedBy != -1 || r.AgreementLapsedDay != -9999 ||
                r.GuaranteeBrokenBy != -1 || r.RivalShiftDay != -9999 || r.RivalShiftPaperBy != -1 ||
                r.RivalShiftPaperDay != -9999 || !string.Equals(r.RivalShiftReason, "", StringComparison.Ordinal) ||
                r.CrisisOpenedDay != -1 ||
                r.DiplomaticActBy != -1 || r.DiplomaticActDay != -9999 || r.DiplomaticActKind != -1 ||
                r.LoanLender != -1 || r.LoanWeekly != 0f || r.LoanMissedWeeks != 0 ||
                r.MissionByA is not null || r.MissionByB is not null ||
                r.TensionStage != 0 || r.TensionStageDay != -9999 ||
                r.QuarrelRefusalDay != 0 || r.QuarrelClaimDay != 0 || r.QuarrelRivalDay != 0 || r.QuarrelBorderDay != 0 ||
                r.LastIncidentDay != -9999 || !string.Equals(r.LastIncident, "", StringComparison.Ordinal) ||
                r.LastIncidentBy != -1 || r.ClashLosses != 0f || r.TensionBaseline != 0f || r.TensionAnchor != -1f ||
                r.TalksBy != -1 || r.TalksUntilDay != -1 || r.LastTalksDay != -9999 ||
                r.MediatorId != -1 || r.LastMediationDay != -9999 ||
                r.InterveningPowerId != -1 || r.InterventionTargetId != -1 || r.InterventionUntilDay != -1 ||
                r.GuaranteeRequestedBy != -1 || r.GuaranteeRequestedDay != -9999 ||
                r.MilitaryAccessByA || r.MilitaryAccessByB ||
                r.MilitaryAccessRequestedBy != -1 || r.MilitaryAccessRequestedDay != -9999 ||
                r.Trade is not null || r.AgreementOfferedTrade is not null ||
                r.AidSentDay >= 0 || r.LastActionDay >= 0)
                return false;
        }
        else
        {
            if (r.Reasons.Count != 0 && !OnlyDerivedReasons(r)) return false;
            var elapsed = c.ElapsedDays;
            if ((r.TruceUntil > 0 && elapsed - r.TruceUntil <= 365) ||
                WithinAge(elapsed, r.AllianceOfferedDay, 365) ||
                WithinAge(elapsed, r.AllianceRefusedDay, 730) ||
                WithinAge(elapsed, r.AllianceSinceDay, 365) ||
                WithinAge(elapsed, r.AllianceAssessmentDay, 365) ||
                WithinAge(elapsed, r.AllianceReaffirmedDay, 365) ||
                WithinAge(elapsed, r.AllianceProgramStarted, 365) ||
                WithinAge(elapsed, r.AllianceProgramLastPaid, 365) ||
                WithinAge(elapsed, r.AllianceProgramPausedDay, 365) ||
                r.AllianceProgramSponsor != -1 && r.AllianceProgramDuration > 0 && elapsed - Math.Max(r.AllianceProgramStarted, r.AllianceProgramLastPaid) <= 365 ||
                WithinAge(elapsed, r.SubmissionAskedDay, 730) ||
                WithinAge(elapsed, r.AgreementRefusedDay, 730) ||
                WithinAge(elapsed, r.AgreementOfferedDay, 365) ||
                (r.AgreementLapsedBy != -1 && (r.AgreementLapsedDay < 0 || elapsed - r.AgreementLapsedDay <= 730)) ||
                r.GuaranteeBrokenBy != -1 ||
                WithinAge(elapsed, r.RivalShiftDay, 365) ||
                (r.RivalShiftPaperBy != -1 && (r.RivalShiftPaperDay < 0 || elapsed - r.RivalShiftPaperDay <= 365)) ||
                r.CrisisOpenedDay >= 0 ||
                r.DiplomaticActBy != -1 || WithinAge(elapsed, r.DiplomaticActDay, 365) || r.DiplomaticActKind != -1 ||
                r.LoanLender != -1 || r.LoanWeekly != 0f || r.LoanMissedWeeks != 0 ||
                r.MissionByA is not null || r.MissionByB is not null ||
                r.TensionStage != 0 || WithinAge(elapsed, r.TensionStageDay, 365) ||
                PositiveWithinAge(elapsed, r.QuarrelRefusalDay, 730) ||
                PositiveWithinAge(elapsed, r.QuarrelClaimDay, 730) ||
                PositiveWithinAge(elapsed, r.QuarrelRivalDay, 730) ||
                PositiveWithinAge(elapsed, r.QuarrelBorderDay, 730) ||
                WithinAge(elapsed, r.LastIncidentDay, 730) ||
                r.TensionBaseline != 0f || r.TensionAnchor != -1f ||
                (r.TalksBy != -1 && (r.TalksUntilDay >= elapsed || WithinAge(elapsed, r.LastTalksDay, 365))) ||
                r.TalksUntilDay >= elapsed || WithinAge(elapsed, r.LastTalksDay, 365) ||
                (r.MediatorId != -1 && (r.LastMediationDay < 0 || elapsed - r.LastMediationDay <= 365)) ||
                WithinAge(elapsed, r.LastMediationDay, 365) ||
                (r.InterveningPowerId != -1 && (r.InterventionUntilDay < 0 || elapsed - r.InterventionUntilDay <= 365)) ||
                r.InterventionUntilDay >= elapsed ||
                r.GuaranteeRequestedBy != -1 || WithinAge(elapsed, r.GuaranteeRequestedDay, 365) ||
                r.MilitaryAccessByA || r.MilitaryAccessByB ||
                r.MilitaryAccessRequestedBy != -1 || WithinAge(elapsed, r.MilitaryAccessRequestedDay, 365) ||
                r.Trade is not null || r.AgreementOfferedTrade is not null ||
                WithinAge(elapsed, r.AidSentDay, 365) || WithinAge(elapsed, r.LastActionDay, 365))
                return false;
        }

        var unknownChecks = UnknownInertFieldChecks();
        for (var i = 0; i < unknownChecks.Length; i++)
        {
            var (field, was) = unknownChecks[i];
            var now = field.GetValue(r);
            if (now is ICollection collection) { if (collection.Count != 0) return false; }
            else if (!Equals(now, was)) return false;
        }
        return true;
    }

    /// <summary>
    /// Weekly opinion settlement writes baseline ideology/identity/reputation affinity onto even
    /// an otherwise untouched row. Removing such a row loses no state, because reopening it
    /// recomputes the same baseline.
    /// </summary>
    private static bool HasDerivedOrZeroOpinion(Campaign c, DiplomaticRelation r, bool allowAgedActivityDays = true)
    {
        if (MathF.Abs(r.Opinion) < 0.01f && r.Reasons.Count == 0) return true;
        var left = c.NationById(r.A);
        var right = c.NationById(r.B);
        if (left is null || right is null) return true;
        var borders = c.ProvinceMap is not null && left.MapIndex > 0 && right.MapIndex > 0 &&
                      CampaignDiplomacy.SharesLandBorder(c, left.MapIndex, right.MapIndex);
        if (r.Reasons.Count > 0)
        {
            if (!allowAgedActivityDays || borders || !OnlyDerivedReasons(r)) return false;
        }
        var affinity = PoliticsCore.Affinity(c, left, right);
        var identity = StateIdentity.Affinity(c, left, right);
        var memory = SocialMemory.OpinionEffect(c, left, right);
        var baseline = 0f;
        if (MathF.Abs(affinity) >= 0.05f) baseline += affinity;
        if (identity >= 0.05f) baseline += identity;
        if (MathF.Abs(memory) >= 0.05f) baseline += memory;
        if (r.Reasons.Count > 0)
        {
            for (var i = 0; i < r.Reasons.Count; i++)
                baseline += OpinionSystem.Weight(c, r.Reasons[i]);
        }
        baseline = M.Clamp(baseline, -100f, 100f);
        var tolerance = allowAgedActivityDays && !borders ? 1.5f : 0.05f;
        return MathF.Abs(r.Opinion - baseline) < tolerance;
    }

    private static bool IsInertForMovement(Campaign c, DiplomaticRelation r) =>
        CarriesNothingExceptOpinion(c, r, allowAgedActivityDays: false) && HasDerivedOrZeroOpinion(c, r, allowAgedActivityDays: false);

    private static bool IsInertNeutralRow(Campaign c, DiplomaticRelation r) =>
        CarriesNothingExceptOpinion(c, r, allowAgedActivityDays: true) &&
        (GoneNow(r.A) || GoneNow(r.B) || HasDerivedOrZeroOpinion(c, r, allowAgedActivityDays: true));

    [ThreadStatic] private static List<(long Key, bool Drop)> _movingPairsScratch;

    /// <summary>Once a week, at the day's start: the dead go to sleep and the restored wake.</summary>
    public static void Sweep(Campaign c)
    {
        // Weekly: the flag is saved, so a load restores exactly what the running game had; and
        // anything that writes to a sleeping relation wakes it at once (CampaignDiplomacy.Get).
        if (c?.Defines is not { RelationDormancyEnabled: true } || c.ElapsedDays % 7 != 0) return;
        if (c.ElapsedDays % 364 == 0) ForgetSpentCooldowns(c);
        var nations = c.Nations;
        var nationCount = nations.Count;
        var gone = _gone;
        var landless = _landlessMovement;
        if (gone is null || gone.Length < nationCount) _gone = gone = new bool[nationCount + 64];
        if (landless is null || landless.Length < nationCount) _landlessMovement = landless = new bool[nationCount + 64];
        Array.Clear(gone, 0, nationCount);
        Array.Clear(landless, 0, nationCount);
        for (var i = 0; i < nationCount; i++)
        {
            var n = nations[i];
            if (n is null) continue;
            var id = n.Id;
            if ((uint)id >= (uint)nationCount) continue;
            gone[id] = n.Population <= 0 && n.Movement is not { Dissolved: false };
            landless[id] = n.Population <= 0 && n.Movement is { Dissolved: false, Landless: true };
        }
        var monthlyPrune = c.ElapsedDays > 0 && c.ElapsedDays % 28 == 0;
        var movingPairs = _movingPairsScratch ??= new List<(long Key, bool Drop)>();
        movingPairs.Clear();
        foreach (var (key, relation) in c.Relations)
        {
            var eitherGone = GoneNow(relation.A) || GoneNow(relation.B);
            if (eitherGone && !Keep(relation))
            {
                movingPairs.Add((key, IsInertNeutralRow(c, relation)));
            }
            else if ((LandlessMovementNow(relation.A) || LandlessMovementNow(relation.B)) && IsInertForMovement(c, relation))
            {
                movingPairs.Add((key, false));
            }
            else if (monthlyPrune && IsInertNeutralRow(c, relation))
            {
                movingPairs.Add((key, true));
            }
        }
        for (var i = 0; i < movingPairs.Count; i++)
        {
            var (key, drop) = movingPairs[i];
            var relation = c.Relations[key];
            c.Relations.Remove(key);
            DiplomaticOutreach.NoteMissionTable(c, relation, -1);
            if (relation.Stance == DiplomaticStance.Allied || relation.GuaranteeByA || relation.GuaranteeByB) c.NoteAllianceChange();
            // Drop completely inert neutral rows instead of archiving empty defaults forever.
            if (drop) continue;
            relation.Dormant = true;
            c.DormantRelations[key] = relation;
        }
        movingPairs.Clear();
        if (c.DormantRelations.Count == 0) return;
        var moving = Moving;
        moving.Clear();
        foreach (var (key, relation) in c.DormantRelations)
            if (!GoneNow(relation.A) && !GoneNow(relation.B) &&
                (!(LandlessMovementNow(relation.A) || LandlessMovementNow(relation.B)) || !IsInertForMovement(c, relation)))
                moving.Add(key);
        for (var i = 0; i < moving.Count; i++) Wake(c, moving[i]);
        moving.Clear();
        if (monthlyPrune && c.DormantRelations.Count > 0)
        {
            var kept = _keptScratch ??= new List<KeyValuePair<long, DiplomaticRelation>>();
            kept.Clear();
            var prunedAny = false;
            foreach (var entry in c.DormantRelations)
            {
                var relation = entry.Value;
                if ((GoneNow(relation.A) || GoneNow(relation.B)) && IsInertNeutralRow(c, relation))
                {
                    prunedAny = true;
                    continue;
                }
                kept.Add(entry);
            }
            if (prunedAny)
            {
                // Empty and refill in original order so host and loaded guest keep identical bucket layout.
                c.DormantRelations.Clear();
                for (var i = 0; i < kept.Count; i++)
                    c.DormantRelations.Add(kept[i].Key, kept[i].Value);
                MovementAi.ForgetRelationIndex(c);
            }
            kept.Clear();
        }
    }

    [ThreadStatic] private static List<string> _spentScratch;

    /// <summary>
    /// Once a year: a cooldown that ran out more than a month ago is forgotten. Every reader asks
    /// whether its day is still to come, and a missing one reads as "now" - the same answer - but
    /// a campaign of centuries kept every key it ever set (a thousand new ones a year). The two
    /// kinds that remember a past day rather than a future one are kept.
    /// </summary>
    private static void ForgetSpentCooldowns(Campaign c)
    {
        var spent = _spentScratch ??= new List<string>();
        var before = c.ElapsedDays - 30;
        var active = c.ActiveNations;
        for (var i = 0; i < active.Count; i++)
        {
            var nation = active[i];
            if (nation.DecisionAvailableDay.Count == 0) continue;
            spent.Clear();
            foreach (var (key, day) in nation.DecisionAvailableDay)
                if (day < before && !key.StartsWith(RevoltRecovery.Prefix, StringComparison.Ordinal) &&
                    !key.StartsWith(SecessionResponse.Prefix, StringComparison.Ordinal)) spent.Add(key);
            foreach (var key in spent) nation.DecisionAvailableDay.Remove(key);
        }
    }

    /// <summary>Back into the daily table.</summary>
    internal static DiplomaticRelation Wake(Campaign c, long key)
    {
        if (!c.DormantRelations.Remove(key, out var relation)) return null;
        relation.Dormant = false;
        c.Relations[key] = relation;
        c.RelationInsertSink?.Add(relation);
        DiplomaticOutreach.NoteMissionTable(c, relation, 1);
        if (relation.Stance == DiplomaticStance.Allied || relation.GuaranteeByA || relation.GuaranteeByB) c.NoteAllianceChange();
        // Its opinion drivers are worked out by the weekly opinion pass like every other relation's.
        return relation;
    }
}
