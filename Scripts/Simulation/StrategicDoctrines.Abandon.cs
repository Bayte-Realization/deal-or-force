namespace DealOrForce.Simulation;

public static partial class StrategicDoctrines
{
    /// <summary>
    /// Only a country that has never adopted a strategic course gets its first choice for free.
    /// After abandoning a course (<see cref="StrategicDoctrineMemory.LastChangeDay"/> &gt;= 0),
    /// adopting another pays its full political price so abandoning cannot bypass switching costs.
    /// </summary>
    public static bool IsFirstChoiceFree(Nation nation) =>
        nation is not null && nation.StrategicDoctrine.Id.Length == 0 && nation.StrategicDoctrine.LastChangeDay < 0;

    /// <summary>Why the active strategic course cannot be abandoned now. Empty means it can.</summary>
    public static string AbandonRefusal(Campaign campaign, Nation nation)
    {
        if (campaign is null || !campaign.FeatureEnabled("strategic_doctrines"))
            return Loc.Get("This mechanic is disabled in this scenario.");
        if (nation is null || Of(nation) is null)
            return Loc.Get("doctrine.err.no_strategy");
        return "";
    }

    /// <summary>
    /// Abandon the active strategic course. Free (0 political points), clears its modifiers immediately,
    /// records <see cref="StrategicDoctrineMemory.LastChangeDay"/> so subsequent adoptions pay full cost,
    /// and logs a public <c>strategy_abandoned</c> chronicle entry.
    /// </summary>
    public static bool Abandon(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || AbandonRefusal(campaign, nation).Length > 0) return false;
        var course = Of(nation);
        if (course is null) return false;
        nation.StrategicDoctrine.Id = "";
        nation.StrategicDoctrine.LastChangeDay = campaign.ElapsedDays;
        NationalModifiers.Invalidate(nation);
        campaign.Chronicle.Record(campaign, "strategy_abandoned", nation.Id, subject: course.Name, isPublic: true);
        campaign.MarkStrategicChange();
        return true;
    }
}

public sealed class AbandonStrategicDoctrineCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>Why the order could not be carried out, in the words the interface shows.</summary>
    public string Refusal { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        Refusal = StrategicDoctrines.AbandonRefusal(campaign, nation);
        if (Refusal.Length > 0) return;
        StrategicDoctrines.Abandon(campaign, nation);
    }
}
