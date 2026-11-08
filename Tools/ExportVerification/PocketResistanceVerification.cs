using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task SettleBattleDossierControls(DealOrForceGame game, string caption)
    {
        Rect2 previous = default;
        var stable = 0;
        for (var frame = 0; frame < 24; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var actions = game.FindChild("BattleActions", true, false) as Control;
            var toggle = game.FindChild("BattleRosterToggle", true, false) as Button;
            if (actions is null || toggle is null || !actions.IsVisibleInTree()) continue;
            var rect = actions.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, actions.Size);
            var fits = GetViewport().GetVisibleRect().Grow(1).Encloses(rect);
            stable = fits && rect.IsEqualApprox(previous) ? stable + 1 : 0;
            previous = rect;
            if (stable >= 4)
            {
                Check(true, "battle footer settles inside the viewport: " + caption);
                return;
            }
        }
        GD.Print($"BATTLE_LAYOUT_DIAGNOSTIC {caption} canvas={GetViewport().GetVisibleRect()} " +
            $"window={GetWindow().Size} footer={previous}");
        Check(false, "battle footer settles inside the viewport: " + caption);
    }

    private async Task VerifyPocketResistance(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var own = c.NationByTag("POL"); var enemy = c.NationByTag("DEU");
        Check(own is not null && enemy is not null, "pocket probe uses actual Poland and Germany");
        if (own is null || enemy is null) return;
        c.ConfigureSinglePlayer(own.Id); c.IsPaused = true;
        var map = c.ProvinceMap;
        var source = BorderFronts.Candidates(c, own.Id, enemy.Id).First(p =>
            map.Neighbors[p].Any(n => !map.IsWater(n) && c.ProvinceController[n] == enemy.MapIndex));
        var front = map.Neighbors[source].First(p => !map.IsWater(p) && c.ProvinceController[p] == enemy.MapIndex);
        c.Wars.Clear(); c.Battles.Clear(); c.Armies.Clear(); c.FrontLines.Clear();
        c.Wars.Add(new StrategicWar { Id = 700, AttackerId = enemy.Id, DefenderId = own.Id });
        var oldLanguage = Loc.Language; var oldSize = GetTree().Root.Size;
        var oldScale = SettingsCatalog.Number("interface_scale");
        try
        {
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language; GetTree().Root.Size = size;
                SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale");
                game.Call("RefreshLocalisedText"); game.Call("CloseSidePanel");
                c.SetProvinceController(source, (ushort)own.MapIndex);
                c.Armies.Clear(); c.Battles.Clear();
                var battle = new LandBattle { Id = 9001, WarId = 700, ProvinceId = source,
                    AttackerId = enemy.Id, DefenderId = own.Id, StartedDay = c.ElapsedDays };
                var defender = new Army { Id = 8002, NationId = own.Id, ProvinceId = source,
                    StateMapIndex = map.ProvinceToState[source], AssignedWarId = 700, BattleId = 9001,
                    Organisation = .001f, Strength = 1, Divisions = 1, SupplyLevel = .75f, PocketStores = 10,
                    Personnel = 15000, OrderKind = ArmyOrder.DefendFront, DirectOrder = true };
                (defender.Latitude, defender.Longitude) = map.CenterOf(source);
                c.Armies.Add(defender); battle.Defenders.Add(defender.Id);
                var id = 8100;
                foreach (var province in map.Neighbors[source].Where(p => !map.IsWater(p)))
                {
                    c.SetProvinceController(province, (ushort)enemy.MapIndex);
                    var attacker = new Army { Id = id++, NationId = enemy.Id, ProvinceId = province,
                        StateMapIndex = map.ProvinceToState[province], AssignedWarId = 700,
                        Divisions = 1, Strength = 1, Organisation = 1, SupplyLevel = 1,
                        Personnel = 15000, DirectOrder = true };
                    (attacker.Latitude, attacker.Longitude) = map.CenterOf(province);
                    c.Armies.Add(attacker);
                    if (province == front) { attacker.BattleId = battle.Id; battle.Attackers.Add(attacker.Id); }
                }
                c.Battles.Add(battle); Warfare.Tick(c);
                var caption = language + "-" + size.X;
                Check(!battle.Ended && defender.InBattle && c.Armies.Contains(defender) &&
                    c.ProvinceController[source] == own.MapIndex, "closed-ring defender survives organisation collapse: " + caption);
                Check(defender.Strength < 1 && battle.DefenderLosses > 0 && battle.Rounds.Last().Note == PocketResistance.BattleNote,
                    "native battle actually loses men and records resistance: " + caption);
                var fp = CampaignFingerprint.Of(c); var rng = c.Random.State;
                game.VerificationShowOrderBattle(battle); await Wait(.4);
                await SettleBattleDossierControls(game, "opened " + caption);
                var note = game.FindChildren("*", "Label", true, false).OfType<Label>()
                    .FirstOrDefault(label => label.IsVisibleInTree() && label.Text == Loc.Get(PocketResistance.BattleNote));
                Check(note is not null, "actual battle dossier explains pocket resistance: " + caption);
                if (note is not null)
                {
                    for (Node parent = note.GetParent(); parent is not null; parent = parent.GetParent())
                        if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(note);
                    await Wait(.2);
                    Check(GetViewport().GetVisibleRect().Grow(1).Encloses(
                        note.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, note.Size)),
                        "pocket explanation is inside the viewport: " + caption);
                    var pixels = (GetViewport().GetStretchTransform() * note.GetGlobalTransformWithCanvas()).Scale.Abs();
                    var font = note.LabelSettings?.FontSize ?? note.GetThemeFontSize("font_size");
                    Check(font * MathF.Min(pixels.X, pixels.Y) >= Ui.Metric("decision.minimum_font_pixels", 14) - .1f,
                        "pocket explanation has a readable physical font size: " + caption);
                }
                Check(CampaignFingerprint.Of(c) == fp && c.Random.State == rng,
                    "battle dossier and map do not alter the pocket or random stream: " + caption);
                var actions = game.FindChild("BattleActions", true, false) as Control;
                var roster = game.FindChild("BattleRosterToggle", true, false) as Button;
                var retreat = game.FindChild("WithdrawFormation_8002", true, false) as Button;
                var refusal = game.FindChild("BattleWithdrawalRefusal", true, false) as Label;
                Check(actions is not null && actions.IsVisibleInTree() &&
                    GetViewport().GetVisibleRect().Grow(1).Encloses(actions.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, actions.Size)),
                    "battle actions stay inside the viewport outside scrolling content: " + caption);
                Check(retreat is { Disabled: true } && refusal?.Text == Loc.Get("No friendly line of retreat") &&
                    (game.FindChild("WithdrawBattleSide", true, false) as Button) is { Disabled: true },
                    "closed pocket disables both withdrawal controls and explains why: " + caption);
                var body = game.FindChild("BattleDossier", true, false) as Control;
                Check(body is not null && body.FindChildren("*", "Label", true, false).OfType<Label>().All(label =>
                {
                    var scale = (GetViewport().GetStretchTransform() * label.GetGlobalTransformWithCanvas()).Scale.Abs();
                    return (label.LabelSettings?.FontSize ?? label.GetThemeFontSize("font_size")) * MathF.Min(scale.X, scale.Y) >= 13.9f;
                }), "all battle body text meets the physical reading floor: " + caption);
                Check(game.FindChild("BattleEndurance", true, false) is null && body is not null &&
                    body.FindChildren("*", "Label", true, false).OfType<Label>().Any(label => label.Text == Loc.Get("Encircled defenders are holding")),
                    "holding pocket does not display a misleading zero-endurance forecast: " + caption);
                if (roster is not null)
                {
                    ClickBattleControl(roster, "fold roster " + caption);
                    await SettleBattleDossierControls(game, "folded " + caption);
                    roster = game.FindChild("BattleRosterToggle", true, false) as Button;
                    Check(roster?.Text == Loc.Get("Show formations") && game.FindChild("WithdrawFormation_8002", true, false) is null,
                        "folding hides the detailed formation cards: " + caption);
                    Check(CampaignFingerprint.Of(c) == fp && c.Random.State == rng,
                        "folding is presentation-only: " + caption);
                    ClickBattleControl(roster, "unfold roster " + caption);
                    await SettleBattleDossierControls(game, "unfolded " + caption);
                    Check((game.FindChild("BattleRosterToggle", true, false) as Button)?.Text == Loc.Get("Hide formations") &&
                        game.FindChild("WithdrawFormation_8002", true, false) is Button,
                        "formation cards can be restored with the pinned action: " + caption);
                }
                GetViewport().PushInput(new InputEventMouseMotion { Position = new Vector2(1000, 80) }, true);
                await Capture("pocket-resistance-" + caption);
                defender.Strength = .24f; Warfare.Tick(c);
                Check(battle.Ended && !c.Armies.Contains(defender) && c.ProvinceController[source] == enemy.MapIndex,
                    "the weakened defence falls in the native campaign too: " + caption);
            }
        }
        finally
        {
            Loc.Language = oldLanguage; GetTree().Root.Size = oldSize;
            SettingsCatalog.Store("interface_scale", oldScale); game.Call("ApplySetting", "interface_scale");
            game.Call("CloseSidePanel"); game.Call("RefreshLocalisedText");
        }
    }
}
