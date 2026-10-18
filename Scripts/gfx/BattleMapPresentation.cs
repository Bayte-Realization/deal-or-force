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
    private int _conditionPalette;

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
        _battleSymbol ??= GD.Load<Texture2D>(SpriteCatalog.TextureOf("icon_battle", "res://game/gfx/interface/icons/icon_battle.png"));
        var font = PresentationFonts.For("ui.default");
        var radius = BattleMarkerRadius();
        foreach (var (id, marker) in _battleMarkers)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.IsVisibleInTree() ||
                _camera.IsPositionBehind(marker.Node.GlobalPosition) || !_battleReadouts.TryGetValue(id, out var state)) continue;
            var center = BattleMarkerScreen(marker.Node);
            if (!new Rect2(Vector2.Zero, Size).Grow(radius).HasPoint(center)) continue;
            var ink = Ui.Colour(state.Tone);
            DrawCircle(center, radius + 1, Ui.Colour("combat.badge_background"));
            DrawArc(center, radius - 1, -Mathf.Pi * .5f, Mathf.Pi * 1.5f, 48, Ui.Colour("surface.line"), 2, true);
            if (state.Text != "?") DrawArc(center, radius - 1, -Mathf.Pi * .5f,
                -Mathf.Pi * .5f + Mathf.Tau * state.Share, 48, ink, 2, true);
            if (_battleSymbol is not null) DrawTextureRect(_battleSymbol,
                new Rect2(center + new Vector2(-7, -13), new Vector2(14, 14)), false, ink);
            var text = state.Text == "?" ? "?" : state.Text + "%";
            const int pixels = 11;
            var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, pixels).X;
            DrawString(font, center + new Vector2(-width * .5f, 11), text,
                HorizontalAlignment.Left, -1, pixels, Ui.Colour("text.bright"));
        }
    }

    private void UpdateArmyConditionBars(ArmyStackMarker marker)
    {
        if (marker.ConditionBars is null) return;
        var visible = marker.Plate.Visible && !(_flatMap && _flatStrategicCounters) &&
            CampaignIntel.PlayerCanInspect(_campaign, marker.NationId);
        marker.ConditionBars.Visible = visible;
        if (!visible) { marker.ConditionKey = -1; return; }
        var palette = HashCode.Combine(InterfaceSkin.Colour("combat.organisation"),
            InterfaceSkin.Colour("combat.equipment"), InterfaceSkin.Colour("combat.bar_background"));
        if (palette != _conditionPalette)
        {
            _conditionPalette = palette;
            _conditionTextures.Clear();
        }
        var condition = BattleReadout.Summarise(marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack);
        var key = ((int)Math.Round(condition.Organisation * 20), (int)Math.Round(condition.Equipment * 20));
        marker.ConditionKey = key.Item1 * 100 + key.Item2;
        if (!_conditionTextures.TryGetValue(key, out var texture))
        {
            // Same canvas and origin as the plate. Offsetting a second billboard in
            // world space made its bars peel sideways when the globe camera rotated.
            const int width = 176;
            const int height = 104;
            var pixels = new byte[width * height * 4];
            for (var y = 76; y < 100; y++)
            for (var x = 11; x < 165; x++)
            {
                var stripY = y - 76;
                var first = stripY < 11;
                var fill = first ? key.Item1 : key.Item2;
                var color = stripY is 11 or 12 || x - 11 >= 154 * fill / 20f
                    ? Ui.Colour("combat.bar_background") : Ui.Colour(first ? "combat.organisation" : "combat.equipment");
                WritePixel(pixels, width, x, y, color);
            }
            texture = ImageTexture.CreateFromImage(Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels));
            _conditionTextures[key] = texture;
        }
        marker.ConditionBars.Texture = texture;
        marker.Node.SetMeta("organisation", condition.Organisation);
        marker.Node.SetMeta("equipment", condition.Equipment);
    }
}
