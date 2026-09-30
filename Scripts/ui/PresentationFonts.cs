using Godot;
using System;
using System.Collections.Generic;
using DealOrForce.Simulation;

namespace DealOrForce;

internal static class PresentationFonts
{
    private static readonly Dictionary<string, Font> Faces = new(StringComparer.OrdinalIgnoreCase);

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

    private static Font Load(string role)
    {
        var file = FontCatalog.FileOf(role);
        if (file.Length > 0 && ResourceLoader.Exists(file))
        {
            var supplied = GD.Load<Font>(file);
            if (supplied is not null) return supplied;
        }
        var families = FontCatalog.FamiliesOf(role);
        if (families.Length > 0) return new SystemFont { FontNames = families };
        return ThemeDB.FallbackFont;
    }
}
