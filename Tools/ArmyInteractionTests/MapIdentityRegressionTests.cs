using System;
using DealOrForce.Simulation;

/// <summary>Real organisations replace four unnamed coalitions without invalidating mod IDs.</summary>
static class MapIdentityRegressionTests
{
    public static void Run()
    {
        MovementCatalog.ResetCatalog();
        foreach (var (id, tag, name) in new[]
        {
            ("scottish_civic_campaign", "SCV", "Scottish National Party"),
            ("welsh_civic_campaign", "WCV", "Plaid Cymru"),
            ("quebec_civic_campaign", "QCV", "Parti Québécois"),
            ("bavarian_civic_voice", "BVC", "Bavaria Party")
        })
        {
            var definition = MovementCatalog.Of(id);
            if (definition is null || definition.Name != name || definition.Tag != tag)
                throw new Exception("Real movement identity missing or old mod identity broken: " + id);
            if (!definition.Has("civic_independence") || !definition.Has("nonviolence") || definition.CanStageUprising)
                throw new Exception("A constitutional party was turned into an automatic armed uprising: " + id);
            if (definition.Peoples.Count == 0 || definition.Homeland.Count == 0 || definition.Description.Length < 60)
                throw new Exception("Real organisation has no geographical/political explanation: " + id);
            Console.WriteLine("PASS map identity: " + name + " keeps its mod ID and peaceful regional role");
        }
    }
}
