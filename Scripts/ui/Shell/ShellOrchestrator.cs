using System;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Standalone orchestrator owning all Wave 2 modular campaign shell views (Corner, DatePlate,
/// MessageModal, AlertColumn, MapBar, Strip, WorkPanel, EconomyWorkspace), non-overlapping
/// full-workspace modal fitting, and state-seal leader badges.
/// </summary>
internal sealed class ShellOrchestrator
{
    public ShellContext Context { get; }
    public ShellCornerView Corner { get; }
    public ShellDatePlateView DatePlate { get; }
    public ShellMessageModalView Messages { get; }
    public ShellAlertColumnView Alerts { get; }
    public ShellMapBarView MapBar { get; }
    public ShellStripView Strip { get; }
    public ShellWorkPanelView WorkPanel { get; }
    public ShellEconomyWorkspaceView EconomyWorkspace { get; }

    private long _refreshHour = long.MinValue;
    private int _refreshRevision = int.MinValue;
    private Campaign _lastWorld;
    private bool _lastPaused;
    private int _lastSpeed = -1;
    private bool _lastAlt;

    public double LastRefreshMs { get; private set; }

    public bool HasTransientPopover =>
        Messages.IsVisible || MapBar.IsPopoverOpen || EconomyWorkspace.IsVisible;

    public ShellOrchestrator(ShellContext ctx)
    {
        Context = ctx;
        InterfaceSkin.Changed += () => Context.ClearCaches();
        Messages = new ShellMessageModalView(ctx);
        Corner = new ShellCornerView(ctx);
        DatePlate = new ShellDatePlateView(ctx, () => Messages.Toggle());
        Alerts = new ShellAlertColumnView(ctx, Messages);
        MapBar = new ShellMapBarView(ctx);
        Strip = new ShellStripView(ctx);
        WorkPanel = new ShellWorkPanelView(ctx);
        EconomyWorkspace = new ShellEconomyWorkspaceView(ctx);
    }

    public void Build()
    {
        Corner.Build();
        DatePlate.Build();
        Alerts.Build();
        MapBar.Build();
        Strip.Build();
        WorkPanel.Build();
        EconomyWorkspace.Build();
    }

    public void ResetCadence() => _refreshHour = long.MinValue;

    public ShellLayoutProfile CurrentProfile()
    {
        var vp = Context.GetViewportSize();
        var world = Context.World;
        var atWar = world?.Player is { } player && Warfare.OpenWars(world).Any(w => !w.Ended && w.Involves(player.Id));
        return ShellLayout.ForViewport(vp.X, vp.Y, atWar);
    }

    public void LayoutAll()
    {
        var world = Context.World;
        var atWar = world?.Player is { } player && Warfare.OpenWars(world).Any(w => !w.Ended && w.Involves(player.Id));
        var profile = ShellLayout.ForViewport(Context.GetViewportSize().X, Context.GetViewportSize().Y, atWar);
        Corner.Layout(profile, atWar);
        DatePlate.Layout(profile);
        Messages.Layout(profile);
        Alerts.Layout(profile);
        MapBar.Layout(profile);
        EconomyWorkspace.Layout(profile);
    }

    public void Refresh(Campaign world, int speed, bool force)
    {
        if (world is null) return;
        var t0 = Stopwatch.GetTimestamp();

        var worldChanged = !ReferenceEquals(_lastWorld, world);
        if (worldChanged)
        {
            _lastWorld = world;
            _refreshHour = long.MinValue;
        }

        var pausedChanged = _lastPaused != world.IsPaused;
        var speedChanged = _lastSpeed != speed;
        var stepOrRevChanged = force || worldChanged || _refreshHour != world.ElapsedHours || _refreshRevision != world.MapRevision;
        _lastPaused = world.IsPaused;
        _lastSpeed = speed;

        if (stepOrRevChanged)
        {
            _refreshHour = world.ElapsedHours;
            _refreshRevision = world.MapRevision;
            Corner.Refresh();
            Alerts.Refresh();
            Strip.Refresh();
            WorkPanel.Refresh();
            EconomyWorkspace.Refresh();
        }
        if (stepOrRevChanged || pausedChanged || speedChanged)
            DatePlate.Refresh();

        MapBar.Refresh();
        LastRefreshMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    public void Tick(double delta)
    {
        var altNow = Input.IsKeyPressed(Key.Alt);
        if (altNow != _lastAlt)
        {
            _lastAlt = altNow;
            Context.AltKeycapsVisible = altNow;
            Corner.SyncKeycapVisibility();
            DatePlate.Refresh();
            Alerts.Refresh();
            MapBar.Refresh();
        }
        Alerts.TickNews(delta);
        MapBar.TickMinimap();
        Strip.TickDivisionVisibility(delta);
    }

    public void CloseTransientPopovers()
    {
        MapBar.ClosePopover();
        Messages.Close();
        Alerts.DismissNewsCard();
    }

    public void FitWorkspaceLayer(Control layer)
    {
        if (layer is not { Visible: true } || Context.Root is null || !GodotObject.IsInstanceValid(Context.Root)) return;
        var profile = CurrentProfile();
        var toLayer = layer.GetGlobalTransformWithCanvas().AffineInverse() * Context.Root.GetGlobalTransformWithCanvas();
        var wsMin = toLayer * new Vector2(profile.FullWorkspace.X, profile.FullWorkspace.Y);
        var wsMax = toLayer * new Vector2(profile.FullWorkspace.Right, profile.FullWorkspace.Bottom);
        var shadeMin = toLayer * new Vector2(profile.LeftRail.Right, profile.TopPlate.Bottom);
        var shadeMax = toLayer * new Vector2(profile.ViewportWidth, profile.Strip.Y - 4f);

        foreach (var child in layer.GetChildren())
        {
            if (child is Control shade && shade.HasMeta("rail_shade"))
            {
                shade.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
                shade.OffsetLeft = shadeMin.X;
                shade.OffsetTop = shadeMin.Y;
                shade.OffsetRight = shadeMax.X;
                shade.OffsetBottom = shadeMax.Y;
                continue;
            }
            if (child is not PanelContainer panel || !panel.HasMeta("skin_layout")) continue;
            var key = panel.GetMeta("skin_layout").AsString();
            if (!key.StartsWith("modal.", StringComparison.Ordinal) && key != "hud.support_panel") continue;

            var authored = InterfaceSkin.Layout(key);
            var vw = profile.ViewportWidth;
            var vh = profile.ViewportHeight;
            var ax0 = authored.AnchorLeft * vw + authored.OffsetLeft;
            var ay0 = authored.AnchorTop * vh + authored.OffsetTop;
            var ax1 = authored.AnchorRight * vw + authored.OffsetRight;
            var ay1 = authored.AnchorBottom * vh + authored.OffsetBottom;

            var preserveWidth = key is "modal.production" or "hud.support_panel" or "modal.pause" or "modal.event" or "modal.peace_offer" or "modal.handover";
            var x0 = Math.Max(profile.FullWorkspace.X, ax0);
            var x1 = preserveWidth ? Math.Min(profile.FullWorkspace.Right, x0 + Math.Max(320f, ax1 - ax0)) : Math.Min(profile.FullWorkspace.Right, Math.Max(x0 + 480f, ax1));
            var y0 = Math.Max(profile.FullWorkspace.Y, ay0);
            var y1 = Math.Min(profile.FullWorkspace.Bottom, Math.Max(y0 + 260f, ay1));

            var p0 = toLayer * new Vector2(x0, y0);
            var p1 = toLayer * new Vector2(x1, y1);
            panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            panel.OffsetLeft = MathF.Round(Math.Min(wsMax.X - 240f, Math.Max(wsMin.X, p0.X)));
            panel.OffsetTop = MathF.Round(Math.Min(wsMax.Y - 200f, Math.Max(wsMin.Y, p0.Y)));
            panel.OffsetRight = MathF.Round(Math.Min(wsMax.X, p1.X));
            panel.OffsetBottom = MathF.Round(Math.Min(wsMax.Y, p1.Y));
        }
    }

    public Control BuildStateSealBadge(Campaign world, Nation nation, int width, int height)
    {
        var frame = new PanelContainer
        {
            CustomMinimumSize = new Vector2(width, height),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        frame.AddThemeStyleboxOverride("panel",
            Context.NineSlice("well_recessed_9s", 8, 4, 4, 4, 4, "shell.well_top", "shell.brass_dim", 3));

        var col = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        col.AddThemeConstantOverride("separation", 2);
        frame.AddChild(col);

        var seal = new TextureRect
        {
            Texture = Context.Icon("icon_laws"),
            CustomMinimumSize = new Vector2(Math.Max(18, width - 22), Math.Max(18, width - 22)),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ShellContext.ApplyIconRelief(seal, Ui.Colour("shell.icon"), 0.55f);
        col.AddChild(seal);

        var rank = 1;
        if (world is not null && nation is not null)
        {
            for (var i = 0; i < world.Nations.Count; i++)
                if (world.Nations[i].Exists && world.Nations[i].Id != nation.Id && world.Nations[i].Gdp > nation.Gdp)
                    rank++;
        }
        var rankLbl = new Label
        {
            Text = $"№ {rank}",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        rankLbl.AddThemeFontOverride("font", Context.Font("shell.sans", tabular: true));
        rankLbl.AddThemeFontSizeOverride("font_size", 10);
        rankLbl.AddThemeColorOverride("font_color", Ui.Colour("shell.paper_muted"));
        col.AddChild(rankLbl);
        return frame;
    }
}
