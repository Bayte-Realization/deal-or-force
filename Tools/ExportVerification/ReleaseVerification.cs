using System;
using System.IO;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyReleaseMenu(MainMenuScene menu, bool developmentProbe)
    {
        // Source-menu checks cannot certify a packaged EXE. Keep every redistribution
        // assertion mandatory for the exported probe (which never uses development).
        if (!developmentProbe)
        {
            var root = Path.GetDirectoryName(OS.GetExecutablePath());
            foreach (var file in new[] { "Deal or Force.exe", "Deal or Force Launcher.exe" })
            {
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(root, file));
            Check(version.FileVersion == GameBuildInfo.Version &&
                    (version.ProductVersion == GameBuildInfo.Version || version.ProductVersion == GameBuildInfo.Version + ".0"),
                    file + " Windows file/product version is " + GameBuildInfo.Version);
            }
            Check(File.Exists(Path.Combine(root, "THIRD_PARTY_NOTICES.md")) &&
                File.Exists(Path.Combine(root, "LICENSE")) && File.Exists(Path.Combine(root, "LICENSE-EXCEPTIONS.md")),
                "root redistribution notices are present");
            Check(!Directory.Exists(Path.Combine(root, "licenses")) && !Directory.Exists(Path.Combine(root, "docs")),
                "no licenses or docs directory beside the executable");
        }
        foreach (var language in new[] { "english", "russian" })
        {
            menu.VerificationReleaseLanguage(language);
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                GetTree().Root.Size = size;
                await Wait(.4);
                var label = menu.FindChild("GameVersion", true, false) as Label;
                Check(label is not null && label.Text == GameBuildInfo.VersionLabel && label.IsVisibleInTree(),
                    "visible menu release label: " + language + " " + size.X);
                await Capture("release-menu-" + language + "-" + size.X);
            }
        }
    }
}

public partial class MainMenuScene
{
    // Read-only visual probe: never persists a language into player preferences.
    internal void VerificationReleaseLanguage(string language)
    {
        Loc.Language = language;
        Relabel(this);
        _language.Text = LanguageLabel();
    }
}
