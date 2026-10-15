using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class MediaLibraryTests
{
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception("media: " + message); Console.WriteLine("PASS media: " + message); }
    public static void Run()
    {
        MediaLibraryCatalog.Load();
        Check(MediaLibraryCatalog.All.Count() == 12, "base library lists every delivered asset and the preserved world loop");
        Check(MediaLibraryCatalog.All.All(x => x.Kind != LibraryMediaKind.Video), "unapproved video render is not claimed or loaded as delivered media");
        Check(MediaLibraryCatalog.All.Where(x => x.Kind is LibraryMediaKind.Music or LibraryMediaKind.Sound)
            .All(x => x.DurationSeconds > 0 && x.Peaks is { Count: > 0 } && x.Peaks.Count <= 512 && x.Peaks.Any(p => p > 0 && p < 1)),
            "each delivered recording has a measured duration and non-binary waveform");
        Check(MediaLibraryCatalog.Find("atlas_paper") is null && MediaLibraryCatalog.Find("committee_at_dawn") is null,
            "failed paid requests create no phantom playable assets");
        var c = new Campaign(414); var fingerprint = CampaignFingerprint.Of(c);
        _ = MediaLibraryCatalog.All.Select(x => x.Purpose).ToArray();
        Check(CampaignFingerprint.Of(c) == fingerprint, "reading presentation inventory never changes campaign state");
        var errors = new List<string>();
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"kind":"music","file":"music/a.ogg","name":"First","purpose":"Quiet"}}}""", "res://mod", "fixture", errors);
        Check(errors.Count == 0 && MediaLibraryCatalog.Find("fixture").File == "res://mod/music/a.ogg", "mod paths resolve inside their own package");
        var revision = MediaLibraryCatalog.Revision;
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"name":"Second"}}}""", "res://later", "overlay", errors);
        Check(MediaLibraryCatalog.Find("fixture") is { Name: "Second", File: "res://mod/music/a.ogg", Purpose: "Quiet" },
            "metadata-only overlay retains original scheduling-independent source path");
        Check(MediaLibraryCatalog.Revision > revision, "catalogue revision invalidates a visible library");
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"duration_seconds":2.5,"peaks":[0,0.3,0.8,0.1]}}}""", "res://mod", "wave", errors);
        Check(MediaLibraryCatalog.Find("fixture") is { DurationSeconds: 2.5, Peaks.Count: 4 }, "valid waveform overlays existing audio metadata");
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"purpose":"Renamed"}}}""", "res://other", "caption", errors);
        Check(MediaLibraryCatalog.Find("fixture").Peaks[2] == .8f, "caption-only changes preserve real measurements");
        var waveErrors = new List<string>();
        foreach (var invalid in new[] { "{\"duration_seconds\":-1}", "{\"duration_seconds\":1e309}", "{\"peaks\":[1.1]}", "{\"peaks\":[-0.1]}", "{\"peaks\":[\"NaN\"]}" })
            MediaLibraryCatalog.Merge("{\"assets\":{\"fixture\":" + invalid + "}}", "res://mod", "badwave", waveErrors);
        Check(waveErrors.Count == 5 && MediaLibraryCatalog.Find("fixture").DurationSeconds == 2.5,
            "non-finite durations and bad amplitudes cannot corrupt a valid recording");
        MediaLibraryCatalog.Merge("{\"assets\":{\"fixture\":{\"peaks\":[" + string.Join(',', Enumerable.Repeat("0.5", 513)) + "]}}}", "res://mod", "hugewave", waveErrors);
        Check(waveErrors.Count == 6 && MediaLibraryCatalog.Find("fixture").Peaks.Count == 4, "oversized waveforms are bounded and rejected");
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"file":"music/replacement.ogg"}}}""", "res://mod", "replacement", errors);
        Check(MediaLibraryCatalog.Find("fixture") is { DurationSeconds: 0, Peaks: null }, "replacing the audio file clears stale measurements");
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"kind":"image"},"bad_kind":{"kind":"99","file":"x.ogg"},"remote":{"kind":"music","file":"https://x/a.mp3"},"escape":{"kind":"image","file":"../secret.png"},"drive":{"kind":"sound","file":"C:/secret.wav"}}}""", "res://mod", "bad", errors);
        Check(errors.Count == 5 && MediaLibraryCatalog.Find("fixture").Kind == LibraryMediaKind.Music,
            "bad media kinds, mismatched formats, remote paths and traversal cannot replace a valid item");
        var escapeErrors = new List<string>();
        foreach (var path in new[] { " ../secret.png", "\t../secret.png", " /absolute.png", " C:/secret.png", " https://example.com/x.png" })
            MediaLibraryCatalog.Merge(System.Text.Json.JsonSerializer.Serialize(new { assets = new { escaped = new { kind = "image", file = path } } }),
                "res://mods/example", "whitespace-path", escapeErrors);
        Check(escapeErrors.Count == 5 && MediaLibraryCatalog.Find("escaped") is null,
            "whitespace prefixes cannot bypass the package path boundary");
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"remove":true}}}""", "res://mod", "remove", errors);
        Check(MediaLibraryCatalog.Find("fixture") is null, "mod can remove one inventory entry");
        MediaLibraryCatalog.Load(); SoundCatalog.Load(); MusicCatalog.Load();
        Check(SoundCatalog.Get("ui_select").File.EndsWith("atlas_tap.wav") && SoundCatalog.Get("action_denied").File.EndsWith("atlas_decline.wav"),
            "ordinary and unavailable actions have different declared cues");
        Check(SoundCatalog.Get("situation_alert").CooldownMs >= 12000 && SoundCatalog.Get("situation_alert").Yielding && SoundCatalog.Get("situation_alert").VolumeDb < 0,
            "critical cue retains quietness, throttling and priority discipline");
        Check(SoundCatalog.Get("deal_received").Yielding && SoundCatalog.Get("deal_received").CooldownMs >= 4000,
            "incoming documents cannot create an unbounded alert queue");
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.SystemicCrisis).Single().Name == "last_lap_world",
            "new companions do not dilute the existential playlist");
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm, "europe").Any(t => t.Name == "afterimage_republic") &&
              MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Crisis, "europe").Any(t => t.Name == "faultlines_in_motion"),
            "new universal score is scheduled by its intended context");
    }
}
