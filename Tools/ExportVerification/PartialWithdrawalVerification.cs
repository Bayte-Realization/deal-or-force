using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyPartialWithdrawal(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var own = c.NationByTag("POL"); var enemy = c.NationByTag("DEU");
        Check(own is not null && enemy is not null && c.ProvinceMap is not null,
            "native withdrawal fixture uses actual scenario tags and loaded topology");
        if (own is null || enemy is null || c.ProvinceMap is null) return;
        c.ConfigureSinglePlayer(own.Id); c.IsPaused = true;
        c.Armies.Clear(); c.Battles.Clear(); c.Wars.Clear(); c.FrontLines.Clear();
        var source = BorderFronts.Candidates(c, own.Id, enemy.Id).First(p =>
            c.ProvinceMap.Neighbors[p].Any(n => c.ProvinceController[n] == enemy.MapIndex));
        var target = c.ProvinceMap.Neighbors[source].First(p => c.ProvinceController[p] == enemy.MapIndex);
        c.Wars.Add(new StrategicWar { Id = 700, AttackerId = own.Id, DefenderId = enemy.Id });
        var show = typeof(DealOrForceGame).GetMethod("ShowBattleDetails", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (lang, width, height) in new[] { ("russian",1920,1080), ("russian",1280,720), ("english",1920,1080), ("english",1280,720) })
        {
            Loc.Language = lang; GetTree().Root.Size = new(width, height); game.Call("RefreshLocalisedText");
            c.Armies.Clear(); c.Battles.Clear();
            var a = new Army { Id = 8001, NationId = own.Id, ProvinceId = source,
                StateMapIndex = c.ProvinceMap.ProvinceToState[source], Divisions = 20, Personnel = 200_000,
                MaxPersonnel = 300_000, Equipment = .4f, Organisation = .7f, Strength = .6f,
                Name = "Verification formation", BattleId = 9001, AssignedWarId = 700, OrderKind = ArmyOrder.AdvanceFront };
            (a.Latitude,a.Longitude) = c.ProvinceMap.CenterOf(source); c.Armies.Add(a);
            var d = new Army { Id = 8002, NationId = enemy.Id, ProvinceId = target,
                Divisions = 1, Name = "Verification defender", BattleId = 9001, AssignedWarId = 700 };
            (d.Latitude,d.Longitude) = c.ProvinceMap.CenterOf(target); c.Armies.Add(d);
            var battle = new LandBattle { Id = 9001, WarId = 700, ProvinceId = target,
                AttackerId = own.Id, DefenderId = enemy.Id, AttackerLosses = 123, DefenderLosses = 456 };
            battle.Attackers.Add(a.Id); battle.Defenders.Add(d.Id); c.Battles.Add(battle);
            var fp = CampaignFingerprint.Of(c);
            show.Invoke(game, new object[] { battle }); await Wait(.5);
            var half = game.FindChild("WithdrawHalf_8001", true, false) as Button;
            Check(half is { Visible:true } && half.IsVisibleInTree() && half.Text == Loc.Get("Withdraw half"),
                "localized partial withdrawal is reachable in the real battle dossier: " + lang + width);
            Check(CampaignFingerprint.Of(c) == fp, "opening battle dossier never withdraws troops");
            if (half is null) continue;
            var rect = half.GetGlobalRect();
            Check(rect.Position.X >= 0 && rect.End.X <= game.Size.X + 1 && rect.End.Y <= game.Size.Y + 1,
                "partial withdrawal control fits the viewport");
            await Capture("partial-withdrawal-" + lang + "-" + width);
            half.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.2);
            Check(c.Armies.Count == 3 && c.Armies[0].Divisions == 10 && c.Armies[^1].Divisions == 10 &&
                c.Armies[0].InBattle && !c.Armies[^1].InBattle && !battle.Ended &&
                c.Armies.Where(u => u.NationId == own.Id).Sum(u => u.Personnel) == 200_000,
                "actual button leaves half fighting and preserves personnel");
            Check(battle.AttackerLosses == 123 && battle.DefenderLosses == 456 && c.Armies[^1].Equipment == .4f,
                "button preserves damage, equipment and battle history");
            show.Invoke(game, new object[] { battle }); await Wait(.2);
            var pendingButton = game.FindChild("WithdrawHalf_8001",true,false) as Button;
            c.Commands.SubmitInterceptor = _ => true;
            var before = CampaignFingerprint.Of(c);
            pendingButton?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            var feedback = (string)typeof(DealOrForceGame).GetField("_battleWithdrawalFeedback", BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(game)!;
            Check(CampaignFingerprint.Of(c) == before && feedback == Loc.Get("Order sent to the host. Awaiting confirmation."),
                "remote command stays pending rather than falsely reporting a retreat failure");
            c.Commands.SubmitInterceptor = null;
        }
    }
}
