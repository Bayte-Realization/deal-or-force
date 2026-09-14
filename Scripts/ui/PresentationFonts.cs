using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

internal static class PresentationFonts
{
    public static Font For(string role)
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
