using System;
using System.Linq;
using DealOrForce.Simulation;

// Losing ground changes where a government stands, not which country its flag is for.
internal static class MovementFlagContinuityTests
{
    private static void Check(bool ok, string note)
    { if (!ok) throw new Exception("movement flag continuity: " + note); Console.WriteLine("PASS movement flag continuity: " + note); }

    internal static void Run()
    {
        SpriteCatalog.Load(); FlagCatalog.ResetCatalog();
        var c = Campaign.CreateBootstrap(20261109);
        Movements.Found(c);
        var before = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        foreach (var tag in new[] { "RUS", "AFG", "USA", "KAZ" })
        {
            var original = c.NationByTag(tag);
            foreach (var (government, ideology) in new[] {
                (original.Government, original.Ideology), ("military_junta", "despotism"),
                ("theocracy", "theocratic_rule") })
            {
                var nation = original.FlagPoliticsPreview(government, ideology);
                var national = FlagCatalog.Resolve(c, nation);
                Check(!string.IsNullOrEmpty(national) && SpriteCatalog.Has(national), tag + "/" + government + " has real national artwork");
                nation.Movement = new MovementState { Id = "exile_" + tag.ToLowerInvariant(), Landless = true, Exile = true };
                Check(FlagCatalog.Resolve(c, nation) == national,
                    tag + "/" + government + " exile keeps the same national artwork");
                nation.Movement.Id = "underground_" + tag.ToLowerInvariant();
                Check(FlagCatalog.Resolve(c, nation) == national,
                    tag + "/" + government + " underground government keeps its banner too");
                nation.Movement.Landless = false;
                Check(FlagCatalog.Resolve(c, nation) == national,
                    tag + "/" + government + " return to land does not replace the banner");
            }
        }
        foreach (var tag in new[] { "HMS", "BDF", "TTP", "BVC" })
        {
            var original = c.NationByTag(tag);
            Check(original?.Movement is not null, tag + " is an actual catalogued movement");
            var nation = original.FlagPoliticsPreview("theocracy", "theocratic_rule");
            nation.Movement = new MovementState { Id = original.Movement.Id, Landless = true };
            var own = FlagCatalog.Resolve(c, nation);
            Check(own == "flag_" + tag.ToLowerInvariant(), tag + " uses its own authored movement flag");
            nation.Movement.Landless = false;
            Check(FlagCatalog.Resolve(c, nation) == own, tag + " keeps that flag when it gains territory");
        }
        var restored = c.NationByTag("CHN").FlagPoliticsPreview("parliamentary_republic", "liberalism");
        restored.FormedNationId = "";
        restored.FoundNameKind = "tag"; restored.FoundNameId = "TWN";
        var restoredBanner = FlagCatalog.Resolve(c, restored);
        restored.Movement = new MovementState { Id = "exile_restored", Exile = true, Landless = true };
        Check(FlagCatalog.Resolve(c, restored) == restoredBanner, "restored national identity survives exile");

        var custom = c.NationByTag("HMS").FlagPoliticsPreview("theocracy", "theocratic_rule");
        custom.CustomFlag = "flag_can";
        Check(FlagCatalog.Resolve(c, custom) == "flag_can", "explicit custom movement flag still wins");
        custom.CustomFlag = "";
        Check(FlagCatalog.ResolveFormationPreview(c, custom, "benelux_union") != "flag_hms",
            "an explicitly proclaimed new identity is not frozen to the old movement flag");
        Check(before == CampaignFingerprint.Of(c) && random == c.Random.State,
            "all resolution and preview checks leave campaign authority and random stream unchanged");

        var regional = new Nation { Id = 15000, MapIndex = SecessionEvents.FirstDynamicMapIndex,
            Tag = "EXREG", Name = "Military Command of East Kazakhstan", Color = "#3c8e71",
            FoundNameKind = "direction", FoundNameId = "east_kazakhstan", SecededFromId = c.NationByTag("KAZ").Id,
            DeclaredCulture = "kazakh", Government = "military_junta", Ideology = "despotism" };
        DemographyCatalog.Declare(regional.Tag, new[] { "kazakh" });
        var fallback = FlagCatalog.Resolve(c, regional);
        Check(FlagCatalog.NeedsRegionalBanner(c, regional, fallback), "regional country uses its bespoke banner, not Kazakhstan's cultural fallback");
        regional.Movement = new MovementState { Id = "underground_exreg", Exile = true, Landless = true };
        Check(FlagCatalog.NeedsRegionalBanner(c, regional, FlagCatalog.Resolve(c, regional)),
            "the reported East Kazakhstan-style underground movement retains that same bespoke banner");
        regional.CustomFlag = "flag_can";
        Check(!FlagCatalog.NeedsRegionalBanner(c, regional, FlagCatalog.Resolve(c, regional)), "explicit regional custom art wins over generated banners");
        regional.CustomFlag = ""; regional.FormedNationId = "benelux_union";
        Check(!FlagCatalog.NeedsRegionalBanner(c, regional, FlagCatalog.Resolve(c, regional)), "a regional formable does not reuse the old generated banner");
    }
}
