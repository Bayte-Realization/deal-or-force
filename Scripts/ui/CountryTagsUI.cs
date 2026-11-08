using System;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string ConsoleCountryTags(string argument)
    {
        var value = argument.Trim().ToLowerInvariant();
        if (value is not ("" or "on" or "off")) return "tags [on|off]";
        SettingsCatalog.Store("show_country_tags", value == "" ? !SettingsCatalog.Flag("show_country_tags") : value == "on");
        ApplySetting("show_country_tags");
        return Tr(SettingsCatalog.Flag("show_country_tags") ? "Country tags: on" : "Country tags: off");
    }
}
