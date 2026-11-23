namespace DealOrForce.Simulation;

/// <summary>Commission a new Strategic Megaproject in an owned region.</summary>
public sealed record StartGrandProjectCommand(int IssuerId, string ProjectId, int StateMapIndex) : ICampaignCommand
{
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var state = campaign.StateByMapIndex(StateMapIndex);
        if (nation is null || state is null) return;
        GrandProjects.Start(campaign, nation, state, ProjectId);
    }
}

/// <summary>Fund and launch the next construction stage (II or III) of an owned megaproject.</summary>
public sealed record AdvanceGrandProjectStageCommand(int IssuerId, int InstanceId) : ICampaignCommand
{
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        GrandProjects.AdvanceStage(campaign, nation, InstanceId);
    }
}

/// <summary>Mothball (pause) or resume an in-progress Strategic Megaproject without losing progress.</summary>
public sealed record ToggleGrandProjectPauseCommand(int IssuerId, int InstanceId, bool Paused) : ICampaignCommand
{
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        GrandProjects.SetPaused(campaign, nation, InstanceId, Paused);
    }
}

/// <summary>Abandon an unfinished Strategic Megaproject for partial treasury salvage.</summary>
public sealed record CancelGrandProjectCommand(int IssuerId, int InstanceId) : ICampaignCommand
{
    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        GrandProjects.Cancel(campaign, nation, InstanceId);
    }
}

/// <summary>Join an allied or organization partner's megaproject as a co-funding Consortium Partner.</summary>
public sealed record JoinGrandProjectConsortiumCommand(int IssuerId, int InstanceId) : ICampaignCommand
{
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        GrandProjects.JoinConsortium(campaign, nation, InstanceId);
    }
}

/// <summary>Run covert sabotage against a rival's actively constructing megaproject via IntelNetworks.</summary>
public sealed record SabotageGrandProjectCommand(int IssuerId, int InstanceId) : ICampaignCommand
{
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        GrandProjects.Sabotage(campaign, nation, InstanceId);
    }
}
