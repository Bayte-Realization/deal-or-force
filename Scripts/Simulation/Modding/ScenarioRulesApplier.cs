using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Applies a declarative <see cref="ScenarioManifest"/> (custom start date, display calendar,
/// era feature availability, base-diplomacy/war clearing, starting alliances, truces, wars,
/// featured countries, skin/theme and music hooks) onto a live <see cref="Campaign"/>.
///
/// Leaves the default 2024 scenario completely untouched so base-game behavior, saves and
/// <see cref="CampaignFingerprint"/> remain bit-for-bit identical.
/// </summary>
public static class ScenarioRulesApplier
{
    public static List<string> Apply(Campaign campaign, ScenarioManifest manifest)
    {
        var warnings = new List<string>();
        if (campaign is null || manifest is null) return warnings;

        if (!string.IsNullOrWhiteSpace(manifest.Id))
            campaign.ScenarioId = manifest.Id;

        if (!string.IsNullOrWhiteSpace(manifest.EffectiveSkin))
            campaign.SkinOverlay = manifest.EffectiveSkin;

        // 1. Start date (validated strictly; invalid dates are rejected with a diagnostic instead of rolling silently).
        if (manifest.HasExplicitStartDate)
        {
            if (manifest.TryResolveStartDate(out var startDate, out var dateError))
            {
                campaign.Defines.StartYear = startDate.Year;
                campaign.Defines.StartMonth = startDate.Month;
                campaign.Defines.StartDay = startDate.Day;
                campaign.Date = new GameDate(startDate.Year, startDate.Month, startDate.Day);
            }
            else
            {
                warnings.Add($"scenario '{manifest.Id}': {dateError}");
            }
        }

        // 2. Display calendar profile (separated from internal Gregorian GameDate).
        if (manifest.Calendar is not null && !manifest.Calendar.IsDefault)
        {
            var calErrors = manifest.Calendar.Validate();
            if (calErrors.Count == 0)
                campaign.Calendar = manifest.Calendar.Clone();
            else
                foreach (var err in calErrors)
                    warnings.Add($"scenario '{manifest.Id}': {err}");
        }

        // 3. Era features & disabled features (tri-state: available / restricted / unavailable).
        ApplyEraFeatures(campaign, manifest.DisabledFeatures, manifest.EraFeatures, warnings);

        // 4. Optional base-2024 war/diplomacy clearing before seeding scenario-specific diplomacy.
        if (manifest.ClearBaseWars || (manifest.TotalConversion && manifest.BaseWorld))
            ClearWars(campaign);
        if (manifest.ClearBaseDiplomacy || (manifest.TotalConversion && manifest.BaseWorld))
            ClearDiplomacy(campaign);

        // 5. Starting alliances, truces, and wars.
        ApplyStartingAlliances(campaign, manifest.StartingAlliances, warnings);
        ApplyStartingTruces(campaign, manifest.StartingTruces, warnings);
        ApplyStartingWars(campaign, manifest.StartingWars, warnings);

        // 6. Player seat if explicitly named and present.
        if (!string.IsNullOrWhiteSpace(manifest.PlayerCountry))
        {
            var seat = campaign.NationByTag(manifest.PlayerCountry);
            if (seat is not null)
                campaign.SetPlayer(seat.Id);
            else
                warnings.Add($"scenario '{manifest.Id}': player_country '{manifest.PlayerCountry}' was not found in the world.");
        }

        // 7. Compact scenario metadata (only populated when non-default so 2024 saves/fingerprints stay untouched).
        if (manifest.HasDeclarativeRules || !string.Equals(manifest.Id, "2024", StringComparison.OrdinalIgnoreCase))
        {
            var meta = new ScenarioMetadata
            {
                ScenarioName = manifest.Name ?? "",
                EraName = manifest.Calendar?.EraName ?? "",
                Theme = manifest.EffectiveSkin ?? "",
                MusicTrack = manifest.MusicTrack ?? "",
                MusicPlaylist = manifest.MusicPlaylist ?? "",
                TotalConversion = manifest.TotalConversion,
                ContentSignature = ModOverlayPolicy.ContentSignature ?? "",
                FeaturedCountries = manifest.FeaturedCountries
                    .Select(f => f.Tag)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
            if (!meta.IsEmpty)
                campaign.ScenarioMeta = meta;
        }

        return warnings;
    }

    /// <summary>
    /// Apply manifest-level <c>disabled_features</c> and <c>era_features</c> onto a campaign.
    /// Used by both <see cref="Apply"/> and mod-package loading.
    /// </summary>
    public static void ApplyEraFeatures(
        Campaign campaign,
        IEnumerable<string> disabledFeatures,
        IDictionary<string, string> eraFeatures,
        List<string> errors)
    {
        if (campaign is null) return;
        var hasAny = (disabledFeatures is not null && disabledFeatures.Any()) ||
                     (eraFeatures is not null && eraFeatures.Count > 0);
        if (!hasAny) return;

        var resolveErrors = new List<string>();
        var resolved = EraFeatureRegistry.Resolve(disabledFeatures, eraFeatures, resolveErrors);
        if (errors is not null) errors.AddRange(resolveErrors);

        foreach (var (featureId, state) in resolved.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            switch (state)
            {
                case EraFeatureState.Available:
                    campaign.RestrictedFeatures?.Remove(featureId);
                    campaign.SetFeature(featureId, true);
                    break;

                case EraFeatureState.Restricted:
                    campaign.RestrictedFeatures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    campaign.RestrictedFeatures.Add(featureId);
                    campaign.SetFeature(featureId, false);
                    break;

                case EraFeatureState.Unavailable:
                    campaign.RestrictedFeatures?.Remove(featureId);
                    campaign.Defines.FeatureUnlocks?.Remove(featureId);
                    campaign.SetFeature(featureId, false);
                    break;
            }
        }
    }

    private static void ClearWars(Campaign campaign)
    {
        if (campaign.Wars.Count == 0 && campaign.Sponsorships.Count == 0) return;
        campaign.Wars.Clear();
        campaign.Sponsorships.Clear();
        foreach (var relation in campaign.Relations.Values)
            if (relation.Stance == DiplomaticStance.AtWar)
                relation.Stance = DiplomaticStance.Neutral;
        campaign.MarkWarsChanged();
        campaign.MarkStrategicChange();
    }

    private static void ClearDiplomacy(Campaign campaign)
    {
        var changedAlliance = false;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.Stance == DiplomaticStance.Allied)
            {
                relation.Stance = DiplomaticStance.Neutral;
                changedAlliance = true;
            }
            else if (relation.Stance == DiplomaticStance.Truce)
            {
                relation.Stance = DiplomaticStance.Neutral;
            }
            relation.GuaranteeByA = false;
            relation.GuaranteeByB = false;
            relation.NonAggressionPact = false;
            if (relation.TradeAgreement)
                AgreementSystem.End(campaign, relation, AgreementSystem.TradeEnd.Cancelled);
            relation.TruceUntil = 0;
        }
        if (campaign.Organizations.Count > 0)
        {
            campaign.Organizations.Clear();
            changedAlliance = true;
        }
        if (changedAlliance) campaign.NoteAllianceChange();
        campaign.MarkStrategicChange();
    }

    private static void ApplyStartingAlliances(Campaign campaign, List<StartingAllianceSpec> alliances, List<string> warnings)
    {
        if (alliances is null || alliances.Count == 0) return;
        foreach (var bloc in alliances)
        {
            var members = new List<Nation>();
            foreach (var tag in bloc.Members)
            {
                var nation = campaign.NationByTag(tag);
                if (nation is null)
                    warnings.Add($"starting_alliances: unknown country tag '{tag}' in '{bloc.Name}'.");
                else if (members.All(m => m.Id != nation.Id))
                    members.Add(nation);
            }
            if (members.Count < 2) continue;

            if (members.Count > 2 && campaign.FeatureEnabled("organizations") && !string.IsNullOrWhiteSpace(bloc.Name))
            {
                var charter = string.IsNullOrWhiteSpace(bloc.Charter) ? "collective_defence" : bloc.Charter;
                if (InternationalOrganizations.Seed(campaign, bloc.Name, charter, members.Select(m => m.Id)) is not null)
                    continue;
            }

            for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var relation = CampaignDiplomacy.Get(campaign, members[i].Id, members[j].Id);
                if (relation.Stance == DiplomaticStance.AtWar)
                {
                    warnings.Add($"starting_alliances: skipping alliance between {members[i].Tag} and {members[j].Tag} (already at war).");
                    continue;
                }
                relation.Stance = DiplomaticStance.Allied;
                relation.AllianceSinceDay = campaign.ElapsedDays;
                OpinionSystem.Remember(campaign, relation, "alliance");
            }
            campaign.NoteAllianceChange();
        }
    }

    private static void ApplyStartingTruces(Campaign campaign, List<StartingTruceSpec> truces, List<string> warnings)
    {
        if (truces is null || truces.Count == 0) return;
        foreach (var truce in truces)
        {
            if (truce.Between is not { Count: 2 }) continue;
            var a = campaign.NationByTag(truce.Between[0]);
            var b = campaign.NationByTag(truce.Between[1]);
            if (a is null || b is null || a.Id == b.Id)
            {
                warnings.Add($"starting_truces: invalid pair [{truce.Between[0]}, {truce.Between[1]}].");
                continue;
            }
            if (truce.Days <= 0)
            {
                warnings.Add($"starting_truces: non-positive truce days ({truce.Days}) for [{a.Tag}, {b.Tag}].");
                continue;
            }
            var relation = CampaignDiplomacy.Get(campaign, a.Id, b.Id);
            if (relation.Stance == DiplomaticStance.AtWar)
            {
                warnings.Add($"starting_truces: skipping truce between {a.Tag} and {b.Tag} because they are at war.");
                continue;
            }
            relation.TruceUntil = Math.Max(relation.TruceUntil, campaign.ElapsedDays + truce.Days);
            if (relation.Stance == DiplomaticStance.Neutral)
                relation.Stance = DiplomaticStance.Truce;
        }
    }

    private static void ApplyStartingWars(Campaign campaign, List<StartingWarSpec> wars, List<string> warnings)
    {
        if (wars is null || wars.Count == 0) return;
        var addedAny = false;
        foreach (var spec in wars)
        {
            var attacker = campaign.NationByTag(spec.Attacker);
            var defender = campaign.NationByTag(spec.Defender);
            if (attacker is null || defender is null || attacker.Id == defender.Id)
            {
                warnings.Add($"starting_wars: invalid war pair '{spec.Attacker}' vs '{spec.Defender}'.");
                continue;
            }
            var relation = CampaignDiplomacy.Get(campaign, attacker.Id, defender.Id);
            if (relation.Stance == DiplomaticStance.AtWar) continue;
            if (relation.Stance == DiplomaticStance.Allied) campaign.NoteAllianceChange();
            relation.Stance = DiplomaticStance.AtWar;
            relation.TruceUntil = 0;
            relation.NonAggressionPact = false;

            var war = new StrategicWar
            {
                Id = campaign.Wars.Count == 0 ? 0 : campaign.Wars.Max(w => w.Id) + 1,
                Name = string.IsNullOrWhiteSpace(spec.Name) ? $"{attacker.Name}–{defender.Name} War" : spec.Name,
                AttackerId = attacker.Id,
                DefenderId = defender.Id,
                Justification = string.IsNullOrWhiteSpace(spec.Justification) ? "scenario" : spec.Justification,
                StartedDay = campaign.ElapsedDays,
                AiNegotiationUnlockDay = campaign.ElapsedDays + Math.Max(0, campaign.Defines.ScenarioWarNegotiationLockDays),
                Limited = spec.Limited,
                ClaimedStateMapIndex = campaign.States
                    .Where(s => s.NationMapIndex == defender.MapIndex)
                    .OrderByDescending(s => s.Population)
                    .FirstOrDefault()?.MapIndex ?? -1
            };
            campaign.Wars.Add(war);
            OpinionSystem.Remember(campaign, relation, "declared_war_on_us");
            OpinionSystem.Remember(campaign, relation, "fought_a_war");
            addedAny = true;
        }
        if (addedAny)
        {
            campaign.MarkWarsChanged();
            if (campaign.ProvinceMap is not null)
                Warfare.RebuildFronts(campaign);
            campaign.MarkStrategicChange();
        }
    }
}
