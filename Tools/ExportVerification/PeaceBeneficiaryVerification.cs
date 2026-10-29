using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyPeaceBeneficiary(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var own = c.NationByTag("POL"); var enemy = c.NationByTag("DEU"); var ally = c.NationByTag("CZE");
        Check(own is not null && enemy is not null && ally is not null && c.ProvinceMap is not null,
            "peace fixture uses real countries and the loaded map");
        if (own is null || enemy is null || ally is null || c.ProvinceMap is null) return;
        c.ConfigureSinglePlayer(own.Id); c.IsPaused = true; enemy.IsHuman = ally.IsHuman = false;
        c.Wars.Clear(); c.Battles.Clear();
        var war = new StrategicWar { Id = 880040, AttackerId = own.Id, DefenderId = enemy.Id,
            WarScore = 100, DefenderWarSupport = 0, StartedDay = c.ElapsedDays - 1000 };
        war.AttackerAllies.Add(ally.Id); c.Wars.Add(war);
        c.Defines.PeaceWholeRegions = true; c.Defines.PeaceCostPerProvince = .001f;
        var map = c.ProvinceMap;
        var region = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && map.ProvinceToNation[p] == enemy.MapIndex)
            .GroupBy(p => map.ProvinceToState[p]).OrderBy(g => g.Count()).ThenBy(g => g.Key).First();
        var ground = region.ToArray(); var province = ground[0];
        var fp = CampaignFingerprint.Of(c); var random = c.Random.State;
        game.VerificationOpenPeace(war);
        await Wait(.3);
        game.VerificationFocusProvince(province);
        var picker = game.FindChild("PeaceBeneficiary", true, false) as OptionButton;
        Check(picker is not null && picker.GetItemIndex(ally.Id) >= 0 && picker.GetItemIndex(enemy.Id) < 0,
            "real recipient picker includes the ally and excludes the enemy");
        if (picker is null) return;
        var index = picker.GetItemIndex(ally.Id); picker.Select(index);
        picker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
        game.VerificationPaintPeace(province);
        game.VerificationPeaceTerm("reparations").EmitSignal(BaseButton.SignalName.Pressed);
        game.VerificationPeaceTerm("trade").EmitSignal(BaseButton.SignalName.Pressed);
        Check(game.VerificationPeaceDraft.BeneficiaryId == ally.Id && game.VerificationPeacePreviewOwner == ally.MapIndex,
            "real picker and brush preview land in the actual recipient's colour");
        Check(ground.All(p => map.ProvinceToNation[p] == enemy.MapIndex) && CampaignFingerprint.Of(c) == fp && c.Random.State == random,
            "opening, selecting, painting and pricing change no campaign authority");
        foreach (var (language, width, height) in new[] { ("english",1920,1080), ("english",1280,720), ("russian",1920,1080), ("russian",1280,720) })
        {
            Loc.Language = language; GetTree().Root.Size = new(width,height); game.Call("RefreshLocalisedText");
            game.VerificationRefreshPeace(); await Wait(.35);
            var propose = game.FindChild("ConferencePropose", true, false) as Button;
            var leave = game.FindChild("ConferenceLeave", true, false) as Button;
            Check(propose is { Disabled:false } && propose.Text == Loc.Get("Propose") && leave is not null,
                "localized signing controls are reachable: " + language + width);
            foreach (var control in new Control[] { picker, propose, leave })
                if (control is not null)
                {
                    var rect = control.GetGlobalRect();
                    var viewport = game.GetViewportRect(); // canvas_items keeps logical coordinates at the design size.
                    Check(viewport.Grow(1).Encloses(rect),
                        "recipient and fixed footer fit viewport: " + control.Name + language + width);
                }
            await Capture("peace-beneficiary-" + language + "-" + width);
            if (width == 1280)
            {
                (game.FindChild("PeaceTreatyTab", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(.1);
                var body = game.FindChild("PeaceBodyScroll",true,false) as ScrollContainer;
                var lastTerm = game.VerificationLastPeaceTerm;
                body?.EnsureControlVisible(lastTerm); await Wait(.2);
                Check(body!.GetGlobalRect().Grow(1).Encloses(lastTerm.GetGlobalRect()),
                    "treaty page scroll reaches all additional terms in 720p");
                foreach (var control in new Control[] { picker, propose, leave,
                             game.FindChild("PeaceBudgetSummary", true, false) as Control,
                             game.FindChild("PeaceSigningStatus", true, false) as Control })
                    Check(control is not null && control.IsVisibleInTree() && !body.IsAncestorOf(control) &&
                        game.GetViewportRect().Grow(1).Encloses(control.GetGlobalRect()),
                        "recipient, totals, status and signing remain fixed while reading the last term: " + control?.Name);
                await Capture("peace-beneficiary-terms-" + language + "-" + width);
                (game.FindChild("PeaceTerritoryTab", true, false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(.1);
            }
            Check(CampaignFingerprint.Of(c) == fp && c.Random.State == random,
                "language, scrolling and preview capture preserve campaign authority");
        }
        var submit = game.FindChild("ConferencePropose",true,false) as Button;
        c.Commands.SubmitInterceptor = _ => true;
        submit!.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
        Check(CampaignFingerprint.Of(c) == fp && game.VerificationPeaceVerdict == Loc.Get("Order sent to the host. Awaiting confirmation."),
            "intercepted client signing stays pending without local settlement");
        c.Commands.SubmitInterceptor = null;
        enemy.IsHuman = true; game.VerificationRefreshPeace();
        Check(game.VerificationPeaceVerdict == Loc.Get("These terms can be offered; a human player must confirm."),
            "human opponent preview never falsely promises automatic signature");
        enemy.IsHuman = false; ally.IsHuman = true; game.VerificationRefreshPeace();
        Check(submit.Disabled && game.VerificationPeaceVerdict == PeaceOffers.DeliveryBlockReason(c,war,own,enemy),
            "real signing control uses the same human-coalition refusal as execution");
        ally.IsHuman = false; c.Defines.PeaceAlliedBenefits = false; game.VerificationRefreshPeace();
        Check(submit.Disabled && game.VerificationPeaceDraft.BeneficiaryId == ally.Id,
            "a now-invalid recipient is refused instead of silently changed to the proposer");
        c.Defines.PeaceAlliedBenefits = true; game.VerificationRefreshPeace();
        var ownCash = own.Treasury; var allyCash = ally.Treasury;
        submit.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.3);
        Check(war.Ended && war.WinnerId == own.Id && ground.All(p => map.ProvinceToNation[p] == ally.MapIndex),
            "actual Propose button awards the selected region to the ally");
        Check(ally.Treasury > allyCash && own.Treasury == ownCash && CampaignDiplomacy.Peek(c,ally.Id,enemy.Id).TradeAgreement,
            "actual signing gives cash and trade to the named beneficiary, not the author");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenPeace(StrategicWar war) => OpenPeaceConference(war);
    internal void VerificationRefreshPeace() => RefreshPeaceConference();
    internal PeaceDemand VerificationPeaceDraft => _peaceDemand;
    internal int VerificationPeacePreviewOwner => _globe.VerificationPeacePreviewOwner;
    internal string VerificationPeaceVerdict => _peaceVerdict.Text;
    internal Button VerificationPeaceTerm(string term) => _peaceTermButtons[term];
    internal Button VerificationLastPeaceTerm => _peaceTermButtons.Values.Last();
    internal void VerificationPaintPeace(int province)
    {
        var point = _campaign.ProvinceMap.CenterOf(province);
        OnProvincePainted(new GlobePick(null,null,province,point.Latitude,point.Longitude), true);
    }
}

public sealed partial class GlobePresentation
{
    internal int VerificationPeacePreviewOwner => _peacePreviewOwner;
}
