using System;
using System.IO;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>One shader/material and one settings pulse for every interface flag.</summary>
internal static class FlagClothPresentation
{
    private static ShaderMaterial _material;
    internal static ShaderMaterial Material
    {
        get
        {
            if (_material is not null) return _material;
            var path = SpriteCatalog.ShaderOf("flag_cloth", "res://game/gfx/interface/shaders/flag_cloth.gdshader");
            var physical = path.StartsWith("res://game/", StringComparison.Ordinal)
                ? GameDataLoader.FindDataFile(path[11..].Split('/')) : path;
            var code = !string.IsNullOrEmpty(physical) && File.Exists(physical) ? File.ReadAllText(physical)
                : Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : "";
            if (string.IsNullOrWhiteSpace(code)) return null;
            _material = new ShaderMaterial { Shader = new Shader { Code = code } };
            Synchronize();
            return _material;
        }
    }

    private static ShaderMaterial _syncedMaterial;
    internal static void ReleaseStaticResources() { _material = null; _syncedMaterial = null; }
    private static bool _lastAnimateFlags, _lastFabricEnabled;
    private static float _lastWaveSpeed = float.NaN;
    private static float _lastWaveAmplitude = float.NaN;
    private static float _lastFoldStrength = float.NaN;
    private static float _lastWeaveStrength = float.NaN;

    internal static void Synchronize()
    {
        if (_material is null) return;
        var animateFlags = SettingsCatalog.Flag("animated_flags");
        var fabricEnabled = SettingsCatalog.Flag("flag_fabric");
        var waveSpeed = Math.Clamp(Ui.Metric("flag.wave_speed", .8f), 0, 4);
        var waveAmplitude = Math.Clamp(Ui.Metric("flag.wave_amplitude", .016f), 0, .04f);
        var foldStrength = Math.Clamp(Ui.Metric("flag.fold_strength", .16f), 0, .35f);
        var weaveStrength = Math.Clamp(Ui.Metric("flag.weave_strength", .035f), 0, .1f);
        if (ReferenceEquals(_syncedMaterial, _material) &&
            animateFlags == _lastAnimateFlags &&
            fabricEnabled == _lastFabricEnabled &&
            waveSpeed == _lastWaveSpeed &&
            waveAmplitude == _lastWaveAmplitude &&
            foldStrength == _lastFoldStrength &&
            weaveStrength == _lastWeaveStrength)
            return;

        _syncedMaterial = _material;
        _lastAnimateFlags = animateFlags;
        _lastFabricEnabled = fabricEnabled;
        _lastWaveSpeed = waveSpeed;
        _lastWaveAmplitude = waveAmplitude;
        _lastFoldStrength = foldStrength;
        _lastWeaveStrength = weaveStrength;
        _material.SetShaderParameter("animate_flags", animateFlags);
        _material.SetShaderParameter("fabric_enabled", fabricEnabled);
        _material.SetShaderParameter("wave_speed", waveSpeed);
        _material.SetShaderParameter("wave_amplitude", waveAmplitude);
        _material.SetShaderParameter("fold_strength", foldStrength);
        _material.SetShaderParameter("weave_strength", weaveStrength);
    }
}

public partial class FlagClothSettingsPulse : Node
{
    private double _remaining;
    public override void _Process(double delta)
    {
        if ((_remaining -= delta) > 0) return;
        _remaining = .25;
        FlagClothPresentation.Synchronize();
    }
}
