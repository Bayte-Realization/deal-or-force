using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    /// <summary>Read the physical map without selecting it, issuing orders or changing its tint.</summary>
    internal bool TryConstructionPreview(Vector2 viewportPosition, out GlobePick pick) =>
        TryPick(GetGlobalTransformWithCanvas().AffineInverse() * viewportPosition, out pick, updateSelection: false);
}
