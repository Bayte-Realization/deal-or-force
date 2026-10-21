using System;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Engine-neutral conference intent. A human opponent receives an offer;
/// only an AI opponent can sign immediately. Replays transmit selected province IDs.</summary>
public sealed class WrittenPeaceCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId;
    public int BeneficiaryId = -1;
    public int[] Provinces = Array.Empty<int>();
    public bool ReleaseRegions, Reparations, TradeConcessions, Demilitarise, Humiliate, LongTruce;
    public string Subjugate = "";
    public bool Delivered { get; private set; }
    public string Note { get; private set; } = "";
    public static WrittenPeaceCommand From(PeaceDemand d) => new()
    {
        IssuerId=d.ProposerId,WarId=d.WarId,BeneficiaryId=d.BeneficiaryId,Provinces=d.Provinces.OrderBy(id=>id).ToArray(),
        ReleaseRegions=d.ReleaseRegions,Reparations=d.Reparations,TradeConcessions=d.TradeConcessions,
        Demilitarise=d.Demilitarise,Humiliate=d.Humiliate,LongTruce=d.LongTruce,Subjugate=d.Subjugate
    };
    public void Apply(Campaign c)
    {
        Delivered=false;
        if (!MechanicFeatures.Allows(c,this)) { Note=Loc.Get("This action is disabled in this campaign.");return; }
        var war=c.Wars.FirstOrDefault(w=>w.Id==WarId);
        var draft=new PeaceDemand { WarId=WarId,ProposerId=IssuerId,BeneficiaryId=BeneficiaryId,ReleaseRegions=ReleaseRegions,
            Reparations=Reparations,TradeConcessions=TradeConcessions,Demilitarise=Demilitarise,
            Humiliate=Humiliate,LongTruce=LongTruce,Subjugate=Subjugate ?? "" };
        foreach(var p in Provinces ?? Array.Empty<int>()) draft.Provinces.Add(p);
        Note=PeaceConference.BlockReason(c,war,draft);
        if(Note.Length>0)return;
        var proposer=c.NationById(IssuerId);
        var opponent=c.NationById(war.EnemyLeaderOf(IssuerId));
        Note=PeaceOffers.DeliveryBlockReason(c,war,proposer,opponent);
        if(Note.Length>0)return;
        Delivered=PeaceOffers.Put(c,war,proposer,opponent,draft);
        Note=Delivered ? (war.Ended ? war.PeaceSummary : Loc.Get("The peace offer is awaiting their answer."))
            : Loc.Get("The offer cannot bypass a human coalition member or replace a pending offer.");
    }
}
