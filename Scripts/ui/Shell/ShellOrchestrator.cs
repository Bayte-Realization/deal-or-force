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
    public ShellTouchModeBarView TouchModeBar { get; }
    public ShellMessageModalView Messages { get; }
    public ShellAlertColumnView Alerts { get; }
    public ShellMapBarView MapBar { get; }
    public ShellStripView Strip { get; }
    public ShellWorkPanelView WorkPanel { get; }
    public ShellEconomyWorkspaceView EconomyWorkspace { get; }
    public GrandProjectsView GrandProjects { get; }

    private long _refreshHour = long.MinValue;
    private int _refreshDay = int.MinValue;
    private int _refreshRevision = int.MinValue;
    private int _refreshWarRevision = int.MinValue;
    private int _refreshPlayerId = int.MinValue;
    private int _refreshObservedId = int.MinValue;
    private bool _refreshObserverMode;
    private ulong _lastHeavyRefreshMsec;
    private ulong _lastResourceRefreshMsec;
    private Campaign _lastWorld;
    private bool _lastPaused;
    private int _lastSpeed = -1;
    private bool _lastAlt;
    private bool _modalObscured;
    private readonly bool _isMobileRuntime = OS.HasFeature("mobile") || OS.GetName() is "Android" or "iOS";

    [Flags]
    private enum PendingView
    {
        None = 0,
        Corner = 1 << 0,
        Alerts = 1 << 1,
        Strip = 1 << 2,
        WorkPanel = 1 << 3,
        Economy = 1 << 4,
        GrandProjects = 1 << 5,
        AllHeavy = Corner | Alerts | Strip | WorkPanel | Economy | GrandProjects
    }

    private PendingView _pendingHeavy;
    private ulong _heavyFrameStamp = ulong.MaxValue;

    private float _cachedProfileW = -1f;
    private float _cachedProfileH = -1f;
    private float _cachedSafeL, _cachedSafeT, _cachedSafeR, _cachedSafeB;
    private bool _cachedProfileWar;
    private ShellLayoutProfile _cachedProfile;

    public double LastRefreshMs { get; private set; }

    public bool HasTransientPopover =>
        Messages.IsVisible || MapBar.IsPopoverOpen || EconomyWorkspace.IsVisible || GrandProjects.IsVisible;

    public ShellOrchestrator(ShellContext ctx)
    {
        Context = ctx;
        InterfaceSkin.Changed += () => { Context.ClearCaches(); _cachedProfile = null; };
        Messages = new ShellMessageModalView(ctx);
        Corner = new ShellCornerView(ctx);
        DatePlate = new ShellDatePlateView(ctx, () => Messages.Toggle());
        TouchModeBar = new ShellTouchModeBarView(ctx);
        Alerts = new ShellAlertColumnView(ctx, Messages);
        MapBar = new ShellMapBarView(ctx);
        Strip = new ShellStripView(ctx);
        WorkPanel = new ShellWorkPanelView(ctx);
        EconomyWorkspace = new ShellEconomyWorkspaceView(ctx);
        GrandProjects = new GrandProjectsView(ctx);
    }

    public void Build()
    {
        Corner.Build();
        DatePlate.Build();
        TouchModeBar.Build();
        Alerts.Build();
        MapBar.Build();
        Strip.Build();
        WorkPanel.Build();
        EconomyWorkspace.Build();
        GrandProjects.Build();
    }

    public void ResetCadence()
    {
        _refreshHour = long.MinValue;
        _refreshDay = int.MinValue;
        _refreshRevision = int.MinValue;
        _refreshWarRevision = int.MinValue;
        _refreshPlayerId = int.MinValue;
        _refreshObservedId = int.MinValue;
        _refreshObserverMode = false;
        _lastHeavyRefreshMsec = 0;
        _lastResourceRefreshMsec = 0;
        _cachedProfile = null;
        _pendingHeavy = PendingView.None;
    }

    public void MarkInboxAndAlertsDirty() => _pendingHeavy |= PendingView.Corner | PendingView.Alerts;

    public bool NeedsLiveCampaignRead(CampaignHudSnapshot hud)
    {
        if (hud is null || hud.Version <= 0) return true;
        if (Alerts.NeedsNewsRead(hud.ChronicleLatestSeq, Context.World)) return true;
        if ((_pendingHeavy & (PendingView.Alerts | PendingView.WorkPanel | PendingView.Economy | PendingView.GrandProjects)) != 0)
        {
            if (((_pendingHeavy & PendingView.Alerts) != 0 && !_modalObscured) ||
                ((_pendingHeavy & PendingView.WorkPanel) != 0 && WorkPanel.IsVisible) ||
                ((_pendingHeavy & PendingView.Economy) != 0 && EconomyWorkspace.IsVisible) ||
                ((_pendingHeavy & PendingView.GrandProjects) != 0 && GrandProjects.IsVisible))
                return true;
        }
        return false;
    }

    public void SetModalObscured(bool obscured)
    {
        if (_modalObscured == obscured) return;
        _modalObscured = obscured;
        TouchModeBar.SetModalObscured(obscured);
        Alerts.SetModalObscured(obscured);
        MapBar.SetModalObscured(obscured);
        if (obscured) Messages.Close();
    }

    private bool IsPlayerAtWar(Campaign world)
    {
        if (Context.HudSnapshot is { Version: > 0 } hs)
            return hs.OpenPlayerWars.Length > 0;
        if (world?.Player is not { } player) return false;
        var open = Warfare.OpenWars(world);
        for (var i = 0; i < open.Count; i++)
        {
            var w = open[i];
            if (!w.Ended && w.Involves(player.Id)) return true;
        }
        return false;
    }

    public ShellLayoutProfile CurrentProfile()
    {
        var vp = Context.GetViewportSize();
        var atWar = IsPlayerAtWar(Context.World);
        var touchMode = Context.IsTouchMode?.Invoke() ?? (DealOrForce.Platform.PlatformProfile.Current?.IsTouchPrimary == true);
        var sa = touchMode ? (DealOrForce.Platform.PlatformProfile.Current?.SafeArea ?? default) : default;
        if (_cachedProfile is not null &&
            MathF.Abs(_cachedProfileW - vp.X) < 0.5f &&
            MathF.Abs(_cachedProfileH - vp.Y) < 0.5f &&
            MathF.Abs(_cachedSafeL - sa.Left) < 0.5f && MathF.Abs(_cachedSafeT - sa.Top) < 0.5f &&
            MathF.Abs(_cachedSafeR - sa.Right) < 0.5f && MathF.Abs(_cachedSafeB - sa.Bottom) < 0.5f &&
            _cachedProfileWar == atWar &&
            _cachedProfile.TouchMode == touchMode)
            return _cachedProfile;

        _cachedProfileW = vp.X;
        _cachedProfileH = vp.Y;
        _cachedSafeL = sa.Left; _cachedSafeT = sa.Top; _cachedSafeR = sa.Right; _cachedSafeB = sa.Bottom;
        _cachedProfileWar = atWar;
        _cachedProfile = ShellLayout.ForViewport(vp.X, vp.Y, atWar, touchMode, sa.Left, sa.Top, sa.Right, sa.Bottom);
        return _cachedProfile;
    }

    public void LayoutAll()
    {
        _cachedProfile = null;
        var atWar = IsPlayerAtWar(Context.World);
        var profile = CurrentProfile();
        Corner.Layout(profile, atWar);
        DatePlate.Layout(profile);
        TouchModeBar.Layout(profile);
        Messages.Layout(profile);
        Alerts.Layout(profile);
        MapBar.Layout(profile);
        EconomyWorkspace.Layout(profile);
        GrandProjects.Layout(profile);
    }

    public void Refresh(Campaign world, int speed, bool force)
    {
        if (world is null) return;
        var t0 = Stopwatch.GetTimestamp();

        var worldChanged = !ReferenceEquals(_lastWorld, world);
        if (worldChanged)
        {
            _lastWorld = world;
            ResetCadence();
        }

        var hud = Context.HudSnapshot is { Version: > 0 } hs ? hs : null;
        var isPaused = hud?.IsPaused ?? world.IsPaused;
        var elapsedHours = hud?.ElapsedHours ?? world.ElapsedHours;
        var elapsedDays = hud?.ElapsedDays ?? world.ElapsedDays;
        var mapRev = hud?.MapRevision ?? world.MapRevision;
        var warRev = hud?.WarRevision ?? world.WarRevision;
        var playerId = hud is not null && hud.PlayerId >= 0 ? hud.PlayerId : (world.Player?.Id ?? -1);
        var observerMode = Context.IsObserverMode?.Invoke() ?? (hud?.ObserverMode ?? world.ObserverMode);
        var observedId = Context.GetObservedNation?.Invoke()?.Id ?? -1;

        var pausedChanged = _lastPaused != isPaused;
        var speedChanged = _lastSpeed != speed;
        var hourChanged = _refreshHour != elapsedHours;
        var identityChanged = _refreshPlayerId != playerId || _refreshObserverMode != observerMode || _refreshObservedId != observedId;
        var dayOrRevChanged = _refreshDay != elapsedDays || _refreshRevision != mapRev ||
            _refreshWarRevision != warRev;
        _lastPaused = isPaused;
        _lastSpeed = speed;

        var obscured = Context.IsAnyModalOpen?.Invoke() ?? false;
        if (obscured != _modalObscured)
            SetModalObscured(obscured);

        var nowMsec = Time.GetTicksMsec();
        var fastForward = !isPaused && speed >= 4;
        var minIntervalMsec = (ulong)(_isMobileRuntime ? 350 : fastForward ? 150 : 220);
        var heavyDue = force || worldChanged || identityChanged ||
            (dayOrRevChanged && (isPaused || pausedChanged || nowMsec - _lastHeavyRefreshMsec >= minIntervalMsec));

        // Orders can charge the treasury and reserve without advancing the clock or changing the map.
        // At Speed 4/5, throttle resource-label repaints to ~125ms so DatePlate stays silk-smooth every frame.
        var resourceDue = force || worldChanged || identityChanged || isPaused || pausedChanged ||
            !fastForward || nowMsec - _lastResourceRefreshMsec >= 125;
        if (resourceDue)
        {
            _lastResourceRefreshMsec = nowMsec;
            Corner.RefreshResources(world.Player);
        }
        if (heavyDue)
        {
            _refreshDay = elapsedDays;
            _refreshRevision = mapRev;
            _refreshWarRevision = warRev;
            _refreshPlayerId = playerId;
            _refreshObserverMode = observerMode;
            _refreshObservedId = observedId;
            if (identityChanged) _cachedProfile = null;
            _lastHeavyRefreshMsec = nowMsec;
            _pendingHeavy |= PendingView.AllHeavy;
        }
        if (force || worldChanged || identityChanged || hourChanged || pausedChanged || speedChanged)
        {
            _refreshHour = elapsedHours;
            DatePlate.Refresh();
        }
        if (force || worldChanged || identityChanged || pausedChanged)
        {
            MapBar.Refresh();
            if (_isMobileRuntime || _cachedProfile?.TouchMode == true || TouchModeBar.IsVisible || (Context.IsTouchMode?.Invoke() ?? false))
                TouchModeBar.Refresh();
        }

        // Amortize heavy view updates within a 1.5ms budget (1.25ms mobile); remaining dirty views spill
        // cleanly into consecutive frames via Tick() so day boundaries never spike a single frame.
        var budgetMs = _isMobileRuntime ? 1.25 : 1.50;
        if (DrainPendingHeavy(t0, budgetMs, allowMinOne: true, allowLiveReads: true))
            _heavyFrameStamp = Engine.GetProcessFrames();

        LastRefreshMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    public void Tick(double delta, bool allowLiveReads = true)
    {
        var tickT0 = Stopwatch.GetTimestamp();
        if (!_isMobileRuntime)
        {
            var altNow = Input.IsKeyPressed(Key.Alt);
            if (altNow != _lastAlt)
            {
                _lastAlt = altNow;
                Context.AltKeycapsVisible = altNow;
                Corner.SyncKeycapVisibility();
                DatePlate.Refresh();
                if (allowLiveReads) Alerts.Refresh();
                else _pendingHeavy |= PendingView.Alerts;
                MapBar.Refresh();
            }
        }
        var didHeavyInTick = false;
        if (_pendingHeavy != PendingView.None)
        {
            var budgetMs = _isMobileRuntime ? 1.25 : 1.50;
            didHeavyInTick = DrainPendingHeavy(tickT0, budgetMs, allowMinOne: true, allowLiveReads);
            if (didHeavyInTick) _heavyFrameStamp = Engine.GetProcessFrames();
        }
        if (allowLiveReads)
            Alerts.TickNews(delta);
        else
            Alerts.TickNewsCardOnly(delta);
        MapBar.TickMinimap(allowTextureRebuild: !didHeavyInTick && Engine.GetProcessFrames() != _heavyFrameStamp);
        if (_isMobileRuntime || _cachedProfile?.TouchMode == true || TouchModeBar.IsVisible || (Context.IsTouchMode?.Invoke() ?? false))
            TouchModeBar.Refresh();
        Strip.TickDivisionVisibility(delta, allowLiveReads);
    }

    private bool DrainPendingHeavy(long startTicks, double budgetMs, bool allowMinOne, bool allowLiveReads)
    {
        var didAny = false;
        var deferredLive = PendingView.None;
        while ((_pendingHeavy & ~deferredLive) != PendingView.None)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
            if ((didAny || !allowMinOne) && elapsedMs >= budgetMs)
                break;

            var ready = _pendingHeavy & ~deferredLive;
            if ((ready & PendingView.Corner) != 0)
            {
                _pendingHeavy &= ~PendingView.Corner;
                Corner.Refresh();
                didAny = true;
            }
            else if ((ready & PendingView.Strip) != 0)
            {
                _pendingHeavy &= ~PendingView.Strip;
                Strip.Refresh();
                didAny = true;
            }
            else if ((ready & PendingView.Alerts) != 0)
            {
                if (!allowLiveReads && !_modalObscured) { deferredLive |= PendingView.Alerts; continue; }
                _pendingHeavy &= ~PendingView.Alerts;
                if (!_modalObscured) { Alerts.Refresh(); didAny = true; }
            }
            else if ((ready & PendingView.WorkPanel) != 0)
            {
                if (!allowLiveReads && WorkPanel.IsVisible) { deferredLive |= PendingView.WorkPanel; continue; }
                _pendingHeavy &= ~PendingView.WorkPanel;
                if (WorkPanel.IsVisible) { WorkPanel.Refresh(); didAny = true; }
            }
            else if ((ready & PendingView.Economy) != 0)
            {
                if (!allowLiveReads && EconomyWorkspace.IsVisible) { deferredLive |= PendingView.Economy; continue; }
                _pendingHeavy &= ~PendingView.Economy;
                if (EconomyWorkspace.IsVisible) { EconomyWorkspace.Refresh(); didAny = true; }
            }
            else if ((ready & PendingView.GrandProjects) != 0)
            {
                if (!allowLiveReads && GrandProjects.IsVisible) { deferredLive |= PendingView.GrandProjects; continue; }
                _pendingHeavy &= ~PendingView.GrandProjects;
                if (GrandProjects.IsVisible) { GrandProjects.Refresh(); didAny = true; }
            }
            else
            {
                break;
            }
        }
        return didAny;
    }

    public void CloseTransientPopovers(bool includeNews = true)
    {
        MapBar.ClosePopover();
        Messages.Close();
        if (includeNews) Alerts.DismissNewsCard();
    }

    public void FitWorkspaceLayer(Control layer)
    {
        if (layer is not { Visible: true } || Context.Root is null || !GodotObject.IsInstanceValid(Context.Root)) return;
        var profile = CurrentProfile();
        var toLayer = layer.GetGlobalTransformWithCanvas().AffineInverse() * Context.Root.GetGlobalTransformWithCanvas();
        var wsMin = toLayer * new Vector2(profile.FullWorkspace.X, profile.FullWorkspace.Y);
        var wsMax = toLayer * new Vector2(profile.FullWorkspace.Right, profile.FullWorkspace.Bottom);
        var topSafe = Math.Max(profile.TopPlate.Bottom, Math.Max(profile.DatePlate.Bottom, profile.CornerNode.Bottom));
        var shadeMin = toLayer * new Vector2(profile.LeftRail.Right, topSafe);
        var shadeMax = toLayer * new Vector2(profile.ViewportWidth, profile.Strip.Y - 4f);

        foreach (var child in layer.GetChildren())
        {
            if (child is Control shade && (shade.HasMeta("rail_shade") || (child is ColorRect && child.GetIndex() == 0)))
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
            if (!key.StartsWith("modal.", StringComparison.Ordinal) &&
                key is not ("hud.support_panel" or "hud.peace_panel" or "workspace.situation")) continue;

            var authored = InterfaceSkin.Layout(key);
            var vw = profile.ViewportWidth;
            var vh = profile.ViewportHeight;
            var ax0 = authored.AnchorLeft * vw + authored.OffsetLeft;
            var ay0 = authored.AnchorTop * vh + authored.OffsetTop;
            var ax1 = authored.AnchorRight * vw + authored.OffsetRight;
            var ay1 = authored.AnchorBottom * vh + authored.OffsetBottom;

            var preserveWidth = key is "modal.production" or "hud.support_panel" or "modal.pause" or "modal.event" or "modal.peace_offer" or "modal.handover";
            var x0 = preserveWidth ? Math.Max(profile.FullWorkspace.X, ax0) : profile.FullWorkspace.X;
            var x1 = preserveWidth ? Math.Min(profile.FullWorkspace.Right, x0 + Math.Max(320f, ax1 - ax0)) : profile.FullWorkspace.Right;
            var y0 = preserveWidth ? Math.Max(profile.FullWorkspace.Y, ay0) : profile.FullWorkspace.Y;
            var y1 = preserveWidth ? Math.Min(profile.FullWorkspace.Bottom, Math.Max(y0 + 260f, ay1)) : profile.FullWorkspace.Bottom;

            var p0 = toLayer * new Vector2(x0, y0);
            var p1 = toLayer * new Vector2(x1, y1);
            panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
            panel.OffsetLeft = MathF.Round(preserveWidth ? Math.Min(wsMax.X - 240f, Math.Max(wsMin.X, p0.X)) : wsMin.X);
            panel.OffsetTop = MathF.Round(preserveWidth ? Math.Min(wsMax.Y - 200f, Math.Max(wsMin.Y, p0.Y)) : wsMin.Y);
            panel.OffsetRight = MathF.Round(preserveWidth ? Math.Min(wsMax.X, p1.X) : wsMax.X);
            panel.OffsetBottom = MathF.Round(preserveWidth ? Math.Min(wsMax.Y, p1.Y) : wsMax.Y);
        }
    }

    public static bool HasRealLeaderPortrait(LeaderProfile leader)
    {
        var slug = DealOrForceGame.SlugLeaderName(leader.Name);
        var path = string.IsNullOrEmpty(leader.PortraitId) ? "" : SpriteCatalog.TextureOf(leader.PortraitId);
        if (string.IsNullOrEmpty(path)) path = SpriteCatalog.TextureOf("leader_" + slug);
        if (string.IsNullOrEmpty(path)) path = $"res://game/gfx/leaders/leader_{slug}.png";
        return ResourceLoader.Exists(path) || Godot.FileAccess.FileExists(path.StartsWith("res://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path);
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
                if (world.Nations[i].Population > 0 && world.Nations[i].Gdp > 0f && world.Nations[i].Id != nation.Id && world.Nations[i].Gdp > nation.Gdp)
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
