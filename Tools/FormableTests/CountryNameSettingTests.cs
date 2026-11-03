using System;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Naming jokes are ordinary player preferences, not campaign rules or new countries.</summary>
static class CountryNameSettingTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS country name setting: " + label); }

    public static void Run()
    {
        const string description = "I have an acquaintance from Lipetsk Who was nearly lynched by Kyrgyz people because in one of His videos He referred to Kyrgyzstan as \"Kirgizia\", so...";
        var language = Loc.Language;
        var previousCampaign = SettingsCatalog.CurrentCampaign;
        // Own temporary preferences only: never read or overwrite the player's real settings.
        var folder = Path.Combine(Path.GetTempPath(), "dof_country_names_" + Guid.NewGuid().ToString("N"));
        try
        {
            SettingsCatalog.CurrentCampaign = null;
            PlayerSettings.UseDirectory(folder);
            var errors = new System.Collections.Generic.List<string>();
            SettingsCatalog.ResetCatalog(errors);
            Loc.ResetCatalog();
            var setting = SettingsCatalog.Of("country_name_kgz");
            Check(errors.Count == 0 && setting is { Kind: SettingKind.Choice, Campaign: false, NeedsRestart: false }, "Kyrgyzstan is a live player-side naming choice");
            Check(setting.Category == "interface" && setting.Order > SettingsCatalog.Of("country_name_mda").Order &&
                setting.Order < SettingsCatalog.Of("movement_flags").Order, "naming joke sits beside Moldova and Belarus");
            Check(setting.Values.SequenceEqual(new[] { "kyrgyzstan" }) && setting.DefaultValue == "kyrgyzstan" && setting.Labels.Count == 1,
                "exactly one answer exists and is the default");
            Check(setting.Description == description, "authored English anecdote is retained verbatim");
            var campaign = new Campaign(20261102);
            var country = new Nation { Id = 0, Tag = "KGZ", Name = "Kyrgyzstan" };
            campaign.AddNation(country);
            SettingsCatalog.CurrentCampaign = () => campaign;
            var before = CampaignFingerprint.Of(campaign); var rng = campaign.Random.State;
            foreach (var locale in new[] { "english", "russian" })
            {
                Loc.Language = locale;
                var expected = locale == "english" ? "Kyrgyzstan" : "Кыргызстан";
                Check(Loc.Has(setting.Name) && Loc.Has(setting.Description) && Loc.Get(setting.Labels[0]) == expected,
                    "heading, description and only answer are translated: " + locale);
                Check(CountryNames.Of("KGZ") == expected && CountryNames.Resolve(country, true) == expected &&
                    CountryNames.Resolve(country, false) == expected, "tag, map and country card share chosen spelling: " + locale);
                Check(Loc.Get(setting.Description) == (locale == "english" ? description :
                    "У меня есть знакомый из Липецка, которого чуть не линчевали Кыргызы за то, что Он в Своём видео назвал Кыргызстан Киргизией, так что..."),
                    "description matches the user's exact wording: " + locale);
            }
            SettingsCatalog.Store(setting.Id, "kyrgyzstan");
            Check(File.Exists(Path.Combine(folder, "settings.json")) && !campaign.Settings.ContainsKey(setting.Id),
                "answer is saved in player preferences, not the campaign");
            PlayerSettings.UseDirectory(folder);
            Check(SettingsCatalog.Chosen(setting.Id) == "kyrgyzstan", "saved answer survives preference reload");
            PlayerSettings.Set(setting.Id, "kirgizia");
            Check(SettingsCatalog.Chosen(setting.Id) == "kyrgyzstan", "stale unsupported answer falls back to the single offered choice");
            SettingsCatalog.Reset(setting.Id); CountryNames.Refresh();
            Check(SettingsCatalog.Chosen(setting.Id) == "kyrgyzstan" && CountryNames.Of("KGZ") == "Кыргызстан",
                "reset retains the only answer and correct spelling");
            Check(CampaignFingerprint.Of(campaign) == before && campaign.Random.State == rng,
                "viewing and storing a naming joke never changes campaign state or randomness");
            country.CustomName = "My custom country";
            Check(CountryNames.Resolve(country, true) == country.CustomName, "player's explicit custom name still takes priority");
        }
        finally
        {
            SettingsCatalog.CurrentCampaign = previousCampaign;
            PlayerSettings.UseDirectory(null);
            CountryNames.Refresh();
            Loc.Language = language;
        }
    }
}
