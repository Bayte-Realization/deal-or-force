using System;
using Godot;

namespace DealOrForce.Modding;

internal static class LauncherSelection
{
    public static ContentSelection Read()
    {
        var path = System.Environment.GetEnvironmentVariable("DOF_CONTENT_SELECTION");
        if (string.IsNullOrWhiteSpace(path)) path = ProjectSettings.GlobalizePath("user://content_selection.json");
        try { return ContentSelection.Load(path); }
        catch (Exception exception)
        {
            // A broken profile must not silently start a different modded campaign.
            throw new InvalidOperationException($"Cannot read launcher selection {path}: {exception.Message}", exception);
        }
    }
}
