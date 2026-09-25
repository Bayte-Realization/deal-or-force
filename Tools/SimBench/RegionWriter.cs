using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Writes every region of the 2024 world as the bookmark opens it, one file per country, in
/// the shape history/states/2024/ reads - and dumps the same world as a table, so two builds
/// of the bookmark can be compared line by line.
///
/// The regions used to be dealt out from their country at the start of every campaign, with
/// a few hundred corrections in four files by topic and the peoples in a fifth folder by
/// country. Written down, a region is one entry in one place: what it has is what the file
/// says, and a modder looking for Moscow finds Moscow.
/// </summary>
public static class RegionWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The arms plants 10_arms_industry.json named, kept beside the regions they are in.</summary>
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru_sve"] = "arms: Uralvagonzavod in Nizhny Tagil, Uraltransmash - tanks and self-propelled guns",
        ["ru_ud"] = "arms: Kalashnikov in Izhevsk, the Votkinsk missile plant",
        ["ru_tul"] = "arms: KBP, Splav, Tulamashzavod - guided weapons and rocket artillery",
        ["ru_mow"] = "arms: Almaz-Antey, Tactical Missiles Corporation, the aviation design bureaux",
        ["ru_niz"] = "arms: Arzamas, the Sverdlov explosives works in Dzerzhinsk",
        ["ru_ta"] = "arms: Kazan helicopters, the Alabuga drone works",
        ["ru_kgn"] = "arms: Kurganmashzavod - infantry fighting vehicles",
        ["ru_per"] = "arms: Motovilikha - artillery and rocket launchers; the Perm powder works",
        ["ru_oms"] = "arms: Omsktransmash - tanks",
        ["ru_vla"] = "arms: Degtyarev in Kovrov - small arms, anti-tank weapons, drones",
        ["ua_32"] = "arms: Artem, Antonov, Kuznya na Rybalskomu",
        ["ua_12"] = "arms: Pivdenmash, the Pavlohrad chemical plant",
        ["aq_x01"] = "held by no state under the 1959 Antarctic Treaty; nobody lives there for good",
        ["aq_x02"] = "held by no state under the 1959 Antarctic Treaty; nobody lives there for good"
    };

    /// <summary>Fields the old files stated for a region that the deal does not work out from them.</summary>
    private static Dictionary<ScenarioState, Dictionary<string, float>> StatedExtras(Campaign campaign)
    {
        var byKey = new Dictionary<string, ScenarioState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in campaign.States)
            if (!string.IsNullOrEmpty(state.Iso)) byKey.TryAdd(state.Iso, state);
        foreach (var state in campaign.States)
            if (!string.IsNullOrEmpty(state.Id)) byKey[state.Id] = state;
        var extras = new Dictionary<ScenarioState, Dictionary<string, float>>();
        foreach (var file in GameDataLoader.DataFiles("history", "states", "2024"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object || !byKey.TryGetValue(entry.Name, out var state)) continue;
                foreach (var key in new[] { "literacy", "standard_of_living", "radicalism" })
                    if (entry.Value.TryGetProperty(key, out var value) && value.TryGetSingle(out var number))
                    {
                        if (!extras.TryGetValue(state, out var fields)) extras[state] = fields = new Dictionary<string, float>();
                        fields[key] = number;
                    }
            }
        }
        return extras;
    }

    public static int Write(Campaign campaign, string folder)
    {
        Directory.CreateDirectory(folder);
        var extras = StatedExtras(campaign);
        var files = 0;
        var regions = 0;
        foreach (var nation in campaign.Nations.OrderBy(nation => nation.Tag, StringComparer.Ordinal))
        {
            var states = campaign.StatesOf(nation);
            if (states.Length == 0) continue;
            var composition = DemographyCatalog.AllocateCultures(nation.Tag, states,
                states.Select(state => state.Population).ToList());
            var entries = new List<(ScenarioState State, IReadOnlyList<(string Culture, float Share)> Cultures)>();
            for (var index = 0; index < states.Length; index++) entries.Add((states[index], composition[index]));
            var people = states.Sum(state => state.Population);

            var text = new StringBuilder();
            text.Append("// ").Append(nation.Name).Append(" (").Append(nation.Tag).Append(") on 1 January 2024: ")
                .Append(states.Length.ToString("N0", Invariant)).Append(states.Length == 1 ? " region, " : " regions, ")
                .Append(people.ToString("N0", Invariant)).Append(" people.\n");
            text.Append(Header);
            text.Append("{\n");
            var first = true;
            foreach (var (state, cultures) in entries.OrderBy(entry => entry.State.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(entry => entry.State.Id, StringComparer.Ordinal))
            {
                if (!first) text.Append(",\n");
                first = false;
                AppendRegion(text, state, cultures, extras.GetValueOrDefault(state), owner: null);
                regions++;
            }
            text.Append("\n}\n");
            File.WriteAllText(Path.Combine(folder, nation.Tag + ".json"), text.ToString(), new UTF8Encoding(false));
            files++;
        }

        // Land that belongs to nobody, and water that belongs to nobody: an unclaimed region is
        // there to be settled, a lake is not, so only the first is written with an owner of none.
        foreach (var (file, header, owner, pick) in new (string, string, string, Func<ScenarioState, bool>)[]
                 {
                     ("unclaimed.json", "// Land nobody owns on 1 January 2024.\n" + UnclaimedHeader, "none",
                         state => state.Unclaimed),
                     ("lakes.json", "// The lakes the map draws as regions of their own.\n" + LakesHeader, null,
                         state => !state.Unclaimed && state.NationMapIndex == 0)
                 })
        {
            var chosen = campaign.States.Where(pick).OrderBy(state => state.Id, StringComparer.Ordinal).ToList();
            if (chosen.Count == 0) continue;
            var text = new StringBuilder();
            text.Append(header);
            text.Append("{\n");
            for (var index = 0; index < chosen.Count; index++)
            {
                if (index > 0) text.Append(",\n");
                AppendRegion(text, chosen[index], Array.Empty<(string, float)>(), extras.GetValueOrDefault(chosen[index]), owner);
                regions++;
            }
            text.Append("\n}\n");
            File.WriteAllText(Path.Combine(folder, file), text.ToString(), new UTF8Encoding(false));
            files++;
        }
        Console.WriteLine($"wrote {regions:N0} region(s) in {files:N0} file(s) to {folder}");
        return 0;
    }

    private const string Header =
        "//\n" +
        "// Every region the country holds when the bookmark opens, and what it has: how many people\n" +
        "// live there and how many of them in towns, who they are, what is built there and what lies\n" +
        "// under the ground. \"A region's first day\" in docs/MODDING_GUIDE.md lists every field.\n" +
        "//\n" +
        "// What is written is what the region has - a building or a deposit left out is not there.\n" +
        "// A field left out altogether is worked out from the country, the way it is for a region a\n" +
        "// mod adds. Files are read in name order and a later one naming the same region wins key by\n" +
        "// key, so a mod that changes one number writes that one number and inherits the rest.\n" +
        "// Who owns a province is history/provinces/ownership.csv; the file a region is in is only\n" +
        "// where to find it.\n";

    private const string UnclaimedHeader =
        "//\n" +
        "// \"owner\": \"none\" makes a region belong to nobody: its provinces stay unowned whatever the\n" +
        "// map painted there, it is drawn in the grey of land no flag flies over, and any country that\n" +
        "// can reach it - over a border of its own or, when it is coastal, by sea from a coast of its\n" +
        "// own - may settle it (ColonisationCore). \"none\" is the only owner a region takes; who owns\n" +
        "// a province is otherwise history/provinces/ownership.csv. A colonial-era scenario would list\n" +
        "// its unclaimed regions here the same way.\n";

    private const string LakesHeader =
        "//\n" +
        "// Nobody lives on them and nobody owns them; they are regions so that the map can draw them\n" +
        "// and armies can be kept off them. Their record is here so that every region of the map is\n" +
        "// written down somewhere, and a mod that wants to give a lake something finds it here.\n";

    private static void AppendRegion(StringBuilder text, ScenarioState state,
        IReadOnlyList<(string Culture, float Share)> cultures, Dictionary<string, float> extras, string owner)
    {
        var note = Notes.TryGetValue(state.Id, out var said) ? " - " + said : "";
        var water = LakeRegions.IsWaterState(state) ? " (water)" : "";
        // The name the game shows, which a localisation table may have corrected (ru_alt is
        // Altai Krai whatever common/states still calls it).
        var name = string.IsNullOrWhiteSpace(state.DisplayName) ? state.Name : state.DisplayName;
        text.Append("  // ").Append(name).Append(water).Append(note).Append('\n');
        text.Append("  \"").Append(state.Id).Append("\": {\n");
        var fields = new List<string>();
        if (owner is not null) fields.Add($"\"owner\": \"{owner}\"");
        fields.Add($"\"population\": {state.Population.ToString(Invariant)}");
        fields.Add($"\"urbanization\": {Round(state.Urbanization, 3)}");
        if (extras is not null)
            foreach (var (key, value) in extras.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                fields.Add($"\"{key}\": {Round(value, 3)}");
        var peoples = cultures.Where(entry => entry.Share >= 0.00005f).ToList();
        if (state.Population > 0 && peoples.Count > 0)
            fields.Add("\"cultures\": { " + string.Join(", ", peoples.Select(entry =>
                $"\"{entry.Culture}\": {Round(entry.Share, 4)}")) + " }");
        var built = state.Buildings.Where(pair => pair.Value > 0).OrderBy(pair => (int)pair.Key).ToList();
        if (built.Count > 0)
            fields.Add("\"buildings\": { " + string.Join(", ", built.Select(pair =>
                $"\"{BuildingCatalog.IdOf(pair.Key)}\": {pair.Value.ToString(Invariant)}")) + " }");
        var ground = state.ResourceDeposits.Where(pair => pair.Value >= 0.0005f).OrderBy(pair => (int)pair.Key).ToList();
        if (ground.Count > 0)
            fields.Add("\"resources\": { " + string.Join(", ", ground.Select(pair =>
                $"\"{CommodityCatalog.IdOf(pair.Key)}\": {Round(pair.Value, 3)}")) + " }");
        text.Append("    ").Append(string.Join(",\n    ", fields)).Append("\n  }");
    }

    private static string Round(float value, int digits) =>
        Math.Round(value, digits).ToString("0." + new string('#', digits), Invariant);

    /// <summary>The day-zero world as a table: every region and every country, one line each.</summary>
    public static int Dump(Campaign campaign, string path)
    {
        var lines = new List<string>();
        foreach (var nation in campaign.Nations.OrderBy(nation => nation.Tag, StringComparer.Ordinal))
            lines.Add(string.Join('\t', "country", nation.Tag, nation.Population.ToString(Invariant),
                Round(nation.Gdp, 2), Round(nation.Literacy, 3), Round(nation.Productivity, 3),
                Round(nation.StandardOfLiving, 3), Round(nation.Stability, 3)));
        foreach (var state in campaign.States.OrderBy(state => state.Id, StringComparer.Ordinal))
        {
            var owner = campaign.Nations.FirstOrDefault(nation => nation.MapIndex == state.NationMapIndex)?.Tag ?? "-";
            var peoples = state.PopulationGroups.GroupBy(group => group.Culture)
                .Select(group => (group.Key, Share: group.Sum(entry => entry.Size) / (double)Math.Max(1, state.Population)))
                .Where(entry => entry.Share >= 0.0005).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Share.ToString("0.000", Invariant)}");
            lines.Add(string.Join('\t', "region", state.Id, owner, state.Population.ToString(Invariant),
                Round(state.Urbanization, 3), Round(state.Literacy, 3), Round(state.StandardOfLiving, 3),
                Round(state.Radicalism, 3),
                string.Join(';', state.Buildings.Where(pair => pair.Value > 0).OrderBy(pair => (int)pair.Key)
                    .Select(pair => $"{BuildingCatalog.IdOf(pair.Key)}={pair.Value}")),
                string.Join(';', state.ResourceDeposits.Where(pair => pair.Value >= 0.0005f).OrderBy(pair => (int)pair.Key)
                    .Select(pair => $"{CommodityCatalog.IdOf(pair.Key)}={Round(pair.Value, 3)}")),
                string.Join(';', peoples)));
        }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Console.WriteLine($"dumped {campaign.Nations.Count:N0} countries and {campaign.States.Count:N0} regions to {path}");
        return 0;
    }
}
