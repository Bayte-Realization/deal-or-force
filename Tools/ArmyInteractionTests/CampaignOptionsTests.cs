using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A campaign's own options (item 55): read from the campaign, written to it, and carried in
/// its save rather than taken from whatever the player has set since.
/// </summary>
static class CampaignOptionsTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var step = SettingsCatalog.Of("hours_per_tick");
        Check(step is { Campaign: true }, "the step length is declared a campaign option in common/settings");
        var language = SettingsCatalog.Of("language");
        Check(language is { Campaign: false }, "the language stays the player's");

        var c = Campaign.CreateBootstrap(20260909);
        c.AttachProvinceTopology(map);
        c.ConfigureSinglePlayer(c.NationByTag("FRA").Id);
        var previous = SettingsCatalog.CurrentCampaign;
        try
        {
            SettingsCatalog.CurrentCampaign = () => c;
            var first = SettingsCatalog.Chosen("hours_per_tick");
            Check(c.Settings.ContainsKey("hours_per_tick"),
                "a campaign asked for its step for the first time keeps the value it was given");

            var other = step.Values.First(value => value != first);
            SettingsCatalog.Store("hours_per_tick", other);
            Check(SettingsCatalog.Chosen("hours_per_tick") == other && c.Settings["hours_per_tick"] == other,
                "a campaign option set inside a campaign is written to that campaign");

            // Through the save and back.
            var folder = Path.Combine(Path.GetTempPath(), "dof_campaign_options_test");
            Directory.CreateDirectory(folder);
            try
            {
                SaveSystem.Save(folder, "options_test", c, "", "options test", false);
                var loaded = SaveSystem.Load(folder, "options_test", "");
                Check(loaded.Success, "the campaign saves and loads: " + loaded.Warning);
                Check(loaded.Campaign.Settings.TryGetValue("hours_per_tick", out var kept) && kept == other,
                    "the save carries the campaign's own step, not the player's");

                // And another campaign does not see this one's.
                var fresh = Campaign.CreateBootstrap(20260910);
                SettingsCatalog.CurrentCampaign = () => fresh;
                Check(!fresh.Settings.ContainsKey("hours_per_tick") || fresh.Settings["hours_per_tick"] != other ||
                      first == other, "a different campaign keeps its own options");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
            }
        }
        finally
        {
            SettingsCatalog.CurrentCampaign = previous;
        }
    }
}
