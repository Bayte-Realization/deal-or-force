using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Full-annexation controls shared by the Peace Conference and the War Ledger detail view.
/// </summary>
public partial class DealOrForceGame
{
    private static string PeaceBrushTooltip() => Platform.PlatformProfile.Current.IsTouchPrimary
        ? "mobile.override.peace_brush_tip"
        : "Paint province by province: the left button takes, the right gives back.";

    private static string PeaceFrameTooltip() => Platform.PlatformProfile.Current.IsTouchPrimary
        ? "mobile.override.peace_frame_tip"
        : "Drag a rectangle: the left button takes everything inside it, the right gives it all back.";

    private Button CreatePeaceConferenceAnnexButton()
    {
        var annex = ActionButton(Tr("peace.annex.btn"), AnnexEnemyTerritory, 0, 34);
        annex.Name = "ConferenceAnnex";
        annex.TooltipText = Ui.Tip(Tr("peace.annex.conference_tip"));
        return annex;
    }

    /// <summary>Claim every province of the enemy war leader for total annexation (or all enemies on a second click).</summary>
    private void AnnexEnemyTerritory()
    {
        if (_peaceDemand is null || _peaceWar is null || _campaign?.Player is null || _campaign.ProvinceMap is null) return;
        var player = _campaign.Player;
        _peaceDemand.ReleaseRegions = false;
        _peaceDemand.Subjugate = "";
        var enemyLeader = _campaign.NationById(_peaceWar.EnemyLeaderOf(player.Id));
        var leaderProvinces = new List<int>();
        if (enemyLeader is not null)
        {
            var topology = _campaign.ProvinceMap;
            var leaderMap = (ushort)enemyLeader.MapIndex;
            for (var province = 1; province <= topology.ProvinceCount; province++)
                if (topology.ProvinceToNation[province] == leaderMap)
                    leaderProvinces.Add(province);
        }
        var allLeaderClaimed = leaderProvinces.Count > 0 && leaderProvinces.All(_peaceDemand.Provinces.Contains);
        if (!allLeaderClaimed && leaderProvinces.Count > 0)
        {
            foreach (var province in leaderProvinces)
                _peaceDemand.Provinces.Add(province);
        }
        else
        {
            foreach (var province in PeaceConference.Claimable(_campaign, _peaceWar, player.Id))
                _peaceDemand.Provinces.Add(province);
        }
        RefreshPeaceConference();
    }

    private void AddWarQuickAnnexButton(HFlowContainer quick, StrategicWar war, Nation player)
    {
        var annexDemand = BuildFullAnnexationDemand(war, player, out var enemyLeader);
        var annexBlocked = _observerMode
            ? Tr("Observer mode cannot issue orders.")
            : PeaceConference.BlockReason(_campaign, war, annexDemand);
        if (annexBlocked.Length == 0 && enemyLeader is not null)
            annexBlocked = PeaceOffers.DeliveryBlockReason(_campaign, war, player, enemyLeader);
        var annexButton = ActionButton(Tr("peace.annex.btn"), () => ProposeQuickAnnexation(war), 138, 30);
        annexButton.Name = "WarQuickAnnex";
        annexButton.Disabled = annexBlocked.Length > 0;
        var annexDesc = Simulation.Loc.Format("peace.annex.war_detail_desc_fmt", enemyLeader?.DisplayName ?? Tr("Unknown"));
        annexButton.TooltipText = Ui.Tip(annexBlocked.Length > 0
            ? annexBlocked + "\n" + annexDesc
            : annexDesc);
        quick.AddChild(annexButton);
    }

    private PeaceDemand BuildFullAnnexationDemand(StrategicWar war, Nation player, out Nation enemyLeader)
    {
        enemyLeader = war is null || player is null ? null : _campaign?.NationById(war.EnemyLeaderOf(player.Id));
        var demand = new PeaceDemand { WarId = war?.Id ?? -1, ProposerId = player?.Id ?? -1 };
        if (enemyLeader is null || _campaign?.ProvinceMap is null) return demand;
        var topology = _campaign.ProvinceMap;
        var leaderMap = (ushort)enemyLeader.MapIndex;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToNation[province] == leaderMap)
                demand.Provinces.Add(province);
        return demand;
    }

    private void ProposeQuickAnnexation(StrategicWar war)
    {
        if (war is null || war.Ended || _campaign?.Player is not { } player || _observerMode) return;
        var demand = BuildFullAnnexationDemand(war, player, out _);
        var wasEnded = war.Ended;
        var command = WrittenPeaceCommand.From(demand);
        CommitImmediateCampaignCommand(command, refreshHud: false, wakeGlobe: true);
        if (command.Delivered && !wasEnded && war.Ended) PlayStrategicCue("peace_signed");
        if (command.Note.Length > 0) SetArmyFeedback(command.Note);
        if (war.Ended) ShowWarLedgerList();
        Refresh();
        if (IsNewShellActive) RefreshNewShell(force: true);
        RefreshWarLedger();
    }
}
