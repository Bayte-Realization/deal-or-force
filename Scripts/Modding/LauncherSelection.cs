using System;
using System.IO;
using Godot;

namespace DealOrForce.Modding;

internal static class LauncherSelection
{
    public static bool IsEditorRun => OS.HasFeature("editor");
    private static string OverridePath => System.Environment.GetEnvironmentVariable("DOF_CONTENT_SELECTION");
    private static string DefaultPath => ProjectSettings.GlobalizePath("user://content_selection.json");
    private static string EditorPath => ProjectSettings.GlobalizePath("user://editor_content_selection.json");

    // Explicit custom profiles stay authoritative. On the first editor run, migrate the
    // previous choice once; later launcher changes must not change editor preferences.
    public static string ReadPath => WritePath;
    public static string WritePath => !string.IsNullOrWhiteSpace(OverridePath) ? OverridePath
        : IsEditorRun ? EditorPath : DefaultPath;

    public static ContentSelection Read()
    {
        var path = ReadPath;
        try
        {
            if (IsEditorRun && string.IsNullOrWhiteSpace(OverridePath) && !File.Exists(EditorPath))
                ContentSelection.Load(DefaultPath).Save(EditorPath);
            return ContentSelection.Load(path);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Cannot read content selection {path}: {exception.Message}", exception);
        }
    }
}
