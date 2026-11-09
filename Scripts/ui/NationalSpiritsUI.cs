using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// A country's national spirits and what its modifiers add up to, on its card.
///
/// The spirits had nowhere to be seen but a line of the ledger, so a country could be a year
/// into a war economy or three years into a humiliation without the player knowing it. Now the
/// card lists each spirit with the days it has left, and under the cursor what it is and exactly
/// what it does; and one more row says what the government, the ideology, the cabinet, the
/// spirits and the research add up to, and where each part of it comes from.
/// </summary>
public partial class DealOrForceGame
{
    private void AddSpiritRows(VBoxContainer rows, Nation nation)
    {
        if (nation is null || _campaign is null) return;
        rows.AddChild(Title("NATIONAL SPIRITS", 11, Ui.Colour("text.caption")));
        var today = _campaign.ElapsedDays;
        foreach (var active in nation.Modifiers)
        {
            var spirit = ModifierCatalog.Get(active.Id);
            if (spirit is null) continue;
            var left = active.IsPermanent ? "" : Simulation.Loc.Format("{0} days left", active.DaysRemaining(today));
            var tip = Tr(spirit.Name) + "\n" + Tr(spirit.Description);
            var modifiers = NationalModifiers.Block(spirit.Modifiers);
            if (modifiers.Length > 0) tip += "\n\n" + modifiers;
            var drifts = ScriptedEffects.Block(spirit.Effects);
            if (drifts.Length > 0) tip += "\n\n" + Tr("For as long as it lasts") + "\n" + drifts;
            CardRow(rows, Tr(spirit.Name), left, tip, spirit.IsPositive ? "state.positive" : "state.negative");
        }

        // The same national sum the army uses, including the same-shelf doctrine ceiling.
        // Each source below is still the book or spirit itself, named whole.
        var totals = new Dictionary<string, float>();
        foreach (var key in NationalModifiers.Keys)
        {
            var value = NationalModifiers.Of(nation, key.Id);
            if (value != 0f) totals[key.Id] = value;
        }
        var sources = NationalModifiers.Sources(nation).ToList();
        var lines = NationalModifiers.Lines(totals);
        string summary;
        if (lines.Count == 0) summary = Tr("Nothing adds to or takes from the country's numbers.");
        else
        {
            summary = Tr("What the country's government, ideology, cabinet, spirits and research add up to:") + "\n" +
                      string.Join("\n", lines.Select(line => "  " + line));
            foreach (var (source, modifiers) in sources)
            {
                var own = NationalModifiers.Lines(modifiers);
                if (own.Count == 0) continue;
                summary += "\n\n" + Tr(source) + "\n" + string.Join("\n", own.Select(line => "  " + line));
            }
        }
        CardRow(rows, "All modifiers", lines.Count.ToString("N0"), summary);
    }
}
