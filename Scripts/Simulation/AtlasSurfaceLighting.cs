using System;

namespace DealOrForce.Simulation;

/// <summary>Presentation-only, deterministic lighting; never consumes a campaign random stream.</summary>
public static class AtlasSurfaceLighting
{
    public static float Relief(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : .65f;
    public static float GrainAmount(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, .05f) : .018f;

    public static float Light(float u, float v, bool pressed = false)
    {
        u = Math.Clamp(u, 0, 1); v = Math.Clamp(v, 0, 1);
        // One light source, upper left. Pressing reverses the bevel, not the whole palette.
        return pressed ? -.045f + .055f * v : .12f * (1 - v) + .025f * (1 - u) - .055f * v;
    }

    public static float Grain(int x, int y)
    {
        var hash = unchecked((uint)x * 374761393u + (uint)y * 668265263u + 1274126177u);
        hash = unchecked((hash ^ (hash >> 13)) * 1274126177u);
        return ((hash ^ (hash >> 16)) & 65535) / 65535f * 2 - 1;
    }
}
