using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How often a country looks at the ground it has lost, in days - each on a day of its own.</summary>
    public int LostLandCheckDays { get; set; } = 30;
    /// <summary>The least time, in days, between two "it is still ours" questions put to one player.</summary>
    public int LostLandAskCooldownDays { get; set; } = 720;
    /// <summary>How much stronger than its holder a country must be before its lost ground is put to it as a question.</summary>
    public float LostLandStrengthRatio { get; set; } = 1.3f;
    /// <summary>What the AI's judgement of a war makes of its own lost homeland on the other side, per its weight (see LostLands.Weight).</summary>
    public float AiRevancheAppetite { get; set; } = 0.6f;
    public float AiRevancheRelativeWeight { get; set; } = 1.2f;
    public float AiRevancheOpportunityWeight { get; set; } = 0.5f;
}

public sealed partial class Nation
{
    /// <summary>The day this country was last asked whether to take back its lost ground (LostLands).</summary>
    public int LostLandAskedDay = -100000;
}

/// <summary>
/// The ground a country has lost, and the question it keeps asking about it.
///
/// A region that seceded, rose under a movement or was signed away used to be decided about
/// once - on the day it went - and then forgotten: a country that had let a province go in a
/// bad year could not, ten years and two armies later, so much as raise the matter. Its
/// homeland (the regions it held when the world first saw it) still remembered the ground;
/// nothing asked what to do about it.
///
/// Now a country that is human is asked, on its own day once a month, when it has lost
/// homeland ground to somebody it could take it back from - stronger by
/// <see cref="Defines.LostLandStrengthRatio"/>, not bound to them by a truce or a treaty -
/// and no more often than <see cref="Defines.LostLandAskCooldownDays"/>: take it back (a war
/// over that region, as a reconquest), not yet, or let it go for good. A country nobody plays
/// weighs the same ground in its judgement of a war (<see cref="Weight"/>), beside its people
/// across the border.
/// </summary>
public static class LostLands
{
    /// <summary>Homeland regions somebody else holds now, with who: ground lost and not renounced.</summary>
    public static List<(ScenarioState Region, Nation Holder)> Of(Campaign campaign, Nation nation)
    {
        var lost = new List<(ScenarioState, Nation)>();
        if (campaign is null || nation is null) return lost;
        foreach (var region in campaign.States)
        {
            if (TerritorialClaims.TitleOf(campaign, nation, region) != TerritorialTitle.Homeland || region.NationMapIndex == nation.MapIndex) continue;
            var holder = NavalOperations.NationAt(campaign, region.NationMapIndex);
            if (holder is null || holder.Id == nation.Id || holder.Population <= 0) continue;
            // Ground our own dependency holds is ours to dispose of already; ground our overlord
            // holds is not ours to fight for.
            if (holder.AnswersTo(nation.Id) || nation.AnswersTo(holder.Id)) continue;
            lost.Add((region, holder));
        }
        return lost;
    }

    /// <summary>
    /// How much of our lost homeland the other country holds, as a weight for the AI's war
    /// judgement: millions of people in it over twelve, at most two and a half - the same scale
    /// as people of ours living across the border.
    /// </summary>
    public static float Weight(Campaign campaign, Nation nation, Nation other)
    {
        if (campaign is null || nation is null || other is null) return 0f;
        double people = 0;
        foreach (var region in campaign.StatesOf(other))
        {
            var title = TerritorialClaims.TitleOf(campaign, nation, region);
            if (title == TerritorialTitle.Homeland) people += region.Population;
            else if (title == TerritorialTitle.Claim) people += region.Population * Math.Clamp(campaign.Defines.TerritorialClaimWeight, 0, 1);
        }
        if (people <= 0 || other.AnswersTo(nation.Id) || nation.AnswersTo(other.Id)) return 0f;
        // Losing a third of a small country is not negligible next to losing the same
        // headcount from a giant one. Claims count fractionally as above; renunciation
        // removes the title. This is motive, not permission to break a treaty or a
        // substitute for the war judgement's independent readiness/deterrence gates.
        var relative = people / (Math.Max(1, nation.Population) + people);
        var memory = people / 12_000_000 + relative * Math.Max(0, campaign.Defines.AiRevancheRelativeWeight);
        var distractions = campaign.Wars.Count(w => !w.Ended && w.Involves(other.Id) && !w.Involves(nation.Id));
        var distress = M.Clamp01((.55f - other.Stability) / .55f);
        var opening = Math.Min(1f, distractions * .25f + distress * .75f);
        return M.Clamp((float)(memory * (1 + opening * Math.Max(0, campaign.Defines.AiRevancheOpportunityWeight))), 0f, 2.5f);
    }

    /// <summary>Once a day: each human country on its own day of the cycle.</summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same daily pass, yielded between countries.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(LostLands))) yield break;
        if (campaign is null || campaign.ObserverMode || campaign.ElapsedDays <= 0) yield break;
        var cycle = Math.Max(1, campaign.Defines.LostLandCheckDays);
        var scanned = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++scanned % 16 == 0) yield return 0;
            if (!nation.IsHuman || nation.Population <= 0 || nation.Movement is not null) continue;
            if (!MovementRules.OnItsDay(campaign, nation.Id, cycle)) continue;
            if (campaign.ElapsedDays - nation.LostLandAskedDay < campaign.Defines.LostLandAskCooldownDays) continue;
            Ask(campaign, nation);
        }
    }

    /// <summary>The largest piece of lost ground this country could take back now, put to it, or nothing.</summary>
    internal static PendingCampaignEvent Ask(Campaign campaign, Nation nation)
    {
        var mine = AiStatecraft.StrengthOf(campaign, nation);
        ScenarioState region = null;
        Nation holder = null;
        foreach (var (candidate, by) in Of(campaign, nation).OrderByDescending(pair => pair.Region.Population)
                     .ThenBy(pair => pair.Region.MapIndex))
        {
            if (CampaignDiplomacy.AtWar(campaign, nation.Id, by.Id)) continue;
            if (mine < AiStatecraft.StrengthOf(campaign, by) * campaign.Defines.LostLandStrengthRatio) continue;
            if (!CampaignDiplomacy.CanDeclareWar(campaign, nation.Id, by.Id, out _)) continue;
            region = candidate;
            holder = by;
            break;
        }
        if (region is null) return null;

        var id = -(campaign.ElapsedDays * 1000 + nation.Id) - 1;
        if (campaign.PendingEvents.Any(item => item.Id == id)) return null;
        nation.LostLandAskedDay = campaign.ElapsedDays;
        var item = new PendingCampaignEvent
        {
            Id = id,
            NationId = nation.Id,
            Title = Loc.Format("{0} is still ours", region.DisplayName),
            Description = Loc.Format(
                "{0} was ours, and {1} holds it - {2} people, on ground every map of ours still draws inside the border. " +
                "We are stronger than they are now. The cabinet wants to know what this country means to do about it.",
                region.DisplayName, holder.Name, region.Population.ToString("N0")),
            CreatedDay = campaign.ElapsedDays,
            ExpiresDay = campaign.ElapsedDays + 60,
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = Loc.Get("Take it back"),
                    Consequence = Loc.Format("War with {0}, declared over {1} as a reconquest.", holder.Name, region.DisplayName),
                    Effect = ScriptedEffects.From(("war_support", 0.04f)),
                    DeclareWarOn = holder.Id,
                    WarGoalStateMapIndex = region.MapIndex
                },
                new CampaignEventOption
                {
                    Title = Loc.Get("Not yet"),
                    Consequence = Loc.Get("The claim stands. The question will come back."),
                },
                new CampaignEventOption
                {
                    Title = Loc.Get("Let it go"),
                    Consequence = Loc.Format("{0} stops being ours to claim - for good. The country is calmer for it.", region.DisplayName),
                    Effect = ScriptedEffects.From(("stability", 0.02f)),
                    RenouncesClaimOn = region.MapIndex
                }
            }
        };
        campaign.PendingEvents.Add(item);
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation", $"LOST_LAND_ASKED nation={nation.Tag} region={region.Id} holder={holder.Tag} day={campaign.ElapsedDays}");
        return item;
    }

    /// <summary>Strike a region off a country's homeland: it is not theirs to claim any more.</summary>
    public static void Renounce(Campaign campaign, Nation nation, int regionMapIndex)
    {
        if (campaign?.States.Find(s => s.MapIndex == regionMapIndex) is { } region)
            TerritorialClaims.Renounce(campaign, nation, region);
    }
}
