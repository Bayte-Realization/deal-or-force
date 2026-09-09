using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private Node3D _battleRoot = null!;
    private readonly Dictionary<int, (Node3D Node, Vector3 Normal)> _battleMarkers = new();

    private void CreateMilitaryOverlays(Node3D world)
    {
        _battleRoot = new Node3D { Name = "BattleMarkers" };
        world.AddChild(_battleRoot);
    }

    private Texture2D ArmyCounterTexture(Nation nation, string templateId)
    {
        var key = (nation.MapIndex, templateId ?? "line_infantry");
        if (_armyCounterTextures.TryGetValue(key, out var cached)) return cached;
        const int width = 176;
        const int height = 104;
        var pixels = new byte[width * height * 4];
        var national = new Color(nation.Color);
        var border = national.Lightened(0.20f);
        var dark = new Color(0.055f, 0.075f, 0.085f, 0.96f);
        var middle = new Color(0.11f, 0.14f, 0.15f, 0.96f);

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var edge = x < 5 || x >= width - 5 || y < 5 || y >= height - 5;
            var commandBand = y < 18;
            var nationalStripe = x >= 7 && x < 15 && y >= 7 && y < height - 7;
            var weave = ((x / 7 + y / 5 + nation.MapIndex) & 3) == 0;
            var color = edge ? border : commandBand ? national.Darkened(0.28f) : weave ? middle : dark;
            if (nationalStripe) color = national.Lightened(0.08f);
            WritePixel(pixels, width, x, y, color);
        }

        // NATO-inspired symbols share one frame language while the unit type remains
        // readable without opening a panel.
        var symbol = new Color(0.72f, 0.78f, 0.78f, 0.66f);
        var counterSymbol = UnitTypeCatalog.Get(templateId).CounterSymbol;
        if (counterSymbol == "armor")
        {
            for (var y = 36; y <= 75; y++)
            for (var x = 43; x <= 132; x++)
            {
                var nx = (x - 87.5f) / 44.5f;
                var ny = (y - 55.5f) / 19.5f;
                var shell = nx * nx + ny * ny;
                if (shell is >= 0.67f and <= 1.0f) WritePixel(pixels, width, 23 + (x-43)/2, y, symbol);
            }
        }
        else
        {
            for (var step = 0; step < 44; step++)
            for (var thickness = -2; thickness <= 2; thickness++)
            {
                WritePixel(pixels, width, 23 + step, 32 + step + thickness, symbol);
                WritePixel(pixels, width, 66 - step, 32 + step + thickness, symbol);
            }
            if (counterSymbol == "motorized")
                for (var angle = 0; angle < 360; angle++)
                {
                    var radians = Mathf.DegToRad(angle);
                    WritePixel(pixels, width, 45 + Mathf.RoundToInt(Mathf.Cos(radians) * 20f),
                        56 + Mathf.RoundToInt(Mathf.Sin(radians) * 20f), symbol);
                }
        }

        var texture = ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels));
        _armyCounterTextures[key] = texture;
        return texture;
    }

    private static void WritePixel(byte[] pixels, int width, int x, int y, Color color)
    {
        if (x < 0 || y < 0 || x >= width || y * width * 4 >= pixels.Length) return;
        var write = (y * width + x) * 4;
        pixels[write] = (byte)Mathf.RoundToInt(color.R * 255f);
        pixels[write + 1] = (byte)Mathf.RoundToInt(color.G * 255f);
        pixels[write + 2] = (byte)Mathf.RoundToInt(color.B * 255f);
        pixels[write + 3] = (byte)Mathf.RoundToInt(color.A * 255f);
    }

    private void UpdateMilitaryPresentation()
    {
        if (_campaign is null) return;
        foreach (var cachedMarker in _armyStacks.Values)
        {
            var marker = cachedMarker.Node;
            var stack = cachedMarker.Stack;
            if (!IsInstanceValid(marker)) continue;
            if (stack.Count == 0) continue;
            // One pass instead of seven. This runs for every counter on the map every
            // frame, and each of those Any and Average calls was a delegate and an
            // enumerator - some seven thousand of them a frame, for arithmetic over a
            // handful of divisions.
            var anySelected = false;
            var anyBattle = false;
            var anyMoving = false;
            var cutOff = false;
            var organisationSum = 0f;
            var planningSum = 0f;
            var supplySum = 0f;
            foreach (var division in stack)
            {
                if (_selectedArmyIds.Contains(division.Id)) anySelected = true;
                if (division.InBattle) anyBattle = true;
                if (division.IsMoving) anyMoving = true;
                if (division.CutOff) cutOff = true;
                organisationSum += division.Organisation;
                planningSum += division.Planning;
                supplySum += division.SupplyLevel;
            }
            var organisation = organisationSum / stack.Count;
            var planning = planningSum / stack.Count;
            var supply = supplySum / stack.Count;
            var plate = cachedMarker.Plate;
            var count = cachedMarker.Count;
            if (plate is not null)
            {
                var tint = anySelected
                    ? new Color(1f, 0.86f, 0.42f, 1f)
                    : anyBattle
                        ? new Color(1f, 0.58f, 0.48f, 1f)
                        : anyMoving ? new Color(0.76f, 0.92f, 1f, 1f) : Colors.White;
                if (plate.Modulate != tint) plate.Modulate = tint;
            }
            if (count is not null)
            {
                // The text belongs to the reconcile pass, which knows the real division
                // and personnel totals; this pass owns only the colour. Both used to write
                // the text, with different numbers, and whichever ran last won.
                //
                // The condition now colours the whole counter rather than the small lower
                // line, so a formation out of supply reads at a glance instead of on
                // inspection. Order, stance and planning remain in the selection panel.
                var tint = supply < 0.45f || cutOff
                    ? Ui.Colour("military.routed")
                    : organisation < 0.35f ? Ui.Colour("military.shaken")
                    : planning > 0.70f ? Ui.Colour("military.planning") : Ui.Colour("military.ready");
                if (count.Modulate != tint) count.Modulate = tint;
            }
        }
        UpdateBattleMarkers();
    }

    private int _battleMarkerDay = -1;
    private int _battleViewer = -1;
    private readonly HashSet<int> _activeBattleIds = new();
    private readonly List<int> _finishedBattleIds = new();
    private readonly Dictionary<int, Simulation.Army> _armyById = new();

    /// <summary>
    /// Which battles are on the map, and how they are going.
    ///
    /// All of this used to run every frame, and the worst of it resolved each participant
    /// by searching the whole army list: three hundred battles, a handful of divisions
    /// each, fourteen hundred divisions to search - millions of comparisons sixty times a
    /// second, to redraw numbers that only change when a day passes. Battles start, end and
    /// grind only inside a tick, so the work happens once a day; only the placement, which
    /// follows the camera, still happens every frame.
    /// </summary>
    private void UpdateBattleMarkers()
    {
        if (!IsInstanceValid(_battleRoot) || _campaign?.ProvinceMap is null) return;
        var viewer = FogOfWarActive && _campaign.Player is { IsHuman: true } ? _campaign.Player.Id : -1;
        if (_battleMarkerDay != _campaign.ElapsedDays || _battleViewer != viewer)
        {
            _battleViewer = viewer;
            _battleMarkerDay = _campaign.ElapsedDays;
            RebuildBattleMarkers();
        }
        foreach (var marker in _battleMarkers.Values)
            if (IsInstanceValid(marker.Node)) PositionBattleMarker(marker.Node, marker.Normal);
        FinishBattleMarkers();
    }

    private void RebuildBattleMarkers()
    {
        _activeBattleIds.Clear();
        foreach (var battle in _campaign.Battles)
            if (!battle.Ended && CanShowBattle(battle)) _activeBattleIds.Add(battle.Id);

        _finishedBattleIds.Clear();
        foreach (var id in _battleMarkers.Keys)
            if (!_activeBattleIds.Contains(id)) _finishedBattleIds.Add(id);
        foreach (var old in _finishedBattleIds)
        {
            if (IsInstanceValid(_battleMarkers[old].Node)) _battleMarkers[old].Node.QueueFree();
            _battleMarkers.Remove(old);
        }

        _armyById.Clear();
        foreach (var army in _campaign.Armies) _armyById[army.Id] = army;

        foreach (var battle in _campaign.Battles)
        {
            if (!_activeBattleIds.Contains(battle.Id)) continue;
            if (!_battleMarkers.TryGetValue(battle.Id, out var marker))
            {
                var center = _campaign.ProvinceMap.CenterOf(battle.ProvinceId);
                var normal = SurfacePoint(center.Latitude, center.Longitude, 1f).Normalized();
                var node = new Node3D { Name = $"Battle_{battle.Id}" };
                _battleRoot.AddChild(node);
                var icon = new Label3D
                {
                    Name = "BattleIcon", Text = "⚔", FontSize = 46, PixelSize = 0.00030f,
                    FixedSize = true, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    OutlineSize = 10, Modulate = Ui.Colour("military.battle"),
                    OutlineModulate = Ui.Colour("military.battle_outline"), NoDepthTest = true
                };
                node.AddChild(icon);
                var balance = new Label3D
                {
                    Name = "BattleBalance", FontSize = 16, PixelSize = 0.00022f,
                    FixedSize = true, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    OutlineSize = 6, Modulate = Ui.Colour("military.clash"),
                    OutlineModulate = Ui.Colour("military.clash_outline"), NoDepthTest = true,
                    Position = new Vector3(0f, -0.025f, 0.01f)
                };
                node.AddChild(balance);
                marker = (node, normal);
                _battleMarkers[battle.Id] = marker;
            }

            var label = marker.Node.GetNodeOrNull<Label3D>("BattleBalance");
            if (label is not null)
                label.Text = !FogOfWarActive || CanShowMilitary(battle.AttackerId) || CanShowMilitary(battle.DefenderId)
                    ? $"{SideOrganisation(battle.Attackers) * 100f:00} | {SideOrganisation(battle.Defenders) * 100f:00}"
                    : "? | ?";
        }
    }

    private float SideOrganisation(List<int> ids)
    {
        var sum = 0f;
        var count = 0;
        foreach (var id in ids)
        {
            if (!_armyById.TryGetValue(id, out var army)) continue;
            sum += army.Organisation;
            count++;
        }
        return count == 0 ? 0f : sum / count;
    }

    /// <summary>Which markers the camera can see. Cheap, and camera-dependent, so per frame.</summary>
    private void FinishBattleMarkers()
    {
        _battleRoot.Visible = ViewDistance <= 14f;
        if (!_flatMap && _battleRoot.Visible)
        {
            var view = _camera.GlobalPosition.Normalized();
            foreach (var marker in _battleMarkers.Values)
                if (IsInstanceValid(marker.Node)) marker.Node.Visible = marker.Normal.Dot(view) > 0.16f;
        }
        else
            foreach (var marker in _battleMarkers.Values)
                if (IsInstanceValid(marker.Node)) marker.Node.Visible = true;
    }

    private void PositionBattleMarker(Node3D node, Vector3 normal)
    {
        if (_flatMap)
        {
            var geo = GeographicFromNormal(normal);
            node.Position = FlatPoint(geo.Latitude, geo.Longitude, FlatTextZ);
        }
        else node.Position = normal * SurfaceTextRadius;
    }

    private void ReprojectBattleMarkers()
    {
        foreach (var marker in _battleMarkers.Values)
            if (IsInstanceValid(marker.Node)) PositionBattleMarker(marker.Node, marker.Normal);
    }

    private bool TryPickBattle(Vector2 pixel, out LandBattle battle)
    {
        battle = null;
        if (!IsInstanceValid(_camera) || ViewDistance > 14f) return false;
        var best = 34f * 34f;
        foreach (var (battleId, marker) in _battleMarkers)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.Visible || _camera.IsPositionBehind(marker.Node.GlobalPosition)) continue;
            var distance = _camera.UnprojectPosition(marker.Node.GlobalPosition).DistanceSquaredTo(pixel);
            if (distance >= best) continue;
            var candidate = _campaign?.Battles.FirstOrDefault(item => item.Id == battleId && !item.Ended);
            if (candidate is null) continue;
            best = distance;
            battle = candidate;
        }
        return battle is not null;
    }
}
