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

    internal static void Synchronize()
    {
        if (_material is null) return;
        _material.SetShaderParameter("animate_flags", SettingsCatalog.Flag("animated_flags"));
        _material.SetShaderParameter("fabric_enabled", SettingsCatalog.Flag("flag_fabric"));
        _material.SetShaderParameter("wave_speed", Math.Clamp(Ui.Metric("flag.wave_speed", .8f), 0, 4));
        _material.SetShaderParameter("wave_amplitude", Math.Clamp(Ui.Metric("flag.wave_amplitude", .016f), 0, .04f));
        _material.SetShaderParameter("fold_strength", Math.Clamp(Ui.Metric("flag.fold_strength", .16f), 0, .35f));
        _material.SetShaderParameter("weave_strength", Math.Clamp(Ui.Metric("flag.weave_strength", .035f), 0, .1f));
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
