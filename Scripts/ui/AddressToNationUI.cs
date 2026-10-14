using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>State Affairs' address to the nation: one button per kind of address, built from common/addresses.</summary>
public partial class DealOrForceGame
{
    private HFlowContainer _addressButtons = null!;
    private Label _addressSummary = null!;
    private readonly Dictionary<string, Button> _addressButtonById = new();

    private void BuildAddressControls(VBoxContainer rows)
    {
        StateSection(rows, "ADDRESS TO THE NATION",
            "Speak to the country. It works at once and less each time - a people addressed every month " +
            "stops listening - and whatever is promised is remembered on the day it falls due.");
        _addressSummary = RowLabel("", 12, Ui.Colour("text.body_soft"));
        _addressSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_addressSummary);
        _addressButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _addressButtons.AddThemeConstantOverride("h_separation", 5);
        _addressButtons.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(_addressButtons);
        foreach (var address in AddressToNation.All)
        {
            var id = address.Id;
            var button = ActionButton(address.Name, () => DeliverAddress(id), 212, 38);
            button.Name = "Address_" + id;
            _addressButtons.AddChild(button);
            _addressButtonById[id] = button;
        }
    }

    private void DeliverAddress(string id)
    {
        if (_campaign?.Player is null) return;
        var command = new AddressNationCommand { IssuerId = _campaign.Player.Id, AddressId = id };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        RefreshStateAffairs();
    }

    private void RefreshAddress(Nation nation)
    {
        if (_addressSummary is null || nation is null) return;
        var share = AddressToNation.Effectiveness(_campaign, nation);
        var summary = Simulation.Loc.Format("The country is listening: an address now lands with {0} of its full weight.", Fmt.Percent(share));
        if (nation.AddressPromise.Length > 0 && nation.AddressPromiseDueDay > _campaign.ElapsedDays &&
            AddressToNation.All.FirstOrDefault(a => a.Promise == nation.AddressPromise) is { } promised)
            summary += "\n" + Simulation.Loc.Format("Promised: {0}. It falls due in {1} day(s).", Tr(promised.Name), nation.AddressPromiseDueDay - _campaign.ElapsedDays);
        _addressSummary.Text = summary;
        foreach (var (id, button) in _addressButtonById)
        {
            var address = AddressToNation.Find(id);
            if (address is null) { button.Visible = false; continue; }
            var refusal = AddressToNation.Refusal(_campaign, nation, address);
            button.Disabled = refusal.Length > 0 || _observerMode;
            var gives = new List<string>();
            if (address.Stability != 0f) gives.Add(Simulation.Loc.Format("stability {0}", Fmt.PercentDelta(address.Stability * share)));
            if (address.WarSupport != 0f) gives.Add(Simulation.Loc.Format("war support {0}", Fmt.PercentDelta(address.WarSupport * share)));
            var tip = Tr(address.Description) + "\n" + string.Join(" · ", gives);
            if (address.Promise.Length > 0)
                tip += "\n" + Simulation.Loc.Format("Broken, in {0} days: stability {1}{2}.", address.PromiseDays,
                    Fmt.PercentDelta(-address.BrokenStability),
                    address.BrokenWarSupport > 0f ? " · " + Simulation.Loc.Format("war support {0}", Fmt.PercentDelta(-address.BrokenWarSupport)) : "");
            if (refusal.Length > 0) tip += "\n" + refusal;
            button.TooltipText = Ui.Tip(tip);
        }
    }
}
