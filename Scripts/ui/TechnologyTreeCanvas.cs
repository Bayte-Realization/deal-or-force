using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>Connectors live behind interactive cards, in the same scroll coordinate space.</summary>
public partial class TechnologyTreeCanvas : Control
{
    public readonly List<(Vector2 From, Vector2 To, Color Colour)> Edges = new();
    public override void _Draw()
    {
        foreach (var (from, to, colour) in Edges)
        {
            var middle = (from.X + to.X) * .5f;
            DrawLine(from, new(middle, from.Y), colour, 2, true);
            DrawLine(new(middle, from.Y), new(middle, to.Y), colour, 2, true);
            DrawLine(new(middle, to.Y), to, colour, 2, true);
            DrawLine(to - new Vector2(6, 4), to, colour, 2, true);
            DrawLine(to - new Vector2(6, -4), to, colour, 2, true);
        }
    }
}
