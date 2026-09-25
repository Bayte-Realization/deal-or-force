using Godot;

namespace DealOrForce;

/// <summary>
/// The renderer the game starts on, chosen from the settings screen. The work is done by the
/// start-up guard (Scripts/Boot/boot_guard.gd), which writes the choice into the settings
/// override project.godot names, user://graphics_override.cfg, and which steps down the
/// renderer chain by itself when a start never shows the menu. It is GDScript so that it runs
/// even when the C# half of the game does not load; this is the C# half's way of asking it.
/// </summary>
public static class GraphicsRenderer
{
    public static void Choose(Node from, string driver)
    {
        var guard = from?.GetNodeOrNull("/root/BootGuard");
        if (guard is null) return;
        guard.Call("set_renderer", string.IsNullOrWhiteSpace(driver) ? "auto" : driver);
    }
}
