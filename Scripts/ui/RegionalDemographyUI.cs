using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void DetailDemographicReference()
    {
        var reference = RegionalDemography.ReferenceFor(_campaign, _selectedState);
        DetailHeading("DATA AND ESTIMATES");
        if (reference is null)
            DemographicNote("Starting cultural composition is modelled; no regional source record is attached yet.",
                "Faith shares are model estimates, not measured by this ethnic reference. Ethnicity does not determine political views.");
        else
        {
            var areas = string.Join(" + ", reference.Areas.Select(area => Loc.Get(area.Name)));
            var pages = string.Join(", ", reference.Areas.Select(area => area.Page));
            var method = Tr("Reference counts are normalised over mapped groups for the starting model. Current population and shares evolve in the simulation; they are not a new census.") + "\n" +
                Tr("This is a geographic reference; scenario and mod overrides may use a different starting composition.");
            DetailRow("Ethnic reference", Loc.Format("Census · {0}", reference.Date.Year),
                method + "\n\n" + Loc.Format("{0}\nTable {1}, pages {2}\n{3}\nReference population: {4}\n{5}",
                    Loc.Get(reference.Source), reference.Table, pages, areas,
                    Fmt.PeopleExact(reference.Population), reference.Url));
            DetailRow("Mapped source coverage", Loc.Format("{0:P1}", reference.Coverage),
                Loc.Format("Mapped: {0}\nUnmapped category: {1}\nNot listed in this table: {2}",
                    Fmt.PeopleExact(reference.Mapped), Fmt.PeopleExact(reference.Unmapped), Fmt.PeopleExact(reference.Unlisted)));
            DemographicNote("Historical reference, not a live census. Faith shares are model estimates.",
                "Faith shares are model estimates, not measured by this ethnic reference. Ethnicity does not determine political views.");
            if (reference.RegionId != _selectedState.Id)
                DemographicNote("Parent-region reference; no district-level census is available here.");
        }
    }

    private void DemographicNote(string text, string tooltip = null)
    {
        var note = Title(text, 12, Ui.Colour("text.muted"));
        note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.CustomMinimumSize = Vector2.Zero;
        if (tooltip is not null) note.TooltipText = Ui.Tip(Tr(tooltip));
        _sideDetail.AddChild(note);
    }
}
