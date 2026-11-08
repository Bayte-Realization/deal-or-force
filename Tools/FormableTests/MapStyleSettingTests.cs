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
            Check(clouds is { Kind: SettingKind.Slider, Minimum: 0, Maximum: 1, Step: .05f, DefaultNumber: 1 },
                "cloud control retains the original sky by default and permits a clear map");
            Check(lighting is { Kind: SettingKind.Toggle, Default: true, NeedsRestart: false, Campaign: false },
                "consistent lighting is a live preference with a reversible legacy option");
            SettingsCatalog.Store(style.Id, "relief");
            SettingsCatalog.Store(clouds.Id, .65f);
            PlayerSettings.UseDirectory(folder);
            Check(SettingsCatalog.Chosen(style.Id) == "relief" && Math.Abs(SettingsCatalog.Number(clouds.Id) - .65f) < .001f,
                "style and cloud transparency survive preference reload");
            SettingsCatalog.Store(style.Id, "unsupported-old-style");
            Check(SettingsCatalog.Chosen(style.Id) == "balanced", "unknown style falls back safely to balanced");
            Loc.ResetCatalog();
            Loc.Language = "russian";
            foreach (var caption in new[] { style.Name, style.Description, clouds.Name, clouds.Description, lighting.Name, lighting.Description }.Concat(style.Labels))
                Check(Loc.Get(caption) != caption, "Russian caption is present: " + caption);
            Check(Loc.Get("Balanced") == "Сбалансированная" && Loc.Get("Relief") == "Рельеф",
                "style captions do not overwrite army stance or editor terminology");
            Loc.Language = "english";
            Check(style.Labels.Select(Loc.Get).SequenceEqual(new[] { "Balanced", "Vivid", "Relief" }),
                "namespaced labels also resolve in English");
        }
        finally
        {
            SettingsCatalog.CurrentCampaign = campaign;
            PlayerSettings.UseDirectory(null);
            Loc.Language = language;
        }
    }
}
