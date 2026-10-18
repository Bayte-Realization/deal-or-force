using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Who governs and what they are for, on the screen where a country is governed.
///
/// Deliberately not a window of its own. A player looking for their form of government is
/// looking for a thing they can change, and everything else they can change about their own
/// country is already on State Affairs - a separate Politics screen would be a ninth button
/// in the top bar for two rows of choices, and one more place to have to know about.
///
/// Both rows are built from the catalogues, so a mod that adds a fourteenth ideology gets a
/// button for it, named and explained out of its own file, without this code being told.
/// </summary>
public partial class DealOrForceGame
{
    private HFlowContainer _governmentButtons = null!;
    private HFlowContainer _ideologyButtons = null!;
    private HFlowContainer _religionButtons = null!;
    private HFlowContainer _cultureButtons = null!;
    private Label _politicsSummary = null!;
    private Label _politicsFeedback = null!;
    private Label _identityFeedback = null!;
    private readonly Dictionary<string, Button> _governmentButtonById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _ideologyButtonById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Id, Button Button, bool Religion)> _identityButtons = new();
    private readonly Dictionary<string, (TextureRect Flag, Label Caption)> _politicsPreviews = new();

    private void BuildPoliticsControls(VBoxContainer rows)
    {
        StateSection(rows, "POLITICS",
            "How the state is arranged, and what the people running it are for. They are two " +
            "questions rather than one: a parliamentary republic can be liberal or socialist, and " +
            "a one-party state can be socialist or fascist. Other countries can see both.");

        _politicsSummary = RowLabel("", 12, Ui.Colour("text.body_soft"));
        _politicsSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_politicsSummary);

        rows.AddChild(RowLabel("FORM OF GOVERNMENT", 11, Ui.Colour("text.caption")));
        _governmentButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _governmentButtons.AddThemeConstantOverride("h_separation", 5);
        _governmentButtons.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(_governmentButtons);
        foreach (var government in GovernmentCatalog.All)
        {
            var id = government.Id;
            var button = ActionButton(government.Name, () => ChangePolitics(id, ""), 212, 38);
            button.TooltipText = Ui.Tip(GovernmentTooltip(government));
            AddPoliticsOption(_governmentButtons, button, "government:" + id);
            _governmentButtonById[id] = button;
        }

        rows.AddChild(RowLabel("IDEOLOGY", 11, Ui.Colour("text.caption")));
        _ideologyButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _ideologyButtons.AddThemeConstantOverride("h_separation", 5);
        _ideologyButtons.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(_ideologyButtons);
        foreach (var ideology in IdeologyCatalog.All)
        {
            var id = ideology.Id;
            var button = ActionButton(ideology.Name, () => ChangePolitics("", id), 212, 42);
            var sprite = "icon_ideology_" + id;
            var iconPath = SpriteCatalog.TextureOf(sprite, SpriteCatalog.TextureOf("icon_ideology_generic", ""));
            if (!string.IsNullOrEmpty(iconPath) && ResourceLoader.Exists(iconPath)) button.Icon = GD.Load<Texture2D>(iconPath);
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 30);
            button.TooltipText = Ui.Tip(IdeologyTooltip(ideology));
            AddPoliticsOption(_ideologyButtons, button, "ideology:" + id);
            _ideologyButtonById[id] = button;
        }

        _politicsFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        _politicsFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_politicsFeedback);

        BuildIdentityControls(rows);
    }

    // What the state says it is, over what its people are. The buttons are the country's own
    // faiths and peoples, largest first, each refused with its reason - usually the share a
    // define asks for - and the secular answers, which need nobody's permission.
    private void BuildIdentityControls(VBoxContainer rows)
    {
        StateSection(rows, "STATE IDENTITY",
            "The faith the state declares and the people it declares itself of. A declaration " +
            "needs the group to live in the country in a share large enough to carry the name; " +
            "declaring a secular state needs nobody's permission. A declaration against the " +
            "majority costs the country's calm.");

        rows.AddChild(RowLabel("STATE RELIGION", 11, Ui.Colour("text.caption")));
        _religionButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _religionButtons.AddThemeConstantOverride("h_separation", 5);
        _religionButtons.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(_religionButtons);

        rows.AddChild(RowLabel("STATE CULTURE", 11, Ui.Colour("text.caption")));
        _cultureButtons = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _cultureButtons.AddThemeConstantOverride("h_separation", 5);
        _cultureButtons.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(_cultureButtons);

        _identityFeedback = RowLabel("", 11, Ui.Colour("text.accent_dim"));
        _identityFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_identityFeedback);
    }

    private void RefreshIdentity(Nation nation)
    {
        if (_religionButtons is null || nation is null) return;
        if (!_campaign.FeatureEnabled("state_identity"))
        {
            _religionButtons.Visible = _cultureButtons.Visible = false;
            return;
        }
        _religionButtons.Visible = _cultureButtons.Visible = true;
        ClearRuntimeChildren(_religionButtons);
        ClearRuntimeChildren(_cultureButtons);
        _identityButtons.Clear();

        foreach (var (id, share) in Simulation.StateIdentity.ReligionChoices(_campaign, nation))
        {
            var captured = id;
            var button = ActionButton(Simulation.StateIdentity.Name(id, true),
                () => DeclareIdentity(captured, ""), 212, 38);
            button.Name = "StateReligion_" + id;
            button.TooltipText = Ui.Tip(Simulation.StateIdentity.IsSecular(id)
                ? Tr("A secular answer needs no congregation: the state may always decline to have a church.")
                : Simulation.Loc.Format("{0} of the country holds this faith.", Fmt.Percent(share)));
            AddIdentityOption(_religionButtons, button, id, share, true);
            _identityButtons.Add((id, button, true));
        }

        foreach (var (id, share) in Simulation.StateIdentity.Candidates(_campaign, nation, false).Take(8))
        {
            var captured = id;
            var button = ActionButton(Simulation.DemographyCatalog.CultureName(id),
                () => DeclareIdentity("", captured), 212, 38);
            AddIdentityOption(_cultureButtons, button, id, share, false);
            _identityButtons.Add((id, button, false));
        }

        foreach (var (id, button, religion) in _identityButtons)
        {
            var current = religion
                ? id.Equals(nation.DeclaredReligion, StringComparison.OrdinalIgnoreCase) ||
                  (id == Simulation.StateIdentity.SecularChoice &&
                   Simulation.StateIdentity.IsSecular(nation.DeclaredReligion))
                : id.Equals(nation.DeclaredCulture, StringComparison.OrdinalIgnoreCase);
            ApplyLedgerButtonState(button, current);
            if (current)
            {
                button.Disabled = true;
                button.TooltipText = Ui.Tip(Tr("Current course"));
            }
            else
            {
                var refusal = Simulation.StateIdentity.BlockReason(_campaign, nation,
                    religion ? id : "", religion ? "" : id);
                button.Disabled = refusal.Length > 0;
                button.TooltipText = Ui.Tip(refusal.Length > 0 ? Tr(refusal) : Tr("Declare this identity"));
            }
        }

        var settling = _campaign.Defines.StateIdentityCooldownDays - (_campaign.ElapsedDays - nation.IdentityChangedDay);
        _identityFeedback.Text = settling > 0
            ? Simulation.Loc.Format("The last declaration is still fresh. {0} day(s) to go.", settling)
            : Simulation.Loc.Format("A declaration costs about {0} stability against the majority's grain, less with it; nothing may be declared again for {1} days.",
                _campaign.Defines.StateIdentityStabilityCost.ToString("0%"), _campaign.Defines.StateIdentityCooldownDays);
    }

    /// <summary>A choice with its exact share printed under it: the number a declaration
    /// is judged by is the number the player sees, not one they hover for.</summary>
    private void AddIdentityOption(HFlowContainer flow, Button button, string id, float share, bool religion)
    {
        var card = new VBoxContainer { CustomMinimumSize = new Vector2(212, 0) };
        card.AddThemeConstantOverride("separation", 2);
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        card.AddChild(button);
        var secular = religion && Simulation.StateIdentity.IsSecular(id);
        var caption = secular
            ? Tr("No minimum · a state may always decline a church")
            : Simulation.Loc.Format("{0:0.0%} of the country · needs {1:0%}",
                share, _campaign.Defines.StateIdentityShare);
        card.AddChild(RowLabel(caption, 10,
            Ui.Colour(secular || share >= _campaign.Defines.StateIdentityShare ? "text.caption" : "text.muted")));
        flow.AddChild(card);
    }

    private void DeclareIdentity(string religion, string culture)
    {
        if (_campaign?.Player is null) return;
        var refusal = Simulation.StateIdentity.BlockReason(_campaign, _campaign.Player, religion, culture);
        if (refusal.Length > 0)
        {
            _identityFeedback.Text = Tr(refusal);
            return;
        }
        _campaign.Commands.Enqueue(new DeclareStateIdentityCommand
        {
            IssuerId = _campaign.Player.Id,
            Religion = religion,
            Culture = culture
        });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("save_complete");
        RefreshStateAffairs();
    }

    private void ChangePolitics(string government, string ideology)
    {
        if (_campaign?.Player is null) return;
        var refusal = PoliticsCore.Refusal(_campaign, _campaign.Player, government, ideology);
        if (refusal.Length > 0)
        {
            _politicsFeedback.Text = Tr(refusal);
            return;
        }
        _campaign.Commands.Enqueue(new ChangePoliticsCommand
        {
            IssuerId = _campaign.Player.Id,
            Government = government,
            Ideology = ideology
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshStateAffairs();
    }

    /// <summary>
    /// Say what the country is, what it would cost to stop being it, and - when the two
    /// halves do not fit - that it is costing something every week already.
    /// </summary>
    private void RefreshPolitics(Nation nation)
    {
        if (_politicsSummary is null || nation is null) return;

        var government = PoliticsCore.GovernmentOf(nation);
        var ideology = PoliticsCore.IdeologyOf(nation);
        var settling = PoliticsCore.ChangeCooldownDays - (_campaign.ElapsedDays - nation.PoliticsChangedDay);

        var summary = Simulation.Loc.Format("{0} · {1} · the centre decides {2:P0} of it",
            Tr(government.Name), Tr(ideology.Name), government.Centralisation);
        if (PoliticsCore.IsMismatched(nation))
            summary += "\n" + Simulation.Loc.Format(
                "{0} is uncomfortable in {1}, and the arrangement costs stability every week it lasts.",
                Tr(ideology.Name), Tr(government.Name));
        _politicsSummary.Text = summary;

        foreach (var (id, button) in _governmentButtonById)
        {
            ApplyLedgerButtonState(button, string.Equals(id, nation.Government, StringComparison.OrdinalIgnoreCase));
            RefreshPoliticsOption(nation, button, "government:" + id, id, "");
        }
        foreach (var (id, button) in _ideologyButtonById)
        {
            ApplyLedgerButtonState(button, string.Equals(id, nation.Ideology, StringComparison.OrdinalIgnoreCase));
            RefreshPoliticsOption(nation, button, "ideology:" + id, "", id);
        }

        if (settling > 0)
        {
            _politicsFeedback.Text = Simulation.Loc.Format(
                "The country is still settling after the last change. {0} day(s) to go.", settling);
            return;
        }
        // What it would cost from here, priced against the form being left rather than the
        // one being arrived at - which is why the number moves when the government does.
        var cost = PoliticsCore.BaseChangeCost * government.ChangeCost;
        _politicsFeedback.Text = Simulation.Loc.Format(
            "Changing the form of government would cost about {0:P0} stability and stir up the regions; " +
            "changing only the ideology costs less. Either way nothing else may change for {1} days.",
            cost, PoliticsCore.ChangeCooldownDays);
    }

    private void AddPoliticsOption(HFlowContainer flow, Button button, string key)
    {
        var card = new VBoxContainer { CustomMinimumSize = new Vector2(212, 0) };
        card.AddThemeConstantOverride("separation", 4);
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        button.Name = "PoliticsOption_" + key.Replace(':', '_');
        card.AddChild(button);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var flag = new TextureRect { CustomMinimumSize = new Vector2(38, 25),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        var caption = RowLabel("", 11, Ui.Colour("text.muted"));
        row.AddChild(flag); row.AddChild(caption); card.AddChild(row); flow.AddChild(card);
        _politicsPreviews[key] = (flag, caption);
    }

    private void RefreshPoliticsOption(Nation nation, Button button, string key, string government, string ideology)
    {
        var sprite = FlagCatalog.ResolvePreview(_campaign, nation, government, ideology);
        var preview = _politicsPreviews[key];
        preview.Flag.Texture = FlagSpriteTexture(sprite);
        var unchanged = sprite == FlagCatalog.Resolve(_campaign, nation);
        var isCurrent = (government.Length > 0 && government == nation.Government) ||
                        (ideology.Length > 0 && ideology == nation.Ideology);
        // The card says what this option IS to the country before it says anything about its
        // flag. "Flag unchanged" is true of the country's own ideology and of half the others.
        preview.Caption.Text = Tr(isCurrent ? "Current course"
            : string.IsNullOrEmpty(sprite) ? "No flag available"
            : unchanged ? "Flag unchanged" : "Flag after adoption");
        preview.Flag.TooltipText = preview.Caption.Text + (sprite.Length > 0 ? "\n" + sprite : "");
        var current = isCurrent;
        var refusal = PoliticsCore.Refusal(_campaign, nation, government, ideology);
        button.Disabled = current || refusal.Length > 0;
        // Keep the substantive description, with availability on the card's flag row.
        preview.Caption.TooltipText = current ? Tr("Current policy") : refusal.Length > 0 ? Tr(refusal) : Tr("Flag after adoption");
    }
}
