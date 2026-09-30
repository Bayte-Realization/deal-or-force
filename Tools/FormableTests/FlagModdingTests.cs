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
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(20260928);
        var saudi = campaign.NationByTag("SAU");
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
        var fingerprint = CampaignFingerprint.Of(campaign);
        foreach (var ideology in IdeologyCatalog.All)
            Check(FlagCatalog.ResolvePreview(campaign, china, ideology: ideology.Id) ==
                  FlagCatalog.Resolve(campaign, china.FlagPoliticsPreview("", ideology.Id)),
                "preview uses the actual resolver: " + ideology.Id);
        Check(fingerprint == CampaignFingerprint.Of(campaign), "previews never mutate campaign politics");
        try
        {
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
