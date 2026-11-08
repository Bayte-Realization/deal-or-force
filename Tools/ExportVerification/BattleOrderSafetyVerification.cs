using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private void ClickBattleControl(Button button, string label)
    {
        // Feed the embedder, not the popup's inherited input-handled state. Window's
        // Position is relative to its embedder and CanvasItem transforms stop at it.
        var viewport = GetTree().Root;
        var point = button.GetGlobalTransformWithCanvas() * (button.Size * .5f);
        if (button.GetWindow() != viewport) point += button.GetWindow().Position;
        viewport.PushInput(new InputEventMouseMotion { Position = point }, true);
        if (button.GetViewport().GuiGetHoveredControl() != button)
            GD.Print($"BATTLE_INPUT_DIAGNOSTIC {label} point={point} viewport={viewport.Name} " +
                $"window={button.GetWindow().Position} hover={viewport.GuiGetHoveredControl()?.Name}");
        Check(button.GetViewport().GuiGetHoveredControl() == button, "battle mouse hit reaches control: " + label);
        viewport.PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        viewport.PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            Pressed = false }, true);
    }

    private async Task VerifyBattleOrderSafety(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var own = c.NationByTag("POL"); var enemy = c.NationByTag("DEU");
        c.ConfigureSinglePlayer(own.Id); c.IsPaused = true;
        var map = c.ProvinceMap;
        var source = BorderFronts.Candidates(c, own.Id, enemy.Id).First(p => map.Neighbors[p].Any(n =>
            c.ProvinceController[n] == own.MapIndex && !map.IsWater(n)));
        var front = map.Neighbors[source].First(p => c.ProvinceController[p] == enemy.MapIndex);
        var home = map.Neighbors[source].First(p => c.ProvinceController[p] == own.MapIndex && !map.IsWater(p));
        c.Armies.Clear(); c.Battles.Clear(); c.Wars.Clear(); c.FrontLines.Clear();
        c.Wars.Add(new StrategicWar { Id = 700, AttackerId = enemy.Id, DefenderId = own.Id });
        (Army attacker, Army defender, LandBattle battle) ResetFight()
        {
            c.ConfigureSinglePlayer(own.Id); c.IsPaused = true;
            c.SetProvinceController(source, (ushort)own.MapIndex);
            c.SetProvinceController(home, (ushort)own.MapIndex);
            c.SetProvinceController(front, (ushort)enemy.MapIndex);
            c.Armies.Clear(); c.Battles.Clear();
            var attacker = new Army { Id = 8001, NationId = enemy.Id, ProvinceId = front,
                StateMapIndex = map.ProvinceToState[front], BattleId = 9001, AssignedWarId = 700,
                OrderKind = ArmyOrder.AdvanceFront, OrderProvinceId = source, Divisions = 1,
                Personnel = 15000, Name = "Attacking formation", Organisation = .9f, Planning = 1f };
            var defender = new Army { Id = 8002, NationId = own.Id, ProvinceId = source,
                StateMapIndex = map.ProvinceToState[source], BattleId = 9001, AssignedWarId = 700,
                OrderKind = ArmyOrder.DefendFront, Divisions = 1, Personnel = 15000,
                Name = "Defending formation", Organisation = .9f };
            (attacker.Latitude, attacker.Longitude) = map.CenterOf(front);
            (defender.Latitude, defender.Longitude) = map.CenterOf(source);
            c.Armies.Add(attacker); c.Armies.Add(defender);
            var battle = new LandBattle { Id = 9001, WarId = 700, ProvinceId = source,
                AttackerId = enemy.Id, DefenderId = own.Id };
            battle.Attackers.Add(attacker.Id); battle.Defenders.Add(defender.Id); c.Battles.Add(battle);
            Warfare.Tick(c);
            return (attacker, defender, battle);
        }

        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size; game.Call("RefreshLocalisedText");
            var (attacker, defender, battle) = ResetFight();
            var clock = c.ElapsedHours; var rng = c.Random.State;
            game.VerificationQueueBattleOrder(defender, home);
            Check(defender.InBattle && defender.ProvinceId == source && defender.PostBattleWaypoints.SequenceEqual(new[] { home }) &&
                !battle.Ended && c.ProvinceController[source] == own.MapIndex && c.IsPaused,
                "actual map-order handler queues without retreat while paused: " + language + size.X);
            await Wait(.2);
            GetViewport().GuiReleaseFocus();
            BattleH(alt: true);
            Check(defender.InBattle && defender.PostBattleWaypoints.Count == 0 && !battle.Ended,
                "actual Alt+H cancels future movement, not the defence: " + language + size.X);
            game.VerificationQueueBattleOrder(defender, home);
            var fp = CampaignFingerprint.Of(c);
            game.VerificationShowOrderBattle(battle);
            await Wait(.4);
            await SettleBattleDossierControls(game, "orders " + language + size.X);
            Check(CampaignFingerprint.Of(c) == fp, "opening localized battle roster is read-only: " + language + size.X);
            var retreat = game.FindChild("WithdrawFormation_8002", true, false) as Button;
            Check(retreat is not null && retreat.IsVisibleInTree(), "explicit retreat remains reachable: " + language + size.X);
            if (retreat is null) continue;
            await RevealBattleOrderControl(retreat, language + size.X);
            await Capture("battle-orders-" + language + "-" + size.X);
            retreat = game.FindChild("WithdrawFormation_8002", true, false) as Button;
            ClickBattleControl(retreat, language + size.X);
            await Wait(.2);
            var dialog = game.GetNodeOrNull<ConfirmationDialog>("ConfirmLastDefenderWithdrawal");
            Check(dialog is { Visible: true } && dialog.Title == Loc.Get("Withdraw the last defenders?") &&
                defender.InBattle && !battle.Ended && CampaignFingerprint.Of(c) == fp,
                "last defender warning appears before any province is yielded: " + language + size.X);
            if (dialog is null) continue;
            await Capture("battle-withdraw-warning-" + language + "-" + size.X);
            ClickBattleControl(dialog.GetCancelButton(), "cancel " + language + size.X);
            await Wait(.2);
            Check(defender.InBattle && !battle.Ended && CampaignFingerprint.Of(c) == fp && c.ElapsedHours == clock &&
                c.Random.State == rng && c.IsPaused,
                "cancel preserves exact authoritative state, clock and pause: " + language + size.X);
            retreat = game.FindChild("WithdrawFormation_8002", true, false) as Button;
            await RevealBattleOrderControl(retreat, "confirmed " + language + size.X);
            retreat = game.FindChild("WithdrawFormation_8002", true, false) as Button;
            ClickBattleControl(retreat, "confirm prompt " + language + size.X);
            await Wait(.2);
            dialog = game.GetNode<ConfirmationDialog>("ConfirmLastDefenderWithdrawal");
            ClickBattleControl(dialog.GetOkButton(), "confirm retreat " + language + size.X);
            await Wait(.2);
            Check(!defender.InBattle && battle.Ended && defender.ProvinceId != source && defender.PostBattleWaypoints.Count == 0,
                "deliberately confirmed retreat leaves the defence and clears its queued orders: " + language + size.X);

            foreach (var defending in new[] { true, false })
            foreach (var ownTile in new[] { false, true })
            {
                (attacker, defender, battle) = ResetFight();
                c.ConfigureSinglePlayer(defending ? own.Id : enemy.Id);
                var selected = defending ? defender : attacker;
                game.VerificationQueueBattleOrder(selected, defending ? home : source);
                var organisation = selected.Organisation; var equipment = selected.Equipment;
                var personnel = selected.Personnel; var hours = c.ElapsedHours;
                GetViewport().GuiReleaseFocus();
                if (ownTile) game.VerificationQueueBattleOrder(selected, selected.ProvinceId);
                else BattleH();
                var label = $"{language}{size.X} {(defending ? "defender" : "attacker")} {(ownTile ? "own-tile RMB" : "H")}";
                Check(!selected.InBattle && battle.Ended && selected.PostBattleWaypoints.Count == 0 &&
                    !game.GetChildren().OfType<ConfirmationDialog>().Any(d => d.Visible),
                    "quick exit leaves battle immediately without confirmation: " + label);
                Check(selected.Organisation == organisation && selected.Equipment == equipment &&
                    selected.Personnel == personnel && c.ElapsedHours == hours && c.IsPaused &&
                    (defending ? selected.ProvinceId != source && selected.ReadyDay > c.ElapsedDays :
                        selected.ProvinceId == front && c.ProvinceController[source] == own.MapIndex),
                    "quick exit uses normal retreat without healing or skipping time: " + label);
                game.VerificationShowOrderBattle(battle);
                await Wait(.15);
                await Capture("battle-quick-exit-" + language + "-" + size.X + "-" + (defending ? "defender" : "attacker") +
                    "-" + (ownTile ? "map" : "h"));
            }

            (attacker, defender, battle) = ResetFight();
            var remaining = new Army { Id = 8003, NationId = own.Id, ProvinceId = source,
                StateMapIndex = map.ProvinceToState[source], BattleId = battle.Id, AssignedWarId = 700,
                Divisions = 1, Personnel = 15000, Organisation = .9f, Name = "Remaining defender" };
            c.Armies.Add(remaining); battle.Defenders.Add(remaining.Id);
            var idle = new Army { Id = 8004, NationId = own.Id, ProvinceId = home,
                StateMapIndex = map.ProvinceToState[home], Divisions = 1, Personnel = 15000,
                OrderProvinceId = source, DirectOrder = true, Name = "Marching formation" };
            idle.Route.Add(source); c.Armies.Add(idle);
            game.VerificationSelectBattleOrders(defender, idle);
            GetViewport().GuiReleaseFocus(); BattleH();
            Check(!defender.InBattle && remaining.InBattle && !battle.Ended &&
                battle.Defenders.SequenceEqual(new[] { remaining.Id }) && c.ProvinceController[source] == own.MapIndex &&
                idle.Route.Count == 0 && idle.OrderProvinceId < 0 && idle.ProvinceId == home && attacker.InBattle,
                "H stops marching selection and withdraws selected combatants only: " + language + size.X);

            (attacker, defender, battle) = ResetFight();
            var neighbors = map.Neighbors[source].ToDictionary(p => p, p => c.ProvinceController[p]);
            try
            {
                foreach (var neighbor in neighbors.Keys) c.SetProvinceController(neighbor, (ushort)enemy.MapIndex);
                game.VerificationSelectBattleOrders(defender);
                var before = CampaignFingerprint.Of(c);
                GetViewport().GuiReleaseFocus(); BattleH();
                Check(CampaignFingerprint.Of(c) == before && defender.InBattle && !battle.Ended,
                    "H cannot teleport an encircled defender out of combat: " + language + size.X);
                game.VerificationQueueBattleOrder(defender, source);
                Check(CampaignFingerprint.Of(c) == before && defender.InBattle && !battle.Ended,
                    "own-tile RMB cannot teleport an encircled defender out of combat: " + language + size.X);
            }
            finally
            {
                foreach (var pair in neighbors) c.SetProvinceController(pair.Key, pair.Value);
            }
        }
    }

    private async Task RevealBattleOrderControl(Button button, string caption)
    {
        // The pinned footer deliberately does not scroll with the report. Reach the
        // actual formation action by scrolling it into view, not by clicking its
        // unclipped rectangle through the footer (which may open the same warning).
        for (Node parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) scroll.EnsureControlVisible(button);
        await Wait(.2);
        var rect = button.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, button.Size);
        Check(GetViewport().GetVisibleRect().Grow(1).Encloses(rect),
            "scrolled formation action fits the viewport: " + caption);
        for (Node parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll)
                Check((scroll.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, scroll.Size)).Grow(1).Encloses(rect),
                    "formation action is revealed above the pinned footer: " + caption);
    }

    private void BattleH(bool alt = false)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = Key.H, PhysicalKeycode = Key.H,
            AltPressed = alt, Pressed = true }, true);
        GetViewport().PushInput(new InputEventKey { Keycode = Key.H, PhysicalKeycode = Key.H,
            AltPressed = alt, Pressed = false }, true);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationQueueBattleOrder(Army army, int target)
    {
        _observerMode = false; _campaign.ObserverMode = false; _campaign.IsPaused = true;
        OnArmyPicked(army);
        var centre = _campaign.ProvinceMap.CenterOf(target);
        OnProvinceCommanded(new GlobePick(_campaign.NationById(army.NationId),
            _campaign.States.Find(state => state.MapIndex == _campaign.ProvinceMap.ProvinceToState[target]),
            target, centre.Latitude, centre.Longitude));
    }
    internal void VerificationShowOrderBattle(LandBattle battle) => ShowBattleDetails(battle);
    internal void VerificationSelectBattleOrders(params Army[] armies)
    {
        _observerMode = false; _campaign.ObserverMode = false; _campaign.IsPaused = true;
        OnArmiesBoxPicked(armies);
    }
}
