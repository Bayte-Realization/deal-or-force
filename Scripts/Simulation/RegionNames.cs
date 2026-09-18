namespace DealOrForce.Simulation;

/// <summary>
/// What a region is called, asked for by its id rather than by its English name.
///
/// The same argument as <see cref="CountryNames"/>, and a sharper one. A country's English
/// name is at least unique among countries; a region's is not unique among regions. Six
/// different regions are called "Southern" - in Israel, Sierra Leone, Botswana, Zambia and
/// two more - and four are called "Amazonas", in Brazil, Colombia, Peru and Venezuela. Keyed
/// by name, all six of the first share one translation and all four of the second share
/// another, because the table is flat and cannot tell them apart.
///
/// So the id is asked first. It is the region's identity everywhere else in this game -
/// saves carry it, the workshop edits it, a border that splits a region derives the halves'
/// ids from it - and it is three to eight characters that mean one region.
///
/// The English name remains the fallback and the index. Nothing breaks without a row here;
/// it simply goes back to being ambiguous, which is what it was.
/// </summary>
public static class RegionNames
{
    /// <summary>The name a player should see for this region.</summary>
    public static string Resolve(ScenarioState state)
    {
        if (state is null) return "";
        if (!string.IsNullOrEmpty(state.Id))
        {
            var key = "region." + state.Id + ".name";
            if (Loc.Has(key)) return Loc.Get(key);
        }
        return Loc.Get(state.Name);
    }
}
