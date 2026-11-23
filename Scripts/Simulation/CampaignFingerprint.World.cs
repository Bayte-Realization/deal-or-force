using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class CampaignFingerprint
{
    private static void MixHomelandsAndGlobalWorldRules(Campaign campaign, ref ulong hash)
    {
        // Homelands that moved in play (HomelandIntegration); nothing mixed while none has, so
        // older worlds keep their fingerprints.
        if (campaign.HomelandChanges > 0)
        {
            MixText(ref hash, "homeland_changes");
            Mix(ref hash, campaign.HomelandChanges);
            foreach (var (id, regions) in campaign.Homelands.OrderBy(p => p.Key))
            {
                Mix(ref hash, id);
                Mix(ref hash, regions.Count);
                foreach (var region in regions.OrderBy(r => r)) Mix(ref hash, region);
            }
        }
        if (!campaign.Defines.PocketResistanceEnabled || campaign.Defines.PocketResistanceMinStrength != .25f ||
            campaign.Defines.PocketResistanceMinSupply != 0f)
        {
            MixText(ref hash, "pocket_resistance_rules");
            Mix(ref hash, campaign.Defines.PocketResistanceEnabled ? 1 : 0);
            MixFloat(ref hash, campaign.Defines.PocketResistanceMinStrength);
            MixFloat(ref hash, campaign.Defines.PocketResistanceMinSupply);
        }
        if (!campaign.Defines.BeliefConversionEnabled || campaign.Defines.BeliefConversionAnnual != .008f)
        {
            MixText(ref hash, "belief_conversion");
            Mix(ref hash, campaign.Defines.BeliefConversionEnabled ? 1 : 0);
            MixFloat(ref hash, campaign.Defines.BeliefConversionAnnual);
        }
        if (!campaign.Defines.QuarrelStep || campaign.Defines.QuarrelStepCooldownDays != 120)
        {
            MixText(ref hash, "quarrel_step_rules");
            Mix(ref hash, campaign.Defines.QuarrelStep ? 1 : 0);
            Mix(ref hash, campaign.Defines.QuarrelStepCooldownDays);
        }
        if (campaign.Curse.Length > 0)
        {
            MixText(ref hash, "world_curse");
            MixText(ref hash, campaign.Curse);
            Mix(ref hash, campaign.CurseDay);
            Mix(ref hash, campaign.CurseDrowned);
        }
        if (campaign.DefconLevel != 5 || campaign.DefconDaysInStage > 0 || campaign.DefconPeakLevel != 5)
        {
            MixText(ref hash, "defcon");
            Mix(ref hash, campaign.DefconLevel);
            Mix(ref hash, campaign.DefconDaysInStage);
            Mix(ref hash, campaign.DefconPeakLevel);
        }
    }

    private static void MixBtaBridgeAndScenarioFeatures(Campaign campaign, ref ulong hash)
    {
        // Empty extension state must not alter the original game's deterministic fingerprint.
        if (campaign.CrossoverScars.Count > 0 || campaign.CrossoverWorks.Count > 0 ||
            campaign.CrossoverImpacts.Count > 0 || campaign.DestroyedProvinces.Count > 0)
        {
            MixText(ref hash, "bta_bridge");
            Mix(ref hash, campaign.CrossoverScars.Count);
            foreach (var cell in campaign.CrossoverScars.OrderBy(k => k)) Mix(ref hash, cell);
            Mix(ref hash, campaign.CrossoverWorks.Count);
            foreach (var work in campaign.CrossoverWorks.OrderBy(w => w.Id, StringComparer.Ordinal))
            {
                MixText(ref hash, work.Id); Mix(ref hash, work.Province); Mix(ref hash, work.State);
                Mix(ref hash, work.Nation); Mix(ref hash, (int)work.Building);
                MixFloat(ref hash, work.X); MixFloat(ref hash, work.Y); MixFloat(ref hash, work.Z);
                Mix(ref hash, work.Active ? 1 : 0);
                foreach (var cell in work.Blocks.OrderBy(k => k)) Mix(ref hash, cell);
                Mix(ref hash, -100);
            }
            foreach (var impact in campaign.CrossoverImpacts)
            {
                Mix(ref hash, impact.Hour); Mix(ref hash, impact.Province); MixText(ref hash, impact.Kind);
                Mix(ref hash, impact.Population); MixFloat(ref hash, impact.Gdp); MixText(ref hash, impact.Message);
            }
            Mix(ref hash, campaign.DestroyedProvinces.Count);
            foreach (var province in campaign.DestroyedProvinces.OrderBy(p => p)) Mix(ref hash, province);
        }
        foreach (var character in campaign.ScenarioId ?? "2024") Mix(ref hash, character);
        Mix(ref hash, -1);
        MixScenarioFeatures(campaign, ref hash);
        MixScenarioCalendarAndMeta(campaign, ref hash);
        MixWorldSituations(campaign, ref hash);
        DiseasesAndDisasters.WriteFingerprint(campaign, ref hash);
    }

    private static void MixWorldSituations(Campaign campaign, ref ulong hash)
    {
        if (!WorldSituations.IsEnabled(campaign))
        {
            MixText(ref hash, "world_situations_disabled");
            Mix(ref hash, 0);
        }
        if (campaign.WorldSituations.Count == 0 && campaign.NextSituationId <= 1) return;

        MixText(ref hash, "world_situations_v1");
        Mix(ref hash, campaign.NextSituationId);
        Mix(ref hash, campaign.WorldSituations.Count);
        foreach (var s in Ordered(campaign.WorldSituations, item => item.Id))
        {
            Mix(ref hash, s.Id);
            MixText(ref hash, s.DefinitionId);
            Mix(ref hash, s.StartedDay);
            Mix(ref hash, s.LastUpdateDay);
            Mix(ref hash, s.ResolvedDay);
            MixFloat(ref hash, s.Intensity);
            MixFloat(ref hash, s.StabilizerWeight);
            MixFloat(ref hash, s.ExploiterWeight);
            Mix(ref hash, s.SummitConvened ? 1 : 0);
            Mix(ref hash, s.SummitHostId);
            Mix(ref hash, (int)s.Outcome);
            Mix(ref hash, s.ParticipantStances?.Count ?? 0);
            if (s.ParticipantStances is not null)
                foreach (var kv in s.ParticipantStances.OrderBy(p => p.Key))
                { Mix(ref hash, kv.Key); Mix(ref hash, (int)kv.Value); }
            Mix(ref hash, s.LastActionDayByNation?.Count ?? 0);
            if (s.LastActionDayByNation is not null)
                foreach (var kv in s.LastActionDayByNation.OrderBy(p => p.Key))
                { Mix(ref hash, kv.Key); Mix(ref hash, kv.Value); }
        }
    }

    private static void MixScenarioCalendarAndMeta(Campaign campaign, ref ulong hash)
    {
        // Only mix custom calendar / scenario metadata / restricted features when actually present,
        // so base 2024 campaigns emit zero extra bytes and keep their exact historical fingerprint.
        if (campaign.Calendar is not null && !campaign.Calendar.IsDefault)
        {
            MixText(ref hash, "scenario_calendar_v1");
            MixText(ref hash, campaign.Calendar.CalendarId);
            MixText(ref hash, campaign.Calendar.EraName);
            MixText(ref hash, campaign.Calendar.EraShort);
            Mix(ref hash, campaign.Calendar.YearOffset);
            MixText(ref hash, campaign.Calendar.DateFormat);
            MixText(ref hash, campaign.Calendar.YearFormat);
            Mix(ref hash, campaign.Calendar.ShowEraInDate ? 1 : 0);
            if (campaign.Calendar.MonthNames is { Length: > 0 } months)
            {
                Mix(ref hash, months.Length);
                foreach (var m in months) MixText(ref hash, m);
            }
        }
        if (campaign.RestrictedFeatures is { Count: > 0 })
        {
            MixText(ref hash, "restricted_features_v1");
            Mix(ref hash, campaign.RestrictedFeatures.Count);
            foreach (var id in campaign.RestrictedFeatures.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                MixText(ref hash, id.ToLowerInvariant());
        }
        if (campaign.ScenarioMeta is not null && !campaign.ScenarioMeta.IsEmpty)
        {
            MixText(ref hash, "scenario_meta_v1");
            Mix(ref hash, campaign.ScenarioMeta.TotalConversion ? 1 : 0);
            MixText(ref hash, campaign.ScenarioMeta.ContentSignature);
            MixText(ref hash, campaign.ScenarioMeta.EraName);
            MixText(ref hash, campaign.ScenarioMeta.Theme);
            MixText(ref hash, campaign.ScenarioMeta.MusicTrack);
            MixText(ref hash, campaign.ScenarioMeta.MusicPlaylist);
        }
    }

    private static void MixScenarioFeatures(Campaign campaign, ref ulong hash)
    {
        foreach (var id in campaign.Defines.Features.Keys.Concat(campaign.FeatureOverrides.Keys).Concat(MechanicFeatures.BuiltIns)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            // A switch added after the table was frozen speaks only when it is not simply on.
            if (MechanicFeatures.LateBuiltIns.Contains(id) && campaign.FeatureEnabled(id) &&
                !campaign.FeatureOverrides.ContainsKey(id)) continue;
            foreach (var character in id.ToLowerInvariant()) Mix(ref hash, character);
            Mix(ref hash, -2);
            Mix(ref hash, campaign.FeatureEnabled(id) ? 1 : 0);
        }
        // The define bindings count only when a scenario changed them: the shipped table adds no bytes.
        var defineBindings = campaign.Defines.FeatureDefines;
        var shippedBindings = MechanicFeatures.DefineDefaults();
        var boundDefines = defineBindings.Count == shippedBindings.Count &&
            defineBindings.All(p => shippedBindings.TryGetValue(p.Key, out var v) && v == p.Value) ? null : defineBindings;
        foreach (var table in new[] { campaign.Defines.FeatureUnlocks, campaign.Defines.FeatureCommands,
                     campaign.Defines.FeatureScreens, campaign.Defines.FeatureSystems, boundDefines }.Where(t => t is not null))
        {
            Mix(ref hash, -3);
            foreach (var entry in table.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var character in entry.Key.ToLowerInvariant()) Mix(ref hash, character);
                Mix(ref hash, -4);
                foreach (var character in entry.Value ?? "") Mix(ref hash, character);
                Mix(ref hash, -5);
            }
        }
    }

    private static void MixStateCountAndInstantConstruction(Campaign campaign, ref ulong hash)
    {
        // The region list is no longer fixed: a border cuts a region in two and a border
        // going away puts it back. Two worlds that disagree about how many regions exist
        // disagree about everything downstream of that.
        Mix(ref hash, campaign.States.Count);
        if (campaign.Nations.Exists(n => n.InstantConstruction))
        {
            MixText(ref hash, "instant_construction");
            foreach (var n in campaign.Nations) if (n.InstantConstruction) Mix(ref hash, n.Id);
        }
    }

    private static void MixNationIdentity(Nation nation, bool longCampaignClock, ref ulong hash)
    {
        if (nation.GovernmentSeatProvinceId != 0)
        { MixText(ref hash, "government_seat"); Mix(ref hash, nation.GovernmentSeatProvinceId); }
        // A retired country (LongCampaign); nothing mixed while none is, so younger worlds keep their fingerprints.
        if (nation.RetiredDay >= 0)
        { MixText(ref hash, "retired"); Mix(ref hash, nation.RetiredDay); }
        // And the clock that decides when one is: kept from the first day a country is found gone.
        if (longCampaignClock && nation.GoneSinceDay >= 0)
        { MixText(ref hash, "gone_since"); Mix(ref hash, nation.GoneSinceDay); }
        if (nation.Delegated.Count > 0 || nation.ArmyByHand)
        {
            MixText(ref hash, "staff_portfolios_v1"); Mix(ref hash, nation.ArmyByHand ? 1 : 0); Mix(ref hash, nation.Delegated.Count);
            foreach (var portfolio in nation.Delegated.OrderBy(p => p, StringComparer.Ordinal)) MixText(ref hash, portfolio);
        }
        // Standing answers (DealAnswer): empty for every country nobody set them for, so a
        // world without them keeps the fingerprint it had.
        if (nation.AutoAnswer.Count > 0)
        {
            MixText(ref hash, "auto_answer_v1"); Mix(ref hash, nation.AutoAnswer.Count);
            foreach (var (kind, mode) in nation.AutoAnswer.OrderBy(p => p.Key, StringComparer.Ordinal)) { MixText(ref hash, kind); Mix(ref hash, mode); }
        }
        if (nation.AutoAnswerLog.Count > 0)
        {
            MixText(ref hash, "auto_answer_log_v1"); Mix(ref hash, nation.AutoAnswerLog.Count);
            foreach (var note in nation.AutoAnswerLog)
            {
                Mix(ref hash, note.Day); Mix(ref hash, note.ProposerId); Mix(ref hash, note.OfferId);
                Mix(ref hash, note.Agreement); MixText(ref hash, note.Kind ?? "");
                Mix(ref hash, (int)note.Mode); Mix(ref hash, note.Accepted ? 1 : 0);
                Mix(ref hash, note.Counted ? 1 : 0); MixFloat(ref hash, note.Benefit);
            }
        }
        Mix(ref hash, nation.Id);
        foreach (var character in nation.FormedNationId ?? "") Mix(ref hash, character);
        Mix(ref hash, -1);
        Mix(ref hash, nation.FormedNationDay);
        foreach (var id in nation.FormedNationHistory.OrderBy(id => id, StringComparer.Ordinal)) MixText(ref hash, id);
        Mix(ref hash, -11);
    }

    private static void MixNationCosmeticsAndRulers(Nation nation, ref ulong hash)
    {
        if (!string.IsNullOrEmpty(nation.CustomName) || !string.IsNullOrEmpty(nation.CustomFlag))
        {
            MixText(ref hash, "national_cosmetics");
            MixText(ref hash, nation.CustomName);
            MixText(ref hash, nation.CustomFlag);
        }
        // New optional campaign cosmetics emit no bytes at their defaults, so
        // existing untouched worlds retain their established fingerprints.
        if (!string.IsNullOrEmpty(nation.CustomFlagDesignJson))
        {
            MixText(ref hash, "campaign_flag_design");
            MixText(ref hash, nation.CustomFlagDesignJson);
        }
        if (!string.IsNullOrEmpty(nation.MapColorBeforeOverride))
        {
            MixText(ref hash, "campaign_map_colour");
            MixText(ref hash, nation.MapColorBeforeOverride);
            MixText(ref hash, nation.Color);
        }
        if (!string.IsNullOrEmpty(nation.FoundNameKind) || nation.NameSettledDay >= 0 || nation.ReleasedFromId >= 0)
        {
            MixText(ref hash, "founded_name");
            MixText(ref hash, nation.FoundNameKind); MixText(ref hash, nation.FoundNameId);
            MixText(ref hash, nation.FoundNameForm); MixText(ref hash, nation.FoundNameBase);
            MixText(ref hash, nation.NameConvention); Mix(ref hash, nation.NameSettledDay); Mix(ref hash, nation.ReleasedFromId);
        }
        if (nation.RevoltParentId >= 0)
        {
            MixText(ref hash, "revolt_parent");
            Mix(ref hash, nation.RevoltParentId);
        }
        if (nation.HasCustomLeadership)
        {
            MixText(ref hash, "ruler_cabinet");
            MixText(ref hash, nation.HeadOfStateName); MixText(ref hash, nation.HeadOfStateTrait);
            Mix(ref hash, nation.HeadOfStateBirthYear); Mix(ref hash, nation.HeadOfStateSinceDay);
            MixText(ref hash, nation.HeadOfGovernmentName); MixText(ref hash, nation.HeadOfGovernmentTrait);
            Mix(ref hash, nation.HeadOfGovernmentBirthYear); Mix(ref hash, nation.HeadOfGovernmentSinceDay);
            Mix(ref hash, nation.NextElectionDay); Mix(ref hash, nation.LastCabinetChangeDay); Mix(ref hash, nation.RulerGeneration);
            Mix(ref hash, nation.Cabinet.Count);
            foreach (var entry in nation.Cabinet.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            { MixText(ref hash, entry.Key.ToLowerInvariant()); MixText(ref hash, entry.Value); }
        }
    }

    private static void MixStateAdministration(ScenarioState state, ref ulong hash)
    {
        // Who administers it although another owns it, and since when (HeldRegions).
        if (state.HeldById != -1 || state.HeldSinceDay != 0)
        { MixText(ref hash, "held"); Mix(ref hash, state.HeldById); Mix(ref hash, state.HeldSinceDay); }
        if (state.AdministeredById >= 0)
        {
            MixText(ref hash, "parallel_administration");
            Mix(ref hash, state.AdministeredById); Mix(ref hash, state.AdministeredSinceDay);
            Mix(ref hash, state.AdministrationExposed ? 1 : 0); MixFloat(ref hash, state.AdministrationTaken);
            if (state.AdministrationProvinces.Count > 0 || state.AdministrationPopulationShare != 1f)
            {
                MixText(ref hash, "administration_footprint"); MixFloat(ref hash, state.AdministrationPopulationShare);
                foreach (var province in state.AdministrationProvinces.OrderBy(p => p)) Mix(ref hash, province);
            }
        }
    }

    private static void MixStateBordersAndAutonomy(ScenarioState state, ref ulong hash)
    {
        Mix(ref hash, state.RememberedOwner); Mix(ref hash, state.SettledBorderDays); Mix(ref hash, state.LastBorderReviewDay);
        MixFloat(ref hash, state.BorderHabit);
        if (state.Acceptance != 0f || state.ExhaustedUntilDay != -1 || state.AutonomousSinceDay != -1 ||
            state.AutonomyRevokedDay != -1 || state.AcceptanceOf != -1)
        {
            MixText(ref hash, "conquest_aftermath");
            MixFloat(ref hash, state.Acceptance); Mix(ref hash, state.ExhaustedUntilDay);
            Mix(ref hash, state.AutonomousSinceDay); Mix(ref hash, state.AutonomyRevokedDay);
            Mix(ref hash, state.AcceptanceOf);
        }
        if (state.YoungShare != 0.30f || state.OldShare != 0.09f)
        {
            MixText(ref hash, "demographic_cycle");
            MixFloat(ref hash, state.YoungShare); MixFloat(ref hash, state.OldShare);
        }
        foreach (var tag in state.CoreTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) MixText(ref hash, tag);
        Mix(ref hash, -18);
        foreach (var tag in state.Claims.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) MixText(ref hash, tag);
        Mix(ref hash, -19);
    }

    private static void MixProvinceControllers(Campaign campaign, ref ulong hash)
    {
        var controllers = campaign.ProvinceController ?? campaign.PendingProvinceController;
        if (controllers is null) return;
        for (var province = 0; province < controllers.Length; province++) Mix(ref hash, controllers[province]);
    }
}
