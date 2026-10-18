using System;
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
            var path = Path.Combine(folder,"fixture.bdfsave");
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
    }
}
