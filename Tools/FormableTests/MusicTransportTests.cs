using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using DealOrForce.Simulation;

static class MusicTransportTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("music transport: " + message); Console.WriteLine("PASS music transport: " + message); }

    public static void Run()
    {
        var c = new Campaign(91831); var fingerprint = CampaignFingerprint.Of(c); var rng = c.Random.State;
        var p = new MusicTransport(); var tracks = new[] { "first", "middle", "last" };
        Check(!p.Manual && !p.Paused && !p.TakeSelection(out _), "starts with ordinary adaptive playback");
        Check(!p.Select("missing", tracks) && !p.Manual, "unknown ID cannot seize the transport");
        Check(!p.Select(null, tracks) && !p.Select("", tracks) && !p.Select("first", null), "invalid selection is a no-op");
        Check(p.Select("FIRST", tracks) && p.Selection == "first" && p.Manual, "selection keeps canonical identity, not translated title");
        Check(p.TakeSelection(out var selected) && selected == "first", "audio deck takes a pending request once");
        Check(!p.TakeSelection(out _), "reading a request does not restart the recording every frame");
        p.Select("middle", tracks); p.Select("last", tracks);
        Check(p.TakeSelection(out selected) && selected == "last", "latest rapid choice wins, not a stale crossfade callback");
        p.Select("middle", tracks); p.SetPaused(true);
        Check(!p.TakeSelection(out _) && p.Paused && p.Manual, "pause retains a pending choice without consuming it");
        p.SetPaused(false);
        Check(p.TakeSelection(out selected) && selected == "middle", "resume retains selection");
        Check(MusicTransport.Neighbour(tracks, "last", 1) == "first", "next wraps in ordered playlist");
        Check(MusicTransport.Neighbour(tracks, "first", -1) == "last", "previous wraps in ordered playlist");
        Check(MusicTransport.Neighbour(tracks, "MIDDLE", -1) == "first", "neighbour identity is case insensitive");
        Check(MusicTransport.Neighbour(tracks, "removed", 1) == "first" && MusicTransport.Neighbour(tracks, "removed", -1) == "last",
            "changed catalogue has a defined browsing fallback");
        Check(MusicTransport.Neighbour(Array.Empty<string>(), "first", 1) == "" && MusicTransport.Neighbour(null, "first", 1) == "",
            "empty catalogue cannot divide by zero");
        Check(MusicTransport.Neighbour(tracks, "first", 0) == "", "zero direction does not secretly skip a track");
        Check(MusicTransport.Neighbour(new[] { "only" }, "only", -1) == "only", "single recording can replay");
        p.SetPaused(true); p.Select("first", tracks);
        Check(!p.Paused, "explicit play resumes music, not campaign time");
        p.SetPaused(true); p.Automatic();
        Check(!p.Manual && !p.Paused && p.Selection == "" && !p.TakeSelection(out _), "returning to automatic discards pending manual intent");
        Check(MusicTransport.Presence(false, false) == "Automatic soundtrack follows the campaign.", "the rail says the director is choosing");
        Check(MusicTransport.Presence(true, false) == "Manual playlist. Keeps playing when this window closes.", "a chosen track is still the line after the gallery shuts");
        Check(MusicTransport.Presence(false, true) == "Soundtrack paused. The campaign clock is unchanged."
            && MusicTransport.Presence(true, true) == "Soundtrack paused. The campaign clock is unchanged.", "pause does not move the campaign clock");
        Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == rng && c.IsPaused,
            "all transport operations are independent of campaign state and random sequence");
        MusicCatalog.Load();
        var count = MusicCatalog.Count;
        var root = Path.Combine(Directory.GetParent(GameDataLoader.DataFolder("music")).Parent.FullName,
            "mods", "_templates", "soundtrack-data");
        var errors = new List<string>();
        try
        {
            MusicCatalog.LoadTree(Path.Combine(root, "music"), "res://mods/example_soundtrack/music", errors);
            var declared = MusicCatalog.All.FirstOrDefault(t => t.Name == "example_modder_track");
            Check(errors.Count == 0 && MusicCatalog.Count == count + 1, "delivered YAML template adds a real track through the package reader");
            Check(declared.File == "res://game/music/audio/quiet_current.ogg" && declared.VolumeDb == -3 && !declared.Loop,
                "template preserves explicit base asset, gain and playback policy");
            Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm).Any(t => t.Name == declared.Name),
                "mod track can participate in automatic campaign selection too");
        }
        finally { MusicCatalog.Load(); }
    }
}
