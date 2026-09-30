using DealOrForce.Simulation;
namespace DealOrForce;
public partial class DealOrForceGame
{
    private bool CanInspectNation(int nationId) => CampaignIntel.PlayerCanInspect(_campaign, nationId);

    /// <summary>
    /// Ground you hold is ground you can survey.
    ///
    /// The fog used to ask one question - do you own this region - which is the wrong
    /// question in the middle of a war. An army that has taken half an oblast is
    /// administering it: it knows what is there. Answering "no intelligence available"
    /// about a place your own soldiers are standing in reads as a bug, because it is one.
    ///
    /// Half rather than any, so that a raid on one border province does not open the
    /// enemy's books on a region of ten million people.
    /// </summary>
    private bool HideForeignRegionDetails()
    {
        var owner = _campaign.Nations.Find(n => n.MapIndex == _selectedState?.NationMapIndex);
        if (owner is null || CanInspectNation(owner.Id)) return false;
        if (PlayerHoldsMostOf(_selectedState)) return false;
        ClearDetail();
        _sideTitle.Text = _selectedState.DisplayName;
        _sideBody.Text = owner.DisplayName + "\n\n" + Tr("Fog of war hides internal conditions, industry and military intelligence, not public population and GDP estimates.");
        DetailRow("Population", Fmt.People(_selectedState.Population), Fmt.PeopleExact(_selectedState.Population));
        DetailRow("GDP (estimate)", Fmt.Money(RegionalStatistics.EstimatedGdp(_campaign, _selectedState)),
            Tr(RegionalStatistics.GdpExplanation));
        SetRegionalActionsEnabled(false);
        return true;
    }

    /// <summary>Whether the player's flag flies over most of a region's provinces.</summary>
    private bool PlayerHoldsMostOf(ScenarioState state)
    {
        var topology = _campaign?.ProvinceMap;
        var control = _campaign?.ProvinceController;
        if (state is null || topology is null || control is null || _campaign.Player is null) return false;

        var mine = 0;
        var all = 0;
        var player = (ushort)_campaign.Player.MapIndex;
        for (var province = 1; province <= topology.ProvinceCount && province < control.Length; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)state.MapIndex) continue;
            all++;
            if (control[province] == player) mine++;
        }
        return all > 0 && mine * 2 >= all;
    }
}
