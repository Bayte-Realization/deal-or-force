using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _nuclearStrikeComposer, _nuclearStrikeReturnFocus;
    private OptionButton _nuclearCountry, _nuclearTargetKind;
    private Label _nuclearStrikePreview, _nuclearStrikeFeedback;
    private Button _nuclearStrikeSend;
    private bool _nuclearStrikeReviewed;
    private int _nuclearStrikeReviewDay, _nuclearStrikeReviewProvince, _nuclearStrikeIssuer;

    private void CloseNuclearStrikeReview()
    {
        if (IsInstanceValid(_nuclearStrikeComposer))
        {
            _nuclearStrikeComposer.GetParent()?.RemoveChild(_nuclearStrikeComposer); _nuclearStrikeComposer.QueueFree();
            if (_globe is not null) _globe.LoadingInputBlocked = false;
        }
        _nuclearStrikeComposer = null;
        RestoreDecisionFocus(ref _nuclearStrikeReturnFocus);
    }

    private void OpenNuclearStrikeReview(bool cities)
    {
        if (_campaign?.Player is not { } player) return;
        CloseNuclearStrikeReview(); CloseWarDeclaration(); CloseDealComposer(); CloseUltimatumComposer();
        _nuclearStrikeReturnFocus = GetViewport().GuiGetFocusOwner(); _nuclearStrikeIssuer = player.Id;
        _nuclearStrikeComposer = FullLayer("NuclearStrikeComposer"); AddChild(_nuclearStrikeComposer);
        _nuclearStrikeComposer.MoveToFront(); _globe.LoadingInputBlocked = true;
        var veil = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _nuclearStrikeComposer.AddChild(veil);
        var panel = TexturedPanel(_nuclearStrikeComposer, PanelSurface.Modal); panel.Name = "NuclearStrikePanel";
        Ui.Place(panel, "modal.ultimatum");
        var rows = PanelRows(panel, 20, 16, 12);
        ScreenHeader(rows, "Nuclear strike planning", "Choose the country explicitly. No order is sent while you are choosing or reviewing.",
            false, ActionButton("×", CloseNuclearStrikeReview, 38, 32));
        SectionRule(rows, "Nuclear target country");
        _nuclearCountry = new OptionButton { Name = "NuclearTargetCountry", FitToLongestItem = false,
            ExpandIcon = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 40) };
        _nuclearCountry.AddThemeConstantOverride("icon_max_width", 32);
        _nuclearCountry.AddItem(Tr("Choose a target country…")); _nuclearCountry.SetItemMetadata(0, -1);
        foreach (var nation in NuclearPosture.Targets(_campaign, player).OrderBy(n => n.DisplayName))
        {
            var index = _nuclearCountry.ItemCount;
            _nuclearCountry.AddIconItem(FlagTextureFor(nation.Tag), nation.DisplayName);
            _nuclearCountry.GetPopup().SetItemIconMaxWidth(index, 32);
            _nuclearCountry.SetItemMetadata(index, nation.Id);
        }
        rows.AddChild(_nuclearCountry);
        rows.AddChild(WrappedUltimatumText("Only opponents in active interstate wars are listed. Selecting a country does not start a war or bypass readiness, cost or cooldown.", 14));
        SectionRule(rows, "Type of strike");
        _nuclearTargetKind = new OptionButton { Name = "NuclearTargetKind", FitToLongestItem = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 40) };
        _nuclearTargetKind.AddItem(Tr("Enemy armed forces")); _nuclearTargetKind.AddItem(Tr("Enemy cities"));
        _nuclearTargetKind.Select(cities ? 1 : 0); rows.AddChild(_nuclearTargetKind);
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        rows.AddChild(scroll);
        _nuclearStrikePreview = WrappedUltimatumText("", 14); _nuclearStrikePreview.Name = "NuclearStrikePreview"; scroll.AddChild(_nuclearStrikePreview);
        _nuclearStrikeFeedback = WrappedUltimatumText("", 14); _nuclearStrikeFeedback.Name = "NuclearStrikeFeedback";
        _nuclearStrikeFeedback.LabelSettings.FontColor = Ui.Colour("state.warning"); rows.AddChild(_nuclearStrikeFeedback);
        var actions = new HBoxContainer(); actions.AddThemeConstantOverride("separation", 12); rows.AddChild(actions);
        var cancel = ActionButton("Cancel", CloseNuclearStrikeReview, 0, 40); cancel.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(cancel);
        _nuclearStrikeSend = ActionButton("Review nuclear strike", SubmitChosenNuclearStrike, 0, 40, true);
        _nuclearStrikeSend.Name = "SendChosenNuclearStrike"; _nuclearStrikeSend.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(_nuclearStrikeSend);
        _nuclearCountry.ItemSelected += _ => RefreshChosenNuclearStrike();
        _nuclearTargetKind.ItemSelected += _ => RefreshChosenNuclearStrike();
        RefreshChosenNuclearStrike(); ReadableDecision(panel, "modal.ultimatum"); _nuclearCountry.GrabFocus();
    }

    private Nation ChosenNuclearCountry() => _nuclearCountry.Selected > 0
        ? _campaign.NationById(_nuclearCountry.GetItemMetadata(_nuclearCountry.Selected).AsInt32()) : null;

    private int ChosenNuclearProvince(Nation target) => _nuclearTargetKind.Selected == 1
        ? NuclearPosture.CityTarget(_campaign, _campaign.Player, out _, target)
        : NuclearPosture.StrikeTarget(_campaign, _campaign.Player, out _, target);

    private void RefreshChosenNuclearStrike()
    {
        _nuclearStrikeReviewed = false; _nuclearStrikeSend.Text = Tr("Review nuclear strike");
        var target = ChosenNuclearCountry(); var cities = _nuclearTargetKind.Selected == 1;
        var why = target is null ? Tr("Choose a target country before reviewing the order.")
            : NuclearPosture.Refusal(_campaign, _campaign.Player, cities ? "devastate" : "strike", target);
        _nuclearStrikeSend.Disabled = why.Length > 0; _nuclearStrikeSend.TooltipText = why;
        _nuclearStrikeFeedback.Text = why;
        var province = target is null ? 0 : ChosenNuclearProvince(target);
        var region = province > 0 ? _campaign.States.Find(s => s.MapIndex == _campaign.ProvinceMap.ProvinceToState[province]) : null;
        _nuclearStrikePreview.Text = (target is null ? "" : Loc.Format("Target country: {0}\nTarget region: {1}\nOrder cost: {2:0} political credit",
            target.DisplayName, region?.DisplayName ?? Tr("No eligible target"),
            cities ? _campaign.Defines.NuclearDevastateCost : _campaign.Defines.NuclearStrikeCost) + "\n\n") +
            Tr(cities ? CitiesHelp : StrikeHelp) + "\n\n" + Tr("There is no taking it back.");
    }

    private void SubmitChosenNuclearStrike()
    {
        if (_campaign?.Player?.Id != _nuclearStrikeIssuer) { CloseNuclearStrikeReview(); return; }
        var target = ChosenNuclearCountry(); if (target is null) { RefreshChosenNuclearStrike(); return; }
        var action = _nuclearTargetKind.Selected == 1 ? "devastate" : "strike";
        if (NuclearPosture.Refusal(_campaign, _campaign.Player, action, target).Length > 0) { RefreshChosenNuclearStrike(); return; }
        var province = ChosenNuclearProvince(target);
        if (!_nuclearStrikeReviewed)
        {
            _nuclearStrikeReviewed = true; _nuclearStrikeReviewDay = _campaign.ElapsedDays; _nuclearStrikeReviewProvince = province;
            _nuclearStrikeSend.Text = Loc.Format("Give the order against {0}", target.DisplayName);
            _nuclearStrikeFeedback.Text = Tr("Check the named country and strike type. Confirming gives the order; Escape cancels."); return;
        }
        if (_nuclearStrikeReviewDay != _campaign.ElapsedDays || _nuclearStrikeReviewProvince != province)
        { RefreshChosenNuclearStrike(); _nuclearStrikeFeedback.Text = Tr("The world changed since your review. Review the current terms again before signing."); return; }
        var command = new NuclearPostureCommand { IssuerId = _nuclearStrikeIssuer, Action = action, TargetId = target.Id };
        _campaign.Commands.Enqueue(command); _campaign.Commands.ApplyAll(_campaign);
        if (_networkConnected && !_networkIsHost)
        { _nuclearStrikeSend.Disabled = true; _nuclearStrikeFeedback.Text = Tr("Order sent to the host. Awaiting confirmation."); return; }
        if (!command.Changed) { RefreshChosenNuclearStrike(); return; }
        CloseNuclearStrikeReview(); RefreshDefconOverview(); Refresh();
    }
}
