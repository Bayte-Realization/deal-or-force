using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Modding;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Narrow dependency surface the developer console and its commands read and mutate,
/// keeping console code decoupled from the full <see cref="DealOrForceGame"/> partial class.
/// </summary>
internal sealed class ConsoleContext
{
    public required Func<Campaign> GetCampaign { get; init; }
    public required Func<GlobePresentation> GetGlobe { get; init; }
    public required Func<Nation> GetSelectedNation { get; init; }
    public required Action<Nation> SetSelectedNation { get; init; }
    public required Func<ScenarioState> GetSelectedState { get; init; }
    public required Action<ScenarioState> SetSelectedState { get; init; }
    public required Func<int> GetSelectedProvinceId { get; init; }
    public required Func<IReadOnlyList<Army>> GetSelectedArmies { get; init; }
    public required Action<bool> SetObserverMode { get; init; }
    public required Func<bool> GetFreeCameraMode { get; init; }
    public required Action<bool> SetSimulating { get; init; }
    public required Func<bool> GetNetworkConnected { get; init; }
    public required Func<ModLoader> GetModLoader { get; init; }
    public required Func<string> GetModFingerprint { get; init; }
    public required Func<string> GetSaveRoot { get; init; }

    public required Func<string, Control> CreateFullLayer { get; init; }
    public required Action<Node> AddChildNode { get; init; }
    public required Func<Control, PanelContainer> CreateTexturedPanel { get; init; }
    public required Func<PanelContainer, int, int, int, VBoxContainer> CreatePanelRows { get; init; }
    public required Func<string, int, Color, Label> CreateTitle { get; init; }
    public required Func<string, Action, int, int, Button> CreateActionButton { get; init; }
    public required Func<LineEdit, string, LineEdit> ApplyPlaceholder { get; init; }
    public required Func<string, string> Tr { get; init; }

    public required Action Refresh { get; init; }
    public required Action RefreshEvents { get; init; }
    public required Action RefreshLocalisedText { get; init; }
    public required Action LayoutCampaignShell { get; init; }
    public required Action RepaintPresentation { get; init; }
    public required Action TogglePause { get; init; }
    public required Action<int> SetSpeed { get; init; }
    public required Action ToggleFreeCamera { get; init; }
    public required Action ClearArmySelectionAndClose { get; init; }
    public required Action<StrategicWar> OpenPeaceConference { get; init; }
    public required Action<List<string>> ApplySkinOverlay { get; init; }
    public required Func<string, System.Threading.Tasks.Task> RestoreCampaignFromSlot { get; init; }
    public required Func<string> CreateSupportReport { get; init; }
    public required Action<string> SetObserverIdentityBadge { get; init; }
    public required Action UpdateLanguageButton { get; init; }
    public required Func<string> MeasureLayout { get; init; }
    public required Func<string, string> ToggleCountryTags { get; init; }
    public required Func<string> CastSnowgrave { get; init; }
    public required Func<string> CastWeirdRoute { get; init; }
    public required Func<string> TouchGrass { get; init; }

    public Campaign Campaign => GetCampaign();
    public GlobePresentation Globe => GetGlobe();

    /// <summary>BBCode markers that follow the skin, so the console recolours with the game.</summary>
    public static string Bb(string token) => "[color=" + Simulation.InterfaceSkin.Colour(token) + "]";

    public static string EscapeBb(string value) => (value ?? "").Replace("[", "[​");

    public static string[] Words(string argument) =>
        (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool TryFloat(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
        float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number);

    /// <summary>A percentage typed either way: 75 and 0.75 both mean three quarters.</summary>
    public static bool TryShare(string value, out float share)
    {
        share = 0f;
        if (!TryFloat(value, out var number)) return false;
        share = Math.Clamp(number > 1f ? number / 100f : number, 0f, 1f);
        return true;
    }

    /// <summary>The country a command is about: the one named, else the selected, else yours.</summary>
    public Nation ResolveNation(string argument)
    {
        var campaign = Campaign;
        if (campaign is null) return null;
        var tag = (argument ?? "").Trim();
        if (tag.Length == 0) return GetSelectedNation() ?? campaign.Player;
        return campaign.NationByTag(tag) ??
               campaign.Nations.FirstOrDefault(nation =>
                   nation.Name.Contains(tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The region a command is about: the one named, else the selected.</summary>
    public ScenarioState ResolveState(string argument)
    {
        var campaign = Campaign;
        if (campaign is null) return null;
        var name = (argument ?? "").Trim();
        if (name.Length == 0) return GetSelectedState();
        return campaign.States.FirstOrDefault(state =>
                   string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase)) ??
               campaign.States.FirstOrDefault(state =>
                   state.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Roughly where a region is, by averaging the provinces that make it up.</summary>
    public static (float Latitude, float Longitude) StateCentre(Campaign campaign, ScenarioState state)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || state is null) return (0f, 0f);
        double latitude = 0, longitude = 0;
        var counted = 0;
        for (var province = 1; province <= map.ProvinceCount && counted < 400; province++)
        {
            if (map.ProvinceToState[province] != state.MapIndex) continue;
            var centre = map.CenterOf(province);
            latitude += centre.Latitude;
            longitude += centre.Longitude;
            counted++;
        }
        return counted == 0 ? (0f, 0f) : ((float)(latitude / counted), (float)(longitude / counted));
    }

    public (float Latitude, float Longitude) StateCentre(ScenarioState state) => StateCentre(Campaign, state);
}
