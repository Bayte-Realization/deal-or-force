using DealOrForce.Modding;
using DealOrForce.Simulation;
using Godot;
using System.Linq;

namespace DealOrForce;

public partial class ExportVerification
{
    private void VerifyModScalars()
    {
        var world = Campaign.CreateBootstrap(555);
        var nation = world.NationByTag("POL");
        using var api = new ModApi(_ => { });
        api.Bind(world);
        using var bridge = EngineScriptMod.CreateApi(api);
        var stamp = CampaignFingerprint.Of(world);
        Check(!bridge["set_define"].AsCallable().Call("ideology_opinion_weight", double.MaxValue).AsBool(),
            "script Callable rejects finite-double to float overflow");
        Check(!api.Call("set_nation_number", "POL", "population", 9223372036854775808d).AsBool(),
            "Godot snake-case method rejects population Int64 overflow");
        Check(api.Call("get_define", "advisor_endpoint", 17d).AsDouble() == 17,
            "Godot numeric getter safely handles text defines");
        Check(!api.Call("set_define", "advisor_endpoint", 17d).AsBool(),
            "Godot numeric setter preserves text configuration");
        Check(stamp == CampaignFingerprint.Of(world), "invalid script calls leave campaign and RNG unchanged");
        Check(bridge["set_nation_number"].AsCallable().Call("POL", "stability", .8d).AsBool() && nation.Stability == .8f,
            "ordinary script Callable keeps the legacy immediate setter semantics");
        Check(api.Call("set_define", "order-settle-days", 3.5d).AsBool() && world.Defines.OrderSettleDays == 4 &&
              api.Call("get_define", "ORDER_SETTLE_DAYS", -1d).AsDouble() == 4,
            "actual Godot bridge preserves numeric rounding and aliases");
        Check(bridge.IsReadOnly, "script capability dictionary remains read-only");
    }

    private void VerifyMusicExitResources()
    {
        var music = MusicDirector.Ensure(this);
        var decks = music.GetChildren().OfType<AudioStreamPlayer>().ToArray();
        Check(decks.Length == 2 && decks.Any(deck => deck.Stream is not null),
            "music exit regression starts with real loaded audio decks");
        music.GetParent().RemoveChild(music);
        Check(decks.All(deck => !deck.Playing && deck.Stream is null),
            "exiting music director immediately stops playback and releases both streams");
        music.QueueFree();
    }
}
