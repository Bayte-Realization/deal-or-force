using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DealOrForce.Simulation;

public static partial class FlagCatalog
{
    private sealed class WarFlagView
    {
        internal int Revision = -1;
        internal int Count = -1;
        internal readonly List<StrategicWar> Active = new();
    }

    // Derived references only, scoped to the world, not a second war model or saved state.
    // A view must never call the military live-list refresh: that pass can clean ended fronts.
    private static readonly ConditionalWeakTable<Campaign, WarFlagView> WarFlagViews = new();
    internal static long WarFlagHistoryPasses { get; private set; }

    private static List<StrategicWar> WarsForFlags(Campaign campaign)
    {
        var view = WarFlagViews.GetValue(campaign, static _ => new WarFlagView());
        var changed = view.Revision != campaign.WarRevision || view.Count != campaign.Wars.Count;
        if (!changed)
            for (var i = 0; i < view.Active.Count; i++)
                if (view.Active[i].Ended) { changed = true; break; }
        if (!changed) return view.Active;

        view.Active.Clear();
        foreach (var war in campaign.Wars)
            if (!war.Ended) view.Active.Add(war);
        view.Count = campaign.Wars.Count;
        view.Revision = campaign.WarRevision;
        WarFlagHistoryPasses++;
        return view.Active;
    }
}
