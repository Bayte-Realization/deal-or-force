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
        var preserved = new[] { "quiet_current", "frayed_civic", "afterimage_republic", "faultlines_in_motion",
            "last_lap_world", "atlas_header", "night_signal", "quiet_meridians", "atlas_tap", "atlas_confirm",
            "atlas_decline", "atlas_document", "atlas_warning", "atlas_crisis" };
        Check(preserved.All(id => MediaLibraryCatalog.Find(id) is not null),
            "library retains every shipped recording and artwork when new content is added");
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
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"volume_db":-5.5}}}""", "res://mod", "trim", errors);
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"purpose":"Quiet preview"}}}""", "res://mod", "trim", errors);
        Check(MediaLibraryCatalog.Find("fixture").VolumeDb == -5.5f, "caption-only overlays preserve the measured preview trim");
        var trimErrors = new List<string>();
        foreach (var invalid in new[] { "5", "-100", "1e309", "\"NaN\"" })
            MediaLibraryCatalog.Merge("{\"assets\":{\"fixture\":{\"volume_db\":" + invalid + "}}}",
                "res://mod", "trim", trimErrors);
        Check(trimErrors.Count == 4 && MediaLibraryCatalog.Find("fixture").VolumeDb == -5.5f,
            "invalid or amplifying trims cannot corrupt a valid preview");
        var waveErrors = new List<string>();
        foreach (var invalid in new[] { "{\"duration_seconds\":-1}", "{\"duration_seconds\":1e309}", "{\"peaks\":[1.1]}", "{\"peaks\":[-0.1]}", "{\"peaks\":[\"NaN\"]}" })
            MediaLibraryCatalog.Merge("{\"assets\":{\"fixture\":" + invalid + "}}", "res://mod", "badwave", waveErrors);
        Check(waveErrors.Count == 5 && MediaLibraryCatalog.Find("fixture").DurationSeconds == 2.5,
            "non-finite durations and bad amplitudes cannot corrupt a valid recording");
        MediaLibraryCatalog.Merge("{\"assets\":{\"fixture\":{\"peaks\":[" + string.Join(',', Enumerable.Repeat("0.5", 513)) + "]}}}", "res://mod", "hugewave", waveErrors);
        Check(waveErrors.Count == 6 && MediaLibraryCatalog.Find("fixture").Peaks.Count == 4, "oversized waveforms are bounded and rejected");
        MediaLibraryCatalog.Merge("""{"assets":{"fixture":{"file":"music/replacement.ogg"}}}""", "res://mod", "replacement", errors);
        Check(MediaLibraryCatalog.Find("fixture") is { DurationSeconds: 0, Peaks: null, VolumeDb: 0 },
            "replacing the audio file clears stale measurements and source-specific trim");
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
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm, "europe").Any(t => t.Name == "quiet_current") &&
              MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Crisis, "europe").Any(t => t.Name == "faultlines_in_motion"),
            "new universal score is scheduled by its intended context");
        Check(MusicCatalog.For(MusicCatalog.Moment.Menu, MusicMood.Calm).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)
                  .SequenceEqual(new[] { "menu_theme_atlas", "quiet_current" }) &&
              MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Recovery, "europe").Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)
                  .SequenceEqual(new[] { "after_ceasefire", "quiet_current" }),
            "soft peace alternative covers the menus and recovery as well as ordinary calm, beside the menu's own theme and the ceasefire piece");
        Check(!MusicCatalog.All.Any(t => t.Name is "frayed_civic" or "afterimage_republic") &&
              MediaLibraryCatalog.Find("frayed_civic") is { Kind: LibraryMediaKind.Music } &&
              MediaLibraryCatalog.Find("afterimage_republic") is { Kind: LibraryMediaKind.Music },
            "earlier universal recordings leave automatic selection but remain in the listening library");
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm, "asia").Any(t => t.Name == "night_desk") &&
              MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm, "asia").Any(t => t.Name == "window_after_rain"),
            "regional Asian peace companions are preserved");
        static IEnumerable<string> Pool(MusicMood mood, string continent, string subRegion) =>
            MusicCatalog.For(MusicCatalog.Moment.Campaign, mood, continent, subRegion).Select(t => t.Name);
        Check(Pool(MusicMood.Calm, "Asia", "Western Asia").Contains("middle_east_calm") && !Pool(MusicMood.Calm, "Asia", "Western Asia").Contains("night_desk") &&
              Pool(MusicMood.Calm, "Asia", "South-Eastern Asia").Contains("night_desk") && !Pool(MusicMood.Calm, "Asia", "South-Eastern Asia").Contains("middle_east_calm"),
            "the most specific region wins: the Middle East hears its own music, not East Asia's");
        Check(Pool(MusicMood.War, "Asia", "Central Asia").Contains("eurasia_tense") && !Pool(MusicMood.War, "Asia", "Central Asia").Contains("pressure_lines") &&
              Pool(MusicMood.Crisis, "Africa", "Northern Africa").Contains("middle_east_tense") && Pool(MusicMood.Crisis, "Africa", "Western Africa").Contains("africa_tense"),
            "sub-regions pick their own palette for tension as well as peace");
        Check(Pool(MusicMood.Calm, "North America", "Northern America").Contains("western_wind") &&
              Pool(MusicMood.Calm, "North America", "Caribbean").Contains("south_america_calm"),
            "a continent written with a space matches a tag written with an underscore");
        Check(MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Crisis, "Asia", "Southern Asia", "IRN").Any(t => t.Name == "middle_east_tense") &&
              MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Crisis, "Asia", "Southern Asia", "IRN").All(t => t.Name != "unsent_telegram"),
            "a country's own tag is the most specific region: Iran hears the Middle East");
        Check(Pool(MusicMood.War, "Europe", "Western Europe").Contains("world_war") && Pool(MusicMood.War, "Oceania", "Polynesia").Contains("world_war"),
            "the world-war underscore plays in every region");
        NewsFeed.ResetCatalog();
        var gameRoot = System.IO.Path.GetDirectoryName(GameDataLoader.DataFolder("sound"));
        bool OnDisk(string cue) => SoundCatalog.Get(cue).File is { Length: > 0 } file && file.StartsWith("res://game/") &&
            System.IO.File.Exists(System.IO.Path.Combine(gameRoot, file["res://game/".Length..]));
        Check(NewsFeed.Stings.Count >= 40 && NewsFeed.Stings.Values.Where(cue => cue.Length > 0).All(OnDisk) && NewsFeed.StingGapMs >= 1000,
            "every headline sting names a declared cue whose recording ships with the game");
        Check(NewsFeed.Sting("coup") == "news_coup" && NewsFeed.Sting("world_war_began") == "news_world_war" && NewsFeed.Sting("aid_sent") == "",
            "a coup and a world war have their own stings; routine news makes no sound");
        Check(new[] { "diplomacy_page", "deal_sent", "deal_concluded", "covert_launched", "map_focus" }.All(OnDisk),
            "the interface sounds the code plays are declared and present, the page turn included");
        Check(SoundCatalog.AmbienceBeds.Count >= 8 && SoundCatalog.AmbienceBeds.Values.All(f => Ships(f)) &&
              SoundCatalog.AmbienceBeds.ContainsKey(SoundCatalog.OpenCountryBed) && SoundCatalog.AmbienceBeds.ContainsKey(SoundCatalog.SeaBed) &&
              SoundCatalog.TerrainAmbience.Values.All(mix => mix.All(part => SoundCatalog.AmbienceBeds.ContainsKey(part.Bed))),
            "every map ambience bed ships with the game and every terrain mix names a declared bed");
        var ambienceErrors = new List<string>();
        SoundCatalog.Merge("""{"map_ambience":{"beds":{"volcano":"audio/volcano.ogg"},"terrain":{"volcanic":{"volcano":1},"marsh":null}}}""",
            "res://mods/fire", "volcano", ambienceErrors);
        Check(ambienceErrors.Count == 0 && SoundCatalog.AmbienceBeds["volcano"] == "res://mods/fire/audio/volcano.ogg" &&
              SoundCatalog.TerrainAmbience["volcanic"].Single().Bed == "volcano" && !SoundCatalog.TerrainAmbience.ContainsKey("marsh"),
            "a mod gives its own terrain a sound and drops a mix with null");
        SoundCatalog.Load();
        EventPictures.ResetCatalog();
        var events = EventCatalog.All.ToArray();
        bool Ships(string path) => path.StartsWith("res://game/") && System.IO.File.Exists(System.IO.Path.Combine(gameRoot, path["res://game/".Length..]));
        var bare = events.Where(e => !Ships(EventPictures.PathFor(e))).Select(e => e.Id).ToArray();
        Check(bare.Length == 0, "every base-game event has a picture that ships with the game" + (bare.Length > 0 ? ": missing " + string.Join(", ", bare) : ""));
        Check(EventPictures.Table.Keys.All(id => events.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))),
            "the picture table names no event that does not exist");
        Check(EventPictures.Resolve("finance") == "res://game/gfx/events/event_finance.jpg" &&
              EventPictures.Resolve("leaders/x.png") == "res://game/gfx/leaders/x.png" && EventPictures.Resolve("res://mod/a.png") == "res://mod/a.png",
            "a picture is a shared name, a path under gfx/ or a mod's own path");
    }
}
