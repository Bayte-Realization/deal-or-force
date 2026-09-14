namespace DealOrForce.Simulation;

/// <summary>Stable localization keys, specific politics before the neutral identity.</summary>
public static class CountryNames
{
    public static string Resolve(Nation nation, bool map)
    {
        var identity = string.IsNullOrEmpty(nation.FormedNationId) ? "country." + nation.Tag : "formable." + nation.FormedNationId;
        var role = map ? ".map" : ".name";
        foreach (var key in new[] { identity + ".government." + nation.Government + ".ideology." + nation.Ideology + role,
                     identity + ".government." + nation.Government + role, identity + ".ideology." + nation.Ideology + role,
                     identity + role })
            if (Loc.Has(key)) return Loc.Get(key);
        return map ? Resolve(nation, false) : Loc.Get(nation.Name);
    }
}
