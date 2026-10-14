using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>A recorded amplitude overview, not a synthetic equalizer or a real-time spectrum.</summary>
public sealed partial class MediaWaveform : Control
{
    private IReadOnlyList<float> _peaks;
    private float _progress;
    public void ShowRecording(IReadOnlyList<float> peaks)
    { _peaks = peaks; _progress = 0; QueueRedraw(); }
    public void ShowProgress(double fraction)
    {
        var p = double.IsFinite(fraction) ? (float)Math.Clamp(fraction, 0, 1) : 0;
        if (MathF.Abs(p - _progress) < .001f) return;
        _progress = p; QueueRedraw();
    }
    public override void _Draw()
    {
        var mid = Size.Y / 2; var width = MathF.Max(0, Size.X - 12);
        DrawLine(new(6, mid), new(Size.X - 6, mid), Ui.Colour("text.caption", .3f), 1);
        if (_peaks is not { Count: > 0 } || width < 1) return;
        var count = Math.Min(_peaks.Count, Math.Max(1, (int)(width / 3)));
        var stride = width / count;
        for (var i = 0; i < count; i++)
        {
            // Collapse columns with their maximum, so narrow layouts do not hide short transients.
            var first = i * _peaks.Count / count; var end = (i + 1) * _peaks.Count / count;
            var peak = 0f; for (var k = first; k < end; k++) peak = MathF.Max(peak, _peaks[k]);
            var height = MathF.Max(1, peak * MathF.Max(1, Size.Y - 20));
            var x = 6 + i * stride;
            DrawRect(new Rect2(x, mid - height / 2, MathF.Max(1, stride - 1.5f), height),
                Ui.Colour((i + .5f) / count <= _progress ? "text.accent" : "text.caption", .85f));
        }
        var playhead = 6 + width * _progress;
        DrawLine(new(playhead, 8), new(playhead, Size.Y - 8), Ui.Colour("text.bright", .8f), 1);
    }
}
