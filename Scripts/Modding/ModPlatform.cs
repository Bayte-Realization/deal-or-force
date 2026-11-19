using System;
using System.Collections.Generic;
using System.Reflection;
using DealOrForce.Simulation;

namespace DealOrForce.Modding;

internal interface IModScriptRuntime : IDisposable
{
    void Start(IReadOnlyList<ModPackage> packages, object api, Campaign campaign, Action<string, Action> guard, Action<string> log);
    void OnDayAdvanced(Action<string, Action> guard);
    void Stop();
}

/// <summary>
/// Host environment hooks used by <see cref="ModLoader"/> and <see cref="DlcLoader"/> so
/// package discovery, presentation overlays, and data overlays work identically inside
/// Godot and inside headless .NET verification suites.
/// </summary>
internal static class ModPlatform
{
    public static Func<string, string> GlobalizePath { get; set; } = path => path ?? "";

    public static Func<string> UserModsDirectory { get; set; } = () =>
        GameFolders.For(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))?.Mods ?? "";

    public static Func<ContentSelection> ReadSelection { get; set; } = () =>
    {
        var overridePath = Environment.GetEnvironmentVariable("DOF_CONTENT_SELECTION");
        return string.IsNullOrWhiteSpace(overridePath)
            ? new ContentSelection()
            : ContentSelection.Load(overridePath);
    };

    public static Action<string> Print { get; set; } = _ => { };
    public static Action<string> PushWarning { get; set; } = _ => { };
    public static Action<object, Campaign> BindApi { get; set; } = (_, _) => { };
    public static Func<IModScriptRuntime> CreateScriptRuntime { get; set; } = () => null;
    public static Func<Assembly[]> SharedAssemblies { get; set; } = () => new[] { typeof(IGlobalityMod).Assembly };
}
