using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Every movement the catalogue declares is actually in the world.
///
/// A movement names its headquarters, its homeland and its support by region id, and nothing checks
/// those ids. Movements.FoundDefinitions looks the headquarters up and, finding nothing, `continue`s
/// - the movement is simply never founded, and no error is raised anywhere. A support entry naming a
/// region that does not exist is dropped the same way. So a typo in a region id produces a movement
/// that is absent, or present with no following, and neither of those announces itself: the world
/// just has one fewer movement in it than the file says.
///
/// This asks the world instead of the file. A fresh campaign is built, and every movement the
/// catalogue says should be there has to have been founded, to claim regions that exist, and to
/// have had the support it writes land on the region it was written for.
/// </summary>
static class MovementCatalogTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("movements: " + label); Console.WriteLine("PASS movements: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261016);

        // The game founds movements when the map arrives (SimulationCore), not when the campaign is
        // built, so a bootstrap campaign has none. This asks for them the way the game does, which
        // is also what makes the check below about the data rather than about which method ran.
        Movements.Found(c);
        Movements.Place(c);

        // Templates exist to be copied, not founded, and an entry with no tag is not a movement yet.
        var declared = MovementCatalog.All.Where(d => !d.Template && !string.IsNullOrWhiteSpace(d.Tag)).ToList();
        var active = declared.Where(d => d.StartActive).ToList();
        Check(active.Count >= 20, $"the catalogue declares the world's movements ({active.Count} of them)");

        var founded = new Dictionary<string, MovementState>(StringComparer.Ordinal);
        foreach (var nation in c.Nations)
            if (nation.Movement is not null) founded[nation.Movement.Id] = nation.Movement;

        // A movement that names a seat and is not founded is the failure this exists for: the region
        // id did not resolve, and the world quietly has one movement fewer than the file says.
        var withSeat = active.Where(d => d.Headquarters.Length > 0).ToList();
        var missing = withSeat.Where(d => !founded.ContainsKey(d.Id)).Select(d => $"{d.Id}/{d.Tag}").ToList();
        Check(missing.Count == 0, $"every movement that names a seat is founded ({withSeat.Count} of them)" +
            (missing.Count > 0 ? $" - never founded: {string.Join(", ", missing.Take(8))}" : ""));
        // And the other way round: a movement with nowhere to sit cannot be placed, so founding one
        // would mean the seat was being read from somewhere nobody wrote it.
        var seatless = declared.Where(d => d.Headquarters.Length == 0).Select(d => d.Id).ToList();
        var wronglyFounded = seatless.Where(id => founded.ContainsKey(id)).ToList();
        Check(wronglyFounded.Count == 0, $"and a movement that names no seat is not founded ({seatless.Count} name none)" +
            (wronglyFounded.Count > 0 ? $": {string.Join(", ", wronglyFounded)}" : ""));

        // The schema the rest of the game reads them through.
        var malformed = declared
            .Where(d => d.Tag.Length != 3 || d.Name.Length == 0 || d.Description.Length == 0 ||
                        d.Colour.Length < 4 || d.Government.Length == 0 || d.Ideology.Length == 0)
            .Select(d => d.Id).ToList();
        Check(malformed.Count == 0, "and each has the tag, name, description, colour and politics it needs" +
            (malformed.Count > 0 ? $": {string.Join(", ", malformed.Take(6))}" : ""));

        // A tag is a country's identity: two movements sharing one would collide on the same key.
        var duplicateTags = declared.GroupBy(d => d.Tag, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        Check(duplicateTags.Count == 0, "and no two movements claim the same tag" +
            (duplicateTags.Count > 0 ? $": {string.Join(", ", duplicateTags)}" : ""));

        // Region ids, which are where the silence is.
        var unknownRegions = new List<string>();
        var unsupported = new List<string>();
        foreach (var definition in declared)
        {
            var seat = definition.Headquarters.Length > 0 && c.States.Any(state => state.Id == definition.Headquarters);
            if (definition.Headquarters.Length > 0 && !seat)
                unknownRegions.Add($"{definition.Id} seat {definition.Headquarters}");
            foreach (var regionId in definition.Homeland)
                if (!c.States.Any(state => state.Id == regionId))
                    unknownRegions.Add($"{definition.Id} homeland {regionId}");
            if (!founded.TryGetValue(definition.Id, out var movement)) continue;
            foreach (var regionId in definition.Support.Keys)
            {
                var state = c.States.FirstOrDefault(candidate => candidate.Id == regionId);
                if (state is null) { unknownRegions.Add($"{definition.Id} support {regionId}"); continue; }
                // Named, and existent, is not the same as honoured: the following it writes has to
                // have landed on the region it was written for rather than been dropped.
                if (!movement.Support.ContainsKey(state.MapIndex)) unsupported.Add($"{definition.Id} -> {regionId}");
            }
        }
        Check(unknownRegions.Count == 0, "every region a movement names is a region the world has" +
            (unknownRegions.Count > 0 ? $" - unknown: {string.Join(", ", unknownRegions.Take(6))}" : ""));
        Check(unsupported.Count == 0, "and the following it claims in a region landed in that region" +
            (unsupported.Count > 0 ? $" - dropped: {string.Join(", ", unsupported.Take(6))}" : ""));

        // A movement that speaks for nobody can never raise a cadre or a donation.
        var voiceless = declared.Where(d => d.Peoples.Count == 0).Select(d => d.Id).ToList();
        Check(voiceless.Count == 0, "and every movement speaks for at least one people" +
            (voiceless.Count > 0 ? $": {string.Join(", ", voiceless.Take(6))}" : ""));

        var noHomeland = declared.Where(d => d.Homeland.Count == 0).Select(d => d.Id).ToList();
        Check(noHomeland.Count == 0, "and claims at least one region as its homeland" +
            (noHomeland.Count > 0 ? $": {string.Join(", ", noHomeland.Take(6))}" : ""));
    }
}
