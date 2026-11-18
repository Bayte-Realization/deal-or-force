using Godot;
using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

namespace DealOrForce;

internal static class PresentationFonts
{
    internal const int MapMsdfPixelRange = 40;
    private static readonly Dictionary<string, Font> Faces = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsMapLabelRole(string role) =>
        string.Equals(role, "map.countries", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, "map.cities", StringComparison.OrdinalIgnoreCase);

    static PresentationFonts()
    {
        // Loading a new campaign/mod catalog invalidates roles, not live users of the old
        // fonts. Do not Dispose fonts that existing Controls/Label3Ds still reference.
        FontCatalog.Changed += () => { lock (Faces) Faces.Clear(); };
    }

    public static Font For(string role)
    {
        lock (Faces)
        {
            if (Faces.TryGetValue(role, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
            return Faces[role] = Load(role);
        }
    }

    public static Font Tabular(string role = "ui.default")
    {
        var key = role + ":tnum";
        lock (Faces)
        {
            if (Faces.TryGetValue(key, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
            var baseFont = For(role);
            var variation = new FontVariation
            {
                BaseFont = baseFont,
                OpentypeFeatures = new Godot.Collections.Dictionary { { "tnum", 1 } }
            };
            return Faces[key] = variation;
        }
    }

    private static Font Load(string role)
    {
        var isMap = IsMapLabelRole(role);
        var msdfSize = string.Equals(role, "map.countries", StringComparison.OrdinalIgnoreCase) ? 64 : 48;
        var file = FontCatalog.FileOf(role);
        if (file.Length > 0 && ResourceLoader.Exists(file))
        {
            var supplied = GD.Load<Font>(file);
            if (supplied is not null)
            {
                if (isMap && supplied is FontFile fileFont)
                {
                    var clone = (FontFile)fileFont.Duplicate();
                    clone.MultichannelSignedDistanceField = true;
                    clone.MsdfPixelRange = MapMsdfPixelRange;
                    clone.MsdfSize = msdfSize;
                    return clone;
                }
                return supplied;
            }
        }
        var families = FontCatalog.FamiliesOf(role);
        if (families.Length > 0)
        {
            var sys = new SystemFont { FontNames = families };
            if (isMap)
            {
                sys.MultichannelSignedDistanceField = true;
                sys.MsdfPixelRange = MapMsdfPixelRange;
                sys.MsdfSize = msdfSize;
            }
            return sys;
        }
        return ThemeDB.FallbackFont;
    }
}
