using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Every layout key the interface resolved before the interface rebuild began
/// (docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md, step 0) still resolves, to a finite rectangle -
/// and the material opt-in of step 1 changes nothing for a key that has not asked for it.
///
/// The list is fixtures/layout_keys.txt. To re-pin it after a key is added on purpose, run with
/// DOF_WRITE_LAYOUT_KEYS=1; a key is never removed from it by hand.
/// </summary>
static class LayoutKeyPinTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("layout keys: " + label);
        Console.WriteLine("PASS layout keys: " + label);
    }

    internal static string Fixtures => Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), "..", "Tools", "FormableTests", "fixtures"));

    public static void Run()
    {
        InterfaceSkin.ResetCatalog();
        var path = Path.Combine(Fixtures, "layout_keys.txt");
        var live = InterfaceSkin.Panels.Select(panel => panel.Key).ToList();
        if (Environment.GetEnvironmentVariable("DOF_WRITE_LAYOUT_KEYS") == "1")
            File.WriteAllLines(path, new[]
            {
                "# Every layout key InterfaceSkin resolves after ResetCatalog: compiled defaults, game/interface and game/gui.",
                "# Pinned at step 0 of docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md. LayoutKeyPinTests requires each to resolve.",
            }.Concat(live));

        var pinned = File.ReadAllLines(path).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal)).ToList();
        Check(pinned.Count >= 40 && pinned.Distinct(StringComparer.OrdinalIgnoreCase).Count() == pinned.Count,
            $"the pinned list holds {pinned.Count} distinct keys");

        static bool Finite(InterfaceSkin.PanelRect r) => new[]
        {
            r.AnchorLeft, r.AnchorTop, r.AnchorRight, r.AnchorBottom,
            r.OffsetLeft, r.OffsetTop, r.OffsetRight, r.OffsetBottom
        }.All(float.IsFinite);
        var dead = pinned.Where(key => !InterfaceSkin.HasLayout(key) || !Finite(InterfaceSkin.Layout(key))).ToList();
        Check(dead.Count == 0, "every pinned key resolves with finite anchors and offsets" +
            (dead.Count == 0 ? "" : ": missing " + string.Join(", ", dead.Take(10))));

        // Step 1v: a material is opt-in per key, and the whole mechanism ships switched off.
        Check(InterfaceSkin.Metric(InterfaceSkin.MaterialSwitch, -1f) == 0f, "panel materials ship switched off (ui.materials = 0)");
        Check(pinned.All(key => InterfaceSkin.LayoutMaterial(key) is null),
            "with the switch off no key resolves to a material, so no panel changes on screen");
        Check(InterfaceSkin.DeclaredLayoutMaterial("hud.outliner") == "plate",
            "the one opt-in, the situation report, is declared in data");

        var campaign = Campaign.CreateBootstrap(73);
        var before = CampaignFingerprint.Of(campaign);
        var errors = new List<string>();
        InterfaceSkin.Merge("{\"metrics\":{\"ui.materials\":1}}", "test", errors);
        Check(InterfaceSkin.LayoutMaterial("hud.outliner") == "plate" && InterfaceSkin.LayoutMaterial("hud.top") is null,
            "switched on, a key with a material resolves to its role and a key without one to null");

        var rect = InterfaceSkin.Layout("hud.outliner");
        InterfaceSkin.Merge("{\"layout\":{\"hud.outliner\":{\"material\":\"well\"}}}", "test", errors);
        Check(errors.Count == 0 && InterfaceSkin.LayoutMaterial("hud.outliner") == "well" && InterfaceSkin.Layout("hud.outliner").Equals(rect),
            "a layout entry naming only a material re-dresses the panel without moving it");
        InterfaceSkin.Merge("{\"layout\":{\"hud.outliner\":{\"material\":\"\"}}}", "test", errors);
        Check(InterfaceSkin.LayoutMaterial("hud.outliner") is null, "an empty material takes the opt-in away again");
        InterfaceSkin.Merge("{\"layout\":{\"hud.outliner\":{\"material\":7}}}", "test", errors);
        Check(errors.Count == 1, "a material that is not a role name is reported, not fatal");
        Check(before == CampaignFingerprint.Of(campaign), "layout and material data never change the world fingerprint");
        InterfaceSkin.ResetCatalog();
    }
}
