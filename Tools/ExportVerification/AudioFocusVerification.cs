using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyAudioFocus(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.IsPaused = true;
        var args = OS.GetCmdlineUserArgs();
        var previewAt = System.Array.IndexOf(args, "--preview-media-id");
        var previewId = previewAt >= 0 && previewAt + 1 < args.Length ? args[previewAt + 1] : "quiet_current";
        var preview = MediaLibraryCatalog.Find(previewId);
        Check(preview is { Kind: LibraryMediaKind.Music }, "requested preview names a delivered music recording: " + previewId);
        var before = CampaignFingerprint.Of(world);
        var music = MusicDirector.Ensure(game);
        music.ClearCueFocus();
        var stream = music.GetChildren().OfType<AudioStreamPlayer>().FirstOrDefault(player => player.Playing);
        Check(stream?.Stream is not null, "native music deck has an imported recording");
        var recording = stream?.Stream;
        var position = stream?.GetPlaybackPosition() ?? 0;
        var audible = SettingsCatalog.Number("volume_master") * SettingsCatalog.Number("volume_effects") > .0005f;
        game.VerificationPlayAudioCue("war_declared");
        await Wait(.3);
        Check(audible ? music.CueFocusGain < .6f : music.CueFocusGain == 1,
            "only an actually started strategic cue ducks music; gain=" + music.CueFocusGain);
        Check(audible ? game.VerificationAmbienceFocus() < .4f : game.VerificationAmbienceFocus() == 1,
            "the same strategic cue gives ambience its own lower mix target");
        Check(stream is not null && stream.Stream == recording && stream.GetPlaybackPosition() >= position,
            "ducking does not reload or restart the music recording");
        var focus = music.CueFocusGain;
        game.VerificationPlayAudioCue("ui_select");
        await Wait(.15);
        Check(music.CueFocusGain <= focus + .001f,
            "a navigation click does not release or pump a stronger cue's focus");
        music.SuppressForAudition(true);
        game.VerificationSuppressAmbience(true);
        await Wait(.1);
        Check(music.GetChildren().OfType<AudioStreamPlayer>().All(player => player.VolumeDb <= -79),
            "audition suppresses both decks immediately even during cue focus");
        Check(game.VerificationAmbienceSilent(), "audition immediately silences every map layer");
        music.SuppressForAudition(false);
        game.VerificationSuppressAmbience(false);
        await Wait(5);
        Check(music.CueFocusGain > .85f && game.VerificationAmbienceFocus() > .85f,
            "music and ambience release their focus after the report");
        music.ClearCueFocus();
        Check(music.CueFocusGain == 1, "session release clears residual focus exactly");
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language;
            game.Call("RefreshLocalisedText");
            GetTree().Root.Size = size;
            game.VerificationShowAudioPreview(previewId);
            (game.FindChild("PlayMediaPreview", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.2);
            Check(game.VerificationAudioPreviewPlaying(previewId), "recording imports, plays and uses its declared trim " + language + " " + size.X);
            Check((game.FindChild("MediaTitle", true, false) as Label)?.Text == Loc.Get(preview?.Name ?? ""),
                "music title is localised " + language);
            Check((game.FindChild("LoopMediaPreview", true, false) as CheckBox)?.Text == Loc.Get("Repeat recording"),
                "repeat control survives a live language switch " + language);
            await Capture("audio-library-" + previewId + "-" + language + "-" + size.X);
            game.VerificationCloseMusicLibrary();
        }
        Check(CampaignFingerprint.Of(world) == before, "audio rendering and cue focus change no campaign state");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationPlayAudioCue(string cue) => PlayStrategicCue(cue);
    internal float VerificationAmbienceFocus() => _mapAmbience.CueFocusGain;
    internal void VerificationSuppressAmbience(bool suppressed) => _mapAmbience.AuditionSuppressed = suppressed;
    internal bool VerificationAmbienceSilent() => _mapAmbience.GetChildren().OfType<AudioStreamPlayer>()
        .All(player => player.VolumeDb <= -79);
    internal void VerificationShowAudioPreview(string id)
    {
        OpenMediaLibrary();
        SelectMedia(MediaLibraryCatalog.Find(id));
    }
    internal bool VerificationAudioPreviewPlaying(string id)
    {
        var entry = MediaLibraryCatalog.Find(id);
        if (entry is null) return false;
        var gain = SettingsCatalog.Number("volume_master") * SettingsCatalog.Number("volume_music");
        var expected = gain <= .0005f ? -80 : Mathf.LinearToDb(Mathf.Clamp(gain, 0, 1)) - 4 + entry.VolumeDb;
        return _mediaAudio.Playing && _mediaAudio.Stream is AudioStreamMP3 or AudioStreamOggVorbis &&
            System.Math.Abs(_mediaAudio.Stream.GetLength() - entry.DurationSeconds) < 1 &&
            Mathf.Abs(_mediaAudio.VolumeDb - expected) < .01f && _mediaSelected.Id == id &&
            _mediaSelected.Peaks is { Count: > 0 };
    }
    internal void VerificationCloseMusicLibrary() => CloseMediaLibrary();
}
