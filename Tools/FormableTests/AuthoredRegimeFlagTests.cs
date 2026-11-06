using System;
using System.Linq;
using DealOrForce.Simulation;

internal static class AuthoredRegimeFlagTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("FAIL authored flags: " + message); Console.WriteLine("PASS authored flags: " + message); }

    internal static void Run()
    {
        SpriteCatalog.Load(); FlagCatalog.ResetCatalog(); FlagDesignCatalog.ResetCatalog();
        var c = Campaign.CreateBootstrap(1206); var rng = c.Random.State;
        var afghan = c.NationByTag("AFG");
        Check(FlagCatalog.Resolve(c, afghan) == "flag_afg_theocratic_rule", "2024 Taliban government actually flies the white banner");
        Check(SpriteCatalog.TextureOf(FlagCatalog.Resolve(c, afghan)).EndsWith("flag_afg_theocratic_rule.png", StringComparison.Ordinal),
            "the chosen sprite resolves to white-banner artwork rather than the old republic texture");
        Check(FlagCatalog.Resolve(c, afghan.FlagPoliticsPreview("presidential_republic", "liberalism")) == "flag_afg_liberalism",
            "a restored Afghan republic automatically uses its authored republican alternative");
        Check(FlagCatalog.Resolve(c, afghan.FlagPoliticsPreview("absolute_monarchy", "traditionalism")) == "flag_afg_absolute_monarchy",
            "an Afghan restoration can use its authored historical royal alternative");
        var count = 0;
        var russia = c.NationByTag("RUS");
        Check(FlagCatalog.ResolvePreview(c, russia, ideology: "communism") == "flag_rus_communism",
            "a communist Russia automatically flies its authored Russian Soviet banner");
        Check(FlagCatalog.ResolvePreview(c, russia, "absolute_monarchy", "traditionalism") is "flag_rus_absolute_monarchy" or "flag_rus_traditionalism",
            "an actual Russian monarchy uses its authored royal alternative");
        Check(FlagCatalog.ResolvePreview(c, russia, ideology: "nationalism") == "flag_rus",
            "a nationalist Russia does not borrow a monarchy merely through an ideology alias");
        var templateFixture = new Nation { Id = -600, Tag = "ABZ", Government = "presidential_republic", Ideology = "communism" };
        Check(SpriteCatalog.Has("flag_abz_communism") && !FlagCatalog.HasAutomaticVariantArt("flag_abz_communism") &&
            !FlagCatalog.Candidates(c, templateFixture).Contains("flag_abz_communism") && FlagCatalog.Resolve(c, templateFixture) != "flag_abz_communism",
            "a legacy workshop template does not replace a country's national artwork");
        foreach (var tag in new[] { "USA", "CAN", "MEX", "BRA", "ARG", "CHL", "POL", "UKR", "FIN", "SWE", "NOR", "DNK", "DEU", "FRA", "GBR", "KOR",
            "RUS", "ITA", "ESP", "PRT", "GRC", "TUR", "AUS", "NZL", "IND", "PAK", "IDN", "JPN", "CHN", "TWN", "ZAF", "ROU" })
        {
            var nation = c.NationByTag(tag);
            Check(nation is not null && SpriteCatalog.Has(FlagCatalog.Resolve(c, nation)) &&
                FlagCatalog.HasAutomaticVariantArt(FlagCatalog.Resolve(c, nation)), tag + " starts with existing non-template national artwork");
            foreach (var (government, ideology, suffix) in new[] {
                ("military_junta", "despotism", "defence_council"), ("theocracy", "theocratic_rule", "covenant") })
            {
                var preview = nation.FlagPoliticsPreview(government, ideology);
                var sprite = "flag_" + tag.ToLowerInvariant() + "_" + suffix;
                Check(FlagCatalog.Resolve(c, preview) == sprite, tag + " resolves its explicit alternate " + suffix);
                var design = FlagDesignCatalog.All.First(d => d.Id == sprite);
                var pixels = design.Render(96).Pixels;
                Check(pixels.Distinct().Count() > 3 && CountryNames.Resolve(preview, false) != CountryNames.Resolve(nation, false),
                    tag + " alternate has non-empty scalable art and its own displayed country name");
                count++;
            }
        }
        Check(count == 64 && c.Random.State == rng, "all 64 alternatives resolve without changing the campaign RNG");
        var custom = c.NationByTag("USA").FlagPoliticsPreview("military_junta", "despotism");
        custom.CustomFlag = "flag_can";
        Check(FlagCatalog.Resolve(c, custom) == "flag_can", "explicit player/mod flag still outranks authored political rules");
        try
        {
            FlagCatalog.Merge("""{"regime_flags":{"AFG":[]}}""");
            Check(FlagCatalog.Resolve(c, afghan) == "flag_afg_theocratic_rule", "removing explicit exceptions does not suppress an exact authored filename variant");
            var errors = new System.Collections.Generic.List<string>();
            FlagCatalog.Merge("""{"regime_flags":{"USA":[{"sprite":"flag_usa","requires":{"invented":"x"}}]}}""", errors);
            Check(errors.Count > 0 && FlagCatalog.Resolve(c, custom) == "flag_can", "unknown predicates are diagnosed without replacing valid rules");
        }
        finally { FlagCatalog.ResetCatalog(); }
    }
}
