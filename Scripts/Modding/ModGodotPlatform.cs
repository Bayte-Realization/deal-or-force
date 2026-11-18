using System.Runtime.CompilerServices;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce.Modding;

internal static class ModGodotPlatform
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ModPlatform.GlobalizePath = ProjectSettings.GlobalizePath;
        ModPlatform.UserModsDirectory = () => GamePaths.Mods;
        ModPlatform.ReadSelection = LauncherSelection.Read;
        ModPlatform.Print = message => GD.Print(message);
        ModPlatform.PushWarning = message => GD.PushWarning(message);
        ModPlatform.BindApi = (api, campaign) => (api as ModApi)?.Bind(campaign);
        ModPlatform.CreateScriptRuntime = () => new ModScriptHost();
        ModPlatform.SharedAssemblies = () => new[] { typeof(IGlobalityMod).Assembly, typeof(Node).Assembly };
    }
}
