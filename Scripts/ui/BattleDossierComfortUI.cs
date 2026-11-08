using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _battleActions;
    private bool _battleRosterExpanded = true;
    private int _battleLayoutRevision;
    private int? _battleRosterScrollToRestore;

    private float BattlePixelScale()
    {
        var scale = (GetViewport().GetStretchTransform() * _hudLayer.GetGlobalTransformWithCanvas()).Scale.Abs();
        return MathF.Max(.01f, MathF.Min(scale.X, scale.Y));
    }

    private void FitBattleReading()
    {
        var scale = BattlePixelScale();
        var preference = Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f);
        var floor = Ui.Metric("combat.minimum_font_pixels", 14);
        foreach (var root in new Control[] { _battleDetailControls, _battleActions })
        {
            if (root is null) continue;
            foreach (var control in root.FindChildren("*", "Control", true, false).OfType<Control>())
            {
                if (control is not (Label or Button)) continue;
                var authored = control is Label { LabelSettings: not null } label
                    ? label.LabelSettings.FontSize : control.GetThemeFontSize("font_size");
                var size = DecisionReadability.FontSize(authored, scale, preference, floor);
                if (control is Label { LabelSettings: not null } paragraph)
                {
                    paragraph.LabelSettings = (LabelSettings)paragraph.LabelSettings.Duplicate();
                    paragraph.LabelSettings.FontSize = size;
                }
                else control.AddThemeFontSizeOverride("font_size", size);
                if (control is Button)
                    control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X,
                        DecisionReadability.TargetHeight(control.CustomMinimumSize.Y, scale, preference,
                            Ui.Metric("combat.minimum_target_pixels", 32)));
            }
        }
        SettleBattleDossierLayout(++_battleLayoutRevision);
    }

    private async void SettleBattleDossierLayout(int revision)
    {
        // A flow row first reports its minimum height before it receives its new
        // width. Font floors and folding replace that row, so a PanelContainer can
        // temporarily grow past its anchored bottom. Reapply the authored bounds
        // after the nested containers have sorted, not just before adding content.
        for (var frame = 0; frame < 4; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (revision != _battleLayoutRevision || !IsInsideTree() ||
                _battleDetailControls is not { Visible: true } || !_sidePanel.Visible) return;
            LayoutCampaignDossier();
        }
        if (_battleRosterScrollToRestore is not { } scroll) return;
        _battleRosterScrollToRestore = null;
        _dossierScroll.ScrollVertical = scroll;
        (_battleActions?.FindChild("BattleRosterToggle", true, false) as Button)?.GrabFocus();
    }

    private string WithdrawalTip(BattleWithdrawalQuote quote)
    {
        if (!quote.Available) return Tr(quote.Refusal);
        var summary = Loc.Format("Withdraw {0:0} division(s); {1:0} remain in combat.", quote.Leaving, quote.Remaining);
        if (quote.Attacking) return Tr("Stop this attack without moving or healing the formation.") + "\n" + summary;
        return summary + "\n" + Loc.Format("Retreat destination: {0}.", BattlePlaceName(quote.Destination)) +
            "\n" + Tr("Retreat costs recovery time; it does not restore men or equipment.");
    }

    private void ToggleBattleRoster()
    {
        _battleRosterExpanded = !_battleRosterExpanded;
        _battleRosterScrollToRestore = _dossierScroll.ScrollVertical;
        _battleDossierFingerprint = int.MinValue;
        RefreshBattleDossier();
    }
}

