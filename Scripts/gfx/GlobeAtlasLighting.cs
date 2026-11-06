using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    // Reserve user-visible render layer 20 for the atlas surface only. Picking does
    // not use render layers. Camera3D's default mask already includes all 20 layers.
    private const uint AtlasSurfaceLayer = 1u << 19;
    private DirectionalLight3D _atlasSun;
    private uint _scenerySunMask;
    private uint AtlasSurfaceLayers => _atlasSun is null ? 1u : AtlasSurfaceLayer;

    private void CreateAtlasLighting()
    {
        if (RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility") return;
        _scenerySunMask = _daySun.LightCullMask;
        _atlasSun = new DirectionalLight3D { Name = "LinearAtlasSun", ShadowEnabled = false,
            SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly, LightEnergy = _daySun.LightEnergy,
            LightColor = _daySun.LightColor, LightCullMask = AtlasSurfaceLayer };
        // A child inherits the same rotating day/night sun, without a second sky light.
        _daySun.AddChild(_atlasSun);
        ApplyAtlasLighting();
    }

    private void ApplyAtlasLighting()
    {
        if (!IsInstanceValid(_atlasSun)) return;
        var consistent = SettingsCatalog.Of("map_consistent_lighting") is null || SettingsCatalog.Flag("map_consistent_lighting");
        _daySun.LightCullMask = consistent ? _scenerySunMask & ~AtlasSurfaceLayer : _scenerySunMask;
        _atlasSun.Visible = consistent;
        // GLES shadowed lights add separate sRGB passes, overexposing the whole atlas.
        // The unshadowed surface light combines sun+ambient in one linear pass instead.
        // Scenery still uses the original shadowed light and quality setting. The atlas
        // retains its procedural mountain normals/hillshade and cloud-shadow layer.
        // Only scenery's projected shadows ON THE SURFACE require the optional old path.
    }

    private void SyncAtlasSun()
    {
        if (!IsInstanceValid(_atlasSun)) return;
        if (_atlasSun.LightEnergy != _daySun.LightEnergy) _atlasSun.LightEnergy = _daySun.LightEnergy;
        if (_atlasSun.LightColor != _daySun.LightColor) _atlasSun.LightColor = _daySun.LightColor;
    }
}
