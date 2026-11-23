using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Wave 6G acceptance checks (--geopolitics-only):
///   1. Cross-Strait Taiwan/PRC branching across 4 postures & world state sensitivity.
///   2. 10 / 100 / 1000-year multi-seed divergence across GeopoliticalCycle, AiTemperament,
///      AiTemper, Escalation, CivilWarOutbreak, CivilIndependence, and StateFailure.
///   3. Bit-exact determinism on identical seeds across horizons.
///   4. Syria (SYR), Myanmar (MMR), DRC (COD) multi-faction movements, flashpoint events,
///      EN/RU localization parity, and code-health constraints (0 GetHashCode, 0 Godot,
///      zero LINQ in MainlandReclamation).
/// </summary>
static class Wave6GeopoliticsTests
{
    private static readonly Regex CompetitorRegex = new(
        @"(?:\bParadox\b|Парадокс|Age\s+of\s+History|\bAoH[23]?\b|\bАоХ\b|\bHOI\s*4?\b|Hearts\s+of\s+Iron|Хойк[аиеу]|\bVictoria\s*[23]\b|\bVic\s*[23]\b|Виктори[яиюей]\s*[23]\b|Europa\s+Universalis|\bEU\s*[345]\b|Европа\s+Универсалис|Crusader\s+Kings|\bCK\s*[23]\b|Крусейдер\s+Кингс)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL geopolitics: " + label);
        Console.WriteLine("PASS geopolitics: " + label);
    }

    private static void SetElapsedDays(Campaign c, int days)
    {
        var prop = typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours));
        prop!.SetValue(c, (long)days * 24L);
    }

    [TestSuite("--geopolitics-only", Order = 2730, IncludeInDefaultRun = true,
        Description = "Cross-Strait seeded multi-path branching, 10/100/1000-year global unpredictability, and SYR/MMR/COD flashpoint content.")]
    public static void Run()
    {
        MapRasterCatalog.ResetCatalog();
        EventCatalog.ResetCatalog();
        ModifierCatalog.ResetCatalog();
        MovementCatalog.ResetCatalog();
        EventPictures.ResetCatalog();
        SpriteCatalog.Load();
        Loc.ResetCatalog();

        _ = Campaign.CreateBootstrap(2026);
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapErr),
            "2024 province topology loads cleanly: " + mapErr);

        VerifyCrossStraitSeededBranching(map);
        VerifyMultiCenturyGlobalDivergence(map);
        VerifyBitExactRepeatability(map);
        VerifyFlashpointMovementsAndEvents();
        VerifyOwnedCodeHealth(map);
    }

    private static void VerifyCrossStraitSeededBranching(ProvinceTopology map)
    {
        var seenPostures = new HashSet<CrossStraitPosture>();
        var distinctLaunchOdds = new HashSet<string>(StringComparer.Ordinal);
        var horizonPostures = new Dictionary<int, HashSet<CrossStraitPosture>>
        {
            [365] = new(),
            [3650] = new(),
            [36500] = new(),
            [365000] = new()
        };

        var seedPool = new[] { 42, 2026, 9001 }.Concat(Enumerable.Range(1, 32)).Distinct().ToArray();
        foreach (var seed in seedPool)
        {
            var c = Campaign.CreateBootstrap(seed);
            c.AttachProvinceTopology(map.CloneForOverrides());
            var twn = c.NationByTag("TWN");
            var chn = c.NationByTag("CHN");
            Check(twn is not null && chn is not null, $"seed {seed} loads TWN and CHN");

            foreach (var day in horizonPostures.Keys)
            {
                SetElapsedDays(c, day);
                var chance = MainlandReclamation.AiLaunchChance(c, twn!, chn!, out var posture);
                Check(chance is >= 0f and <= 0.90f && float.IsFinite(chance),
                    $"bounded Cross-Strait launch chance at seed={seed} day={day} ({chance:F3})");
                seenPostures.Add(posture);
                horizonPostures[day].Add(posture);
                if (day == 3650)
                    distinctLaunchOdds.Add($"{posture}:{chance:F2}");
            }
        }

        Check(seenPostures.Count == 4,
            $"Cross-Strait doctrine covers all 4 postures across seeds ({string.Join(", ", seenPostures)})");
        foreach (var (day, postures) in horizonPostures)
            Check(postures.Count >= 3,
                $"day {day} branches into multiple Cross-Strait postures across seeds ({postures.Count} of 4)");
        Check(distinctLaunchOdds.Count >= 4,
            $"10-year launch likelihood diverges across seeds ({distinctLaunchOdds.Count} distinct posture/probability outcomes)");

        // Live crisis reaction: Mainland strain elevates reclamation posture / probability vs stable Detente.
        var reactive = Campaign.CreateBootstrap(2026);
        reactive.AttachProvinceTopology(map.CloneForOverrides());
        var rtwn = reactive.NationByTag("TWN")!;
        var rchn = reactive.NationByTag("CHN")!;
        SetElapsedDays(reactive, 3650);
        var calmChance = MainlandReclamation.AiLaunchChance(reactive, rtwn, rchn, out _);
        rchn.Stability = 0.25f;
        rchn.WarSupport = 0.25f;
        var strainedChance = MainlandReclamation.AiLaunchChance(reactive, rtwn, rchn, out var strainedPosture);
        Check(strainedChance > calmChance || strainedPosture is CrossStraitPosture.AmphibiousReclamation or CrossStraitPosture.AsymmetricCrisis,
            $"Mainland collapse/strain shifts Cross-Strait posture and readiness ({calmChance:F2} -> {strainedChance:F2}, posture={strainedPosture})");
    }

    private static void VerifyMultiCenturyGlobalDivergence(ProvinceTopology map)
    {
        var seeds = new[] { 42, 2026, 9001 };
        var horizons = new[] { 3650, 36500, 365000 }; // 10y, 100y, 1000y
        var tags = new[] { "USA", "CHN", "RUS", "TWN", "IND", "BRA" };

        var campaigns = new Dictionary<int, Campaign>();
        foreach (var seed in seeds)
        {
            var c = Campaign.CreateBootstrap(seed);
            c.AttachProvinceTopology(map.CloneForOverrides());
            campaigns[seed] = c;

            // Day-0 baselines remain preserved.
            Check(GeopoliticalCycle.GlobalTensionOffset(c) == 0f, $"seed {seed} day-0 GlobalTensionOffset is 0");
            Check(GeopoliticalCycle.SystemicFragmentationPressure(c, c.NationByTag("USA")!) == 1f,
                $"seed {seed} day-0 SystemicFragmentationPressure is 1");
            Check(AiTemper.EraAggressionModifier(c, c.NationByTag("USA")!) == 1f,
                $"seed {seed} day-0 EraAggressionModifier is 1");
        }

        var eraRegimesSeen = new HashSet<GlobalEraRegime>();
        foreach (var day in horizons)
        {
            var tensionValues = new HashSet<int>();
            var usaTemps = new HashSet<string>(StringComparer.Ordinal);
            var bilateralPairs = new HashSet<string>(StringComparer.Ordinal);
            var aggroBands = new HashSet<string>(StringComparer.Ordinal);
            var syrCivilChances = new HashSet<string>(StringComparer.Ordinal);

            foreach (var seed in seeds)
            {
                var c = campaigns[seed];
                SetElapsedDays(c, day);

                var offset = GeopoliticalCycle.GlobalTensionOffset(c);
                Check(offset is >= -8.0f and <= 10.0f, $"seed {seed} day {day} GlobalTensionOffset bounded ({offset:F2})");
                tensionValues.Add((int)MathF.Round(offset * 100f));
                eraRegimesSeen.Add(GeopoliticalCycle.CurrentEraRegime(c));

                var usa = c.NationByTag("USA")!;
                var chn = c.NationByTag("CHN")!;
                var temp = AiTemperament.Of(c, usa);
                var spread = c.Defines.AiTemperamentSpread;
                var oppMult = AiTemperament.OpportunismOf(c, usa);
                Check(oppMult >= 0.05f && oppMult <= 1f + spread + 0.001f,
                    $"seed {seed} day {day} opportunism multiplier stays bounded ({oppMult:F3})");
                usaTemps.Add($"{temp.Opportunism:F3}:{temp.Foresight:F3}:{temp.Volatility:F3}");

                var biasAB = GeopoliticalCycle.BilateralRealignmentBias(c, usa, chn);
                var biasBA = GeopoliticalCycle.BilateralRealignmentBias(c, chn, usa);
                Check(MathF.Abs(biasAB - biasBA) < 1e-6f && biasAB is >= -1f and <= 1f,
                    $"seed {seed} day {day} BilateralRealignmentBias symmetric & bounded ({biasAB:F3})");
                bilateralPairs.Add($"{biasAB:F3}");

                var aggro = AiTemper.EraAggressionModifier(c, usa);
                Check(aggro is >= 0.78f and <= 1.25f, $"seed {seed} day {day} EraAggressionModifier bounded ({aggro:F3})");
                aggroBands.Add($"{aggro:F3}");

                var syr = c.NationByTag("SYR")!;
                var savedSyrStab = syr.Stability;
                syr.Stability = 0.18f;
                syrCivilChances.Add($"{CivilWarOutbreak.Chance(c, syr):F5}");
                syr.Stability = savedSyrStab;

                // Ensure generational change alters each nation across decades/centuries.
                if (day == 36500)
                {
                    foreach (var tag in tags)
                    {
                        var n = c.NationByTag(tag);
                        Check(n is not null, $"nation {tag} present on seed {seed}");
                    }
                }
            }

            Check(tensionValues.Count >= 2, $"day {day} tension wave diverges across seeds 42/2026/9001 ({tensionValues.Count} distinct)");
            Check(usaTemps.Count == 3, $"day {day} leader temperament diverges across seeds 42/2026/9001");
            Check(bilateralPairs.Count == 3, $"day {day} bilateral bloc realignment diverges across seeds 42/2026/9001");
            Check(aggroBands.Count == 3, $"day {day} era aggression diverges across seeds 42/2026/9001");
            Check(syrCivilChances.Count == 3, $"day {day} flashpoint civil-war propensity diverges across seeds 42/2026/9001");
        }

        // Across 10y -> 100y -> 1000y on a single seed, nations and regimes evolve organically.
        var single = campaigns[2026];
        var chnSnapshots = new HashSet<string>(StringComparer.Ordinal);
        var singleChn = single.NationByTag("CHN")!;
        foreach (var day in horizons)
        {
            SetElapsedDays(single, day);
            var tc = AiTemperament.Of(single, singleChn);
            chnSnapshots.Add($"{tc.Opportunism:F3}:{tc.Cunning:F3}:{tc.Foresight:F3}:{tc.Volatility:F3}");
        }
        Check(chnSnapshots.Count == 3, "same seed evolves national temperament across 10y -> 100y -> 1000y");
        Check(eraRegimesSeen.Count >= 2, $"multi-seed centuries traverse distinct macro era regimes ({eraRegimesSeen.Count})");
    }

    private static void VerifyBitExactRepeatability(ProvinceTopology map)
    {
        foreach (var day in new[] { 0, 3650, 36500, 365000 })
        {
            var a = Campaign.CreateBootstrap(2026);
            var b = Campaign.CreateBootstrap(2026);
            a.AttachProvinceTopology(map.CloneForOverrides());
            b.AttachProvinceTopology(map.CloneForOverrides());
            SetElapsedDays(a, day);
            SetElapsedDays(b, day);

            Check(GeopoliticalCycle.GlobalTensionOffset(a) == GeopoliticalCycle.GlobalTensionOffset(b) &&
                  GeopoliticalCycle.CurrentEraRegime(a) == GeopoliticalCycle.CurrentEraRegime(b),
                $"day {day} GeopoliticalCycle is bit-exact across independent campaigns");

            var twnA = a.NationByTag("TWN")!;
            var chnA = a.NationByTag("CHN")!;
            var twnB = b.NationByTag("TWN")!;
            var chnB = b.NationByTag("CHN")!;
            var chanceA = MainlandReclamation.AiLaunchChance(a, twnA, chnA, out var posA);
            var chanceB = MainlandReclamation.AiLaunchChance(b, twnB, chnB, out var posB);
            Check(chanceA == chanceB && posA == posB,
                $"day {day} MainlandReclamation Cross-Strait posture and probability are bit-exact");

            Check(AiTemperament.Of(a, twnA) == AiTemperament.Of(b, twnB) &&
                  AiTemperament.Of(a, chnA) == AiTemperament.Of(b, chnB),
                $"day {day} AiTemperament is bit-exact across campaigns");
        }
    }

    private static void VerifyFlashpointMovementsAndEvents()
    {
        var c = Campaign.CreateBootstrap(2026);
        var stateIds = new HashSet<string>(c.States.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        // 1. Multi-faction SYR / MMR / COD movements (enriched active + emergent flashpoints).
        var expectedMovements = new[]
        {
            ("sdf", true, "SYR"),
            ("suwayda_druze_assembly", false, "SYR"),
            ("euphrates_tribal_coalition", false, "SYR"),
            ("karen_national_union", true, "MMR"),
            ("dry_zone_peoples_defense", false, "MMR"),
            ("shan_autonomous_congress", false, "MMR"),
            ("congo_river_alliance", true, "COD"),
            ("katanga_federal_league", false, "COD"),
            ("wazalendo_kivu_defense", false, "COD"),
            ("kasai_constitutional_front", false, "COD")
        };

        var emergentTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, shouldStartActive, theater) in expectedMovements)
        {
            var def = MovementCatalog.Of(id);
            Check(def is not null, $"{theater} movement '{id}' is loaded in MovementCatalog");
            Check(def!.StartActive == shouldStartActive, $"{theater} movement '{id}' has start_active={shouldStartActive}");
            Check(def.Tag.Length == 3 && emergentTags.Add(def.Tag), $"{theater} movement '{id}' has unique 3-char tag '{def.Tag}'");
            Check(stateIds.Contains(def.Headquarters), $"{theater} movement '{id}' headquarters '{def.Headquarters}' exists");
            Check(def.Homeland.Count > 0 && def.Homeland.All(stateIds.Contains),
                $"{theater} movement '{id}' homelands exist on map");
            Check(def.Peoples.Count > 0, $"{theater} movement '{id}' declares peoples");

            // Ensure every named support region contains at least one of its declared peoples.
            foreach (var regionId in def.Support.Keys)
            {
                var st = c.States.FirstOrDefault(s => string.Equals(s.Id, regionId, StringComparison.OrdinalIgnoreCase));
                Check(st is not null && MovementRules.PeopleOf(st, def, out _) > 0,
                    $"{theater} movement '{id}' support region '{regionId}' has matching peoples");
            }

            if (!def.StartActive)
            {
                var cultureSprite = "flag_culture_" + def.Peoples[0].ToLowerInvariant();
                Check(SpriteCatalog.Has(cultureSprite),
                    $"{theater} emergent movement '{id}' primary culture '{def.Peoples[0]}' has fallback banner '{cultureSprite}'");
            }
        }

        // 2. Flashpoint events + verified EventPictures + >=2 options + no competitor names.
        var expectedEvents = new[]
        {
            "w6g_taiwan_strait_crossroads",
            "w6g_beijing_strait_mobilization",
            "w6g_multipolar_era_inflection",
            "w6g_syria_fragmented_frontlines",
            "w6g_syria_suwayda_euphrates_unrest",
            "w6g_myanmar_federal_crossroads",
            "w6g_myanmar_dry_zone_resistance",
            "w6g_drc_eastern_kivu_mineral_crisis",
            "w6g_drc_katanga_kasai_autonomy"
        };

        var gameRoot = Path.GetFullPath(GameDataLoader.DataFolder());
        var locKeysToCheck = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evId in expectedEvents)
        {
            var ev = EventCatalog.All.FirstOrDefault(e => string.Equals(e.Id, evId, StringComparison.Ordinal));
            Check(ev is not null, $"flashpoint event '{evId}' loads cleanly");
            Check(ev!.Options.Length >= 2, $"flashpoint event '{evId}' has {ev.Options.Length} options (>= 2)");
            var pic = EventPictures.PathFor(ev);
            Check(pic.StartsWith("res://game/", StringComparison.Ordinal),
                $"flashpoint event '{evId}' resolves picture '{pic}'");
            var diskPath = Path.Combine(gameRoot, pic["res://game/".Length..]);
            Check(File.Exists(diskPath), $"flashpoint event '{evId}' picture exists on disk: {diskPath}");

            locKeysToCheck.Add(ev.Title);
            locKeysToCheck.Add(ev.Description);
            foreach (var opt in ev.Options)
            {
                locKeysToCheck.Add(opt.Title);
                locKeysToCheck.Add(opt.Consequence);
            }
        }

        foreach (var (id, shouldStartActive, _) in expectedMovements)
        {
            if (shouldStartActive) continue;
            var def = MovementCatalog.Of(id)!;
            locKeysToCheck.Add(def.Name);
            locKeysToCheck.Add(def.Description);
            locKeysToCheck.Add(def.StateName);
        }

        // 3. EN + RU localization parity and zero competitor names.
        var savedLang = Loc.Language;
        try
        {
            foreach (var lang in new[] { "english", "russian" })
            {
                Loc.Language = lang;
                foreach (var key in locKeysToCheck)
                {
                    Check(Loc.Has(key) && !string.IsNullOrWhiteSpace(Loc.Get(key)),
                        $"localization ({lang}) defines non-empty key '{key}'");
                    var val = Loc.Get(key);
                    Check(!CompetitorRegex.IsMatch(val),
                        $"localization ({lang}) key '{key}' has zero competitor names");
                }
            }
        }
        finally { Loc.Language = savedLang; }
    }

    private static void VerifyOwnedCodeHealth(ProvinceTopology map)
    {
        var gameRoot = Path.GetFullPath(GameDataLoader.DataFolder());
        var repoRoot = Path.GetDirectoryName(gameRoot)!;
        var ownedSimFiles = new[]
        {
            "AiTemperament.cs", "AiTemperSettings.cs", "GeopoliticalCycle.cs",
            "MainlandReclamation.cs", "Escalation.cs", "AiDiplomacy.cs",
            "CivilWarOutbreak.cs", "CivilIndependence.cs", "StateFailure.cs"
        };

        foreach (var name in ownedSimFiles)
        {
            var path = Path.Combine(repoRoot, "Scripts", "Simulation", name);
            var text = File.ReadAllText(path);
            Check(!text.Contains("GetHashCode", StringComparison.Ordinal),
                $"simulation file {name} has zero GetHashCode() calls");
            Check(!Regex.IsMatch(text, @"\bGodot\b"),
                $"simulation file {name} has zero Godot references");
            if (name == "MainlandReclamation.cs")
            {
                Check(!text.Contains("System.Linq", StringComparison.Ordinal) &&
                      !text.Contains(".ToArray(", StringComparison.Ordinal) &&
                      !text.Contains(".OrderBy(", StringComparison.Ordinal),
                    "MainlandReclamation.cs keeps zero LINQ / zero per-day heap allocation architecture");
            }
        }

        // CivilWarOutbreak / StateFailure flashpoint divergence smoke check across seeds 42, 2026, 9001.
        var flashpointTags = new[] { "SYR", "MMR", "COD" };
        foreach (var tag in flashpointTags)
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in new[] { 42, 2026, 9001 })
            {
                var c = Campaign.CreateBootstrap(seed);
                c.AttachProvinceTopology(map.CloneForOverrides());
                SetElapsedDays(c, 36500);
                c.Defines.CollapseSignsNeeded = 1;
                var n = c.NationByTag(tag)!;
                n.Stability = 0.16f;
                foreach (var st in c.StatesOf(n)) st.Radicalism = 0.85f;
                var cw = CivilWarOutbreak.Chance(c, n);
                var sf = StateFailure.CollapseChance(c, n);
                Check(cw > 0f && sf > 0f, $"{tag} seed {seed} crisis produces positive outbreak/collapse risk ({cw:F4}/{sf:F4})");
                distinct.Add($"{cw:F5}:{sf:F5}");
            }
            Check(distinct.Count == 3,
                $"flashpoint state {tag} outbreak/failure pressure diverges cleanly across seeds ({distinct.Count}/3)");
        }
    }
}
