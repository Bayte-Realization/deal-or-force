using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ListTerritorialDisputes()
    {
        var disputes = TerritorialClaims.Disputes(_campaign)
            .OrderByDescending(d => d.Claimant.Id == _campaign.Player.Id || d.Holder.Id == _campaign.Player.Id)
            .ThenBy(d => d.Title == TerritorialTitle.Homeland ? 0 : 1)
            .ThenByDescending(d => d.Region.Population).ToArray();
        _conflictsList.AddChild(Caption("TITLE IS NOT MILITARY CONTROL"));
        if (disputes.Length == 0) _conflictsList.AddChild(Title("No outstanding territorial claims.", 14, Ui.Colour("text.caption")));
        foreach (var d in disputes.Take(160))
        {
            var captured = d;
            var row = ActionButton(Loc.Format("{0} · {1} → {2}", d.Region.DisplayName, d.Claimant.Tag, d.Holder.Tag),
                () => { _territoryRegion = captured.Region.MapIndex; _territoryClaimant = captured.Claimant.Id;
                    RefreshConflictsScreen(true); }, 0, 38, iconPath: "res://game/gfx/interface/icons/icon_nation.png");
            row.Name = $"TerritoryDispute_{d.Region.MapIndex}_{d.Claimant.Id}";
            row.ClipText = true;
            row.TooltipText = Ui.Tip(Loc.Format("{0} has a {1} in {2}; actual owner: {3}.", d.Claimant.DisplayName,
                Tr(d.Title == TerritorialTitle.Homeland ? "Homeland title" : "Territorial claim"),
                d.Region.DisplayName, d.Holder.DisplayName));
            StyleButton(row, _territoryRegion == d.Region.MapIndex && _territoryClaimant == d.Claimant.Id);
            _conflictsList.AddChild(row);
        }
    }

    private void ShowTerritoryPage()
    {
        var nation = _campaign.NationById(_territoryClaimant);
        var region = _campaign.States.Find(s => s.MapIndex == _territoryRegion);
        var holder = region is null ? null : _campaign.Nations.Find(n => n.MapIndex == region.NationMapIndex);
        var title = TerritorialClaims.TitleOf(_campaign, nation, region);
        if (holder is null || title == TerritorialTitle.None || holder.Id == nation?.Id)
        {
            _conflictsPage.AddChild(Title("Select a territorial dispute on the left.", 20, Ui.Colour("text.accent")));
            _conflictsPage.AddChild(WrappedUltimatumText("Ownership is the de facto border; control says whose army holds a province. A homeland title or a disputed claim can survive both. None of them forces an immediate war.", 15));
            return;
        }
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 10);
        head.AddChild(FlagBadge(nation.Tag, 28)); head.AddChild(Title(region.DisplayName, 22, Ui.Colour("text.bright")));
        _conflictsPage.AddChild(head);
        _conflictsPage.AddChild(Caption(title == TerritorialTitle.Homeland ? "Homeland title" : "Territorial claim"));
        _conflictsPage.AddChild(WrappedUltimatumText(Loc.Format("Claimant: {0}\nDe facto owner: {1}", nation.DisplayName, holder.DisplayName), 16));
        var ground = _campaign.ProvinceMap;
        var provinces = Enumerable.Range(1, ground.ProvinceCount).Where(p => ground.ProvinceToState[p] == region.MapIndex).ToArray();
        var ownControl = provinces.Count(p => _campaign.ProvinceController[p] == nation.MapIndex);
        _conflictsPage.AddChild(WrappedUltimatumText(Loc.Format("Claimant controls {0} of {1} provinces in this piece.", ownControl, provinces.Length), 14));
        _conflictsPage.AddChild(new HSeparator());
        _conflictsPage.AddChild(WrappedUltimatumText(title == TerritorialTitle.Homeland
            ? "A government puts recovering its lost homeland before unrelated expansion when recovery is viable. Truces, treaties, readiness and deterrence still apply. Homeland provinces cost less in a peace conference, but are never free."
            : "A disputed claim is a weaker political interest. It can increase tension, but does not receive homeland recovery priority or its peace discount.", 15));
        _conflictsPage.AddChild(WrappedUltimatumText("A transfer does not erase a title. A deliberate renunciation covers the original region and all of its later pieces.", 14));
        _conflictsPage.AddChild(ActionButton("Show region on map", () => {
            CloseConflictsScreen(); var (lat, lon) = StateCentre(region); _globe.FocusOn(lat, lon, 6.8f);
        }, 0, 36, iconPath: "res://game/gfx/interface/icons/icon_nation.png"));
        _conflictsPage.AddChild(ActionButton("Open diplomacy", () => {
            _selectedNation = holder.Id == _campaign.Player.Id ? nation : holder;
            CloseConflictsScreen(); OpenDiplomacyScreen();
        }, 0, 36, iconPath: "res://game/gfx/interface/icons/icon_diplomacy.png"));
        if (nation.Id == _campaign.Player.Id)
            _conflictsPage.AddChild(ActionButton("Propose a peaceful deal", () => OpenDealComposer(holder), 0, 36,
                iconPath: "res://game/gfx/interface/icons/icon_diplomacy.png"));
    }
}
