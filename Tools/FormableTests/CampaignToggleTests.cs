using System;
using System.IO;
using DealOrForce.Simulation;

/// <summary>
/// The two campaign toggles the menu stores as JSON booleans. A new campaign must read
/// those booleans. Chosen() only reads strings, which is how "On" used to do nothing.
/// </summary>
static class CampaignToggleTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dof_campaign_toggles_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previous = SettingsCatalog.CurrentCampaign;
        try
        {
            PlayerSettings.UseDirectory(folder);
            PlayerSettings.Set("civil_wars_players", true);
            PlayerSettings.Set("peace_by_provinces", true);
            Check(SettingsCatalog.Of("civil_wars_players") is { Campaign: true },
                "civil-war outbreaks in player countries is a campaign option");
            Check(SettingsCatalog.Of("peace_by_provinces") is { Campaign: true },
                "peace by provinces is a campaign option");
            Check(SettingsCatalog.Flag("civil_wars_players") && SettingsCatalog.Flag("peace_by_provinces"),
                "both toggles set in the menu are on before a campaign exists");

            var campaign = new Campaign(20261015) { ScenarioId = "toggle-fixture" };
            SettingsCatalog.CurrentCampaign = () => campaign;
            Check(SettingsCatalog.Flag("civil_wars_players") && campaign.Settings["civil_wars_players"] == "true",
                "a new campaign keeps civil wars in player countries from the menu");
            Check(SettingsCatalog.Flag("peace_by_provinces") && campaign.Settings["peace_by_provinces"] == "true",
                "a new campaign keeps peace by provinces from the menu");

            campaign.Defines.CivilWarStrikesPlayers = SettingsCatalog.Flag("civil_wars_players");
            campaign.Defines.PeaceWholeRegions = !SettingsCatalog.Flag("peace_by_provinces");
            Check(campaign.Defines.CivilWarStrikesPlayers && !campaign.Defines.PeaceWholeRegions,
                "the running campaign receives both choices");

            var player = new Nation
            {
                Id = 1, MapIndex = 1, Tag = "SWE", Name = "Sweden", IsHuman = true,
                Stability = 0.01f, Population = 10_000_000, OutputScale = 1f, LastCivilWarDay = -1
            };
            campaign.Nations.Add(player);
            for (var region = 1; region <= 4; region++)
                campaign.States.Add(new ScenarioState
                {
                    Id = "r" + region, MapIndex = region, NationMapIndex = 1, Population = 1_000_000
                });
            campaign.Defines.CivilWarEarliestDay = 0;
            campaign.Defines.CivilWarStrikesPlayers = false;
            Check(CivilWarOutbreak.EligibilityReason(campaign, player)
                    .Contains("disabled in campaign settings", StringComparison.Ordinal),
                "off keeps a player's country out of the weekly outbreak roll");
            campaign.Defines.CivilWarStrikesPlayers = true;
            Check(CivilWarOutbreak.EligibilityReason(campaign, player).Length == 0,
                "on puts a player's country through the same outbreak gate as any other");
        }
        finally
        {
            SettingsCatalog.CurrentCampaign = previous;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
