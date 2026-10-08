using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void MovementDiplomaticList(IEnumerable<int> ids, string heading)
    {
        var countries = ids.Select(_campaign.NationById).Where(n => n is not null).OrderBy(n => n.DisplayName).ToArray();
        var prefix = heading == "RECOGNISED BY" ? "MovementRecognition" : "MovementDesignation";
        var rows = new VBoxContainer { Name = prefix + "List", Visible = false };
        var toggle = ActionButton(Loc.Format("{0} · {1}", Tr(heading), countries.Length), () => rows.Visible = !rows.Visible,
            0, 34, iconPath: Icon("diplomacy"));
        toggle.Name = prefix + "Toggle"; _movementRows.AddChild(toggle);
        _movementRows.AddChild(rows);
        if (countries.Length == 0) rows.AddChild(Title("No countries recorded.", 13, Ui.Colour("text.muted")));
        foreach (var country in countries)
        {
            var selected = country;
            var button = ActionButton(country.DisplayName, () => { _selectedNation = selected; OpenDiplomacyScreen(); }, 0, 32);
            button.Name = prefix + "Country_" + country.Id;
            button.ClipText = true; button.TooltipText = Ui.Tip(country.DisplayName);
            rows.AddChild(button);
        }
    }
}
