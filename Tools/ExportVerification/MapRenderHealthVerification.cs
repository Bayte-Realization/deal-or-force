using System;
using System.Threading.Tasks;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class ExportVerification
{
    /// <summary>CPU/day tests cannot see a shader that failed on the GPU. Inspect a
    /// real central map region after drawing; labels alone must not count as a map.</summary>
    private async Task VerifyMapSurface(DealOrForceGame game)
    {
        // Compare actual backends at the SAME authored quality, not Forward+ high
        // against an automatically downgraded GL frame. Disable only the decorative
        // weather for the first unobstructed geometry capture, then verify it too.
        game.VerificationAtlasQuality = QualityProfiles.Ladder[0].Clone();
        var weather = game.VerificationRenderWeather;
        game.VerificationRenderWeather = false;
        game.VerificationFocusAdministration(game.VerificationWorld.NationByTag("POL"));
        await Wait(1.0);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var frame = GetTree().Root.GetTexture().GetImage();
        var samples = 0; var white = 0; var chromatic = 0;
        for (var y = (int)(frame.GetHeight() * .22); y < frame.GetHeight() * .72; y += 13)
        for (var x = (int)(frame.GetWidth() * .38); x < frame.GetWidth() * .76; x += 13)
        {
            var p = frame.GetPixel(x, y);
            var high = Math.Max(p.R, Math.Max(p.G, p.B));
            var low = Math.Min(p.R, Math.Min(p.G, p.B));
            samples++;
            if (low > .90f && high - low < .06f) white++;
            if (high > .08f && high - low > .08f) chromatic++;
        }
        var whiteShare = samples > 0 ? white / (float)samples : 1f;
        var colourShare = samples > 0 ? chromatic / (float)samples : 0f;
        GD.Print($"MAP_RENDER_HEALTH renderer={RenderingServer.GetCurrentRenderingMethod()} samples={samples} white={whiteShare:F3} colour={colourShare:F3}");
        GD.Print($"MAP_RENDER_MEMORY driver={RenderingServer.GetCurrentRenderingDriverName()} texture_bytes={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed)} buffer_bytes={RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed)}");
        // Real weather can desaturate the surface. A coloured pixel floor must catch
        // blank white/black geometry without rejecting legitimate winter cloud cover.
        Check(samples > 100 && whiteShare < .70f && colourShare > .03f,
            "GPU draws real coloured map geometry, not a white/blank surface with labels");
        Check(game.VerificationPackedMapLookups(), "packed GPU lookup layers retain the exact live color/data bytes");
        await Capture("map-render-health-" + RenderingServer.GetCurrentRenderingMethod());
        game.VerificationRenderWeather = weather;
        await Wait(.25);
        await Capture("map-render-weather-" + RenderingServer.GetCurrentRenderingMethod());
    }
}

public sealed partial class DealOrForceGame
{
    internal bool VerificationPackedMapLookups() => _globe.VerificationPackedMapLookups();
    internal bool VerificationRenderWeather { get => _globe.WeatherEnabled; set => _globe.WeatherEnabled = value; }
}

public sealed partial class GlobePresentation
{
    internal bool VerificationPackedMapLookups()
    {
        if (_packedLookups is null) return true; // The regular renderer has no packed tables.
        foreach (var (uniform, original) in new[] {
            ("nation_palette", _nationPaletteImage), ("province_control_colors", _controlImage),
            ("province_owner_colors", _ownerImage), ("province_owner_ids", _ownerIdImage),
            ("front_membership", _frontImage), ("fog_visibility", _visionImage) })
        {
            if (original is null) continue;
            using var expected = (Image)original.Duplicate();
            expected.Convert(Image.Format.Rgba8);
            using var actual = _packedLookups.GetLayerData(PackedMapShader.Layer(PackedMapShader.Lookups, uniform));
            if (!System.Linq.Enumerable.SequenceEqual(expected.GetData(), actual.GetData())) return false;
        }
        // A late refresh must replace an existing array layer, not update a stale
        // standalone ImageTexture that the packed material never reads.
        using var sentinel = Image.CreateEmpty(256, 256, false, Image.Format.Rgba8);
        sentinel.Fill(new Color(.2f, .4f, .6f, .8f));
        UploadPacked("province_control_colors", sentinel);
        using var changed = _packedLookups.GetLayerData(PackedMapShader.Layer(PackedMapShader.Lookups, "province_control_colors"));
        var live = System.Linq.Enumerable.SequenceEqual(sentinel.GetData(), changed.GetData());
        if (_controlImage is not null) UploadPacked("province_control_colors", _controlImage);
        return live;
    }
}
