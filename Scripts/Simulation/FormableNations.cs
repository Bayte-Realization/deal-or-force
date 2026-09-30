using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record FormableNation(string Id, string Name, string Description, string[] EligibleTags,
    string[] RequiredTags, int[] RequiredProvinces, float MinimumStability, float CostShare,
    float GovernanceGain, float ProductivityGain,
    string[] RequiredIdeology, string[] RequiredGovernment, string Colour);
public sealed record FormationRequirement(string Label, bool Met);
public sealed record FormationStatus(FormationRequirement[] Requirements, int Owned, int Total, float Cost)
{
    public bool CanForm => Requirements.Length > 0 && Requirements.All(r => r.Met);
}

/// <summary>Territories refer to authored scenario ownership, not shifting live borders.</summary>
public static class FormableNations
{
    private static readonly Dictionary<string, FormableNation> Definitions = new(StringComparer.Ordinal);
    public static IEnumerable<FormableNation> All => Definitions.Values.OrderBy(d => d.Id, StringComparer.Ordinal);
    static FormableNations() => ResetCatalog();
    public static void ResetCatalog()
    {
        Definitions.Clear();
        foreach (var doc in GameDataLoader.DataDocuments("common", "formable_nations"))
            using (doc) Merge(doc.RootElement, null);
    }
    public static FormableNation Find(string id) => id is not null ? Definitions.GetValueOrDefault(id) : null;
    public static bool Eligible(Nation nation, FormableNation definition) =>
        nation is not null && definition.EligibleTags.Contains(nation.Tag, StringComparer.OrdinalIgnoreCase);
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("formable_nations: expected object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            try
            {
                var e = entry.Value;
                if (e.ValueKind != JsonValueKind.Object) throw new FormatException("expected object");
                if (e.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
                { Definitions.Remove(entry.Name); continue; }
                var old = Find(entry.Name);
                string Text(string key, string fallback) => e.TryGetProperty(key, out var x) ? x.GetString() : fallback;
                string[] Tags(string key, string[] fallback) => e.TryGetProperty(key, out var x)
                    ? x.EnumerateArray().Select(v => v.GetString()?.ToUpperInvariant() ?? "").Where(v => v.Length > 0).Distinct().ToArray()
                    : fallback ?? Array.Empty<string>();
                float Number(string key, float fallback) => e.TryGetProperty(key, out var x) ? x.GetSingle() : fallback;
                // Lower case rather than upper: these name ideologies and forms of
                // government, which are ids in common/, not country tags.
                string[] Ids(string key, string[] fallback) => e.TryGetProperty(key, out var x)
                    ? x.EnumerateArray().Select(v => v.GetString()?.ToLowerInvariant() ?? "").Where(v => v.Length > 0).Distinct().ToArray()
                    : fallback ?? Array.Empty<string>();
                var d = new FormableNation(entry.Name, Text("name", old?.Name ?? entry.Name), Text("description", old?.Description ?? ""),
                    Tags("eligible_tags", old?.EligibleTags), Tags("required_tags", old?.RequiredTags),
                    e.TryGetProperty("required_provinces", out var ps) ? ps.EnumerateArray().Select(p => p.GetInt32()).Distinct().ToArray() : old?.RequiredProvinces ?? Array.Empty<int>(),
                    Number("minimum_stability", old?.MinimumStability ?? .65f), Number("gdp_cost_share", old?.CostShare ?? .02f),
                    Number("governance_gain", old?.GovernanceGain ?? .04f), Number("productivity_gain", old?.ProductivityGain ?? .015f),
                    Ids("required_ideology", old?.RequiredIdeology), Ids("required_government", old?.RequiredGovernment),
                    Text("colour", old?.Colour ?? ""));
                if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name) || d.EligibleTags.Length == 0 || (d.RequiredTags.Length == 0 && d.RequiredProvinces.Length == 0) ||
                    d.RequiredProvinces.Any(p => p <= 0) || new[] { d.MinimumStability, d.CostShare, d.GovernanceGain, d.ProductivityGain }.Any(v => !float.IsFinite(v) || v < 0 || v > 1))
                    throw new FormatException("empty eligibility/territory or values outside 0..1");
                if (d.RequiredIdeology.Any(id => !IdeologyCatalog.Knows(id)))
                    throw new FormatException("names an ideology nothing declares");
                if (d.RequiredGovernment.Any(id => !GovernmentCatalog.Knows(id)))
                    throw new FormatException("names a form of government nothing declares");
                Definitions[entry.Name] = d;
            }
            catch (Exception ex) { errors?.Add($"formable '{entry.Name}': {ex.Message}"); }
        }
    }
    public static FormationStatus Evaluate(Campaign campaign, Nation nation, FormableNation d)
    {
        if (campaign is null || nation is null || d is null) return new(Array.Empty<FormationRequirement>(), 0, 0, 0);
        var map = campaign.ProvinceMap;
        var requirements = new List<FormationRequirement>
        {
            new(Loc.Get("Eligible founding country"), Eligible(nation, d)),
            new(Loc.Get("No state already proclaimed"), string.IsNullOrEmpty(nation.FormedNationId)),
            new(Loc.Get("At peace"), !campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id))),
            new(Loc.Format("Stability: {0:P0} / {1:P0}", nation.Stability, d.MinimumStability), nation.Stability >= d.MinimumStability),
            new(Loc.Get("Territorial map ready"), map is not null && campaign.ProvinceController is not null)
        };
        var provinces = new HashSet<int>(d.RequiredProvinces);
        foreach (var tag in d.RequiredTags)
        {
            var authored = campaign.ScenarioId == "2024" ? ProvinceOwnership.ProvincesOf(tag) :
                campaign.ScenarioTerritories.GetValueOrDefault(tag, Array.Empty<int>());
            requirements.Add(new(Loc.Format("Scenario territory: {0}", tag), authored.Length > 0));
            provinces.UnionWith(authored);
        }
        bool Held(int p) => map is not null && p > 0 && p <= map.ProvinceCount &&
            !map.WaterByProvince[p] && map.ProvinceToNation[p] == nation.MapIndex &&
            campaign.ProvinceController is not null && p < campaign.ProvinceController.Length && campaign.ProvinceController[p] == nation.MapIndex;
        // Unresolved IDs fail closed rather than silently disappearing.
        var land = provinces.Where(p => map is null || p > map.ProvinceCount || !map.WaterByProvince[p]).ToArray();
        var owned = land.Count(Held);
        requirements.Add(new(Loc.Format("Territory owned and controlled: {0}/{1}", owned, land.Length), land.Length > 0 && owned == land.Length));
        var cost = Math.Max(.05f, nation.Gdp * d.CostShare);
        requirements.Add(new(Loc.Get("Treasury covers the founding cost"), nation.Treasury >= cost));
        requirements.Add(new(Loc.Get("No other country has proclaimed this state"), !campaign.Nations.Any(n => n.FormedNationId == d.Id)));

        // What kind of state may proclaim it. A caliphate is not something a parliamentary
        // republic declares, and the Soviet Union is not proclaimed by a monarchy: for the
        // states where that is the whole character of the thing, the politics are part of
        // the price. Most formables name neither and are open to anybody.
        if (d.RequiredIdeology.Length > 0)
            requirements.Add(new(Loc.Format("Ideology: {0}",
                    string.Join(", ", d.RequiredIdeology.Select(id => Loc.Get(IdeologyCatalog.Of(id).Name)))),
                d.RequiredIdeology.Contains(nation.Ideology, StringComparer.OrdinalIgnoreCase)));
        if (d.RequiredGovernment.Length > 0)
            requirements.Add(new(Loc.Format("Government: {0}",
                    string.Join(", ", d.RequiredGovernment.Select(id => Loc.Get(GovernmentCatalog.Of(id).Name)))),
                d.RequiredGovernment.Contains(nation.Government, StringComparer.OrdinalIgnoreCase)));
        return new(requirements.ToArray(), owned, land.Length, cost);
    }
}

public sealed class FormNationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string FormableId { get; init; } = "";
    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        var d = FormableNations.Find(FormableId);
        var status = FormableNations.Evaluate(campaign, nation, d);
        if (!status.CanForm) return;
        nation.Treasury -= status.Cost;
        nation.FormedNationId = d.Id;
        nation.FormedNationDay = campaign.ElapsedDays;
        nation.Name = d.Name;
        // And its own colour, where one was declared. A proclaimed state that keeps the
        // founder's colour has changed nothing on the one screen a player spends the game
        // looking at. The flag needs no line here: FlagCatalog reads what has been
        // proclaimed as a qualifier, so gfx/flags/flag_formable_<id>.png is the whole of it.
        if (!string.IsNullOrWhiteSpace(d.Colour)) nation.Color = d.Colour;
        var limit = StabilityCore.Weights.GovernanceLimit;
        nation.StabilityGovernance = M.Clamp(nation.StabilityGovernance + d.GovernanceGain, -limit, limit);
        nation.Productivity += d.ProductivityGain;
        campaign.Chronicle.Record(campaign, "state_formed", nation.Id, subject: d.Name);
        // IDs, diplomacy, armies, government and flags remain valid. No territory is granted.
        campaign.MarkStrategicChange();
    }
}
