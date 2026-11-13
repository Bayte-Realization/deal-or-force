using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
    // Recovery report only: no assets or catalogues are modified by this command.
    public static void AuditMovementBanners()
    {
        SpriteCatalog.Load(); FlagCatalog.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(20260928); Movements.Found(campaign);
        var mappings = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var nation in campaign.Nations.Where(n => n.Movement is not null))
        {
            var stem = "flag_" + nation.Tag.ToLowerInvariant();
            if (!WorkshopSized(stem)) continue;
            var previous = FlagCatalog.Candidates(campaign, nation).FirstOrDefault(name =>
                name != stem && !name.StartsWith(stem + "_", StringComparison.Ordinal) &&
                SpriteCatalog.Has(name) && Artwork(name) && !WorkshopSized(name));
            if (previous is not null) mappings[stem] = new { texture = SpriteCatalog.TextureOf(previous) };
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { sprites = mappings },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool WorkshopSized(string sprite)
    {
        var path = SpriteCatalog.TextureOf(sprite);
        if (!path.StartsWith("res://game/", StringComparison.Ordinal)) return false;
        path = Path.Combine(GameDataLoader.DataFolder(), path["res://game/".Length..]);
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        if (stream.Read(header) != 24 || header[0] != 137 || header[1] != 80 ||
            header[2] != 78 || header[3] != 71) return false;
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]) == 192 &&
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]) == 120;
    }

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
        MovementBannersKeepTheirIdentity(campaign);
        HistoricalMovementBindings(campaign);

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
        var placeholder = campaign.Nations
            .Where(nation => WorkshopSized(FlagCatalog.Resolve(campaign, nation)))
            .Select(nation => nation.Tag).ToArray();
        Check(placeholder.Length == 0,
            "no country or movement flies a 192×120 workshop placeholder" +
            (placeholder.Length == 0 ? "" : ": " + string.Join(", ", placeholder.Take(12))));

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
        var formablePlaceholder = FormableNations.All
            .Where(definition => WorkshopSized("flag_formable_" + definition.Id))
            .Select(definition => definition.Id).ToArray();
        Check(formablePlaceholder.Length == 0,
            "no formable flies a 192×120 workshop placeholder" +
            (formablePlaceholder.Length == 0 ? "" : ": " + string.Join(", ", formablePlaceholder.Take(12))));

        // A flag file that exists but no qualifier names is dead art: worth knowing, not fatal.
        var generic = Directory.Exists(Path.Combine(GameDataLoader.DataFolder(), "gfx", "flags"))
            ? Directory.GetFiles(Path.Combine(GameDataLoader.DataFolder(), "gfx", "flags"), "flag_formable_*.png").Length
            : 0;
        Check(generic >= total, $"a file exists for every formable: {generic} files for {total} formables");

        // And the newest additions are really in the catalogue: a suite that iterates what exists
        // passes just as happily when the content it is meant to guard was never loaded.
        foreach (var id in new[] { "congo_reunited", "mali_federation", "panama_reunited",
                     "polish_lithuanian", "samoa_reunited" })
        {
            var definition = FormableNations.Find(id);
            Check(definition is not null, $"the catalogue holds '{id}'");
            Check(Artwork("flag_formable_" + id), $"'{id}' flies its own flag");
        }
    }

    private static void MovementBannersKeepTheirIdentity(Campaign campaign)
    {
        // Either authored answer is the movement's own: its painted insignia, or the sourced
        // regional/homeland flag of the ground it stands for. What must never win is a
        // procedural government or ideology variant.
        foreach (var movement in campaign.Nations.Where(n => n.Movement is { Landless: true, Exile: false }))
        {
            var resolved = FlagCatalog.Resolve(campaign, movement);
            var own = resolved == "flag_" + movement.Tag.ToLowerInvariant() ||
                      resolved == "flag_" + movement.Tag.ToLowerInvariant() + "_regional";
            Check(own, movement.Tag + " keeps its own banner ahead of procedural political variants");
        }
        var hamas = campaign.NationByTag("HMS");
        Check(SpriteCatalog.Has("flag_hms_theocracy") && FlagCatalog.Resolve(campaign, hamas).StartsWith("flag_hms"),
            "the actual theocratic-placeholder regression does not replace Hamas's own flag");
        var before = CampaignFingerprint.Of(campaign);
        FlagCatalog.ResolvePreview(campaign, hamas, "theocracy", "theocratic_rule");
        Check(CampaignFingerprint.Of(campaign) == before, "flag preview does not mutate a movement");
        var landed = hamas.Movement.Landless;
        try
        {
            hamas.Movement.Landless = false;
            Check(FlagCatalog.Resolve(campaign, hamas) == "flag_hms",
                "a territorial movement keeps its authored banner instead of a hypothetical regime template");
            hamas.Movement.Landless = true;
            Check(FlagCatalog.ResolveFormationPreview(campaign, hamas, "benelux_union") != "flag_hms",
                "a proclaimed formable outranks the movement's old banner");
            FlagCatalog.Merge("{\"prefer_movement_base\":false}");
            Check(FlagCatalog.Resolve(campaign, hamas) == "flag_hms_theocracy",
                "mods can explicitly opt into political variants for landless movements");
        }
        finally { hamas.Movement.Landless = landed; FlagCatalog.ResetCatalog(); }
        using var banners = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(
            GameDataLoader.DataFolder(), "..", "Tools", "IconCompiler", "Source", "movement-banners.json")));
        foreach (var banner in banners.RootElement.GetProperty("banners").EnumerateObject())
        {
            var directory = Path.Combine(GameDataLoader.DataFolder(), "gfx", "flags");
            // A later gfx declaration can correct an old tag-named PNG without destroying
            // that source file. Test the image the game actually loads, not the stale file.
            var texture = SpriteCatalog.TextureOf(banner.Name);
            var physical = texture.StartsWith("res://game/", StringComparison.Ordinal)
                ? Path.Combine(GameDataLoader.DataFolder(), texture["res://game/".Length..]) : texture;
            var actual = SHA256.HashData(File.ReadAllBytes(physical));
            var original = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, banner.Value.GetString() + ".png")));
            Check(actual.SequenceEqual(original) && !WorkshopSized(banner.Name),
                banner.Name + " resolves to its exact authored movement/homeland artwork, not a workshop placeholder");
        }
    }

    private static void HistoricalMovementBindings(Campaign campaign)
    {
        var before = CampaignFingerprint.Of(campaign);
        var democratic = campaign.NationByTag("BDF");
        Check(democratic?.Movement is not null, "BDF is the catalogued Belarusian democratic movement");
        Check(FlagCatalog.Resolve(campaign, democratic) == "flag_bdf" &&
              SpriteCatalog.TextureOf("flag_bdf") == "res://game/gfx/flags/flag_blr_nationalism.png",
            "Belarusian democratic forces use the existing white-red-white artwork");
        Check(SpriteCatalog.TextureOf("flag_bdf") != SpriteCatalog.TextureOf("flag_culture_belarusian"),
            "correcting one opposition banner does not overwrite the Belarusian cultural/state flag");
        var congo = campaign.NationByTag("AFC");
        Check(congo?.Movement is not null && FlagCatalog.Resolve(campaign, congo) == "flag_afc" &&
              SpriteCatalog.TextureOf("flag_afc") == "res://game/gfx/flags/flag_cod.png",
            "AFC has a documented Congolese homeland fallback rather than a fabricated blue-yellow banner");
        Check(before == CampaignFingerprint.Of(campaign), "historical movement flag lookup does not mutate the world");
        try
        {
            var errors = new List<string>();
            SpriteCatalog.Merge("""{"sprites":{"flag_bdf":"res://game/gfx/flags/flag_blr.png"}}""",
                "res://game/gfx", "test movement override", errors);
            Check(errors.Count == 0 && SpriteCatalog.TextureOf(FlagCatalog.Resolve(campaign, democratic)) ==
                  "res://game/gfx/flags/flag_blr.png", "mods can still explicitly replace a corrected movement banner");
        }
        finally { SpriteCatalog.Load(); }
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
