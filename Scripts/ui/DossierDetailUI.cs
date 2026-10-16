using System;
using System.Globalization;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>Empty the structured rows before a screen writes its own.</summary>
    private void ClearDetail()
    {
        _detailGroup = "";
        if (_sideDetail is null) return;
        foreach (var child in _sideDetail.GetChildren())
        {
            _sideDetail.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>
    /// The proportion a formatted figure represents, or -1 when it is not one.
    ///
    /// Only a percentage counts, and only one between nought and a hundred: a war score of
    /// -40 % and an inflation of 300 % are both real figures and neither is a proportion of
    /// anything, so drawing them as a full or empty bar would be inventing information. The
    /// separator is read either way round, because the game formats numbers in the player's
    /// culture and Russian writes 74,5 where English writes 74.5.
    /// </summary>
    private static float ShareOf(string value)
    {
        if (string.IsNullOrEmpty(value)) return -1f;
        var text = value.Trim();
        if (!text.EndsWith("%", StringComparison.Ordinal)) return -1f;
        text = text[..^1].Trim().Replace(',', '.').Replace("\u00a0", "").Replace(" ", "");
        if (!float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percent)) return -1f;
        if (percent < 0f || percent > 100f) return -1f;
        return percent / 100f;
    }

    /// <summary>
    /// Digits of equal width, so a column of money or people stops shifting sideways as its
    /// numbers change. The figure beside it is already right-aligned; this lines up the ones
    /// place as well, which is the difference between a column you can scan and one you re-read
    /// every time it refreshes. A font without tabular figures simply looks as it did, and a
    /// mod can turn the whole thing off with `hud.tabular_numbers`.
    /// </summary>
    private static void TabularFigures(Label label)
    {
        if (label is null || InterfaceSkin.Metric("hud.tabular_numbers", 1) <= 0) return;
        label.AddThemeFontOverride("font", PresentationFonts.Tabular());
    }

    private void DetailRow(string caption, string value, string tooltip = null, string valueToken = "text.bright",
        Func<Simulation.Breakdown> explain = null)
    {
        // Every screen in the game builds its rows here, so a breakdown offered here is a
        // breakdown offered everywhere: the figure on the right, and what it is made of
        // under the cursor.
        var row = new ExplainedRow
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Explain = explain,
            Plain = tooltip ?? "",
            // A figure printed as a percentage is a share, and a share can be drawn. Read
            // off the text rather than passed in by eighty-nine call sites, because the
            // formatter is the same one everywhere and this is what its output means.
            Share = ShareOf(value),
            Fill = Ui.Colour(valueToken)
        };
        row.QueueRedraw();
        if (!string.IsNullOrEmpty(tooltip) || explain is not null)
        {
            row.TooltipText = Ui.Tip(tooltip ?? caption);
            row.MouseFilter = Control.MouseFilterEnum.Stop;
        }

        row.CustomMinimumSize = new Vector2(0, 30);
        row.AddThemeConstantOverride("separation", 14);
        // The same mark for the same idea wherever it appears. Reusing one symbol across
        // the top bar, the alert rail and every dossier row is the whole of what makes an
        // icon set worth having, and a caption with no symbol declared simply has none.
        var symbol = RowIcon(caption);
        if (symbol.Length > 0 && SpriteCatalog.Has("icon_" + symbol))
            row.AddChild(new TextureRect
            {
                Texture = Art("icon_" + symbol, $"res://game/gfx/interface/icons/icon_{symbol}.png"),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(15, 15),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = ArtColour("icon_" + symbol, "text.faint")
            });

        var label = Title(CampaignUiCatalog.DetailLabel(_dossierScreen, caption, caption), 14, Ui.Colour("text.body_soft"));
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsStretchRatio = 1.1f;
        row.AddChild(label);

        var figure = Title(value, 14, Ui.Colour(valueToken));
        figure.HorizontalAlignment = HorizontalAlignment.Right;
        figure.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        figure.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(figure);

        var card = new PanelContainer();
        card.SetMeta("dossier_field", caption);
        card.SetMeta("dossier_group", _detailGroup);
        card.AddThemeStyleboxOverride("panel", ShellRowStyle(_sideDetail.GetChildCount() % 2 == 0));
        card.AddChild(row);
        _sideDetail.AddChild(card);
        // The row is in the tree now, so the figure can read the real theme font before it is
        // given tabular digits.
        TabularFigures(figure);
    }

    /// <summary>
    /// A symbol, at the size and weight every other symbol in the interface is drawn at.
    ///
    /// Returns null when nothing is declared for that name, so a caller can put it straight
    /// into a row and get nothing rather than a missing-texture box: a screen is never
    /// obliged to have art before it can list something.
    /// </summary>
    private TextureRect Symbol(string name, int size = 16, string colourToken = "text.faint")
    {
        if (string.IsNullOrEmpty(name) || !SpriteCatalog.Has("icon_" + name)) return null;
        return new TextureRect
        {
            Texture = Art("icon_" + name, $"res://game/gfx/interface/icons/icon_{name}.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(size, size),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            // Commodity PNGs carry their own palette; do not multiply that gold twice.
            Modulate = name.StartsWith("good_", StringComparison.Ordinal)
                ? ArtColour("icon_" + name, colourToken) : Ui.Colour(colourToken)
        };
    }

    /// <summary>Named after the data ID, not a numeric enum slot for mod-added goods.</summary>
    private static string GoodIcon(Simulation.Commodity commodity) =>
        CommodityCatalog.IconNameOf(commodity);

    /// <summary>And for one building. Seventeen, named the same way.</summary>
    private static string BuildingIcon(Simulation.BuildingKind building) =>
        "building_" + building.ToString().ToLowerInvariant();

    private static string RowIcon(string caption) => CampaignUiCatalog.Icon("field." + caption, caption switch
    {
        // ---- the country -------------------------------------------------------------
        "Population" or "Largest people" or "Largest minority" or "Minorities" or "Net migration" => "population",
        "Capital" or "Form of government" or "Politics" or "Ideology" or "Status" => "government",
        "Literacy" or "Brain drain" => "literacy",
        "Standard of living" => "living",
        "Radicalism" or "Wants out" => "unrest",
        "Stability" => "stability",
        "Urbanisation" or "Urbanization" => "urbanisation",
        "Buildings" => "production_capacity",
        "Productivity" => "productivity",
        "Largest faith" => "faith",

        // ---- the money ---------------------------------------------------------------
        "Treasury" => "treasury",
        "GDP" or "GDP per head" => "gdp",
        // Money coming and going is the cash-flow mark; money the state takes is the scales
        // with coins on them - the two used to share the scales, which read as justice.
        "Balance" or "Expenses" or "Military spending" => "balance",
        "Taxes" or "Tax rate" or "Tariffs" or "Tribute" => "taxes",
        "Income" => "income",
        "Trade balance" or "Market access" => "trade",
        "Inflation" => "inflation",

        // ---- the ground --------------------------------------------------------------
        // A province is a pin on the map and whoever holds it is a flag; the globe is kept
        // for the one row that really is about the world around a country.
        "Provinces" or "States held" => "province",
        "Owner" or "Controller" => "nation",
        "Neighbours" => "world",
        "Occupation" => "war",
        "Terrain" => "terrain",
        "River crossings" or "Lake shore" => "water",

        // ---- the forces --------------------------------------------------------------
        "Divisions" or "Divisions here" or "Field armies" or "Visible divisions"
            or "Fighting strength" or "Reserve" or "Daily reinforcement" => "manpower",
        "Fleets" => "navy",
        "Supply" or "Cut off" => "alert_supply",
        "Organisation" => "alert_battle",
        "War score" => "victory",

        // ---- the neighbours ----------------------------------------------------------
        "Opinion" or "Stance" or "Intelligence" => "diplomacy",
        "Guarantees" or "Guaranteed by" or "Answers to" or "Arranged" => "alliance",
        "Non-aggression pact" or "Offered" => "treaty",

        _ => ""
    });

    private void DetailHeading(string text)
    {
        _detailGroup = text;
        // A rule along the baseline, in the same brass the dossier frame uses. A heading was
        // a dim line of small capitals with nothing between it and the rows above, so a
        // window read as one column with occasional shouting in it; this is what makes the
        // divisions inside a window agree with the window.
        var source = CampaignUiCatalog.DetailLabel(_dossierScreen, "#" + text, text);
        var heading = new SectionHeading
        {
            Text = Tr(source),
            Rule = Ui.Colour("button.primary_border"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        // The same three marks Title sets, so a heading follows a language change like every
        // other caption in the game rather than freezing in whatever it was built in.
        heading.SetMeta(LocSourceMeta, source);
        heading.SetMeta(LocAppliedMeta, heading.Text);
        heading.AddThemeColorOverride("font_color", Ui.Colour("text.accent_dim"));
        heading.AddThemeFontSizeOverride("font_size", 12);
        heading.SetMeta("dossier_field", "#" + text);
        heading.SetMeta("dossier_group", text);
        heading.CustomMinimumSize = new Vector2(0, 32);
        heading.VerticalAlignment = VerticalAlignment.Bottom;
        _sideDetail.AddChild(heading);
    }

    private void BuildDossierSidePanel()
    {
        _sidePanel = TexturedPanel(_hudLayer);
        _sidePanel.Name = "CampaignDossier";
        Ui.Place(_sidePanel, "hud.side_panel");
        _sidePanel.Visible = false;

        var dossierRows = PanelRows(_sidePanel, 16, 12, 10);
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        dossierRows.AddChild(header);
        _sideFlag = FlagBadge("", 26);
        _sideFlag.Visible = false;
        header.AddChild(_sideFlag);
        _sideTitle = Title("Nation", 24, Ui.Colour("text.bright"));
        _sideTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _sideTitle.AutowrapMode = TextServer.AutowrapMode.Off;
        _sideTitle.ClipText = true;
        _sideTitle.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        header.AddChild(_sideTitle);
        _sideBackToOrders = ActionButton("← Orders", ReturnToArmyOrders, 0, 34);
        _sideBackToOrders.TooltipText = Ui.Tip(Tr("Back to the orders of the divisions this dossier was opened from (Esc)."));
        _sideBackToOrders.Visible = false;
        header.AddChild(_sideBackToOrders);
        var dossierHelp = ActionButton("?", () => OpenHandbook(HandbookCatalog.ForScreen(_openScreen)), 34, 34);
        dossierHelp.Name = "DossierHandbook";
        Tip(dossierHelp, "Open the handbook at the page about this screen (F1).");
        header.AddChild(dossierHelp);
        header.AddChild(ActionButton("×", CloseSidePanel, 38, 34));

        _dossierTabs = new HBoxContainer();
        _dossierTabs.AddThemeConstantOverride("separation", 4);
        dossierRows.AddChild(_dossierTabs);
        _dossierScroll = new ScrollContainer
        {
            Name = "DossierScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FollowFocus = true
        };
        dossierRows.AddChild(_dossierScroll);
        var sideRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        sideRows.AddThemeConstantOverride("separation", 10);
        _dossierScroll.AddChild(sideRows);

        _sideBody = Title("", 16, Ui.Colour("text.body"));
        _sideBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _sideBody.SizeFlagsVertical = SizeFlags.Fill;
        sideRows.AddChild(_sideBody);

        // Figures belong in aligned rows, not in a paragraph. A player comparing two
        // numbers should not have to find them inside a sentence first.
        _sideDetail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sideDetail.AddThemeConstantOverride("separation", 3);
        sideRows.AddChild(_sideDetail);

        // Where to go next from whatever is being read.
        //
        // A number on this panel is almost always a number somebody wants to do something
        // about, and until now doing something about it meant knowing which of eight buttons
        // at the top of the screen owned it. These are the same destinations, put beside the
        // figures that lead to them.
        _sideLinks = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sideLinks.AddThemeConstantOverride("h_separation", 5);
        _sideLinks.AddThemeConstantOverride("v_separation", 4);
        sideRows.AddChild(_sideLinks);

        BuildCountryActionControls(sideRows);
        BuildRegionInteractionControls(sideRows);
        BuildEconomyControls(sideRows);
        BuildDecisionControls(sideRows);
        BuildBattleDetailControls(sideRows);

        _militarySpending = new HSlider
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.01,
            Scrollable = false,
            TooltipText = Ui.Tip(Tr("How much the state spends on the armed forces."))
        };
        _militarySpending.ValueChanged += OnSpendingChanged;
        sideRows.AddChild(_militarySpending);

        // A bare slider tells the player nothing about what they just set. The readout
        // below it names the figure and what it costs, and hides with the slider.
        _militarySpendingReadout = Title("", 12, Ui.Colour("text.caption"));
        _militarySpendingReadout.Visible = false;
        sideRows.AddChild(_militarySpendingReadout);
    }
}
