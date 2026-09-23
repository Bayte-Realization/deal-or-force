using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Campaign
{
    /// <summary>
    /// This campaign's own options - the ones common/settings marks `"scope": "campaign"` -
    /// by id, as text, and saved with the campaign (item 55 on the list).
    ///
    /// How long a step of the world is and how often it saves itself are facts about a
    /// campaign, not about the person playing it. They were the player's, so a campaign
    /// played at a daily step came back from a save at whatever step had been set since.
    /// See SettingDefinition.Campaign for how they are read and written.
    /// </summary>
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
}
