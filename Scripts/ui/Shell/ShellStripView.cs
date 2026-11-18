using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Bottom army/front woven linen-tag strip + Ctrl+Z Undo cell + active-front-preserving
/// division visibility policy for the Wave 2 campaign shell.
/// </summary>
internal sealed class ShellStripView
{
    private readonly ShellContext _ctx;

    private PanelContainer _stripPlate;
    private HBoxContainer _stripRow;
    private readonly HashSet<int> _visibleArmyIds = new();

    private ShellLayoutProfile _profile;
    private ShellDockGeometry _dock;
    private double _mergeClock;
    private string _lastStripSig = "";

    public ShellStripView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _stripPlate = new PanelContainer
        {
            Name = "ShellBottomStrip",
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _stripPlate.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 12, 8, 6, 8, 6, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_stripPlate);

        _stripRow = new HBoxContainer
        {
            Name = "ShellBottomStripRow",
            Alignment = BoxContainer.AlignmentMode.Begin
        };
        _stripRow.AddThemeConstantOverride("separation", 8);
        _stripPlate.AddChild(_stripRow);
    }

    public void ApplyDock(ShellLayoutProfile profile, ShellDockGeometry dock)
    {
        _profile = profile;
        var compactChanged = _dock is null || _dock.CompactStripTokens != dock.CompactStripTokens;
        _dock = dock;
        if (compactChanged) _lastStripSig = "";
        FitStripBounds();
    }

    private void FitStripBounds()
    {
        if (_stripPlate is null || _dock is null) return;
        _stripPlate.ResetSize();
        var wantedW = Math.Min(_dock.BottomStrip.W, Math.Max(72f, _stripPlate.GetCombinedMinimumSize().X));
        var rect = new ShellRect(_dock.BottomStrip.X, _dock.BottomStrip.Y, wantedW, _dock.BottomStrip.H);
        ShellContext.PlaceRect(_stripPlate, rect);
    }

    public void Refresh()
    {
        var world = _ctx.World;
        if (world?.Player is not { } player || _dock is null) return;

        var openWars = Warfare.OpenWars(world).Where(w => !w.Ended && w.Involves(player.Id)).ToArray();
        var playerArmies = world.Armies.Where(a => a.NationId == player.Id).ToArray();
        var selectedIds = (_ctx.GetSelectedArmies?.Invoke() ?? Array.Empty<Army>()).Select(a => a.Id).ToHashSet();
        var undoRec = BatchActionUndo.Peek(world, player.Id);

        var sig = $"{_dock.CompactStripTokens}|{_profile.Compact}|{Loc.Language}|{undoRec?.SummaryText}|" +
                  $"{string.Join(",", selectedIds.OrderBy(x => x))}|" +
                  $"{openWars.Length}|{playerArmies.Length}:{playerArmies.Sum(a => (int)MathF.Ceiling(a.Divisions))}";
        if (_lastStripSig == sig) return;
        _lastStripSig = sig;

        foreach (var ch in _stripRow.GetChildren()) ch.QueueFree();

        var maxSlots = undoRec is not null ? 3 : 4;
        var added = 0;

        // 1. Active Front bundle tags first while at war
        foreach (var war in openWars.Take(2))
        {
            if (added >= maxSlots) break;
            var capturedWar = war;
            var enemyId = war.EnemiesOf(player.Id).FirstOrDefault();
            var enemyName = world.NationById(enemyId)?.DisplayName ?? Loc.Get("Front");
            var score = (int)MathF.Round(war.IsAttacking(player.Id) ? war.WarScore : -war.WarScore);
            var frontDivs = (int)MathF.Ceiling(playerArmies.Where(a => a.AssignedWarId == war.Id).Sum(a => a.Divisions));
            if (frontDivs <= 0) frontDivs = (int)MathF.Ceiling(playerArmies.Sum(a => a.Divisions));

            AddWovenPaperTag(
                isFront: true,
                flagTag: "",
                title: $"{Loc.Get("Front")} {enemyName}",
                divsText: $"{frontDivs} {Loc.Get("shell.div_short")}",
                orderGlyph: "icon_shell_fight",
                statusText: $"{Loc.Get("Live")} · {score:+#;-#;0}%",
                groupText: "",
                stripeColor: Ui.Colour("shell.state.negative"),
                selected: false,
                compactMode: _dock.CompactStripTokens,
                compactCount: frontDivs.ToString(CultureInfo.InvariantCulture),
                onClick: () =>
                {
                    _ctx.OpenWarLedger?.Invoke();
                    _ctx.ShowWarDetail?.Invoke(capturedWar.Id);
                });
            added++;
        }

        // 2. Field Armies / Army Groups as luggage-style woven linen tags
        var groups = world.ArmyGroups.Where(g => g.NationId == player.Id).ToArray();
        if (groups.Length > 0 && added < maxSlots)
        {
            foreach (var grp in groups.Take(maxSlots - added))
            {
                var grpArmies = playerArmies.Where(a => a.ArmyGroupId == grp.Id).ToArray();
                if (grpArmies.Length == 0) continue;
                var lead = grpArmies[0];
                var divs = (int)MathF.Ceiling(grpArmies.Sum(a => a.Divisions));
                var sel = grpArmies.Any(a => selectedIds.Contains(a.Id));
                var (glyph, orderTxt, stripe) = DescribeArmyOrder(world, lead);
                var nm = string.IsNullOrWhiteSpace(grp.Name) ? $"{Loc.Get("Army Group")} {grp.Id}" : grp.Name;

                AddWovenPaperTag(
                    isFront: false,
                    flagTag: player.Tag,
                    title: nm,
                    divsText: $"{divs} {Loc.Get("shell.div_short")}",
                    orderGlyph: glyph,
                    statusText: orderTxt,
                    groupText: $"{Loc.Get("shell.group_short")} {grp.Id % 10}",
                    stripeColor: stripe,
                    selected: sel,
                    compactMode: _dock.CompactStripTokens,
                    compactCount: divs.ToString(CultureInfo.InvariantCulture),
                    onClick: () => FocusArmy(world, lead));
                added++;
            }
        }
        else if (playerArmies.Length > 0 && added < maxSlots)
        {
            foreach (var army in playerArmies.OrderByDescending(a => a.Divisions).Take(maxSlots - added))
            {
                var capturedArmy = army;
                var divs = Math.Max(1, (int)MathF.Ceiling(army.Divisions));
                var sel = selectedIds.Contains(army.Id);
                var (glyph, orderTxt, stripe) = DescribeArmyOrder(world, army);
                var title = string.IsNullOrWhiteSpace(army.Name)
                    ? (world.States.FirstOrDefault(s => s.MapIndex == army.StateMapIndex)?.DisplayName ?? Loc.Get("Army"))
                    : army.Name;

                AddWovenPaperTag(
                    isFront: false,
                    flagTag: player.Tag,
                    title: title,
                    divsText: $"{divs} {Loc.Get("shell.div_short")}",
                    orderGlyph: glyph,
                    statusText: orderTxt,
                    groupText: army.ArmyGroupId > 0 ? $"{Loc.Get("shell.group_short")} {army.ArmyGroupId % 10}" : "",
                    stripeColor: stripe,
                    selected: sel,
                    compactMode: _dock.CompactStripTokens,
                    compactCount: divs.ToString(CultureInfo.InvariantCulture),
                    onClick: () => FocusArmy(world, capturedArmy));
                added++;
            }
        }

        // 3. Sunken Ctrl+Z Undo cell when an undoable batch/budget action exists
        if (undoRec is not null && !_dock.CompactStripTokens)
            AddUndoCell(undoRec.SummaryText);

        _stripPlate.Visible = added > 0 || undoRec is not null;
        FitStripBounds();
    }

    private static (string Glyph, string Text, Color Stripe) DescribeArmyOrder(Campaign world, Army army)
    {
        var stateName = world.States.FirstOrDefault(s => s.MapIndex == army.StateMapIndex)?.DisplayName ?? "";
        if (army.InBattle)
            return ("icon_shell_fight", $"{Loc.Get("Live")} · {stateName}", Ui.Colour("shell.state.negative"));
        if (army.DirectOrder)
            return ("icon_shell_march", $"{Loc.Get("shell.order_march")} · {stateName}", Ui.Colour("shell.state.warning"));
        return ("icon_shell_stand", $"{Loc.Get("shell.order_stand")} · {stateName}", Ui.Colour("shell.paper_tag_edge"));
    }

    private void AddWovenPaperTag(
        bool isFront,
        string flagTag,
        string title,
        string divsText,
        string orderGlyph,
        string statusText,
        string groupText,
        Color stripeColor,
        bool selected,
        bool compactMode,
        string compactCount,
        Action onClick)
    {
        var smallVp = _profile.Compact;
        var w = compactMode ? (smallVp ? 48f : 64f) : (smallVp ? 142f : 182f);
        var h = smallVp ? 36f : 52f;

        var tagStyle = _ctx.NineSlice("paper_tag_9s", 12, 0, 0, 0, 0, "shell.paper_tag_bottom", "shell.border", 2);
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(w, h),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", tagStyle);
        btn.AddThemeStyleboxOverride("hover", tagStyle);
        btn.AddThemeStyleboxOverride("pressed", tagStyle);
        btn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            onClick();
        };

        var decor = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        decor.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        decor.Draw += () =>
        {
            var sz = decor.Size;
            if (isFront)
            {
                decor.DrawRect(new Rect2(3f, -4f, sz.X - 4f, 4f), Ui.Colour("shell.paper_tag_top", 0.85f));
                decor.DrawRect(new Rect2(3f, -4f, sz.X - 4f, 4f), Ui.Colour("shell.ink", 0.55f), filled: false, width: 1f);
            }
            decor.DrawRect(new Rect2(1f, 1f, 5f, sz.Y - 2f), stripeColor);
            if (selected)
            {
                decor.DrawRect(new Rect2(1f, 1f, sz.X - 2f, sz.Y - 2f), Ui.Colour("shell.ink"), filled: false, width: 2.2f);
                decor.DrawLine(new Vector2(6f, sz.Y + 2f), new Vector2(sz.X - 4f, sz.Y + 2f), Ui.Colour("shell.paper"), 2f);
            }
        };
        btn.AddChild(decor);

        if (compactMode)
        {
            var crow = new HBoxContainer
            {
                Alignment = BoxContainer.AlignmentMode.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            crow.AddThemeConstantOverride("separation", 4);
            crow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            crow.OffsetLeft = 6; crow.OffsetRight = -4;
            btn.AddChild(crow);

            if (!isFront && !string.IsNullOrEmpty(flagTag))
            {
                var fl = new TextureRect
                {
                    Texture = _ctx.FlagTexture(flagTag),
                    CustomMinimumSize = new Vector2(smallVp ? 16 : 20, smallVp ? 11 : 14),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                crow.AddChild(fl);
            }
            var cl = new Label
            {
                Text = compactCount,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            cl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            cl.AddThemeFontSizeOverride("font_size", smallVp ? 11 : 13);
            cl.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
            crow.AddChild(cl);
        }
        else
        {
            var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            v.AddThemeConstantOverride("separation", 1);
            v.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            v.OffsetLeft = 9; v.OffsetRight = -7; v.OffsetTop = smallVp ? 3 : 5; v.OffsetBottom = -3;
            btn.AddChild(v);

            var r1 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            r1.AddThemeConstantOverride("separation", 5);
            v.AddChild(r1);

            if (!isFront && !string.IsNullOrEmpty(flagTag))
            {
                var fl = new TextureRect
                {
                    Texture = _ctx.FlagTexture(flagTag),
                    CustomMinimumSize = new Vector2(smallVp ? 17 : 22, smallVp ? 11 : 15),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                r1.AddChild(fl);
            }

            var nm = new Label
            {
                Text = title,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.Off,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            nm.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            nm.AddThemeFontSizeOverride("font_size", smallVp ? 11 : 14);
            nm.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
            r1.AddChild(nm);

            var dv = new Label
            {
                Text = divsText,
                HorizontalAlignment = HorizontalAlignment.Right,
                AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            dv.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            dv.AddThemeFontSizeOverride("font_size", smallVp ? 10 : 11);
            dv.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
            r1.AddChild(dv);

            var r2 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            r2.AddThemeConstantOverride("separation", 4);
            v.AddChild(r2);

            var og = new TextureRect
            {
                Texture = _ctx.Icon(orderGlyph),
                CustomMinimumSize = new Vector2(11, 11),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = Ui.Colour("shell.ink_muted"),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            r2.AddChild(og);

            var st = new Label
            {
                Text = statusText,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.Off,
                ClipText = true,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            st.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            st.AddThemeFontSizeOverride("font_size", smallVp ? 10 : 11);
            st.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
            r2.AddChild(st);

            if (!string.IsNullOrEmpty(groupText))
            {
                var gr = new Label
                {
                    Text = groupText,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                gr.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
                gr.AddThemeFontSizeOverride("font_size", 10);
                gr.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_faint"));
                r2.AddChild(gr);
            }
        }

        _stripRow.AddChild(btn);
    }

    private void AddUndoCell(string summary)
    {
        var box = new PanelContainer
        {
            CustomMinimumSize = new Vector2(_profile.Compact ? 210 : 280, _profile.Compact ? 36 : 48),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        box.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("well_recessed_9s", 10, 8, 4, 8, 4, "shell.well_top", "shell.border", 2));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 7);
        box.AddChild(row);

        var ico = new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_undo"),
            CustomMinimumSize = new Vector2(15, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            Modulate = Ui.Colour("shell.paper_muted")
        };
        row.AddChild(ico);

        var lbl = new Label
        {
            Text = summary,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        lbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(lbl);

        var undoBtn = new Button
        {
            Text = $"{Loc.Get("Undo")} Ctrl+Z",
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        undoBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 8, 7, 2, 7, 2));
        undoBtn.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        undoBtn.AddThemeFontSizeOverride("font_size", 11);
        undoBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        undoBtn.Pressed += () =>
        {
            _ctx.PlayCue?.Invoke("ui_select");
            _ctx.PerformBatchUndo?.Invoke();
        };
        row.AddChild(undoBtn);

        _stripRow.AddChild(box);
    }

    private void FocusArmy(Campaign world, Army army)
    {
        if (army is null || world.ProvinceMap is null) return;
        _ctx.PickArmy?.Invoke(army, false);
        if (army.ProvinceId > 0)
        {
            var (lat, lon) = world.ProvinceMap.CenterOf(army.ProvinceId);
            _ctx.FocusLatLon?.Invoke(lat, lon, 4.5f);
        }
    }

    /// <summary>
    /// Ensures 100% of divisions stay visible at combat/working zoom (NewShellVisibleArmyIds = null)
    /// and never culls active-war/front/player armies on fronts like Russia–Ukraine even at orbital zoom!
    /// </summary>
    public void TickDivisionVisibility(double delta)
    {
        _mergeClock += delta;
        if (_mergeClock < 0.25) return;
        _mergeClock = 0;

        var world = _ctx.World;
        if (world?.Player is not { } player)
        {
            _ctx.SetMergedVisibleArmies?.Invoke(null);
            return;
        }

        var isFlat = _ctx.IsFlatMap?.Invoke() ?? false;
        var tier = MapCounterMerge.ZoomTier(
            _ctx.GetViewDistance?.Invoke() ?? 6f,
            isFlat,
            _ctx.GetFlatZoom?.Invoke() ?? 4f);

        // At combat, tactical, and regional zoom, or in Wars map mode: show ALL divisions without filtering!
        if (tier is MapZoomTier.TacticalClose or MapZoomTier.OperationalMid ||
            (_ctx.GetMapMode?.Invoke() ?? StrategicMapMode.Political) == StrategicMapMode.Wars)
        {
            _ctx.SetMergedVisibleArmies?.Invoke(null);
            return;
        }

        // At far continental/orbital zoom: keep ALL player + ALL active-war/front armies visible,
        // filtering only distant peacetime neutral garrisons so active fronts are never culled.
        var openWars = Warfare.OpenWars(world);
        var warNationIds = new HashSet<int>();
        for (var ni = 0; ni < world.Nations.Count; ni++)
        {
            var nid = world.Nations[ni].Id;
            for (var wi = 0; wi < openWars.Count; wi++)
                if (!openWars[wi].Ended && openWars[wi].Involves(nid)) { warNationIds.Add(nid); break; }
        }

        _visibleArmyIds.Clear();
        for (var i = 0; i < world.Armies.Count; i++)
        {
            var a = world.Armies[i];
            if (a.NationId == player.Id || a.InBattle || a.AssignedWarId >= 0 || warNationIds.Contains(a.NationId))
                _visibleArmyIds.Add(a.Id);
        }
        _ctx.SetMergedVisibleArmies?.Invoke(_visibleArmyIds);
    }
}
