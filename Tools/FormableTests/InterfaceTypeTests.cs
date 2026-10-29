using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>The interface's typographic foundation, held to its own numbers: a scale of a dozen
/// steps and not two dozen, two declared faces, and a law catalogue a player can actually choose
/// from. None of this is a taste test; it is the part of "this looks serious" that can be counted.</summary>
static class InterfaceTypeTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL interface type: " + label);
        Console.WriteLine("PASS interface type: " + label);
    }

    public static void Run()
    {
        Scale();
        Faces();
        Vocabulary();
        Laws();
    }

    /// <summary>
    /// The vocabulary is only worth having if the screens use it. A source check, the same kind
    /// the doctrine suite already uses for its own invariants: a head that exists in one file
    /// and is used by nobody is a mock-up, not a design system.
    /// </summary>
    static void Vocabulary()
    {
        var repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(GameDataLoader.DataFolder(), ".."));
        string Source(string relative) => System.IO.File.ReadAllText(System.IO.Path.Combine(repo, relative));

        var chrome = Source("Scripts/ui/ScreenChromeUI.cs");
        Check(chrome.Contains("ScreenHeader(", StringComparison.Ordinal) &&
              chrome.Contains("SectionRule(", StringComparison.Ordinal) &&
              chrome.Contains("NumberCell(", StringComparison.Ordinal) &&
              chrome.Contains("ActionRow(", StringComparison.Ordinal) &&
              chrome.Contains("FactRow(", StringComparison.Ordinal),
            "the screen vocabulary exists: head, section, figure, row, fact");

        foreach (var screen in new[]
                 {
                     "Scripts/ui/DiplomacyScreenUI.cs",
                     "Scripts/ui/PolicyWorkspaceUI.cs",
                     "Scripts/ui/DoctrineScreenUI.cs",
                     "Scripts/ui/ResearchScreenUI.cs",
                     "Scripts/ui/ProductionScreenUI.cs",
                     "Scripts/ui/TradeScreenUI.cs",
                     "Scripts/ui/LedgerUI.cs",
                     "Scripts/ui/StateAffairsUI.cs",
                     "Scripts/ui/SovereignFinanceWorkspaceUI.cs",
                     "Scripts/ui/ConflictsScreenUI.cs",
                     "Scripts/ui/WarLedgerUI.cs",
                     "Scripts/ui/SituationRoomUI.cs",
                     "Scripts/ui/SupportScreenUI.cs",
                     "Scripts/ui/RecruitmentUI.cs",
                     "Scripts/ui/SubjectsAndReleaseUI.cs",
                     "Scripts/ui/PeaceConferenceUI.cs",
                     "Scripts/ui/PeaceOfferUI.cs",
                     "Scripts/ui/DealComposerUI.cs",
                     "Scripts/ui/DealInboxUI.cs",
                     "Scripts/ui/UltimatumComposerUI.cs",
                     "Scripts/ui/SaveLoadUI.cs",
                     "Scripts/ui/HandbookUI.cs",
                     "Scripts/ui/MediaLibraryUI.cs"
                 })
            Check(Source(screen).Contains("ScreenHeader(", StringComparison.Ordinal),
                screen + " is headed by the shared vocabulary");

        // Tabs are a row of buttons and nothing else; three screens had grown their own gaps and
        // their own heights (6 by 6, 6 by 4, 6 by 5), which is invisible per screen and obvious
        // when the screens are seen one after another.
        foreach (var screen in new[]
                 {
                     "Scripts/ui/LedgerUI.cs",
                     "Scripts/ui/ConflictsScreenUI.cs",
                     "Scripts/ui/SettingsUI.cs"
                 })
            Check(Source(screen).Contains("TabStrip(", StringComparison.Ordinal),
                screen + " uses the shared tab strip");

        Check(!Source("Scripts/ui/SaveLoadUI.cs").Contains("AcceptDialog", StringComparison.Ordinal),
            "SaveLoadUI uses the authored Atlas modal surface instead of a raw engine AcceptDialog");

        // The head that changes its words keeps the same door: the ledger's title says whose
        // record is on the desk, and it must come from the shared header, not from a label built
        // by hand beside it.
        Check(Source("Scripts/ui/LedgerUI.cs").Contains("HeaderTitle(head)", StringComparison.Ordinal),
            "a head whose words change is still the shared head");

        // One door for type: a raw size must not survive inside Title.
        var game = Source("Scripts/DealOrForceGame.cs");
        Check(game.Contains("Ui.TypeStep(size)", StringComparison.Ordinal) &&
              game.Contains("Ui.DisplayThreshold", StringComparison.Ordinal),
            "Title snaps every size to the scale and chooses the display face");
        Check(!game.Contains("label.AddThemeFontSizeOverride(\"font_size\", size)", StringComparison.Ordinal),
            "no raw size is applied to a title any more");
        Check(game.Contains("Ui.NumberFont", StringComparison.Ordinal) &&
              game.Contains("Ui.Tracked(", StringComparison.Ordinal),
            "figures are tabular and capitals are tracked");
        // Capitals are recognised rather than requested: "POSTURE" written in the source used to
        // arrive without tracking and read as crowded letters.
        Check(game.Contains("LooksUppercase(", StringComparison.Ordinal),
            "a label already set in capitals gets the tracking too");

        // Reachability, as a check rather than a promise. Every screen the router knows how to
        // open must also be in the index the player can search: a screen reachable only from a
        // button that lives inside a tab of a panel is a screen some players will never see.
        var navigator = Source("Scripts/ui/NavigatorUI.cs");
        Check(navigator.Contains("NavigatorEntries", StringComparison.Ordinal) &&
              navigator.Contains("ToggleNavigator", StringComparison.Ordinal),
            "the campaign has an index of every screen");
        foreach (var screen in new[]
                 {
                     "Situation", "Ledger", "Wars", "Conflicts", "State Affairs", "Laws", "Decisions",
                     "Diplomacy", "Trade", "Military", "Production", "Support", "Research",
                     "Doctrines", "Economy", "Nation"
                 })
            Check(navigator.Contains("\"" + screen + "\"", StringComparison.Ordinal),
                "the index reaches " + screen);
        Check(navigator.Contains("ShowSettings", StringComparison.Ordinal) &&
              navigator.Contains("ShowSaveLoadInterface", StringComparison.Ordinal) &&
              navigator.Contains("OpenHandbook", StringComparison.Ordinal),
            "the index reaches the system screens too");

        // One GDScript file, and it is the start-up guard.
        //
        // boot_guard.gd is here for exactly one reason: it runs when the C# half of the game did
        // not load, and says so instead of leaving a black window. That is the only job this
        // project needs GDScript for. Gameplay written in GDScript would be invisible to every
        // check in this file and to the whole C# test stand, which is how a codebase ends up with
        // two half-tested halves. The rule is enforced here rather than remembered.
        var scripts = System.IO.Directory.GetFiles(System.IO.Path.Combine(repo, "Scripts"), "*.gd",
            System.IO.SearchOption.AllDirectories);
        Check(scripts.Length == 1 &&
              scripts[0].EndsWith("boot_guard.gd", StringComparison.OrdinalIgnoreCase),
            "Scripts holds exactly one GDScript file, the start-up guard");
    }

    static void Scale()
    {
        InterfaceSkin.ResetCatalog();
        float Step(string token) => InterfaceSkin.Metric(token, -1f);

        var micro = Step("ui.font.micro");
        var caption = Step("ui.font.caption");
        var body = Step("ui.font.body");
        var heading = Step("ui.font.heading");
        var title = Step("ui.font.title");
        var display = Step("ui.font.display");
        var displayLarge = Step("ui.font.display_large");
        Check(micro >= 9f && caption >= 10f && body >= 11f,
            "the small steps are declared and legible");
        Check(micro < caption && caption < body && body < heading && heading < title &&
              title < display && display < displayLarge,
            "the scale is strictly ordered: micro < caption < body < heading < title < display < display_large");
        Check(heading >= body * 1.25f,
            "a heading is at least a quarter larger than body text, so a screen has a top");
        Check(display >= 26f && display <= 36f,
            "the display step is a page title, not a caption in bold");
        Check(InterfaceSkin.Metric("ui.font.track_caps", -1f) >= 1f &&
              InterfaceSkin.Metric("ui.font.track_caps", -1f) <= 4f,
            "upper-case labels carry letter spacing of their own");
        Check(InterfaceSkin.Metric("ui.font.leading", -1f) >= 1f,
            "reading blocks have extra line height");

        // The scale the interface may actually use is the declared one, and it is a scale:
        // eight steps at most, no neighbours a pixel apart.
        var steps = new[]
            {
                micro, caption, body, heading, title, display, displayLarge
            }
            .Where(step => step > 0f).Distinct().OrderBy(step => step).ToArray();
        Check(steps.Length <= 10, "the interface sets text in ten steps or fewer");
        var narrow = 0;
        for (var index = 1; index < steps.Length; index++)
            if (steps[index] - steps[index - 1] < 1f) narrow++;
        Check(narrow == 0, "no two steps of the scale are a pixel apart");
    }

    static void Faces()
    {
        FontCatalog.Load();
        var ui = FontCatalog.FamiliesOf("ui.default");
        var display = FontCatalog.FamiliesOf("ui.display");
        Check(ui.Length > 0, "the interface face is declared by name, not left to the engine default");
        Check(display.Length > 0, "and so is the display face");
        Check(display.Any(family => family.Contains("Serif", StringComparison.OrdinalIgnoreCase) ||
                                    family.Contains("Georgia", StringComparison.OrdinalIgnoreCase)),
            "the display face is a serif: a heading should not look like a larger caption");
        Check(!display.SequenceEqual(ui), "the two faces are not the same list of families");
        // The map keeps its own serif, so a reskin of the interface cannot silently retype the globe.
        Check(FontCatalog.FamiliesOf("map.countries").Length > 0,
            "the map's own lettering is still declared");
    }

    static void Laws()
    {
        var laws = PoliticalPolicyCatalog.AllLaws.ToArray();
        Check(laws.Length >= 200, $"the game offers {laws.Length} law options, not a short list");
        var categories = laws.GroupBy(law => law.Category).ToArray();
        Check(categories.Length == 23, "all twenty-three categories are still there");
        Check(categories.All(group => group.Count() >= 6),
            "every category offers at least six choices, so choosing a law is a decision");
        Check(categories.All(group => group.Any(law => law.Default)),
            "every category still has a baseline a country can be on");
        Check(laws.All(law => law.Name.Length > 0 && law.Description.Length > 0),
            "every law has a name and a sentence explaining what it costs");
        Check(laws.Where(law => !law.Default).All(law => law.Modifiers.Count > 0 || law.Scope == "foreign"),
            "every alternative on offer changes something the simulation reads");
        Check(laws.Select(law => law.Id).Distinct(StringComparer.Ordinal).Count() == laws.Length,
            "no two laws share an id");
    }
}
