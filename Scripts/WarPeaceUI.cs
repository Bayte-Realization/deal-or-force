using System.Linq;
using ByDealOrForce.Simulation;
using Godot;

namespace ByDealOrForce;

public partial class GlobalityGame
{
    private VBoxContainer _warControls = null!;
    private Label _peaceStatus = null!;

    private void BuildWarControls(VBoxContainer parent)
    {
        _warControls = new VBoxContainer { Visible = false };
        _warControls.AddThemeConstantOverride("separation", 6);
        parent.AddChild(_warControls);
        _warControls.AddChild(Title("PEACE NEGOTIATION", 12, Ui.Colour("text.caption")));
        var ceasefire = ActionButton("Ceasefire", () => ProposePeace(PeaceTerms.Ceasefire), 150, 32);
        ceasefire.TooltipText = PeaceNegotiation.Description(PeaceTerms.Ceasefire);
        _warControls.AddChild(ceasefire);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        _warControls.AddChild(row);
        var whitePeace = ActionButton("White peace", () => ProposePeace(PeaceTerms.WhitePeace), 150, 32);
        whitePeace.TooltipText = "Restore every wartime occupation and end hostilities without territorial changes.";
        row.AddChild(whitePeace);
        var reparations = ActionButton("Reparations", () => ProposePeace(PeaceTerms.WarReparations), 155, 32);
        reparations.TooltipText = "Restore the pre-war border and demand an immediate, economy-scaled payment.";
        row.AddChild(reparations);

        var concessionsRow = new HBoxContainer();
        concessionsRow.AddThemeConstantOverride("separation", 5);
        _warControls.AddChild(concessionsRow);
        var trade = ActionButton("Trade concessions", () => ProposePeace(PeaceTerms.TradeConcessions), 150, 32, true);
        trade.TooltipText = PeaceNegotiation.Description(PeaceTerms.TradeConcessions);
        concessionsRow.AddChild(trade);
        var demilitarize = ActionButton("Demilitarize", () => ProposePeace(PeaceTerms.DemilitarizedSettlement), 155, 32, true);
        demilitarize.TooltipText = PeaceNegotiation.Description(PeaceTerms.DemilitarizedSettlement);
        concessionsRow.AddChild(demilitarize);

        var termsRow = new HBoxContainer();
        termsRow.AddThemeConstantOverride("separation", 5);
        _warControls.AddChild(termsRow);
        var warGoal = ActionButton("War goal", () => ProposePeace(PeaceTerms.EnforceWarGoal), 150, 32, true);
        warGoal.TooltipText = "Keep secured occupations only inside the declared strategic objective.";
        termsRow.AddChild(warGoal);
        var demands = ActionButton("Hold occupied", () => ProposePeace(PeaceTerms.EnforceDemands), 155, 32, true);
        demands.TooltipText = "Retain all occupations secured above the scenario threshold; evacuate exposed advances.";
        termsRow.AddChild(demands);
        var capitulate = ActionButton("Capitulate", () => ProposePeace(PeaceTerms.Capitulate), 310, 30);
        capitulate.TooltipText = "Concede the war immediately and accept the current occupation map.";
        _warControls.AddChild(capitulate);
        _peaceStatus = Title("", 12, Ui.Colour("text.accent"));
        _peaceStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _warControls.AddChild(_peaceStatus);
    }

    private StrategicWar PlayerWar() => _campaign.Wars.FirstOrDefault(war => !war.Ended && war.Involves(_campaign.Player.Id));

    private void RefreshWarControls()
    {
        var war = PlayerWar();
        _warControls.Visible = _openScreen == "Wars" && war is not null;
        if (war is null) return;
        var white = PeaceNegotiation.BlockReason(_campaign, war, _campaign.Player.Id, PeaceTerms.WhitePeace);
        var ceasefire = PeaceNegotiation.BlockReason(_campaign, war, _campaign.Player.Id, PeaceTerms.Ceasefire);
        var reparations = PeaceNegotiation.BlockReason(_campaign, war, _campaign.Player.Id, PeaceTerms.WarReparations);
        var warGoal = PeaceNegotiation.BlockReason(_campaign, war, _campaign.Player.Id, PeaceTerms.EnforceWarGoal);
        var trade = PeaceNegotiation.BlockReason(_campaign, war, _campaign.Player.Id, PeaceTerms.TradeConcessions);
        var demilitarize = PeaceNegotiation.BlockReason(_campaign, war, _campaign.Player.Id, PeaceTerms.DemilitarizedSettlement);
        var enforce = PeaceNegotiation.BlockReason(_campaign, war, _campaign.Player.Id, PeaceTerms.EnforceDemands);
        var whiteAcceptance = PeaceNegotiation.Acceptance(_campaign, war, _campaign.Player.Id, PeaceTerms.WhitePeace);
        var ceasefireAcceptance = PeaceNegotiation.Acceptance(_campaign, war, _campaign.Player.Id, PeaceTerms.Ceasefire);
        var reparationsAcceptance = PeaceNegotiation.Acceptance(_campaign, war, _campaign.Player.Id, PeaceTerms.WarReparations);
        var warGoalAcceptance = PeaceNegotiation.Acceptance(_campaign, war, _campaign.Player.Id, PeaceTerms.EnforceWarGoal);
        var tradeAcceptance = PeaceNegotiation.Acceptance(_campaign, war, _campaign.Player.Id, PeaceTerms.TradeConcessions);
        var demilitarizeAcceptance = PeaceNegotiation.Acceptance(_campaign, war, _campaign.Player.Id, PeaceTerms.DemilitarizedSettlement);
        var enforceAcceptance = PeaceNegotiation.Acceptance(_campaign, war, _campaign.Player.Id, PeaceTerms.EnforceDemands);
        _peaceStatus.Text =
            $"Ceasefire · cost {PeaceNegotiation.DemandCost(PeaceTerms.Ceasefire):0} · acceptance {ceasefireAcceptance:0}%: {(ceasefire.Length == 0 ? "AVAILABLE" : ceasefire)}\n" +
            $"White peace · acceptance {whiteAcceptance:0}%: {(white.Length == 0 ? "AVAILABLE" : white)}\n" +
            $"Reparations · acceptance {reparationsAcceptance:0}%: {(reparations.Length == 0 ? "AVAILABLE" : reparations)}\n" +
            $"Trade concessions · cost {PeaceNegotiation.DemandCost(PeaceTerms.TradeConcessions):0} · acceptance {tradeAcceptance:0}%: {(trade.Length == 0 ? "AVAILABLE" : trade)}\n" +
            $"Demilitarize · cost {PeaceNegotiation.DemandCost(PeaceTerms.DemilitarizedSettlement):0} · acceptance {demilitarizeAcceptance:0}%: {(demilitarize.Length == 0 ? "AVAILABLE" : demilitarize)}\n" +
            $"War goal · acceptance {warGoalAcceptance:0}%: {(warGoal.Length == 0 ? "AVAILABLE" : warGoal)}\n" +
            $"Enforce terms · acceptance {enforceAcceptance:0}%: {(enforce.Length == 0 ? "AVAILABLE" : enforce)}" +
            (war.LastNegotiation.Length == 0 ? "" : $"\nLast proposal: {war.LastNegotiation}");
    }

    private void ProposePeace(PeaceTerms terms)
    {
        var war = PlayerWar();
        if (war is null) return;
        var wasEnded = war.Ended;
        _campaign.Commands.Enqueue(new ProposePeaceCommand { IssuerId = _campaign.Player.Id, WarId = war.Id, Terms = terms });
        _campaign.Commands.ApplyAll(_campaign);
        if (!wasEnded && war.Ended) PlayStrategicCue("peace_signed");
        OpenScreen("Wars");
        Refresh();
    }

    private string PeaceHistory()
    {
        var ended = _campaign.Wars.Where(war => war.Ended).OrderByDescending(war => war.EndedDay).Take(4).ToArray();
        if (ended.Length == 0) return "";
        return "\n\nRECENT PEACE SETTLEMENTS\n\n" + string.Join("\n\n", ended.Select(war =>
            $"{war.Name} · day {war.EndedDay:N0}\n{war.PeaceSummary}"));
    }
}
