using Godot;

namespace DealOrForce
{
/// <summary>
/// Which of the two maps a game has.
///
/// The globe and the flat map are both built every time, and a player switches between them
/// with a button or F2. A mod - or a scenario that only makes sense on one of them - can take
/// either away in common/defines (GlobeMapEnabled, FlatMapEnabled): the campaign then opens on
/// the one that is left, will not switch to the other, and the switch is not shown. With both
/// turned off the globe stays, because a game with no map is not a choice anyone made.
/// </summary>
public sealed partial class GlobePresentation
{
    private int _allowedProjections = -1;

    /// <summary>Whether this map view exists in this game.</summary>
    public bool ProjectionAllowed(bool flat)
    {
        var defines = _campaign?.Defines;
        if (defines is null) return true;
        if (!defines.GlobeMapEnabled && !defines.FlatMapEnabled) return !flat;
        return flat ? defines.FlatMapEnabled : defines.GlobeMapEnabled;
    }

    /// <summary>Whether the player has a choice of map at all, and so a switch to show.</summary>
    public bool BothProjections => ProjectionAllowed(true) && ProjectionAllowed(false);

    /// <summary>Once a frame: off the map that does not exist, and the switch told when the choice changes.</summary>
    private void KeepAllowedProjection()
    {
        var allowed = (ProjectionAllowed(false) ? 1 : 0) | (ProjectionAllowed(true) ? 2 : 0);
        if (!ProjectionAllowed(_flatMap)) SetFlatMap(!_flatMap);
        if (allowed == _allowedProjections) return;
        _allowedProjections = allowed;
        ProjectionChanged?.Invoke(_flatMap);
    }
}
}

namespace DealOrForce.Simulation
{
    public sealed partial class Defines
    {
        /// <summary>Whether the game has the 3D globe.</summary>
        public bool GlobeMapEnabled { get; set; } = true;

        /// <summary>Whether the game has the flat map that wraps round.</summary>
        public bool FlatMapEnabled { get; set; } = true;
    }
}
