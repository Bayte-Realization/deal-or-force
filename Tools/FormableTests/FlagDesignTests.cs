using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Flags written as data (common/flag_designs): the format parses, draws the pixels it says it
/// draws, survives a save and a reload unchanged, rejects what it cannot draw with a reason, and
/// takes part in the flag search exactly as a picture of the same name would.
///
/// Pixels are checked at points whose colour is not in doubt - the middle of a band, the crossing
/// of a cross, the centre of a star - rather than against a stored image, so the test says what
/// the renderer must do instead of freezing whatever it happens to do today.
/// </summary>
static class FlagDesignTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("flag designs: " + label);
        Console.WriteLine("PASS flag designs: " + label);
    }

    static FlagDesign One(string json)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var errors = new List<string>();
        var designs = FlagDesignCatalog.Read(document.RootElement, errors);
        if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
        return designs.Single();
    }

    public static void Run()
    {
        // ---- the commented examples a modder copies parse cleanly, JSON and YAML alike ----
        var template = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder("common"), "..", "..", "mods", "_templates", "flag_designs", "common", "flag_designs"));
        var examples = new List<FlagDesign>();
        var templateErrors = new List<string>();
        foreach (var file in StructuredData.Files(template))
        {
            using var document = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
            examples.AddRange(FlagDesignCatalog.Read(document.RootElement, templateErrors));
        }
        Check(templateErrors.Count == 0 && examples.Count == 4,
            $"the template's JSON and YAML examples parse without a complaint ({examples.Count} designs{(templateErrors.Count > 0 ? ": " + templateErrors[0] : "")})");
        var baseFolder = new List<string>();
        foreach (var document in GameDataLoader.DataDocuments("common", "flag_designs"))
            using (document) FlagDesignCatalog.Read(document.RootElement, baseFolder);
        Check(baseFolder.Count == 0, "the base game's own flag_designs folder is valid");

        // ---- stripes ----
        var tricolour = One("""{ "flag_designs": { "flag_tst": { "ratio": 1.5, "background": { "pattern": "horizontal", "colours": ["#ffffff", "#0038a8", "#ce1126"] } } } }""");
        var raster = tricolour.Render(60);
        Check(raster.Width == 90 && raster.Height == 60, "a 2:3 flag drawn 60 high is 90 wide");
        Check(raster.Hex(45, 10) == "#ffffff" && raster.Hex(45, 30) == "#0038a8" && raster.Hex(45, 50) == "#ce1126",
            "three horizontal bands, top to bottom, in their exact colours");
        var spain = One("""{ "flag_esx": { "background": { "pattern": "horizontal", "colours": ["#c8102e", "#fcd116", "#c8102e"], "weights": [1, 2, 1] } } }""");
        var spainRaster = spain.Render(80);
        Check(spainRaster.Hex(60, 10) == "#c8102e" && spainRaster.Hex(60, 25) == "#fcd116" && spainRaster.Hex(60, 70) == "#c8102e",
            "weighted stripes: the middle band of 1, 2, 1 takes half the height (no envelope needed)");
        var vertical = One("""{ "flag_vtx": { "background": { "pattern": "vertical", "colours": ["#009a44", "#ffffff", "#ff8200"] } } }""");
        var verticalRaster = vertical.Render(40);
        Check(verticalRaster.Hex(5, 20) == "#009a44" && verticalRaster.Hex(30, 20) == "#ffffff" && verticalRaster.Hex(55, 20) == "#ff8200",
            "three vertical bands, hoist to fly");

        // ---- crosses, canton, diagonal ----
        var nordic = One("""{ "flag_nrx": { "ratio": 1.375, "background": { "pattern": "nordic_cross", "colours": ["#ce1126", "#ffffff", "#002868"] } } }""");
        var nordicRaster = nordic.Render(80);
        Check(nordicRaster.Hex(30, 40) == "#002868", "the Nordic cross crosses at a third of the length, in the inner colour");
        Check(nordicRaster.Hex(40, 30) == "#ffffff", "with its fimbriation around the inner cross");
        Check(nordicRaster.Hex(3, 3) == "#ce1126" && nordicRaster.Hex(100, 75) == "#ce1126", "and the field in the corners");
        var saltire = One("""{ "flag_slx": { "ratio": 1.5, "background": { "pattern": "saltire", "colours": ["#002868", "#ffffff"] } } }""");
        var saltireRaster = saltire.Render(60);
        Check(saltireRaster.Hex(45, 30) == "#ffffff" && saltireRaster.Hex(45, 4) == "#002868", "a saltire crosses in the middle and leaves the edges to the field");
        var canton = One("""{ "flag_cnx": { "background": { "pattern": "canton", "colours": ["#ce1126", "#ffffff"], "accent": "#002868" } } }""");
        var cantonRaster = canton.Render(60);
        Check(cantonRaster.Hex(5, 5) == "#002868" && cantonRaster.Hex(80, 5) == "#ce1126" && cantonRaster.Hex(80, 55) == "#ffffff",
            "the canton sits upper-hoist over the stripes");
        var diagonal = One("""{ "flag_dgx": { "background": { "pattern": "diagonal", "colours": ["#009a44", "#0038a8", "#fcd116"] } } }""");
        var diagonalRaster = diagonal.Render(60);
        Check(diagonalRaster.Hex(3, 3) == "#009a44" && diagonalRaster.Hex(86, 56) == "#0038a8" && diagonalRaster.Hex(45, 30) == "#fcd116",
            "a diagonal splits hoist and fly with its band across the middle");

        // ---- emblems ----
        var japan = One("""{ "flag_jpx": { "background": { "pattern": "solid", "colours": ["#ffffff"] }, "emblems": [ { "shape": "disc", "colour": "#bc002d", "x": 0.5, "y": 0.5, "size": 0.6 } ] } }""");
        var japanRaster = japan.Render(100);
        Check(japanRaster.Hex(75, 50) == "#bc002d" && japanRaster.Hex(10, 10) == "#ffffff", "a disc in the centre, field around it");
        var star = One("""{ "flag_stx": { "background": { "pattern": "solid", "colours": ["#ce1126"] }, "emblems": [ { "shape": "star", "colour": "#fcd116", "size": 0.8 } ] } }""");
        var starRaster = star.Render(100);
        Check(starRaster.Hex(75, 50) == "#fcd116", "a star's centre is the star's colour");
        Check(starRaster.Hex(75, 20) == "#fcd116" && starRaster.Hex(60, 15) == "#ce1126",
            "its upper point is filled and the notch beside it is not");
        var masked = One("""{ "flag_spx": { "background": { "pattern": "solid", "colours": ["#000000"] }, "emblems": [ { "sprite": "icon_test", "colour": "#00ff00", "size": 0.5 } ] } }""");
        var white = new FlagRaster(4, 4);
        for (var y = 0; y < 4; y++) for (var x = 0; x < 4; x++) white.Set(x, y, 1, 1, 1, 1);
        var maskedRaster = masked.Render(40, name => name == "icon_test" ? white : null);
        Check(maskedRaster.Hex(30, 20) == "#00ff00" && maskedRaster.Hex(2, 2) == "#000000",
            "a sprite emblem with a colour is drawn as a mask of that colour");
        Check(masked.Render(40).Hex(30, 20) == "#000000", "and a sprite nobody can supply is left out rather than failing");

        // ---- several sizes ----
        foreach (var height in new[] { 20, 48, 160, 512 })
        {
            var drawn = tricolour.Render(height);
            Check(drawn.Width == (int)MathF.Round(height * 1.5f) && drawn.Pixels.Length == drawn.Width * height * 4,
                $"drawn at {height} pixels high in its own proportions");
        }

        // ---- round trip ----
        var rich = examples.First(d => d.Emblems.Count >= 2).Clone();
        rich.Emblems[0].Rotation = 15;
        var saved = rich.ToDocument();
        var reloaded = One(saved);
        Check(reloaded.ToJson() == rich.ToJson(), "save then load gives back the same design, field for field");
        Check(reloaded.Render(64).Pixels.SequenceEqual(rich.Render(64).Pixels), "and it draws the same pixels");
        Check(saved.StartsWith("//", StringComparison.Ordinal), "the saved file explains itself in a header comment");
        var yamlTwin = One(StructuredData.YamlToJson(
            "flag_yml:\n  background:\n    pattern: horizontal\n    colours: [\"#FFF\", \"#0038A8\"]\n"));
        Check(yamlTwin.Colours.SequenceEqual(new[] { "#ffffff", "#0038a8" }), "YAML reads the same, with short and upper-case hex normalised");

        // ---- refusals with reasons ----
        void Refused(string json, string reason, string label)
        {
            using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
            var errors = new List<string>();
            var read = FlagDesignCatalog.Read(document.RootElement, errors);
            Check(read.Count == 0 && errors.Any(e => e.Contains(reason, StringComparison.OrdinalIgnoreCase)), label);
        }
        Refused("""{ "flag_bad": { "background": { "pattern": "tartan", "colours": ["#ffffff"] } } }""", "unknown pattern", "an unknown pattern is refused by name");
        Refused("""{ "flag_bad": { "background": { "pattern": "horizontal", "colours": ["#ffffff"] } } }""", "at least 2", "stripes with one colour are refused");
        Refused("""{ "flag_bad": { "background": { "pattern": "solid", "colours": ["red"] } } }""", "at least 1", "a colour that is not hex is refused");
        Refused("""{ "Flag Bad": { "background": { "pattern": "solid", "colours": ["#ffffff"] } } }""", "not a valid id", "an id that is not a sprite name is refused");

        // ---- a design is a sprite, and the flag search finds it ----
        SpriteCatalog.Load();
        var campaign = Campaign.CreateBootstrap(20261101);
        FlagDesignCatalog.ResetCatalog();
        // Political variants are an explicit mod policy now; national artwork is stable
        // in the base game. Test the opt-in ability without changing that default.
        FlagCatalog.Merge("""{"prefer_country_base":false}""");
        var proven = false;
        foreach (var nation in campaign.Nations.Where(n => n.Ideology.Length > 0).Take(80))
        {
            var id = "flag_" + nation.Tag.ToLowerInvariant() + "_" + FlagCatalog.Slug(nation.Ideology);
            if (FlagCatalog.Resolve(campaign, nation) == id) continue;
            if (!FlagCatalog.Candidates(campaign, nation).Contains(id)) continue;
            var design = tricolour.Clone();
            design.Id = id;
            FlagDesignCatalog.Put(design);
            var resolved = FlagCatalog.Resolve(campaign, nation);
            FlagDesignCatalog.ResetCatalog();
            if (resolved != id) continue;
            Check(true, $"a design named {id} becomes the flag {nation.Tag} flies under its ideology");
            proven = true;
            break;
        }
        Check(proven, "some country's ideology variant can be supplied by a design");
        FlagCatalog.ResetCatalog();
        var named = tricolour.Clone();
        named.Id = "flag_zz_editor_probe";
        FlagDesignCatalog.Put(named);
        Check(SpriteCatalog.Has(named.Id) && SpriteCatalog.TextureOf(named.Id).StartsWith(FlagDesignCatalog.TexturePrefix, StringComparison.Ordinal),
            "the sprite catalogue knows a design and points at it rather than at a file");
        FlagDesignCatalog.ResetCatalog();
        Check(!SpriteCatalog.Has(named.Id), "and forgets it when the catalogue is rebuilt");

        // ---- the workbench writes and validates these, JSON and YAML ----
        var root = Path.Combine(Path.GetTempPath(), "ClaudeK_flag_designs_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var path = ContentWorkbench.Write(root, $"common/flag_designs/{rich.Id}.json", rich.ToDocument());
            Check(File.Exists(path), "the workbench saves a design into a mod folder");
            ContentWorkbench.Write(root, "common/flag_designs/yaml_twin.yaml", "flag_designs:\n  flag_yml:\n    background:\n      pattern: solid\n      colours: [\"#123456\"]\n");
            var refused = false;
            try { ContentWorkbench.Write(root, "common/flag_designs/broken.json", """{ "flag_x": { "background": { "pattern": "tartan" } } }"""); }
            catch (InvalidDataException) { refused = true; }
            Check(refused && !File.Exists(Path.Combine(root, "common", "flag_designs", "broken.json")), "a design that cannot be drawn is refused before it reaches the disk");
            Check(ContentWorkbench.IsEditable("common/flag_designs/a.yaml") && ContentWorkbench.IsEditable("history/provinces/a.csv") &&
                  !ContentWorkbench.IsEditable("common/a.txt") && !ContentWorkbench.IsEditable("Scripts/a.json"),
                "the workbench edits YAML and province tables in content folders, and nothing outside them");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
