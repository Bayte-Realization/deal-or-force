using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using DealOrForce.Simulation;

static class SecessionColourTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS secession colours: " + message); }
    public static void Run()
    {
        var configured = new Defines { SecessionColourPalette = Array.Empty<string>() };
        GameDataLoader.ApplyDefines(configured);
        Check(configured.SecessionColourPalette.Length >= 5, "the palette is actually read through the shipped JSON define loader");
        var c = new Campaign(20261018);
        var parent = c.AddNation(new Nation { Tag = "AAA", Color = "#607080", MapIndex = 501 });
        var before = CampaignFingerprint.Of(c); var rng = c.Random.State;
        var colours = Enumerable.Range(0, 32).Select(i => SecessionColours.Choose(c, parent, "R"+i, new[] { 3001 })).ToArray();
        Check(colours.Distinct().Count() >= 5, "repeated uprisings do not all use one rotated grey-brown colour");
        Check(colours.SequenceEqual(Enumerable.Range(0,32).Select(i => SecessionColours.Choose(c,parent,"R"+i,new[]{3001}))), "selection is reproducible without random-order dependence");
        Check(before == CampaignFingerprint.Of(c) && rng == c.Random.State, "evaluating colours changes neither simulation nor its random stream");
        Check(SecessionColours.Choose(c,parent,"R0",new[]{3001},parent.Color) != parent.Color.ToUpperInvariant(), "a culture colour identical to the parent is not reused");
        c.Defines.SecessionColourPalette = new[] { "#FF0000", "#0000FF" };
        Check(new[]{"#FF0000","#0000FF"}.Contains(SecessionColours.Choose(c,parent,"R0",new[]{3001})), "a mod controls the available palette");
        c.Defines.SecessionColourPalette = new[] { "bad", "#oops", "" };
        Check(SecessionColours.Choose(c,parent,"R0",new[]{3001}).Length == 7, "a malformed mod palette has a safe nonempty fallback");
        var rebel = c.AddNation(new Nation { Tag = "RRR", Color = "#A87470", FoundedInRevolt = "discontent", SecededFromId = parent.Id });
        // Old rotation of #607080 is #A87470; a custom-painted sibling must remain untouched.
        var custom = c.AddNation(new Nation { Tag = "RRQ", Color = "#123456", FoundedInRevolt = "discontent", SecededFromId = parent.Id });
        Check(SecessionColours.MigrateLegacy(c) == 1 && rebel.Color != "#A87470" && custom.Color == "#123456",
            "only an exact legacy generated colour is migrated; authored or custom colours survive");
        Check(SecessionColours.MigrateLegacy(c) == 0, "a migrated colour is stable on the next load");
        rebel.Color = "#A87470"; c.Defines.MigrateLegacySecessionColoursOnLoad = false;
        Check(SecessionColours.MigrateLegacy(c) == 0 && rebel.Color == "#A87470", "a mod may keep the legacy map colours");
        var (disk, originalParent, originalRebel) = TerritorialRecoveryTests.World();
        disk.ConfigureSinglePlayer(originalParent.Id);
        originalParent.Color = "#607080"; originalRebel.Color = "#A87470";
        originalRebel.SecededFromId = originalParent.Id; originalRebel.FoundedInRevolt = "discontent";
        var peer = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(disk,"colours"),"colours");
        Check(peer.Success && peer.Campaign.NationById(originalRebel.Id).Color == "#A87470", "network import preserves the host's exact colours rather than independently migrating them");
        var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dof-colours-" + Guid.NewGuid().ToString("N")));
        try
        {
            SaveSystem.Save(folder,"fixture",disk,"colours");
            var path = Path.Combine(folder,"fixture.dofsave");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var loaded = SaveSystem.Load(folder,"fixture","colours");
            Check(loaded.Success && loaded.Campaign.NationById(originalRebel.Id).Color != "#A87470", "disk loading repairs an exact old generated colour in memory");
            Check(hash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "loading does not rewrite the original save file");
        }
        finally
        {
            if (folder.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(folder).StartsWith("dof-colours-",StringComparison.Ordinal) && Directory.Exists(folder)) Directory.Delete(folder,true);
        }

        RunCentralEuropeMultiSplinterTests();
    }

    private static void RunCentralEuropeMultiSplinterTests()
    {
        var eu = new Campaign(20350525);
        GameDataLoader.ApplyDefines(eu.Defines);
        var deu = eu.AddNation(new Nation { Tag = "DEU", Name = "Germany", Region = "Central Europe", Color = "#5a5a5a", MapIndex = 500, Population = 83_000_000 });
        var fra = eu.AddNation(new Nation { Tag = "FRA", Name = "France", Region = "Western Europe", Color = "#4f83c4", MapIndex = 501, Population = 68_000_000 });
        var pol = eu.AddNation(new Nation { Tag = "POL", Name = "Poland", Region = "Central Europe", Color = "#a33b40", MapIndex = 502, Population = 38_000_000 });
        var cze = eu.AddNation(new Nation { Tag = "CZE", Name = "Czechia", Region = "Central Europe", Color = "#48a284", MapIndex = 503, Population = 10_500_000 });
        var che = eu.AddNation(new Nation { Tag = "CHE", Name = "Switzerland", Region = "Central Europe", Color = "#c9403f", MapIndex = 504, Population = 8_700_000 });
        var nld = eu.AddNation(new Nation { Tag = "NLD", Name = "Netherlands", Region = "Western Europe", Color = "#d4762e", MapIndex = 505, Population = 17_500_000 });
        var bel = eu.AddNation(new Nation { Tag = "BEL", Name = "Belgium", Region = "Western Europe", Color = "#d8ae3b", MapIndex = 506, Population = 11_600_000 });
        var splinters = new List<Nation>();
        for (var i = 0; i < 6; i++)
        {
            var tag = $"D0{i}";
            var colour = SecessionColours.Choose(eu, deu, tag, Array.Empty<int>());
            var child = eu.AddNation(new Nation
            {
                Tag = tag, Name = $"Splinter {i}", Region = "Central Europe", Color = colour,
                MapIndex = 600 + i, SecededFromId = deu.Id, RevoltParentId = deu.Id, Population = 4_000_000
            });
            splinters.Add(child);
        }
        var hexes = splinters.Select(s => s.Color.ToUpperInvariant()).ToArray();
        Check(hexes.Distinct().Count() == 6, $"all 6 German splinters receive unique hex colors ({string.Join(", ", hexes)})");
        foreach (var child in splinters)
        {
            Check(!SecessionColours.ClashesWithNeighbors(eu, child),
                $"splinter {child.Tag} ({child.Color}) cleanly separated from Germany, Central Europe neighbors, and siblings");
        }
        // Verify AssignDistinctSurvivorIdentity fixes a clashing slate-blue survivor against living Germany
        var clash = eu.AddNation(new Nation { Tag = "DX9", Name = "Military Command", Region = "Central Europe", Color = "#5B5D5F", MapIndex = 610, SecededFromId = deu.Id, RevoltParentId = deu.Id, Population = 5_000_000 });
        SecessionEvents.AssignDistinctSurvivorIdentity(eu, clash, deu, "Hesse");
        Check(clash.Color != "#5B5D5F" && !SecessionColours.ClashesWithNeighbors(eu, clash),
            $"AssignDistinctSurvivorIdentity replaced near-duplicate grey '#5B5D5F' with '{clash.Color}'");
        _ = fra; _ = pol; _ = cze; _ = che; _ = nld; _ = bel;
    }
}
