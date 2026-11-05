using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

static class Release0530Tests
{
    private static void Check(bool ok, string why)
    { if (!ok) throw new Exception(why); Console.WriteLine("PASS 0.5.3.0: " + why); }

    public static void Run()
    {
        SpriteCatalog.Load(); FlagCatalog.ResetCatalog(); FormableNations.ResetCatalog();
        var campaign = Campaign.CreateBootstrap(20261105);
        var before = CampaignFingerprint.Of(campaign);
        var count = 0;
        foreach (var nation in campaign.Nations.Where(n => n.Movement is null && SpriteCatalog.Has("flag_" + n.Tag.ToLowerInvariant())))
        {
            var own = "flag_" + nation.Tag.ToLowerInvariant();
            Check(GovernmentCatalog.All.All(g => FlagCatalog.ResolvePreview(campaign, nation, g.Id) == own) &&
                IdeologyCatalog.All.All(i => FlagCatalog.ResolvePreview(campaign, nation, ideology: i.Id) == own),
                nation.Tag + " retains national artwork across every government and ideology");
            count++;
        }
        Check(count > 150 && CampaignFingerprint.Of(campaign) == before, "real-world flag previews are broad and non-mutating");
        foreach (var tag in new[] { "CHN", "TWN" })
        {
            var nation = campaign.NationByTag(tag);
            var banner = FlagCatalog.ResolveFormationPreview(campaign, nation, "china_reunified");
            Check(SpriteCatalog.TextureOf(banner) == SpriteCatalog.TextureOf("flag_" + tag.ToLowerInvariant()),
                tag + " reunification uses that founder's authored Chinese flag");
        }
        try
        {
            FlagCatalog.Merge("""{"prefer_country_base":false}""");
            Check(FlagCatalog.Candidates(campaign, campaign.NationByTag("RUS").FlagPoliticsPreview("absolute_monarchy", "monarchism"))[0] != "flag_rus",
                "mods can explicitly opt into qualifier-first flags");
        }
        finally { FlagCatalog.ResetCatalog(); }
        Components();
        Workspaces();
        LuauExample();
    }

    private static void LuauExample()
    {
        var repo = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var source = File.ReadAllText(Path.Combine(repo, "mods", "_templates", "luau_engine", "corecode", "modules", "war_damage.mod.lua"));
        var names = Regex.Matches(source, @"function\s+Event\.(\w+)\(").Select(m => m.Groups[1].Value).ToArray();
        Check(names.Length == 3 && names.Distinct().Count() == 3 && source.TrimEnd().EndsWith("return Event"),
            "Luau reference module has one effective definition per event and a final return");
        Check(!File.Exists(Path.Combine(repo, "mods", "luau_events", "war_damage.mod.lua")),
            "inactive Luau event reference lives with its template, not among installed mods");
    }

    private static void Components()
    {
        var c = new Campaign(20261105) { ScenarioId = "component_fixture" };
        var founder = new Nation { Id = 0, MapIndex = 1, Tag = "CHN", Population = 1000000, Gdp = 100, Treasury = 100, Stability = .95f };
        var other = new Nation { Id = 1, MapIndex = 2, Tag = "TWN", Population = 100000 };
        c.Nations.Add(founder); c.Nations.Add(other);
        var states = Enumerable.Range(1, 100).Select(i => (ushort)i).ToArray();
        var owners = Enumerable.Repeat((ushort)1, 100).ToArray(); owners[99] = 2;
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 100, 1, _ => false));
        var mainland = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToNation[p] == 1).ToArray();
        var island = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Where(p => c.ProvinceMap.ProvinceToNation[p] == 2).ToArray();
        c.ScenarioTerritories["CHN"] = mainland; c.ScenarioTerritories["TWN"] = island;
        var d = FormableNations.Find("china_reunified");
        Check(mainland.Length > 3 * island.Length && island.Length > 0, "unequal founding components reproduce the rounding trap");
        Check(!FormableNations.Evaluate(c, founder, d).CanForm, "mainland alone cannot count Taiwan as rounding error");
        foreach (var p in island) c.ProvinceController[p] = 1;
        Check(!FormableNations.Evaluate(c, founder, d).CanForm, "military occupation of the small component is not annexation");
        foreach (var p in island) c.ProvinceMap.ProvinceToNation[p] = 1;
        Check(FormableNations.Evaluate(c, founder, d).CanForm, "secured ownership of both components permits genuine reunification");
        Check(FormableNations.Find("italia_unita").MinimumComponentShare == 1f, "Greater Italy requires every enclave and Malta, not just Italian provinces");
    }

    private static void Workspaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "dof-workspaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entry = ModWorkspace.Create(root, "my_world", "My world / Мой мир");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(entry.Root, "manifest.json")));
            Check(manifest.RootElement.GetProperty("id").GetString() == "my_world" &&
                !manifest.RootElement.GetProperty("enabled").GetBoolean(), "new package has a real manifest, and never silently enables code");
            Check(Directory.Exists(Path.Combine(entry.Root, "localization", "russian")) &&
                ModWorkspace.Discover(root).Single().Root == entry.Root, "new package is discoverable and has bilingual data folders");
            var original = File.ReadAllText(Path.Combine(entry.Root, "manifest.json"));
            try { ModWorkspace.Create(root, "my_world", "Overwrite"); throw new Exception("collision accepted"); }
            catch (InvalidDataException) { }
            Check(File.ReadAllText(Path.Combine(entry.Root, "manifest.json")) == original, "folder collisions preserve the user's files");
            foreach (var id in new[] { "../escape", "A", "_hidden", "con", "x/y", "x:y" })
            {
                Check(!ModWorkspace.ValidId(id), "unsafe package id refused: " + id);
            }
            ContentWorkbench.Write(entry.Root, "common/scripted_effects/greeting.yaml", "greeting:\n  stability: 0.02\n");
            Check(File.Exists(Path.Combine(entry.Root, "common", "scripted_effects", "greeting.yaml")), "editor writes YAML into the chosen package, not the base game");
        }
        finally { Directory.Delete(root, true); }
    }
}
