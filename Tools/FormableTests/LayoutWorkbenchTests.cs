using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class LayoutWorkbenchTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }

    public static void Run()
    {
        var source = "{\"layout\":{\"hud.side_panel\":{\"offsets\":[20,30,420,630],\"extension\":true}},\"metrics\":{\"test\":7}}";
        var inherited = new[] { ("hud.side_panel", new InterfaceSkin.PanelRect(0, 0, 0, 1, 0, 0, 0, 0)),
            ("hud.top", new InterfaceSkin.PanelRect(0, 0, 1, 0, 0, 0, 0, 80)) };
        var parsed = LayoutWorkbench.Read(source, inherited);
        var original = parsed["hud.side_panel"];
        Check(original.AnchorBottom == 1 && original.OffsetLeft == 20, "visual editor inherits omitted anchors");
        var moved = LayoutWorkbench.Move(original, 24, -16);
        Check(moved.OffsetLeft == 44 && moved.OffsetRight == 444 && moved.AnchorBottom == 1,
            "drag changes offsets without changing anchors or size");
        var written = LayoutWorkbench.SetRect(source, "hud.side_panel", moved);
        var root = JsonNode.Parse(written);
        Check(root["metrics"]["test"].GetValue<int>() == 7 && root["layout"]["hud.side_panel"]["extension"].GetValue<bool>(),
            "layout edits preserve unrelated sections and extension fields");
        Check(!LayoutWorkbench.HasOverride(written, "hud.top") && source.Contains("[20,30,420,630]"),
            "inherited panels are not copied into the patch and source is untouched");
        Check(LayoutWorkbench.Read(written, inherited)["hud.side_panel"] == moved, "layout draft roundtrips exactly");
        var removed = LayoutWorkbench.RemoveOverride(written, "hud.side_panel");
        Check(!LayoutWorkbench.HasOverride(removed, "hud.side_panel") && LayoutWorkbench.Read(removed, inherited)["hud.side_panel"] == inherited[0].Item2,
            "removing a file override restores inherited geometry");
        var resized = LayoutWorkbench.Resize(moved, -10000, -10000, 1920, 1080);
        var bounds = LayoutWorkbench.Measure(resized, 1920, 1080);
        Check(bounds.Width == 32 && bounds.Height == 24, "drag resize cannot invert a panel");
        var rebound = LayoutWorkbench.Reanchor(moved, new float[] { 1, 1, 1, 1 }, 1920, 1080);
        Check(LayoutWorkbench.Measure(rebound, 1920, 1080) == LayoutWorkbench.Measure(moved, 1920, 1080),
            "changing anchor preset preserves reference-screen rectangle");
        Check(LayoutWorkbench.Measure(rebound, 2560, 1440).X == LayoutWorkbench.Measure(rebound, 1920, 1080).X + 640,
            "right anchor follows a wider resolution");
        var inside = new InterfaceSkin.PanelRect(.5f, 0, .5f, 0, -200, 40, 200, 400);
        Check(LayoutWorkbench.FitInside(inside, 1280, 720) == inside, "fit is an exact no-op for a visible panel");
        var offscreen = LayoutWorkbench.Move(inside, 3000, -1000);
        var fitted = LayoutWorkbench.FitInside(offscreen, 1280, 720);
        Check(LayoutWorkbench.Measure(fitted, 1280, 720) == new LayoutWorkbench.Bounds(880, 0, 400, 360) &&
            LayoutWorkbench.Anchors(fitted).SequenceEqual(LayoutWorkbench.Anchors(offscreen)),
            "fit recovers an off-screen panel without shrinking it or changing anchors");
        var huge = new InterfaceSkin.PanelRect(0, 0, 1, 1, -200, -100, 200, 100);
        Check(LayoutWorkbench.Measure(LayoutWorkbench.FitInside(huge, 1280, 720), 1280, 720) == new LayoutWorkbench.Bounds(0, 0, 1280, 720),
            "fit shrinks oversized geometry to the selected reference screen");
        var inverted = new InterfaceSkin.PanelRect(1, 1, 0, 0, 40, 40, -40, -40);
        Check(LayoutWorkbench.Measure(LayoutWorkbench.FitInside(inverted, 1280, 720), 1280, 720) == new LayoutWorkbench.Bounds(1248, 696, 32, 24),
            "fit repairs inverted geometry with a usable minimum size");
        Check(LayoutWorkbench.FitInside(fitted, 1280, 720) == fitted, "fit is idempotent");
        foreach (var invalid in new[] { "{\"layout\":[]}", "{\"layout\":{\"x\":null}}", "{\"layout\":{\"x\":{\"anchors\":[0,0,2,1]}}}",
            "{\"layout\":{\"x\":{\"offsets\":[0,0,1]}}}", "{\"layout\":{\"x\":{\"offsets\":[0,0,1e90,1]}}}" })
        {
            var rejected = false;
            try { LayoutWorkbench.Read(invalid, inherited); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "malformed visual layout is rejected without adoption");
        }
        var runtimeBefore = InterfaceSkin.Panels.ToArray();
        var errors = new System.Collections.Generic.List<string>();
        InterfaceSkin.Merge(written, "visual-editor-test", errors);
        Check(errors.Count == 0 && InterfaceSkin.Layout("hud.side_panel") == moved, "actual game skin consumes the editor's output");
        InterfaceSkin.ResetCatalog();
        Check(InterfaceSkin.Panels.SequenceEqual(runtimeBefore), "layout validation leaves no test skin behind");
        var selectionPanels = InterfaceSkin.Panels.ToArray();
        var selectionDraft = LayoutWorkbench.Read("{}", selectionPanels);
        var countryCard = selectionDraft["hud.country_card"];
        var regionCard = selectionDraft["hud.region_card"];
        Check(LayoutWorkbench.Measure(countryCard, 1920, 1080).Height > 0, "selected-country card has editable nonzero height");
        var selectionJson = LayoutWorkbench.SetRect("{}", "hud.country_card", LayoutWorkbench.Move(countryCard, 16, 8));
        var movedRegion = LayoutWorkbench.Move(regionCard, -24, -16);
        selectionJson = LayoutWorkbench.SetRect(selectionJson, "hud.region_card", movedRegion);
        var selectionRoundtrip = LayoutWorkbench.Read(selectionJson, selectionPanels);
        Check(selectionRoundtrip["hud.country_card"] == LayoutWorkbench.Move(countryCard, 16, 8) &&
            selectionRoundtrip["hud.region_card"] == movedRegion && movedRegion.AnchorBottom == 1,
            "country and combined region-province cards roundtrip independently");
    }
}
