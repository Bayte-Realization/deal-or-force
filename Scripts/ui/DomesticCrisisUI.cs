using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private int _crisisSelectedRegion = -1;
    private string _crisisResponseConfirmation = "";

    private void ShowDomesticCrisisWorkspace()
    {
        if (_campaign?.Player is not { } nation) return;
        ClearPolicyRows(_policyList); ClearPolicyRows(_policyOptions); ClearPolicyRows(_policyPreview);
        _policyEnact = null;
        var situation = DomesticCrises.Read(_campaign, nation);
        PolicyHeading(_policyList, "COUNTRY UNDER PRESSURE");
        if (!_campaign.FeatureEnabled("domestic_crises"))
            PolicyText(_policyList, "Institutional crises are disabled in this scenario.", "state.warning");
        CrisisGauge(_policyList, "Population awareness", situation.Awareness, 0f,
            "Education and civic networks accumulate independently of anger. Awareness is not a desire for violence.", "agenda");
        CrisisGauge(_policyList, "Radicalism", situation.Radicalism, 0f,
            "Dissatisfaction with existing institutions. High radicalism is different from peaceful civic participation.", "stability");
        CrisisGauge(_policyList, "Coup pressure", situation.CoupPressure, situation.CoupThreshold,
            "A coup requires sustained institutional pressure and an eligible political transition. Crossing a line for one day is not enough.", "government");
        PolicyStat(_policyList, "Critical pressure sustained", Loc.Format("{0} / {1} days",
            situation.DaysAboveThreshold, situation.RequiredDays), "history");
        if (situation.CooldownDays > 0)
            PolicyStat(_policyList, "Aftermath protection", Loc.Format("{0} days", situation.CooldownDays), "stability");
        PolicyHeading(_policyList, "CIVIL-WAR WARNING");
        var civilReason = CivilWarOutbreak.EligibilityReason(_campaign, nation);
        var civilChance = civilReason.Length == 0 ? CivilWarOutbreak.Chance(_campaign, nation) : 0f;
        PolicyStat(_policyList, "Weekly outbreak chance", civilChance.ToString("P2"), "stability");
        PolicyText(_policyList, "This is a weekly risk, not a countdown. Low stability, recent coups and defeats raise it. Armed opposition can take only connected governed territory; a foreign office does not grant land.", "text.caption");
        if (civilReason.Length > 0) PolicyText(_policyList, civilReason, "text.body_soft");
        else PolicyText(_policyList, "An outbreak is possible. Restore institutional stability before the next weekly review.", "state.warning");
        if (nation.CoupPlotDay >= 0 && MechanicFeatures.SystemEnabled(_campaign, nameof(Coups)))
        {
            PolicyStat(_policyList, "Military plot under way", Loc.Format("{0} / {1} days",
                Math.Max(0, _campaign.ElapsedDays - nation.CoupPlotDay), Math.Max(1, _campaign.Defines.CoupPlotDays)), "military");
            PolicyText(_policyList, "Officers are plotting. Raise stability before the plot ripens.");
        }
        PolicyText(_policyList, situation.CoupEligible ? "Political conditions permit a coup if the pressure persists."
            : "No eligible coup transition now. Pressure alone does not guarantee regime change.");
        PolicyText(_policyList, "These gauges show current conditions and actual thresholds, not a guaranteed date for an uprising.", "text.caption");
        PolicyHeading(_policyList, "PRESSURE DRIVERS");
        foreach (var driver in situation.Drivers) PolicyText(_policyList, driver);
        if (situation.Drivers.Length == 0) PolicyText(_policyList, "No institutional pressure drivers recorded.");

        var query = _policySearch.Text.Trim();
        var regions = situation.Regions.Where(row => query.Length == 0 ||
                row.Region.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.SecessionPressure).ThenByDescending(row => row.Radicalism)
            .ThenBy(row => row.Region.DisplayName, StringComparer.Ordinal).ToArray();
        var maximum = (int)Math.Clamp(Ui.Metric("crises.regions_shown", 40), 5, 150);
        PolicyHeading(_policyOptions, "REGIONAL PRESSURES");
        PolicyText(_policyOptions, "Regions are ordered by accumulated secession pressure. Select one to inspect the causes and locate it on the map.");
        if (regions.Length == 0) PolicyText(_policyOptions, "No matching governed regions. Clear the search.");
        if (!situation.Regions.Any(row => row.Region.MapIndex == _crisisSelectedRegion))
            _crisisSelectedRegion = regions.FirstOrDefault()?.Region.MapIndex ?? -1;
        foreach (var row in regions.Take(maximum))
        {
            var card = new PanelContainer { Name = "CrisisRegion_" + row.Region.MapIndex };
            card.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 8)); _policyOptions.AddChild(card);
            var content = new VBoxContainer(); content.AddThemeConstantOverride("separation", 5); card.AddChild(content);
            var button = ActionButton(row.Region.DisplayName, () =>
            { _crisisSelectedRegion = row.Region.MapIndex; _crisisResponseConfirmation = ""; ShowDomesticCrisisWorkspace(); },
                0, 32, row.Region.MapIndex == _crisisSelectedRegion,
                "res://game/gfx/interface/icons/icon_" + (row.IsCapital ? "nation" : "population") + ".png");
            button.ClipText = true; button.TooltipText = Ui.Tip(row.Region.DisplayName); content.AddChild(button);
            CrisisGauge(content, "Secession pressure", row.SecessionPressure, row.SecessionThreshold,
                "Accumulated separatist pressure is separate from radicalism and awareness. Independence still requires the simulation's political and territorial conditions.", "stability");
            PolicyText(content, Loc.Format("Awareness {0:P0} · radicalism {1:P0}{2}", row.Awareness,
                row.Radicalism, row.IsCapital ? " · " + Tr("Capital region") : ""), "text.body_soft");
        }
        if (regions.Length > maximum)
            PolicyText(_policyOptions, Loc.Format("Showing {0} of {1}. Search to inspect another region.", maximum, regions.Length));

        PolicyHeading(_policyPreview, "RESPOND, DO NOT GUESS");
        foreach (var action in new[] { "public_dialogue", "institutional_review" }) BuildCrisisResponse(action, nation);
        var response = nation.DomesticCrisis;
        if (response is { ResponseId.Length: > 0 } && response.ResponseUntilDay > _campaign.ElapsedDays)
            PolicyStat(_policyPreview, "Active response", Loc.Format("{0} · {1} days remaining",
                CrisisResponseName(response.ResponseId), response.ResponseUntilDay - _campaign.ElapsedDays), "agenda");
        var selected = situation.Regions.FirstOrDefault(row => row.Region.MapIndex == _crisisSelectedRegion);
        if (selected is not null)
        {
            PolicyHeading(_policyPreview, selected.Region.DisplayName);
            PolicyText(_policyPreview, RadicalismTooltip(selected.Region));
            _policyPreview.AddChild(ActionButton("Inspect region on map", () =>
            {
                ClosePolicyWorkspace(); _selectedState = selected.Region;
                var (latitude, longitude) = StateCentre(selected.Region);
                _globe.FocusOn(latitude, longitude, 6.8f);
                OpenScreen("Region"); ShowRegionOverview();
            }, 0, 34, iconPath: "res://game/gfx/interface/icons/icon_population.png"));
        }
        if (response is { LastOutcomeId.Length: > 0 })
            PolicyText(_policyPreview, Loc.Format("Last institutional outcome: {0} · campaign day {1}",
                Tr(response.LastOutcomeId), response.LastOutcomeDay), "text.caption");
    }

    private void CrisisGauge(VBoxContainer rows, string caption, float value, float threshold, string help, string icon)
    {
        PolicyStat(rows, caption, threshold > 0f ? Loc.Format("{0:P1} · threshold {1:P0}", value, threshold)
            : value.ToString("P1"), icon);
        var bar = new ProgressBar { Name = "CrisisGauge_" + caption.Replace(" ", "").ToLowerInvariant(), MinValue = 0,
            MaxValue = Math.Max(1f, Math.Max(value, threshold)), Value = value, ShowPercentage = false,
            CustomMinimumSize = new(0, 9), TooltipText = Ui.Tip(Tr(help)), MouseFilter = MouseFilterEnum.Stop };
        rows.AddChild(bar);
    }

    private static string CrisisResponseName(string id) => Loc.Get(id == "public_dialogue" ? "Public dialogue" : "Institutional review");

    private void BuildCrisisResponse(string action, Nation nation)
    {
        var reason = DomesticCrises.Refusal(_campaign, nation, action);
        var title = CrisisResponseName(action);
        PolicyStat(_policyPreview, title, Fmt.Money(DomesticCrises.Cost(_campaign, nation, action)), "agenda");
        PolicyText(_policyPreview, action == "public_dialogue"
            ? "A temporary dialogue programme. Pressure on the institutions falls while it runs."
            : "A temporary review of the institutions. Recovery is faster while it runs.");
        var button = ActionButton(_crisisResponseConfirmation == action ? "Confirm response" : "Review response", () =>
        {
            if (_crisisResponseConfirmation != action) { _crisisResponseConfirmation = action; ShowDomesticCrisisWorkspace(); return; }
            _campaign.Commands.Enqueue(new StartCrisisResponseCommand { IssuerId = nation.Id, ActionId = action });
            _campaign.Commands.ApplyAll(_campaign); _crisisResponseConfirmation = "";
            ShowDomesticCrisisWorkspace(); Refresh();
        }, 0, 32, _crisisResponseConfirmation == action,
            "res://game/gfx/interface/icons/icon_agenda.png");
        button.Name = "CrisisResponse_" + action; button.Disabled = reason.Length > 0;
        button.TooltipText = Ui.Tip(reason.Length > 0 ? reason : Loc.Format("Duration: {0} days · cooldown: {1} days. Payment at the start.",
            _campaign.Defines.CrisisResponseDurationDays, _campaign.Defines.CrisisResponseCooldownDays));
        _policyPreview.AddChild(button);
        if (reason.Length > 0) PolicyText(_policyPreview, reason, "state.warning");
    }
}
