using DealOrForce.Modding;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly ModLoader _modLoader = new();
    private ModApi _modApi;
    private Label _modStatus = null!;

    private void InitializeModding()
    {
        _modLoader.Discover();
        _modApi = new ModApi(message => GD.Print($"[mod-api] {message}"));
    }

    private void BindCampaignMods()
    {
        _modLoader.ApplyCampaignData(_campaign);
        MarketSystem.Seed(_campaign);
        _campaign.History.Reset();
        _campaign.History.Capture(_campaign, true);
        _modApi.Bind(_campaign);
        _modLoader.StartCode(_modApi, _campaign);
        if (_modStatus is null) return;
        _modStatus.Text = _modLoader.StatusLine;
        // What is actually loaded, and what is present but locked. The menu line has room
        // for a count; the reason a pack is not loading needs a sentence.
        var lines = new System.Collections.Generic.List<string>();
        foreach (var pack in _modLoader.Dlc.Packages)
            lines.Add($"{(pack.Active ? "✓" : "·")} {pack.Manifest.Name} {pack.Manifest.Version}" +
                      (pack.Active ? "" : pack.Errors.Count > 0 ? $" — {pack.Errors[0]}" : " — not owned"));
        foreach (var package in _modLoader.Packages)
            if (!package.IsContentPack)
                lines.Add($"✓ {package.Manifest.Name} {package.Manifest.Version} (mod)");
        if (lines.Count == 0) lines.Add("Nothing loaded beyond the base game.");
        lines.Add("");
        lines.Add("Content packs load after the base game and before mods, so a mod can");
        lines.Add("correct a pack exactly as it can correct the game. See dlc/README.md.");
        _modStatus.TooltipText = string.Join(System.Environment.NewLine, lines);
    }
}
