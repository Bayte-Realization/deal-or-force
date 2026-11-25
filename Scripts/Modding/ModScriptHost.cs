using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce.Modding;

/// <summary>
/// Engine-hosted GDScript and Luau mod execution backing <see cref="ModLoader"/> when running
/// inside Godot. Compiled only in the main game assembly so headless simulation tests stay
/// free of Godot bindings.
/// </summary>
internal sealed class ModScriptHost : IModScriptRuntime
{
    private readonly List<(GodotObject Instance, string ModId)> _gdscript = new();
    private readonly List<(RefCounted Instance, string ModId, Godot.Collections.Dictionary Api)> _engineScripts = new();
    private ModApi _api;

    public bool HasActiveScripts => _gdscript.Count > 0 || _engineScripts.Count > 0;

    public void Start(IReadOnlyList<ModPackage> packages, object api, Campaign campaign, Action<string, Action> guard, Action<string> log)
    {
        Stop();
        _api = api as ModApi;
        foreach (var package in packages)
        {
            LoadGdscript(package, guard, log);
            LoadEngineScript(package, campaign, guard, log);
        }
    }

    public void OnDayAdvanced(Action<string, Action> guard)
    {
        foreach (var plugin in _gdscript)
            if (plugin.Instance.HasMethod("on_day_advanced"))
                guard(plugin.ModId, () => plugin.Instance.Call("on_day_advanced", _api));

        for (var index = 0; index < _engineScripts.Count;)
        {
            var plugin = _engineScripts[index];
            var healthy = false;
            guard(plugin.ModId, () =>
            {
                EngineScriptMod.Invoke(plugin.Instance, "on_day_advanced", plugin.Api);
                healthy = true;
            });
            if (healthy) { index++; continue; }
            plugin.Instance.Dispose();
            plugin.Api.Dispose();
            _engineScripts.RemoveAt(index);
        }
    }

    public void Stop()
    {
        foreach (var (instance, _) in _gdscript) instance.Dispose();
        _gdscript.Clear();
        foreach (var plugin in _engineScripts)
        {
            plugin.Instance.Dispose();
            plugin.Api.Dispose();
        }
        _engineScripts.Clear();
        _api = null;
    }

    public void Dispose() => Stop();

    private void LoadGdscript(ModPackage package, Action<string, Action> guard, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(package.Manifest.GdscriptEntry)) return;
        if (!IsSafeRelativePath(package.Manifest.GdscriptEntry))
        {
            package.Errors.Add("GDScript entry must stay inside the mod directory.");
            return;
        }
        var resourcePath = $"{package.VirtualRoot}/{package.Manifest.GdscriptEntry.Replace('\\', '/')}";

        guard(package.Manifest.Id, () =>
        {
            var script = ResourceLoader.Load<GDScript>(resourcePath);
            if (script is null || !script.CanInstantiate())
                throw new InvalidOperationException($"Cannot instantiate {resourcePath}");
            var instance = script.New().AsGodotObject();
            if (instance is null) throw new InvalidOperationException($"{resourcePath} returned no instance");
            _gdscript.Add((instance, package.Manifest.Id));
            if (instance.HasMethod("on_load")) instance.Call("on_load", _api);
            if (instance.HasMethod("on_campaign_created")) instance.Call("on_campaign_created", _api);
            log?.Invoke($"[{package.Manifest.Id}] GDScript active: {Path.GetFileName(resourcePath)}");
        });
    }

    private void LoadEngineScript(ModPackage package, Campaign campaign, Action<string, Action> guard, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(package.Manifest.EngineScriptEntry)) return;
        if (campaign.HumanSeats.Count > 1)
        { package.Errors.Add("Experimental engine-script mods are single-player only."); return; }
        var path = SafePath(package, package.Manifest.EngineScriptEntry);
        if (path is null || !File.Exists(path))
        { package.Errors.Add("Engine script must exist inside the mod directory."); return; }

        guard(package.Manifest.Id, () =>
        {
            var instance = EngineScriptMod.Load(path);
            var retained = false;
            Godot.Collections.Dictionary bridge = null;
            try
            {
                bridge = EngineScriptMod.CreateApi(_api);
                EngineScriptMod.Invoke(instance, "on_load", bridge);
                EngineScriptMod.Invoke(instance, "on_campaign_created", bridge);
                _engineScripts.Add((instance, package.Manifest.Id, bridge));
                retained = true;
            }
            finally
            {
                if (!retained)
                {
                    instance.Dispose();
                    bridge?.Dispose();
                }
            }
            log?.Invoke($"[{package.Manifest.Id}] engine script active: {Path.GetFileName(path)}");
        });
    }

    private static string SafePath(ModPackage package, string relative)
    {
        if (!IsSafeRelativePath(relative)) return null;
        var root = Path.GetFullPath(package.PhysicalRoot) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(package.PhysicalRoot, relative));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Split('/', '\\').Contains("..");
}
