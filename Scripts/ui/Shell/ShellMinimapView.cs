using System;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Live 256x128 political/land/sea corner minimap extracted from <see cref="ShellMapBarView"/>
/// with Sovereign Brass framing, click/drag camera panning, and zero raw Color literals.
/// </summary>
internal sealed class ShellMinimapView
{
    private const int MinimapW = 256, MinimapH = 128;
    private const ulong PanRedrawIntervalMsec = 60;
    private const float ActiveMoveDeg = 0.8f;
    private const float SettleEpsilonDeg = 0.08f;

    private readonly ShellContext _ctx;
    private readonly bool _isMobileRuntime = OS.HasFeature("mobile") || OS.GetName() is "Android" or "iOS";

    private PanelContainer _panel;
    private Control _canvas;
    private ImageTexture _texture;
    private Campaign _world;
    private int _revision = int.MinValue;
    private bool _dragging;
    private ulong _lastBuildMsec;
    private ulong _lastViewRedrawMsec;
    private (float Lat, float Lon, float Span) _lastDrawnCamView;
    private (float Lat, float Lon, float Span) _lastSeenCamView;
    private bool _hasDrawnCamView;
    private bool _pendingExactRedraw;
    private readonly int[] _ownerGrid = new int[MinimapW * MinimapH];
    private readonly byte[] _bytes = new byte[MinimapW * MinimapH * 4];
    private byte[] _palR = Array.Empty<byte>(), _palG = Array.Empty<byte>(), _palB = Array.Empty<byte>();
    private bool[] _palHas = Array.Empty<bool>();

    public bool IsVisible => _panel is { Visible: true };

    public ShellMinimapView(ShellContext ctx)
    {
        _ctx = ctx;
    }

    public void Build()
    {
        _panel = new PanelContainer
        {
            Name = "ShellMinimap",
            Visible = false,
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _panel.AddThemeStyleboxOverride("panel",
            _ctx.NineSlice("plate_brass_9s", 10, 4, 4, 4, 4, "shell.well_top", "shell.brass", 2));
        _ctx.Root.AddChild(_panel);
        ChromePlateArt.AttachSurfaceTexture(_panel, "blueprint", 0.20f);

        _canvas = new Control
        {
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        _canvas.Draw += DrawMinimap;
        _canvas.GuiInput += OnMinimapInput;
        _panel.AddChild(_canvas);
        _panel.TooltipText = Ui.Tip(Loc.Get("Click on the minimap to jump the camera there."));
        _ctx.AttachPlateFrame(_panel, chamfer: 4.0f);
    }

    public void Layout(ShellLayoutProfile profile)
    {
        if (_panel is null) return;
        ShellContext.PlaceRect(_panel, ShellLayout.MinimapRect(profile));
    }

    public void SetVisible(bool visible)
    {
        if (_panel is null) return;
        _panel.Visible = visible;
        if (visible)
        {
            _pendingExactRedraw = true;
            _canvas?.QueueRedraw();
        }
    }

    public void Tick(bool allowTextureRebuild = true)
    {
        if (_panel is not { Visible: true }) return;
        EnsureTextureCurrent(allowTextureRebuild);
        var cam = _ctx.GetCameraMinimapView?.Invoke() ?? (20f, 0f, 60f);
        if (!_hasDrawnCamView)
        {
            QueueCameraViewRedraw(cam, Time.GetTicksMsec());
            return;
        }
        var moving = CamDiffers(cam, _lastSeenCamView, SettleEpsilonDeg);
        _lastSeenCamView = cam;
        if (moving)
        {
            _pendingExactRedraw = true;
            var now = Time.GetTicksMsec();
            if (now - _lastViewRedrawMsec >= PanRedrawIntervalMsec &&
                CamDiffers(cam, _lastDrawnCamView, ActiveMoveDeg))
                QueueCameraViewRedraw(cam, now);
            return;
        }
        if (_pendingExactRedraw && CamDiffers(cam, _lastDrawnCamView, SettleEpsilonDeg))
            QueueCameraViewRedraw(cam, Time.GetTicksMsec());
        else
            _pendingExactRedraw = false;
    }

    private void QueueCameraViewRedraw((float Lat, float Lon, float Span) cam, ulong nowMsec)
    {
        _lastDrawnCamView = cam;
        _lastSeenCamView = cam;
        _hasDrawnCamView = true;
        _pendingExactRedraw = false;
        _lastViewRedrawMsec = nowMsec;
        _canvas?.QueueRedraw();
    }

    private static bool CamDiffers((float Lat, float Lon, float Span) a, (float Lat, float Lon, float Span) b, float eps) =>
        MathF.Abs(a.Lat - b.Lat) > eps || MathF.Abs(a.Lon - b.Lon) > eps || MathF.Abs(a.Span - b.Span) > eps;

    private void EnsureTextureCurrent(bool allowTextureRebuild = true)
    {
        var world = _ctx.World;
        if (world?.ProvinceMap is not { Width: > 0, Height: > 0 } map) return;
        var snap = world.RenderSnapshot is { Version: > 0 } rs ? rs : null;
        var curRev = snap?.MapRevision ?? world.MapRevision;
        if (ReferenceEquals(_world, world) && _revision == curRev && _texture is not null)
            return;

        if (!allowTextureRebuild && _texture is not null)
            return;

        var paused = snap?.IsPaused ?? (_ctx.IsPaused?.Invoke() ?? true);
        if (!paused && _texture is not null && ReferenceEquals(_world, world) &&
            Time.GetTicksMsec() - _lastBuildMsec < (ulong)(_isMobileRuntime ? 1200 : 450))
            return;

        var needsWorldRead = snap is null || snap.ProvinceController is null;
        var readLocked = false;
        if (needsWorldRead)
        {
            if (!world.TryEnterRead(0, false)) return;
            readLocked = true;
            curRev = snap?.MapRevision ?? world.MapRevision;
        }

        try
        {
            _world = world;
            _revision = curRev;
            _lastBuildMsec = Time.GetTicksMsec();

            var maxIdx = 1;
            if (snap is not null)
            {
                var snapNations = snap.Nations;
                for (var i = 0; i < snapNations.Length; i++)
                    if (snapNations[i].MapIndex > maxIdx) maxIdx = snapNations[i].MapIndex;
            }
            else
            {
                for (var i = 0; i < world.Nations.Count; i++)
                    if (world.Nations[i].MapIndex > maxIdx) maxIdx = world.Nations[i].MapIndex;
            }

            var palLen = maxIdx + 1;
            if (_palR.Length < palLen)
            {
                _palR = new byte[palLen]; _palG = new byte[palLen];
                _palB = new byte[palLen]; _palHas = new bool[palLen];
            }
            else Array.Clear(_palHas, 0, palLen);

            var fallbackNeutral = Ui.Colour("shell.paper_ghost");
            var muteTarget = Ui.Colour("shell.plate_bottom");
            var playerMapIdx = snap?.PlayerMapIndex ?? (world.Player?.MapIndex ?? -1);
            if (snap is not null)
            {
                var snapNations = snap.Nations;
                for (var i = 0; i < snapNations.Length; i++)
                {
                    var n = snapNations[i];
                    if (n.MapIndex <= 0 || n.MapIndex > maxIdx) continue;
                    var col = Color.FromString(string.IsNullOrEmpty(n.Color) ? "#6e6c66" : n.Color, fallbackNeutral);
                    col = n.MapIndex == playerMapIdx ? col.Lightened(0.14f) : col.Lerp(muteTarget, 0.22f);
                    _palR[n.MapIndex] = (byte)Math.Clamp((int)(col.R * 255f), 0, 255);
                    _palG[n.MapIndex] = (byte)Math.Clamp((int)(col.G * 255f), 0, 255);
                    _palB[n.MapIndex] = (byte)Math.Clamp((int)(col.B * 255f), 0, 255);
                    _palHas[n.MapIndex] = true;
                }
            }
            else
            {
                for (var i = 0; i < world.Nations.Count; i++)
                {
                    var n = world.Nations[i];
                    if (n.MapIndex <= 0 || n.MapIndex > maxIdx) continue;
                    var col = Color.FromString(string.IsNullOrEmpty(n.Color) ? "#6e6c66" : n.Color, fallbackNeutral);
                    col = n.MapIndex == playerMapIdx ? col.Lightened(0.14f) : col.Lerp(muteTarget, 0.22f);
                    _palR[n.MapIndex] = (byte)Math.Clamp((int)(col.R * 255f), 0, 255);
                    _palG[n.MapIndex] = (byte)Math.Clamp((int)(col.G * 255f), 0, 255);
                    _palB[n.MapIndex] = (byte)Math.Clamp((int)(col.B * 255f), 0, 255);
                    _palHas[n.MapIndex] = true;
                }
            }

            var cells = map.CellToProvince;
            var controllers = snap?.ProvinceController ?? world.ProvinceController;
            var owners = snap?.ProvinceOwner is { Length: > 0 } snapOwners ? snapOwners : map.ProvinceToNation;
            for (var y = 0; y < MinimapH; y++)
            {
                var sy = Math.Clamp((int)((y + 0.5f) / MinimapH * map.Height), 0, map.Height - 1);
                var rowOff = sy * map.Width;
                for (var x = 0; x < MinimapW; x++)
                {
                    var sx = Math.Clamp((int)((x + 0.5f) / MinimapW * map.Width), 0, map.Width - 1);
                    var prov = cells[rowOff + sx];
                    if (prov <= 0 || prov > map.ProvinceCount || map.IsWater(prov)) { _ownerGrid[y * MinimapW + x] = 0; continue; }
                    var ctrl = controllers is not null && prov < controllers.Length && controllers[prov] > 0
                        ? controllers[prov] : (prov < owners.Length ? owners[prov] : 0);
                    _ownerGrid[y * MinimapW + x] = ctrl > 0 && ctrl <= maxIdx ? ctrl : -1;
                }
            }
        }
        finally
        {
            if (readLocked) world.ExitRead();
        }

        for (var y = 0; y < MinimapH; y++)
        {
            for (var x = 0; x < MinimapW; x++)
            {
                var idx = y * MinimapW + x;
                var o = _ownerGrid[idx];
                var bOff = idx * 4;
                if (o == 0)
                {
                    var latShade = 1f - 0.18f * MathF.Abs((y / (float)MinimapH) * 2f - 1f);
                    _bytes[bOff] = (byte)(17 * latShade); _bytes[bOff + 1] = (byte)(24 * latShade);
                    _bytes[bOff + 2] = (byte)(32 * latShade); _bytes[bOff + 3] = 255;
                    continue;
                }
                var has = o > 0 && o < _palHas.Length && _palHas[o];
                byte r = has ? _palR[o] : (byte)86, g = has ? _palG[o] : (byte)85, b = has ? _palB[o] : (byte)82;
                if ((x > 0 && _ownerGrid[idx - 1] != o) || (y > 0 && _ownerGrid[idx - MinimapW] != o))
                { r = (byte)(r * 0.52f); g = (byte)(g * 0.52f); b = (byte)(b * 0.52f); }
                _bytes[bOff] = r; _bytes[bOff + 1] = g; _bytes[bOff + 2] = b; _bytes[bOff + 3] = 255;
            }
        }

        var img = Image.CreateFromData(MinimapW, MinimapH, false, Image.Format.Rgba8, _bytes);
        if (_texture is null) _texture = ImageTexture.CreateFromImage(img);
        else _texture.Update(img);
        _canvas?.QueueRedraw();
    }

    private void DrawMinimap()
    {
        var w = _canvas.Size.X;
        var h = _canvas.Size.Y;
        if (w <= 4f || h <= 4f) return;

        EnsureTextureCurrent();
        if (_texture is not null) _canvas.DrawTextureRect(_texture, new Rect2(0, 0, w, h), tile: false);
        else _canvas.DrawRect(new Rect2(0, 0, w, h), Ui.Colour("shell.well"));

        var gridCol = Ui.Colour("shell.paper", 0.08f);
        _canvas.DrawLine(new Vector2(0, h * 0.5f), new Vector2(w, h * 0.5f), gridCol, 1f);
        _canvas.DrawLine(new Vector2(w * 0.5f, 0), new Vector2(w * 0.5f, h), gridCol, 1f);

        var cam = _ctx.GetCameraMinimapView?.Invoke() ?? (20f, 0f, 60f);
        _lastDrawnCamView = cam;
        _hasDrawnCamView = true;
        var (lat, lon, span) = cam;
        var cx = ((lon + 180f) / 360f) * w;
        var cy = ((90f - lat) / 180f) * h;
        var vw = Math.Clamp((span / 360f) * w * 1.6f, 14f, w - 4f);
        var vh = Math.Clamp((span / 180f) * h, 10f, h - 4f);
        var rx = Math.Clamp(cx - vw * 0.5f, 1f, Math.Max(1f, w - vw - 1f));
        var ry = Math.Clamp(cy - vh * 0.5f, 1f, Math.Max(1f, h - vh - 1f));

        _canvas.DrawRect(new Rect2(rx - 1f, ry - 1f, vw + 2f, vh + 2f), Ui.Colour("shell.border", 0.80f), filled: false, width: 1.5f);
        _canvas.DrawRect(new Rect2(rx, ry, vw, vh), Ui.Colour("shell.brass_hi"), filled: false, width: 1.5f);
    }

    private void OnMinimapInput(InputEvent ev)
    {
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            _dragging = mb.Pressed;
            if (mb.Pressed) PanCameraFromPoint(mb.Position, immediate: true);
            else _pendingExactRedraw = true;
            _canvas.AcceptEvent();
        }
        else if (ev is InputEventMouseMotion mm && _dragging)
        {
            if ((mm.ButtonMask & MouseButtonMask.Left) == 0)
            {
                _dragging = false;
                _pendingExactRedraw = true;
                return;
            }
            PanCameraFromPoint(mm.Position, immediate: false);
            _canvas.AcceptEvent();
        }
    }

    private void PanCameraFromPoint(Vector2 localPos, bool immediate)
    {
        var w = MathF.Max(1f, _canvas.Size.X);
        var h = MathF.Max(1f, _canvas.Size.Y);
        var lon = Math.Clamp((localPos.X / w) * 360f - 180f, -180f, 180f);
        var lat = Math.Clamp(90f - (localPos.Y / h) * 180f, -78f, 78f);
        _ctx.FocusLatLon?.Invoke(lat, lon, Math.Clamp(_ctx.GetViewDistance?.Invoke() ?? 6.5f, 3.2f, 12f));
        _pendingExactRedraw = true;
        var now = Time.GetTicksMsec();
        if (immediate || !_hasDrawnCamView || now - _lastViewRedrawMsec >= PanRedrawIntervalMsec)
        {
            _lastViewRedrawMsec = now;
            _canvas?.QueueRedraw();
        }
    }
}
