using System;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private bool _socialAudiencesExpanded;
    private int _socialAudiencePage;

    /// <summary>Views belong to named audiences. Reading this panel must not create beliefs.</summary>
    private void AddSocialMemoryDiplomacy(Nation nation, Nation player,
        Func<string, string, Color?, VBoxContainer> field)
    {
        var own = nation.Id == player.Id;
        var positive = Ui.Colour("state.positive");
        var negative = Ui.Colour("state.negative");
        var muted = Ui.Colour("text.muted");
        Label Note(string text, string name = "")
        {
            var label = RowLabel("", 12, muted);
            label.Text = text;
            label.Name = name.Length > 0 ? name : "SocialNote";
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return label;
        }
        void Belief(VBoxContainer stack, string caption, PublicImage image, string name)
        {
            var known = image is { LastReviewDay: >= 0 };
            var label = Note(Loc.Format("{0}: {1}", caption, known
                ? Loc.Format("{0} · {1}", SocialMemory.StatusName(image), (image.Esteem * 100f).ToString("+0;-0;0"))
                : Loc.Get("Not established yet")), name);
            if (known && image.Status != 0) label.AddThemeColorOverride("font_color", image.Status > 0 ? positive : negative);
            stack.AddChild(label);
            if (!known) return;
            var bar = new WarBar { CustomMinimumSize = new Vector2(0, 12), SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Track = Ui.Colour("war.track"), TooltipText = Loc.Format("Entrenchment: {0}. A lasting image changes slowly.", Fmt.Percent(image.Entrenchment)) };
            bar.Lean(image.Esteem, positive, negative);
            stack.AddChild(bar);
            if (image.LastIncidentDay >= 0 && image.LastReason.Length > 0)
                label.TooltipText = Loc.Format("Latest public fact: {0} · {1} days ago", Loc.Get(image.LastReason),
                    Math.Max(0, _campaign.ElapsedDays - image.LastIncidentDay));
        }
        if (SocialMemory.ImagesEnabled(_campaign))
        {
            var reputation = field("PUBLIC REPUTATION", "", null);
            reputation.Name = "SocialMemoryPanel";
            reputation.AddChild(Note(Loc.Get("A society's view, not a universal verdict. Public facts leave a gradually fading memory.")));
            if (!own)
            {
                Belief(reputation, Loc.Get("Our society's view of them"), SocialMemory.Image(player, SocialMemory.NationKey(nation)), "ReputationOurView");
                Belief(reputation, Loc.Get("Their society's view of us"), SocialMemory.Image(nation, SocialMemory.NationKey(player)), "ReputationTheirView");
                AddPublicOutreach(reputation, nation, player);
            }
            else
            {
                Belief(reputation, Loc.Get("Domestic standing"), nation.DomesticImage, "DomesticStanding");
                var audiences = _campaign.Nations.Where(n => n.Id != nation.Id)
                    .Select(n => (Nation: n, Image: SocialMemory.Image(n, SocialMemory.NationKey(nation))))
                    .Where(entry => entry.Image is { LastReviewDay: >= 0 }).OrderByDescending(entry => MathF.Abs(entry.Image.Esteem))
                    .ThenBy(entry => entry.Nation.Id).ToList();
                reputation.AddChild(Note(Loc.Format("Admiring societies: {0} · Stigmatising societies: {1}",
                    audiences.Count(entry => entry.Image.Status > 0), audiences.Count(entry => entry.Image.Status < 0)), "PublicAudienceCounts"));
                var toggle = ActionButton(_socialAudiencesExpanded ? "Hide public views" : "Show public views", () => {}, 0, 32);
                toggle.Name = "SocialAudienceToggle";
                toggle.Disabled = audiences.Count == 0 && nation.PublicImages.Count == 0;
                reputation.AddChild(toggle);
                var details = new VBoxContainer { Name = "SocialAudienceDetails", Visible = _socialAudiencesExpanded };
                details.AddThemeConstantOverride("separation", 7);
                reputation.AddChild(details);
                void FillDetails()
                {
                    if (details.GetChildCount() > 0) return;
                    var views = audiences.Select(e => (Caption: e.Nation.DisplayName, Image: e.Image, Name: "Audience" + e.Nation.Id)).ToList();
                    foreach (var entry in nation.PublicImages.Where(p => p.Key.StartsWith("culture:", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
                        views.Add((Loc.Format("Our view: {0}", Loc.Get(DemographyCatalog.CultureName(entry.Key[8..]))), entry.Value, "CultureView"));
                    foreach (var group in _campaign.Coalitions)
                        if (SocialMemory.Image(nation, SocialMemory.CoalitionKey(group)) is { } image)
                            views.Add((Loc.Format("Coalition against {0}", _campaign.NationById(group.TargetId)?.DisplayName ?? "?"), image, "CoalitionView"));
                    var rows = new VBoxContainer { Name = "SocialAudiencePageRows" };
                    rows.AddThemeConstantOverride("separation", 7);
                    var navigation = new HBoxContainer { Name = "SocialAudienceNavigation" };
                    var previous = ActionButton("Previous views", () => {}, 0, 32);
                    var next = ActionButton("Next views", () => {}, 0, 32);
                    previous.Name = "SocialAudiencePrevious"; next.Name = "SocialAudienceNext";
                    var count = Note("", "SocialAudiencePageCount");
                    count.HorizontalAlignment = HorizontalAlignment.Center;
                    navigation.AddChild(previous); navigation.AddChild(count); navigation.AddChild(next);
                    details.AddChild(navigation); details.AddChild(rows);
                    void Page()
                    {
                        const int perPage = 6;
                        var pages = Math.Max(1, (views.Count + perPage - 1) / perPage);
                        _socialAudiencePage = Math.Clamp(_socialAudiencePage, 0, pages - 1);
                        foreach (var child in rows.GetChildren()) { rows.RemoveChild(child); child.QueueFree(); }
                        foreach (var view in views.Skip(_socialAudiencePage * perPage).Take(perPage))
                            Belief(rows, view.Caption, view.Image, view.Name);
                        count.Text = Loc.Format("Page {0}/{1} · {2} views", _socialAudiencePage + 1, pages, views.Count);
                        previous.Disabled = _socialAudiencePage == 0; next.Disabled = _socialAudiencePage == pages - 1;
                        if (views.Count == 0) rows.AddChild(Note(Loc.Get("No established public views yet.")));
                    }
                    previous.Pressed += () => { _socialAudiencePage--; Page(); if (previous.Disabled) next.GrabFocus(); };
                    next.Pressed += () => { _socialAudiencePage++; Page(); if (next.Disabled) previous.GrabFocus(); };
                    Page();
                }
                if (_socialAudiencesExpanded) FillDetails();
                toggle.Pressed += () => { _socialAudiencesExpanded = !_socialAudiencesExpanded;
                    if (_socialAudiencesExpanded) FillDetails();
                    details.Visible = _socialAudiencesExpanded;
                    toggle.Text = Loc.Get(_socialAudiencesExpanded ? "Hide public views" : "Show public views"); };
            }
        }
        // Detailed domestic expectations are shown only for the player's own nation.
        if (!own || !SocialMemory.HabitsEnabled(_campaign)) return;
        var habits = field("THE ESTABLISHED NORMAL", "", null);
        habits.Name = "HistoricalNormsPanel";
        habits.AddChild(SocialMetric("social.habit", "government", Loc.Format("Institutional habit: {0} · {1} years", Fmt.Percent(nation.InstitutionalHabit),
            (nation.InstitutionAgeDays / 365f).ToString("0.0")), "InstitutionHabit"));
        var habitBar = new WarBar { CustomMinimumSize = new Vector2(0, 12), SizeFlagsHorizontal = SizeFlags.ExpandFill, Track = Ui.Colour("war.track") };
        habitBar.Shares((nation.InstitutionalHabit, Ui.Colour("text.accent")));
        habits.AddChild(habitBar);
        habits.AddChild(SocialMetric("social.expectations", "welfare", nation.ExpectedLivingStandard < 0f ? Loc.Get("Living-standard expectations are not established yet.") :
            Loc.Format("Expected living standard: {0} · Current: {1}", Fmt.Percent(nation.ExpectedLivingStandard), Fmt.Percent(nation.StandardOfLiving)), "LivingExpectations"));
        var effect = SocialMemory.StabilityEffect(_campaign, nation);
        habits.AddChild(SocialMetric("social.stability", "stability", Loc.Format("Net stability effect: {0} percentage points", (effect * 100f).ToString("+0.00;-0.00;0.00")), "HabitStabilityEffect"));
        habits.AddChild(Note(Loc.Get("Continuity builds trust, but prosperity becomes an expectation. Changing institutions resets the habit; old borders reduce, never eliminate, secession pressure.")));
    }

}
