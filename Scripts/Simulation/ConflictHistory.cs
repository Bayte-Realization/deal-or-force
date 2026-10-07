using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// The tensions and the backers the bookmark opens with: history/conflicts/2024.
///
/// The world of 1 January 2024 was not at peace with itself between its wars. Korea was
/// divided by an armistice and shelling near Yeonpyeong, Israel and Hezbollah had been firing
/// across the Lebanese border for three months, Venezuela had just voted to annex most of
/// Guyana, Iran and Pakistan were a fortnight from striking each other. And the war that was
/// being fought had backers: the countries arming Ukraine and the ones arming Russia.
///
///   "tensions": [{ "between": ["KOR", "PRK"], "structural": 55, "tension": 65,
///                  "grievance": [20, 20] }]
///     structural   the part of it no week of good behaviour removes (an armistice line)
///     tension      where it stands on the first day
///     grievance    what each holds against the other, in the order of `between`
///
///   "sponsorships": [{ "sponsor": "USA", "client": "UKR", "war": ["RUS", "UKR"], "level": 3 }]
///     the war is named by the two countries leading it
///
/// Applied after the opening wars exist. A later file naming the same pair replaces it.
/// </summary>
public static class ConflictHistory
{
    public static void Apply(Campaign campaign)
    {
        if (campaign is null) return;
        foreach (var file in GameDataLoader.DataFiles("history", "conflicts", "2024"))
        {
            if (file is null || !File.Exists(file)) continue;
            try { Apply(campaign, StructuredData.ReadJson(file), null); }
            catch (Exception exception)
            {
                DiagnosticLogs.Write("simulation", $"HISTORY_CONFLICTS_FAILED file={Path.GetFileName(file)} error={exception.Message}");
            }
        }
    }

    public static void Apply(Campaign campaign, string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("tensions", out var tensions) && tensions.ValueKind == JsonValueKind.Array)
            foreach (var row in tensions.EnumerateArray())
            {
                var pair = Pair(campaign, row, "between", errors);
                if (pair is null) continue;
                var (first, second) = pair.Value;
                var relation = CampaignDiplomacy.Get(campaign, first.Id, second.Id);
                if (relation is null) continue;
                if (Number(row, "structural") is { } structural) relation.StructuralTension = M.Clamp(structural, 0f, 100f);
                if (Number(row, "tension") is { } tension) relation.Tension = M.Clamp(tension, 0f, 100f);
                if (row.TryGetProperty("grievance", out var grievance) && grievance.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var value in grievance.EnumerateArray())
                    {
                        if (!value.TryGetSingle(out var amount)) { index++; continue; }
                        var holder = index == 0 ? first : second;
                        if (holder.Id == relation.A) relation.GrievanceA = M.Clamp(amount, 0f, 100f);
                        else relation.GrievanceB = M.Clamp(amount, 0f, 100f);
                        index++;
                    }
                }
                // The stage it opens at, so the first week does not announce a crisis that was already old.
                var defines = campaign.Defines;
                relation.TensionStage = relation.Tension >= defines.TensionClashesAt ? (int)TensionStage.Clashes
                    : relation.Tension >= defines.TensionCrisisAt ? (int)TensionStage.Crisis
                    : relation.Tension >= defines.TensionFrictionAt ? (int)TensionStage.Friction : (int)TensionStage.Calm;
                relation.TensionStageDay = campaign.ElapsedDays;
            }

        if (root.TryGetProperty("sponsorships", out var sponsorships) && sponsorships.ValueKind == JsonValueKind.Array)
            foreach (var row in sponsorships.EnumerateArray())
            {
                var sponsor = campaign.NationByTag(Text(row, "sponsor"));
                var client = campaign.NationByTag(Text(row, "client"));
                var warPair = Pair(campaign, row, "war", errors);
                if (sponsor is null || client is null || warPair is null)
                {
                    errors?.Add($"history/conflicts: unknown sponsor, client or war in '{row.GetRawText()}'.");
                    continue;
                }
                StrategicWar war = null;
                foreach (var candidate in campaign.Wars)
                    if (!candidate.Ended && candidate.Involves(warPair.Value.Item1.Id) && candidate.Involves(warPair.Value.Item2.Id))
                    { war = candidate; break; }
                if (war is null) { errors?.Add($"history/conflicts: no war between {warPair.Value.Item1.Tag} and {warPair.Value.Item2.Tag}."); continue; }
                var level = row.TryGetProperty("level", out var levelValue) && levelValue.TryGetInt32(out var parsed) ? Math.Clamp(parsed, 1, 3) : 1;
                level = Math.Min(level, ProxyWars.MaximumLevel(sponsor));
                if (level < 1) continue;
                campaign.Sponsorships.RemoveAll(s => s.SponsorId == sponsor.Id && s.WarId == war.Id);
                campaign.Sponsorships.Add(new Sponsorship
                {
                    Id = campaign.NextSponsorshipId++, SponsorId = sponsor.Id, ClientId = client.Id, WarId = war.Id,
                    Level = level, StartedDay = campaign.ElapsedDays,
                    CommittedUntilDay = campaign.ElapsedDays + Math.Max(0, campaign.Defines.ProxyHistoricCommitmentDays)
                });
                OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, sponsor.Id, client.Id), "armed_us");
                foreach (var enemyId in war.EnemiesOf(client.Id))
                    if (enemyId != sponsor.Id && CampaignDiplomacy.Get(campaign, sponsor.Id, enemyId) is { } hostile)
                        OpinionSystem.Remember(campaign, hostile, "armed_our_enemy");
            }
    }

    private static (Nation, Nation)? Pair(Campaign campaign, JsonElement row, string key, List<string> errors)
    {
        if (!row.TryGetProperty(key, out var between) || between.ValueKind != JsonValueKind.Array) return null;
        var found = new List<Nation>();
        foreach (var tag in between.EnumerateArray())
        {
            var nation = campaign.NationByTag(tag.GetString() ?? "");
            if (nation is null) errors?.Add($"history/conflicts: unknown country '{tag.GetString()}'.");
            else found.Add(nation);
        }
        if (found.Count != 2 || found[0].Id == found[1].Id) return null;
        return (found[0], found[1]);
    }

    private static string Text(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static float? Number(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.TryGetSingle(out var number) ? number : null;
}
