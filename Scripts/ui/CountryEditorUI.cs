using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// One screen for a whole country: what it is called in every language the game has, its
    /// colour, capital, politics, leaders, peoples, money, flag and land - saved as the ordinary
    /// data files a mod ships (see <see cref="CountryEditorDocument"/>), into the Editor
    /// workspace mod and never into the base game.
    ///
    /// Opened on the country being looked at; the tag field at the top switches to another, and
    /// a tag the world has never seen starts a fresh document for a country a mod will add.
    /// </summary>
    private void OpenCountryEditor(string tag, Action back = null)
    {
        if (_networkConnected || _isLoading || _campaign is null) return;
        tag = (tag ?? _campaign.Player?.Tag ?? "").Trim().ToUpperInvariant();
        var rows = AuthorWindow("Country editor", back, back is null ? "Close" : "Back", () => OpenCountryEditor(tag, back));
        rows.AddChild(Title("Edit a country as a whole. Saving writes common/countries, history/countries, history/provinces and localization files into your mod; the base game is never changed.", 14, Ui.Colour("text.muted")));

        var nation = _campaign.NationByTag(tag);
        var saved = CountryEditorDocument.Read(AuthorRoot, tag);
        var document = nation is not null ? CountryEditorDocument.FromNation(nation, Loc.Languages) : new CountryEditorDocument { Tag = tag, Colour = "#888888" };
        // What this editor saved before wins over the running world, which may not have the mod
        // enabled yet: reopening the editor shows the author's own last word.
        if (saved is not null) Overlay(document, saved);
        foreach (var language in Loc.Languages)
            if (!document.Names.ContainsKey(language)) document.Names[language] = (nation?.Name ?? tag, "");

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        rows.AddChild(scroll);
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 24);
        scroll.AddChild(columns);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 6);
        right.AddThemeConstantOverride("separation", 6);
        columns.AddChild(left);
        columns.AddChild(right);

        HBoxContainer Row(VBoxContainer column, string caption)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var label = Title(caption, 13, Ui.Colour("text.muted"));
            label.CustomMinimumSize = new Vector2(170, 0);
            label.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(label);
            column.AddChild(row);
            return row;
        }
        LineEdit Field(VBoxContainer column, string caption, string value, float width = 0)
        {
            var edit = new LineEdit { Text = value ?? "", SizeFlagsHorizontal = width > 0 ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(width, 32) };
            Row(column, caption).AddChild(edit);
            edit.TextChanged += _ => _authorDirty = true;
            return edit;
        }

        // ---- Identity ----
        left.AddChild(Title("Identity", 13, Ui.Colour("text.accent"), true));
        var tagRow = Row(left, "Tag");
        var tagEdit = new LineEdit { Text = tag, MaxLength = 8, CustomMinimumSize = new Vector2(110, 32) };
        tagRow.AddChild(tagEdit);
        tagRow.AddChild(ActionButton("Open", () => ConfirmAuthorExit(() => OpenCountryEditor(tagEdit.Text, back)), 100, 32));
        if (nation is null)
            left.AddChild(Title("No country with this tag exists in the current world: saving prepares the files a mod adding it needs.", 12, Ui.Colour("text.caption")));

        var names = new Dictionary<string, (LineEdit Name, LineEdit Adjective)>();
        foreach (var language in document.Names.Keys.OrderBy(l => l == "english" ? "" : l, StringComparer.Ordinal))
        {
            var (name, adjective) = document.Names[language];
            var label = Loc.DisplayName(language);
            names[language] = (Field(left, Tr("Name") + " · " + label, name),
                Field(left, Tr("Adjective") + " · " + label, adjective));
        }

        var colourRow = Row(left, "Map colour");
        var colourPicker = new ColorPickerButton { CustomMinimumSize = new Vector2(64, 32), EditAlpha = false, Color = Color.FromHtml(FlagDesign.Normalise(document.Colour) ?? "#888888") };
        var colourHex = new LineEdit { Text = FlagDesign.Normalise(document.Colour) ?? "#888888", MaxLength = 7, CustomMinimumSize = new Vector2(110, 32) };
        colourRow.AddChild(colourPicker);
        colourRow.AddChild(colourHex);
        colourPicker.ColorChanged += c => { colourHex.Text = "#" + c.ToHtml(false).ToLowerInvariant(); _authorDirty = true; };
        colourHex.TextChanged += text => { if (FlagDesign.Normalise(text) is { } hex) colourPicker.Color = Color.FromHtml(hex); _authorDirty = true; };

        var capital = Field(left, "Capital", document.CapitalName);
        var latitude = Field(left, "Capital latitude", document.CapitalLatitude?.ToString(CultureInfo.InvariantCulture) ?? "", 140);
        latitude.PlaceholderText = Tr("unchanged");
        var longitude = Field(left, "Capital longitude", document.CapitalLongitude?.ToString(CultureInfo.InvariantCulture) ?? "", 140);
        longitude.PlaceholderText = Tr("unchanged");

        // ---- Politics ----
        right.AddChild(Title("Politics", 13, Ui.Colour("text.accent"), true));
        OptionButton Choice(string caption, IEnumerable<(string Id, string Name)> options, string current)
        {
            var box = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddItem(Tr("(unchanged)"));
            box.SetItemMetadata(0, "");
            foreach (var (id, name) in options)
            {
                box.AddItem(Tr(name.Length > 0 ? name : id));
                box.SetItemMetadata(box.ItemCount - 1, id);
                if (id == current) box.Selected = box.ItemCount - 1;
            }
            box.ItemSelected += _ => _authorDirty = true;
            Row(right, caption).AddChild(box);
            return box;
        }
        var government = Choice("Government", GovernmentCatalog.All.Select(g => (g.Id, g.Name)), document.Government);
        var ideology = Choice("Ideology", IdeologyCatalog.All.Select(i => (i.Id, i.Name)), document.Ideology);
        var headOfState = Field(right, "Head of state", document.HeadOfState);
        var headOfGovernment = Field(right, "Head of government", document.HeadOfGovernment);
        var cultures = Field(right, "Primary cultures", string.Join(", ", document.PrimaryCultures));
        cultures.TooltipText = Ui.Tip(Tr("Culture ids from common/cultures, separated by commas."));
        var treasury = Field(right, "Starting treasury (billions)", document.Treasury?.ToString("0.###", CultureInfo.InvariantCulture) ?? "", 160);

        // ---- Flag ----
        right.AddChild(Title("Flag", 13, Ui.Colour("text.accent"), true));
        var flagRow = new HBoxContainer();
        flagRow.AddThemeConstantOverride("separation", 10);
        right.AddChild(flagRow);
        if (nation is not null) flagRow.AddChild(FlagBadge(nation.Tag, 40));
        flagRow.AddChild(ActionButton("Edit flag…", () => ConfirmAuthorExit(() =>
            OpenFlagEditor(tagEdit.Text, () => OpenCountryEditor(tagEdit.Text, back))), 180, 40));

        // ---- Regions ----
        right.AddChild(Title("Regions", 13, Ui.Colour("text.accent"), true));
        right.AddChild(Title("Ticked regions belong to this country on the bookmark. Untick one to hand it to the country named below; add others by name.", 12, Ui.Colour("text.caption")));
        var states = _campaign.States.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        var owned = nation is null ? new HashSet<int>() : states.Where(s => s.NationMapIndex == nation.MapIndex).Select(s => s.MapIndex).ToHashSet();
        // Regions the saved document already gave this country count as chosen.
        var provinceToState = new Dictionary<int, int>();
        if (_campaign.ProvinceMap is { } map)
            for (var province = 1; province <= map.ProvinceCount; province++) provinceToState[province] = map.ProvinceToState[province];
        var chosen = new HashSet<int>(owned);
        if (saved is not null)
            foreach (var (province, owner) in saved.Ownership)
                if (provinceToState.TryGetValue(province, out var state))
                {
                    if (owner == tag) chosen.Add(state);
                    else chosen.Remove(state);
                }
        var regionList = new VBoxContainer();
        right.AddChild(regionList);
        void ShowRegions()
        {
            foreach (var child in regionList.GetChildren()) child.QueueFree();
            foreach (var state in states.Where(s => owned.Contains(s.MapIndex) || chosen.Contains(s.MapIndex)))
            {
                var box = new CheckBox { Text = state.Name + (owned.Contains(state.MapIndex) ? "" : "  (+)"), ButtonPressed = chosen.Contains(state.MapIndex) };
                var index = state.MapIndex;
                box.Toggled += on => { if (on) chosen.Add(index); else chosen.Remove(index); _authorDirty = true; };
                regionList.AddChild(box);
            }
        }
        ShowRegions();
        var addRow = new HBoxContainer();
        right.AddChild(addRow);
        var addRegion = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        addRegion.AddItem(Tr("Add a region…"));
        addRegion.SetItemMetadata(0, -1);
        foreach (var state in states.Where(s => !owned.Contains(s.MapIndex)))
        {
            var holder = _campaign.Nations.FirstOrDefault(n => n.MapIndex == state.NationMapIndex);
            addRegion.AddItem(state.Name + (holder is null ? "" : " — " + holder.DisplayName));
            addRegion.SetItemMetadata(addRegion.ItemCount - 1, state.MapIndex);
        }
        addRegion.ItemSelected += index =>
        {
            var state = addRegion.GetItemMetadata((int)index).AsInt32();
            addRegion.Selected = 0;
            if (state < 0) return;
            chosen.Add(state);
            _authorDirty = true;
            ShowRegions();
        };
        addRow.AddChild(addRegion);
        var releaseTo = Field(right, "Untick hands regions to", "", 110);
        releaseTo.PlaceholderText = "TAG";

        var status = Title("", 13, Ui.Colour("text.caption"));
        rows.AddChild(status);
        var actions = new HFlowContainer();
        rows.AddChild(actions);
        actions.AddChild(ActionButton("Save country to my mod", () =>
        {
            try
            {
                var edited = new CountryEditorDocument { Tag = tagEdit.Text.Trim().ToUpperInvariant() };
                foreach (var (language, (name, adjective)) in names) edited.Names[language] = (name.Text, adjective.Text);
                edited.Colour = colourHex.Text.Trim();
                edited.CapitalName = capital.Text.Trim();
                edited.CapitalLatitude = Optional(latitude.Text, "Capital latitude");
                edited.CapitalLongitude = Optional(longitude.Text, "Capital longitude");
                edited.Government = government.GetSelectedMetadata().AsString();
                edited.Ideology = ideology.GetSelectedMetadata().AsString();
                edited.HeadOfState = headOfState.Text;
                edited.HeadOfGovernment = headOfGovernment.Text;
                edited.PrimaryCultures = cultures.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToList();
                edited.Treasury = Optional(treasury.Text, "Starting treasury");
                var recipient = releaseTo.Text.Trim().ToUpperInvariant();
                var released = owned.Where(s => !chosen.Contains(s)).ToList();
                if (released.Count > 0 && _campaign.NationByTag(recipient) is null)
                    throw new InvalidDataException(Tr("Name the country that receives the unticked regions."));
                foreach (var (province, state) in provinceToState)
                {
                    if (chosen.Contains(state) && !owned.Contains(state)) edited.Ownership.Add((province, edited.Tag));
                    else if (released.Contains(state)) edited.Ownership.Add((province, recipient));
                }
                var written = edited.Write(AuthorRoot);
                ContentWorkbench.EnsureMod(AuthorRoot);
                _authorDirty = false;
                status.Text = Tr("Saved. Enable the Editor workspace mod and start a new campaign to play it.") + "\n" + string.Join("\n", written);
            }
            catch (Exception e) { status.Text = e.Message; }
        }, 260, 42, true));
        actions.AddChild(ActionButton("Open mod folder", () => { ContentWorkbench.EnsureMod(AuthorRoot); OS.ShellOpen(AuthorRoot); }, 180, 42));
        _authorDirty = false;

        static float? Optional(string text, string what)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)) return value;
            throw new InvalidDataException(Tr(what) + ": " + Tr("not a number."));
        }
    }

    /// <summary>The values a saved document states, laid over the live country's.</summary>
    private static void Overlay(CountryEditorDocument into, CountryEditorDocument saved)
    {
        if (saved.Colour.Length > 0) into.Colour = saved.Colour;
        if (saved.CapitalName.Length > 0) into.CapitalName = saved.CapitalName;
        into.CapitalLatitude = saved.CapitalLatitude ?? into.CapitalLatitude;
        into.CapitalLongitude = saved.CapitalLongitude ?? into.CapitalLongitude;
        if (saved.Government.Length > 0) into.Government = saved.Government;
        if (saved.Ideology.Length > 0) into.Ideology = saved.Ideology;
        if (saved.HeadOfState.Length > 0) into.HeadOfState = saved.HeadOfState;
        if (saved.HeadOfGovernment.Length > 0) into.HeadOfGovernment = saved.HeadOfGovernment;
        if (saved.PrimaryCultures.Count > 0) into.PrimaryCultures = saved.PrimaryCultures;
        into.Treasury = saved.Treasury ?? into.Treasury;
        foreach (var (language, value) in saved.Names) into.Names[language] = value;
    }
}
