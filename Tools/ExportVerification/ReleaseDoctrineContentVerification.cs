using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // A release gate, not a new game rule. The vanilla export runs with its own
    // documents profile; extra mod books are allowed, but missing shipped ones are not.
    private void VerifyReleaseDoctrineContent()
    {
        var required = new[]
        {
            "manoeuvre", "fire", "fortress", "levy", "shock", "infiltration",
            "elastic_defence", "dispersed_warfare", "sky", "strategic_air", "air_shield",
            "coast", "sea_denial", "convoy_service", "staff", "mobilisation",
            "mission_command", "joint_logistics"
        };
        var books = TacticalDoctrineCatalog.All.ToArray();
        foreach (var id in required)
        {
            var book = TacticalDoctrineCatalog.Find(id);
            Check(book is not null && book.Nodes.Count >= 16,
                "shipped doctrine has all sixteen articles: " + id);
            if (book is null) continue;
            var advanced = book.Nodes.Where(node => node.Id.StartsWith(id + "_adv_", StringComparison.Ordinal)).ToArray();
            Check(advanced.Length == 8 && advanced.Count(node => node.Excludes.Count > 0) == 4,
                "shipped doctrine includes its advanced choices and continuations: " + id);
        }
        Check(DoctrineSlots.All.All(slot => books.Any(book => book.Slot == slot)),
            "shipped doctrine catalogue covers all four services");
        var advancedCount = books.Sum(book => book.Nodes.Count(node => node.Id.Contains("_adv_", StringComparison.Ordinal)));
        GD.Print($"DOCTRINE_CONTENT books={books.Length} articles={books.Sum(book => book.Nodes.Count)} advanced={advancedCount}");
    }
}
