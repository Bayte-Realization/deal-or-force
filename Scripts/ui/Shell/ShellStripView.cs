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
/// division visibility policy for the campaign shell: live war-score &amp; order hash tracking,
/// whole-group selection on Army Group tags, clipped hover-responsive tags, and overflow count.
/// </summary>
internal sealed class ShellStripView
{
    private readonly ShellContext _ctx;

    private PanelContainer _stripPlate;
    private HBoxContainer _stripRow;
    private readonly HashSet<int> _visibleArmyIds = new();
    private readonly HashSet<int> _warNationIds = new();

    private readonly List<StrategicWar> _openWarsScratch = new();
    private readonly List<Army> _playerArmiesScratch = new();
    private readonly HashSet<int> _selectedIdsScratch = new();
    private readonly List<int> _selectedIdsSortedScratch = new();
    private readonly List<ArmyGroup> _playerGroupsScratch = new();
    private readonly List<Army> _groupArmiesScratch = new();
    private readonly List<Army> _topArmiesScratch = new();

    private ShellLayoutProfile _profile;
    private ShellDockGeometry _dock;
    private double _mergeClock;
    private readonly bool _isMobileRuntime = OS.HasFeature("mobile") || OS.GetName() is "Android" or "iOS";
    private int _lastStripHash;
    private bool _hasStripHash;
    private Campaign _lastMergeWorld;
    private int _lastMergePlayerId = int.MinValue;
    private int _lastMergeWarRev = int.MinValue;
    private int _lastMergeArmyCount = -1;
    private MapZoomTier? _lastMergeTier;
    private StrategicMapMode? _lastMergeMode;

    public ShellStripView(ShellContext ctx) => _ctx = ctx;

    public void Build()
    {
        _stripPlate = new PanelContainer
        {
            Name = "ShellBottomStrip",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _stripPlate.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_umber_9s", 12, 8, 4, 8, 4, "shell.plate_bottom", "shell.border", 3));
        _ctx.Root.AddChild(_stripPlate);

        _stripRow = new HBoxContainer
        {
            Name = "ShellBottomStripRow",
            ClipContents = true,
            Alignment = BoxContainer.AlignmentMode.Begin
        };
        _stripRow.AddThemeConstantOverride("separation", 8);
        _stripPlate.AddChild(_stripRow);
    }

    public void ApplyDock(ShellLayoutProfile profile, ShellDockGeometry dock)
    {
        var compactChanged = _dock is null || _dock.CompactStripTokens != dock.CompactStripTokens ||
                             _profile is null || _profile.Compact != profile.Compact || _profile.TouchMode != profile.TouchMode;
        _profile = profile;
        _dock = dock;
        if (compactChanged) _hasStripHash = false;
        FitStripBounds();
    }

    private void FitStripBounds()
    {
        if (_stripPlate is null || _dock is null) return;
        _stripPlate.ResetSize();
        var minSz = _stripPlate.GetCombinedMinimumSize();
        var stripX = _profile?.TouchMode == true
            ? Math.Max(_dock.BottomStrip.X, _profile.LeftRail.Right + 14f)
            : _dock.BottomStrip.X;
        var availW = Math.Max(72f, _dock.BottomStrip.Right - stripX);
        var wantedW = Math.Min(availW, Math.Max(72f, minSz.X));
        var wantedH = Math.Max(_dock.BottomStrip.H, minSz.Y);
        var bottomY = _profile?.TouchMode == true
            ? Math.Min(_dock.BottomStrip.Bottom, _profile.ViewportHeight - 12f)
            : _dock.BottomStrip.Bottom;
        var stripY = Math.Max(8f, bottomY - wantedH);
        ShellContext.PlaceRect(_stripPlate, new ShellRect(stripX, stripY, wantedW, wantedH));
    }

    public void Refresh()
    {
        var world = _ctx.World;
        if (world?.Player is not { } player || _dock is null) return;

        _openWarsScratch.Clear();
        var liveHash = new HashCode();
        var allWars = Warfare.OpenWars(world);
        for (var i = 0; i < allWars.Count; i++)
        {
            var w = allWars[i];
            if (!w.Ended && w.Involves(player.Id))
            {
                _openWarsScratch.Add(w);
                liveHash.Add(w.Id);
                liveHash.Add((int)MathF.Round(w.WarScore));
            }
        }

        _playerArmiesScratch.Clear();
        var totalDivisions = 0;
        for (var i = 0; i < world.Armies.Count; i++)
        {
            var a = world.Armies[i];
            if (a.NationId == player.Id)
            {
                _playerArmiesScratch.Add(a);
                totalDivisions += (int)MathF.Ceiling(a.Divisions);
                liveHash.Add(a.InBattle);
                liveHash.Add(a.DirectOrder);
                liveHash.Add((int)a.OrderKind);
                liveHash.Add(a.StateMapIndex);
                liveHash.Add(a.ArmyGroupId);
            }
        }

        _selectedIdsScratch.Clear();
        if (_ctx.GetSelectedArmies?.Invoke() is { } selArmies)
            for (var i = 0; i < selArmies.Count; i++)
                _selectedIdsScratch.Add(selArmies[i].Id);
        var undoRec = BatchActionUndo.Peek(world, player.Id);

        _selectedIdsSortedScratch.Clear();
        foreach (var id in _selectedIdsScratch) _selectedIdsSortedScratch.Add(id);
        _selectedIdsSortedScratch.Sort();

        var isObs = _ctx.IsObserverMode?.Invoke() ?? world.ObserverMode;
        liveHash.Add(player.Id);
        liveHash.Add(isObs);
        liveHash.Add(_dock.CompactStripTokens);
        liveHash.Add(_profile.Compact);
        liveHash.Add(_profile.TouchMode);
        liveHash.Add(Loc.Language);
        liveHash.Add(undoRec?.SummaryText);
        liveHash.Add(_openWarsScratch.Count);
        liveHash.Add(_playerArmiesScratch.Count);
        liveHash.Add(totalDivisions);
        liveHash.Add(_selectedIdsSortedScratch.Count);
        for (var i = 0; i < _selectedIdsSortedScratch.Count; i++) liveHash.Add(_selectedIdsSortedScratch[i]);
        var stripHash = liveHash.ToHashCode();
        if (_hasStripHash && _lastStripHash == stripHash) return;
        _hasStripHash = true;
        _lastStripHash = stripHash;

        foreach (var ch in _stripRow.GetChildren()) { _stripRow.RemoveChild(ch); ch.QueueFree(); }

        var maxSlots = undoRec is not null ? 3 : 4;
        var added = 0;
        var hiddenCount = 0;

        // 1. Active Front bundle tags first while at war
        var warsToTake = Math.Min(2, _openWarsScratch.Count);
        hiddenCount += Math.Max(0, _openWarsScratch.Count - warsToTake);
        for (var wi = 0; wi < warsToTake; wi++)
        {
            if (added >= maxSlots) { hiddenCount++; continue; }
            var war = _openWarsScratch[wi];
            var capturedWar = war;
            var enemyId = war.EnemiesOf(player.Id).FirstOrDefault(-1);
            var enemyName = world.NationById(enemyId)?.DisplayName ?? Loc.Get("Front");
            var score = (int)MathF.Round(war.IsAttacking(player.Id) ? war.WarScore : -war.WarScore);
            var frontDivs = 0;
            for (var ai = 0; ai < _playerArmiesScratch.Count; ai++)
                if (_playerArmiesScratch[ai].AssignedWarId == war.Id)
                    frontDivs += (int)MathF.Ceiling(_playerArmiesScratch[ai].Divisions);
            if (frontDivs <= 0) frontDivs = totalDivisions;

            AddWovenPaperTag(
                isFront: true, flagTag: "", title: $"{Loc.Get("Front")} {enemyName}",
                divsText: $"{frontDivs} {Loc.Get("shell.div_short")}", orderGlyph: "icon_shell_fight",
                statusText: $"{Loc.Get("Live")} · {score:+#;-#;0}%", groupText: "",
                stripeColor: Ui.Colour("shell.state.negative"), selected: false,
                compactMode: _dock.CompactStripTokens, compactCount: frontDivs.ToString(CultureInfo.InvariantCulture),
                onClick: () => { _ctx.OpenWarLedger?.Invoke(); _ctx.ShowWarDetail?.Invoke(capturedWar.Id); });
            added++;
        }

        // 2. Field Armies / Army Groups (clicking selects the ENTIRE Army Group!)
        _playerGroupsScratch.Clear();
        for (var gi = 0; gi < world.ArmyGroups.Count; gi++)
            if (world.ArmyGroups[gi].NationId == player.Id) _playerGroupsScratch.Add(world.ArmyGroups[gi]);

        if (_playerGroupsScratch.Count > 0)
        {
            var populatedGroups = 0;
            for (var gi = 0; gi < _playerGroupsScratch.Count; gi++)
            {
                var grp = _playerGroupsScratch[gi];
                _groupArmiesScratch.Clear();
                var divs = 0;
                var sel = false;
                for (var ai = 0; ai < _playerArmiesScratch.Count; ai++)
                {
                    var a = _playerArmiesScratch[ai];
                    if (a.ArmyGroupId == grp.Id)
                    {
                        _groupArmiesScratch.Add(a);
                        divs += (int)MathF.Ceiling(a.Divisions);
                        if (!sel && _selectedIdsScratch.Contains(a.Id)) sel = true;
                    }
                }
                if (_groupArmiesScratch.Count == 0) continue;
                populatedGroups++;
                if (added >= maxSlots) { hiddenCount++; continue; }

                var lead = _groupArmiesScratch[0];
                var capturedGroupArmies = _groupArmiesScratch.ToArray();
                var (glyph, orderTxt, stripe) = DescribeArmyOrder(world, lead);
                var nm = string.IsNullOrWhiteSpace(grp.Name) ? $"{Loc.Get("Army Group")} {grp.Id}" : grp.Name;

                AddWovenPaperTag(
                    isFront: false, flagTag: player.Tag, title: nm,
                    divsText: $"{divs} {Loc.Get("shell.div_short")}", orderGlyph: glyph,
                    statusText: orderTxt, groupText: $"{Loc.Get("shell.group_short")} {grp.Id % 10}",
                    stripeColor: stripe, selected: sel,
                    compactMode: _dock.CompactStripTokens, compactCount: divs.ToString(CultureInfo.InvariantCulture),
                    onClick: () => SelectAndFocusGroup(world, capturedGroupArmies));
                added++;
            }
            if (populatedGroups == 0) AddTopIndividualArmies(world, player, maxSlots, ref added, ref hiddenCount);
        }
        else if (_playerArmiesScratch.Count > 0)
        {
            AddTopIndividualArmies(world, player, maxSlots, ref added, ref hiddenCount);
        }

        // Compact +N overflow indicator when extra fronts or armies exist beyond the visible slots
        if (hiddenCount > 0)
            AddOverflowBadge(hiddenCount);

        // 3. Sunken Ctrl+Z Undo cell when an undoable batch/budget action exists
        if (undoRec is not null && !_dock.CompactStripTokens)
            AddUndoCell(undoRec.SummaryText);

        _stripPlate.Visible = added > 0 || undoRec is not null;
        FitStripBounds();
    }

    private void AddTopIndividualArmies(Campaign world, Nation player, int maxSlots, ref int added, ref int hiddenCount)
    {
        _topArmiesScratch.Clear();
        _topArmiesScratch.AddRange(_playerArmiesScratch);
        _topArmiesScratch.Sort(static (a, b) => b.Divisions.CompareTo(a.Divisions));
        var armiesToTake = Math.Min(Math.Max(0, maxSlots - added), _topArmiesScratch.Count);
        hiddenCount += Math.Max(0, _topArmiesScratch.Count - armiesToTake);
        for (var ai = 0; ai < armiesToTake; ai++)
        {
            var army = _topArmiesScratch[ai];
            var capturedArmy = army;
            var divs = Math.Max(1, (int)MathF.Ceiling(army.Divisions));
            var sel = _selectedIdsScratch.Contains(army.Id);
            var (glyph, orderTxt, stripe) = DescribeArmyOrder(world, army);
            var title = string.IsNullOrWhiteSpace(army.Name)
                ? (world.StateByMapIndex(army.StateMapIndex)?.DisplayName ?? Loc.Get("Army"))
                : army.Name;

            AddWovenPaperTag(
                isFront: false, flagTag: player.Tag, title: title,
                divsText: $"{divs} {Loc.Get("shell.div_short")}", orderGlyph: glyph,
                statusText: orderTxt, groupText: army.ArmyGroupId > 0 ? $"{Loc.Get("shell.group_short")} {army.ArmyGroupId % 10}" : "",
                stripeColor: stripe, selected: sel,
                compactMode: _dock.CompactStripTokens, compactCount: divs.ToString(CultureInfo.InvariantCulture),
                onClick: () => FocusArmy(world, capturedArmy));
            added++;
        }
    }

    private void AddOverflowBadge(int hiddenCount)
    {
        var smallVp = _profile.Compact;
        var touch = _profile.TouchMode;
        var btn = new Button
        {
            Name = "ShellStripOverflowBadge",
            Text = Loc.Format("shell.ws.army.strip_more_fmt", hiddenCount),
            CustomMinimumSize = new Vector2(touch ? 44 : (smallVp ? 36 : 44), touch ? 44 : (smallVp ? 36 : 52)),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            TooltipText = Ui.Tip(Loc.Get("shell.ws.army.strip_more_tip")),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", _ctx.BorderedBox("shell.plate_top", "shell.engrave", 1, 2, 4, 2, 4, 2));
        btn.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 4, 2, 4, 2));
        btn.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        btn.AddThemeFontSizeOverride("font_size", smallVp ? 11 : 13);
        btn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); _ctx.OpenScreen?.Invoke("Military"); };
        _stripRow.AddChild(btn);
    }

    private static (string Glyph, string Text, Color Stripe) DescribeArmyOrder(Campaign world, Army army)
    {
        var stateName = world.StateByMapIndex(army.StateMapIndex)?.DisplayName ?? "";
        if (army.InBattle) return ("icon_shell_fight", $"{Loc.Get("Live")} · {stateName}", Ui.Colour("shell.state.negative"));
        if (army.DirectOrder) return ("icon_shell_march", $"{Loc.Get("shell.order_march")} · {stateName}", Ui.Colour("shell.state.warning"));
        return ("icon_shell_stand", $"{Loc.Get("shell.order_stand")} · {stateName}", Ui.Colour("shell.paper_tag_edge"));
    }

    private void AddWovenPaperTag(
        bool isFront, string flagTag, string title, string divsText, string orderGlyph,
        string statusText, string groupText, Color stripeColor, bool selected,
        bool compactMode, string compactCount, Action onClick)
    {
        var smallVp = _profile.Compact;
        var touch = _profile.TouchMode;
        var w = compactMode ? (smallVp ? 48f : 64f) : (touch ? 158f : (smallVp ? 142f : 182f));
        var h = touch ? 44f : (smallVp ? 38f : 52f);

        var tagStyle = _ctx.NineSlice("paper_tag_9s", 12, 0, 0, 0, 0, "shell.paper_tag_bottom", "shell.border", 2);
        var tagHover = _ctx.BorderedBox("shell.paper_tag_top", "shell.brass_hi", 1, 2, 0, 0, 0, 0);
        var tagPressed = _ctx.BorderedBox("shell.paper_tag_bottom", "shell.ink", 2, 2, 0, 0, 0, 0);

        var btn = new Button
        {
            CustomMinimumSize = new Vector2(w, h),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            ClipContents = true,
            TooltipText = Ui.Tip($"{title} · {divsText}\n{statusText}"),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        btn.AddThemeStyleboxOverride("normal", tagStyle);
        btn.AddThemeStyleboxOverride("hover", tagHover);
        btn.AddThemeStyleboxOverride("pressed", tagPressed);
        btn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); onClick(); };

        var decor = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        decor.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        decor.Draw += () =>
        {
            var sz = decor.Size;
            if (isFront)
                decor.DrawRect(new Rect2(1f, 1f, sz.X - 2f, 3f), Ui.Colour("shell.state.negative", 0.85f));
            decor.DrawRect(new Rect2(1f, 1f, 5f, sz.Y - 2f), stripeColor);
            if (selected)
                decor.DrawRect(new Rect2(1f, 1f, sz.X - 2f, sz.Y - 2f), Ui.Colour("shell.ink"), filled: false, width: 2.2f);
        };
        btn.AddChild(decor);

        if (compactMode)
        {
            var crow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            crow.AddThemeConstantOverride("separation", 4);
            crow.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            crow.OffsetLeft = 6; crow.OffsetRight = -4;
            btn.AddChild(crow);

            if (!isFront && !string.IsNullOrEmpty(flagTag))
            {
                crow.AddChild(new TextureRect
                {
                    Material = FlagClothPresentation.Material,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
                    CustomMinimumSize = new Vector2(smallVp ? 16 : 20, smallVp ? 11 : 14),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Texture = _ctx.FlagTexture(flagTag),
                    MouseFilter = Control.MouseFilterEnum.Ignore
                });
            }
            var cl = new Label { Text = compactCount, VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            cl.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            cl.AddThemeFontSizeOverride("font_size", smallVp ? 11 : 13);
            cl.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
            crow.AddChild(cl);
        }
        else
        {
            var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            v.AddThemeConstantOverride("separation", 1);
            v.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var padV = smallVp && !touch ? 2 : 4;
            v.OffsetLeft = 9; v.OffsetRight = -7; v.OffsetTop = padV; v.OffsetBottom = -padV;
            btn.AddChild(v);

            var r1 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            r1.AddThemeConstantOverride("separation", 5);
            v.AddChild(r1);

            if (!isFront && !string.IsNullOrEmpty(flagTag))
            {
                r1.AddChild(new TextureRect
                {
                    Material = FlagClothPresentation.Material,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
                    CustomMinimumSize = new Vector2(smallVp ? 17 : 22, smallVp ? 11 : 15),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, Texture = _ctx.FlagTexture(flagTag),
                    MouseFilter = Control.MouseFilterEnum.Ignore
                });
            }

            var nm = new Label { Text = title, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.Off, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = Control.MouseFilterEnum.Ignore };
            nm.AddThemeFontOverride("font", _ctx.Font("shell.serif_semibold"));
            nm.AddThemeFontSizeOverride("font_size", smallVp ? 11 : 14);
            nm.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
            r1.AddChild(nm);

            var dv = new Label { Text = divsText, HorizontalAlignment = HorizontalAlignment.Right, AutowrapMode = TextServer.AutowrapMode.Off, MouseFilter = Control.MouseFilterEnum.Ignore };
            dv.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
            dv.AddThemeFontSizeOverride("font_size", smallVp ? 10 : 11);
            dv.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
            r1.AddChild(dv);

            var r2 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ClipContents = true };
            r2.AddThemeConstantOverride("separation", 4);
            v.AddChild(r2);

            r2.AddChild(new TextureRect
            {
                Texture = _ctx.Icon(orderGlyph), CustomMinimumSize = new Vector2(11, 11),
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.ink_muted"),
                MouseFilter = Control.MouseFilterEnum.Ignore
            });

            var st = new Label { Text = statusText, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.Off, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = Control.MouseFilterEnum.Ignore };
            st.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
            st.AddThemeFontSizeOverride("font_size", smallVp ? 10 : 11);
            st.AddThemeColorOverride("font_color", Ui.Colour("shell.ink_muted"));
            r2.AddChild(st);

            if (!string.IsNullOrEmpty(groupText))
            {
                var gr = new Label { Text = groupText, HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = Control.MouseFilterEnum.Ignore };
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
        var touch = _profile.TouchMode;
        var box = new PanelContainer
        {
            CustomMinimumSize = new Vector2(_profile.Compact ? 210 : 280, touch ? 44 : (_profile.Compact ? 36 : 48)),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ClipContents = true
        };
        box.AddThemeStyleboxOverride("panel", _ctx.NineSlice("well_recessed_9s", 10, 8, 4, 8, 4, "shell.well_top", "shell.border", 2));

        var row = new HBoxContainer { ClipContents = true };
        row.AddThemeConstantOverride("separation", 7);
        box.AddChild(row);

        row.AddChild(new TextureRect
        {
            Texture = _ctx.Icon("icon_shell_undo"), CustomMinimumSize = new Vector2(15, 15),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, Modulate = Ui.Colour("shell.paper_muted")
        });

        var lbl = new Label { Text = summary, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        lbl.AddThemeFontOverride("font", _ctx.Font("shell.sans"));
        lbl.AddThemeFontSizeOverride("font_size", 11);
        lbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        row.AddChild(lbl);

        var undoBtn = new Button
        {
            Text = touch ? Loc.Get("Undo") : $"{Loc.Get("Undo")} Ctrl+Z", CustomMinimumSize = new Vector2(0, touch ? 36 : 26),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            FocusMode = Control.FocusModeEnum.None
        };
        undoBtn.AddThemeStyleboxOverride("normal", _ctx.NineSlice("button_umber_9s", 8, 7, 2, 7, 2));
        undoBtn.AddThemeStyleboxOverride("hover", _ctx.BorderedBox("shell.plate_hover", "shell.brass_hi", 1, 2, 7, 2, 7, 2));
        undoBtn.AddThemeFontOverride("font", _ctx.Font("shell.sans", tabular: true));
        undoBtn.AddThemeFontSizeOverride("font_size", 11);
        undoBtn.AddThemeColorOverride("font_color", Ui.Colour("shell.paper"));
        undoBtn.Pressed += () => { _ctx.PlayCue?.Invoke("ui_select"); _ctx.PerformBatchUndo?.Invoke(); };
        row.AddChild(undoBtn);
        _stripRow.AddChild(box);
    }

    private void SelectAndFocusGroup(Campaign world, Army[] groupArmies)
    {
        if (groupArmies is null || groupArmies.Length == 0 || world.ProvinceMap is null) return;
        for (var i = 0; i < groupArmies.Length; i++)
            _ctx.PickArmy?.Invoke(groupArmies[i], i > 0);
        var lead = groupArmies[0];
        if (lead.ProvinceId > 0)
        {
            var (lat, lon) = world.ProvinceMap.CenterOf(lead.ProvinceId);
            _ctx.FocusLatLon?.Invoke(lat, lon, 4.5f);
        }
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

    public void TickDivisionVisibility(double delta)
    {
        _mergeClock += delta;
        if (_mergeClock < (_isMobileRuntime ? 0.50 : 0.25)) return;
        _mergeClock = 0;

        var world = _ctx.World;
        if (world?.Player is not { } player)
        {
            if (_lastMergeWorld is not null)
            {
                _lastMergeWorld = null;
                _ctx.SetMergedVisibleArmies?.Invoke(null);
            }
            return;
        }

        var isFlat = _ctx.IsFlatMap?.Invoke() ?? false;
        var tier = MapCounterMerge.ZoomTier(_ctx.GetViewDistance?.Invoke() ?? 6f, isFlat, _ctx.GetFlatZoom?.Invoke() ?? 4f);
        var mapMode = _ctx.GetMapMode?.Invoke() ?? StrategicMapMode.Political;
        if (ReferenceEquals(_lastMergeWorld, world) && _lastMergePlayerId == player.Id &&
            _lastMergeWarRev == world.WarRevision && _lastMergeArmyCount == world.Armies.Count &&
            _lastMergeTier == tier && _lastMergeMode == mapMode)
            return;
        _lastMergeWorld = world;
        _lastMergePlayerId = player.Id;
        _lastMergeWarRev = world.WarRevision;
        _lastMergeArmyCount = world.Armies.Count;
        _lastMergeTier = tier;
        _lastMergeMode = mapMode;

        if (tier is MapZoomTier.TacticalClose or MapZoomTier.OperationalMid || mapMode == StrategicMapMode.Wars)
        {
            _ctx.SetMergedVisibleArmies?.Invoke(null);
            return;
        }

        var openWars = Warfare.OpenWars(world);
        _warNationIds.Clear();
        for (var ni = 0; ni < world.Nations.Count; ni++)
        {
            var nid = world.Nations[ni].Id;
            for (var wi = 0; wi < openWars.Count; wi++)
                if (!openWars[wi].Ended && openWars[wi].Involves(nid)) { _warNationIds.Add(nid); break; }
        }

        _visibleArmyIds.Clear();
        for (var i = 0; i < world.Armies.Count; i++)
        {
            var a = world.Armies[i];
            if (a.NationId == player.Id || a.InBattle || a.AssignedWarId >= 0 || _warNationIds.Contains(a.NationId))
                _visibleArmyIds.Add(a.Id);
        }
        _ctx.SetMergedVisibleArmies?.Invoke(_visibleArmyIds);
    }
}
