using System;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Political raster view: samples the same province IDs for drawing and picking.</summary>
public partial class ScenarioMapCanvas : Control
{
    public ScenarioMapDraft Draft;
    public ProvinceTopology Topology;
    public Action<int> Picked;
    public Action Changed;
    public int TargetOwner, TargetRegion;
    public bool PaintMode;
    public int SelectedProvince { get; private set; }
    private bool _painting, _pan, _dirty = true;
    private float _zoom = 1;
    private Vector2 _center = new(.5f, .5f), _last;
    private ImageTexture _texture;
    private Vector2 _renderedSize;
    private Color[] _colors;
    public override void _Ready()
    {
        ClipContents = true; MouseFilter = MouseFilterEnum.Stop; MouseForcePassScrollEvents = false;
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.Cross;
        MouseExited += FinishStroke;
        RefreshColours();
    }
    private void RefreshColours()
    {
        _colors = new Color[(Draft?.Countries.Select(c => c.MapIndex).DefaultIfEmpty(0).Max() ?? 0) + 1];
        if (Draft is not null) foreach (var c in Draft.Countries) _colors[c.MapIndex] = Color.FromString(c.Color, Colors.Gray);
    }
    private float MapScale => Math.Max(.001f, Math.Min(Size.X / Topology.Width, Size.Y / Topology.Height) * _zoom);
    private Vector2 Source(Vector2 local) => (local - Size / 2) / MapScale + _center * new Vector2(Topology.Width, Topology.Height);
    public int ProvinceAt(Vector2 local)
    {
        var s = Source(local); var x = (int)MathF.Floor(s.X); var y = (int)MathF.Floor(s.Y);
        return x < 0 || y < 0 || x >= Topology.Width || y >= Topology.Height ? 0 : Topology.CellToProvince[y * Topology.Width + x];
    }
    public void InvalidateMap() { RefreshColours(); _dirty = true; }
    public void Fit() { FinishStroke(); _zoom = 1; _center = new(.5f, .5f); _dirty = true; }
    public void FocusCountry(int owner)
    {
        FinishStroke();
        var provinces = Enumerable.Range(1, Draft.ProvinceCount).Where(p => Draft.OwnerAt(p) == owner).ToArray();
        if (provinces.Length == 0) return;
        // Select a real province, not an average that can land between distant overseas territories.
        var cell = Topology.ProvinceCenterCell[provinces[provinces.Length / 2]];
        _center = new((cell % Topology.Width + .5f) / Topology.Width, (cell / Topology.Width + .5f) / Topology.Height);
        _zoom = 5; _dirty = true;
    }
    public void FinishStroke()
    {
        _pan = false;
        if (!_painting) { _pan = false; return; }
        _painting = false; Draft.EndStroke(); Changed?.Invoke();
    }
    public bool CancelGesture()
    {
        if (!_painting && !_pan) return false;
        _painting = _pan = false; Draft.CancelStroke(); InvalidateMap(); Changed?.Invoke(); return true;
    }
    private void Paint(Vector2 pos)
    {
        var p = ProvinceAt(pos);
        if (p <= 0 || Topology.WaterByProvince[p] || TargetRegion == 0) return;
        if (Draft.Paint(p, TargetOwner, TargetRegion)) _dirty = true;
    }
    public override void _GuiInput(InputEvent input)
    {
        if (Draft is null || Topology is null) return;
        if (input is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.Escape && CancelGesture()) AcceptEvent();
            return;
        }
        if (input is InputEventMouseButton b)
        {
            if (b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown && b.Pressed)
            {
                FinishStroke(); var before = Source(b.Position);
                _zoom = Math.Clamp(_zoom * (b.ButtonIndex == MouseButton.WheelUp ? 1.25f : .8f), 1, 40);
                _center += (before - Source(b.Position)) / new Vector2(Topology.Width, Topology.Height); _dirty = true;
            }
            else if (b.ButtonIndex == MouseButton.Middle) { FinishStroke(); _pan = b.Pressed; }
            else if (b.ButtonIndex == MouseButton.Left)
            {
                GrabFocus();
                if (!b.Pressed) FinishStroke();
                else if (PaintMode) { _painting = true; Paint(b.Position); }
                else { SelectedProvince = ProvinceAt(b.Position); _dirty = true; Picked?.Invoke(SelectedProvince); }
                _last = b.Position;
            }
            AcceptEvent();
        }
        if (input is InputEventMouseMotion m)
        {
            if (_pan) { _center -= m.Relative / MapScale / new Vector2(Topology.Width, Topology.Height); _dirty = true; }
            if (_painting)
            {
                var steps = Math.Clamp((int)MathF.Ceiling(_last.DistanceTo(m.Position) / 3), 1, 512);
                for (var i = 1; i <= steps; i++) Paint(_last.Lerp(m.Position, (float)i / steps));
            }
            _last = m.Position; AcceptEvent();
        }
    }
    public override void _Process(double delta)
    {
        if (Draft is null || Topology is null || Size.X < 1 || Size.Y < 1) return;
        if (!_dirty && _renderedSize == Size) return;
        _dirty = false; _renderedSize = Size;
        var w = Math.Clamp((int)Size.X, 1, 1400); var h = Math.Clamp((int)Size.Y, 1, 900);
        var bytes = new byte[w * h * 4];
        var start = Source(Vector2.Zero); var dx = Size.X / w / MapScale; var dy = Size.Y / h / MapScale;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var sx = (int)MathF.Floor(start.X + x * dx); var sy = (int)MathF.Floor(start.Y + y * dy);
            var color = new Color(.055f, .105f, .15f);
            if (sx >= 0 && sy >= 0 && sx < Topology.Width && sy < Topology.Height)
            {
                var p = Topology.CellToProvince[sy * Topology.Width + sx]; var owner = Draft.OwnerAt(p);
                if (p > 0 && !Topology.WaterByProvince[p])
                {
                    color = owner > 0 && owner < _colors.Length ? _colors[owner] : new Color(.3f, .33f, .35f);
                    var nx = Math.Min(Topology.Width - 1, sx + Math.Max(1, (int)dx));
                    var ny = Math.Min(Topology.Height - 1, sy + Math.Max(1, (int)dy));
                    var right = Topology.CellToProvince[sy * Topology.Width + nx];
                    var down = Topology.CellToProvince[ny * Topology.Width + sx];
                    if (owner != Draft.OwnerAt(right) || owner != Draft.OwnerAt(down)) color = new Color(.86f, .86f, .75f);
                    else if (_zoom >= 3 && (p != right || p != down)) color = color.Darkened(.65f);
                    if (p == SelectedProvince) color = color.Lerp(new Color(1, .82f, .4f), .4f);
                }
            }
            var offset = (y * w + x) * 4;
            bytes[offset] = (byte)(color.R * 255); bytes[offset + 1] = (byte)(color.G * 255); bytes[offset + 2] = (byte)(color.B * 255); bytes[offset + 3] = 255;
        }
        using var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes);
        if (_texture is null || _texture.GetWidth() != w || _texture.GetHeight() != h)
        { _texture?.Dispose(); _texture = ImageTexture.CreateFromImage(img); }
        else _texture.Update(img);
        QueueRedraw();
    }
    public override void _Draw() { if (_texture is not null) DrawTextureRect(_texture, new Rect2(Vector2.Zero, Size), false); }
    public override void _ExitTree() { _texture?.Dispose(); }
}
