using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// What the 2024 scenario's own manifest decides, and what it does not.
///
/// The base world used to open on Korea twice over, in two literals, and its Korean treasury was
/// a number compiled into the scenario builder. A world with those in it can be looked at but not
/// changed: a mod that wants to start a player anywhere else, or rebuild Korea's finances, has to
/// fork the code that assembles the world rather than a file in game/.
///
/// The seat now comes from game/scenarios/2024/scenario.json and the treasury from
/// game/history/countries/2024/00_countries.json. These are the properties that has to keep
/// being true, including the one that matters most - that changing the file changes the world.
/// </summary>
static class ScenarioManifestTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("scenario manifest: " + label); Console.WriteLine("PASS scenario manifest: " + label); }

    public static void Run()
    {
        var entry = ScenarioLibrary.Discover(Array.Empty<string>(), "").FirstOrDefault(e => e.Id == "2024");
        Check(entry is not null, "the shipped 2024 scenario is discovered at all");
        if (entry is null) return;
        Check(entry.ManifestFile.Length > 0, "the base scenario knows which manifest described it");
        Check(File.Exists(entry.ManifestFile), "the manifest it names is a file that exists");

        // What the manifest says, read the way a reader of the file would read it.
        var declared = "";
        using (var doc = JsonDocument.Parse(File.ReadAllText(entry.ManifestFile), GameDataLoader.ForgivingJson))
            if (doc.RootElement.TryGetProperty("player_country", out var tag) && tag.ValueKind == JsonValueKind.String)
                declared = tag.GetString() ?? "";
        Check(declared.Length > 0, "the manifest names the country a run opens as");

        var campaign = ScenarioLibrary.Create(entry, 20261016);
        Check(campaign.Player is not null, "a campaign built from the manifest has a player");
        if (campaign.Player is null) return;
        // If the tag in the file were a typo the builder would fall back to Korea and say nothing,
        // and the mistake would only show up as a game that starts you somewhere you did not ask
        // for. So the name is checked against the world, not against a constant.
        Check(campaign.NationByTag(declared) is not null,
            $"the country the manifest names ('{declared}') is in the world");
        Check(campaign.Player.Tag == declared, "and that country is the one the run opens as");
        Check(campaign.HumanSeats.Count == 1 && campaign.HumanSeats[0] == campaign.Player.Id,
            "with exactly one human seat, in that country");
        Check(campaign.Nations.Count(n => n.IsHuman) == 1, "and no second country left flagged human");

        // The point of the move: a different file gives a different world, with no code change.
        var temp = Path.Combine(Path.GetTempPath(), "dof_scenario_manifest_test.json");
        try
        {
            File.WriteAllText(temp, "{ \"player_country\": \"CHN\" }");
            var other = ScenarioCatalog.CreateBaseCampaign(20261016, temp);
            Check(other.Player?.Tag == "CHN", "a manifest naming China opens the campaign as China");
            Check(other.Nations.Count(n => n.IsHuman) == 1 && other.Player.IsHuman,
                "and moves the human seat with it instead of leaving Korea's behind");
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        // And the treasury that used to be a literal is the one written in history.
        var korea = campaign.NationByTag("KOR");
        Check(korea is not null, "Korea is in the base world");
        if (korea is null) return;
        var stated = 0f;
        var countriesFile = Path.Combine(GameDataLoader.DataFolder("history") ?? "", "countries", "2024", "00_countries.json");
        if (File.Exists(countriesFile))
            using (var doc = JsonDocument.Parse(File.ReadAllText(countriesFile), GameDataLoader.ForgivingJson))
                if (doc.RootElement.TryGetProperty("KOR", out var kor) &&
                    kor.TryGetProperty("treasury", out var figure) && figure.ValueKind == JsonValueKind.Number)
                    stated = figure.GetSingle();
        Check(stated > 0f, "Korea's opening balance is written down in history/countries, not compiled in");
        // Compared loosely: NormalizeAccounts runs after the history and may round. What is being
        // guarded is that the file is what the campaign opened on, not that it is untouched.
        Check(stated > 0f && Math.Abs(korea.Treasury - stated) < Math.Max(1f, stated * 0.01f),
            $"and the campaign opened on it ({korea.Treasury:0.###} against the file's {stated:0.###})");
    }
}
