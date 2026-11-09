using System;
using System.Linq;
using DealOrForce.Simulation;

static class FlagModdingTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS flags: " + label);
    }

    public static void Run()
    {
        MovementFlagContinuityTests.Run();
        FlagWarViewTests.Run();
        FlagSlugTests.Run();
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(20260928);
        var saudi = campaign.NationByTag("SAU");
        var ttp = new Nation { Tag = "TTP", Government = "theocracy", Ideology = "theocratic_rule" };
        Check(!FlagCatalog.Candidates(campaign, ttp).Contains("flag_ideology_theocratic_rule"),
            "an Islamist movement cannot inherit the Vatican's flag through generic theocracy");
        Check(FlagCatalog.Resolve(campaign, campaign.NationByTag("VAT")) == "flag_vat", "Vatican keeps its actual national flag");
        Check(SpriteCatalog.TextureOf(FlagCatalog.Resolve(campaign, saudi)) == SpriteCatalog.TextureOf("flag_sau"),
            "2024 Saudi Arabia resolves to its green national artwork, not merely a Saudi-named sprite");
        var tested = 0;
        foreach (var nation in campaign.Nations.Where(n => SpriteCatalog.Has("flag_" + n.Tag.ToLowerInvariant())))
        {
            var own = "flag_" + nation.Tag.ToLowerInvariant();
            var actual = FlagCatalog.Resolve(campaign, nation);
            Check(actual == own || actual.StartsWith(own + "_", StringComparison.Ordinal),
                nation.Tag + " starts with its own flag or variant");
            tested++;
        }
        Check(tested > 150, "real scenario coverage, not an empty catalogue");
        var china = campaign.NationByTag("CHN");
        Check(!FlagCatalog.Candidates(campaign, china.FlagPoliticsPreview("theocracy", "traditionalism"))
            .Contains("flag_ideology_traditionalism"), "Asian traditionalism does not borrow a Spanish historical cross");
        foreach (var culture in new[] { "han", "cantonese", "hakka", "hui", "javanese", "buginese",
                     "cebuano", "ilocano", "maronite", "bambara", "ovambo", "melanesian" })
        {
            var texture = SpriteCatalog.TextureOf("flag_culture_" + culture);
            var physical = texture.StartsWith("res://game/", StringComparison.Ordinal)
                ? System.IO.Path.Combine(GameDataLoader.DataFolder(), texture["res://game/".Length..]) : texture;
            Check(System.IO.File.Exists(physical), "fallback artwork exists: " + culture);
        }
        var fingerprint = CampaignFingerprint.Of(campaign);
        foreach (var ideology in IdeologyCatalog.All)
            Check(FlagCatalog.ResolvePreview(campaign, china, ideology: ideology.Id) ==
                  FlagCatalog.Resolve(campaign, china.FlagPoliticsPreview("", ideology.Id)),
                "preview uses the actual resolver: " + ideology.Id);
        Check(fingerprint == CampaignFingerprint.Of(campaign), "previews never mutate campaign politics");
        var index = 0;
        foreach (var parentTag in new[] { "RUS", "DEU", "CHN", "IND", "CAN" })
        {
            var rebels = new Nation { Tag = "CW" + index++, Name = "rising:transitional_council:" + parentTag + ":Parent",
                Government = "provisional_government", Ideology = "social_democracy" };
            DemographyCatalog.Declare(rebels.Tag, new[] { "ukrainian" });
            var flag = FlagCatalog.Resolve(campaign, rebels);
            Check(flag.Length > 0 && SpriteCatalog.TextureOf(flag) != SpriteCatalog.TextureOf("flag_ukr"),
                parentTag + " nationwide opposition does not inherit Ukraine's flag from its seed region");
            Check(!FlagCatalog.Candidates(campaign, rebels).Any(x => x.StartsWith("flag_culture_ukrainian", StringComparison.Ordinal)),
                parentTag + " nationwide opposition is not resolved as a Ukrainian national secession");
            Check(FlagCatalog.Candidates(campaign, rebels).Contains("flag_" + parentTag.ToLowerInvariant() + "_civil_war_social_democracy"),
                parentTag + " supports a mod-authored civil-war banner without code changes");
        }
        var separatists = new Nation { Tag = "CWS", Name = "Independent region", Ideology = "social_democracy" };
        DemographyCatalog.Declare(separatists.Tag, new[] { "ukrainian" });
        Check(FlagCatalog.Candidates(campaign, separatists).Any(x => x.StartsWith("flag_culture_ukrainian", StringComparison.Ordinal)),
            "genuine regional independence retains its people's flag fallback");
        var opposition = new Nation { Tag = "CWF", Name = "rising:transitional_council:RUS:Russia",
            Government = "provisional_government", Ideology = "social_democracy" };
        var ukraine = campaign.NationByTag("UKR");
        opposition.OverlordId = ukraine.Id;
        Check(FlagCatalog.ValueOf("overlord", campaign, opposition) == "", "an overlord id alone cannot invent dependency for a free country");
        opposition.SubjectType = "puppet";
        Check(FlagCatalog.Resolve(campaign, opposition) == "flag_rus_civil_war_ukr_social_democracy",
            "a democratic Russian opposition actually dependent on Ukraine can fly its white-blue-white alternative");
        opposition.SubjectType = "";
        Check(!FlagCatalog.Candidates(campaign, opposition).Contains("flag_rus_civil_war_ukr_social_democracy"),
            "alliance or friendship alone does not qualify for a foreign-dependent flag");
        opposition.SubjectType = "puppet"; opposition.OverlordId = china.Id;
        Check(!FlagCatalog.Candidates(campaign, opposition).Contains("flag_rus_civil_war_ukr_social_democracy"),
            "dependence on another patron does not select Ukraine's opposition variant");
        var diaspora = new Nation { Tag = "CWD", Name = "Independent region", Government = "provisional_government", Ideology = "nationalism" };
        DemographyCatalog.Declare(diaspora.Tag,new[]{"indian_diaspora"});
        Check(FlagCatalog.Resolve(campaign,diaspora) == "flag_culture_hindustani", "an independent diaspora government does not inherit the British-style diaspora banner");
        diaspora.OverlordId = campaign.NationByTag("GBR").Id;
        Check(FlagCatalog.Resolve(campaign,diaspora) == "flag_culture_hindustani", "a stale British overlord id without a subject relationship does not enable a colonial banner");
        diaspora.SubjectType = "puppet";
        Check(FlagCatalog.Resolve(campaign,diaspora) == "flag_culture_indian_diaspora", "only actual British dependence enables the restricted diaspora banner");
        diaspora.OverlordId = china.Id;
        Check(FlagCatalog.Resolve(campaign,diaspora) == "flag_culture_hindustani", "another patron does not imply British colonial identity");
        try
        {
            FlagCatalog.Merge("""{"flag_context":{"flag_culture_indian_diaspora":{"requires":{}}}}""");
            diaspora.SubjectType = "";
            Check(FlagCatalog.Resolve(campaign,diaspora) == "flag_culture_indian_diaspora", "a mod can explicitly remove an inherited flag-context restriction");
            var errors = new System.Collections.Generic.List<string>();
            FlagCatalog.Merge("""{"flag_context":{"flag_culture_indian_diaspora":{"requires":{"invented_condition":"yes"}}}}""",errors);
            Check(errors.Count > 0 && FlagCatalog.Resolve(campaign,diaspora) == "flag_culture_indian_diaspora", "an unknown flag-context source is reported instead of silently replacing a valid rule");
            FlagCatalog.Merge("""{"flag_context":{"flag_culture_indian_diaspora":{"requires":{"overlord":"GBR"},"otherwise":"flag_culture_hindustani"},"flag_culture_hindustani":{"requires":{"overlord":"GBR"},"otherwise":"flag_culture_indian_diaspora"}}}""");
            Check(!FlagCatalog.Candidates(campaign,diaspora).Any(x=>x is "flag_culture_indian_diaspora" or "flag_culture_hindustani"), "a cyclic mod fallback terminates safely");
            FlagCatalog.Merge("""{"generic_flag_cultures":{"flag_ideology_traditionalism":[]}}""");
            Check(FlagCatalog.Candidates(campaign, china.FlagPoliticsPreview("theocracy", "traditionalism"))
                .Contains("flag_ideology_traditionalism"), "a mod can explicitly lift a historical emblem restriction");
            Check(FlagCatalog.Chain("ideology", "socialism").Contains("communism"), "base alias present");
            FlagCatalog.Merge("""{"also_answers_to":{"ideology":{"socialism":[]}}}""");
            Check(FlagCatalog.Chain("ideology", "socialism").SequenceEqual(new[] { "socialism" }),
                "empty mod list removes inherited alias");
            FlagCatalog.Merge("""{"also_answers_to":{"ideology":{"socialism":["agrarianism"]}}}""");
            Check(FlagCatalog.Chain("ideology", "socialism").SequenceEqual(new[] { "socialism", "agrarianism" }),
                "later mod replaces aliases without stale values");
            var liberal = china.FlagPoliticsPreview("presidential_republic", "liberalism");
            Check(!FlagCatalog.Fits(campaign, liberal, "chn"), "base regime restriction active");
            FlagCatalog.Merge("""{"own_flag_is_for":{"chn":{}}}""");
            Check(FlagCatalog.Fits(campaign, liberal, "chn"), "empty mod declaration removes inherited restriction");
            FlagCatalog.Merge("""{"own_flag_is_for":{"chn":{"ideology":"communism"}}}""");
            var names = FlagCatalog.Candidates(campaign, china.FlagPoliticsPreview("theocracy", "agrarianism"));
            Check(!names.Contains("flag_chn_liberalism"), "replacement restriction drops inherited otherwise flag");
        }
        finally { FlagCatalog.ResetCatalog(); }
        Check(FlagCatalog.Chain("ideology", "socialism").Contains("communism"), "catalogue reset restores base data");
    }
}
