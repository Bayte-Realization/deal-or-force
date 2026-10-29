using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>Exercise each new law through the same command used by the policy workspace.</summary>
static class LawOptionIntegrationTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL law options: " + label); Console.WriteLine("PASS law options: " + label); }

    static (Campaign Campaign, Nation Nation, Nation Other) World(bool atWar = false)
    {
        var c = new Campaign(20261026);
        var n = c.AddNation(new Nation { Tag = "AAA", Name = "Law fixture", MapIndex = 501,
            Gdp = 100, Treasury = 100, Population = 2_000_000, Stability = .6f, OutputScale = 1,
            TaxRate = .23f, MilitarySpending = .031f, WelfareSpending = .043f, EducationSpending = .017f });
        var other = c.AddNation(new Nation { Tag = "BBB", Name = "Other", MapIndex = 502,
            Gdp = 100, Treasury = 100, Population = 1_000_000, Stability = .6f, OutputScale = 1 });
        var third = c.AddNation(new Nation { Tag = "CCC", Name = "Unrelated", MapIndex = 503,
            Gdp = 100, Treasury = 100, Population = 1_000_000, Stability = .6f, OutputScale = 1 });
        foreach (var (id, map, owner, ownerMap) in new[] {
            ("one", 1001, "AAA", 501), ("two", 1002, "AAA", 501), ("three", 1003, "BBB", 502), ("four", 1004, "CCC", 503) })
            c.States.Add(new ScenarioState { Id = id, MapIndex = map, NationId = owner,
                NationMapIndex = ownerMap, Population = 1_000_000, Stability = .5f, StandardOfLiving = .5f, Radicalism = .5f });
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002, 1003, 1004 },
            new ushort[] { 501, 501, 502, 503 }, 2, 2, _ => false));
        if (atWar) c.Wars.Add(new StrategicWar { AttackerId = n.Id, DefenderId = other.Id });
        c.SetPlayer(n.Id);
        SovereignFinance.NormalizeAccounts(c);
        EconomySystem.Explain(c);
        return (c, n, other);
    }

    static string Sliders(Nation n) => string.Join("|", new[] { n.TaxRate, n.MilitarySpending,
        n.WelfareSpending, n.EducationSpending }.Select(BitConverter.SingleToInt32Bits));
    static bool Near(float a, float b) => MathF.Abs(a - b) < .000002f;
    static void Day(Campaign c, int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);

    public static void Run()
    {
        PoliticalPolicyCatalog.ResetCatalog();
        using var additions = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "common", "laws", "40_expanded_laws.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var ids = additions.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith("//")).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Check(ids.Count == 69, "69 alternatives are explicitly defined");
        var secondPack = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in GameDataLoader.DataFiles("common", "laws").Where(path =>
                     string.CompareOrdinal(Path.GetFileName(path), "50_") >= 0 &&
                     string.CompareOrdinal(Path.GetFileName(path), "56_") < 0))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            foreach (var row in doc.RootElement.EnumerateObject())
                if (!row.Name.StartsWith("//")) secondPack.Add(row.Name);
        }
        Check(secondPack.Count is 0 or 69 && !ids.Overlaps(secondPack), "second expansion is complete and does not overwrite first-pack ids");
        ids.UnionWith(secondPack);
        var laws = PoliticalPolicyCatalog.AllLaws.ToArray();
        var categories = laws.Where(p => p.Default).Select(p => (p.Scope, p.Category)).ToHashSet();
        Check(laws.Length >= 140 && categories.Count == 23 && ids.All(id => laws.Any(p => p.Id == id)),
            laws.Length + " loaded laws retain the original 23 categories, including every tested addition");
        Check(laws.All(p => categories.Contains((p.Scope, p.Category))), "all expanded laws keep their category's original national, regional or foreign scope");
        foreach (var category in categories)
        {
            var group = laws.Where(p => (p.Scope, p.Category) == category).ToArray();
            Check(group.Count(p => ids.Contains(p.Id)) == (secondPack.Count == 0 ? 3 : 6), category.Category + " receives alternatives inside its existing scope");
            Check(group.Count(p => p.Default) == 1 && group.Single(p => p.Default).Id is { } baseline && !ids.Contains(baseline),
                category.Category + " keeps its original institutional baseline");
        }
        foreach (var id in ids.OrderBy(id => id, StringComparer.Ordinal))
        {
            var p = PoliticalPolicyCatalog.Find(id);
            Check(p is not null && p.Description.Length > 0 && p.CostShare > 0 && p.CooldownDays >= 90,
                id + " has explanation, real payment and reform cooldown");
            Check(categories.Contains((p.Scope, p.Category)) && !p.Default, id + " extends an existing slot, never an extra stacking slot");
            Check(p.Modifiers.All(m => NationalModifiers.Knows(m.Key) && float.IsFinite(m.Value)) &&
                (p.Modifiers.Count > 0 || p.Scope == "foreign" && p.ForeignTrust != 0), id + " uses real finite effects");
            foreach (var language in new[] { "english", "russian" })
                Check(Loc.Keys(language).Contains(p.Name) && Loc.Keys(language).Contains(p.Description), id + " name and effects are explained in " + language);
            Check(File.Exists(Path.Combine(GameDataLoader.DataFolder(), "gfx", "interface", "icons", "icon_" + p.Icon + ".png")),
                id + " uses an existing icon");
            ApplyOne(p);
        }
        WartimeRequirements();
        AiWartimeChoice();
        RejectedScopedChoices();
        SavedScopedChoices();
    }

    static void ApplyOne(PoliticalPolicy p)
    {
        var (c, n, other) = World(p.RequiresWar);
        var region = c.StatesOf(n).First();
        var otherOwn = c.StatesOf(n).Last();
        var foreign = c.StatesOf(other).First();
        var target = p.Scope == "foreign" ? other.Tag : "";
        var selected = p.Scope == "region" ? region.MapIndex : 0;
        var hash = CampaignFingerprint.Of(c);
        Check(PoliticalPolicies.CanAdopt(c, n, p, target, selected, false, out var reason), p.Id + " can be adopted: " + reason);
        _ = NationalModifiers.Lines(p.Modifiers).ToArray();
        _ = PoliticalPolicies.Effective(n, p, target, selected);
        Check(hash == CampaignFingerprint.Of(c), p.Id + " preview and eligibility are read-only");
        var cash = n.Treasury;
        var settings = Sliders(n);
        var before = p.Modifiers.ToDictionary(m => m.Key, m => NationalModifiers.Of(n, m.Key));
        c.Commands.Enqueue(new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = p.Id, TargetTag = target, Region = selected });
        c.Commands.ApplyAll(c);
        Check(n.Laws.GetValueOrDefault(PoliticalPolicies.Slot(p, target, selected))?.Id == p.Id &&
            Near(n.Treasury, cash - n.Gdp * p.CostShare) && c.ElapsedHours == 0 && c.IsPaused && Sliders(n) == settings,
            p.Id + " command pays and applies on pause without changing budget sliders");
        Check(n.PolicyReadyOnDay[PoliticalPolicies.Slot(p, target, selected)] == p.CooldownDays,
            p.Id + " cooldown uses the same declared terms");
        var paid = CampaignFingerprint.Of(c);
        new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = p.Id, TargetTag = target, Region = selected }.Apply(c);
        var alternative = PoliticalPolicyCatalog.Baseline(p);
        new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = alternative.Id, TargetTag = target, Region = selected }.Apply(c);
        Check(paid == CampaignFingerprint.Of(c), p.Id + " repetition and immediate reversal cannot evade cost or cooldown");
        if (p.Scope == "nation")
        {
            foreach (var (key, value) in p.Modifiers)
                Check(Near(NationalModifiers.Of(n, key), before[key] + value), p.Id + " reaches national calculation: " + key);
        }
        else if (p.Scope == "foreign")
        {
            Check(Near(PoliticalPolicies.ForeignTrust(c, n, other), p.ForeignTrust) &&
                PoliticalPolicies.ForeignTrust(c, n, c.NationByTag("CCC")) == 0, p.Id + " affects trust with its selected partner only");
        }
        else
        {
            var old = (region.Stability, region.StandardOfLiving, region.Radicalism);
            var unchangedOwn = (otherOwn.Stability, otherOwn.StandardOfLiving, otherOwn.Radicalism);
            var unchangedForeign = (foreign.Stability, foreign.StandardOfLiving, foreign.Radicalism);
            PoliticalPolicies.Tick(c);
            Check(Near(region.Stability, old.Stability + p.Modifiers.GetValueOrDefault("stability_weekly") / 7) &&
                Near(region.StandardOfLiving, old.StandardOfLiving + p.Modifiers.GetValueOrDefault("living_weekly") / 7) &&
                Near(region.Radicalism, old.Radicalism + p.Modifiers.GetValueOrDefault("radicalism_weekly") / 7), p.Id + " reaches selected region's daily calculation");
            Check((otherOwn.Stability, otherOwn.StandardOfLiving, otherOwn.Radicalism) == unchangedOwn &&
                (foreign.Stability, foreign.StandardOfLiving, foreign.Radicalism) == unchangedForeign,
                p.Id + " never leaks into another own or foreign region");
        }
        Day(c, p.CooldownDays);
        Check(PoliticalPolicies.Adopt(c, n, alternative, target, selected) && n.Laws.Count == 1 &&
            PoliticalPolicies.Effective(n, p, target, selected).Id == alternative.Id,
            p.Id + " can be replaced after cooldown, without stacking slots");
        if (p.Scope == "nation")
            Check(p.Modifiers.Keys.All(key => Near(NationalModifiers.Of(n, key), before[key])), p.Id + " repeal removes its modifiers immediately");
        if (p.Scope == "foreign")
            Check(PoliticalPolicies.ForeignTrust(c, n, other) == 0, p.Id + " repeal removes targeted trust immediately");
    }

    static void RejectedScopedChoices()
    {
        foreach (var id in new[] { "education_vocational", "regional_cultural_charter", "foreign_allied_integration" })
        {
            var (c, n, other) = World();
            var p = PoliticalPolicyCatalog.Find(id);
            var target = p.Scope == "foreign" ? other.Tag : "";
            var region = p.Scope == "region" ? c.StatesOf(n).First().MapIndex : 0;
            n.Treasury = 0;
            var hash = CampaignFingerprint.Of(c);
            Check(!PoliticalPolicies.CanAdopt(c, n, p, target, region, false, out var reason) &&
                reason == "Insufficient treasury for reform", id + " refuses an unfunded reform");
            new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = id, TargetTag = target, Region = region }.Apply(c);
            Check(hash == CampaignFingerprint.Of(c), id + " unfunded command cannot partially change the campaign");
        }
        {
            var (c, n, other) = World();
            var hash = CampaignFingerprint.Of(c);
            Check(!PoliticalPolicies.Adopt(c, n, PoliticalPolicyCatalog.Find("regional_cultural_charter"), region: c.StatesOf(other).First().MapIndex),
                "new regional law cannot be enacted in foreign territory");
            Check(!PoliticalPolicies.Adopt(c, n, PoliticalPolicyCatalog.Find("foreign_allied_integration"), n.Tag) &&
                !PoliticalPolicies.Adopt(c, n, PoliticalPolicyCatalog.Find("foreign_allied_integration"), "UNKNOWN"),
                "new foreign doctrine rejects self and missing targets");
            Check(hash == CampaignFingerprint.Of(c), "invalid scoped targets cannot charge or mutate the campaign");
        }
    }

    static void WartimeRequirements()
    {
        foreach (var id in new[] { "service_total_levy", "rights_martial_tribunals", "labour_wartime_shifts", "media_wartime_censorship" })
        {
            var (c, n, other) = World();
            var p = PoliticalPolicyCatalog.Find(id);
            var hash = CampaignFingerprint.Of(c);
            Check(p.RequiresWar && !PoliticalPolicies.CanAdopt(c, n, p, "", 0, false, out var reason) &&
                reason == "Requires an active war", id + " explains its wartime requirement in peace");
            new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = id }.Apply(c);
            Check(hash == CampaignFingerprint.Of(c), id + " cannot be enacted or charged in peace");
            c.Wars.Add(new StrategicWar { AttackerId = other.Id, DefenderId = c.NationByTag("CCC").Id });
            Check(!PoliticalPolicies.CanAdopt(c, n, p, "", 0, false, out _), id + " does not mistake a foreign war for its own");
            c.Wars.Add(new StrategicWar { AttackerId = n.Id, DefenderId = other.Id, Ended = true });
            Check(!PoliticalPolicies.CanAdopt(c, n, p, "", 0, false, out _), id + " does not count an ended war");
        c.Wars.Add(new StrategicWar { AttackerId = n.Id, DefenderId = other.Id });
            Check(PoliticalPolicies.Adopt(c, n, p), id + " accepts a real active war");
            c.Wars.Clear(); // Cleanup to ensure state is clean
        }
        var original = PoliticalPolicyCatalog.Find("fiscal_austerity");
        var errors = new List<string>();
        using var bad = JsonDocument.Parse("""{"fiscal_austerity":{"requires_war":"yes","cost_share":0}}""");
        PoliticalPolicyCatalog.MergeLaws(bad.RootElement, errors);
        Check(errors.Count > 0 && ReferenceEquals(original, PoliticalPolicyCatalog.Find(original.Id)), "malformed wartime overlay is rejected atomically");
    }

    static void SavedScopedChoices()
    {
        var (c, n, other) = World();
        var region = c.StatesOf(n).First();
        foreach (var id in new[] { "education_vocational", "regional_cultural_charter", "foreign_allied_integration" })
        {
            var p = PoliticalPolicyCatalog.Find(id);
            Check(PoliticalPolicies.Adopt(c, n, p, p.Scope == "foreign" ? other.Tag : "", p.Scope == "region" ? region.MapIndex : 0),
                id + " added to save fixture");
        }
        var copy = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "laws"), "laws");
        Check(copy.Success, "new laws reuse the existing save and authoritative network snapshot: " + copy.Warning);
        copy.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(copy.Campaign), "save/network roundtrip preserves all law ids, scopes, dates and cooldowns");
        c.SetFeature("laws", false);
        var before = CampaignFingerprint.Of(c);
        Check(!PoliticalPolicies.Adopt(c, n, PoliticalPolicyCatalog.Find("industry_champions")) && before == CampaignFingerprint.Of(c),
            "feature toggle disables additional reforms without spending money");
        Check(!NationalModifiers.Sources(n).Any(source => source.Source == PoliticalPolicyCatalog.Find("education_vocational").Name),
            "feature toggle also removes additional laws from effective national sources");
    }

    static void AiWartimeChoice()
    {
        foreach (var atWar in new[] { false, true })
        {
            var (c, _, other) = World(atWar);
            other.Ideology = "fascism";
            Day(c, 90);
            PoliticalPolicies.Tick(c);
            var actual = PoliticalPolicies.Effective(other, PoliticalPolicyCatalog.Find("service_professional"));
            Check(actual.Id == (atWar ? "service_total_levy" : "service_reserve"),
                "AI chooses " + (atWar ? "wartime levy only in its own real war" : "legal reserve service instead of unavailable wartime levy in peace"));
        }
    }
}
