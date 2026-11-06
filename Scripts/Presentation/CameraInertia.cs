using System;

namespace DealOrForce;

/// <summary>Exact exponential coast, independent of render FPS. Units are arbitrary:
/// the map uses world units and the globe uses radians. Input itself stays direct.</summary>
public static class CameraInertia
{
    public const float Damping = 8f;
    public static float Remaining(float velocity, float seconds) => velocity * MathF.Exp(-Damping * Math.Clamp(seconds, 0, .1f));
    public static float Travel(float velocity, float seconds) => (velocity - Remaining(velocity, seconds)) / Damping;
}
