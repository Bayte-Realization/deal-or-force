using System;
using System.Linq;
using DealOrForce.Simulation;

static class TerritorialClaimsTests
{
    private static void Check(bool ok, string note) { if (!ok) throw new Exception(note); Console.WriteLine("PASS territory: " + note); }
    public static void Run()
    {
        var c = new Campaign(31) { ScenarioId = "claims_fixture" };
        var a = new Nation { Id = 0, MapIndex = 501, Tag = "CLA", Name = "A", Population = 1000000 };
        var b = new Nation { Id = 1, MapIndex = 502, Tag = "CLB", Name = "B", Population = 1000000 };
        var d = new Nation { Id = 2, MapIndex = 503, Tag = "CLC", Name = "C", Population = 1000000 };
        c.Nations.AddRange(new[] { a, b, d });
        var home = new ScenarioState { MapIndex = 3001, Id = "home", Name = "Home", NationMapIndex = a.MapIndex, Population = 1000000 };
        var abroad = new ScenarioState { MapIndex = 3002, Id = "abroad", Name = "Abroad", NationMapIndex = b.MapIndex, Population = 1000000 };
        home.CoreTags.Add(a.Tag); home.Claims.Add(d.Tag);
        c.States.AddRange(new[] { home, abroad }); c.Homelands[a.Id] = new() { home.MapIndex };
        var states = Enumerable.Range(0, 8192).Select(i => (ushort)(i % 128 < 64 ? 3001 : 3002)).ToArray();
        var owners = Enumerable.Range(0, 8192).Select(i => (ushort)(i % 128 < 64 ? 501 : 502)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 128, 64, _ => false));
        c.ConfigureSinglePlayer(a.Id);
        var all = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToState[p] == home.MapIndex).ToArray();
        Check(all.Length >= 2, "fixture has divisible ground");
        StatePartition.Annex(c, new[] { all[0] }, b, false);
        var child = c.States.First(s => s.ParentMapIndex == home.MapIndex);
        Check(child.CoreTags.Contains(a.Tag) && child.Claims.Contains(d.Tag), "cores and claims survive a partial treaty");
        Check(TerritorialClaims.TitleOf(c, a, child) == TerritorialTitle.Homeland, "small lost piece remains homeland");
        Check(TerritorialClaims.HoldsOurHomeland(c, a, b) && LostLands.Of(c, a).Any(p => p.Region.MapIndex == child.MapIndex), "lost-land discovery includes partitioned pieces");
        Check(TerritorialClaims.PreferTarget(c, a, b, .2f, d, 3f), "viable homeland recovery precedes unrelated expansion");
        Check(!TerritorialClaims.PreferTarget(c, a, b, -.1f, d, 3f), "unviable recovery does not force a war");
        Check(!TerritorialClaims.PreferTarget(c, a, d, 4f, b, .2f), "unrelated conquest does not displace recovery");
        c.Defines.AiPrioritisesLostHomeland = false;
        Check(TerritorialClaims.PreferTarget(c, a, d, 4f, b, .2f), "mod can disable recovery preference");
        var before = CampaignFingerprint.Of(c); _ = TerritorialClaims.Disputes(c);
        Check(before == CampaignFingerprint.Of(c), "dispute inspection is read-only");
        Check(Statecraft.CasusBelli(c, a, b) == "reconquest", "homeland title supplies a reconquest aim");
        var copied = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "claims"), "claims");
        Check(copied.Success && copied.Campaign.States.First(s => s.MapIndex == child.MapIndex).CoreTags.Contains(a.Tag), "titles persist in real snapshots: " + copied.Warning);
        TerritorialClaims.Renounce(c, a, child);
        Check(TerritorialClaims.TitleOf(c, a, home) == TerritorialTitle.None && TerritorialClaims.TitleOf(c, a, child) == TerritorialTitle.None,
            "renunciation applies to the whole original family");
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "claims"), "claims");
        Check(restored.Success && restored.Campaign.NationById(a.Id).RenouncedTerritoryOrigins.Contains(home.MapIndex), "renunciation persists instead of returning on load");
        StatePartition.Annex(c, all, a, false);
        Check(c.States.Any(s => s.MapIndex == home.MapIndex && s.CoreTags.Contains(a.Tag)), "reunification retains authored identity");
    }
}
