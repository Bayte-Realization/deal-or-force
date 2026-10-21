using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A read-only look at what the naming composer does with real ground: which base it picks,
/// which form it wraps it in, and what a player would read in English and in Russian.
/// Not a test - a way to see the answers while the rules are being changed.
/// </summary>
static class NameProbe
{
    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var campaign = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        if (MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out _))
            campaign.AttachProvinceTopology(map);

        Show(campaign, "RUS", new[] { "ua_14", "ua_59" }, "ukrainian", "Donbas leaving Russia");
        Show(campaign, "RUS", new[] { "ua_43" }, "ukrainian", "Crimea leaving Russia");
        Show(campaign, "RUS", new[] { "ua_63" }, "ukrainian", "Kharkiv leaving Russia");
        Show(campaign, "UKR", new[] { "ua_43" }, "ukrainian", "Crimea leaving Ukraine");
        Show(campaign, "UKR", new[] { "ua_14", "ua_59" }, "ukrainian", "Donbas leaving Ukraine");
        Show(campaign, "RUS", new[] { "ru_ce" }, "chechen", "Chechnya leaving Russia");
        Show(campaign, "RUS", new[] { "ru_ta" }, "tatar", "Tatarstan leaving Russia");
        Show(campaign, "RUS", new[] { "ru_irk" }, "russian", "Irkutsk leaving Russia");
        Show(campaign, "RUS", new[] { "ru_irk" }, "buryat", "Buryatia leaving Russia");
        Show(campaign, "GBR", new[] { "gb_scb", "gb_dgy", "gb_agb", "gb_hld", "gb_abd" },
            "scottish", "Scotland leaving Britain");
        Show(campaign, "ESP", new[] { "es_l", "es_b", "es_gi", "es_t" }, "catalan",
            "Catalonia leaving Spain");
        Show(campaign, "TUR", new[] { "tr_21" }, "kurdish", "Kurdistan leaving Turkey");
        Show(campaign, "CHN", new[] { "cn_xz" }, "tibetan", "Tibet leaving China");
        Show(campaign, "YEM", new[] { "ye_hd" }, "arab_yemeni", "Hadhramaut leaving Yemen");
        Show(campaign, "KAZ", new[] { "kz_kus" }, "russian", "Russian north leaving Kazakhstan");
        SyntheticDirection(campaign);
        var irkutskState = campaign.States.FirstOrDefault(s => s.Id == "ru_irk");
        if (irkutskState is not null)
        {
            var land = LandAtlas.BestFor(campaign, new[] { irkutskState.MapIndex }, "RUS");
            var topo = campaign.ProvinceMap;
            var centre = "(no map)";
            if (topo is not null)
                for (var province = 1; province <= topo.ProvinceCount; province++)
                    if (topo.ProvinceToState[province] == (ushort)irkutskState.MapIndex)
                    {
                        var c = topo.CenterOf(province);
                        centre = $"{c.Latitude:0.##},{c.Longitude:0.##}";
                        break;
                    }
            var sib = LandAtlas.Find("siberia");
            var sibCountries = sib?.Countries is null ? "" : string.Join("/", sib.Countries);
            Console.WriteLine($"LandAtlas: count={LandAtlas.All.Count} best={(land?.Id ?? "none")} centre={centre} " +
                $"sibBox={sib?.HasBox} lat={sib?.LatMin}..{sib?.LatMax} lon={sib?.LonMin}..{sib?.LonMax} macro={sib?.Macro} " +
                $"countries={sibCountries}");
        }
        Console.WriteLine("--- names ---");
    }

    /// <summary>The same fixture NamingTests uses for a directional fallback, so the kind is visible.</summary>
    private static void SyntheticDirection(Campaign campaign)
    {
        var directional = new Campaign(20261019);
        var predecessor = new Nation { Id = 0, MapIndex = 1, Tag = "SHA", Name = "Shan State", CapitalLongitude = 80 };
        directional.Nations.Add(predecessor);
        var pixels = new ushort[24];
        var owners = new ushort[24];
        pixels[9] = 1;
        owners[9] = 1;
        directional.States.Add(new ScenarioState
        {
            MapIndex = 1, Id = "test_direction", NationMapIndex = 1, Name = "Ayeyarwady", Population = 1_000_000
        });
        directional.AttachProvinceTopology(ProvinceTopology.Build(pixels, owners, 8, 3, _ => false));
        var (kind, id) = NationNaming.BaseFor(directional, predecessor, new[] { 1 }, "", "auto");
        Console.WriteLine($"synthetic directional: kind={kind} id={id}");
    }

    private static void Show(Campaign campaign, string parentTag, string[] regionIds,
        string culture, string label)
    {
        var parent = campaign.NationByTag(parentTag);
        if (parent is null) { Console.WriteLine($"{label}: no {parentTag}"); return; }
        var indices = regionIds
            .Select(id => campaign.States.FirstOrDefault(state => state.Id == id)?.MapIndex ?? -1)
            .Where(index => index > 0).ToArray();
        if (indices.Length == 0) { Console.WriteLine($"{label}: no regions"); return; }
        var (kind, id) = NationNaming.BaseFor(campaign, parent, indices, culture, "auto");
        var born = new Nation
        {
            Tag = "PROBE", Name = parent.Name,
            Government = "provisional_government", Ideology = "nationalism",
            FoundNameKind = kind, FoundNameId = id,
            FoundNameForm = NationNaming.FormFor("provisional_government", "nationalism", "")
        };
        var previous = Loc.Language;
        Loc.Language = "english";
        var english = NationNaming.Render(campaign, born, false);
        var englishMap = NationNaming.Render(campaign, born, true);
        Loc.Language = "russian";
        var russian = NationNaming.Render(campaign, born, false);
        var russianMap = NationNaming.Render(campaign, born, true);
        Loc.Language = previous;
        Console.WriteLine($"{label}: kind={kind} id={id} | {english} [{englishMap}] | {russian} [{russianMap}]");
        var dump = System.IO.Path.Combine(AppContext.BaseDirectory, "tmp_probe_names.txt");
        System.IO.File.AppendAllText(dump,
            $"{label}\t{kind}\t{id}\t{english}\t{russian}\t{string.Join(",", russian.Select(c => ((int)c).ToString("X4")))}\n",
            new System.Text.UTF8Encoding(false));
    }
}
