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

            var lodCampaign = new Campaign(20261127) { ScenarioId = "lod-fixture" };
            SettingsCatalog.CurrentCampaign = () => lodCampaign;
            Check(SettingsCatalog.Of("ai_diplomacy_lod") is { Campaign: true, Kind: SettingKind.Choice } lodSetting &&
                  lodSetting.Values.Count == 4 && lodSetting.DefaultValue == "balanced",
                "ai_diplomacy_lod is registered as a 4-option campaign choice defaulting to balanced");
            Check(SettingsCatalog.Chosen("ai_diplomacy_lod") == "balanced" &&
                  lodCampaign.Settings["ai_diplomacy_lod"] == "balanced",
                "a new campaign receives balanced ai_diplomacy_lod by default");

            var eurMajor = new Nation
            {
                Id = 2, MapIndex = 2, Tag = "DEU", Name = "Germany", Region = "Europe",
                Population = 80_000_000, Gdp = 4_000f, CapitalLatitude = 52.5f, CapitalLongitude = 13.4f, Stability = 0.8f
            };
            var farMinorA = new Nation
            {
                Id = 3, MapIndex = 3, Tag = "BLZ", Name = "Belize", Region = "Central America",
                Population = 400_000, Gdp = 3f, CapitalLatitude = 17.25f, CapitalLongitude = -88.77f, Stability = 0.8f
            };
            var farMinorB = new Nation
            {
                Id = 4, MapIndex = 4, Tag = "SWZ", Name = "Eswatini", Region = "Southern Africa",
                Population = 1_200_000, Gdp = 5f, CapitalLatitude = -26.32f, CapitalLongitude = 31.13f, Stability = 0.8f
            };
            var humanFarMinor = new Nation
            {
                Id = 5, MapIndex = 5, Tag = "TON", Name = "Tonga", Region = "Oceania",
                Population = 100_000, Gdp = 0.5f, CapitalLatitude = -21.13f, CapitalLongitude = -175.20f, Stability = 0.8f,
                IsHuman = true
            };
            for (var gp = 0; gp < 12; gp++)
            {
                lodCampaign.Nations.Add(new Nation
                {
                    Id = 100 + gp, MapIndex = 100 + gp, Tag = $"GP{gp}", Name = $"Power {gp}",
                    Region = $"Region{gp}", Population = 150_000_000, Gdp = 10_000f, Stability = 0.8f
                });
            }
            lodCampaign.Nations.Add(humanFarMinor);
            lodCampaign.Nations.Add(eurMajor);
            lodCampaign.Nations.Add(farMinorA);
            lodCampaign.Nations.Add(farMinorB);

            lodCampaign.Defines.AiDiplomacyLodMode = "balanced";
            var fpBalanced = CampaignFingerprint.Of(lodCampaign);
            Check(AiStatecraft.IsActiveDiplomacyTier(lodCampaign, humanFarMinor, farMinorA, null, true, false),
                "balanced mode keeps human minor pairs in the active tier");
            Check(!AiStatecraft.IsActiveDiplomacyTier(lodCampaign, farMinorA, farMinorB, null, true, false),
                "balanced mode sends unrelated distant AI minors to the background tier");
            Check(AiStatecraft.IsActiveDiplomacyTier(lodCampaign, eurMajor, farMinorA, null, true, false),
                "balanced mode keeps major GDP powers in the active tier");

            lodCampaign.Defines.AiDiplomacyLodMode = "world";
            var fpWorld = CampaignFingerprint.Of(lodCampaign);
            Check(fpWorld != fpBalanced, "non-default ai_diplomacy_lod mode is mixed into CampaignFingerprint");
            Check(!AiStatecraft.IsActiveDiplomacyTier(lodCampaign, humanFarMinor, farMinorA, null, true, false),
                "world mode is player-independent for unrelated distant minor pairs");
            farMinorA.Notoriety = 25f;
            Check(AiStatecraft.IsActiveDiplomacyTier(lodCampaign, farMinorA, farMinorB, null, true, false),
                "world mode promotes pairs involved in live crises or high notoriety");
            farMinorA.Notoriety = 0f;

            lodCampaign.Defines.AiDiplomacyLodMode = "player_focus";
            Check(AiStatecraft.IsActiveDiplomacyTier(lodCampaign, humanFarMinor, farMinorA, null, true, false),
                "player_focus mode keeps all human diplomacy in the active tier");
            Check(!AiStatecraft.IsActiveDiplomacyTier(lodCampaign, eurMajor, farMinorA, null, true, false),
                "player_focus mode throttles non-human major-to-distant-minor chatter outside player sphere");
            lodCampaign.ObserverMode = true;
            farMinorA.Notoriety = 25f;
            Check(AiStatecraft.IsActiveDiplomacyTier(lodCampaign, farMinorA, farMinorB, null, true, false),
                "player_focus falls back to world significance automatically in Observer mode");
            lodCampaign.ObserverMode = false;
            farMinorA.Notoriety = 0f;

            lodCampaign.Defines.AiDiplomacyLodMode = "full";
            Check(AiStatecraft.IsActiveDiplomacyTier(lodCampaign, farMinorA, farMinorB, null, true, false) &&
                  AiStatecraft.IsPairDueThisCycle(lodCampaign, farMinorA, farMinorB, null, true, false, false),
                "full mode evaluates every pair in the active tier every week");

            lodCampaign.Defines.AiDiplomacyLodMode = "balanced";
            Check(CampaignFingerprint.Of(lodCampaign) == fpBalanced,
                "returning to balanced restores bit-exact default CampaignFingerprint");
        }
        finally
        {
            SettingsCatalog.CurrentCampaign = previous;
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
