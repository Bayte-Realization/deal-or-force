using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// Put the era overlay the campaign asks for on the interface (SkinOverlays): the scenario's
    /// `skin`, unless the player's "Era overlay" setting says otherwise. Called whenever a new
    /// campaign is installed - a fresh start, a scenario, a save, a network session.
    ///
    /// The base scenario names no overlay and the setting defaults to "scenario", so for it this
    /// does nothing at all: no reload, no repaint. Only a change of overlay rebuilds the skin.
    /// The overlay's file errors go to the warnings, and to <paramref name="report"/> when a caller
    /// (the console's reload) shows the modder what was skipped.
    /// </summary>
    private void ApplySkinOverlay(List<string> report = null)
    {
        if (_campaign is null) return;
        var wanted = SkinOverlays.Choose(_campaign.SkinOverlay, PlayerSettings.Text(SkinOverlays.SettingKey, "scenario"));
        if (string.Equals(wanted, SkinOverlays.Active, System.StringComparison.OrdinalIgnoreCase)) return;

        var errors = new List<string>();
        var packages = _modLoader.Packages.Select(package => (package.PhysicalRoot, package.VirtualRoot)).ToList();
        var skins = GameDataLoader.DataFolder("skins");
        if (SkinOverlays.Active.Length == 0) SkinOverlays.Apply(wanted, skins, packages, errors);
        else SkinOverlays.Rebuild(wanted, skins, packages, errors);
        foreach (var error in errors) GD.PushWarning("[skin] " + error);
        report?.AddRange(errors.Select(error => "[skin] " + error));
        if (_hudLayer is not null) RepaintPresentation(this);
    }
}
