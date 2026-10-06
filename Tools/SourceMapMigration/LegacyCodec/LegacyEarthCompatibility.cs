using System;

namespace Globality; // Offline migration codec; never included in the game assembly.

/// <summary>
/// Godot compatibility marker for data copied verbatim from the s&box source.
/// The attribute carried hot-reload intent in s&box; in Godot it is deliberately inert.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class SkipHotloadAttribute : Attribute
{
}
