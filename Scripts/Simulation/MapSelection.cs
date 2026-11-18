using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The one rule a selection on the map follows, whatever is being selected - divisions, long-range
/// formations, or both in one box: a plain pick replaces the selection, "adding" puts the picked
/// ones beside it, "removing" takes them out of it. Which key means which is the interface's to
/// say (ArmyOperationsUI.AddingToSelection / RemovingFromSelection); that the two kinds of thing
/// on the map answer the same keys the same way is said here, once, so it cannot drift apart.
/// </summary>
public static class MapSelection
{
    public static void Apply<T>(ICollection<T> selection, IEnumerable<T> picked, bool adding, bool removing)
    {
        if (selection is null || picked is null) return;
        if (removing)
        {
            foreach (var item in picked) selection.Remove(item);
            return;
        }
        if (!adding) selection.Clear();
        foreach (var item in picked)
            if (!selection.Contains(item)) selection.Add(item);
    }
}
