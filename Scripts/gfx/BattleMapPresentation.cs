using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly Dictionary<int, (string Text, float Share, string Tone)> _battleReadouts = new();
    private readonly Dictionary<(int Organisation, int Equipment), Texture2D> _conditionTextures = new();
    private Texture2D _battleSymbol;
    private string _battleSymbolPath;
    private int _conditionPalette;

    public override void _EnterTree()
    {
        InvalidateBattleSymbol();
        SpriteCatalog.Changed += InvalidateBattleSymbol;
    }

    public override void _ExitTree() => SpriteCatalog.Changed -= InvalidateBattleSymbol;

    private void InvalidateBattleSymbol()
    {
        _battleSymbolPath = null;
        _battleSymbol = null;
    }

    // Remember a failed load as well as a successful one. A missing import must not
    // retry on every frame; a catalogue reload or changed mod path permits a retry.
    private Texture2D BattleSymbolFor(string path, Func<string, Texture2D> loader = null)
    {
        if (_battleSymbolPath == path) return _battleSymbol;
        _battleSymbolPath = path;
        _battleSymbol = loader is not null ? loader(path) :
            !string.IsNullOrEmpty(path) && ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        return _battleSymbol;
    }

    private float BattleMarkerRadius() => Math.Clamp(InterfaceSkin.Metric("combat.map_marker_pixels", 36), 28, 48) * .5f;
    private Vector2 BattleMarkerScreen(Node3D anchor) => _camera.UnprojectPosition(anchor.GlobalPosition);

    private string BattleMapTooltip(LandBattle battle)
    {
        var assessment = BattleReadout.Assess(battle, _campaign.Defines.BattleBreakOrganisation);
        var attacks = battle.DefenderId != _campaign.Player.Id;
        return Loc.Get(assessment.StatusFor(attacks)) +
            (assessment.Known ? "\n" + Loc.Format("Endurance balance: {0:P0}", assessment.ShareFor(attacks)) : "") +
            "\n" + Loc.Get("Balance is not victory probability. Click to open the detailed battle report.");
    }

    // Drawn in viewport pixels in BOTH projections. Neither distance compensation nor
    // an offset between fixed-size 3D labels can inflate or detach the badge's number.
    private void DrawBattleReadouts()
    {
        if (!MilitaryMarkersEnabled || !IsInstanceValid(_battleRoot) || !_battleRoot.IsVisibleInTree()) return;
        var font = PresentationFonts.For("ui.default");
        var radius = BattleMarkerRadius();
        var ticks = Time.GetTicksMsec();
        var pulse = 0.5f + 0.5f * MathF.Sin(ticks * 0.0055f);
        foreach (var (id, marker) in _battleMarkers)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.IsVisibleInTree() ||
                _camera.IsPositionBehind(marker.Node.GlobalPosition) || !_battleReadouts.TryGetValue(id, out var state)) continue;
            var center = BattleMarkerScreen(marker.Node);
            if (!new Rect2(Vector2.Zero, Size).Grow(radius + 14f).HasPoint(center)) continue;
            var ink = Ui.Colour(state.Tone);
            var bg = Ui.Colour("combat.badge_background");
            var brass = Ui.Colour("button.primary_border", 0.78f);

            // 1. Directional assault vector & artillery muzzle flash if an attacking province is known
            if (marker.Node.HasMeta("attacking_province_id"))
            {
                var atkProv = marker.Node.GetMeta("attacking_province_id").AsInt32();
                if (atkProv > 0 && _campaign?.ProvinceMap is { } map && atkProv <= map.ProvinceCount)
                {
                    var atkGeo = map.CenterOf(atkProv);
                    var atkWorld = _flatMap ? FlatPoint(atkGeo.Latitude, atkGeo.Longitude, 0f) : SurfacePoint(atkGeo.Latitude, atkGeo.Longitude, GlobeRadius);
                    if (!_camera.IsPositionBehind(atkWorld))
                    {
                        var atkScreen = _camera.UnprojectPosition(atkWorld);
                        var toBattle = center - atkScreen;
                        var dist = toBattle.Length();
                        if (dist > radius + 6f && dist < 240f)
                        {
                            var dir = toBattle / dist;
                            var perp = new Vector2(-dir.Y, dir.X);
                            var tip = center - dir * (radius + 3.5f);
                            var tail = tip - dir * MathF.Min(16f, (dist - radius) * 0.55f);
                            DrawLine(tail, tip, new Color(0f, 0f, 0f, 0.65f), 4.2f, true);
                            DrawLine(tail, tip, ink.Lightened(0.15f), 2.4f, true);
                            DrawColoredPolygon(new[]
                            {
                                tip + dir * 2.5f,
                                tip - dir * 6.5f + perp * 4.8f,
                                tip - dir * 6.5f - perp * 4.8f
                            }, ink.Lightened(0.22f));
                        }
                    }
                }
            }

            // Deterministic artillery muzzle flash bursts around the engagement perimeter
            var flashPhase = (ticks * 0.0042f + id * 1.37f) % 1f;
            if (flashPhase < 0.35f)
            {
                var flashAngle = (id * 2.39996f) + ((int)(ticks / 240) % 5) * 1.256f;
                var flashPos = center + new Vector2(MathF.Cos(flashAngle), MathF.Sin(flashAngle)) * (radius + 2.5f);
                var flashAlpha = (1f - flashPhase / 0.35f) * 0.72f;
                DrawCircle(flashPos, 3.5f + flashPhase * 4f, new Color(1f, 0.76f, 0.28f, flashAlpha));
            }

            // 2. Subtle pulsing combat shockwave halo & grounding shadow
            DrawCircle(center + new Vector2(0, 2f), radius + 4f, new Color(0f, 0f, 0f, 0.52f));
            DrawArc(center, radius + 3.5f + pulse * 2.5f, 0f, Mathf.Tau, 48,
                new Color(ink.R, ink.G, ink.B, (1f - pulse) * 0.42f), 1.5f, true);

            // 2. Armored obsidian medallion body + inner bevel
            DrawCircle(center, radius + 2f, bg.Darkened(0.28f));
            DrawCircle(center, radius - 0.5f, bg);
            DrawArc(center, radius + 1.5f, 0f, Mathf.Tau, 48, brass, 1.2f, true);

            // 3. Two-sided endurance balance ring (our share in `ink`, opposing share in muted crimson/slate)
            DrawArc(center, radius - 1.5f, -Mathf.Pi * .5f, Mathf.Pi * 1.5f, 48,
                Ui.Colour("state.negative", 0.45f), 3.0f, true);
            if (state.Text != "?")
            {
                var endAngle = -Mathf.Pi * .5f + Mathf.Tau * Mathf.Clamp(state.Share, 0f, 1f);
                DrawArc(center, radius - 1.5f, -Mathf.Pi * .5f, endAngle, 48, ink, 3.2f, true);
            }

            // 4. Crossed-swords battle emblem in upper crown
            var symbol = BattleSymbolFor(SpriteCatalog.TextureOf("icon_battle", "res://game/gfx/interface/icons/icon_battle.png"));
            if (symbol is not null)
                DrawTextureRect(symbol, new Rect2(center + new Vector2(-8, -14), new Vector2(16, 16)), false,
                    ink.Lightened(0.18f));
            else
            {
                DrawLine(center + new Vector2(-5,-11), center + new Vector2(5,-1), ink, 1.5f, true);
                DrawLine(center + new Vector2(5,-11), center + new Vector2(-5,-1), ink, 1.5f, true);
            }

            // 5. Recessed lower readout pill for the percentage
            var text = state.Text == "?" ? "?" : state.Text + "%";
            const int pixels = 11;
            var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, pixels).X;
            var pillRect = new Rect2(center.X - width * 0.5f - 5f, center.Y + 1f, width + 10f, 14f);
            DrawRect(pillRect, bg.Darkened(0.35f), true);
            DrawRect(pillRect, brass.Darkened(0.2f), false, 1f);
            DrawString(font, center + new Vector2(-width * .5f, 12), text,
                HorizontalAlignment.Left, -1, pixels, Ui.Colour("text.bright"));
        }
    }

    private void UpdateArmyConditionBars(ArmyStackMarker marker)
    {
        if (marker.ConditionBars is null) return;
        var condition = MilitaryConditionReadout.ForPlayer(_campaign,
            marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack, !FogOfWarActive);
        var visible = marker.Plate.Visible && !(_flatMap && _flatStrategicCounters) && condition.Known;
        marker.ConditionBars.Visible = visible;
        if (!visible) { marker.ConditionKey = -1; return; }
        var palette = HashCode.Combine(InterfaceSkin.Colour("combat.organisation"),
            InterfaceSkin.Colour("combat.equipment"), InterfaceSkin.Colour("combat.bar_background"));
        if (palette != _conditionPalette)
        {
            _conditionPalette = palette;
            _conditionTextures.Clear();
        }
        var key = ((int)Math.Round(condition.Organisation * 20), (int)Math.Round(condition.Equipment * 20));
        marker.ConditionKey = key.Item1 * 100 + key.Item2;
        if (!_conditionTextures.TryGetValue(key, out var texture))
        {
            // Same canvas and origin as the plate. Offsetting a second billboard in
            // world space made its bars peel sideways when the globe camera rotated.
            const int width = 176;
            const int height = 104;
            var pixels = new byte[width * height * 4];
            var bg = Ui.Colour("combat.bar_background").Darkened(0.25f);
            var frame = new Color(0.03f, 0.04f, 0.05f, 0.96f);
            var orgBase = Ui.Colour("combat.organisation");
            var eqBase = Ui.Colour("combat.equipment");
            for (var y = 76; y < 98; y++)
            for (var x = 10; x < 166; x++)
            {
                var isBorder = x == 10 || x == 165 || y == 76 || y == 97 || y == 86 || y == 87;
                if (isBorder)
                {
                    WritePixel(pixels, width, x, y, frame);
                    continue;
                }
                var first = y < 86;
                var localY = first ? y - 77 : y - 88;
                var fill = first ? key.Item1 : key.Item2;
                var filled = x - 11 < 154 * fill / 20f;
                var isTick = (x - 11) > 0 && (x - 11) % 15 == 0;
                Color color;
                if (!filled)
                {
                    color = isTick ? bg.Darkened(0.22f) : bg;
                }
                else
                {
                    var barCol = first ? orgBase : eqBase;
                    if (localY <= 1) color = barCol.Lightened(0.26f);
                    else if (localY >= 7) color = barCol.Darkened(0.20f);
                    else color = barCol;
                    if (isTick) color = color.Darkened(0.24f);
                }
                WritePixel(pixels, width, x, y, color);
            }
            texture = ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels));
            _conditionTextures[key] = texture;
        }
        if (marker.ConditionBars.Texture != texture) marker.ConditionBars.Texture = texture;
        marker.Node.SetMeta("organisation", condition.Organisation);
        marker.Node.SetMeta("equipment", condition.Equipment);
    }
}
