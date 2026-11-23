using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private const int ConditionTextureCap = 256;
    private const int ConditionCanvasWidth = 128;
    private const int ConditionCanvasHeight = 64;
    private readonly Dictionary<int, (string Text, float Share, string Tone)> _battleReadouts = new();
    private readonly Dictionary<(int Org, int Str, byte Flags, byte Stance), (Texture2D Texture, LinkedListNode<(int Org, int Str, byte Flags, byte Stance)> Node)> _conditionTextures = new();
    private readonly LinkedList<(int Org, int Str, byte Flags, byte Stance)> _conditionLru = new();
    private Texture2D _battleSymbol;
    private string _battleSymbolPath;
    private int _conditionPalette;

    private const byte ArmyFlagInBattle = 1 << 0;
    private const byte ArmyFlagEncircled = 1 << 1;
    private const byte ArmyFlagLowSupply = 1 << 2;
    private const byte ArmyFlagEntrenched = 1 << 3;
    private const byte ArmyFlagMoving = 1 << 4;

    private const byte StanceNeutral = 0;
    private const byte StancePlayer = 1;
    private const byte StanceAlly = 2;
    private const byte StanceEnemy = 3;

    public override void _EnterTree()
    {
        InvalidateBattleSymbol();
        SpriteCatalog.Changed += InvalidateBattleSymbol;
    }

    public override void _ExitTree()
    {
        DisposeConditionTextures();
        ClearArmyMarkerPool();
        SpriteCatalog.Changed -= InvalidateBattleSymbol;
        base._ExitTree();
    }

    private void DisposeConditionTextures()
    {
        foreach (var entry in _conditionTextures.Values)
            entry.Texture?.Dispose();
        _conditionTextures.Clear();
        _conditionLru.Clear();
    }

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

    private float BattleMarkerRadius() => Math.Clamp(InterfaceSkin.Metric("combat.map_marker_pixels", 40), 30, 52) * .5f;

    private string BattleMapTooltip(LandBattle battle)
    {
        var snap = BattleTelemetry.Build(_campaign, battle);
        var assessment = BattleReadout.Assess(battle, _campaign.Defines.BattleBreakOrganisation);
        var attacks = _campaign.Player is null || battle.DefenderId != _campaign.Player.Id;
        var progress = attacks ? snap.AttackerProgress : 1f - snap.AttackerProgress;
        var phaseLine = string.IsNullOrWhiteSpace(snap.PhaseLabel) ? "" : $"\n{snap.PhaseLabel}";
        var flankLine = snap.AttackDirections > 1 ? $" · {snap.AttackDirections}x" : "";
        var pocketLine = snap.HasPocketCollapse || snap.HasEncircledDefenders ? " · !" : "";
        return Loc.Get(assessment.StatusFor(attacks)) + phaseLine + flankLine + pocketLine +
            (assessment.Known ? "\n" + Loc.Format("Endurance balance: {0:P0}", progress) : "") +
            "\n" + Loc.Get("Balance is not victory probability. Click to open the detailed battle report.");
    }

    // Badges are owned depth-tested Sprite3D surface quads now. No viewport overlay
    // may paint the old billboard on top, or it would still show through the globe.
    private void DrawBattleReadouts() { }

    private byte CounterStanceFor(int nationId)
    {
        var player = _campaign?.Player;
        if (player is null || !player.IsHuman || nationId < 0) return StanceNeutral;
        if (nationId == player.Id) return StancePlayer;
        if (CampaignDiplomacy.AtWar(_campaign, player.Id, nationId)) return StanceEnemy;
        var nation = _campaign.NationById(nationId);
        if (nation is not null && (nation.AnswersTo(player.Id) || player.AnswersTo(nationId)))
            return StanceAlly;
        if (CampaignDiplomacy.Peek(_campaign, player.Id, nationId) is { Stance: DiplomaticStance.Allied })
            return StanceAlly;
        return StanceNeutral;
    }

    private void UpdateArmyConditionBars(ArmyStackMarker marker)
    {
        if (marker.ConditionBars is null) return;
        var stack = marker.DisplayStack.Count > 0 ? marker.DisplayStack : marker.Stack;
        var condition = MilitaryConditionReadout.ForPlayer(_campaign, stack, !FogOfWarActive);
        var visible = marker.Plate.Visible && !(_flatMap && _flatStrategicCounters) && condition.Known;
        marker.ConditionBars.Visible = visible;
        if (!visible) { marker.ConditionKey = -1; return; }

        var palette = HashCode.Combine(
            InterfaceSkin.Colour("combat.organisation"),
            InterfaceSkin.Colour("combat.equipment"),
            InterfaceSkin.Colour("combat.bar_background"),
            InterfaceSkin.Colour("state.positive"),
            InterfaceSkin.Colour("state.warning"),
            InterfaceSkin.Colour("state.negative"));
        if (palette != _conditionPalette)
        {
            _conditionPalette = palette;
            DisposeConditionTextures();
        }

        // Single allocation-free scan of visible divisions in the stack for Strength + tactical state pips.
        var player = _campaign.Player;
        var viewerId = player?.Id ?? -1;
        var omniscient = !FogOfWarActive || player is null || !player.IsHuman;
        var defines = _campaign.Defines;
        byte flags = 0;
        var divWeight = 0f;
        var strSum = 0f;
        var entrenchSum = 0f;
        for (var i = 0; i < stack.Count; i++)
        {
            var army = stack[i];
            if (!MilitaryConditionReadout.CanRead(_campaign, viewerId, army, omniscient)) continue;
            var w = MathF.Max(0.1f, army.Divisions);
            divWeight += w;
            strSum += BattleReadout.Unit(army.Strength) * w;
            entrenchSum += Math.Clamp(army.Entrenchment, 0f, 1f) * w;
            if (army.InBattle) flags |= ArmyFlagInBattle;
            if (army.CutOff || BattleTelemetry.IsInPocketCollapse(defines, army)) flags |= ArmyFlagEncircled;
            else if (army.SupplyLevel < 0.72f) flags |= ArmyFlagLowSupply;
            if (army.IsMoving || army.Route.Count > 0) flags |= ArmyFlagMoving;
        }
        if ((flags & ArmyFlagEncircled) != 0) flags &= unchecked((byte)~ArmyFlagLowSupply);
        var avgStrength = divWeight > 0f ? MathF.Min(strSum / divWeight, condition.Equipment) : condition.Equipment;
        if (divWeight > 0f && entrenchSum / divWeight >= 0.15f) flags |= ArmyFlagEntrenched;
        var stance = CounterStanceFor(marker.NationId);

        // Quantized 0..10 bins + 128x64 (32 KiB) canvas + bounded LRU (<=256) eliminates the +822 MiB D3D12 VRAM leak.
        var orgBin = Math.Clamp((int)Math.Round(condition.Organisation * 10), 0, 10);
        var strBin = Math.Clamp((int)Math.Round(avgStrength * 10), 0, 10);
        var key = (orgBin, strBin, flags, stance);
        marker.ConditionKey = HashCode.Combine(orgBin, strBin, flags, stance);

        Texture2D texture;
        if (_conditionTextures.TryGetValue(key, out var cached))
        {
            texture = cached.Texture;
            if (cached.Node != _conditionLru.First)
            {
                _conditionLru.Remove(cached.Node);
                _conditionLru.AddFirst(cached.Node);
            }
        }
        else
        {
            texture = BuildArmyOverlayTexture(orgBin, strBin, flags, stance);
            var lruNode = _conditionLru.AddFirst(key);
            _conditionTextures[key] = (texture, lruNode);
            while (_conditionLru.Count > ConditionTextureCap && _conditionLru.Last is { } oldest)
            {
                _conditionLru.RemoveLast();
                if (_conditionTextures.Remove(oldest.Value, out var evicted))
                    evicted.Texture?.Dispose();
            }
        }
        if (marker.ConditionBars.Texture != texture) marker.ConditionBars.Texture = texture;
        // Scale 128x64 overlay proportionally onto the 176x104 counter plate footprint.
        var targetScale = new Vector3(176f / ConditionCanvasWidth, 104f / ConditionCanvasHeight, 1f);
        if (marker.ConditionBars.Scale != targetScale) marker.ConditionBars.Scale = targetScale;
        marker.Node.SetMeta("organisation", condition.Organisation);
        marker.Node.SetMeta("equipment", condition.Equipment);
    }

    private static Texture2D BuildArmyOverlayTexture(int orgBin, int strBin, byte flags, byte stance)
    {
        // Compact 128x64 RGBA8 (32 KiB, strictly under D3D12 64 KB small-resource alignment),
        // scaled proportionally onto the 176x104 counter plate.
        const int width = ConditionCanvasWidth;
        const int height = ConditionCanvasHeight;
        var pixels = new byte[width * height * 4];

        var bg = Ui.Colour("combat.bar_background").Darkened(0.30f);
        var frame = new Color(0.03f, 0.04f, 0.05f, 0.96f);
        var posCol = Ui.Colour("state.positive");
        var warnCol = Ui.Colour("state.warning");
        var negCol = Ui.Colour("state.negative");
        var orgBase = orgBin <= 2
            ? negCol.Lightened(0.08f)
            : orgBin <= 5
                ? warnCol.Lerp(Ui.Colour("combat.organisation"), 0.40f)
                : Ui.Colour("combat.organisation").Lerp(posCol, 0.35f);
        var strBase = strBin <= 3
            ? negCol.Lerp(warnCol, 0.25f)
            : Ui.Colour("combat.equipment");

        // 1. Diplomatic stance accent trim along bottom & outer side corners (keeps national colour intact).
        if (stance != StanceNeutral)
        {
            var stanceCol = stance switch
            {
                StancePlayer => posCol.Lerp(new Color(0.96f, 0.84f, 0.36f), 0.45f),
                StanceAlly => Ui.Colour("state.info").Lightened(0.18f),
                _ => negCol.Lightened(0.14f)
            };
            for (var x = 6; x < width - 6; x++)
            {
                WritePixel(pixels, width, x, height - 3, stanceCol);
                WritePixel(pixels, width, x, height - 2, stanceCol.Darkened(0.18f));
            }
            for (var y = 4; y < 14; y++)
            {
                WritePixel(pixels, width, 2, y, stanceCol);
                WritePixel(pixels, width, 3, y, stanceCol.Darkened(0.15f));
                WritePixel(pixels, width, width - 3, y, stanceCol);
                WritePixel(pixels, width, width - 4, y, stanceCol.Darkened(0.15f));
            }
        }

        // 2. Active Combat vivid crimson-amber glow frame around the counter.
        if ((flags & ArmyFlagInBattle) != 0)
        {
            var battleOuter = new Color(1.0f, 0.22f, 0.12f, 0.96f);
            var battleInner = new Color(1.0f, 0.62f, 0.22f, 0.86f);
            for (var x = 3; x < width - 3; x++)
            {
                WritePixel(pixels, width, x, 0, battleOuter);
                WritePixel(pixels, width, x, 1, battleInner);
                WritePixel(pixels, width, x, height - 1, battleOuter);
                WritePixel(pixels, width, x, height - 2, battleInner);
            }
            for (var y = 1; y < height - 1; y++)
            {
                WritePixel(pixels, width, 0, y, battleOuter);
                WritePixel(pixels, width, 1, y, battleInner);
                WritePixel(pixels, width, width - 1, y, battleOuter);
                WritePixel(pixels, width, width - 2, y, battleInner);
            }
        }

        // 3. Dual micro-bars at bottom tray: Top = Organization/Morale, Bottom = Strength/Equipment.
        for (var y = 47; y < 61; y++)
        for (var x = 7; x < width - 7; x++)
        {
            var isBorder = x == 7 || x == width - 8 || y == 47 || y == 60 || y == 53 || y == 54;
            if (isBorder)
            {
                WritePixel(pixels, width, x, y, frame);
                continue;
            }
            var first = y < 53;
            var localY = first ? y - 48 : y - 55;
            var fill = first ? orgBin : strBin;
            var filled = x - 8 < 112 * fill / 10f;
            var isTick = (x - 8) > 0 && (x - 8) % 11 == 0;
            Color color;
            if (!filled)
            {
                color = isTick ? bg.Darkened(0.24f) : bg;
            }
            else
            {
                var barCol = first ? orgBase : strBase;
                if (localY == 0) color = barCol.Lightened(0.28f);
                else if (localY >= 4) color = barCol.Darkened(0.22f);
                else color = barCol;
                if (isTick) color = color.Darkened(0.24f);
            }
            WritePixel(pixels, width, x, y, color);
        }

        // 4. Tactical status pips in the top header strip (x = 45..92, y = 2..14 — clear of flag, count & echelon).
        if ((flags & ArmyFlagEncircled) != 0)
            DrawStatusPipDisc(pixels, width, 52, 8, 5, negCol.Lightened(0.12f), new Color(1f, 0.96f, 0.92f), isExclamation: true);
        else if ((flags & ArmyFlagLowSupply) != 0)
            DrawStatusPipTriangle(pixels, width, 52, 8, 5, warnCol.Lightened(0.10f), new Color(0.08f, 0.06f, 0.04f));

        if ((flags & ArmyFlagEntrenched) != 0)
            DrawStatusPipShield(pixels, width, 67, 8, Ui.Colour("state.info").Lightened(0.22f), new Color(0.95f, 0.98f, 1.0f));

        if ((flags & ArmyFlagMoving) != 0)
            DrawStatusPipChevron(pixels, width, 82, 8, posCol.Lightened(0.22f));

        using var img = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(img);
    }

    private static void DrawStatusPipDisc(byte[] pixels, int w, int cx, int cy, int r, Color fill, Color mark, bool isExclamation)
    {
        var dark = new Color(0.04f, 0.04f, 0.05f, 0.98f);
        for (var y = cy - r - 1; y <= cy + r + 1; y++)
        for (var x = cx - r - 1; x <= cx + r + 1; x++)
        {
            var dx = x - cx;
            var dy = y - cy;
            var d = MathF.Sqrt(dx * dx + dy * dy);
            if (d > r + 1.0f) continue;
            WritePixel(pixels, w, x, y, d > r - 1.2f ? dark : fill);
        }
        if (isExclamation)
        {
            for (var y = cy - 4; y <= cy + 1; y++)
            {
                WritePixel(pixels, w, cx, y, mark);
                WritePixel(pixels, w, cx + 1, y, mark);
            }
            WritePixel(pixels, w, cx, cy + 4, mark);
            WritePixel(pixels, w, cx + 1, cy + 4, mark);
        }
    }

    private static void DrawStatusPipTriangle(byte[] pixels, int w, int cx, int cy, int r, Color fill, Color mark)
    {
        var dark = new Color(0.04f, 0.04f, 0.05f, 0.98f);
        var top = cy - r;
        var bot = cy + r - 1;
        for (var y = top; y <= bot; y++)
        {
            var t = (y - top) / MathF.Max(1f, bot - top);
            var half = (int)MathF.Round(t * r);
            for (var x = cx - half - 1; x <= cx + half + 1; x++)
            {
                var edge = y == top || y == bot || x <= cx - half || x >= cx + half;
                WritePixel(pixels, w, x, y, edge ? dark : fill);
            }
        }
        for (var y = cy - 3; y <= cy + 1; y++) WritePixel(pixels, w, cx, y, mark);
        WritePixel(pixels, w, cx, cy + 4, mark);
    }

    private static void DrawStatusPipShield(byte[] pixels, int w, int cx, int cy, Color fill, Color rim)
    {
        var dark = new Color(0.04f, 0.05f, 0.07f, 0.98f);
        for (var dy = -7; dy <= 7; dy++)
        {
            var half = dy <= 1 ? 6 : Math.Max(0, 6 - (dy - 1));
            for (var dx = -half - 1; dx <= half + 1; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                var outer = dx == -half - 1 || dx == half + 1 || dy == -7 || dy == 7;
                var innerRim = !outer && (Math.Abs(dx) >= half - 1 || dy == -6);
                WritePixel(pixels, w, x, y, outer ? dark : innerRim ? rim : fill);
            }
        }
    }

    private static void DrawStatusPipChevron(byte[] pixels, int w, int cx, int cy, Color fill)
    {
        var dark = new Color(0.03f, 0.05f, 0.04f, 0.98f);
        for (var dy = -6; dy <= 6; dy++)
        {
            var tip = 4 - Math.Abs(dy);
            for (var dx = tip - 5; dx <= tip + 2; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                var isCore = dx >= tip - 3 && dx <= tip;
                WritePixel(pixels, w, x, y, isCore ? fill : dark);
            }
        }
    }
}
