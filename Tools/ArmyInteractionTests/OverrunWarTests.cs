using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A country overrun - all its ground lost in the war, nothing left in the field - capitulates,
/// and the war ends from the winner's side too: a country with no people left in its hands never
/// sits down to negotiate. Such wars used to run for years with the winner's army standing about
/// on a front that was not there any more.
/// </summary>
static class OverrunWarTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL overrun: " + name);
        Console.WriteLine("PASS overrun: " + name);
    }

    public static void Run(Campaign campaign)
    {
        var germany = campaign.NationByTag("DEU");
        var liechtenstein = campaign.NationByTag("LIE");
        var control = campaign.ProvinceController;
        var topology = campaign.ProvinceMap;
        var held = Enumerable.Range(1, control.Length - 1)
            .Where(province => control[province] == (ushort)liechtenstein.MapIndex).ToList();
        Check(held.Count > 0, $"Liechtenstein holds ground on the first day ({held.Count} province(s))");

        var war = CampaignDiplomacy.DeclareWar(campaign, germany.Id, liechtenstein.Id);
        Check(war is not null, "Germany can go to war with Liechtenstein");
        war.AttackerAllies.Clear();
        war.DefenderAllies.Clear();
        Check(!AiStatecraft.SideOverrun(campaign, war, attackers: false), "a country that has lost nothing is not overrun");

        foreach (var province in held)
        {
            war.OriginalController[province] = control[province];
            campaign.SetProvinceController(province, (ushort)germany.MapIndex);
            campaign.ProvinceOccupation[province] = 1f;
        }
        var standing = campaign.Armies.Where(army => army.NationId == liechtenstein.Id).ToList();
        campaign.Armies.RemoveAll(army => army.NationId == liechtenstein.Id);

        Check(AiStatecraft.SideOverrun(campaign, war, attackers: false),
            "all its ground taken in this war and nothing in the field: overrun");
        Check(!AiStatecraft.SideOverrun(campaign, war, attackers: true), "the winner is not");

        var exile = new Army { Id = int.MaxValue - 7, NationId = liechtenstein.Id, ProvinceId = held[0] };
        campaign.Armies.Add(exile);
        Check(!AiStatecraft.SideOverrun(campaign, war, attackers: false),
            "a government with an army left is fighting on, wherever the army is");
        campaign.Armies.Remove(exile);

        var switzerland = campaign.NationByTag("CHE");
        war.DefenderAllies.Add(switzerland.Id);
        Check(!AiStatecraft.SideOverrun(campaign, war, attackers: false),
            "nor is a side with an ally still in the field");
        war.DefenderAllies.Clear();

        // Settled from the winner's side: the beaten country is not asked.
        germany.IsHuman = false;
        liechtenstein.IsHuman = false;
        war.PendingOfferFromId = -1;
        war.LastPeaceProposalDay = -9999;
        AiStatecraft.Negotiate(campaign, germany, war);
        Check(war.Ended, "Germany sitting down ends it");
        Check(war.WinnerId == germany.Id, "Liechtenstein capitulated to Germany");
        // Whose the ground is afterwards is the treaty's business (PeaceShape: nothing that does
        // not touch the winner's border - Liechtenstein does not touch Germany's, so it keeps
        // it). What matters here is that none of it is left occupied by a war that is over.
        Check(held.All(province => campaign.ProvinceController[province] == topology.ProvinceToNation[province]),
            "and none of its ground is left under an occupation that no longer has a war");

        campaign.Armies.AddRange(standing);
    }
}
