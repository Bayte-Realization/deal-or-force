using System;
using System.IO;
using Godot;

namespace DealOrForce.Modding;

/// <summary>Opt-in ScriptLanguageExtension mods, separate from process workers.</summary>
public static class EngineScriptMod
{
    public const string LuauExtension = "res://game/extensions/luau/luau_script.gdextension";

    // Capabilities, not Object.Call: USER Luau threads must not acquire the
    // adapter's INTERNAL permission just to reach our campaign bridge.
    public static Godot.Collections.Dictionary CreateApi(ModApi api)
    {
        var bridge = new Godot.Collections.Dictionary
        {
            ["log"] = Callable.From<string>(api.log),
            ["get_day"] = Callable.From<int>(api.get_day),
            ["get_date"] = Callable.From<string>(api.get_date),
            ["feature_enabled"] = Callable.From<string, bool>(api.feature_enabled),
            ["set_feature"] = Callable.From<string, bool, bool>(api.set_feature),
            ["get_nation"] = Callable.From<string, Godot.Collections.Dictionary>(api.get_nation),
            ["set_nation_number"] = Callable.From<string, string, double, bool>(api.set_nation_number),
            ["get_define"] = Callable.From<string, double, double>(api.get_define),
            ["set_define"] = Callable.From<string, double, bool>(api.set_define),
            ["get_province_terrain"] = Callable.From<int, Godot.Collections.Dictionary>(api.get_province_terrain),
            ["get_capabilities"] = Callable.From<string[]>(api.get_capabilities),
            ["get_politics"] = Callable.From<string, Godot.Collections.Dictionary>(api.get_politics),
            ["enact_policy"] = Callable.From<string, string, string, int, bool, bool>(api.enact_policy),
            ["manage_debt"] = Callable.From<string, string, double, bool>(api.manage_debt),
            ["add_course_shock"] = Callable.From<string, double, string, bool>(api.add_course_shock)
        };
        bridge.MakeReadOnly();
        return bridge;
    }

    public static void Invoke(RefCounted instance, string hook, Godot.Collections.Dictionary api)
    {
        if (!instance.HasMethod(hook)) return;
        var acknowledgement = instance.Call(hook, api);
        // Script VM errors are logged by Godot, not thrown as C# exceptions.
        // Require an explicit acknowledgement so failed hooks cannot look active.
        if (acknowledgement.VariantType != Variant.Type.Bool || !acknowledgement.AsBool())
            throw new InvalidOperationException($"Luau hook {hook} failed or did not return true; engine-script mod disabled.");
    }

    public static RefCounted Load(string physicalPath)
    {
        var suffix = Path.GetExtension(physicalPath).ToLowerInvariant();
        if (suffix != ".lua")
            throw new InvalidOperationException("The verified engine-script adapter accepts Luau .lua scripts only.");
        if (!ClassDB.ClassExists("LuauScript"))
        {
            if (!Godot.FileAccess.FileExists(LuauExtension))
                throw new InvalidOperationException("Luau adapter is not installed. Run Tools/LanguageAdapters/install-luau.ps1 first.");
            var status = GDExtensionManager.LoadExtension(LuauExtension);
            if (status != GDExtensionManager.LoadStatus.Ok && status != GDExtensionManager.LoadStatus.AlreadyLoaded)
                throw new InvalidOperationException($"Luau extension failed to load ({status}). Restart Godot after installation.");
        }
        var script = ResourceLoader.Load<Script>(physicalPath, cacheMode: ResourceLoader.CacheMode.Ignore);
        if (script is null || !script.CanInstantiate() || script.GetClass() != "LuauScript")
            throw new InvalidOperationException($"Cannot instantiate Luau script: {physicalPath}");
        if (script.GetInstanceBaseType() != "RefCounted")
            throw new InvalidOperationException("Engine-script mod entry must extend RefCounted, not a scene Node.");
        var instance = new RefCounted();
        instance.SetScript(script);
        if (instance.GetScript().AsGodotObject() != script || !instance.HasMethod("on_load"))
        {
            instance.Dispose();
            throw new InvalidOperationException("Engine-script entry requires a Godot-registered on_load(api) method.");
        }
        return instance;
    }
}
