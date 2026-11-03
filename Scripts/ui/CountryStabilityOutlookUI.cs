using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // Только память интерфейса этой игровой сцены. Раскрытие расчёта не является
    // политическим решением и не должно попадать в сейв, командную ленту или RNG.
    private readonly System.Collections.Generic.HashSet<int> _openCountryOutlookCalculations = new();

    private void AddCountryStabilityOutlook(Nation nation)
    {
        // The dossier's existing intelligence boundary applies to this explanation too.
        if (nation != _campaign.Player && !CanInspectNation(nation.Id)) return;
        var outlook = StabilityOutlook.Read(_campaign, nation);
        if (outlook is null) return;
        var card = new PanelContainer { Name = "CountryStabilityOutlook", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var style = WarInsetStyle(10, 9);
        style.BorderWidthLeft = 2;
        style.BorderColor = Ui.Colour(outlook.Tone);
        card.AddThemeStyleboxOverride("panel", style);
        AtlasSurfaceSkin.Attach(card, AtlasSurfaceSkin.Level.Inset, "war.card");
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 5); card.AddChild(rows);

        Label Text(string value, int size, string tone, string name)
        {
            var label = Title(value, size, Ui.Colour(tone)); label.Name = name;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            return label;
        }
        rows.AddChild(Text(Tr("Domestic outlook"), 14, "text.caption", "OutlookHeading"));
        rows.AddChild(Text(Tr(outlook.Direction), 17, outlook.Tone, "OutlookDirection"));
        var figures = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        figures.AddThemeConstantOverride("separation", 12); rows.AddChild(figures);
        void Figure(string caption, float amount, string name)
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1 };
            column.AddThemeConstantOverride("separation", 1); figures.AddChild(column);
            column.AddChild(Text(Tr(caption), 14, "text.caption", name + "Caption"));
            var value = Text(Fmt.Percent(amount), 22, "text.bright", name);
            value.AddThemeFontOverride("font", Ui.NumberFont); column.AddChild(value);
        }
        Figure("Current stability", outlook.Current, "OutlookCurrent");
        Figure("Under current conditions", outlook.Target, "OutlookTarget");
        // The meter has its own row. It can never paint through a caption or modifier name.
        var meter = BattleGauge(outlook.Current, outlook.Tone, "OutlookMeter");
        meter.CustomMinimumSize = new Vector2(0, 4); rows.AddChild(meter);
        var explanation = Loc.Get("Conditions, not a prediction: stability moves gradually towards this level at economic reviews. War, living standards and government can change it.");
        if (outlook.MainDrag is { } drag)
            rows.AddChild(Text(Loc.Format("Main drag: {0}", Loc.Get(drag.Source)), 14, "text.body_soft", "OutlookMainDrag"));
        card.TooltipText = Ui.Tip(explanation + "\n\n" + string.Join("\n", outlook.Drivers.Entries.Select(entry =>
            Loc.Format("{0}: {1:+0.0;-0.0;0.0} pp", Loc.Get(entry.Source), entry.Value * 100f))));
        AddOutlookCalculation(rows, nation, outlook, explanation);
        _countryCardRows.AddChild(card);
    }

    /// <summary>A keyboard-reachable explanation of the same model as the hover tooltip.
    /// No simulated field is changed, and unknown foreign figures never reach this method.</summary>
    private void AddOutlookCalculation(VBoxContainer rows, Nation nation, StabilityOutlook outlook, string explanation)
    {
        var details = new VBoxContainer { Name = "OutlookCalculation", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        details.AddThemeConstantOverride("separation", 5);
        Button toggle = null;
        void SetOpen(bool open)
        {
            details.Visible = open;
            if (open) _openCountryOutlookCalculations.Add(nation.Id);
            else _openCountryOutlookCalculations.Remove(nation.Id);
            var key = open ? "Hide calculation" : "Show calculation";
            toggle.Text = Tr(key);
            // Dynamic captions still participate in the ordinary language refresh.
            toggle.SetMeta(LocSourceMeta, key); toggle.SetMeta(LocAppliedMeta, toggle.Text);
            StyleButton(toggle, open);
        }
        toggle = ActionButton("Show calculation", () => SetOpen(!details.Visible), 0, 28);
        toggle.Name = "OutlookCalculationToggle";
        toggle.TooltipText = Tr("Read the stability calculation without hovering. This does not change policy.");
        rows.AddChild(toggle); rows.AddChild(details);
        SetOpen(_openCountryOutlookCalculations.Contains(nation.Id));

        var heading = Title(Tr("How the target is calculated"), 14, Ui.Colour("text.accent"));
        heading.SizeFlagsHorizontal = SizeFlags.ExpandFill; details.AddChild(heading);
        foreach (var entry in outlook.Drivers.Entries)
        {
            var row = new HBoxContainer { Name = "OutlookContribution", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 10);
            var caption = Title(Loc.Get(entry.Source), 14, Ui.Colour("text.body_soft"));
            caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            // The amount keeps its natural width; long translated causes wrap, not clip.
            var amount = Title(Loc.Format("{0:+0.0;-0.0;0.0} pp", entry.Value * 100f), 14,
                Ui.Colour(entry.Value < 0 ? "state.negative" : "state.positive"));
            amount.AddThemeFontOverride("font", Ui.NumberFont);
            amount.AutowrapMode = TextServer.AutowrapMode.Off;
            amount.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            row.AddChild(caption); row.AddChild(amount); details.AddChild(row);
        }
        var total = Title(Loc.Format("Target under current conditions: {0}", Fmt.Percent(outlook.Target)), 14, Ui.Colour("text.bright"));
        total.Name = "OutlookCalculationTotal"; total.SizeFlagsHorizontal = SizeFlags.ExpandFill; details.AddChild(total);
        var note = Title(explanation, 14, Ui.Colour("text.caption"));
        note.Name = "OutlookCalculationNote"; note.SizeFlagsHorizontal = SizeFlags.ExpandFill; details.AddChild(note);
    }
}
