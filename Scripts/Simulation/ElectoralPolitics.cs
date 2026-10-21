using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool ElectionsEnabled { get; set; } = true;
    public int ElectionHandoverDays { get; set; } = 30;
    public int ElectionPostponeDays { get; set; } = 180;
    public float ElectionPostponePoliticalCost { get; set; } = 20;
    public float ElectionSuspendPoliticalCost { get; set; } = 50;
    public float ElectionRefusePoliticalCost { get; set; } = 40;
    public float ElectionResumePoliticalCost { get; set; } = 10;
    public float ElectionControlAdvantage { get; set; } = .22f;
    public float ElectionTermFatigue { get; set; } = .035f;
    public float ElectionDisputeRadicalism { get; set; } = .08f;
}

public sealed class ElectionState
{
    public string Government = "";
    public float Control;
    public int Round;
    public int IncumbentTerms;
    public string IncumbentName = "";
    public int LastDay = -1;
    public float IncumbentVote;
    public string Result = "";
    public bool Suspended;
    public int LastPostponedRound = -1;
    public int TransferOnDay = -1;
    public string Winner = "";
    public string WinnerTrait = "";
    public int WinnerBirthYear;
    public int DisputedUntilDay = -1;
    public int LastControlReviewDay = -1;
}

public sealed partial class Nation { public ElectionState Elections; }
public enum ElectionAction { AcceptResult, RefuseResult, Postpone, Suspend, Resume }

/// <summary>
/// Cause: constitutional term expires. Facts: public approval, institutional control,
/// fatigue and a reproducible contest. Consequence: renewal or a named successor; rejecting
/// a defeat damages legitimacy. Action: hand over, dispute, postpone once, suspend or resume.
/// Parliament elects the government; it never replaces a hereditary/lifetime head of state.
/// </summary>
public static class ElectoralPolitics
{
    private static Dictionary<string, float> _initial;
    public static void ResetCatalog() => _initial = null;
    public static bool Enabled(Campaign c) => c is not null && c.Defines.RulersEnabled &&
        c.Defines.ElectionsEnabled && c.FeatureEnabled("rulers");
    public static bool Eligible(Nation n)
    {
        if (n is null || n.Movement is not null) return false;
        var office = Rulers.OfficeOf(n);
        return office.TermDays > 0 && (office.Dual || !office.HereditaryState && !office.LifeState);
    }
    private static LeaderProfile Executive(Campaign c, Nation n) => Rulers.OfficeOf(n).Dual
        ? Rulers.HeadOfGovernment(c, n) : Rulers.HeadOfState(c, n);
    private static float InstitutionalBase(Nation n) => Math.Clamp(
        (PoliticsCore.GovernmentOf(n).Centralisation - .4f) * .7f +
        Math.Max(0, PoliticsCore.IdeologyOf(n).Authority) * .65f, 0, .95f);

    private static float InitialControl(Nation n)
    {
        if (n.ReleasedFromId >= 0 || n.SecededFromId >= 0 || !string.IsNullOrWhiteSpace(n.FormedNationId) ||
            n.StartGovernment.Length > 0 && n.StartGovernment != n.Government ||
            n.StartIdeology.Length > 0 && n.StartIdeology != n.Ideology) return InstitutionalBase(n);
        if (_initial is null)
        {
            _initial = new(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in GameDataLoader.DataDocuments("common", "elections"))
            using (doc)
                if (doc.RootElement.TryGetProperty("initial_control", out var rows) && rows.ValueKind == JsonValueKind.Object)
                    foreach (var row in rows.EnumerateObject())
                        if (row.Value.ValueKind == JsonValueKind.Number && row.Value.TryGetSingle(out var v) && float.IsFinite(v))
                            _initial[row.Name] = Math.Clamp(v, 0, 1);
        }
        return _initial.TryGetValue(n.Tag ?? "", out var value) ? value : InstitutionalBase(n);
    }

    public static float Approval(Campaign c, Nation n)
    {
        var mood = PoliticalCohorts.Mood(c, n);
        return mood.Reviewed ? mood.Government : Math.Clamp(.32f + n.Stability * .3f + n.WarSupport * .15f - n.ShortageIndex * .2f, 0, 1);
    }

    public static void Tick(Campaign c)
    {
        if (!Enabled(c)) return;
        foreach (var n in c.Nations)
        {
            if (n.Movement is not null || n.Population <= 0 || c.Fallen.Contains(n.Id)) continue;
            if (!Eligible(n))
            {
                if (n.Elections is { } former && former.Government != n.Government)
                {
                    former.Government = n.Government; former.TransferOnDay = -1;
                    former.Winner = ""; former.Suspended = true; n.NextElectionDay = -1;
                    c.MarkStrategicChange();
                }
                continue;
            }
            var office = Rulers.OfficeOf(n); var e = n.Elections;
            if (e is null)
            {
                n.Elections = e = new ElectionState { Government = n.Government, Control = InitialControl(n) };
                var since = office.Dual ? n.HeadOfGovernmentSinceDay : n.HeadOfStateSinceDay;
                // An overdue legacy save gets one election, not decades of catch-up rolls.
                if (n.NextElectionDay < 0) n.NextElectionDay = Math.Max(c.ElapsedDays + 1, Math.Max(0, since) + office.TermDays);
            }
            if (e.Government != n.Government)
            {
                e.Government = n.Government; e.Control = InstitutionalBase(n); e.Suspended = false;
                e.TransferOnDay = -1; e.Winner = ""; e.IncumbentTerms = 0;
                n.NextElectionDay = c.ElapsedDays + Math.Max(1, office.TermDays);
            }
            if (c.ElapsedDays % 30 == 17 && e.LastControlReviewDay != c.ElapsedDays)
            {
                e.Control = Math.Clamp(e.Control + (InstitutionalBase(n) - e.Control) * .01f -
                    (Approval(c, n) < .35f ? .008f : 0), 0, 1);
                e.LastControlReviewDay = c.ElapsedDays;
            }
            if (e.TransferOnDay >= 0)
            {
                if (c.ElapsedDays >= e.TransferOnDay) Transfer(c, n);
                continue;
            }
            if (e.Suspended) continue;
            if (!n.IsHuman && n.NextElectionDay - c.ElapsedDays is >= 0 and <= 30 &&
                e.LastPostponedRound != e.Round && c.Wars.Any(w => !w.Ended && w.Involves(n.Id) &&
                    (w.CivilConflict || (w.IsAttacking(n.Id) ? w.WarScore : -w.WarScore) < -30)))
                new ElectionCommand { IssuerId = n.Id, Action = ElectionAction.Postpone }.Apply(c);
            if (c.ElapsedDays >= n.NextElectionDay) Hold(c, n);
        }
    }

    private static void Hold(Campaign c, Nation n)
    {
        var e = n.Elections; var executive = Executive(c, n);
        if (e.IncumbentName != executive.Name) e.IncumbentTerms = 0;
        e.IncumbentName = executive.Name;
        var noise = (Escalation.Roll(c.Seed, n.Id, 0xE1EC, e.Round, c.ElapsedDays) - .5f) * .28f;
        e.IncumbentVote = Math.Clamp(.5f + (Approval(c, n) - .55f) * .6f +
            e.Control * c.Defines.ElectionControlAdvantage -
            Math.Min(8, e.IncumbentTerms) * c.Defines.ElectionTermFatigue * (1f - e.Control) + noise, .05f, .95f);
        e.Round++; e.LastDay = c.ElapsedDays;
        n.NextElectionDay = c.ElapsedDays + Math.Max(1, Rulers.OfficeOf(n).TermDays);
        if (e.IncumbentVote >= .5f)
        {
            e.IncumbentTerms++; e.Result = "Incumbent re-elected";
            c.Chronicle.Record(c, "election_renewed", n.Id, place: executive.Name);
        }
        else
        {
            var candidate = Rulers.CandidatePool(c, n, 4).FirstOrDefault(p => p.Name != executive.Name);
            e.Winner = candidate.Name ?? Rulers.GenerateName(n, e.Round + 3000, false, c.Seed);
            e.WinnerTrait = candidate.TraitId ?? "reformer";
            e.WinnerBirthYear = candidate.BirthYear > 0 ? candidate.BirthYear : c.Date.Year - 45;
            e.TransferOnDay = c.ElapsedDays + Math.Clamp(c.Defines.ElectionHandoverDays, 1, 365);
            e.Result = "Opposition victory; handover pending";
            c.Chronicle.Record(c, "election_lost", n.Id, place: e.Winner);
            if (!n.IsHuman)
            {
                var defy = e.Control > .60f && Escalation.Roll(c.Seed, n.Id, 0xDEFA, e.Round, c.ElapsedDays) <
                    e.Control * (executive.TraitId is "iron_hand" or "hawk" or "ideologue" ? .8f : .4f);
                new ElectionCommand { IssuerId = n.Id, Action = defy ? ElectionAction.RefuseResult : ElectionAction.AcceptResult }.Apply(c);
            }
        }
        c.MarkStrategicChange();
    }

    public static bool MayAct(Campaign c, Nation n, ElectionAction action, out float cost, out string why)
    {
        cost = 0; why = "";
        if (!Enabled(c) || !Eligible(n) || n.Elections is not { } e || c.Fallen.Contains(n.Id))
        { why = Loc.Get("No active electoral constitution."); return false; }
        if (!Enum.IsDefined(typeof(ElectionAction), action)) { why = Loc.Get("Unknown election action."); return false; }
        var pending = e.TransferOnDay >= 0 && e.Winner.Length > 0;
        if (action is ElectionAction.AcceptResult or ElectionAction.RefuseResult)
        {
            if (!pending) { why = Loc.Get("There is no defeated incumbent awaiting a handover."); return false; }
            if (action == ElectionAction.RefuseResult) cost = c.Defines.ElectionRefusePoliticalCost;
        }
        else
        {
            if (pending) { why = Loc.Get("Resolve the election result before changing the calendar."); return false; }
            if (action == ElectionAction.Resume ? !e.Suspended : e.Suspended)
            { why = Loc.Get("This election calendar action is not currently available."); return false; }
            if (action == ElectionAction.Postpone && e.LastPostponedRound == e.Round)
            { why = Loc.Get("This electoral term has already been postponed once."); return false; }
            cost = action switch { ElectionAction.Postpone => c.Defines.ElectionPostponePoliticalCost,
                ElectionAction.Suspend => c.Defines.ElectionSuspendPoliticalCost, _ => c.Defines.ElectionResumePoliticalCost };
        }
        if (!float.IsFinite(cost) || cost < 0 || !float.IsFinite(n.PoliticalPoints) || n.PoliticalPoints < cost)
        { why = Loc.Format("Requires {0:0.#} political points.", cost); return false; }
        return true;
    }

    public static void Act(Campaign c, Nation n, ElectionAction action)
    {
        if (!MayAct(c, n, action, out var cost, out _)) return;
        n.PoliticalPoints -= cost; var e = n.Elections;
        switch (action)
        {
            case ElectionAction.AcceptResult: Transfer(c, n); return;
            case ElectionAction.RefuseResult:
                e.TransferOnDay = -1; e.Result = "Incumbent refused to leave";
                e.DisputedUntilDay = c.ElapsedDays + 730; e.Control = Math.Min(1, e.Control + .08f);
                Discontent(c, n, 1f); c.Chronicle.Record(c, "election_refused", n.Id, place: e.Winner); break;
            case ElectionAction.Postpone:
                n.NextElectionDay = Math.Max(c.ElapsedDays + 1, n.NextElectionDay) + Math.Clamp(c.Defines.ElectionPostponeDays, 1, 365);
                e.LastPostponedRound = e.Round; e.Result = "Election postponed";
                Discontent(c, n, .25f); c.Chronicle.Record(c, "election_postponed", n.Id); break;
            case ElectionAction.Suspend:
                e.Suspended = true; e.Result = "Elections suspended"; e.Control = Math.Min(1, e.Control + .12f);
                Discontent(c, n, .75f); c.Chronicle.Record(c, "election_suspended", n.Id); break;
            case ElectionAction.Resume:
                e.Suspended = false; n.NextElectionDay = c.ElapsedDays + Math.Clamp(c.Defines.ElectionHandoverDays, 1, 365);
                e.Result = "Elections restored"; e.Control = Math.Max(0, e.Control - .1f);
                c.Chronicle.Record(c, "election_resumed", n.Id); break;
        }
        c.MarkStrategicChange();
    }

    private static void Discontent(Campaign c, Nation n, float strength)
    {
        n.Stability = Math.Clamp(n.Stability - .08f * strength, 0, 1);
        foreach (var s in c.StatesOf(n))
        {
            s.Radicalism = Math.Clamp(s.Radicalism + Math.Clamp(c.Defines.ElectionDisputeRadicalism, 0, 1) * strength, 0, 1);
            if (s.GovernmentApproval >= 0) s.GovernmentApproval = Math.Max(0, s.GovernmentApproval - .18f * strength);
        }
    }
    private static void Transfer(Campaign c, Nation n)
    {
        var e = n.Elections;
        if (e is null || e.TransferOnDay < 0 || e.Winner.Length == 0) return;
        if (Rulers.OfficeOf(n).Dual)
        {
            n.HeadOfGovernmentName = e.Winner; n.HeadOfGovernmentTrait = e.WinnerTrait;
            n.HeadOfGovernmentBirthYear = e.WinnerBirthYear; n.HeadOfGovernmentSinceDay = c.ElapsedDays;
        }
        else
        {
            n.HeadOfStateName = e.Winner; n.HeadOfStateTrait = e.WinnerTrait;
            n.HeadOfStateBirthYear = e.WinnerBirthYear; n.HeadOfStateSinceDay = c.ElapsedDays;
        }
        e.TransferOnDay = -1; e.IncumbentTerms = 0; e.IncumbentName = e.Winner;
        e.Result = "Peaceful transfer of power"; e.DisputedUntilDay = -1; e.Control = Math.Max(0, e.Control - .05f);
        n.RulerGeneration++; NationalModifiers.Invalidate(n);
        c.Chronicle.Record(c, "election_transfer", n.Id, place: e.Winner); c.MarkStrategicChange();
    }
}

public sealed class ElectionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public ElectionAction Action { get; init; }
    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        ElectoralPolitics.Act(c, c.NationById(IssuerId), Action);
    }
}
