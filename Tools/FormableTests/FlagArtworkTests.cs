using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Every flag the game says it will fly, checked against the art that is actually on disk.
///
/// The rest of the flag suite asks whether the *rule* picks the right sprite; this one asks
/// whether the sprite exists. They are different failures with different symptoms: a wrong rule
/// shows the wrong flag, and a missing file shows a grey rectangle - which is what a player
/// reported, half the picker's rows with nothing beside the name, and the only way to catch it
/// before they do is to resolve every flag the world can raise and look for the file.
///
/// Three populations, because all three are drawn on screens a player sees:
///   countries   - the map, the HUD, every list. 260 of them in the bootstrap scenario.
///   movements   - a banner each, listed without land in the picker (Movements).
///   formables   - what a country becomes, shown as a preview before it is proclaimed
///                 (common/formable_nations, FlagCatalog.ResolveFormationPreview).
/// </summary>
static class FlagArtworkTests
{
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS flag artwork: " + label);
    }

    public static void Run()
    {
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(20260928);
        // A campaign that has been started founds the movements it declares (the scenario setup
        // does the same on a new game). The bare bootstrap has none, and a flag check with no
        // movements in it would pass while the picker's landless list showed grey rectangles.
        Movements.Found(campaign);

        // Countries and movements: what the map draws for each of them today.
        var missing = new List<string>();
        var countries = 0;
        var movements = 0;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Movement is not null) movements++; else countries++;
            var sprite = FlagCatalog.Resolve(campaign, nation);
            if (!Artwork(sprite)) missing.Add($"{nation.Tag} -> {sprite}");
        }
        Check(missing.Count == 0, $"{countries} countries all fly artwork that exists" +
            (missing.Count == 0 ? "" : $" — missing {missing.Count}: " + string.Join(", ", missing.Take(10))));
        Check(movements > 20, $"{movements} movements all fly artwork that exists");

        // Movements once more by their own banners: a movement is a nation with no land, and the
        // file named after it is what a row in the picker shows.
        var bannered = 0;
        foreach (var nation in campaign.Nations.Where(n => n.Movement is not null))
            if (SpriteCatalog.Has("flag_" + nation.Tag.ToLowerInvariant())) bannered++;
        Check(bannered == movements, "every movement has a flag file named after its tag");

        // Formables: what the country would fly the day it proclaims itself.
        var total = 0;
        var own = 0;
        foreach (var definition in FormableNations.All)
        {
            total++;
            var founder = definition.EligibleTags.Select(campaign.NationByTag).FirstOrDefault(n => n is not null);
            if (founder is null)
            {
                // Nobody in this scenario can proclaim it, so there is no preview to resolve:
                // its artwork is checked by its own name instead. A formable whose claimants
                // are absent here is unreachable, not unflaggeable.
                if (Artwork("flag_formable_" + definition.Id)) own++;
                else missing.Add($"{definition.Id} -> flag_formable_{definition.Id}");
                continue;
            }
            var sprite = FlagCatalog.ResolveFormationPreview(campaign, founder, definition.Id);
            if (!Artwork(sprite)) { missing.Add($"{definition.Id} -> {sprite}"); continue; }
            // Its own artwork, not the flag the founder already flies: a formable shown wearing
            // the country that proclaims it is the bug this suite exists for.
            if (sprite.Contains(definition.Id, StringComparison.OrdinalIgnoreCase) ||
                sprite.StartsWith("flag_formable_", StringComparison.OrdinalIgnoreCase)) own++;
            else missing.Add($"{definition.Id} flies {sprite}, which is not its own");
        }
        Check(total > 70, "the formable catalogue is real: " + total);
        Check(missing.Count == 0, $"{total} formables all fly artwork of their own" +
            (missing.Count == 0 ? "" : $" — {missing.Count} exceptions: " + string.Join(", ", missing.Take(10))));
        Check(own == total, "every formable resolves to art made for it");

        // A flag file that exists but no qualifier names is dead art: worth knowing, not fatal.
        var generic = Directory.Exists(Path.Combine(GameDataLoader.DataFolder(), "gfx", "flags"))
            ? Directory.GetFiles(Path.Combine(GameDataLoader.DataFolder(), "gfx", "flags"), "flag_formable_*.png").Length
            : 0;
        Check(generic >= total, $"a file exists for every formable: {generic} files for {total} formables");
    }

    private static bool Artwork(string sprite)
    {
        if (string.IsNullOrWhiteSpace(sprite)) return false;
        var texture = SpriteCatalog.TextureOf(sprite);
        if (string.IsNullOrWhiteSpace(texture)) return false;
        var physical = texture.StartsWith("res://game/", StringComparison.Ordinal)
            ? Path.Combine(GameDataLoader.DataFolder(), texture["res://game/".Length..]) : texture;
        return File.Exists(physical);
    }
}
