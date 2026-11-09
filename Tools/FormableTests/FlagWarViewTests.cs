using System;
using System.Linq;
using DealOrForce.Simulation;

internal static class FlagWarViewTests
{
    private static void Check(bool ok, string note)
    { if (!ok) throw new Exception("flag war view: " + note); Console.WriteLine("PASS flag war view: " + note); }

    private static Campaign World(int seed)
    {
        var c = new Campaign(seed) { ScenarioId = "flag_view_fixture" };
        for (var id = 0; id < 4; id++)
        {
            c.Nations.Add(new Nation { Id = id, MapIndex = id + 10, Tag = "FV" + id,
                Name = "Flag nation " + id, Population = 1000, Gdp = 1, Stability = .7f });
            c.States.Add(new ScenarioState { Id = "flag_state_" + id, MapIndex = id + 1,
                NationId = "FV" + id, NationMapIndex = id + 10, Population = 1000 });
        }
        return c;
    }

    private static string Brute(Campaign c, Nation n) =>
        c.Wars.Any(w => !w.Ended && w.Involves(n.Id)) ? "at_war" : "at_peace";

    private static void Matches(Campaign c, string note)
    {
        Check(c.Nations.All(n => FlagCatalog.ValueOf("war", c, n) == Brute(c, n)), note);
        Check(FlagCatalog.VisualStateHash(c) == ReferenceHash(c), "visual invalidation stays identical to the reference: " + note);
    }

    private static int ReferenceHash(Campaign c)
    {
        var hash = new HashCode();
        foreach (var n in c.Nations)
        {
            hash.Add(n.Id); hash.Add(n.Tag); hash.Add(n.CustomFlag); hash.Add(n.Color);
            hash.Add(n.CustomFlagDesignJson);
            hash.Add(n.FoundNameKind); hash.Add(n.FoundNameId); hash.Add(n.FoundedInRevolt);
            hash.Add(n.SecededFromId); hash.Add(n.ReleasedFromId);
            foreach (var q in FlagCatalog.All) hash.Add(q.Source == "war" ? Brute(c, n) : FlagCatalog.ValueOf(q.Source, c, n));
        }
        return hash.ToHashCode();
    }

    internal static void Run()
    {
        FlagCatalog.ResetCatalog();
        Check(FlagCatalog.All.Any(q => q.Source == "war"), "real catalog declares a war qualifier");
        var c = World(61109);
        for (var i = 0; i < 2500; i++) c.Wars.Add(new StrategicWar { Id = i, AttackerId = 0, DefenderId = 1, Ended = true });
        var active = new StrategicWar { Id = 2500, AttackerId = 0, DefenderId = 1 };
        c.Wars.Add(active);
        var fingerprint = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        var scans = FlagCatalog.WarFlagHistoryPasses;
        Matches(c, "cold view agrees with full history");
        Check(FlagCatalog.WarFlagHistoryPasses == scans + 1, "one cold scan, not one scan per country");
        for (var i = 0; i < 1000; i++) FlagCatalog.VisualStateHash(c);
        Check(FlagCatalog.WarFlagHistoryPasses == scans + 1, "1000 quiet polls do not revisit 2500 archived wars");
        Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random,
            "warm and cold visual reads change neither authority nor RNG");

        active.AttackerAllies.Add(2);
        Matches(c, "joining an active coalition is visible without replacing its record");
        active.AttackerAllies.Remove(2); active.DefenderAllies.Add(3);
        Matches(c, "leaving and joining the other side are read from current participant sets");
        active.AttackerId = 2;
        Matches(c, "current war leaders are not cached as old nation IDs");

        // Prime the military cache while open, then end the war. Reading flags must not
        // trigger that cache's ended-front cleanup or otherwise advance the simulation.
        Warfare.LiveWars(c);
        active.AttackerFront.Add(99); active.DefenderFront.Add(98);
        active.FrontDirty = true; active.LastFrontDay = 17;
        active.Ended = true;
        fingerprint = CampaignFingerprint.Of(c);
        Matches(c, "a directly ended live record disappears immediately");
        Check(active.AttackerFront.SequenceEqual(new[] { 99 }) && active.DefenderFront.SequenceEqual(new[] { 98 }) &&
            active.FrontDirty && active.LastFrontDay == 17 && CampaignFingerprint.Of(c) == fingerprint,
            "visual reads never invoke military cleanup of an ended war");

        var second = World(61110);
        var other = new StrategicWar { Id = 1, AttackerId = 2, DefenderId = 3 };
        second.Wars.Add(other);
        var secondFingerprint = CampaignFingerprint.Of(second);
        Matches(second, "another world has its own belligerents");
        Matches(c, "reading the other world leaves the first at peace");
        Check(CampaignFingerprint.Of(second) == secondFingerprint, "the neighbour world's derived view is read-only too");

        c.Wars.Add(new StrategicWar { Id = 2501, AttackerId = 1, DefenderId = 3 });
        Matches(c, "an appended war is observed even before an explicit revision");
        c.Wars.RemoveAt(c.Wars.Count - 1);
        Matches(c, "removing a live record releases it from the view");
        active.Ended = false; c.MarkWarsChanged();
        Matches(c, "explicit restoration/revision rebuilds a previously closed record");
        c.Wars[^1] = new StrategicWar { Id = 5000, AttackerId = 1, DefenderId = 3 };
        c.MarkWarsChanged();
        Matches(c, "same-count replacement follows the campaign's revision contract");

        // The real snapshot exporter requires a player and a prepared tactical map.
        // Supply them here rather than weakening that production precondition.
        second.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1, 2, 3, 4 },
            new ushort[] { 10, 11, 12, 13 }, 4, 1, _ => false));
        second.ConfigureSinglePlayer(0);
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(second, "flag-view"), "flag-view");
        Check(loaded.Success, "a snapshot contains war authority, not the derived view: " + loaded.Warning);
        var restored = loaded.Campaign;
        fingerprint = CampaignFingerprint.Of(restored);
        Matches(restored, "cold snapshot derives the same flags from its own war records");
        Check(CampaignFingerprint.Of(restored) == fingerprint, "cold snapshot visual lookup leaves its fingerprint alone");
        Check(FlagCatalog.ValueOf("war", null, second.Nations[0]) == "at_peace" &&
            FlagCatalog.ValueOf("war", second, null) == "", "null world/nation retain their existing fallback semantics");
    }
}
