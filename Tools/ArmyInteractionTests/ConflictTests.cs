using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The conflicts of 2024 between and inside states: the de facto powers the bookmark opens
/// with, civil wars kept to their two sides, a faction's patrons and proscribers, a faction
/// taking the state, coups, and the intelligence a government weighs a war with.
/// </summary>
static class ConflictTests
{
    static int failures;
    static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
        if (!ok) failures++;
    }

    public static void Run(ProvinceTopology shared)
    {
        // A map of its own: the shared one has already had the first campaign's factions cut
        // out of it, which is the borders a second campaign would open on without the restore
        // the game does before every new one.
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), "a fresh province map: " + message);
        var c = Campaign.CreateBootstrap(20261007);
        c.AttachProvinceTopology(map);

        // Every de facto power of the bookmark stands on ground of its own.
        var missing = Factions.All.Where(definition =>
            c.NationByTag(definition.Tag) is not { } faction || c.StatesOf(faction).Length == 0).Select(d => d.Tag).ToArray();
        Check(Factions.All.Count >= 10 && missing.Length == 0,
            $"every faction of 2024 is founded with land ({Factions.All.Count} defined, missing: {string.Join(",", missing)})");

        var sdn = c.NationByTag("SDN");
        var rsf = c.NationByTag("RSF");
        Check(sdn is not null && rsf is not null && rsf.SecededFromId == sdn.Id && rsf.FactionId == "rapid_support_forces",
            "the Rapid Support Forces are a faction of Sudan");
        Check(sdn?.CapitalName == "Port Sudan", "Sudan's government sits in Port Sudan");
        var war = c.Wars.FirstOrDefault(w => !w.Ended && w.Involves(sdn.Id) && w.Involves(rsf.Id));
        Check(war is { CivilConflict: true }, "the Sudanese civil war is on from the first day");
        Check(c.Armies.Count(a => a.NationId == rsf.Id) >= 4, "the RSF has fighters of its own");

        // A civil war is fought by its two sides.
        var usa = c.NationByTag("USA");
        Check(war is not null && !CampaignDiplomacy.JoinWar(c, war, usa.Id, defending: true),
            "a great power cannot join somebody else's civil war");
        Check(war is not null && ProxyWars.Interest(c, usa, rsf, sdn, war) <= -1f,
            "a state that sanctions a faction will not arm it");

        // A patron deters an attacker; a history-only arrangement cannot be imposed.
        var tur = c.NationByTag("TUR");
        var sdf = c.NationByTag("SDF");
        Check(AiStatecraft.Deterrence(c, tur, sdf) > CampaignIntel.EstimatedStrength(c, tur.Id, sdf) * 1.5f,
            "the American patron counts against an attack on north-eastern Syria");
        Check(!SubjectCatalog.Imposable.Any(kind => kind.Id is "dependent_territory" or "leased_territory" or "associated_state"),
            "no peace imposes a lease, a dependent territory or a free association");

        // Intelligence is the real order of battle, within the stated error.
        var kor = c.NationByTag("KOR");
        var prk = c.NationByTag("PRK");
        var real = AiStatecraft.CachedStrength(c, kor);
        var seen = CampaignIntel.EstimatedStrength(c, prk.Id, kor);
        Check(real > 0f && MathF.Abs(seen - real) <= real * c.Defines.IntelEstimateError + 0.01f,
            $"Pyongyang sees South Korea's army within a quarter of what it is ({seen:0} against {real:0})");

        // The fall of a capital: the faction takes the state.
        var capital = sdn.CapitalProvinceId;
        var sudan = Reintegration.Everything(c, sdn);
        foreach (var province in sudan.Take(sudan.Count * 3 / 4).Append(capital))
            c.SetProvinceController(province, (ushort)rsf.MapIndex);
        Check(Factions.HoldsTheState(c, rsf, sdn), "with the capital and three quarters of the ground the RSF holds the state");
        Factions.TakeOver(c, rsf, sdn, war);
        Check(war.Ended && war.WinnerId == rsf.Id, "the civil war ends with the government's fall");
        Check(c.StatesOf(sdn).Length == 0 && sudan.All(p => map.ProvinceToNation[p] == rsf.MapIndex),
            "every province the government had is the new government's");
        Check(rsf.Name == "Sudan" && rsf.FactionId.Length == 0 && rsf.SecededFromId < 0,
            "the faction becomes the state, under its name");

        // Coups: a plot below the line, never above it, never in a dependency, not straight after a coup.
        var mli = c.NationByTag("MLI");
        mli.Stability = 0.10f;
        mli.LastCoupDay = -99999;
        Check(Coups.Chance(c, mli) > 0f, "an unstable government can be plotted against");
        mli.Stability = 0.60f;
        Check(Coups.Chance(c, mli) == 0f, "a settled one cannot");
        mli.Stability = 0.10f;
        var before = mli.Government;
        mli.Government = "presidential_republic";
        Coups.Seize(c, mli);
        Check(mli.Government == "military_junta" && mli.LastCoupDay == c.ElapsedDays,
            "a coup makes a military junta");
        mli.Stability = 0.10f;
        Check(Coups.Chance(c, mli) == 0f, "a junta that has just taken power is left alone for a while");
        mli.Government = before;
        var pitcairn = c.Nations.FirstOrDefault(n => n.IsSubject && n.Population > 0);
        if (pitcairn is not null)
        {
            pitcairn.Stability = 0.05f;
            pitcairn.LastCoupDay = -99999;
            var plotted = pitcairn.CoupPlotDay;
            Coups.Tick(c);
            Check(pitcairn.CoupPlotDay == plotted, "a dependency has no officers to plot with");
        }

        // War names follow the language the game is played in.
        var border = new StrategicWar { Id = 9001, AttackerId = tur.Id, DefenderId = c.NationByTag("GEO").Id,
            Name = $"{tur.Name}–{c.NationByTag("GEO").Name} Border War", Limited = true };
        Check(WarNames.Of(c, border).Contains(tur.DisplayName), "a border war is named from the two countries as they are shown");

        if (failures > 0) throw new Exception($"{failures} conflict regressions failed");
    }
}
