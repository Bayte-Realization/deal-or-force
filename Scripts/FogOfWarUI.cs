using DealOrForce.Simulation;
namespace DealOrForce;
public partial class DealOrForceGame
{
    private bool CanInspectNation(int nationId) => CampaignIntel.PlayerCanInspect(_campaign, nationId);
    private bool HideForeignRegionDetails()
    {
        var owner = _campaign.Nations.Find(n => n.MapIndex == _selectedState?.NationMapIndex);
        if (owner is null || CanInspectNation(owner.Id)) return false;
        ClearDetail();
        _sideTitle.Text = _selectedState.Name;
        _sideBody.Text = Simulation.Loc.Format("{0}\n\nFog of war — regional population, industry and military intelligence are unavailable.", owner.DisplayName);
        SetRegionalActionsEnabled(false);
        return true;
    }
}
