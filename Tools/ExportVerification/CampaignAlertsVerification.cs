using System;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyCampaignAlerts(DealOrForceGame game)
    {
        var world=game.VerificationWorld; var n=world.NationByTag("POL"); world.ConfigureSinglePlayer(n.Id);
        world.IsPaused=true; n.Cabinet.Clear();
        typeof(DealOrForceGame).GetField("_selectedNation",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(game,n);
        game.Call("FocusSelectedCapital");
        n.Market[Commodity.Food]=new MarketQuote { Commodity=Commodity.Food,Demand=100,Production=30,Satisfaction=.3f };
        game.Call("Refresh"); game.Call("RefreshSituationRoom",true); await Wait(.3);
        foreach (var (lang,width,height) in new[]{("russian",1920,1080),("russian",1280,720),("english",1920,1080),("english",1280,720)})
        {
            Loc.Language=lang; GetTree().Root.Size=new(width,height); game.Call("RefreshLocalisedText");
            game.Call("RefreshSituationRoom",true); await Wait(.4);
            var fp=CampaignFingerprint.Of(world);
            var strip=game.FindChild("CampaignAlerts",true,false) as Control;
            var icons=strip.FindChildren("CampaignIcon_*","Button",true,false).OfType<Button>().Where(b=>b.Visible).ToArray();
            Check(strip.IsVisibleInTree() && icons.Length>0 && icons.All(b=>b is CampaignHintButton { Glyph:not null }),"top hints use the generated atlas: "+lang+width);
            Check(icons.Cast<CampaignHintButton>().All(b => b.GlyphRect.GetCenter().DistanceTo(b.Size*.5f)<.001f),
                "every visible glyph's alpha-cropped rectangle is exactly centred");
            Check(new[] { "must", "should", "could", "important", "treasury", "good_food", "materials", "good_energy",
                "good_ammunition", "good_copper", "good_lithium", "good_sulfur", "research", "elections", "decision", "personnel",
                "alert_construction", "alert_supply", "diplomacy", "organisations", "wars", "alert_battle", "unrest", "alert_event" }
                .All(key => CampaignHintArt.Of(key) is AtlasTexture { Region.Size.X: > 0, Region.Size.Y: > 0 }),
                "all 24 generated symbols have nonempty cropped atlas regions");
            Check(icons.All(b=>b.GetGlobalRect().Size.X>=30 && b.GetGlobalRect().Size.Y>=30),"hint hit areas remain usable");
            Check(strip.GetGlobalRect().End.X<=game.Size.X && strip.GetGlobalRect().Position.Y>0,"hint strip fits viewport");
            Check(icons.All(b=>!string.IsNullOrEmpty(b.TooltipText) && b.TooltipText.Contains(Loc.Get("Left-click: open. Right-click: hide temporarily (urgent hints return tomorrow)."))),"all icon hover cards explain both mouse buttons");
            await Capture("top-hints-"+lang+"-"+width);
            var hovered=icons.First();
            GetViewport().NotifyMouseExited(); GetViewport().NotifyMouseEntered();
            // The responsive HUD lives in a scaled CanvasLayer. GlobalRect is in
            // canvas coordinates; input belongs to the viewport, including that scale.
            var mousePoint=hovered.GetGlobalTransformWithCanvas()*(hovered.Size*.5f);
            GetViewport().PushInput(new InputEventMouseMotion { Position=mousePoint },true); await Wait(.9);
            Check(GetViewport().GuiGetHoveredControl()==hovered,"real viewport mouse motion reaches hint icon");
            await Capture("hint-hover-"+lang+"-"+width);
            GetViewport().PushInput(new InputEventMouseMotion { Position=new Vector2(300,300) },true);
            (game.FindChild("CampaignAlertGroupMust",true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.2); await Capture("hints-expanded-"+lang+"-"+width);
            Check(CampaignFingerprint.Of(world)==fp,"opening hint list is read-only");
            (game.FindChild("CampaignAlertGroupMust",true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        }
        var hint=game.FindChild("CampaignIcon_shortage_Food",true,false) as Button;
        Check(hint is { Visible:true },"precise food warning has a top icon");
        var fingerprint=CampaignFingerprint.Of(world);
        hint?.EmitSignal(Control.SignalName.GuiInput,new InputEventMouseButton { ButtonIndex=MouseButton.Right,Pressed=true });
        Check(hint is { Visible:false } && CampaignFingerprint.Of(world)==fingerprint,"actual RMB hides hint without changing campaign");
        (game.FindChild("RestoreCampaignAlerts",true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(hint is { Visible:true },"hidden hint can be restored immediately");
        hint?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.3);
        Check(game.FindChild("MarketSearch",true,false) is LineEdit search && search.Text==Loc.Get(CommodityCatalog.Get(Commodity.Food).Name),"actual LMB opens responsible commodity market");
        Check(CampaignFingerprint.Of(world)==fingerprint,"hint navigation does not execute a policy or offer");
        game.Call("CloseSidePanel");
        game.Call("EnsureMapSelectionPanels");
        typeof(DealOrForceGame).GetField("_selectedNation",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(game,n);
        typeof(DealOrForceGame).GetField("_countryDossierTab",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(game,"Government");
        typeof(DealOrForceGame).GetField("_mapSelectionNation",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(game,n);
        typeof(DealOrForceGame).GetField("_mapSelectionOpen",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(game,true);
        game.Call("ShowMapSelectionContent");
        var research=game.FindChild("StaffPortfolio_research",true,false) as CheckButton;
        Check(research is not null,"country government card exposes research delegation");
        if (research is not null)
        {
            research.EmitSignal(BaseButton.SignalName.Toggled,false);
            Check(!Delegation.Holds(n,Delegation.Research),"actual staff switch revokes automatic research selection");
        }
        Check(world.IsPaused,"verification campaign remains paused");
        await VerifyDiplomaticMissionInterface(game);
    }

    private async Task VerifyDiplomaticMissionInterface(DealOrForceGame game)
    {
        var world=game.VerificationWorld; var player=world.Player; var target=world.NationByTag("DEU");
        var relation=CampaignDiplomacy.Get(world,player.Id,target.Id);
        player.PoliticalPoints=100; relation.LastActionDay=-30;
        foreach(var (lang,width,height) in new[]{("english",1920,1080),("russian",1920,1080),("russian",1280,720),("english",1280,720)})
        {
            Loc.Language=lang; GetTree().Root.Size=new(width,height); game.Call("RefreshLocalisedText");
            typeof(DealOrForceGame).GetField("_selectedNation",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(game,target);
            game.Call("OpenDiplomacyScreen"); await Wait(.3);
            var button=(Button)typeof(DealOrForceGame).GetField("_diplomacyImproveButton",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(game);
            Check(button.IsVisibleInTree() && !button.Disabled,"diplomatic mission has a usable real action: "+lang+width);
            Check(button.TooltipText.Contains("180") && button.TooltipText.Contains("1.5".Replace('.',Loc.Culture.NumberFormat.NumberDecimalSeparator[0])),
                "diplomacy explains duration and weekly political price in the active locale");
            GetViewport().NotifyMouseExited(); GetViewport().NotifyMouseEntered();
            GetViewport().PushInput(new InputEventMouseMotion { Position=button.GetGlobalTransformWithCanvas()*(button.Size*.5f) },true);
            await Wait(.9); await Capture("diplomatic-mission-price-"+lang+"-"+width);
            GetViewport().PushInput(new InputEventMouseMotion { Position=new Vector2(300,300) },true);
        }
        var action=(Button)typeof(DealOrForceGame).GetField("_diplomacyImproveButton",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(game);
        var before=player.PoliticalPoints; var opinion=relation.Opinion;
        action.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.2);
        Check(player.PoliticalPoints==before-world.Defines.DiplomaticMissionPoliticalCost && DiplomaticOutreach.Of(relation,player.Id) is not null,
            "real diplomacy button dispatches and pays for the authoritative mission");
        Check(relation.Opinion==opinion,"real UI does not secretly grant instant goodwill");
        Check(action.Text==Loc.Get("Recall diplomatic mission") && !action.Disabled,"running mission exposes recall instead of a duplicate dispatch");
        action.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.2);
        Check(DiplomaticOutreach.Of(relation,player.Id) is null && player.PoliticalPoints==before-world.Defines.DiplomaticMissionPoliticalCost,
            "real recall stops the assignment with no refund or duplicate payment");
        relation.LastActionDay=-30;
        game.Call("CloseDiplomacyScreen");
        var feedbackField=typeof(DealOrForceGame).GetField("_diplomacyFeedback",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var oldFeedback=feedbackField.GetValue(game); feedbackField.SetValue(game,null);
        typeof(DealOrForceGame).GetMethod("ShowMapDiplomacyMenu",BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(game,new object[]{target,new Vector2(500,300)});
        var mapAction=game.FindChild("MapDiplomaticMission",true,false) as Button;
        Check(mapAction is { Disabled:false },"floating map diplomacy exposes the same available mission");
        mapAction.EmitSignal(BaseButton.SignalName.Pressed);
        Check(DiplomaticOutreach.Of(relation,player.Id) is not null && mapAction.Text==Loc.Get("Recall diplomatic mission"),
            "map action pays and refreshes to recall without requiring the wide diplomacy screen");
        mapAction.EmitSignal(BaseButton.SignalName.Pressed);
        Check(DiplomaticOutreach.Of(relation,player.Id) is null,"map action can recall the same real mission");
        feedbackField.SetValue(game,oldFeedback);
        game.Call("CloseMapDiplomacyMenu");
    }
}
