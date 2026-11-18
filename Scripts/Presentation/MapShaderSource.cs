using System.IO;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Single canonical globe shader source (<c>game/shaders/globe_map.gdshader</c>) shared between
/// the Godot runtime and pure-C# headless contract tests.
/// </summary>
public static class MapShaderSource
{
    public const string ResourcePath = "res://game/shaders/globe_map.gdshader";

    public static string LocateFile() =>
        GameDataLoader.FindDataFile("shaders", "globe_map.gdshader")
        ?? throw new FileNotFoundException("Could not locate game/shaders/globe_map.gdshader.");

    public static string LoadFull() =>
        File.ReadAllText(LocateFile()).Replace("\r\n", "\n");
}
