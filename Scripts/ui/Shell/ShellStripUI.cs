using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _shellStripHost;
    private HBoxContainer _shellStripRow;
    private double _shellMergeTimer;
    private readonly HashSet<int> _shellMergedArmyIds = new();

    private void BuildShellStrip()
    {
        var host = new Control
        {
            Name = "ShellBottomStrip",
            MouseFilter = MouseFilterEnum.Ignore
        };
        _newShellRoot.AddChild(host);
        _shellStripHost = host;

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        host.AddChild(row);
        _shellStripRow = row;
    }

    private void LayoutShellStrip(ShellLayoutProfile profile)
    {
        if (_shellStripHost is null) return;
        PlaceShellRect(_shellStripHost, profile.Strip);
    }

    private void RefreshShellStrip()
    {
        if (_shellStripRow is null || _campaign?.Player is not { } player) return;
        foreach (var child in _shellStripRow.GetChildren()) child.QueueFree();

        var openWars = Warfare.OpenWars(_campaign).Where(w => !w.Ended && w.Involves(player.Id)).ToArray();
        var atWar = openWars.Length > 0;
        var playerArmies = _campaign.Armies.Where(a => a.NationId == player.Id).ToArray();
        var maxTags = 2;
        var count = 0;

        // 1. Active War / Front paper tags first while at war
        if (atWar)
        {
            foreach (var war in openWars.Take(2))
            {
                if (count >= maxTags) break;
                var capturedWar = war;
                var enemyId = war.EnemiesOf(player.Id).FirstOrDefault();
                var enemyName = _campaign.NationById(enemyId)?.DisplayName ?? Tr("Front");
                var score = (int)MathF.Round(war.IsAttacking(player.Id) ? war.WarScore : -war.WarScore);
                var assignedCount = playerArmies.Count(a => a.AssignedWarId == war.Id);
                var subtitle = assignedCount > 0 ? $"{assignedCount} {Tr("shell.div_short")} · {score:+#;-#;0}%" : $"{score:+#;-#;0}%";
                AddShellPaperTag(
                    enemyName,
                    subtitle,
                    "icon_shell_front",
                    isAlert: score < -10,
                    onClick: () =>
                    {
                        OpenWarLedger();
                        ShowWarLedgerDetail(capturedWar.Id);
                    });
                count++;
            }
        }

        // 2. Leading Army Group or Field Army paper tags (never overwhelming rest budget)
        var groups = _campaign.ArmyGroups.Where(g => g.NationId == player.Id).ToArray();
        if (groups.Length > 0 && count < maxTags)
        {
            foreach (var group in groups.Take(maxTags - count))
            {
                var groupArmies = playerArmies.Where(a => a.ArmyGroupId == group.Id).ToArray();
                if (groupArmies.Length == 0) continue;
                var lead = groupArmies[0];
                var divs = (int)MathF.Ceiling(groupArmies.Sum(a => a.Divisions));
                var title = string.IsNullOrWhiteSpace(group.Name) ? $"{Tr("Army Group")} #{group.Id}" : group.Name;
                AddShellPaperTag(
                    title,
                    $"{divs} {Tr("shell.div_short")}",
                    "icon_military",
                    isAlert: false,
                    onClick: () => FocusShellArmy(lead));
                count++;
            }
        }
        else if (playerArmies.Length > 0 && count < maxTags)
        {
            foreach (var army in playerArmies.OrderByDescending(a => a.Divisions).Take(maxTags - count))
            {
                var capturedArmy = army;
                var divs = (int)MathF.Ceiling(army.Divisions);
                var stateName = _campaign.States.FirstOrDefault(s => s.MapIndex == army.StateMapIndex)?.DisplayName ?? Tr("Army");
                AddShellPaperTag(
                    stateName,
                    $"{divs} {Tr("shell.div_short")}",
                    "icon_military",
                    isAlert: false,
                    onClick: () => FocusShellArmy(capturedArmy));
                count++;
            }
        }

        _shellStripHost.Visible = count > 0;
    }

    private void AddShellPaperTag(string title, string detail, string iconName, bool isAlert, Action onClick)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(158, 44),
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        btn.AddThemeStyleboxOverride("normal", ShellPaperTagStyle(isAlert, false));
        btn.AddThemeStyleboxOverride("hover", ShellPaperTagStyle(isAlert, true));
        btn.AddThemeStyleboxOverride("pressed", ShellPaperTagStyle(true, true));
        btn.Pressed += () => { PlayStrategicCue("ui_select"); onClick(); };

        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", 7);
        h.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        btn.AddChild(h);

        h.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });

        var icon = new TextureRect
        {
            Texture = ShellIconTexture(iconName),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(18, 18),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = Ui.Colour("shell.ink"),
            MouseFilter = MouseFilterEnum.Ignore
        };
        h.AddChild(icon);

        var textBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        textBox.AddThemeConstantOverride("separation", 0);
        h.AddChild(textBox);

        var tLbl = new Label
        {
            Text = title,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore
        };
        tLbl.AddThemeFontOverride("font", ShellFont("shell.serif_semibold"));
        tLbl.AddThemeFontSizeOverride("font_size", 13);
        tLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.ink"));
        textBox.AddChild(tLbl);

        var dLbl = new Label
        {
            Text = detail,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        dLbl.AddThemeFontOverride("font", ShellFont("shell.sans", tabular: true));
        dLbl.AddThemeFontSizeOverride("font_size", 11);
        dLbl.AddThemeColorOverride("font_color", Ui.Colour(isAlert ? "shell.state.negative" : "shell.ink_muted"));
        textBox.AddChild(dLbl);

        h.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });
        _shellStripRow.AddChild(btn);
    }

    private void FocusShellArmy(Army army)
    {
        if (army is null || _campaign?.ProvinceMap is null) return;
        OnArmyPicked(army);
        if (_globe is not null && army.ProvinceId > 0)
        {
            var (lat, lon) = _campaign.ProvinceMap.CenterOf(army.ProvinceId);
            _globe.FocusOn(lat, lon, 4.5f);
        }
    }

    private void TickShellCounterMerge(double delta)
    {
        if (_globe is null || _campaign?.Player is not { } player) return;
        _shellMergeTimer += delta;
        if (_shellMergeTimer < 0.20) return;
        _shellMergeTimer = 0;

        var tier = MapCounterMerge.ZoomTier(_globe.ShellViewDistance, _globe.FlatProjection, _globe.ShellFlatZoom);
        var enemySet = new HashSet<int>();
        var allySet = new HashSet<int>();
        foreach (var war in Warfare.OpenWars(_campaign))
        {
            if (war.Ended || !war.Involves(player.Id)) continue;
            foreach (var eId in war.EnemiesOf(player.Id)) enemySet.Add(eId);
        }
        foreach (var rel in _campaign.Relations.Values)
        {
            if (rel.Stance != DiplomaticStance.Allied) continue;
            if (rel.A == player.Id) allySet.Add(rel.B);
            else if (rel.B == player.Id) allySet.Add(rel.A);
        }

        var selectedIds = _selectedArmies.Select(a => a.Id).ToHashSet();
        var inspectedId = _selectedNation?.Id ?? -1;
        var map = _campaign.ProvinceMap;
        if (map is null) return;

        var inputs = new List<MapCounterInput>(_campaign.Armies.Count);
        foreach (var army in _campaign.Armies)
        {
            var (lat, lon) = army.ProvinceId > 0 ? map.CenterOf(army.ProvinceId) : (0f, 0f);
            inputs.Add(new MapCounterInput(
                army.Id,
                army.NationId,
                _campaign.NationById(army.NationId)?.Tag ?? "",
                army.StateMapIndex,
                army.AssignedWarId,
                lat,
                lon,
                army.Divisions,
                army.NationId == player.Id,
                allySet.Contains(army.NationId),
                enemySet.Contains(army.NationId),
                selectedIds.Contains(army.Id),
                army.NationId == inspectedId));
        }

        var alertPinEstimate = _shellAlertRows?.GetChildCount() ?? 0;
        var clusters = MapCounterMerge.Merge(
            inputs,
            tier,
            warMapMode: _activeMapMode == StrategicMapMode.Wars,
            alertPinsCount: Math.Min(4, alertPinEstimate),
            maxSigns: ShellLayout.MaxMapSigns);

        _shellMergedArmyIds.Clear();
        foreach (var cluster in clusters)
            _shellMergedArmyIds.Add(cluster.RepresentativeId);
        _globe.NewShellVisibleArmyIds = _shellMergedArmyIds;
    }
}
