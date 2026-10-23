using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MusicSituationTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("music: " + message); Console.WriteLine("PASS music: " + message); }
    public static void Run()
    {
        MusicCatalog.Load();
        var c = new Campaign(908);
        var n = new Nation { Id = 1, MapIndex = 701, Tag = "MUS", Name = "Local", Population = 1000000, Stability = .9f, Continent = "Europe" };
        var rival = new Nation { Id = 2, MapIndex = 702, Tag = "MU2", Name = "Abroad", Population = 1000000, Stability = .9f, Continent = "Asia" };
        c.Nations.Add(n); c.Nations.Add(rival);
        var random = c.Random.State;
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Calm, "peaceful stable country begins in calm context");
        c.SetFeature("world_order_emergency", true);
        Check(MusicSituation.Read(c, n).Mood == MusicMood.SystemicCrisis, "explicit world-order signal outranks ordinary national context");
        c.SetFeature("world_order_emergency", false);
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Calm, "world-order signal can clear without altering a war");
        Check(c.Random.State == random && c.Wars.Count == 0, "reading musical context consumes no simulation random value");
        var global = new Campaign(909);
        for (var id = 1; id <= 12; id++) global.Nations.Add(new Nation { Id = id, MapIndex = 800 + id,
            Tag = "SYS" + id, Population = 1000000, Gdp = 10, Stability = .9f });
        for (var id = 1; id <= 9; id += 2) global.Wars.Add(new StrategicWar { Id = id,
            AttackerId = id, DefenderId = id + 1 });
        Check(MusicSituation.Read(global, global.Nations[11]).Mood == MusicMood.SystemicCrisis,
            "global war across ten countries and most population and GDP triggers existential score without a flag");
        global.Wars.RemoveAt(4);
        Check(MusicSituation.Read(global, global.Nations[11]).Mood == MusicMood.Crisis,
            "population exposure alone is not enough for a systemic-war score");
        n.Stability = .2f;
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Uneasy, "low stability is uneasy, not automatically a war");
        n.Stability = .9f; n.CoupPlotDay = 0;
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Crisis, "active plot changes the musical context");
        c.SetFeature("coups", false);
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Calm, "disabled plot mechanic cannot trigger crisis music");
        n.CoupPlotDay = -1;
        n.DebtCondition = DebtCondition.Default;
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Crisis, "sovereign default has a distinct pressure route");
        c.SetFeature("public_debt", false);
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Calm, "disabled public debt does not affect the score");
        n.DebtCondition = (DebtCondition)0;
        var region = new ScenarioState { MapIndex = 91, NationMapIndex = n.MapIndex, Radicalism = .9f };
        Check(MusicSituation.Read(c, n, region).Mood == MusicMood.Uneasy, "selected governed region can color the background");
        region.NationMapIndex = rival.MapIndex;
        Check(MusicSituation.Read(c, n, region).Mood == MusicMood.Calm, "foreign region is not represented as national radicalism");
        Check(MusicSituation.Read(c, n, region, observer: true).Region == "asia", "observer's selected region chooses the regional palette");
        rival.Region = "Western Asia";
        Check(MusicSituation.Read(c, n, region, observer: true).SubRegion == "western_asia", "the palette carries the sub-region as a tag");
        c.Wars.Add(new StrategicWar { Id = 1, AttackerId = n.Id, DefenderId = rival.Id });
        Check(MusicSituation.Read(c, n).Mood == MusicMood.War, "own active war outranks other pressure");
        Check(MusicSituation.Read(c, n, observer: true).Mood == MusicMood.Crisis && MusicSituation.Read(c, n, observer: true).WorldWarExposure == 1,
            "observer reads world exposure rather than pretending to command a belligerent");
        c.Wars[0].Ended = true; c.Wars[0].EndedDay = c.ElapsedDays;
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Recovery, "peace is followed by recovery, not immediate triumph");
        DomesticCrisesTests.Day(c, MusicCatalog.Director.RecoveryDays + 1);
        Check(MusicSituation.Read(c, n).Mood == MusicMood.Calm, "recovery context eventually clears");

        var policy = new MusicPolicy(); var latch = new MusicMoodLatch();
        Check(latch.Observe(MusicMood.Calm, 0, policy), "initial mood is primed once");
        Check(!latch.Observe(MusicMood.War, 1, policy), "one frame of pressure cannot switch a track");
        Check(!latch.Observe(MusicMood.War, 8, policy), "war context still respects a short minimum hold");
        Check(latch.Observe(MusicMood.War, 12, policy), "persistent war enters after escalation delay and hold");
        Check(!latch.Observe(MusicMood.Calm, 13, policy), "one calm frame cannot end wartime music");
        Check(!latch.Observe(MusicMood.War, 20, policy), "returning pressure cancels the release candidate");
        Check(!latch.Observe(MusicMood.Calm, 21, policy) && !latch.Observe(MusicMood.Calm, 55, policy), "release waits for stable calm and minimum hold");
        Check(latch.Observe(MusicMood.Calm, 60, policy), "sustained calm releases pressure");

        var errors = new List<string>();
        MusicCatalog.Merge("""{"tracks":{"fixture_universal":"audio/a.ogg","fixture_asia":{"file":"audio/b.ogg","when":"campaign","moods":["calm"],"regions":["asia"],"volume_db":-4},"fixture_war":{"file":"audio/c.ogg","when":"campaign","moods":["war"]}}}""", "res://music", "fixture", errors);
        Check(errors.Count == 0, "new and legacy track declarations both load");
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm, "europe").All(t => t.Name != "fixture_asia" && t.Name != "fixture_war"),
            "mood and region are separate playlist filters");
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm, "asia").Any(t => t.Name == "fixture_asia"), "regional track is available in its own region");
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.War, "europe").Any(t => t.Name == "fixture_universal"), "old untagged mods remain compatible");
        MusicCatalog.Merge("""{"tracks":{"fixture_asia":"audio/replaced.ogg"}}""", "res://mod/music", "replace", errors);
        var replaced = MusicCatalog.All.Single(t => t.Name == "fixture_asia");
        Check(replaced.When == MusicCatalog.Moment.Campaign && replaced.Moods.Contains(MusicMood.Calm) && replaced.Regions.Contains("asia") && replaced.VolumeDb == -4,
            "one-line replacement preserves scheduling, region and gain");
        MusicCatalog.Merge("""{"tracks":{"fixture_war":{"remove":true},"bad":{"file":"x","moods":["not_a_mood"]}},"director":{"fade_seconds":500,"escalate_seconds":"bad"}}""", "res://music", "patch", errors);
        Check(MusicCatalog.All.All(t => t.Name != "fixture_war" && t.Name != "bad"), "removal and malformed tags do not create inappropriate tracks");
        Check(MusicCatalog.Director.FadeSeconds == 30 && errors.Count == 2, "director limits and invalid settings are reported");
        MusicCatalog.Load();
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.SystemicCrisis).Single().Loop,
            "existential score loops instead of dropping into a calm random track");
    }
}
