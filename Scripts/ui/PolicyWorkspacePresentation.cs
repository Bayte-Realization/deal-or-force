using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Label _policyReformPrice, _policyReformStatus;
    private Button _policyCancelReform, _policyReformEnact;
    private ulong _policyReformNextCheck;

    private void BuildPolicyReformBar(VBoxContainer rows)
    {
        _policyReformBar = new PanelContainer { Name = "PolicyReformBar" };
        _policyReformBar.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.raised"));
        rows.AddChild(_policyReformBar);
        var line = new HBoxContainer(); line.AddThemeConstantOverride("separation", Ui.Gap(16)); _policyReformBar.AddChild(line);
        _policyReformTerms = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _policyReformTerms.AddThemeConstantOverride("separation", 4); line.AddChild(_policyReformTerms);
        _policyReformPrice = Title("", 14, Ui.Colour("text.bright")); _policyReformPrice.Name = "PolicyReformPrice";
        _policyReformPrice.AddThemeFontOverride("font", Ui.NumberFont); _policyReformTerms.AddChild(_policyReformPrice);
        _policyReformStatus = Title("", 12, Ui.Colour("text.caption")); _policyReformStatus.Name = "PolicyReformStatus";
        _policyReformTerms.AddChild(_policyReformStatus);
        _policyReformActions = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _policyReformActions.AddThemeConstantOverride("separation", 8); line.AddChild(_policyReformActions);
        _policyCancelReform = ActionButton("Cancel", () => { _policyConfirmation = ""; RefreshPolicyReformBar(true); _policyEnact.GrabFocus(); }, 0, 38);
        _policyCancelReform.Name = "PolicyCancelReform"; _policyReformActions.AddChild(_policyCancelReform);
        _policyEnact = ActionButton("Enact law", EnactWorkspacePolicy, 180, 38, true,
            "res://game/gfx/interface/icons/icon_laws.png");
        _policyEnact.Icon = null; // One clear, centred primary caption; no shrinking icon beside a long confirmation.
        _policyReformEnact = _policyEnact;
        _policyEnact.Name = "PolicyEnact"; _policyReformActions.AddChild(_policyEnact);
    }

    private void RefreshPolicyReformBar(bool force = false)
    {
        if (_policyReformBar is null || _campaign?.Player is not { } nation) return;
        var now = Time.GetTicksMsec();
        if (!force && now < _policyReformNextCheck) return;
        _policyReformNextCheck = now + 150;
        _policyReformBar.Visible = _policyTab is not ("Course" or "Crises" or "Doctrines");
        if (!_policyReformBar.Visible) return;
        _policyEnact = _policyReformEnact; // Crisis/legacy doctrine pages own their separate actions.
        var policy = PoliticalPolicyCatalog.Find(_policySelected, PolicyIsAgenda);
        var exists = policy is not null;
        _policyReformActions.Visible = exists;
        if (!exists) { _policyReformPrice.Text = Tr("No policy selected"); _policyReformStatus.Text = Tr("Choose another section or clear your search."); return; }
        var may = PoliticalPolicies.CanAdopt(_campaign, nation, policy, _lawTarget, _lawRegion, PolicyIsAgenda, out var refusal);
        var confirming = _policyConfirmation == policy.Id;
        var ready = nation.PolicyReadyOnDay.GetValueOrDefault(PolicyIsAgenda ? "agenda" : PoliticalPolicies.Slot(policy, _lawTarget, _lawRegion));
        var target = PolicyScope switch
        {
            "region" => _campaign.StatesOf(nation).FirstOrDefault(s => s.MapIndex == _lawRegion)?.DisplayName ?? Tr("No governed regions"),
            "foreign" => _campaign.NationByTag(_lawTarget)?.DisplayName ?? "—",
            _ => nation.DisplayName
        };
        _policyReformPrice.Text = Loc.Format("Reform: {0} · Cooling-off: {1} days", Fmt.Money(nation.Gdp * policy.CostShare), policy.CooldownDays);
        _policyReformPrice.TooltipText = Ui.Tip(Loc.Format("Treasury after reform: {0}", Fmt.Money(nation.Treasury - nation.Gdp * policy.CostShare)));
        var state = !may ? Tr(refusal) : confirming ? Tr("Confirm payment and adoption") : Tr("Ready for consideration");
        if (ready > _campaign.ElapsedDays) state += " · " + Loc.Format("Next reform in {0} days.", ready - _campaign.ElapsedDays);
        _policyReformStatus.Text = target + " · " + state;
        _policyReformStatus.AddThemeColorOverride("font_color", Ui.Colour(!may || confirming ? "state.warning" : "text.body_soft"));
        _policyCancelReform.Visible = confirming;
        _policyEnact.Text = Tr(confirming ? "Confirm reform" : PolicyIsAgenda ? "Announce promise" : "Enact law");
        _policyEnact.Disabled = !may;
        _policyEnact.TooltipText = Ui.Tip(may ? Tr("Review the reform, then click again to confirm payment and adoption.") : Tr(refusal));
    }

    private void BuildPolicyComparison(Nation nation, PoliticalPolicy current, PoliticalPolicy candidate)
    {
        SectionRule(_policyPreview, PolicyIsAgenda ? "EFFECTS" : "IMPACT VS CURRENT LAW");
        if (!PolicyIsAgenda)
        {
            PolicyText(_policyPreview, Loc.Format("In force: {0}", Tr(current?.Name ?? "Neutral institutional baseline")), "text.caption");
            PolicyText(_policyPreview, "Law contributions, not national totals. The change replaces the current law in this category.", "text.caption");
        }
        var table = new GridContainer { Name = "PolicyEffectTable", Columns = PolicyIsAgenda ? 2 : 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        table.AddThemeConstantOverride("h_separation", 12); table.AddThemeConstantOverride("v_separation", 10); _policyPreview.AddChild(table);
        PolicyComparisonCell(table, "Effect", false, "text.caption");
        if (!PolicyIsAgenda) PolicyComparisonCell(table, "In force", true, "text.caption");
        PolicyComparisonCell(table, "Selected", true, "text.caption");
        if (!PolicyIsAgenda) PolicyComparisonCell(table, "Change", true, "text.caption");
        var keys = new HashSet<string>(candidate.Modifiers.Keys, StringComparer.OrdinalIgnoreCase);
        if (!PolicyIsAgenda && current is not null) keys.UnionWith(current.Modifiers.Keys);
        foreach (var key in keys.OrderBy(k => Tr(NationalModifiers.Of(k)?.Name ?? k), StringComparer.CurrentCulture))
        {
            var definition = NationalModifiers.Of(key);
            var before = current?.Modifiers.GetValueOrDefault(key) ?? 0f;
            var after = candidate.Modifiers.GetValueOrDefault(key);
            ComparisonRow(Tr(definition?.Name ?? key), before, after, definition?.HigherIsBetter ?? true,
                Tr(definition?.Help ?? ""));
        }
        if (candidate.ForeignTrust != 0 || (current?.ForeignTrust ?? 0) != 0)
            ComparisonRow(Tr("Sustainable alliance trust"), current?.ForeignTrust ?? 0, candidate.ForeignTrust, true, "");
        if (keys.Count == 0 && candidate.ForeignTrust == 0 && (current?.ForeignTrust ?? 0) == 0)
            PolicyText(_policyPreview, "Neutral institutional baseline");
        if (candidate.RequiresWar) PolicyText(_policyPreview, "Requirement: an active war.", "state.warning");

        void ComparisonRow(string label, float before, float after, bool higherIsBetter, string tooltip)
        {
            var name = PolicyComparisonCell(table, label, false); name.TooltipText = Ui.Tip(tooltip);
            if (!PolicyIsAgenda) PolicyComparisonCell(table, PolicyPercentage(before), true, "text.muted");
            PolicyComparisonCell(table, PolicyPercentage(after), true);
            if (PolicyIsAgenda) return;
            var delta = after - before;
            var token = MathF.Abs(delta) < .0001f ? "text.caption" : (delta > 0) == higherIsBetter ? "state.positive" : "state.warning";
            PolicyComparisonCell(table, MathF.Abs(delta) < .0001f ? "—" : PolicyPercentage(delta, true), true, token);
        }
    }

    private Label PolicyComparisonCell(GridContainer table, string text, bool number, string token = "text.body")
    {
        var cell = Title(text, 12, Ui.Colour(token));
        cell.SizeFlagsHorizontal = number ? SizeFlags.Fill : SizeFlags.ExpandFill;
        if (number)
        {
            cell.CustomMinimumSize = new Vector2(66, 0); cell.HorizontalAlignment = HorizontalAlignment.Right;
            cell.AddThemeFontOverride("font", Ui.NumberFont);
        }
        table.AddChild(cell); return cell;
    }

    private static string PolicyPercentage(float value, bool signed = false) =>
        (value * 100f).ToString(signed ? "+0.##;-0.##;0" : "0.##", CultureInfo.CurrentCulture) + "%";

    // The game uses a 1080-line design canvas. At 720p a twelve-unit caption otherwise
    // becomes eight physical pixels. Reflow this document instead of shrinking its text;
    // scroll areas absorb the longer lists. Only run on rebuild or a real window resize.
    private void FitPolicyTypography()
    {
        if (_policyPanel is null) return;
        _policyPixelHeight = GetWindow().Size.Y;
        _policyPixelWidth = GetWindow().Size.X;
        var compact = _policyPixelHeight < 900 || _policyPixelWidth < 1600;
        _policyCategoryColumn.CustomMinimumSize = new Vector2(compact ? 250 : 180, 0);
        foreach (var node in _policyPanel.FindChildren("*", "Control", true, false))
        {
            if (node is not Control control || control is not (Label or Button or LineEdit)) continue;
            if (!control.HasMeta("policy_base_font")) control.SetMeta("policy_base_font", control.GetThemeFontSize("font_size"));
            var font = control.GetMeta("policy_base_font").AsInt32();
            control.AddThemeFontSizeOverride("font_size", compact && font <= 15 ? Ui.TypeStep((int)MathF.Ceiling(font * 1.5f)) : font);
            if (control is Button button)
            {
                if (!button.HasMeta("policy_base_height")) button.SetMeta("policy_base_height", button.CustomMinimumSize.Y);
                var height = button.GetMeta("policy_base_height").AsSingle();
                button.CustomMinimumSize = new Vector2(button.CustomMinimumSize.X, compact ? height >= 76 ? 110 : MathF.Max(height, 44) : height);
                if (button == _policyReformEnact) button.CustomMinimumSize = new Vector2(compact ? 280 : 180, button.CustomMinimumSize.Y);
            }
            if (control is Label label && label.GetParent() is GridContainer)
            {
                if (!label.HasMeta("policy_base_width")) label.SetMeta("policy_base_width", label.CustomMinimumSize.X);
                var width = label.GetMeta("policy_base_width").AsSingle();
                label.CustomMinimumSize = new Vector2(compact && width > 0 ? 90 : width, label.CustomMinimumSize.Y);
            }
        }
    }
}
