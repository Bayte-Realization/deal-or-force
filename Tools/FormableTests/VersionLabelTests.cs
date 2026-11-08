using System;
using DealOrForce.Simulation;

/// <summary>The menu label. Save format and network protocol are separate contracts.</summary>
static class VersionLabelTests
{
    public static void Run()
    {
        var label = GameBuildInfo.VersionLabel;
        var version = GameBuildInfo.Version;
        if (!string.Equals(label, "EntertAlpha 0.6.2.0", StringComparison.Ordinal) ||
            !string.Equals(version, "0.6.2.0", StringComparison.Ordinal) ||
            label.StartsWith("DoF ", StringComparison.Ordinal) ||
            label.StartsWith("ReimAlpha 2", StringComparison.Ordinal))
            throw new System.Exception("FAIL version label: menu shows " + label);
        if (SaveSystem.GameVersion != GameBuildInfo.Version)
            throw new System.Exception("FAIL version label: new-save release label drifted from the menu");
        if (SaveSystem.FormatVersion != 2)
            throw new System.Exception("FAIL version label: save format version was changed");
        System.Console.WriteLine("PASS version label: " + GameBuildInfo.VersionLabel);
    }
}
