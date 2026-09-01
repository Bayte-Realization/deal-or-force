using ByDealOrForce.Modding;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
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
        if (_modStatus is not null)
            _modStatus.Text = _modLoader.StatusLine;
    }
}
