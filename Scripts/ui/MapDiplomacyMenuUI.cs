using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>When false, a left click on the map selects the country but does not open the country/region panels or the floating diplomacy menu.</summary>
    private bool _mapLeftClickInteractionEnabled = false;
    /// <summary>Floating inter-country action menu shown at the cursor after a map click on a country.</summary>
    private Control _mapDiplomacyMenuLayer = null!;
    private PanelContainer _mapDiplomacyMenu = null!;
    private Label _mapDiplomacyFeedback = null!;

    /// <summary>Adds one diplomacy action button to the floating map menu.</summary>
    private void AddMapDiplomacyAction(VBoxContainer rows, string caption, string iconRole, Action action,
        string tooltip, bool primary = false)
    {
        var button = ActionButton(caption, action, 0, 34, primary);
        button.TooltipText = Ui.Tip(Tr(tooltip));
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rows.AddChild(button);
    }

    /// <summary>Closes the floating map diplomacy menu if it is open.</summary>
    private void CloseMapDiplomacyMenu()
    {
        if (_mapDiplomacyMenuLayer is not null && IsInstanceValid(_mapDiplomacyMenuLayer))
            _mapDiplomacyMenuLayer.QueueFree();
        _mapDiplomacyMenuLayer = null;
        _mapDiplomacyMenu = null;
        _mapDiplomacyFeedback = null;
    }

    /// <summary>Floating inter-country action panel at screen position <c>at</c>, offering the
    /// diplomacy actions for <c>target</c> directly from the campaign map.</summary>
    private void ShowMapDiplomacyMenu(Nation target, Vector2 at)
    {
        CloseMapDiplomacyMenu();
        var player = _campaign?.Player;
        if (player is null) return;

        // The diplomacy screen's action methods key off _diplomacySelectedId; point it at the
        // country under the cursor so the same code paths serve map and screen.
        _diplomacySelectedId = target.Id;

        // A full-rect layer that swallows clicks outside the menu and dismisses it.
        _mapDiplomacyMenuLayer = new Control { Name = "MapDiplomacyMenuLayer", MouseFilter = MouseFilterEnum.Stop };
        _mapDiplomacyMenuLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _mapDiplomacyMenuLayer.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true }) CloseMapDiplomacyMenu();
            else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) CloseMapDiplomacyMenu();
        };
        _hudLayer.AddChild(_mapDiplomacyMenuLayer);

        _mapDiplomacyMenu = TexturedPanel(_mapDiplomacyMenuLayer, PanelSurface.Command);
        _mapDiplomacyMenu.Name = "MapDiplomacyMenu";
        _mapDiplomacyMenu.SetAnchorsPreset(LayoutPreset.TopLeft);

        var rows = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) };
        rows.AddThemeConstantOverride("separation", 4);
        var margin = Margin(_mapDiplomacyMenu, 8, 6);
        margin.AddChild(rows);

        var title = Title(Tr("Actions for {0}", target.DisplayName), 13, Ui.Colour("text.bright"));
        title.CustomMinimumSize = new Vector2(200, 0);
        title.ClipText = true;
        rows.AddChild(title);

        var relation = CampaignDiplomacy.Get(_campaign, player.Id, target.Id);
        var atWar = relation is { Stance: DiplomaticStance.AtWar };

        // --- Influence ---
        AddMapDiplomacyAction(rows, Tr("Improve relations"), "INFLUENCE", ImproveDiplomacyRelations,
            Tr("Offer a diplomatic overture to raise opinion."));
        AddMapDiplomacyAction(rows, Tr("Denounce"), "INFLUENCE", DenounceDiplomacyTarget,
            Tr("Publicly denounce them, lowering opinion and raising tension."));

        // --- Agreements ---
        AddMapDiplomacyAction(rows, Tr("Propose alliance"), "AGREEMENTS", ToggleDiplomacyAlliance,
            Tr("Propose a mutual defence pact."));
        AddMapDiplomacyAction(rows, Tr("Guarantee"), "AGREEMENTS", ToggleDiplomacyGuarantee,
            Tr("Guarantee their independence; an attack on them is an attack on us."));
        AddMapDiplomacyAction(rows, Tr("Trade agreement"), "AGREEMENTS",
            () => ToggleAgreement(AgreementKind.TradeAgreement, DiplomacyTarget(), _mapDiplomacyFeedback),
            Tr("Open a trade agreement."));
        AddMapDiplomacyAction(rows, Tr("Non-aggression pact"), "AGREEMENTS",
            () => ToggleAgreement(AgreementKind.NonAggressionPact, DiplomacyTarget(), _mapDiplomacyFeedback),
            Tr("Sign a non-aggression pact."));
        AddMapDiplomacyAction(rows, Tr("Send aid"), "AGREEMENTS", () => SendAidTo(DiplomacyTarget(), _mapDiplomacyFeedback),
            Tr("Send economic aid."));
        AddMapDiplomacyAction(rows, Tr("Embargo"), "AGREEMENTS", () => ToggleEmbargoOn(DiplomacyTarget(), _mapDiplomacyFeedback),
            Tr("Impose a trade embargo."));

        // --- War and peace ---
        if (atWar)
        {
            AddMapDiplomacyAction(rows, Tr("Negotiate peace"), "War and peace", () => OpenDiplomaticPeace(DiplomacyTarget()),
                Tr("Open the peace conference."), primary: true);
        }
        else
        {
            AddMapDiplomacyAction(rows, Tr("Declare war"), "War and peace", ConfirmOrDeclareDiplomacyWar,
                Tr("Begin an immediate land war."));
            AddMapDiplomacyAction(rows, Tr("Issue ultimatum"), "War and peace", ConfirmOrIssueUltimatum,
                Tr("Issue an ultimatum with demands."));
        }

        _mapDiplomacyFeedback = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _mapDiplomacyFeedback.AddThemeFontSizeOverride("font_size", 11);
        _mapDiplomacyFeedback.SetMeta(LocAppliedMeta, "");
        rows.AddChild(_mapDiplomacyFeedback);

        // Size and clamp to screen.
        var content = rows.GetCombinedMinimumSize();
        var screen = GetViewport().GetVisibleRect().Size;
        var width = Math.Max(220f, content.X + 4f);
        var height = Math.Min(content.Y + 12f, screen.Y * 0.6f);
        _mapDiplomacyMenu.Size = new Vector2(width, height);
        var x = Math.Clamp(at.X + 14f, 8f, MathF.Max(8f, screen.X - width - 8f));
        var y = Math.Clamp(at.Y + 10f, 8f, MathF.Max(8f, screen.Y - height - 8f));
        _mapDiplomacyMenu.Position = new Vector2(x, y);
    }
}
