using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Ultimatums: the demand that arrives before the war does — "give us what we want,
/// or we take it". The name of the game in one mechanic.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — the full package a country can demand at gunpoint:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// An ultimatum is a PACKAGE — you can ask for several things on the same paper:
///
///   LAND: up to UltimatumMaxRegions (8), but never occupied land and never all of it.
///   MONEY: up to UltimatumMaxTreasuryShare (50%) of their treasury.
///   TREATIES: lift their embargo; sign a non-aggression pact.
///   THE COUNTRY ITSELF: annexation (the whole thing, nothing else on the paper).
///   VASSALISE: it keeps its flag, answers to you.
///   DISARM: its army is cut for three years.
///   BREAK ALLIANCES: every alliance it holds is torn up.
///   RELEASE SUBJECTS: every country answering to it is let go.
///   TECHNOLOGY: its current research, handed over for two years.
///   CHANGE GOVERNMENT: its government is replaced with one of your kind.
///   FLEET: half its fleet, struck or surrendered.
///
/// THE TARGET ASSESSES: what it costs to accept vs what it costs to refuse (the
/// demander's strength, the target's stability, how angry the target is). A target
/// that thinks it can win the war refuses.
///
/// THE ANSWER: accept and it happens; refuse and there's a 3-day truce before
/// the war can begin (the demander may walk away; the target may not).
/// Silence = refusal. A player is never auto-answered.
///
/// ANNEXATION is special: the demander must be 1.75× the target's strength, and
/// nothing else can be on the paper.
///
/// WHAT TO CHANGE:
///   • Easier ultimatums → lower UltimatumAnnexStrengthRatio to 1.2.
///   • Less land per ultimatum → lower UltimatumMaxRegions to 3.
///   • No annexation ever → set UltimatumAnnexStrengthRatio to 10.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed partial class Defines
{
    public int UltimatumMaxRegions { get; set; } = 8;
    public float UltimatumMaxTreasuryShare { get; set; } = .5f;
    public int UltimatumRefusalTruceDays { get; set; } = 3;
    /// <summary>Days a player has to answer an ultimatum. Silence refuses it. Nothing is handed over.</summary>
    public int UltimatumAnswerDays { get; set; } = 21;
    public int UltimatumHistoryLimit { get; set; } = 32;

    /// <summary>How long a disarmed country stays disarmed after signing.</summary>
    public int UltimatumDisarmamentDays { get; set; } = 1095;

    /// <summary>How long the drawings handed over in a technology demand keep working.</summary>
    public int UltimatumTechnologyDays { get; set; } = 730;

    /// <summary>What share of a fleet changes hands when a fleet is demanded.</summary>
    public float UltimatumFleetShare { get; set; } = 0.5f;

    /// <summary>
    /// How much stronger the demander must be before a demand for the whole country is even
    /// worth putting on paper: annexing a country is not a border adjustment.
    /// </summary>
    public float UltimatumAnnexStrengthRatio { get; set; } = 1.75f;
}

public static partial class Ultimatum
{
    public sealed record Demand
    {
        public int[] Regions { get; init; } = Array.Empty<int>();
        // The campaign's money unit is billions, not raw currency units.
        public float Reparations { get; init; }
        public bool LiftEmbargo { get; init; }
        public bool NonAggression { get; init; }

        // The rest of the things a government can be made to sign. Each one is a term of the
        // same package: it is priced in the same burden, refused by the same assessment, and
        // applied by the same resolution, so an ultimatum can ask for almost anything and the
        // answer is still one answer.
        /// <summary>The whole country, not a province of it: the target ceases to exist as a state.</summary>
        public bool Annex { get; init; }
        /// <summary>The country keeps its flag and its army, and answers to us about both.</summary>
        public bool Vassalise { get; init; }
        /// <summary>Its army is cut down to a police force for the length of the treaty.</summary>
        public bool Disarm { get; init; }
        /// <summary>Every alliance it holds is torn up.</summary>
        public bool BreakAlliances { get; init; }
        /// <summary>Every country that answers to it is let go.</summary>
        public bool ReleaseSubjects { get; init; }
        /// <summary>The drawings it is working from, handed over.</summary>
        public bool TechnologyTransfer { get; init; }
        /// <summary>Its government is replaced by one of our own colour.</summary>
        public bool ChangeGovernment { get; init; }
        /// <summary>Half of its fleet, struck or surrendered.</summary>
        public bool FleetTransfer { get; init; }
        /// <summary>Which dependency, when <see cref="Vassalise"/> is set. Empty means a puppet.</summary>
        public string SubjectKind { get; init; } = "";
        public bool TradeAgreement { get; init; }
        /// <summary>The target embargoes <see cref="EmbargoOnId"/>. The bool is the demand; id 0 is a real country.</summary>
        public bool EmbargoThird { get; init; }
        public int EmbargoOnId { get; init; }
        /// <summary>Break the alliance with <see cref="BreakWithId"/> only. Distinct from breaking every alliance.</summary>
        public bool BreakNamed { get; init; }
        public int BreakWithId { get; init; }
        public bool CancelGuarantee { get; init; }
        public int CancelGuaranteeId { get; init; }
        public bool JoinAlliance { get; init; }
        public bool AcceptGuarantee { get; init; }
        public bool MilitaryAccess { get; init; }
        public bool WhitePeace { get; init; }
        public bool CedeOccupied { get; init; }
        public bool Humiliate { get; init; }
        public bool ReleaseNamed { get; init; }
        public int ReleaseSubjectId { get; init; }
        public bool Liberate { get; init; }
        public int LiberateRegion { get; init; }
    }

    public sealed record Assessment(bool Valid, string Reason, Answer Response, float StrengthRatio, float TerritoryShare);

    public static int[] RegionIds(Demand demand) => (demand?.Regions ?? Array.Empty<int>()).Distinct().OrderBy(id => id).ToArray();

    /// <summary>Whether the paper asks for anything at all.</summary>
    public static bool HasAnyTerm(Demand demand) =>
        demand is not null &&
        (RegionIds(demand).Length > 0 || demand.Reparations > 0f || demand.LiftEmbargo || demand.NonAggression ||
         demand.Annex || demand.Vassalise || demand.Disarm || demand.BreakAlliances ||
         demand.ReleaseSubjects || demand.TechnologyTransfer || demand.ChangeGovernment || demand.FleetTransfer ||
         demand.TradeAgreement || demand.EmbargoThird || demand.BreakNamed || demand.CancelGuarantee ||
         demand.JoinAlliance || demand.AcceptGuarantee || demand.MilitaryAccess || demand.WhitePeace ||
         demand.CedeOccupied || demand.Humiliate || demand.ReleaseNamed || demand.Liberate);

    public static Assessment Assess(Campaign c, Nation demander, Nation target, Demand demand)
    {
        if (!Validate(c, demander, target, demand, out var reason)) return new(false, reason, Answer.Refuse, 0, 0);
        var ids = RegionIds(demand).ToHashSet();
        var population = c.States.Where(s => ids.Contains(s.MapIndex)).Sum(s => (double)s.Population);
        if (demand.Liberate && c.States.Find(s => s.MapIndex == demand.LiberateRegion) is { } freed)
            population += freed.Population;
        var share = demand.Annex ? 1f : (float)Math.Clamp(population / Math.Max(1, target.Population), 0, 1);
        var mine = AiStatecraft.StrengthOf(c, target);
        var theirs = AiStatecraft.StrengthOf(c, demander);
        var defenders = Statecraft.DefendersOf(c, target);
        mine *= 1f + MathF.Min(Math.Max(0, defenders) * .15f, .6f);
        var ratio = theirs / MathF.Max(.01f, mine);
        var moneyShare = demand.Reparations / MathF.Max(.01f, target.Treasury);
        // Larger packages are harder to swallow. A preview explains the actual rule;
        // this is not a fabricated percentage chance or a separate UI calculation.
        var burden = 1f + share + moneyShare * .5f + (demand.NonAggression ? .15f : 0f);
        // Every further term is another thing to swallow. Annexation is the whole country and
        // is priced as such: nothing short of overwhelming strength makes it an answer.
        if (demand.Annex) burden += 3f;
        if (demand.Vassalise) burden += 1.4f;
        if (demand.Disarm) burden += .5f;
        if (demand.BreakAlliances || demand.BreakNamed) burden += .35f;
        if (demand.ReleaseSubjects || demand.ReleaseNamed) burden += .45f;
        if (demand.TechnologyTransfer) burden += .25f;
        if (demand.ChangeGovernment) burden += .8f;
        if (demand.FleetTransfer) burden += .35f;
        if (demand.TradeAgreement) burden += .15f;
        if (demand.EmbargoThird) burden += .2f;
        if (demand.CancelGuarantee) burden += .2f;
        if (demand.JoinAlliance) burden += .35f;
        if (demand.AcceptGuarantee) burden += .15f;
        if (demand.MilitaryAccess) burden += .2f;
        if (demand.WhitePeace) burden += .25f;
        if (demand.CedeOccupied) burden += .6f;
        if (demand.Humiliate) burden += .3f;
        if (demand.Liberate) burden += .5f;
        var relation = CampaignDiplomacy.Peek(c, demander.Id, target.Id);
        if (relation is not null && relation.Opinion <= -c.Defines.UltimatumRefuseGrudge) burden *= 1.25f;
        var yield = mine <= .01f || ratio > c.Defines.UltimatumYieldStrengthRatio * burden ||
            target.Stability < c.Defines.UltimatumYieldStability / burden;
        // A full annexation is not bought with a bad week. The target yields only when it is
        // already negligible, or the demander's strength clears the annexation ratio.
        if (demand.Annex)
        {
            var overwhelming = ratio >= MathF.Max(c.Defines.UltimatumAnnexStrengthRatio,
                c.Defines.UltimatumYieldStrengthRatio * burden);
            yield = mine <= .01f || overwhelming;
        }
        return new(true, "", yield ? Answer.Yield : Answer.Refuse, ratio, share);
    }

    public static bool Validate(Campaign c, Nation demander, Nation target, Demand demand, out string reason)
        => ValidateCore(c, demander, target, demand, out reason, allowHuman: false);

    /// <summary>The package itself, including one addressed to a player. A player is not answered here.</summary>
    internal static bool ValidateTerms(Campaign c, Nation demander, Nation target, Demand demand, out string reason)
        => ValidateCore(c, demander, target, demand, out reason, allowHuman: true);

    private static bool ValidateCore(Campaign c, Nation demander, Nation target, Demand demand, out string reason, bool allowHuman)
    {
        reason = "";
        if (c?.ProvinceMap is null || demander is null || target is null || demander.Id == target.Id || demand is null)
        { reason = "Invalid target."; return false; }
        var endingWar = demand.WhitePeace || demand.CedeOccupied;
        if (demand.WhitePeace && demand.CedeOccupied)
            return Rejected("Choose one peace: the pre-war line, or the ground each side holds.", out reason);
        if (!endingWar && !CampaignDiplomacy.CanDeclareWar(c, demander.Id, target.Id, out reason)) return false;
        if (!allowHuman && target.IsHuman) return Rejected("Human-controlled countries cannot be forced to answer automatically.", out reason);
        var ids = RegionIds(demand).ToHashSet();
        if (ids.Count > Math.Clamp(c.Defines.UltimatumMaxRegions, 1, 100)) return Rejected("Too many regions in this demand.", out reason);
        if (!float.IsFinite(demand.Reparations) || demand.Reparations < 0 || demand.Reparations >
            Math.Max(0, target.Treasury) * Math.Clamp(c.Defines.UltimatumMaxTreasuryShare, 0, 1))
            return Rejected("Reparations exceed the allowed share of their treasury.", out reason);
        if (ids.Count == 0 && demand.Reparations <= 0 && !demand.LiftEmbargo && !demand.NonAggression &&
            !HasAnyTerm(demand))
            return Rejected("Select at least one demand.", out reason);

        // A demand for the whole country and a demand for a province of it cannot both be on
        // one paper: the first already includes the second.
        if (demand.Annex && (ids.Count > 0 || demand.Vassalise || demand.ReleaseSubjects || demand.ReleaseNamed ||
                             demand.Liberate || demand.Disarm || demand.ChangeGovernment || demand.FleetTransfer ||
                             demand.TradeAgreement || demand.JoinAlliance || demand.WhitePeace || demand.CedeOccupied ||
                             demand.MilitaryAccess || demand.AcceptGuarantee))
            return Rejected("Annexation is the whole demand: nothing else is left to ask for.", out reason);
        if (demand.Annex && target.IsSubject && target.OverlordId != demander.Id)
            return Rejected("They answer to somebody else: their overlord is the one to ask.", out reason);
        if (demand.Annex && !UltimatumAnnexation.Eligible(c, target))
            return Rejected("Full annexation requires a living country at peace with all its territory under its own control.", out reason);
        if (endingWar && (ids.Count > 0 || demand.Annex || demand.Vassalise || demand.Liberate || demand.ReleaseNamed ||
                          demand.ReleaseSubjects || HasTreatyTerm(demand)))
            return Rejected("End the war on its own. Do not add another settlement to the same paper.", out reason);
        if (endingWar && !PeaceBetween(c, demander, target, demand.CedeOccupied ? PeaceTerms.StatusQuo : PeaceTerms.WhitePeace, out reason))
            return false;
        if (demand.Vassalise && (target.IsSubject || demander.AnswersTo(target.Id)))
            return Rejected("They already answer to somebody, or we answer to them.", out reason);
        if (demand.Vassalise && !SubjectCatalog.Knows(SubjectKindOf(demand)))
            return Rejected("There is no such arrangement to impose.", out reason);
        if (demand.Vassalise && SubjectCatalog.Of(SubjectKindOf(demand)) is { Imposable: false })
            return Rejected("There is no such arrangement to impose.", out reason);
        if (demand.Disarm && !c.Armies.Any(army => army.NationId == target.Id))
            return Rejected("They have no army left to disband.", out reason);
        if (demand.BreakAlliances && !c.Relations.Values.Any(r => r.Stance == DiplomaticStance.Allied &&
                (r.A == target.Id || r.B == target.Id)))
            return Rejected("They are in no alliance to break.", out reason);
        if (demand.ReleaseSubjects && !c.Nations.Any(other => other.AnswersTo(target.Id)))
            return Rejected("Nobody answers to them.", out reason);
        if (demand.ChangeGovernment && string.Equals(target.Government, demander.Government, StringComparison.OrdinalIgnoreCase))
            return Rejected("They already have our form of government.", out reason);
        if (demand.FleetTransfer && !c.Fleets.Any(fleet => fleet.NationId == target.Id))
            return Rejected("They have no fleet to hand over.", out reason);
        if (demand.TechnologyTransfer && !HasResearch(target))
            return Rejected("They have no research to hand over.", out reason);
        if (demand.TradeAgreement && CampaignDiplomacy.Peek(c, demander.Id, target.Id)?.TradeAgreement == true)
            return Rejected("A trade agreement is already in force.", out reason);
        if (demand.EmbargoThird && !EmbargoTarget(c, demander, target, demand.EmbargoOnId, out reason)) return false;
        if (demand.BreakNamed && !AlliedWith(c, target, demand.BreakWithId, out reason)) return false;
        if (demand.CancelGuarantee && !Guarantees(c, target, demand.CancelGuaranteeId, out reason)) return false;
        if (demand.JoinAlliance && CampaignDiplomacy.Peek(c, demander.Id, target.Id)?.Stance == DiplomaticStance.Allied)
            return Rejected("They are already allied with us.", out reason);
        if (demand.JoinAlliance && (target.IsSubject || demander.IsSubject))
            return Rejected("A country that answers to somebody else does not sign its own alliances.", out reason);
        if (demand.AcceptGuarantee && CampaignDiplomacy.Peek(c, demander.Id, target.Id)?.GuaranteedBy(demander.Id) == true)
            return Rejected("We already guarantee them.", out reason);
        if (demand.MilitaryAccess && CampaignDiplomacy.Peek(c, demander.Id, target.Id)?.GrantsMilitaryAccess(target.Id) == true)
            return Rejected("They already grant us military access.", out reason);
        if (demand.ReleaseNamed)
        {
            var heldSubject = c.NationById(demand.ReleaseSubjectId);
            if (heldSubject is null || !heldSubject.AnswersTo(target.Id))
                return Rejected("They do not hold that subject.", out reason);
        }
        if (demand.Liberate && !CountryRelease.CanRelease(c, target, new CountryRelease.ReleaseOptions
            {
                AsSubject = false,
                StateMapIndices = new List<int> { demand.LiberateRegion }
            }, out reason))
            return false;
        var relation = CampaignDiplomacy.Get(c, demander.Id, target.Id);
        if (demand.LiftEmbargo && !relation.EmbargoBy(target.Id)) return Rejected("They have not imposed an embargo on us.", out reason);
        foreach (var id in ids)
            if (!c.States.Any(s => s.MapIndex == id && s.NationMapIndex == target.MapIndex))
                return Rejected("They cannot yield what they do not hold.", out reason);
        var ownedRegions = new HashSet<int>();
        var retained = false;
        for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
        {
            if (ids.Contains(c.ProvinceMap.ProvinceToState[p]) && c.ProvinceMap.ProvinceToNation[p] != target.MapIndex)
                return Rejected("They cannot yield what they do not hold.", out reason);
            if (c.ProvinceMap.ProvinceToNation[p] != target.MapIndex) continue;
            var state = c.ProvinceMap.ProvinceToState[p];
            if (!ids.Contains(state)) { retained = true; continue; }
            ownedRegions.Add(state);
            if ((c.ProvinceController ?? c.ProvinceMap.ProvinceToNation)[p] != target.MapIndex)
                return Rejected("A demand must leave them territory and cannot include occupied land.", out reason);
        }
        if (ids.Count > 0 && (!retained || ownedRegions.Count != ids.Count))
            return Rejected("A demand must leave them territory and cannot include occupied land.", out reason);
        return true;
    }

    private static bool Rejected(string note, out string reason) { reason = note; return false; }

    public static string Summary(Campaign c, Demand demand)
    {
        var ids = RegionIds(demand).ToHashSet();
        var parts = c is null
            ? new List<string>()
            : c.States.Where(s => ids.Contains(s.MapIndex)).OrderBy(s => s.MapIndex).Select(s => s.Name).ToList();
        if (demand.Reparations > 0) parts.Add(Loc.Format("Reparations: {0}", Fmt.Money(demand.Reparations)));
        if (demand.LiftEmbargo) parts.Add(Loc.Get("Lift their embargo"));
        if (demand.NonAggression) parts.Add(Loc.Get("Non-aggression pact"));
        if (demand.Annex) parts.Add(Loc.Get("The whole country, annexed"));
        if (demand.Vassalise) parts.Add(Loc.Get("The country as our subject"));
        if (demand.Disarm) parts.Add(Loc.Get("Disarmament"));
        if (demand.BreakAlliances) parts.Add(Loc.Get("Break their alliances"));
        if (demand.ReleaseSubjects) parts.Add(Loc.Get("Release their subjects"));
        if (demand.TechnologyTransfer) parts.Add(Loc.Get("Hand over their drawings"));
        if (demand.ChangeGovernment) parts.Add(Loc.Get("A government of our colour"));
        if (demand.FleetTransfer) parts.Add(Loc.Get("Half their fleet"));
        if (demand.TradeAgreement) parts.Add(Loc.Get("Trade agreement"));
        if (demand.EmbargoThird) parts.Add(Loc.Format("Embargo {0}", c?.NationById(demand.EmbargoOnId)?.DisplayName ?? "?"));
        if (demand.BreakNamed) parts.Add(Loc.Format("Break the alliance with {0}", c?.NationById(demand.BreakWithId)?.DisplayName ?? "?"));
        if (demand.CancelGuarantee) parts.Add(Loc.Format("Cancel the guarantee of {0}", c?.NationById(demand.CancelGuaranteeId)?.DisplayName ?? "?"));
        if (demand.JoinAlliance) parts.Add(Loc.Get("Join our alliance"));
        if (demand.AcceptGuarantee) parts.Add(Loc.Get("Accept our guarantee"));
        if (demand.MilitaryAccess) parts.Add(Loc.Get("Military access"));
        if (demand.WhitePeace) parts.Add(Loc.Get("White peace"));
        if (demand.CedeOccupied) parts.Add(Loc.Get("Keep the ground each side holds"));
        if (demand.Humiliate) parts.Add(Loc.Get("Public humiliation"));
        if (demand.ReleaseNamed) parts.Add(Loc.Format("Release {0}", c?.NationById(demand.ReleaseSubjectId)?.DisplayName ?? "?"));
        if (demand.Liberate) parts.Add(Loc.Format("Liberate {0}", c?.States.Find(s => s.MapIndex == demand.LiberateRegion)?.Name ?? "?"));
        if (demand.Vassalise && !string.IsNullOrWhiteSpace(demand.SubjectKind))
            parts.Add(Loc.Format("Subject: {0}", Loc.Get(SubjectCatalog.Of(demand.SubjectKind)?.Name ?? demand.SubjectKind)));
        return string.Join(" · ", parts);
    }

    public static Outcome Resolve(Campaign c, Nation demander, Nation target, Demand demand, Answer answer)
        => ResolveCore(c, demander, target, demand, answer, allowHuman: false);

    /// <summary>The addressed player has answered. The same package, and still no automatic guess.</summary>
    internal static Outcome ResolveAnswered(Campaign c, Nation demander, Nation target, Demand demand, Answer answer)
        => ResolveCore(c, demander, target, demand, answer, allowHuman: true);

    private static Outcome ResolveCore(Campaign c, Nation demander, Nation target, Demand demand, Answer answer, bool allowHuman)
    {
        // All terms are validated before the first mutation. An invalid second region
        // must not leave a half-applied settlement or take a partial cash payment.
        if (!ValidateCore(c, demander, target, demand, out var reason, allowHuman)) return new(false, reason);
        var summary = Summary(c, demand);
        var relation = CampaignDiplomacy.Get(c, demander.Id, target.Id);
        if (answer == Answer.Yield)
        {
            var ids = RegionIds(demand).ToHashSet();
            var population = c.States.Where(s => ids.Contains(s.MapIndex)).Sum(s => s.Population);
            var provinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p =>
                ids.Contains(c.ProvinceMap.ProvinceToState[p]) && c.ProvinceMap.ProvinceToNation[p] == target.MapIndex).ToArray();
            if (provinces.Length > 0) StatePartition.Annex(c, provinces, demander, asConquest: false);
            target.Treasury -= demand.Reparations;
            demander.Treasury += demand.Reparations;
            if (demand.LiftEmbargo)
            {
                if (relation.A == target.Id) relation.EmbargoByA = false;
                else relation.EmbargoByB = false;
            }
            if (demand.NonAggression) relation.NonAggressionPact = true;

            // The rest of the package. Each term is applied through the system that already
            // owns that thing - a subject is made by SubjectSystem, an alliance ends the way
            // Statecraft ends one, a fleet changes hands as a fleet - so there is no second
            // copy of any rule here.
            if (demand.Vassalise)
                SubjectSystem.Subjugate(c, demander, target, SubjectKindOf(demand), out _);
            if (demand.Disarm)
            {
                ModifierSystem.Grant(c, target, "ultimatum_disarmament", c.Defines.UltimatumDisarmamentDays);
                target.ManpowerReserve = MathF.Max(0f, target.ManpowerReserve * .5f);
                if (target.EstablishedDivisions > 0f)
                    target.EstablishedDivisions = MathF.Max(1f, target.EstablishedDivisions * .5f);
                foreach (var army in c.Armies)
                    if (army.NationId == target.Id)
                    {
                        army.Divisions = MathF.Max(1f, army.Divisions * .5f);
                        army.Equipment = MathF.Min(army.Equipment, .5f);
                        army.Cohesion = MathF.Min(army.Cohesion, .4f);
                    }
            }
            if (demand.BreakAlliances)
                foreach (var bond in c.Relations.Values.Where(bond => bond.Stance == DiplomaticStance.Allied &&
                             (bond.A == target.Id || bond.B == target.Id)).ToArray())
                    AllianceSystem.Break(c, target, c.NationById(bond.Other(target.Id)), out _);
            if (demand.BreakNamed)
                AllianceSystem.Break(c, target, c.NationById(demand.BreakWithId), out _);
            if (demand.ReleaseNamed)
                SubjectSystem.Release(c, c.NationById(demand.ReleaseSubjectId));
            if (demand.ReleaseSubjects)
                foreach (var other in c.Nations.ToArray())
                    if (other.AnswersTo(target.Id)) SubjectSystem.Release(c, other);
            if (demand.Liberate)
                CountryRelease.Release(c, target, new CountryRelease.ReleaseOptions
                {
                    AsSubject = false,
                    StateMapIndices = new List<int> { demand.LiberateRegion }
                }, out _);
            if (demand.TechnologyTransfer)
            {
                foreach (var technology in target.Technologies)
                    if (!demander.Technologies.Contains(technology)) demander.Technologies.Add(technology);
                foreach (var progress in target.ResearchProgress)
                    demander.ResearchProgress[progress.Key] = MathF.Max(
                        demander.ResearchProgress.GetValueOrDefault(progress.Key), progress.Value);
                ModifierSystem.Grant(c, demander, "ultimatum_technology", c.Defines.UltimatumTechnologyDays);
            }
            if (demand.ChangeGovernment)
            {
                target.Government = demander.Government;
                target.Stability = M.Clamp01(target.Stability - 0.08f);
                OpinionSystem.Remember(c, relation, "imposed_our_government");
            }
            if (demand.TradeAgreement)
                AgreementSystem.Sign(c, relation, AgreementKind.TradeAgreement);
            if (demand.EmbargoThird)
                new SetEmbargoCommand { IssuerId = target.Id, TargetId = demand.EmbargoOnId, Embargo = true }.Apply(c);
            if (demand.CancelGuarantee)
                new GuaranteeCommand { IssuerId = target.Id, TargetId = demand.CancelGuaranteeId, Guarantee = false }.Apply(c);
            if (demand.JoinAlliance)
                AllianceSystem.Sign(c, relation, demander, target);
            if (demand.AcceptGuarantee)
                new GuaranteeCommand { IssuerId = demander.Id, TargetId = target.Id, Guarantee = true }.Apply(c);
            if (demand.MilitaryAccess)
                new MilitaryAccessCommand { IssuerId = target.Id, TargetId = demander.Id, Action = MilitaryAccessAction.Grant }.Apply(c);
            if (demand.Humiliate)
            {
                target.WarSupport = M.Clamp01(target.WarSupport - 0.18f);
                target.Stability = M.Clamp01(target.Stability - 0.10f);
                demander.WarSupport = M.Clamp01(demander.WarSupport + 0.08f);
                ModifierSystem.Grant(c, target, "humiliated", 365 * 3);
            }
            if (demand.WhitePeace || demand.CedeOccupied)
            {
                var war = BilateralWar(c, demander, target);
                if (war is not null)
                    PeaceNegotiation.Resolve(c, war, demander.Id,
                        demand.CedeOccupied ? PeaceTerms.StatusQuo : PeaceTerms.WhitePeace);
            }
            if (demand.FleetTransfer)
            {
                var share = M.Clamp(c.Defines.UltimatumFleetShare, 0f, 1f);
                var hulls = c.Fleets.Where(fleet => fleet.NationId == target.Id)
                    .OrderBy(fleet => fleet.Id).ToArray();
                var handingOver = (int)MathF.Ceiling(hulls.Length * share);
                for (var index = 0; index < handingOver && index < hulls.Length; index++)
                    hulls[index].NationId = demander.Id;
            }
            // Annexation last: it takes the country the rest of the package was about, and
            // nothing can be demanded of a country that no longer holds anything.
            if (demand.Annex)
            {
                var whole = c.StatesOf(target).Sum(state => (long)state.Population);
                foreach (var other in c.Nations.ToArray())
                    if (other.AnswersTo(target.Id)) SubjectSystem.Release(c, other);
                foreach (var fleet in c.Fleets)
                    if (fleet.NationId == target.Id) fleet.NationId = demander.Id;
                UltimatumAnnexation.Apply(c, demander, target);
                if (whole > 0) Statecraft.RecordAnnexation(c, demander, whole);
            }

            target.Stability = M.Clamp01(target.Stability - c.Defines.UltimatumYieldStabilityCost);
            if (population > 0) Statecraft.RecordAnnexation(c, demander, population);
        }
        else OpinionSystem.Remember(c, relation, "refused_our_ultimatum");
        relation.TruceUntil = Math.Max(relation.TruceUntil, c.ElapsedDays + Math.Max(1,
            answer == Answer.Yield ? c.Defines.UltimatumSettlementTruceDays : c.Defines.UltimatumRefusalTruceDays));
        relation.LastActionDay = c.ElapsedDays;
        OpinionSystem.Remember(c, relation, "received_our_ultimatum");
        c.Chronicle.Record(c, answer == Answer.Yield ? "ultimatum_accepted" : "ultimatum_refused",
            demander.Id, target.Id, subject: summary);
        demander.DecisionAvailableDay["ultimatum/" + target.Id] =
            c.ElapsedDays + Math.Max(30, c.Defines.AiUltimatumIntervalDays);
        c.MarkStrategicChange();
        return new(true, summary);
    }

    static string SubjectKindOf(Demand demand) =>
        string.IsNullOrWhiteSpace(demand.SubjectKind) ? "puppet" : demand.SubjectKind;

    static bool HasResearch(Nation nation) =>
        nation.Technologies.Count > 0 || nation.ResearchProgress.Count > 0 || !string.IsNullOrEmpty(nation.CurrentResearch);

    static bool HasTreatyTerm(Demand demand) =>
        demand.Reparations > 0f || demand.LiftEmbargo || demand.NonAggression || demand.TradeAgreement ||
        demand.EmbargoThird || demand.BreakNamed || demand.BreakAlliances || demand.CancelGuarantee ||
        demand.JoinAlliance || demand.AcceptGuarantee || demand.MilitaryAccess || demand.Humiliate ||
        demand.Disarm || demand.TechnologyTransfer || demand.ChangeGovernment || demand.FleetTransfer;

    static bool LeadersOnly(StrategicWar war, int left, int right) =>
        ((war.AttackerId == left && war.DefenderId == right) || (war.AttackerId == right && war.DefenderId == left)) &&
        war.AttackerAllies.Count == 0 && war.DefenderAllies.Count == 0;

    static StrategicWar BilateralWar(Campaign c, Nation left, Nation right) =>
        c.Wars.FirstOrDefault(war => !war.Ended && LeadersOnly(war, left.Id, right.Id));

    static bool PeaceBetween(Campaign c, Nation demander, Nation target, PeaceTerms terms, out string reason)
    {
        var wider = c.Wars.Any(war => !war.Ended && war.Involves(demander.Id) && war.Involves(target.Id) &&
            !LeadersOnly(war, demander.Id, target.Id));
        var war = BilateralWar(c, demander, target);
        if (war is null)
        {
            reason = wider
                ? "That war includes another country. Their land is not on this paper."
                : "There is no war between the two of you to end.";
            return false;
        }
        var block = PeaceNegotiation.BlockReason(c, war, demander.Id, terms);
        if (block.Length > 0) { reason = block; return false; }
        reason = "";
        return true;
    }

    static bool EmbargoTarget(Campaign c, Nation demander, Nation target, int thirdId, out string reason)
    {
        var third = c.NationById(thirdId);
        if (third is null || third.Id == demander.Id || third.Id == target.Id)
        { reason = "Name another country for the embargo."; return false; }
        if (CampaignDiplomacy.Peek(c, target.Id, third.Id)?.EmbargoBy(target.Id) == true)
        { reason = "They already embargo that country."; return false; }
        reason = "";
        return true;
    }

    static bool AlliedWith(Campaign c, Nation target, int otherId, out string reason)
    {
        var other = c.NationById(otherId);
        if (other is null || other.Id == target.Id ||
            CampaignDiplomacy.Peek(c, target.Id, other.Id)?.Stance != DiplomaticStance.Allied)
        { reason = "They are not allied with that country."; return false; }
        reason = "";
        return true;
    }

    static bool Guarantees(Campaign c, Nation target, int otherId, out string reason)
    {
        var other = c.NationById(otherId);
        if (other is null || other.Id == target.Id ||
            CampaignDiplomacy.Peek(c, target.Id, other.Id)?.GuaranteedBy(target.Id) != true)
        { reason = "They do not guarantee that country."; return false; }
        reason = "";
        return true;
    }
}
