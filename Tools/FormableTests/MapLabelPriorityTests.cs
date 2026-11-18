using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce;
using DealOrForce.Simulation;

/// <summary>
/// Headless invariants for Wave 2 typography, priority ordering, smooth collision fade,
/// worker-thread major river label anchors, and Forward+-only tabletop atmosphere gating.
/// Run: FormableTests --map-labels-only
/// </summary>
static class MapLabelPriorityTests
{
    private static int _passed;

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("map labels: " + label);
        _passed++;
        Console.WriteLine("PASS map labels: " + label);
    }

    public static void Run()
    {
        _passed = 0;
        Ordering();
        CollisionAvoidance();
        ZoomBandsAndFade();
        RiverAnchorsAndLocalization();
        AtmosphereAndTypographyContracts();
        Console.WriteLine($"map labels: {_passed} PASS");
    }

    private static void Ordering()
    {
        var capitals = new[] { 10, 25, 42 };
        var playerCities = new HashSet<int> { 7, 19 };
        var ordered = MapLabelPriority.OrderCityCandidates(
            capitals, playerCapital: 42, selectedCapital: 25, candidateLimit: 50,
            index => playerCities.Contains(index));

        Check(ordered[0] == 42 && ordered[1] == 25 && ordered[2] == 10,
            "player capital and selected capital lead other national capitals");
        var p7 = ordered.IndexOf(7);
        var p19 = ordered.IndexOf(19);
        var p0 = ordered.IndexOf(0);
        Check(p7 > 2 && p19 > p7 && p0 > p19,
            "player and selected nation cities take precedence over generic cities while keeping catalog order");
        Check(ordered.Distinct().Count() == ordered.Count,
            "priority ordering emits no duplicate city index");
    }

    private static void CollisionAvoidance()
    {
        var taken = new List<LabelBox>();
        var stars = new List<(float X, float Y, int Key)> { (300f, 200f, 10) };

        var firstSide = MapLabelPriority.SolveSide(300f, 200f, 10f, 80f, 14f, taken, stars, selfKey: 10, out var box1);
        Check(firstSide == 1, "uncontested priority capital claims right side first");
        taken.Add(box1);

        // Rival city west of box1 whose rightward label would collide must flip cleanly left (-1).
        var rivalSide = MapLabelPriority.SolveSide(280f, 200f, 8f, 70f, 14f, taken, stars, selfKey: 99, out var box2);
        Check(rivalSide == -1 && !box2.Intersects(box1),
            "colliding lower-priority label moves to a non-overlapping side");
        taken.Add(box2);

        // Fill left/above/below around (300, 200) and verify box rejection returns 0 cleanly.
        taken.Add(new LabelBox(180f, 140f, 240f, 120f));
        var crowded = MapLabelPriority.SolveSide(300f, 200f, 8f, 60f, 14f, taken, stars, selfKey: 105, out _);
        Check(crowded == 0, "fully crowded spot yields cleanly (returns 0)");
    }

    private static void ZoomBandsAndFade()
    {
        // Classic stays binary; atlas adds smooth band ramp and reveals mid-zoom regions + rivers.
        Check(MapLabelPriority.CountryTargetAlpha(false, false, 12.5f, 30000, false, true) == 1f &&
              MapLabelPriority.CountryTargetAlpha(false, false, 7.0f, 30000, false, true) == 0f,
            "classic country labels preserve exact binary distance threshold");
        Check(MapLabelPriority.RegionTargetAlpha(false, false, 8.5f, 5000, true) == 0f &&
              MapLabelPriority.RegionTargetAlpha(true, false, 8.5f, 5000, true) > 0.5f &&
              MapLabelPriority.RegionTargetAlpha(true, false, 16.0f, 5000, true) == 0f &&
              MapLabelPriority.RegionTargetAlpha(true, false, 5.2f, 5000, true) == 0f,
            "region labels are atlas-only and strictly confined to the mid-zoom band");

        var riverMid = MapLabelPriority.RiverTargetAlpha(true, false, 8.5f, rank: 2, horizonVisible: true);
        var riverFar = MapLabelPriority.RiverTargetAlpha(true, false, 15.0f, rank: 2, horizonVisible: true);
        Check(riverMid > 0.6f && riverFar == 0f &&
              MapLabelPriority.RiverTargetAlpha(false, false, 8.5f, rank: 2, horizonVisible: true) == 0f,
            "major rivers show in atlas near+mid zoom and stay off in classic/far view");

        var alpha = 0f;
        for (var frame = 0; frame < 20; frame++)
        {
            var next = MapLabelPriority.StepOpacity(alpha, 0.90f, 0.016f);
            Check(next >= alpha && next <= 0.90f, $"opacity step {frame} converges monotonically without overshoot");
            alpha = next;
        }
        Check(alpha > 0.899f, "fade reaches target smoothly within ~0.3s");
    }

    private static void RiverAnchorsAndLocalization()
    {
        HydrologyCatalog.ResetCatalog();
        var anchors = RiverLabelLayout.BuildAnchors(HydrologyCatalog.RiverFeatures);
        Check(anchors.Count >= 60, $"worker river layout extracted major world rivers ({anchors.Count})");
        var keys = new HashSet<string>(anchors.Select(a => a.Key), StringComparer.OrdinalIgnoreCase);
        Check(keys.Count == anchors.Count, "multi-segment Natural Earth reaches deduplicate cleanly to 1 anchor per river");
        foreach (var major in new[] { "nile", "amazonas", "danube", "rhine", "dnieper", "volga", "mississippi", "yangtze" })
            Check(keys.Contains(major), $"major river present and deduplicated: {major}");

        Check(anchors.All(a => a.AxisX >= -0.051f && float.IsFinite(a.Latitude) && float.IsFinite(a.Longitude)),
            "every river anchor keeps upright reading orientation and finite coordinates");

        var prevLang = Loc.Language;
        try
        {
            Loc.ResetCatalog();
            Loc.Language = "english";
            var danubeEn = anchors.First(a => a.Key == "danube").DisplayName;
            var yellowEn = anchors.First(a => a.Key == "yellow").DisplayName;
            Loc.Language = "russian";
            var danubeRu = anchors.First(a => a.Key == "danube").DisplayName;
            var yellowRu = anchors.First(a => a.Key == "yellow").DisplayName;
            Check(danubeEn == "Danube" && yellowEn == "Yellow River" &&
                  danubeRu == "Дунай" && yellowRu == "Хуанхэ",
                "major rivers resolve crisp localized names in both English and Russian");
        }
        finally
        {
            Loc.Language = prevLang;
        }
    }

    private static void AtmosphereAndTypographyContracts()
    {
        var fpAtlas = MapLabelPriority.AtmospherePolish(true, "forward_plus", 0.38f);
        var compatAtlas = MapLabelPriority.AtmospherePolish(true, "gl_compatibility", 0.38f);
        var mobileAtlas = MapLabelPriority.AtmospherePolish(true, "mobile", 0.38f);
        var fpClassic = MapLabelPriority.AtmospherePolish(false, "forward_plus", 0.38f);

        Check(fpAtlas.WarmLampRim && fpAtlas.RimWarmth > 0f && fpAtlas.VignetteWarmth > 0f,
            "Forward+ + atlas activates warm tabletop NW lamp rim and atlas vignette");
        Check(!compatAtlas.WarmLampRim && compatAtlas.RimWarmth == 0f && compatAtlas.VignetteWarmth == 0f &&
              !mobileAtlas.WarmLampRim && !fpClassic.WarmLampRim && fpClassic.VignetteStrength == 0.38f,
            "Compatibility, Mobile, and Classic retain classic rim & vignette identically");

        InterfaceSkin.ResetCatalog();
        var fill = LinearColour.FromHex(MapLook.ColourHex("map.look_label_fill"), default);
        var cap = LinearColour.FromHex(MapLook.ColourHex("map.look_capital_fill"), default);
        var outline = LinearColour.FromHex(MapLook.ColourHex("map.look_label_outline"), default);
        Check(MapLook.Luminance(fill) > 0.65f && MapLook.Luminance(outline) < 0.02f &&
              cap.R > 0.85f && cap.B > 0.60f,
            "atlas label colours keep high paper-on-ink legibility without saturated yellow-gold");

        var root = Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts", "gfx");
        var globe = File.ReadAllText(Path.Combine(root, "GlobePresentation.cs"));
        var cities = File.ReadAllText(Path.Combine(root, "GlobeCities.cs"));
        var atmo = File.ReadAllText(Path.Combine(root, "GlobeAtmosphere.cs"));
        Check(globe.Contains("BuildRegionAnchorsDownsampled") && globe.Contains("RiverLabelLayout.BuildAnchors"),
            "region and river anchors build on worker threads inside BuildPoliticalMap");
        Check(cities.Contains("MapLabelPriority.OrderCityCandidates") && cities.Contains("PlaceRivers("),
            "city placement follows priority order and gives rivers lowest collision priority");
        Check(atmo.Contains("MapLabelPriority.AtmospherePolish("),
            "atmosphere rim uses pure Forward+/atlas gating");
    }
}
