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

    // Badges are owned depth-tested Sprite3D surface quads now. No viewport overlay
    // may paint the old billboard on top, or it would still show through the globe.
    private void DrawBattleReadouts() { }

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
