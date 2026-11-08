using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifySoundtrackPlayer(DealOrForceGame game)
    {
        var world = game.VerificationWorld; world.IsPaused = true;
        game.VerificationSelectPlayerCountry();
        var before = CampaignFingerprint.Of(world); var rng = world.Random.State;
        var music = MusicDirector.Ensure(game);
        var recording = MediaLibraryCatalog.Find("quiet_current");
        var track = MusicCatalog.All.First(t => t.File == recording.File);
        var fade = MusicCatalog.Director.FadeSeconds;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            game.VerificationShowAudioPreview("quiet_current"); await Wait(.25);
            Check(game.VerificationReadinessPointerTooltip().Length == 0 && game.VerificationReadinessPopupText().Length == 0,
                "opening the library dismisses native map tooltip and blocks new map hover");
            var play = game.FindChild("PlaySelectedSoundtrack", true, false) as Button;
            Check(play is { Disabled: false, Visible: true }, "declared music has a real soundtrack action");
            play.EmitSignal(BaseButton.SignalName.Pressed); await Wait(fade + .3);
            Check(music.Manual && !music.Paused && music.CurrentTrackId == track.Name, "library transfers the chosen recording to persistent decks");
            Check(music.GetChildren().OfType<AudioStreamPlayer>().Any(d => d.Playing && d.Stream is not null), "native soundtrack really has a playing voice");
            var card = game.FindChild("SoundtrackPlayer", true, false) as Control;
            var playlist = game.FindChild("SoundtrackTrackSelector", true, false) as OptionButton;
            Check(playlist.ItemCount == MusicCatalog.Count && Enumerable.Range(0, playlist.ItemCount).Any(i =>
                playlist.GetItemMetadata(i).AsString() == track.Name), "all declared soundtrack tracks are directly selectable");
            CheckFits(game.FindChild("MediaLibraryPanel", true, false) as Control, "soundtrack library " + language + " " + size.X);
            foreach (var name in new[] { "SoundtrackPrevious", "SoundtrackPause", "SoundtrackNext", "SoundtrackAutomatic", "PlaySelectedSoundtrack" })
            {
                var button = game.FindChild(name, true, false) as Button;
                Check(button is not null && button.IsVisibleInTree() && button.Size.Y >= button.GetMinimumSize().Y - 1,
                    "reachable transport button " + name);
                Check(GetTree().Root.GetVisibleRect().Encloses(button.GetGlobalRect()), "transport button stays in viewport " + name);
            }
            Check(card.GetGlobalRect().Encloses((game.FindChild("SoundtrackStatus", true, false) as Control).GetGlobalRect()), "transport status remains inside its own surface");
            await Capture("soundtrack-manual-" + language + "-" + size.X);
            var position = music.PlaybackPosition;
            game.VerificationCloseMusicLibrary(); await Wait(.3);
            Check(music.Manual && music.CurrentTrackId == track.Name && music.PlaybackPosition >= position,
                "closing library neither stops nor restarts the soundtrack");
            game.VerificationShowAudioPreview("quiet_current");
            (game.FindChild("SoundtrackPause", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.1); position = music.PlaybackPosition;
            game.VerificationCloseMusicLibrary(); await Wait(.3);
            Check(music.Paused && Math.Abs(music.PlaybackPosition - position) < .1 && world.IsPaused,
                "music pause survives closing without changing campaign pause");
            music.SetPaused(false);
        }
        game.VerificationShowAudioPreview("quiet_current");
        var previewPosition = music.PlaybackPosition;
        (game.FindChild("PlayMediaPreview", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.3);
        Check(game.VerificationAudioPreviewPlaying("quiet_current") && music.Manual && music.CurrentTrackId == track.Name &&
            music.GetChildren().OfType<AudioStreamPlayer>().All(d => d.VolumeDb <= -79),
            "audition temporarily suppresses manual soundtrack without taking its selection");
        game.VerificationCloseMusicLibrary(); await Wait(.3);
        Check(music.Manual && music.CurrentTrackId == track.Name && music.PlaybackPosition >= previewPosition,
            "ending audition restores persistent music without restarting it");
        var initial = music.CurrentTrackId;
        Check(!music.PlayTrack("there_is_no_such_track") && music.CurrentTrackId == initial, "unknown track keeps working soundtrack");
        MusicCatalog.Merge("""{"tracks":{"probe_missing":{"file":"missing_transport_fixture.ogg"}}}""", "res://game/music", "transport fixture", null);
        Check(!music.PlayTrack("probe_missing") && music.CurrentTrackId == initial && music.Manual,
            "missing mod recording is refused without cutting the current track");
        MusicCatalog.Merge("""{"tracks":{"probe_missing":{"remove":true}}}""", "res://game/music", "transport cleanup", null);
        Check(music.StepTrack(1), "next selects a playable recording");
        var next = music.RequestedTrackId;
        Check(music.StepTrack(-1), "previous also works during an unfinished crossfade");
        var lastRequest = music.RequestedTrackId;
        await Wait(fade * 2 + .3);
        Check(next != initial && lastRequest == initial && music.CurrentTrackId == lastRequest,
            "rapid next/previous coalesces to the latest request");
        game.VerificationShowAudioPreview("quiet_current");
        (game.FindChild("SoundtrackAutomatic", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2); Check(!music.Manual && !music.Paused, "automatic control releases manual ownership");
        game.VerificationCloseMusicLibrary();
        Check(music.PlayTrack(track.Name), "manual selection can start while director is transitioning");
        var silence = true;
        music.FollowScene(game, () => MusicCatalog.Moment.Campaign, silence: () => silence);
        await Wait(.2);
        Check(music.GetChildren().OfType<AudioStreamPlayer>().All(d => d.VolumeDb <= -79),
            "world silence suppresses both decks during a crossfade without reentering the envelope");
        await Wait(fade * 2 + .2);
        silence = false;
        music.FollowScene(game, () => MusicCatalog.Moment.Campaign, silence: () => silence);
        await Wait(fade + .2);
        Check(music.Manual && music.CurrentTrackId == track.Name, "manual soundtrack resumes after world silence is released");
        Check(CampaignFingerprint.Of(world) == before && world.Random.State == rng, "all player and audio actions are campaign-neutral");
        Check(music.PlayTrack(track.Name), "prepare persistent scene change selection"); await Wait(fade * 2 + .2);
        Check(GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn") == Error.Ok, "return to real menu scene");
        await Wait(2);
        Check(ReferenceEquals(music, MusicDirector.Ensure(GetTree().CurrentScene)) && music.Manual && music.CurrentTrackId == track.Name,
            "scene transition retains the director and the manual recording");
    }
}
