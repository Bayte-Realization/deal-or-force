namespace Globality; // Offline migration codec; never included in the game assembly.

/// <summary>
/// The s&box map data is copied verbatim. Godot's visual build needs the decoded grids
/// once, then retains only its GPU texture. A later picking system may lazily decode them
/// again, preserving exactness without paying the RAM cost continuously.
/// </summary>
public static partial class EarthData
{
    public static void ReleaseDecodedGrids()
    {
        _control = null;
        _states = null;
        _disputeCells = null;
    }
}
