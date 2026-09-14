using System;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void RefreshSideLinks(string screen)
    {
        if (_sideLinks is null) return;
        ClearRuntimeChildren(_sideLinks);
        if (_campaign?.Player is null) return;
        var foreign = (screen is "Nation" or "Diplomacy") && _selectedNation is not null && _selectedNation != _campaign.Player;
        foreach (var link in CampaignUiCatalog.For(screen).Links)
        {
            if (!CampaignUiCatalog.LinkVisible(link, _dossierTab, foreign)) continue;
            if (foreign && link.Action == "government") continue;
            Action action = link.Action switch
            {
                "government" => OpenStateAffairs,
                "ledger" => OpenLedger,
                "wars" => OpenWarLedger,
                "diplomacy" => () => OpenScreen("Diplomacy"),
                _ => null
            };
            if (action is null) continue;
            var button = ActionButton(link.Label, action, 168, 30);
            Tip(button, link.Help.Length > 0 ? link.Help : link.Label);
            _sideLinks.AddChild(button);
        }
        _sideLinks.Visible = _sideLinks.GetChildCount() > 0;
    }
}
