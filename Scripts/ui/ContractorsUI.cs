using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>The recruitment screen's contractor shelf: veterans for the war, bought rather than raised (Contractors).</summary>
public partial class DealOrForceGame
{
    private SpinBox _contractorCount = null!;
    private Label _contractorDetails = null!;
    private Button _contractorSubmit = null!;

    private void AddContractorRows(VBoxContainer body)
    {
        body.AddChild(Title("PRIVATE MILITARY CONTRACTORS", 18, Ui.Colour("text.accent")));
        body.AddChild(Title("Veterans who arrive within days and draw no one from the reserve - for a price on signing, a fee every week and only while the war lasts.", 13, Ui.Colour("text.caption")));
        _contractorCount = new SpinBox { MinValue = 1, MaxValue = 3, Step = 1, Value = 1 };
        body.AddChild(_contractorCount);
        _contractorCount.ValueChanged += _ => RefreshContractors();
        _contractorDetails = Title("", 14, Ui.Colour("text.body_soft"));
        _contractorDetails.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_contractorDetails);
        _contractorSubmit = ActionButton("Sign the contract", () =>
        {
            var command = new HireContractorsCommand { IssuerId = _campaign.Player.Id, Formations = (int)_contractorCount.Value };
            _campaign.Commands.Enqueue(command);
            _campaign.Commands.ApplyAll(_campaign);
            RefreshContractors();
        }, 180, 38);
        body.AddChild(_contractorSubmit);
    }

    private void RefreshContractors()
    {
        if (_contractorDetails is null || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var count = (int)_contractorCount.Value;
        var refusal = Contractors.Refusal(_campaign, player, count);
        var serving = _campaign.Armies.Where(a => a.NationId == player.Id && a.ContractUntilDay >= 0).ToList();
        var signing = Contractors.SigningCost(_campaign, player);
        _contractorDetails.Text = Simulation.Loc.Format(
            "{0} formation(s) of {1:N0} veterans · signing {2} · {3} a week each · {4} days\nIn service: {5}{6}",
            count, _campaign.Defines.ContractorPersonnel, Fmt.Money(signing * count),
            Fmt.Money(signing * _campaign.Defines.ContractorWeeklyShare), _campaign.Defines.ContractorDays,
            serving.Count,
            serving.Count == 0 ? "" : Simulation.Loc.Format(" · the first contract ends in {0} days", serving.Min(a => a.ContractUntilDay) - _campaign.ElapsedDays)) +
            "\n" + (refusal.Length == 0 ? Tr("Ready to sign.") : refusal);
        _contractorSubmit.Disabled = refusal.Length > 0 || _observerMode;
    }
}
