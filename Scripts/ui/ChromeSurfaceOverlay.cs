using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Edge-to-edge seamlessly tiled surface overlay hosted as a <see cref="Node2D"/> so
/// <see cref="PanelContainer"/> content margins never inset or shrink the material weave.
/// </summary>
public partial class ChromeSurfaceOverlay : Node2D
{
    private Control _host;
    private Texture2D _texture;
    private float _alpha = 0.32f;

    internal void Configure(Texture2D texture, float alpha)
    {
        _texture = texture;
        _alpha = Math.Clamp(alpha, 0f, 1f);
        TextureRepeat = TextureRepeatEnum.Enabled;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        Visible = _texture is not null && _alpha > 0.005f;
        QueueRedraw();
    }

    public override void _EnterTree()
    {
        _host = GetParent() as Control;
        InterfaceSkin.Changed += OnHostChanged;
        if (_host is not null) _host.Resized += OnHostChanged;
    }

    public override void _ExitTree()
    {
        InterfaceSkin.Changed -= OnHostChanged;
        if (GodotObject.IsInstanceValid(_host)) _host.Resized -= OnHostChanged;
    }

    private void OnHostChanged() => QueueRedraw();

    public override void _Draw()
    {
        if (_host is null || _texture is null || _alpha <= 0.005f) return;
        var sz = _host.Size;
        if (sz.X < 4f || sz.Y < 4f) return;
        DrawTextureRectRegion(
            _texture,
            new Rect2(Vector2.Zero, sz),
            new Rect2(Vector2.Zero, sz),
            new Color(1f, 1f, 1f, _alpha));
    }
}
