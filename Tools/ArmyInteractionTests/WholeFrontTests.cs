using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The whole front (DrawnFrontLine.AllEnemies): one assignment puts an army group on every
/// stretch of land border with every country at war with it - across separate wars and
/// co-belligerents alike - the line grows when an enemy joins and shrinks when one makes
/// peace, and nobody on it is left without an order when part of the war ends.
///
/// Ukraine is the player. Russia's war is the bootstrap's; Belarus opens a war of its own
/// (a separate war, so that it can make a separate peace); Moldova later joins Russia's war
/// as a co-belligerent.
/// </summary>
static class WholeFrontTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    static void NextDay(Campaign c) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, c.ElapsedHours + 24L);

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20261101);
        c.Defines.HoursPerTick = 24;
        c.AttachProvinceTopology(map);
        var ukr = c.NationByTag("UKR"); var rus = c.NationByTag("RUS");
        var blr = c.NationByTag("BLR"); var mda = c.NationByTag("MDA");
        Check(ukr is not null && rus is not null && blr is not null && mda is not null, "Ukraine, Russia, Belarus and Moldova exist");
        c.ConfigureSinglePlayer(ukr.Id);
        c.Battles.Clear();
        var russiaWar = c.Wars.First(w => !w.Ended && w.Opposes(ukr.Id, rus.Id));
        Warfare.RebuildFronts(c);

        var rusBorder = BorderFronts.Candidates(c, ukr.Id, rus.Id);
        var blrBorder = BorderFronts.Candidates(c, ukr.Id, blr.Id);
        var mdaBorder = BorderFronts.Candidates(c, ukr.Id, mda.Id);
        Check(rusBorder.Count > 0 && blrBorder.Count > 0 && mdaBorder.Count > 0,
            $"Ukraine borders Russia ({rusBorder.Count}), Belarus ({blrBorder.Count}) and Moldova ({mdaBorder.Count})");

        var group = c.Armies.Where(a => a.NationId == ukr.Id && a.TemplateId != MilitiaCore.TemplateId && a.EmbarkedFleetId < 0)
            .OrderBy(a => a.Id).ToList();
        foreach (var army in group)
        {
            army.BattleId = -1; army.DirectOrder = false; army.Route.Clear(); army.PlannedRoute.Clear();
        }
        var ids = group.Select(a => a.Id).ToList();
        Check(group.Count >= 20, $"Ukraine fields an army group of {group.Count} divisions");

        // --- Peace with everybody: nothing to hold, nothing changed ---------------------------
        var pol = c.NationByTag("POL");
        var polArmies = c.Armies.Where(a => a.NationId == pol.Id).Select(a => a.Id).ToList();
        var linesBefore = c.FrontLines.Count;
        new DrawFrontLineCommand { IssuerId = pol.Id, Kind = FrontLineKind.Defence, AllEnemies = true, ArmyIds = polArmies }.Apply(c);
        Check(c.FrontLines.Count == linesBefore, "a country at war with nobody gets no whole front");

        // --- One war: the whole front is Russia's border ---------------------------------------
        var assign = new DrawFrontLineCommand { IssuerId = ukr.Id, Kind = FrontLineKind.Defence, AllEnemies = true, ArmyIds = ids };
        Check(NetworkCommandCodec.TryEncode(assign, out var type, out var json) &&
              NetworkCommandCodec.TryDecode(type, json, out var decoded) &&
              decoded is DrawFrontLineCommand { AllEnemies: true }, "the whole-front order survives the network codec");
        assign.Apply(c);
        var line = c.FrontLines.Single(l => l.NationId == ukr.Id);
        Check(line.AllEnemies && line.BorderNationId < 0 && line.Kind == FrontLineKind.Defence, "one line, against every enemy rather than one country");
        Check(line.Provinces.SetEquals(rusBorder), $"with one enemy it is exactly the Russian border ({line.Provinces.Count} posts)");
        Check(group.All(a => a.FrontLineId == line.Id && a.OrderKind == ArmyOrder.DefendFront), "the whole army group is on it, holding");
        Check(group.All(a => a.OrderProvinceId > 0 && rusBorder.Contains(a.OrderProvinceId)), "and every division is sent to a post on it");

        // --- Belarus declares a war of its own: the line grows by itself -----------------------
        var blrWar = new StrategicWar
        {
            Id = c.Wars.Max(w => w.Id) + 1, Name = "Belarus-Ukraine War", AttackerId = blr.Id, DefenderId = ukr.Id,
            // Begun long enough ago for a white peace to be on the table.
            StartedDay = c.ElapsedDays - 720
        };
        c.Wars.Add(blrWar);
        CampaignDiplomacy.Get(c, blr.Id, ukr.Id).Stance = DiplomaticStance.AtWar;
        c.MarkWarsChanged();
        Warfare.RebuildFronts(c);
        Check(BattlePlanning.LineFor(c, group[0]) == line, "the same line, no new order given");
        var both = new HashSet<int>(rusBorder); both.UnionWith(blrBorder);
        Check(line.Provinces.SetEquals(both), $"it now runs along Belarus too ({line.Provinces.Count} posts, two wars)");
        for (var day = 0; day < 20; day++) { NextDay(c); FrontManager.Tick(c); }
        var onRussia = group.Count(a => rusBorder.Contains(a.OrderProvinceId) && !blrBorder.Contains(a.OrderProvinceId));
        var onBelarus = group.Count(a => blrBorder.Contains(a.OrderProvinceId));
        Check(onRussia > 0 && onBelarus > 0, $"the staff spreads the group over both: {onRussia} facing Russia, {onBelarus} facing Belarus");
        Check(group.All(a => a.OrderKind == ArmyOrder.DefendFront && line.Provinces.Contains(a.OrderProvinceId)),
            "every division holds a post on the whole front");

        // An order given now names the war each post faces.
        foreach (var army in group)
            new SetArmyOrderCommand { IssuerId = ukr.Id, ArmyId = army.Id, Order = ArmyOrder.DefendFront }.Apply(c);
        var facingBelarus = group.Where(a => blrBorder.Contains(a.OrderProvinceId) && !rusBorder.Contains(a.OrderProvinceId)).ToList();
        Check(facingBelarus.Count > 0 && facingBelarus.All(a => a.AssignedWarId == blrWar.Id),
            $"the {facingBelarus.Count} division(s) re-ordered onto the Belarusian stretch serve the war with Belarus");

        // --- Save and reload: the line is still the whole front --------------------------------
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "whole-front"), "whole-front");
        Check(saved.Success, "the campaign saves and reloads");
        var restored = saved.Campaign.FrontLines.Single(l => l.NationId == ukr.Id);
        // A network snapshot carries no map; the game attaches its own on load.
        saved.Campaign.AttachProvinceTopology(map);
        Array.Copy(c.ProvinceController, saved.Campaign.ProvinceController, c.ProvinceController.Length);
        BorderFronts.Refresh(saved.Campaign, restored);
        Check(restored.AllEnemies && restored.Provinces.SetEquals(both) && restored.ArmyIds.Count == group.Count,
            "after a reload it is still the whole front, with the same roster");
        Check(CampaignFingerprint.Of(saved.Campaign) == CampaignFingerprint.Of(c), "the reload fingerprints the same as the original");

        // --- Belarus makes peace: the line shrinks, nobody is stood down ------------------------
        PeaceNegotiation.Resolve(c, blrWar, blr.Id, PeaceTerms.WhitePeace);
        Check(blrWar.Ended, "Belarus has made a white peace" + (blrWar.Ended ? "" : " - refused: " + blrWar.LastNegotiation));
        Warfare.RebuildFronts(c);
        Check(c.FrontLines.Contains(line) && BattlePlanning.LineFor(c, group[0]) == line, "the whole front is still there");
        Check(line.Provinces.SetEquals(rusBorder), $"and it is the Russian border again ({line.Provinces.Count} posts)");
        Check(group.All(a => a.OrderKind == ArmyOrder.DefendFront && a.FrontLineId == line.Id),
            "no division of the group is left without an order by the peace");
        Check(group.All(a => a.AssignedWarId == russiaWar.Id), "the ones that faced Belarus now serve the war still being fought");
        for (var day = 0; day < 20; day++) { NextDay(c); FrontManager.Tick(c); }
        Check(group.All(a => rusBorder.Contains(a.OrderProvinceId)), "within days every division's post faces Russia");

        // --- Moldova joins Russia's war: an enemy's ally is on the front too --------------------
        Check(CampaignDiplomacy.JoinWar(c, russiaWar, mda.Id, defending: false), "Moldova joins Russia's war as a co-belligerent");
        var withMoldova = new HashSet<int>(rusBorder); withMoldova.UnionWith(mdaBorder);
        Check(BattlePlanning.LineFor(c, group[0]) == line && line.Provinces.SetEquals(withMoldova),
            $"the whole front now runs along Moldova as well ({line.Provinces.Count} posts)");
        for (var day = 0; day < 20; day++) { NextDay(c); FrontManager.Tick(c); }
        Check(group.Any(a => mdaBorder.Contains(a.OrderProvinceId)), "and the staff sends somebody to the Moldovan stretch");

        // --- A second army group pressed onto it joins the same front ---------------------------
        var reserve = new Army
        {
            Id = c.Armies.Max(a => a.Id) + 1, NationId = ukr.Id, ProvinceId = group[0].ProvinceId,
            StateMapIndex = map.ProvinceToState[group[0].ProvinceId], Divisions = 1, Strength = 1, Organisation = 1,
            SupplyLevel = 1, ReadyDay = 0
        };
        c.Armies.Add(reserve);
        new DrawFrontLineCommand { IssuerId = ukr.Id, Kind = FrontLineKind.Defence, AllEnemies = true, ArmyIds = new[] { reserve.Id } }.Apply(c);
        Check(c.FrontLines.Count(l => l.NationId == ukr.Id) == 1 && reserve.FrontLineId == line.Id &&
              line.ArmyIds.Count == group.Count + 1, "a second press adds to the one whole front instead of drawing another");

        // --- The per-country border front still works ------------------------------------------
        var start = map.CenterOf(rusBorder.OrderBy(p => p).First());
        new DrawFrontLineCommand
        {
            IssuerId = ukr.Id, Kind = FrontLineKind.Defence, BorderNationId = rus.Id, WholeBorder = true,
            Provinces = new[] { rusBorder.OrderBy(p => p).First() }, ArmyIds = new[] { reserve.Id },
            StartLatitude = start.Latitude, StartLongitude = start.Longitude, EndLatitude = start.Latitude, EndLongitude = start.Longitude
        }.Apply(c);
        var single = BattlePlanning.LineFor(c, reserve);
        Check(single is { AllEnemies: false, BorderNationId: var b } && b == rus.Id && single.Provinces.IsSubsetOf(rusBorder) &&
              single.Provinces.Count > 0, "a border front against Russia alone is still drawn the old way");
        Check(line.ArmyIds.Count == group.Count && c.FrontLines.Contains(line), "taking a division off the whole front leaves the rest on it");

        // --- An offensive drawn from the whole front aims at whoever holds the objective -------
        var departure = rusBorder.OrderBy(p => p).First(p => map.Neighbors[p].Any(n => !map.IsWater(n) && c.ProvinceController[n] == rus.MapIndex));
        var goal = map.Neighbors[departure].First(n => !map.IsWater(n) && c.ProvinceController[n] == rus.MapIndex);
        Check(OffensivePlanning.TargetOf(c, line, goal) == rus.Id, "an objective on Russian ground is an offensive against Russia");
        var attacker = group[0];
        new DrawFrontLineCommand { IssuerId = ukr.Id, Kind = FrontLineKind.Offensive, Provinces = new[] { goal }, ArmyIds = new[] { attacker.Id } }.Apply(c);
        var offensive = BattlePlanning.LineFor(c, attacker);
        Check(offensive is { Kind: FrontLineKind.Offensive } && offensive.BorderNationId == rus.Id &&
              offensive.SourceProvinces.Count > 0 && offensive.SourceProvinces.IsSubsetOf(rusBorder),
            "and it departs from the Russian stretch of the whole front");
    }
}
