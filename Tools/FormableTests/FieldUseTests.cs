using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DealOrForce.Simulation;

static class FieldUseTests
{
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); Console.WriteLine("PASS field use: " + label); }

    public static void Run()
    {
        TechnologyCatalog.ResetCatalog();
        OldSaveLoadsFamiliarityAtZero();

        var campaign = Campaign.CreateBootstrap(73);
        var nation = campaign.NationByTag("POL");
        campaign.ConfigureSinglePlayer(nation.Id);
        Check(nation is not null && nation.Id == campaign.NationById(nation.Id).Id, "Poland is the bootstrap country the rule is shown on");

        var military = TechnologyCatalog.All.Where(technology => technology.Category == "military").Take(2).ToArray();
        var logistics = TechnologyCatalog.All.First(technology => technology.Category == "logistics");
        var named = TechnologyCatalog.All.First(technology => technology.Category == "industry" &&
            technology.Icon == "icon_building_civilianindustry");
        var unnamed = TechnologyCatalog.All.First(technology => technology.Category == "industry" &&
            technology.Icon == "icon_research");
        var society = TechnologyCatalog.All.First(technology => technology.Category == "society");
        Check(military.Length == 2 && logistics != null && named != null && unnamed != null && society != null,
            "the catalogue still has a fight, a shipment, a named industry, an unnamed industry and a society technology");

        nation.Technologies.Clear();
        nation.TechnologyFamiliarity.Clear();
        nation.ResearchProgress.Clear();
        nation.CurrentResearch = "";

        var armies = campaign.Armies.Where(army => army.NationId == nation.Id && army.Divisions > 0f).ToArray();
        Check(armies.Length > 0, "Poland has an army that can be committed to a fight");
        var battleIds = armies.Select(army => army.BattleId).ToArray();
        var warIds = armies.Select(army => army.AssignedWarId).ToArray();
        foreach (var army in armies) { army.BattleId = -1; army.AssignedWarId = -1; }

        var exports = nation.Market.ToDictionary(pair => pair.Key, pair => pair.Value.Exports);
        foreach (var quote in nation.Market.Values) quote.Exports = 0f;

        var plant = campaign.StatesOf(nation).FirstOrDefault(state => state.Population > 0);
        Check(plant is not null, "Poland has a populated region where an industry can run");
        var civilian = campaign.StatesOf(nation)
            .Select(state => (state, count: state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry)))
            .ToArray();
        if (civilian.All(row => row.count <= 0)) plant.Buildings[BuildingKind.CivilianIndustry] = 1;

        try
        {
            Know(nation, military[0]);
            var idle = CampaignFingerprint.Of(campaign);
            Check(ResearchSystem.ApplyFieldUse(campaign, nation, 10f) == 0f && nation.TechnologyFamiliarity.Count == 0,
                "a known military technology does not grow while no army is fighting");
            Check(CampaignFingerprint.Of(campaign) == idle, "an idle week writes no familiarity and does not change the fingerprint");
            var idleLine = ResearchSystem.FieldUseLine(campaign, nation, military[0], 10f);
            Check(idleLine == Sentence("Field use: {0} / {1} points. It is not in use, so it does not grow.",
                "0.0", Ceiling(campaign, military[0])), "English detail says an idle technology does not grow: " + idleLine);

            armies[0].BattleId = 0;
            var fighting = CampaignFingerprint.Of(campaign);
            Check(fighting != idle, "committing an army to battle is its own fact, apart from familiarity");
            var spent = ResearchSystem.ApplyFieldUse(campaign, nation, 10f);
            var expected = 10f * campaign.Defines.ResearchUseShare;
            Check(Near(spent, expected) && Near(ResearchSystem.Familiarity(nation, military[0].Id), expected),
                "one technology in a fight takes the week's field-use share of the research points");
            Check(CampaignFingerprint.Of(campaign) != fighting, "familiarity above zero changes the fingerprint");
            nation.TechnologyFamiliarity.Clear();
            Check(CampaignFingerprint.Of(campaign) == fighting, "clearing familiarity restores the fingerprint of the same fight");
            for (var time = 0; time < 20; time++)
                _ = ResearchSystem.FieldUseLine(campaign, nation, military[0], 10f);
            Check(CampaignFingerprint.Of(campaign) == fighting, "reading the research detail does not change the fingerprint");

            Know(nation, military[1], logistics, society, unnamed);
            foreach (var quote in nation.Market.Values) quote.Exports = 0f;
            if (!nation.Market.ContainsKey(Commodity.Machinery))
                nation.Market[Commodity.Machinery] = new MarketQuote { Commodity = Commodity.Machinery };
            nation.TechnologyFamiliarity.Clear();
            ResearchSystem.ApplyFieldUse(campaign, nation, 10f);
            Check(ResearchSystem.Familiarity(nation, military[0].Id) > 0f &&
                ResearchSystem.Familiarity(nation, military[1].Id) > 0f &&
                ResearchSystem.Familiarity(nation, logistics.Id) == 0f &&
                ResearchSystem.Familiarity(nation, society.Id) == 0f &&
                ResearchSystem.Familiarity(nation, unnamed.Id) == 0f,
                "a fight practices military technologies only; logistics, society and an unnamed industry stay idle");
            var oneShare = ResearchSystem.Familiarity(nation, military[0].Id);
            Check(Near(oneShare, expected / 2f) && Near(ResearchSystem.Familiarity(nation, military[1].Id), expected / 2f),
                "two technologies in use split the same slice");

            nation.TechnologyFamiliarity.Clear();
            nation.Market[Commodity.Machinery].Exports = 4f;
            armies[0].BattleId = -1;
            ResearchSystem.ApplyFieldUse(campaign, nation, 10f);
            Check(ResearchSystem.Familiarity(nation, logistics.Id) > 0f &&
                ResearchSystem.Familiarity(nation, military[0].Id) == 0f,
                "an export practices the logistics technology and does not practice a military one");

            nation.TechnologyFamiliarity.Clear();
            nation.Market[Commodity.Machinery].Exports = 0f;
            Know(nation, named);
            ResearchSystem.ApplyFieldUse(campaign, nation, 10f);
            Check(ResearchSystem.Familiarity(nation, named.Id) > 0f &&
                ResearchSystem.Familiarity(nation, unnamed.Id) == 0f,
                "civilian works practice the technology whose icon names them, not one with a generic icon");
            foreach (var state in campaign.StatesOf(nation))
                state.Buildings[BuildingKind.CivilianIndustry] = 0;
            var beforeIdlePlant = ResearchSystem.Familiarity(nation, named.Id);
            Check(ResearchSystem.ApplyFieldUse(campaign, nation, 10f) == 0f &&
                Near(ResearchSystem.Familiarity(nation, named.Id), beforeIdlePlant),
                "the same technology stops growing when those works are gone");
            RestoreCivilian(civilian, plant);

            armies[0].BattleId = 0;
            nation.Technologies.Remove(military[0].Id);
            nation.TechnologyFamiliarity.Clear();
            Check(ResearchSystem.ApplyFieldUse(campaign, nation, 10f) == 0f ||
                ResearchSystem.Familiarity(nation, military[0].Id) == 0f,
                "a technology the country has not learned does not gain familiarity from a fight");
            Know(nation, military[0]);

            var ceiling = ResearchSystem.FamiliarityCeiling(campaign, military[0]);
            nation.TechnologyFamiliarity.Clear();
            nation.Technologies.Clear();
            Know(nation, military[0]);
            nation.TechnologyFamiliarity[military[0].Id] = ceiling - 0.1f;
            var topped = ResearchSystem.ApplyFieldUse(campaign, nation, 1000f);
            Check(Near(topped, 0.1f) && Near(ResearchSystem.Familiarity(nation, military[0].Id), ceiling),
                "practice stops at the ceiling instead of spending the whole offer");
            Check(ResearchSystem.ApplyFieldUse(campaign, nation, 1000f) == 0f, "a full ceiling takes nothing further");
            var full = ResearchSystem.FieldUseLine(campaign, nation, military[0], 1000f);
            Check(full == Sentence("Field use: {0} / {1} points. The ceiling is reached.",
                ceiling.ToString("0.0", CultureInfo.InvariantCulture), ceiling.ToString("0.0", CultureInfo.InvariantCulture)),
                "English detail says the ceiling is reached: " + full);

            nation.TechnologyFamiliarity[military[0].Id] = 1.5f;
            var inUse = ResearchSystem.FieldUseOf(campaign, nation, military[0], 10f);
            var usingLine = ResearchSystem.FieldUseLine(campaign, nation, military[0], 10f);
            Check(usingLine == Sentence("Field use: {0} / {1} points. In use; this week it takes {2} from the laboratories.",
                "1.5", ceiling.ToString("0.0", CultureInfo.InvariantCulture), inUse.ThisWeek.ToString("0.00", CultureInfo.InvariantCulture)),
                "English detail names the points this week takes from the laboratories: " + usingLine);

            var language = Loc.Language;
            try
            {
                Loc.Language = "russian";
                var russian = ResearchSystem.FieldUseLine(campaign, nation, military[0], 10f);
                Check(russian == Sentence("Опыт применения: {0} / {1} очков. В деле; на этой неделе она берёт {2} у лабораторий.",
                    "1.5", ceiling.ToString("0.0", CultureInfo.InvariantCulture), inUse.ThisWeek.ToString("0.00", CultureInfo.InvariantCulture)),
                    "Russian detail uses the same numbers: " + russian);
                Loc.Language = "english";
            }
            finally { Loc.Language = language; }

            var priced = CampaignFingerprint.Of(campaign);
            campaign.Defines.ResearchUseShare = 0f;
            Check(ResearchSystem.ApplyFieldUse(campaign, nation, 10f) == 0f, "a zero share turns field use off");
            Check(ResearchSystem.FieldUseLine(campaign, nation, military[0], 10f) == "Field use is off.",
                "the detail says field use is off");
            var offLanguage = Loc.Language;
            try
            {
                Loc.Language = "russian";
                Check(ResearchSystem.FieldUseLine(campaign, nation, military[0], 10f) == "Опыт применения выключен.",
                    "Russian detail says field use is off");
            }
            finally { Loc.Language = offLanguage; }
            Check(CampaignFingerprint.Of(campaign) != priced, "a non-default field-use price is part of the fingerprint");
            campaign.Defines.ResearchUseShare = Defines.DefaultResearchUseShare;
            Check(CampaignFingerprint.Of(campaign) == priced, "the default price is not mixed into the fingerprint");

            nation.Technologies.Clear();
            nation.TechnologyFamiliarity.Clear();
            nation.ResearchProgress.Clear();
            Know(nation, military[0]);
            armies[0].BattleId = 0;
            var project = TechnologyCatalog.Of("industrial_automation");
            Check(project is not null && ResearchSystem.Available(nation, project), "the open project is one Poland can actually research");
            nation.CurrentResearch = project.Id;
            SetElapsedDay(campaign, 7);
            var week = ResearchSystem.WeeklyPoints(campaign, nation);
            ResearchSystem.Tick(campaign);
            var practiced = ResearchSystem.Familiarity(nation, military[0].Id);
            var invested = nation.ResearchProgress.GetValueOrDefault(project.Id);
            Check(week > 0f && week < project.Cost && Near(practiced, week * campaign.Defines.ResearchUseShare) &&
                Near(invested, week - practiced),
                "the weekly tick pays field use out of the same points and leaves the rest on the open project");
        }
        finally
        {
            for (var index = 0; index < armies.Length; index++)
            {
                armies[index].BattleId = battleIds[index];
                armies[index].AssignedWarId = warIds[index];
            }
            foreach (var pair in exports)
                if (nation.Market.TryGetValue(pair.Key, out var quote)) quote.Exports = pair.Value;
            RestoreCivilian(civilian, plant);
        }
    }

    private static void OldSaveLoadsFamiliarityAtZero()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate
        };
        var blank = JsonSerializer.Deserialize<Nation>("{}", options);
        Check(blank is not null && blank.TechnologyFamiliarity.Count == 0, "an old save loads familiarity at zero");
        var loaded = JsonSerializer.Deserialize<Nation>("""{"TechnologyFamiliarity":{"secure_tactical_comms":1.25}}""", options);
        Check(loaded is not null && Near(loaded.TechnologyFamiliarity["secure_tactical_comms"], 1.25f),
            "a saved familiarity comes back on that technology");
    }

    private static void SetElapsedDay(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!
            .GetSetMethod(true)!
            .Invoke(campaign, new object[] { day * 24L });

    private static void RestoreCivilian((ScenarioState state, int count)[] civilian, ScenarioState plant)
    {
        foreach (var row in civilian)
        {
            if (row.count > 0) row.state.Buildings[BuildingKind.CivilianIndustry] = row.count;
            else row.state.Buildings.Remove(BuildingKind.CivilianIndustry);
        }
        if (civilian.All(row => row.count <= 0))
            plant.Buildings.Remove(BuildingKind.CivilianIndustry);
    }

    private static void Know(Nation nation, params TechnologyDefinition[] technologies)
    {
        foreach (var technology in technologies)
            if (!nation.HasTechnology(technology.Id)) nation.Technologies.Add(technology.Id);
    }

    private static string Ceiling(Campaign campaign, TechnologyDefinition technology) =>
        ResearchSystem.FamiliarityCeiling(campaign, technology).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Sentence(string text, params string[] arguments) => string.Format(CultureInfo.InvariantCulture, text, arguments);

    private static bool Near(float left, float right) => MathF.Abs(left - right) < 0.0001f;
}
