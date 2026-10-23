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
        if (_campaign.FeatureEnabled("political_cohorts"))
        {
            var mood = PoliticalCohorts.Mood(_campaign, nation);
            if (!mood.Reviewed)
                PolicyText(_policyList, "Political opinion has not been reviewed yet. It is read with the country, once a day.");
            else
            {
                CrisisGauge(_policyList, "Law approval", mood.Law, 0f,
                    "Share of the region that accepts the laws it lives under. It moves with living standards and shortages, not with a button.", "agenda");
                CrisisGauge(_policyList, "Government approval", mood.Government, 0f,
                    "Share of the region that accepts the government over it. A low figure with a still-orderly state is a scandal. A low figure with a failing state feeds coups and breaks.", "government");
                CrisisGauge(_policyList, "Political divergence", mood.Divergence, _campaign.Defines.CenterRegionIdeologySplit,
                    "How far this region's politics have moved from the centre. High divergence is a state with its own mind. It is not a foreign people, and it does not by itself draw a border.", "stability");
            }
        }
        CrisisGauge(_policyList, "Coup pressure", situation.CoupPressure, situation.CoupThreshold,
            "A coup requires sustained institutional pressure and an eligible political transition. Crossing a line for one day is not enough.", "government");
        PolicyStat(_policyList, "Critical pressure sustained", Loc.Format("{0} / {1} days",
            situation.DaysAboveThreshold, situation.RequiredDays), "history");
        if (situation.CooldownDays > 0)
            PolicyStat(_policyList, "Aftermath protection", Loc.Format("{0} days", situation.CooldownDays), "stability");
        PoliticalMoodBlock(_policyList, nation);
        PolicyHeading(_policyList, "CIVIL-WAR WARNING");
        var civilReason = CivilWarOutbreak.EligibilityReason(_campaign, nation);
        var civilChance = civilReason.Length == 0 ? CivilWarOutbreak.Chance(_campaign, nation) : 0f;
        PolicyStat(_policyList, "Weekly outbreak chance", civilChance.ToString("P2"), "stability");
        if (civilReason.Length == 0 && civilChance > 0f)
            foreach (var factor in CivilWarOutbreak.Factors(_campaign, nation))
                PolicyText(_policyList, factor, "text.body");
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
            .OrderByDescending(row => PoliticalCohorts.UnrestHeat(row.Region)).ThenByDescending(row => row.SecessionPressure)
            .ThenBy(row => row.Region.DisplayName, StringComparer.Ordinal).ToArray();
        var maximum = (int)Math.Clamp(Ui.Metric("crises.regions_shown", 40), 5, 150);
        // The people come before the provinces: who lives here, and which way each group leans,
        // is the summary the regional detail below is evidence for.
        DemographyMoodBlock(_policyOptions, nation);
        PolicyHeading(_policyOptions, "REGIONAL PRESSURES");
        PolicyText(_policyOptions, "Regions are ordered by how hot they are on the unrest map: radicalism, the pull to leave, and a government the region has stopped accepting. Select one to inspect the causes and locate it on the map.");
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
            if (_campaign.FeatureEnabled("political_cohorts") && row.Region.PoliticalDivergence >= 0f)
            {
                PolicyText(content, Loc.Format("Laws {0:P0} · government {1:P0} · divergence {2:P0}",
                    row.Region.LawApproval, row.Region.GovernmentApproval, row.Region.PoliticalDivergence), "text.body_soft");
                var (cause, _) = PoliticalCohorts.CauseOf(_campaign, nation, row.Region);
                if (cause.Length > 0) PolicyText(content, cause, "text.caption");
            }
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
            var runningHere = nation.DomesticCrisis is { ConciliationUntilDay: >= 0 } paid
                && paid.ConciliationUntilDay > _campaign.ElapsedDays && paid.ConciliationRegion == selected.Region.MapIndex;
            if (runningHere || PoliticalCohorts.UnrestHeat(selected.Region) >= _campaign.Defines.CrisisConciliationHeat
                || selected.Region.SecessionPressure >= 0.12f)
            {
                var refusal = DomesticCrises.ConciliationRefusal(_campaign, nation, selected.Region);
                PolicyStat(_policyPreview, "Settle this region",
                    Fmt.Money(DomesticCrises.ConciliationCost(_campaign, nation, selected.Region)), "agenda");
                PolicyText(_policyPreview, "Money for this region alone. While it lasts, the pull to leave, the anger and the political distance cool. The border stays where it is.");
                var settle = ActionButton(_crisisResponseConfirmation == "settle" ? "Confirm settlement" : "Settle this region", () =>
                {
                    if (_crisisResponseConfirmation != "settle") { _crisisResponseConfirmation = "settle"; ShowDomesticCrisisWorkspace(); return; }
                    _campaign.Commands.Enqueue(new ConciliateRegionCommand { IssuerId = nation.Id, StateMapIndex = selected.Region.MapIndex });
                    _campaign.Commands.ApplyAll(_campaign);
                    _crisisResponseConfirmation = "";
                    ShowDomesticCrisisWorkspace();
                    Refresh();
                }, 0, 32, _crisisResponseConfirmation == "settle", "res://game/gfx/interface/icons/icon_laws.png");
                settle.Name = "ConciliateRegion";
                settle.Disabled = refusal.Length > 0;
                settle.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal : Loc.Format("{0} days. One region at a time.", _campaign.Defines.CrisisConciliationDays));
                _policyPreview.AddChild(settle);
                if (refusal.Length > 0) PolicyText(_policyPreview, refusal, "state.warning");
            }
        }
        if (response is { LastOutcomeId.Length: > 0 })
            PolicyText(_policyPreview, Loc.Format("Last institutional outcome: {0} · campaign day {1}",
                Tr(response.LastOutcomeId), response.LastOutcomeDay), "text.caption");
    }

    /// <summary>
    /// What the country actually believes, rather than what its government does. The shares come
    /// from <see cref="PoliticalMood"/>: the residents' own position on the same two axes the
    /// ideologies are placed on, read from what they have and what is being done to them. A
    /// government below the plurality line holds office without a mandate, and that is where the
    /// unrest in the numbers above comes from.
    /// </summary>
    private void PoliticalMoodBlock(VBoxContainer rows, Nation nation)
    {
        var shares = PoliticalMood.Shares(_campaign, nation);
        if (shares.Count == 0) return;
        PolicyHeading(rows, "POLITICAL MOOD");
        var threshold = MathF.Max(0f, _campaign.Defines.PoliticalMoodMandateMultiple) /
            MathF.Max(1f, IdeologyCatalog.All.Count);
        CrisisGauge(rows, "Governing mandate", PoliticalMood.Mandate(_campaign, nation), threshold,
            "The share of the country that leans towards the government's own ideology. Below the plurality line, holding office breeds unrest every week.", "government");
        PoliticalMoodShare runnerUp = null;
        for (var index = 0; index < shares.Count && index < 4; index++)
        {
            var share = shares[index];
            PolicyStat(rows, Tr(share.Name), share.Share.ToString("P1"), share.Governing ? "government" : "agenda");
            if (runnerUp is null && !share.Governing) runnerUp = share;
        }
        if (runnerUp is not null)
            PolicyText(rows, Loc.Format("The largest other current is {0} at {1:P1}.", Tr(runnerUp.Name), runnerUp.Share), "text.caption");
    }

    /// <summary>
    /// The country's people as the data actually keeps them - culture and religion - with the
    /// current each group leans towards. Class and profession are not modelled by this game, so
    /// they are not shown here: a chart of a number nobody keeps is worse than no chart. What is
    /// shown is real: each group leans the way its own regions do.
    /// </summary>
    private void DemographyMoodBlock(VBoxContainer rows, Nation nation)
    {
        var groups = PoliticalMood.GroupLeans(_campaign, nation);
        if (groups.Count == 0) return;
        PolicyHeading(rows, "DEMOGRAPHY AND MOOD");
        foreach (var group in groups)
        {
            var name = string.IsNullOrEmpty(group.ReligionName) || group.ReligionName == group.CultureName
                ? Tr(group.CultureName)
                : Loc.Format("{0} · {1}", Tr(group.CultureName), Tr(group.ReligionName));
            PolicyStat(rows, name, Loc.Format("{0:P0} → {1}", group.Share, Tr(group.IdeologyName)),
                group.Governing ? "government" : "agenda");
        }
        PolicyText(rows, "Each group leans the way its own regions do, not the way its name does.", "text.caption");
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
