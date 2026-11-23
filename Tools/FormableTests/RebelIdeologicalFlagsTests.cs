using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Verification suite for Wave 6K:
/// 1. Distinct splinter colours (expanded palette + 2-hop neighbors + same-parent/culture siblings + golden-angle fallback).
/// 2. Distinct splinter &amp; civil-war survivor names (eliminating duplicate "Republic of Russia" x4 clones).
/// 3. Nationwide rebel ideological flags matching rebel ideology against parent country.
/// 4. 15 new Wave 6K formable nations + 30 bespoke 768x512 PNG flags + 40+ dynamic EN/RU names.
/// </summary>
static class RebelIdeologicalFlagsTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL rebel-flags: " + label);
        Console.WriteLine("PASS rebel-flags: " + label);
    }

    [TestSuite("--rebel-flags-only", Order = 2705, IncludeInDefaultRun = true,
        Description = "Unique splinter colors/names, nationwide rebel ideological flags, and Wave 6K formables/flags/names.")]
    public static void Run()
    {
        TestDistinctSplinterColoursAndGoldenAngleFallback();
        TestDistinctSplinterAndCivilWarSurvivorNames();
        TestNationwideRebelIdeologicalFlags();
        TestWave6KFormablesFlagsAndDynamicNames();
        Console.WriteLine("REBEL_IDEOLOGICAL_FLAGS_TESTS_OK");
    }

    private static void TestDistinctSplinterColoursAndGoldenAngleFallback()
    {
        var c = new Campaign(20261122) { ScenarioId = "wave6k-colour-fixture" };
        var parent = c.AddNation(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "RUS", Name = "Russia", Color = "#3F6B45",
            Population = 100_000_000, DeclaredCulture = "russian"
        });
        Check(c.Defines.SecessionColourPalette.Length >= 32,
            $"SecessionColourPalette expanded to {c.Defines.SecessionColourPalette.Length} high-contrast colours (>= 32)");

        var usedColours = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parent.Color };
        // Simulate 40 breakaways from the same parent/culture to force BOTH expanded palette AND golden-angle fallback!
        for (var i = 1; i <= 40; i++)
        {
            var tag = "S" + i.ToString("00");
            var col = SecessionColours.Choose(c, parent, tag, Array.Empty<int>(), parent.Color);
            Check(!string.Equals(col, parent.Color, StringComparison.OrdinalIgnoreCase),
                $"splinter {tag} never clones parent colour {parent.Color} (got {col})");
            Check(usedColours.Add(col), $"splinter {tag} receives unique colour {col} across 40 siblings");
            c.AddNation(new Nation
            {
                Id = i, MapIndex = i + 1, Tag = tag, Name = "Splinter " + i, Color = col,
                Population = 1_000_000, SecededFromId = parent.Id, RevoltParentId = parent.Id,
                DeclaredCulture = "russian"
            });
        }
    }

    private static void TestDistinctSplinterAndCivilWarSurvivorNames()
    {
        var c = new Campaign(20261122) { ScenarioId = "wave6k-survivor-fixture" };
        var rus = c.AddNation(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "RUS", Name = "Russia", Color = "#3F6B45",
            Population = 80_000_000, Government = "presidential_republic", Ideology = "conservatism"
        });
        c.States.Add(new ScenarioState { Id = "ru_mow", MapIndex = 10, NationMapIndex = 1, Name = "Moscow", Population = 80_000_000 });

        string[] seats = { "Novosibirsk", "Khabarovsk", "Yekaterinburg", "Krasnoyarsk" };
        string[] ideos = { "social_democracy", "communism", "traditionalism", "liberalism" };
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rus.Name };

        for (var i = 0; i < seats.Length; i++)
        {
            var rebel = c.AddNation(new Nation
            {
                Id = i + 1, MapIndex = i + 2, Tag = "R" + i + "X",
                Name = $"rising:transitional_council:RUS:Russia",
                Color = rus.Color,
                Population = 5_000_000, SecededFromId = rus.Id, RevoltParentId = rus.Id,
                Government = "provisional_government", Ideology = ideos[i]
            });
            c.States.Add(new ScenarioState
            {
                Id = "ru_s" + i, MapIndex = 20 + i, NationMapIndex = rebel.MapIndex,
                Name = seats[i], Population = 5_000_000
            });
            c.InvalidateStateIndex();

            SecessionEvents.AssignDistinctSurvivorIdentity(c, rebel, rus, "Russia");
            Check(!string.Equals(rebel.Name, "Russia", StringComparison.OrdinalIgnoreCase),
                $"civil-war survivor {rebel.Tag} does not clone parent name 'Russia' (got '{rebel.Name}')");
            Check(seenNames.Add(rebel.Name), $"civil-war survivor {rebel.Tag} gets unique name '{rebel.Name}'");
            Check(!string.Equals(rebel.Color, rus.Color, StringComparison.OrdinalIgnoreCase),
                $"civil-war survivor {rebel.Tag} drops parent colour {rus.Color} (got {rebel.Color})");
        }
    }

    private static void TestNationwideRebelIdeologicalFlags()
    {
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        var boot = Campaign.CreateBootstrap(20261122);
        var rus = boot.NationByTag("RUS");
        var mmr = boot.NationByTag("MMR");
        var mli = boot.NationByTag("MLI");
        Check(rus is not null && mmr is not null && mli is not null, "RUS, MMR, MLI exist in 2024 bootstrap");

        // 1. Rising rebel factions resolve their parent country's ideological flag matching rebel ideology!
        var rusComm = new Nation
        {
            Tag = "RC1", Name = "rising:peoples_front:RUS:Russia",
            Government = "one_party_state", Ideology = "communism", SecededFromId = rus.Id, FoundedInRevolt = "uprising"
        };
        var rusLib = new Nation
        {
            Tag = "RL1", Name = "rising:civic_front:RUS:Russia",
            Government = "parliamentary_republic", Ideology = "liberalism", SecededFromId = rus.Id, FoundedInRevolt = "uprising"
        };
        var rusTrad = new Nation
        {
            Tag = "RT1", Name = "rising:sovereign_front:RUS:Russia",
            Government = "absolute_monarchy", Ideology = "traditionalism", SecededFromId = rus.Id, FoundedInRevolt = "uprising"
        };
        var mmrLib = new Nation
        {
            Tag = "ML1", Name = "rising:federal_front:MMR:Myanmar",
            Government = "parliamentary_republic", Ideology = "liberalism", SecededFromId = mmr.Id, FoundedInRevolt = "uprising"
        };
        var mliLib = new Nation
        {
            Tag = "MI1", Name = "rising:civic_front:MLI:Mali",
            Government = "parliamentary_republic", Ideology = "liberalism", SecededFromId = mli.Id, FoundedInRevolt = "uprising"
        };

        var fComm = FlagCatalog.Resolve(boot, rusComm);
        var fLib = FlagCatalog.Resolve(boot, rusLib);
        var fTrad = FlagCatalog.Resolve(boot, rusTrad);
        var fMmr = FlagCatalog.Resolve(boot, mmrLib);
        var fMli = FlagCatalog.Resolve(boot, mliLib);

        Check(fComm.Contains("rus", StringComparison.OrdinalIgnoreCase) && fComm.Contains("communism", StringComparison.OrdinalIgnoreCase),
            $"Russian communist rebels resolve Russian communist banner (got {fComm})");
        Check(fLib.Contains("rus", StringComparison.OrdinalIgnoreCase) && fLib != "flag_rus",
            $"Russian liberal rebels resolve distinct Russian liberal banner (got {fLib})");
        Check(fTrad.Contains("rus", StringComparison.OrdinalIgnoreCase) && fTrad.Contains("traditionalism", StringComparison.OrdinalIgnoreCase),
            $"Russian traditionalist rebels resolve Russian traditionalist banner (got {fTrad})");
        Check(fComm != fLib && fLib != fTrad && fComm != fTrad,
            "all three Russian rebel ideologies fly visually distinct banners");
        Check(fMmr.Contains("mmr", StringComparison.OrdinalIgnoreCase) && fMmr != "flag_mmr",
            $"Myanmar liberal rebels resolve Myanmar civil-war/ideological banner (got {fMmr})");
        Check(fMli.Contains("mli", StringComparison.OrdinalIgnoreCase) && fMli != "flag_mli",
            $"Mali liberal rebels resolve Mali civil-war/ideological banner (got {fMli})");

        // 2. Non-rising nationwide uprising (after losing rising: prefix) STILL resolves parent ideological banner!
        var postRisingRus = new Nation
        {
            Tag = "RP1", Name = "Novosibirsk",
            Government = "one_party_state", Ideology = "communism",
            SecededFromId = rus.Id, RevoltParentId = rus.Id, FoundedInRevolt = "uprising"
        };
        Check(FlagCatalog.CivilWarParentTag(boot, postRisingRus) == "rus",
            "CivilWarParentTag identifies nationwide uprising via SecededFromId + FoundedInRevolt");
        var fPost = FlagCatalog.Resolve(boot, postRisingRus);
        Check(fPost.Contains("rus", StringComparison.OrdinalIgnoreCase) && fPost.Contains("communism", StringComparison.OrdinalIgnoreCase),
            $"post-rising nationwide communist rebels still fly Russian communist banner (got {fPost})");
    }

    private static void TestWave6KFormablesFlagsAndDynamicNames()
    {
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        var boot = Campaign.CreateBootstrap(20261122);

        string[] wave6KFormables =
        {
            "north_sea_union", "pyrenean_union", "adriatic_alps_union", "balkan_black_sea_accord",
            "caspian_commonwealth", "red_sea_federation", "great_lakes_african_union", "zambezi_confederation",
            "atlas_sahara_union", "indus_himalaya_accord", "silk_steppe_union", "coral_sea_federation",
            "northern_andes_federation", "southern_atlantic_accord", "laurentian_commonwealth"
        };
        foreach (var id in wave6KFormables)
        {
            var def = FormableNations.Find(id);
            Check(def is not null, $"Wave 6K formable '{id}' loaded");
            var founder = def.EligibleTags.Select(boot.NationByTag).FirstOrDefault(n => n is not null);
            Check(founder is not null, $"Wave 6K formable '{id}' has valid 2024 founder");
            var sprite = FlagCatalog.ResolveFormationPreview(boot, founder, id);
            var tex = SpriteCatalog.TextureOf(sprite);
            var physical = tex.StartsWith("res://game/", StringComparison.Ordinal)
                ? Path.Combine(GameDataLoader.DataFolder(), tex["res://game/".Length..]) : tex;
            Check(sprite == "flag_formable_" + id && File.Exists(physical),
                $"Wave 6K formable '{id}' resolves to bespoke 768x512 flag '{sprite}'");
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", def.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", def.Name)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", def.Description)),
                $"Wave 6K formable '{id}' has EN+RU name and description");
        }

        string[] sampleDynKeys =
        {
            "country.RUS.ideology.social_democracy.name", "country.RUS.ideology.liberalism.name",
            "country.MMR.ideology.liberalism.name", "country.MLI.ideology.liberalism.name",
            "country.CHN.ideology.liberalism.name", "country.IND.ideology.traditionalism.name",
            "country.TUR.ideology.traditionalism.name", "country.UKR.ideology.traditionalism.name"
        };
        foreach (var key in sampleDynKeys)
        {
            Check(!string.IsNullOrWhiteSpace(Loc.TextIn("english", key)) &&
                  !string.IsNullOrWhiteSpace(Loc.TextIn("russian", key)),
                $"Wave 6K dynamic country name '{key}' present in EN+RU");
        }
    }
}
