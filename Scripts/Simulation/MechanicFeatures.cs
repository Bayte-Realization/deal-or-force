using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
        ["subjects"] = true, ["formable_nations"] = true, ["secession"] = true, ["culture_union"] = true,
        ["war_damage"] = true, ["war_weariness"] = true, ["militia"] = true,
        ["historical_norms"] = true, ["public_reputation"] = true, ["seasons"] = true, ["seasonal_water"] = true, ["seasonal_roads"] = true, ["weather"] = true, ["movement_lifecycle"] = true,
        ["public_debt"] = true, ["laws"] = true, ["political_agendas"] = true, ["national_courses"] = true, ["deals"] = true,
        ["resident_awareness"] = true, ["domestic_crises"] = true, ["political_cohorts"] = true, ["federal_fracture"] = true, ["organizations"] = true,
        ["escalation"] = true, ["crisis_ladder"] = true, ["proxy_wars"] = true, ["de_facto_factions"] = true, ["coups"] = true,
        ["foreign_assistance"] = true, ["government_promises"] = true, ["strategic_goals"] = true, ["state_identity"] = true,
        ["tactical_doctrines"] = true, ["strategic_doctrines"] = true,
        // The union and demographic cycles ship enabled; the data files 78 and 79 declare
        // the same. They were missing from this compiled table, so a mod that shipped no
        // override saw a different world from the one the package describes.
        ["union_cohesion"] = true, ["demographic_cycle"] = true,
        // Intelligence networks and the world-order emergency on top: dormant content the
        // console `feature` command can turn on, and a save that predates them agrees with
        // what the shipped data declares because the data declares the same thing.
        ["intelligence"] = true, ["world_order_emergency"] = false,
        // Who leads a country, and what happens to them. A ruler is a person with traits,
        // and the traits feed the same modifier totals a national spirit does.
        ["rulers"] = true,
        // Leases and troops in without a war (LandArrangements).
        ["land_arrangements"] = true,
        // The nuclear age, in three layers a scenario can open one at a time: the world's alert
        // ladder (DefconSystem), what a holder does with its weapons short of using them
        // (NuclearPosture: alert, tests, the red telephone, the umbrella), and the use itself.
        ["defcon"] = true, ["nuclear_posture"] = true, ["nuclear_strikes"] = true
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
        ["PeacefulFormationAi"] = "formable_nations", ["FormableProclamationAi"] = "formable_nations", ["SecessionEvents"] = "secession",
        ["WarDamage"] = "war_damage", ["WarWeariness"] = "war_weariness",
        ["MilitiaCore"] = "militia", ["CivilWarPersistence"] = "militia",
        ["Escalation"] = "escalation", ["ProxyWars"] = "proxy_wars", ["Factions"] = "de_facto_factions", ["Coups"] = "coups",
        ["ForeignAssistance"] = "foreign_assistance", ["Rulers"] = "rulers",
        ["LandArrangementSystem"] = "land_arrangements", ["KinStatecraft"] = "movements"
    };
    // Feature -> a true/false define it drives. Many systems answer to one switch in their own
    // defines file (ElectionsEnabled, ParliamentEnabled...), which a scenario could set once and
    // never again. Bound here, the define follows the feature: an event's "feature.elections_set",
    // a FeatureUnlocks rule (a technology, a year) or the console moves it in play. A binding is
    // silent until the scenario declares the feature in Features or changes it - so shipping one
    // changes nothing on its own. A mod may bind any boolean define; empty string removes one.
    public Dictionary<string, string> FeatureDefines { get; set; } = MechanicFeatures.DefineDefaults();
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
        MechanicFeatures.SyncDefine(this, id);
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
        // A loaded save or a fresh scenario: bound defines catch up with their features first.
        MechanicFeatures.SyncDefines(this);
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
        "movements", "colonisation", "subjects", "formable_nations", "secession", "culture_union",
        "war_damage", "war_weariness", "militia", "historical_norms", "public_reputation", "seasons", "seasonal_water", "seasonal_roads", "weather", "movement_lifecycle", "public_debt",
        "laws", "political_agendas", "national_courses", "deals", "resident_awareness", "domestic_crises", "political_cohorts", "federal_fracture",
        "escalation", "proxy_wars", "de_facto_factions", "coups", "organizations", "foreign_assistance",
        "government_promises", "strategic_goals", "state_identity", "rulers", "intelligence", "world_order_emergency", "tactical_doctrines",
        "strategic_doctrines", "union_cohesion", "demographic_cycle", "land_arrangements",
        "defcon", "nuclear_posture", "nuclear_strikes"
    };

    /// <summary>Built-in switches added after saves were fingerprinted; on by default, they add no bytes.</summary>
    public static readonly HashSet<string> LateBuiltIns = new(StringComparer.OrdinalIgnoreCase)
        { "defcon", "nuclear_posture", "nuclear_strikes" };

    public static Dictionary<string, string> CommandDefaults()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Bind(string flag, params string[] commands)
        { foreach (var command in commands) result[command] = flag; }
        Bind("navy", "SetFleetOrderCommand", "OrderHullsCommand", "CommissionNewFleetCommand", "EmbarkArmiesCommand", "LaunchInvasionCommand");
        Bind("research", "SetResearchCommand");
        Bind("construction", "QueueConstructionCommand", "DemolishBuildingCommand");
        Bind("recruitment", "RaiseArmyCommand");
        Bind("decisions", "TakeDecisionCommand");
        Bind("politics", "ChangePoliticsCommand", "SetPolicyRateCommand", "SetGovernmentFocusCommand");
        Bind("domestic_crises", "StartCrisisResponseCommand", "ConciliateRegionCommand");
        Bind("movements", "MovementActionCommand", "MovementDealingCommand", "KinStatecraftCommand");
        Bind("colonisation", "ColoniseCommand");
        Bind("subjects", "OfferSubmissionCommand", "ConsolidateSubjectsCommand");
        Bind("formable_nations", "FormNationCommand", "InviteFormationCommand");
        Bind("war_damage", "RebuildRegionCommand");
        Bind("public_reputation", "StartPublicOutreachCommand");
        Bind("public_debt", "ManageDebtCommand");
        Bind("escalation", "HoldTalksCommand", "MediateCrisisCommand", "InterveneCrisisCommand");
        Bind("proxy_wars", "SponsorWarCommand", "RequestArmsShipmentCommand", "RespondArmsShipmentCommand");
        Bind("deals", "ProposeDealCommand", "RespondDealCommand", "AskForCorridorCommand");
        Bind("foreign_assistance", "RequestForeignAssistanceCommand", "RespondForeignAssistanceCommand");
        Bind("organizations", "OrganizationCommand");
        Bind("intelligence", "IntelNetworkCommand");
        Bind("government_promises", "MakePromiseCommand");
        Bind("strategic_goals", "AdoptGoalCommand", "AbandonGoalCommand");
        Bind("state_identity", "DeclareStateIdentityCommand");
        Bind("tactical_doctrines", "StudyDoctrineCommand");
        Bind("strategic_doctrines", "AdoptStrategicDoctrineCommand");
        Bind("land_arrangements", "LandArrangementCommand");
        // Cancel/refund and pending-event response commands remain available by default.
        return result;
    }

    public static Dictionary<string, string> DefineDefaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["elections"] = nameof(Defines.ElectionsEnabled), ["parliament"] = nameof(Defines.ParliamentEnabled),
        ["dynastic_claims"] = nameof(Defines.DynasticClaimsEnabled), ["diplomatic_missions"] = nameof(Defines.DiplomaticMissionsEnabled),
        ["belief_conversion"] = nameof(Defines.BeliefConversionEnabled), ["brain_drain"] = nameof(Defines.BrainDrainEnabled),
        ["internal_migration"] = nameof(Defines.InternalMigrationEnabled), ["occupation_resistance"] = nameof(Defines.OccupationResistanceEnabled),
        ["mutual_deterrence"] = nameof(Defines.MutualDeterrenceEnabled), ["strategic_stockpiles"] = nameof(Defines.StrategicStockpilesEnabled),
        ["war_emergency"] = nameof(Defines.WarEmergencyEnabled), ["secular_cycle"] = nameof(Defines.SecularCycleEnabled),
        ["combat_tactics"] = nameof(Defines.CombatTacticsEnabled), ["regional_administration"] = nameof(Defines.RegionalAdministrationEnabled),
        ["household_dynamics"] = nameof(Defines.HouseholdDynamicsEnabled), ["demographic_extinction"] = nameof(Defines.DemographicExtinctionEnabled)
    };

    // Every boolean define a binding may drive, by name. Our own Defines class only: nothing a
    // mod ships is executed, a mod only names one of these.
    private static readonly Dictionary<string, PropertyInfo> BoolDefines = typeof(Defines)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public static bool IsBoolDefine(string name) => name is not null && BoolDefines.ContainsKey(name);

    public static void SyncDefines(Campaign c)
    {
        if (c?.Defines.FeatureDefines is not { Count: > 0 } bindings) return;
        foreach (var id in bindings.Keys) SyncDefine(c, id);
    }

    /// <summary>
    /// Sets the define bound to <paramref name="id"/> to the feature's state - only for a feature
    /// the scenario declared or changed; an undeclared binding leaves the define to its file.
    /// </summary>
    public static void SyncDefine(Campaign c, string id)
    {
        if (c?.Defines.FeatureDefines is null || !c.Defines.FeatureDefines.TryGetValue(id, out var name) ||
            string.IsNullOrWhiteSpace(name) || !BoolDefines.TryGetValue(name, out var property)) return;
        if (!c.FeatureOverrides.ContainsKey(id) && !(c.Defines.Features?.ContainsKey(id) ?? false)) return;
        var enabled = c.FeatureEnabled(id);
        if ((bool)property.GetValue(c.Defines)! != enabled) property.SetValue(c.Defines, enabled);
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

    public static bool ValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80) return false;
        if (BuiltIns.Contains(id)) return true;
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }

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
