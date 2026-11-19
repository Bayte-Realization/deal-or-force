using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

static class MapStyleSettingTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS map style settings: " + message); }

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dof-map-style-settings-" + Guid.NewGuid().ToString("N"));
        var migrationFolder = Path.Combine(Path.GetTempPath(), "dof-map-style-migrate-" + Guid.NewGuid().ToString("N"));
        var classicFolder = Path.Combine(Path.GetTempPath(), "dof-map-style-classic-" + Guid.NewGuid().ToString("N"));
        var language = Loc.Language;
        var campaign = SettingsCatalog.CurrentCampaign;
        try
        {
            SettingsCatalog.CurrentCampaign = null;
            PlayerSettings.UseDirectory(folder);
            var errors = new System.Collections.Generic.List<string>();
            SettingsCatalog.ResetCatalog(errors);
            var style = SettingsCatalog.Of("map_visual_style");
            var clouds = SettingsCatalog.Of("map_cloud_opacity");
            var lighting = SettingsCatalog.Of("map_consistent_lighting");
            var inertia = SettingsCatalog.Of("map_camera_inertia");
            Check(inertia is { Kind: SettingKind.Toggle, Default: true, NeedsRestart: false, Campaign: false },
                "camera inertia is a live, local and reversible preference");
            SettingsCatalog.Store(inertia.Id, false);
            PlayerSettings.UseDirectory(folder);
            Check(!SettingsCatalog.Flag(inertia.Id), "disabled camera inertia survives preference reload");
            Check(errors.Count == 0 && style is { Category: "video", Campaign: false, NeedsRestart: false },
                "styles are live video preferences, not rules or renderer selections");
            Check(style.Values.SequenceEqual(new[] { "balanced", "vivid", "relief" }) &&
                SettingsCatalog.Chosen(style.Id) == "balanced", "original balanced style is the default");

            var look = SettingsCatalog.Of("map_look");
            Check(look is { Category: "video", Kind: SettingKind.Choice, Campaign: false, NeedsRestart: false } &&
                  look.Values.SequenceEqual(new[] { "realistic", "paper", "atlas", "classic" }) &&
                  SettingsCatalog.Chosen(look.Id) == "realistic",
                "the map look is a live 4-way video preference: realistic by default, paper/atlas/classic one click away");

            var fill = SettingsCatalog.Of("map_political_fill");
            Check(fill is { Category: "video", Kind: SettingKind.Choice, Campaign: false, NeedsRestart: false } &&
                  fill.Values.SequenceEqual(new[] { "classic_opaque", "balanced", "translucent" }) &&
                  SettingsCatalog.Chosen(fill.Id) == "translucent",
                "country colour fill is a live optional preference: classic_opaque, balanced, translucent");

            Check(clouds is { Kind: SettingKind.Slider, Minimum: 0, Maximum: 1, Step: .05f, DefaultNumber: 1 },
                "cloud control retains the original sky by default and permits a clear map");
            Check(lighting is { Kind: SettingKind.Toggle, Default: true, NeedsRestart: false, Campaign: false },
                "consistent lighting is a live preference with a reversible legacy option");
            SettingsCatalog.Store(style.Id, "relief");
            SettingsCatalog.Store(clouds.Id, .65f);
            SettingsCatalog.Store(look.Id, "paper");
            SettingsCatalog.Store(fill.Id, "balanced");
            PlayerSettings.UseDirectory(folder);
            Check(SettingsCatalog.Chosen(style.Id) == "relief" &&
                  Math.Abs(SettingsCatalog.Number(clouds.Id) - .65f) < .001f &&
                  SettingsCatalog.Chosen(look.Id) == "paper" &&
                  SettingsCatalog.Chosen(fill.Id) == "balanced",
                "style, cloud transparency, map mood and political fill survive preference reload");

            SettingsCatalog.Store(style.Id, "unsupported-old-style");
            Check(SettingsCatalog.Chosen(style.Id) == "balanced", "unknown style falls back safely to balanced");

            // One-time migration from Wave 1/2 saved "atlas" -> "realistic" while preserving manual choices thereafter.
            Directory.CreateDirectory(migrationFolder);
            File.WriteAllText(Path.Combine(migrationFolder, "settings.json"), "{ \"map_look\": \"atlas\" }");
            PlayerSettings.UseDirectory(migrationFolder);
            Check(SettingsCatalog.Chosen("map_look") == "realistic" && PlayerSettings.Bool(MapLook.MigrationKeyV3, false),
                "pre-Wave-3 saved atlas upgrades once on boot to realistic and stamps map_look_default_v3");
            SettingsCatalog.Store("map_look", "atlas");
            PlayerSettings.UseDirectory(migrationFolder);
            Check(SettingsCatalog.Chosen("map_look") == "atlas",
                "subsequent manual choice of atlas is never overwritten on reload");

            Directory.CreateDirectory(classicFolder);
            File.WriteAllText(Path.Combine(classicFolder, "settings.json"), "{ \"map_look\": \"classic\" }");
            PlayerSettings.UseDirectory(classicFolder);
            Check(SettingsCatalog.Chosen("map_look") == "classic",
                "pre-existing explicit classic choice is preserved during v3 migration");

            Loc.ResetCatalog();
            Loc.Language = "russian";
            foreach (var caption in new[] { style.Name, style.Description, clouds.Name, clouds.Description, lighting.Name, lighting.Description,
                         look.Name, look.Description, fill.Name, fill.Description }
                     .Concat(style.Labels).Concat(look.Labels).Concat(fill.Labels))
                Check(Loc.Get(caption) != caption, "Russian caption is present: " + caption);
            Check(Loc.Get("Balanced") == "Сбалансированная" && Loc.Get("Relief") == "Рельеф",
                "style and fill captions do not overwrite army stance or editor terminology");
            Loc.Language = "english";
            Check(style.Labels.Select(Loc.Get).SequenceEqual(new[] { "Balanced", "Vivid", "Relief" }) &&
                  look.Labels.Select(Loc.Get).SequenceEqual(new[] { "Realistic (AAA)", "Paper chart (always)", "Atlas (paper from afar)", "Classic" }) &&
                  fill.Labels.Select(Loc.Get).SequenceEqual(new[] { "Solid", "Balanced", "Translucent" }),
                "namespaced mood and political fill labels resolve cleanly in English");
        }
        finally
        {
            SettingsCatalog.CurrentCampaign = campaign;
            PlayerSettings.UseDirectory(null);
            Loc.Language = language;
        }
    }
}
