using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyMapStyles(DealOrForceGame game)
    {
        await VerifyAtlasColorTransfer();
        Check(game.VerificationAtlasLightingSeparated(), "atlas linear light is isolated; scenery retains its quality-controlled shadow light");
        // The runner uses PRIVATE documents. Never save test choices to a player profile.
        // Fix the camera, quality and weather clock across drivers (world seed is fixed too).
        game.VerificationSetAtlasWeatherClock(40);
        foreach (var flat in new[] { false, true })
        {
            game.VerificationAtlasStyleCamera(flat);
            await Wait(2);
            foreach (var style in new[] { "balanced", "vivid", "relief" })
            {
                SettingsCatalog.Store("map_visual_style", style);
                SettingsCatalog.Store("map_cloud_opacity", .65f);
                game.VerificationApplyAtlasStyle();
                game.VerificationRenderWeather = false;
                await Wait(.6);
                await Capture("map-style-" + (flat ? "flat-" : "globe-") + style);
                game.VerificationRenderWeather = true;
                await Wait(.2);
                await Capture("map-style-" + (flat ? "flat-" : "globe-") + style + "-weather");
            }
        }
        SettingsCatalog.Reset("map_visual_style");
        SettingsCatalog.Reset("map_cloud_opacity");
        game.VerificationApplyAtlasStyle();
        // Separate material diagnostic: do not confuse illumination differences with
        // texture decoding. Preserve the ordinary lit/context frames captured above.
        game.VerificationRenderWeather = false;
        var lit = game.VerificationAtlasShader;
        var grading = game.VerificationAtlasGrading;
        try
        {
            game.VerificationAtlasGrading = false;
            game.VerificationAtlasShader = lit.Replace("render_mode diffuse_burley,", "render_mode unshaded,", StringComparison.Ordinal);
            await Wait(.6);
            await Capture("map-material-unlit-diagnostic");
        }
        finally { game.VerificationAtlasShader = lit; game.VerificationAtlasGrading = grading; }
        var shadows = game.VerificationAtlasSunShadows;
        try
        {
            game.VerificationAtlasSunShadows = false;
            await Wait(.6);
            await Capture("map-light-singlepass-diagnostic");
        }
        finally { game.VerificationAtlasSunShadows = shadows; }
        SettingsCatalog.Store("map_consistent_lighting", false);
        game.VerificationApplyAtlasStyle();
        Check(game.VerificationAtlasLightingSeparated(legacy: true), "legacy projected-surface lighting can be selected without changing quality");
        await Wait(.3);
        await Capture("map-legacy-projected-shadows");
        SettingsCatalog.Reset("map_consistent_lighting");
        game.VerificationApplyAtlasStyle();
        await VerifyMapStyleOptions(game);
    }

    private async Task VerifyMapStyleOptions(DealOrForceGame game)
    {
        var originalSize = GetTree().Root.Size;
        var originalLanguage = Loc.Language;
        try
        {
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language;
                GetTree().Root.Size = size;
                game.Call("RefreshLocalisedText");
                game.VerificationShowMapStyleOptions();
                await Wait(.35);
                var style = game.VerificationMapStyleRow("Map visual style");
                Check(style is not null && style.GetChildren().OfType<OptionButton>().FirstOrDefault() is { } choice &&
                    choice.ItemCount == 3, "three real style choices are reachable: " + language + " " + size.X);
                var option = style?.GetChildren().OfType<OptionButton>().FirstOrDefault();
                if (option is not null) option.Selected = 1;
                option?.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
                Check(SettingsCatalog.Chosen("map_visual_style") == "vivid" && game.VerificationAtlasSaturation > 1f,
                    "real style control immediately updates the material on pause");
                game.VerificationScrollToStyle(style);
                await Wait(.2);
                await Capture("map-style-options-" + language + "-" + size.X);
            }
        }
        finally
        {
            game.Call("HideSettings");
            Loc.Language = originalLanguage;
            GetTree().Root.Size = originalSize;
            SettingsCatalog.Reset("map_visual_style");
            game.VerificationApplyAtlasStyle();
        }
    }

    private async Task VerifyAtlasColorTransfer()
    {
        // Separate diagnostic viewport, never an overlay on a map evidence image.
        // Bands exercise literals, source_color texture samples, and untouched raw data.
        using var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        image.Fill(new Color(.2f, .4f, .6f));
        using var texture = ImageTexture.CreateFromImage(image);
        using var shader = new Shader { Code = @"shader_type spatial;
render_mode unshaded, fog_disabled;
uniform sampler2D colour : source_color, filter_nearest;
uniform sampler2D raw_data : filter_nearest;
" + MapColorSpaceShader.Helpers + @"
void fragment() {
    vec3 result = atlas_decode_srgb(vec3(0.2, 0.4, 0.6));
    if (UV.x > 0.333) result = atlas_color_sample(texture(colour, UV)).rgb;
    if (UV.x > 0.666) result = texture(raw_data, UV).rgb;
    ALBEDO = atlas_color_output(result);
}" };
        using var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("colour", texture);
        material.SetShaderParameter("raw_data", texture);
        var viewport = new SubViewport { Size = new Vector2I(96, 32), OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(viewport);
        viewport.AddChild(new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 2, Position = new Vector3(0, 0, 3), Current = true });
        viewport.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(6, 2) }, MaterialOverride = material });
        try
        {
            await Wait(.3);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var frame = viewport.GetTexture().GetImage();
            var expected = new Color(.2f, .4f, .6f);
            var rawExpected = expected.LinearToSrgb();
            foreach (var (x, reference, label) in new[] { (16, expected, "literal"),
                         (48, expected, "source_colour"), (80, rawExpected, "raw_data") })
            {
                var actual = frame.GetPixel(x, 16);
                var error = Math.Max(Math.Abs(actual.R - reference.R), Math.Max(Math.Abs(actual.G - reference.G), Math.Abs(actual.B - reference.B)));
                GD.Print($"ATLAS_COLOR_TRANSFER {label} actual={actual} expected={reference} error={error:F4}");
                Check(error < .025f, "GPU colour transfer preserves " + label + " on " + RenderingServer.GetCurrentRenderingMethod());
            }
        }
        finally { viewport.QueueFree(); await Wait(.1); }
    }
}

public sealed partial class DealOrForceGame
{
    internal void VerificationApplyAtlasStyle() => _globe.ApplyMapVisualStyle();
    internal void VerificationSetAtlasWeatherClock(double time) => _globe.VerificationWeatherTime = time;
    internal string VerificationAtlasShader { get => _globe.VerificationAtlasShader; set => _globe.VerificationAtlasShader = value; }
    internal bool VerificationAtlasGrading { get => _globe.VerificationAtlasGrading; set => _globe.VerificationAtlasGrading = value; }
    internal bool VerificationAtlasSunShadows { get => _globe.VerificationAtlasSunShadows; set => _globe.VerificationAtlasSunShadows = value; }
    internal bool VerificationAtlasLightingSeparated(bool legacy = false) => _globe.VerificationAtlasLightingSeparated(legacy);
    internal float VerificationAtlasSaturation => _globe.VerificationAtlasSaturation;
    internal void VerificationShowMapStyleOptions() { ShowSettings(); ShowSettingsCategory("video"); }
    internal Control VerificationMapStyleRow(string source) => _settingsRows.FindChildren("*", "Label", true, false)
        .OfType<Label>().FirstOrDefault(label => label.Text == Loc.Get(source))?.GetParent() as Control;
    internal void VerificationScrollToStyle(Control row)
    {
        if (row is null) return;
        for (var parent = _settingsRows.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll)
            { scroll.ScrollVertical = Math.Max(0, (int)(row.GlobalPosition.Y - _settingsRows.GlobalPosition.Y) - 8); break; }
    }
    internal void VerificationAtlasStyleCamera(bool flat)
    {
        _globe.SetFlatMap(flat);
        _globe.FocusOn(flat ? 48 : 32, flat ? 26 : 65, flat ? 7f : 13.5f);
    }
}

public sealed partial class GlobePresentation
{
    internal double? VerificationWeatherTime;
    internal string VerificationAtlasShader { get => _globeMaterial.Shader.Code; set => _globeMaterial.Shader.Code = value; }
    internal bool VerificationAtlasGrading { get => _skyEnvironment.Environment.AdjustmentEnabled; set => _skyEnvironment.Environment.AdjustmentEnabled = value; }
    internal bool VerificationAtlasSunShadows { get => _daySun.ShadowEnabled; set => _daySun.ShadowEnabled = value; }
    internal float VerificationAtlasSaturation => (float)_globeMaterial.GetShaderParameter("political_saturation");
    internal bool VerificationAtlasLightingSeparated(bool legacy)
    {
        if (_atlasSun is null) return true;
        return _globeNode.Layers == AtlasSurfaceLayer && !_atlasSun.ShadowEnabled &&
            (legacy ? !_atlasSun.Visible && (_daySun.LightCullMask & AtlasSurfaceLayer) != 0 :
                _atlasSun.Visible && (_daySun.LightCullMask & AtlasSurfaceLayer) == 0 && _atlasSun.LightCullMask == AtlasSurfaceLayer);
    }
}
