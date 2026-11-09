namespace DealOrForce;

public sealed partial class GlobePresentation
{
    /// <summary>Whether the map has been built and applied: until then frame times are loading, not play.</summary>
    public bool MapReady => _mapApplied;
}
