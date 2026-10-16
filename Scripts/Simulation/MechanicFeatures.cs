using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    // Initial campaign switches. Package overrides merge by key, never replace the table.
    public Dictionary<string, bool> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["long_range_forces"] = true, ["aviation"] = true, ["drones"] = true,
        ["artillery"] = true, ["air_defence"] = true,
        ["navy"] = true, ["research"] = true, ["construction"] = true,
        ["recruitment"] = true, ["decisions"] = true, ["events"] = true,
        ["politics"] = true, ["movements"] = true, ["colonisation"] = true,
        ["subjects"] = true, ["formable_nations"] = true, ["secession"] = true,
        ["war_damage"] = true, ["war_weariness"] = true, ["militia"] = true,
        ["historical_norms"] = true, ["public_reputation"] = true, ["seasons"] = true, ["seasonal_water"] = true, ["seasonal_roads"] = true, ["weather"] = true, ["movement_lifecycle"] = true,
        ["public_debt"] = true, ["laws"] = true, ["political_agendas"] = true, ["national_courses"] = true, ["deals"] = true,
        ["resident_awareness"] = true, ["domestic_crises"] = true, ["political_cohorts"] = true, ["federal_fracture"] = true, ["organizations"] = true,
        ["escalation"] = true, ["proxy_wars"] = true, ["de_facto_factions"] = true, ["coups"] = true,
        ["foreign_assistance"] = true, ["government_promises"] = true, ["strategic_goals"] = true, ["state_identity"] = true,
        ["tactical_doctrines"] = true, ["strategic_doctrines"] = true,
        // Intelligence networks and the world-order emergency on top: dormant content the
        // console `feature` command can turn on, and a save that predates them agrees with
        // what the shipped data declares because the data declares the same thing.
        ["intelligence"] = true, ["world_order_emergency"] = false
    };

    // Feature -> named common/scripted_triggers condition. Empty string removes a rule.
    // A matching country unlocks the mechanic globally; no random event roll is involved.
    public Dictionary<string, string> FeatureUnlocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Additive extension points: a mod can gate any existing command or navigation
    // screen with its own flag. Empty string removes a binding. These are class/screen
    // names, not user-visible translations. No reflection executes mod-provided code.
    public Dictionary<string, string> FeatureCommands { get; set; } = MechanicFeatures.CommandDefaults();
    public Dictionary<string, string> FeatureSystems { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AiAdmiralty"] = "navy", ["NavalOperations"] = "navy", ["Shipbuilding"] = "navy",
        ["ResearchSystem"] = "research", ["ConstructionSystem"] = "construction",
        ["RecruitmentSystem"] = "recruitment", ["EventSystem"] = "events",
        ["GovernanceEvents"] = "politics", ["MovementRules"] = "movements", ["MovementAi"] = "movements",
        ["ColonisationCore"] = "colonisation", ["SubjectSystem"] = "subjects", ["SubmissionSystem"] = "subjects",
        ["PeacefulFormationAi"] = "formable_nations", ["SecessionEvents"] = "secession",
        ["WarDamage"] = "war_damage", ["WarWeariness"] = "war_weariness",
        ["MilitiaCore"] = "militia", ["CivilWarPersistence"] = "militia",
        ["Escalation"] = "escalation", ["ProxyWars"] = "proxy_wars", ["Factions"] = "de_facto_factions", ["Coups"] = "coups",
        ["ForeignAssistance"] = "foreign_assistance"
    };
    public Dictionary<string, string> FeatureScreens { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Research"] = "research", ["Navy"] = "navy", ["Decisions"] = "decisions",
        ["State Affairs"] = "politics", ["Movements"] = "movements", ["Laws"] = "laws"
    };
}

public sealed partial class Campaign
{
    public readonly Dictionary<string, bool> FeatureOverrides = new(StringComparer.OrdinalIgnoreCase);
    private int _featureEvaluationDay = -1;

    public bool FeatureEnabled(string id)
    {
        if (!MechanicFeatures.ValidId(id)) return false;
        if (FeatureOverrides.TryGetValue(id, out var changed)) return changed;
        if (Defines.Features is not null && Defines.Features.TryGetValue(id, out var initial)) return initial;
        // Saves produced before new built-in switches must retain their old mechanics.
        return MechanicFeatures.BuiltIns.Contains(id);
    }

    /// <summary>Global scenario effect, not a player/network command. Never destroys dormant content.</summary>
    public bool SetFeature(string id, bool enabled)
    {
        if (!MechanicFeatures.ValidId(id)) return false;
        var before = FeatureEnabled(id);
        FeatureOverrides[id] = enabled;
        if (id.Equals("public_debt", StringComparison.OrdinalIgnoreCase))
            SovereignFinance.NormalizeAccounts(this);
        if (id.Equals("national_courses", StringComparison.OrdinalIgnoreCase))
            foreach (var n in Nations)
            {
                n.NationalCourseActive = enabled; n.NationalCourse.LastReviewDay = ElapsedDays;
                NationalModifiers.Invalidate(n);
            }
        if (id.Equals("laws", StringComparison.OrdinalIgnoreCase) || id.Equals("political_agendas", StringComparison.OrdinalIgnoreCase))
            foreach (var n in Nations)
            {
                n.LawsActive = FeatureEnabled("laws"); n.AgendasActive = FeatureEnabled("political_agendas");
                NationalModifiers.Invalidate(n);
            }
        if (!enabled && !SocialMemory.ImagesEnabled(this)) PublicOutreachActions.Suspend(this);
        if (before == enabled) return true;
        if (enabled) SocialMemory.Resume(this, id);
        SupportCore.IndexReach(this); // also safe while paused, before the next battle tick
        SupportExposure.Clear();
        MarkStrategicChange();
        return true;
    }

    public void EvaluateFeatureUnlocks()
    {
        if (_featureEvaluationDay == ElapsedDays) return;
        _featureEvaluationDay = ElapsedDays;
        if (Defines.FeatureUnlocks is null || Defines.FeatureUnlocks.Count == 0) return;
        // Snapshot first: one unlock cannot make another rule pass merely due to key order.
        var unlock = new List<string>();
        foreach (var (id, trigger) in Defines.FeatureUnlocks.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (!FeatureEnabled(id) && MechanicFeatures.ValidId(id) && !string.IsNullOrWhiteSpace(trigger) &&
                Nations.OrderBy(n => n.Id).Any(n => ScriptLibrary.MatchesTrigger(this, n, trigger)))
                unlock.Add(id);
        foreach (var id in unlock) SetFeature(id, true);
    }
}

public static class MechanicFeatures
{
    public static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "long_range_forces", "aviation", "drones", "artillery", "air_defence", "navy",
        "research", "construction", "recruitment", "decisions", "events", "politics",
        "movements", "colonisation", "subjects", "formable_nations", "secession",
        "war_damage", "war_weariness", "militia", "historical_norms", "public_reputation", "seasons", "seasonal_water", "seasonal_roads", "weather", "movement_lifecycle", "public_debt",
        "laws", "political_agendas", "national_courses", "deals", "resident_awareness", "domestic_crises", "political_cohorts", "federal_fracture",
        "escalation", "proxy_wars", "de_facto_factions", "coups", "organizations", "foreign_assistance",
        "government_promises", "strategic_goals", "state_identity", "intelligence", "world_order_emergency", "tactical_doctrines",
        "strategic_doctrines"
    };

    public static Dictionary<string, string> CommandDefaults()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Bind(string flag, params string[] commands)
        { foreach (var command in commands) result[command] = flag; }
        Bind("navy", "SetFleetOrderCommand", "OrderHullsCommand", "EmbarkArmiesCommand", "LaunchInvasionCommand");
        Bind("research", "SetResearchCommand");
        Bind("construction", "QueueConstructionCommand", "DemolishBuildingCommand");
        Bind("recruitment", "RaiseArmyCommand");
        Bind("decisions", "TakeDecisionCommand");
        Bind("politics", "ChangePoliticsCommand", "SetPolicyRateCommand", "SetGovernmentFocusCommand");
        Bind("domestic_crises", "StartCrisisResponseCommand", "ConciliateRegionCommand");
        Bind("movements", "MovementActionCommand", "MovementDealingCommand");
        Bind("colonisation", "ColoniseCommand");
        Bind("subjects", "OfferSubmissionCommand");
        Bind("formable_nations", "FormNationCommand", "InviteFormationCommand");
        Bind("war_damage", "RebuildRegionCommand");
        Bind("public_reputation", "StartPublicOutreachCommand");
        Bind("public_debt", "ManageDebtCommand");
        Bind("escalation", "HoldTalksCommand");
        Bind("proxy_wars", "SponsorWarCommand");
        Bind("deals", "ProposeDealCommand", "RespondDealCommand", "AskForCorridorCommand");
        Bind("foreign_assistance", "RequestForeignAssistanceCommand", "RespondForeignAssistanceCommand");
        Bind("organizations", "OrganizationCommand");
        Bind("intelligence", "IntelNetworkCommand");
        Bind("government_promises", "MakePromiseCommand");
        Bind("strategic_goals", "AdoptGoalCommand", "AbandonGoalCommand");
        Bind("state_identity", "DeclareStateIdentityCommand");
        Bind("tactical_doctrines", "StudyDoctrineCommand");
        Bind("strategic_doctrines", "AdoptStrategicDoctrineCommand");
        // Cancel/refund and pending-event response commands remain available by default.
        return result;
    }

    public static bool ScreenEnabled(Campaign c, string screen) => c is not null &&
        (!c.Defines.FeatureScreens.TryGetValue(screen, out var id) || string.IsNullOrWhiteSpace(id) || c.FeatureEnabled(id));

    public static bool SystemEnabled(Campaign c, string system) => c is not null &&
        (!c.Defines.FeatureSystems.TryGetValue(system, out var id) || string.IsNullOrWhiteSpace(id) || c.FeatureEnabled(id));

    public static bool Allows(Campaign c, ICampaignCommand command)
    {
        if (c is null) return false;
        if (!c.Defines.FeatureCommands.TryGetValue(command.GetType().Name, out var id) ||
            string.IsNullOrWhiteSpace(id) || c.FeatureEnabled(id)) return true;
        DiagnosticLogs.Write("orders", $"COMMAND_DISABLED type={command.GetType().Name} feature={id}");
        return false;
    }

    public static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 80 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static string Of(SupportKind kind) => kind switch
    {
        SupportKind.Air => "aviation", SupportKind.Drone => "drones",
        SupportKind.AirDefence => "air_defence", _ => "artillery"
    };

    public static void Merge<T>(Dictionary<string, T> target, Dictionary<string, T> patch)
    {
        foreach (var (id, value) in patch)
            if (ValidId(id)) target[id.ToLowerInvariant()] = value;
    }

    public static void MergeBindings(Dictionary<string, string> target, Dictionary<string, string> patch)
    {
        foreach (var (key, value) in patch)
            if (!string.IsNullOrWhiteSpace(key) && key.Length <= 120 &&
                (string.IsNullOrWhiteSpace(value) || ValidId(value))) target[key] = value ?? "";
    }
}
