using System.Collections.Generic;
using System.IO;

namespace DealOrForce.Simulation;

/// <summary>Reload presentation data without reapplying campaign initial conditions.</summary>
public static class PresentationReload
{
    public static IReadOnlyList<string> Reload(IEnumerable<string> packageRoots)
    {
        var errors = new List<string>();
        InterfaceSkin.ResetCatalog(errors);
        Loc.ResetCatalog(errors);
        foreach (var root in packageRoots)
        {
            InterfaceSkin.LoadDirectory(Path.Combine(root, "interface"), errors);
            InterfaceSkin.LoadDirectory(Path.Combine(root, "gui"), errors);
            Loc.LoadDirectory(Path.Combine(root, "localization"), errors);
        }
        return errors;
    }
}
