using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Every command the console knows, as a table rather than a switch.
///
/// The switch it replaces had the shape every console grows into: twenty-seven cases in one
/// place and a hand-written help string in another, which had already drifted - `event` and
/// `teleport` worked and went unmentioned, and nothing made the two agree. A table cannot
/// drift, because `help` is printed FROM it: a command that exists is documented, and a
/// command that is documented exists.
///
/// It also buys the three things that make a console quick to use rather than merely
/// present - completion, history and "did you mean" - because all three are questions about
/// a list of names, and there was no list of names before.
///
/// Adding a command is one `Add(...)` call. The handler takes the rest of the line as one
/// string and returns what to print; returning an empty string prints nothing, which is what
/// a command that opens a panel wants.
/// </summary>
public partial class DealOrForceGame
{
    private sealed class ConsoleCommand
    {
        public string Name = "";
        public string[] Aliases = Array.Empty<string>();
        public string Category = "";

        /// <summary>The argument shape, shown by `help NAME` and when a command is misused.</summary>
        public string Usage = "";

        public string Help = "";
        public Func<string, string> Run = _ => "";
    }

    private List<ConsoleCommand> _consoleCommands;
    private Dictionary<string, ConsoleCommand> _consoleByName;

    /// <summary>The order categories are printed in: what you reach for most, first.</summary>
    private static readonly string[] ConsoleCategories =
    {
        "CONSOLE", "TIME", "PLAY", "COUNTRY", "PEOPLE", "WAR", "ARMIES",
        "MAP", "ECONOMY", "MONEY", "DIPLOMACY", "CONTENT", "DEBUG", "FUN"
    };

    private void EnsureConsoleCommands()
    {
        if (_consoleCommands is not null) return;
        var table = new List<ConsoleCommand>();

        void Add(string name, string category, string usage, string help,
            Func<string, string> run, params string[] aliases) =>
            table.Add(new ConsoleCommand
            {
                Name = name, Category = category, Usage = usage,
                Help = help, Run = run, Aliases = aliases
            });

        // ------------------------------------------------------------------ the console --
        Add("help", "CONSOLE", "help [command]",
            "Every command, or what one command does and what it takes.",
            ConsoleHelp, "?", "h");
        Add("commands", "CONSOLE", "commands [text]",
            "Every command name on one line, for finding one you half remember.",
            ConsoleCommandList);
        Add("clear", "CONSOLE", "clear", "Empty the console.", _ => ClearConsole(), "cls");
        Add("selftest", "CONSOLE", "selftest",
            "Run every command once with no arguments and report the ones that threw. " +
            "It touches the world - it pauses, raises an event, writes a save - so run it on " +
            "a campaign you are willing to lose, not on the one you care about.",
            _ => ConsoleSelfTest());

        // --------------------------------------------------------------------- the clock --
        Add("pause", "TIME", "pause", "Stop and start the clock.", _ => ConsolePause());
        Add("speed", "TIME", "speed 1-5", "How fast the clock runs while it is running.", ConsoleSpeed);
        Add("date", "TIME", "date", "The date, the hour, and how long the campaign has run.",
            _ => ConsoleDate());
        Add("feature", "MODDING", "feature · feature aviation off · feature aviation on",
            "Inspect or switch global scenario mechanics. Disabled content stays dormant, not deleted.", ConsoleFeature);
        Add("skip", "TIME", "skip 30 · skip 2w · skip 1y",
            "Run days immediately, without watching them. The fastest way to find out what a " +
            "change does over a year. Days, weeks, months or years; ten years is the ceiling.",
            ConsoleSkip, "run", "advance");
        Add("fingerprint", "TIME", "fingerprint",
            "One number for the whole world state. Two machines that disagree about a " +
            "campaign disagree about this first; it is what desync is diagnosed with.",
            _ => ConsoleFingerprint(), "hash", "sync");

        // --------------------------------------------------------- who you are playing as --
        Add("tag", "PLAY", "tag DEU", "Become another country.", ConsoleTag, "play");
        Add("observe", "PLAY", "observe", "Hand every country to the AI and watch.", _ => ConsoleObserve());
        Add("find", "PLAY", "find korea", "Countries whose name or tag matches, with their tags.",
            ConsoleFind, "search");
        Add("country", "COUNTRY", "country [DEU]",
            "Everything a country is, on one screen: politics, money, war, people, army.",
            ConsoleCountry, "nation", "dossier");

        // --------------------------------------------------- changing a country by hand --
        Add("money", "COUNTRY", "money 500", "Add to the treasury, in billions. Negative takes it away.",
            ConsoleMoney, "cash", "treasury");
        Add("manpower", "COUNTRY", "manpower 100000", "Add to the manpower reserve.", ConsoleManpower);
        Add("stability", "COUNTRY", "stability 75", "Set stability, as a percentage.", ConsoleStability);
        Add("warsupport", "COUNTRY", "warsupport 80", "Set war support, as a percentage.", ConsoleWarSupport);
        Add("notoriety", "COUNTRY", "notoriety 0", "Set how much the world holds against you.",
            ConsoleNotoriety);
        Add("literacy", "COUNTRY", "literacy 90", "Set literacy, as a percentage.", ConsoleLiteracy);
        Add("politics", "COUNTRY", "politics communism one_party_state",
            "Change ideology and form of government at once. Either may be given alone. " +
            "This is the quick way to see a country's other flags.",
            ConsolePolitics, "regime");
        Add("budget", "COUNTRY", "budget [tax|military|welfare|education] [0-100]",
            "The four rates, or set one of them.", ConsoleBudget, "spending");
        Add("govfocus", "COUNTRY", "govfocus nationalsecurity",
            "What the cabinet is working on.", ConsoleGovernmentFocus, "cabinet");
        Add("tradepolicy", "COUNTRY", "tradepolicy balancedtrade",
            "How open the country is to trade.", ConsoleTradePolicy);
        Add("modifier", "COUNTRY", "modifier ID [days] · modifier -ID",
            "Grant a modifier from common/modifiers, or take one away with a leading minus.",
            ConsoleModifier);
        Add("modifiers", "COUNTRY", "modifiers [DEU]",
            "What is currently acting on a country, and when each of it lapses.", ConsoleModifiers);

        // --------------------------------------------------------------------- the people --
        Add("pops", "PEOPLE", "pops [region name]",
            "A region's people three ways: by trade, by class, and by sector. The selected " +
            "region unless one is named.", ConsolePops, "population");

        // ------------------------------------------------------------------------- the war --
        Add("wars", "WAR", "wars", "Every war being fought, with its score and its fronts.", _ => ConsoleWars());
        Add("declare", "WAR", "declare RUS", "Declare war, and say why if it is refused.", ConsoleDeclare);
        Add("whitepeace", "WAR", "whitepeace [RUS]",
            "End a war where it stands, taking nothing.", ConsoleWhitePeace, "endwar");
        Add("warscore", "WAR", "warscore 60",
            "Set the score of the war you are fighting, to reach a peace worth testing.",
            ConsoleWarScore);
        Add("peace", "WAR", "peace", "Open the peace conference for your war.", _ => ConsolePeaceConference(),
            "conference");
        Add("battles", "WAR", "battles",
            "Every battle being fought right now: where, who, how it is going.", _ => ConsoleBattles());
        Add("fronts", "WAR", "fronts", "The length and score of every front.", _ => ConsoleFronts());
        Add("occupy", "WAR", "occupy [PROVINCE]",
            "Hand a province to your country's control. The selected one unless a number is given.",
            ConsoleOccupy);
        Add("alliances", "DIPLOMACY", "alliances [DEU]",
            "Who a country is allied to, who it has guaranteed, and what is on its table.",
            ConsoleAlliances, "allies");
        Add("ally", "DIPLOMACY", "ally FRA",
            "Offer an alliance, or accept the one you have been offered. What they answer " +
            "and why is printed.", ConsoleAlly, "alliance");
        Add("unally", "DIPLOMACY", "unally FRA",
            "Walk out of one. They remember it.", ConsoleUnally, "breakalliance");
        Add("guarantee", "DIPLOMACY", "guarantee LUX [off]",
            "Promise to fight for a country that has not asked you to be its ally - the " +
            "cheap half of an alliance, and the useful half of being a great power.",
            ConsoleGuarantee);

        Add("subjects", "DIPLOMACY", "subjects [DEU]",
            "Who answers to a country, under what arrangement, and how badly each of them " +
            "wants out.", ConsoleSubjects, "puppets");
        Add("puppet", "DIPLOMACY", "puppet LUX [puppet|protectorate|satellite|dominion|client]",
            "Make a country answer to yours. The kinds come from common/subjects, so a mod " +
            "may add its own.", ConsoleSubjugate, "subjugate");
        Add("release", "DIPLOMACY", "release LUX",
            "Let one go.", ConsoleRelease);
        Add("integrate", "DIPLOMACY", "integrate LUX",
            "Take a country that answers to yours into it. Only after the years its kind asks " +
            "for, and only while it is not trying to leave - `subjects` says how far off both are.",
            ConsoleIntegrate, "absorb");

        Add("annex", "WAR", "annex LUX",
            "Take a whole country. Blunt on purpose - it is for reaching a state of the world, " +
            "not for playing.", ConsoleAnnex);

        // ------------------------------------------------------------------- the divisions --
        Add("armies", "ARMIES", "armies [DEU]",
            "What a country has under arms, and where the weight of it is.", ConsoleArmies, "divisions");
        Add("orders", "ARMIES", "orders",
            "What the selected divisions were told to do, when, how far along they are, and " +
            "what the last attempt to move them returned. This is what a division that will " +
            "not advance is diagnosed with.", _ => ConsoleOrders(), "why");
        Add("spawn", "ARMIES", "spawn 3 [line_infantry]",
            "Put divisions in the capital, fully formed, without training them.", ConsoleSpawn);
        Add("teleport", "ARMIES", "teleport PROVINCE",
            "Move the selected divisions to a province at once.", ConsoleTeleport, "tp");
        Add("heal", "ARMIES", "heal [all]",
            "Fill the selected divisions back up - men, organisation, equipment, supply. " +
            "`heal all` does the whole army.", ConsoleHeal, "reinforce");
        Add("kill", "ARMIES", "kill [all]",
            "Delete the selected divisions, or the whole army with `all`.", ConsoleKill, "disband");
        Add("groups", "ARMIES", "groups", "Army headquarters and what hangs off them.", _ => ConsoleGroups());
        Add("fleets", "ARMIES", "fleets [DEU]", "Ships, where they are and what they are doing.",
            ConsoleFleets, "navy");

        // ----------------------------------------------------------------------- the map --
        Add("mapmode", "MAP", "mapmode pol|dip|war|pop|gdp", "What the globe is coloured by.",
            ConsoleMapMode, "mm");
        Add("camera", "MAP", "camera 55.7 37.6", "Point the camera at a latitude and longitude.",
            ConsoleFocus, "focus");
        Add("goto", "MAP", "goto bavaria",
            "Fly to a country or a region by name, and select it.", ConsoleGoto, "fly");
        Add("province", "MAP", "province [ID]",
            "Everything about one tactical province: region, owner, holder, ground, who is standing on it.",
            ConsoleProvince);
        Add("region", "MAP", "region [name]",
            "Everything about one region: people, wealth, anger, buildings, what it might secede over.",
            ConsoleRegion, "state");
        Add("freecam", "MAP", "freecam [on|off]", "The camera that flies, and its controls.",
            ConsoleFreeCamera);

        // -------------------------------------------------------------------- the economy --
        Add("economy", "ECONOMY", "economy",
            "Whether the emergent economy still reproduces the authored figures.", _ => ConsoleEconomy(),
            "gdp");
        Add("market", "ECONOMY", "market [oil]",
            "What a country makes, wants, buys and sells, and what it is short of.", ConsoleMarket,
            "goods", "prices");
        Add("trade", "ECONOMY", "trade [DEU]",
            "Who a country trades with, how much, and how badly it would miss them.", ConsoleTrade);
        Add("build", "ECONOMY", "build agriculture [3]",
            "Queue building work in the selected region, without waiting for the money.",
            ConsoleBuild, "construct");
        Add("buildings", "ECONOMY", "buildings [region]",
            "What stands in a region, and what is being built there.", ConsoleBuildings);

        // ----------------------------------------------------------------- the neighbours --
        Add("relations", "DIPLOMACY", "relations [DEU]",
            "How the world feels about a country, warmest and coldest first, with the reasons.",
            ConsoleRelations, "opinions");
        Add("opinion", "DIPLOMACY", "opinion RUS -100",
            "Set what another country thinks of you.", ConsoleOpinion);

        // -------------------------------------------------------------------- the content --
        Add("effects", "CONTENT", "effects [filter]",
            "Every key content can write to, which is the vocabulary a modder has to guess at otherwise.",
            ConsoleEffectVocabulary);
        Add("loc", "CONTENT", "loc [russian] [template]",
            "Switch language, and report what that language has not translated yet. 'loc german template' starts a translation mod in user://mods with every line to translate.",
            ConsoleLocalization, "localization", "language");
        Add("skin", "CONTENT", "skin [filter]",
            "What the interface is painted with, and which tokens a mod has moved.",
            ConsoleSkin, "interface");
        Add("flags", "CONTENT", "flags [DEU]",
            "Which flag a country flies and why: every name the resolver tries, in order, " +
            "marked with whether the art exists.", ConsoleFlags, "flag");
        Add("defines", "CONTENT", "defines [filter]",
            "Every tunable number the simulation runs on, and what it is set to.", ConsoleDefines);
        Add("settings", "CONTENT", "settings [filter]",
            "Every setting the game declares, and what it is currently set to.", ConsoleSettings);
        Add("decisions", "CONTENT", "decisions",
            "What you could enact, what it costs, and the reason for each one you cannot.",
            _ => ConsoleDecisions());
        Add("decision", "CONTENT", "decision ID", "Enact one by id, cost and blocks ignored.",
            ConsoleDecision);
        Add("events", "CONTENT", "events [filter]", "The event catalogue, by id.", ConsoleEvents);
        Add("event", "CONTENT", "event [ID]",
            "Raise an event now - a named one, or whatever the catalogue offers this country.",
            ConsoleEvent);
        Add("formable", "CONTENT", "formable",
            "Every state this country could proclaim itself, and for each one it cannot, what is missing.",
            _ => ConsoleFormable());
        Add("secession", "CONTENT", "secession [DEU]",
            "Which regions are pulling away, how hard, and which are close to going.",
            ConsoleSecession, "discontent");
        Add("secede", "CONTENT", "secede [region]",
            "Make a region declare independence now - the selected one, or the one named - " +
            "by the same road a secession takes on its own: a new country, its militia, and an " +
            "answer from the one it left.", ConsoleSecede);
        Add("reload", "CONTENT", "reload",
            "Reload interface/, gui/ and localization/ from game and the active packages. " +
            "Campaign state, gameplay rules, art and code are not reloaded. Ctrl+Shift+R.", _ => ConsoleReload());
        Add("validate", "CONTENT", "validate",
            "Every complaint the data made while loading, mod by mod.", _ => ConsoleValidate(), "errors");

        // ---------------------------------------------------------------------- the debug --
        Add("fps", "DEBUG", "fps", "Frames, frame time, draw calls and what the renderer is holding.",
            _ => ConsoleFps());
        Add("sim", "DEBUG", "sim [filter]",
            "Where a simulated day is actually spent, slowest pass first.", ConsoleSimulation);
        Add("mem", "DEBUG", "mem", "What the process is holding, and a collection on request.",
            ConsoleMemory, "gc");
        Add("logs", "DEBUG", "logs", "Where the diagnostic logs are and whether they are being written.",
            _ => ConsoleLogs());
        Add("saves", "DEBUG", "saves", "Every save on disk, newest first.", _ => ConsoleSaves());
        Add("save", "DEBUG", "save [slot]", "Write a save without leaving the map.", ConsoleSave);
        Add("load", "DEBUG", "load SLOT", "Load a save by slot.", ConsoleLoad);

        // ---------------------------------------------------------------------- the money --
        Add("inflation", "MONEY", "inflation [country]",
            "What prices are doing and why, line by line, with the key rate beside them.",
            ConsoleInflation, "prices");
        Add("keyrate", "MONEY", "keyrate [PERCENT|seize|release]",
            "Read the central bank's key rate, take it off the bank, give it back, or set it " +
            "yourself once you have taken it - and see what the cheap money is costing.",
            ConsoleKeyRate, "rate", "cb");
        Add("emission", "MONEY", "emission [PERCENT|off]",
            "Have money printed, as a share of a year's output. Needs the bank taken first. " +
            "The money is real and arrives at once; what it costs is written in the prices.",
            ConsoleEmission, "print", "printer");
        Add("layout", "DEBUG", "layout",
            "Look at the interface on screen and name every caption that has been squeezed to " +
            "nothing. The bug that hid four headings in this project, findable in one command.",
            ConsoleLayout, "captions");

        // ------------------------------------------------------------------- the clock, again
        Add("step", "TIME", "step [HOURS]",
            "How much campaign time one simulation step covers, and set it live. One hour is " +
            "the shipped pace; the answer does not change with it, only the grain.",
            ConsoleStep, "tick");
        Add("advance", "TIME", "advance [DAYS]",
            "Advance whole campaign days without starting the clock, whatever the step length is.",
            ConsoleAdvance, "day", "days");
        Add("cadence", "DEBUG", "cadence [filter]",
            "How often each pass has run, per step - the quickest way to see a system that " +
            "thinks in days running on every step instead.",
            ConsoleCadence);
        Add("plural", "DEBUG", "plural [key]",
            "The forms a counted sentence takes in the language that is loaded. One, two, five, " +
            "eleven, twenty-one - which is where a translation usually breaks.",
            ConsolePlural);

        // ------------------------------------------------------------------------- the rest --
        //
        // Jokes that do arithmetic. Every one of these is computed from the world in front of
        // you rather than printed from a list, because a console that answers a silly question
        // with something true about your own country is the best advertisement this game has.
        Add("blame", "FUN", "blame",
            "Find out whose fault it is. Names the country that actually dislikes you most and " +
            "what they say it is about.", _ => ConsoleBlame(), "whodunnit");
        Add("vibe", "FUN", "vibe",
            "How the country is doing, in one sentence, at the register the figures deserve.",
            _ => ConsoleVibe(), "mood");
        Add("doomsday", "FUN", "doomsday",
            "When stability reaches nothing, at the rate it has actually been falling. " +
            "Arithmetic, not prophecy.", _ => ConsoleDoomsday(), "deathclock");
        Add("ratio", "FUN", "ratio",
            "You against whoever you are fighting, figure by figure, with a verdict.",
            _ => ConsoleRatio());
        Add("idle", "FUN", "idle",
            "Every division of yours that nobody has told to do anything. Usually more than you think.",
            _ => ConsoleIdleArmies(), "slackers");
        Add("flip", "FUN", "flip [question]",
            "A coin, flipped out of the campaign's own seed - so it is the same coin twice, which " +
            "is either reassuring or the worst possible property for a coin.", ConsoleFlip, "8ball");
        Add("coffee", "FUN", "coffee",
            "The treasury buys a coffee. It costs one, and it goes on the books.",
            _ => ConsoleCoffee(), "tea");
        Add("whoami", "FUN", "whoami",
            "Everything the simulation knows about you, which is less than you would like.",
            _ => ConsoleWhoAmI());
        Add("touchgrass", "FUN", "touchgrass",
            "A verse, and eight seconds later the game closes. It does not save first. " +
            "The verse is in game/common/console.",
            _ => ConsoleTouchGrass());
        Add("sudo", "FUN", "sudo [anything]",
            "There is nobody above you to ask.", ConsoleSudo);
        Add("xyzzy", "FUN", "xyzzy", "Nothing happens.", _ => ConsoleXyzzy());

        _consoleCommands = table;
        _consoleByName = new Dictionary<string, ConsoleCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in table)
        {
            _consoleByName[command.Name] = command;
            foreach (var alias in command.Aliases) _consoleByName[alias] = command;
        }
    }

    // =====================================================================================
    //  The console talking about itself
    // =====================================================================================

    private string ConsoleHelp(string argument)
    {
        EnsureConsoleCommands();
        var wanted = argument.Trim();
        if (wanted.Length > 0)
        {
            if (!_consoleByName.TryGetValue(wanted, out var found))
                return $"No command called '{EscapeBb(wanted)}'.{ConsoleSuggestion(wanted)}";
            var alsoKnownAs = found.Aliases.Length == 0
                ? ""
                : $"\n  also: {string.Join(", ", found.Aliases)}";
            return $"{Bb("text.accent")}{found.Name}[/color]  ·  {found.Category}\n" +
                   $"  {EscapeBb(found.Usage)}\n  {EscapeBb(found.Help)}{alsoKnownAs}";
        }

        var lines = new List<string>
        {
            $"{_consoleCommands.Count} commands. " +
            $"{Bb("state.info")}help NAME[/color] explains one; Tab completes; Up and Down are the history."
        };
        foreach (var category in ConsoleCategories)
        {
            var names = _consoleCommands.Where(command => command.Category == category)
                .Select(command => command.Name).ToArray();
            if (names.Length == 0) continue;
            lines.Add($"{Bb("text.accent")}{category}[/color]  {string.Join(" · ", names)}");
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Every command, run once, with the failures collected.
    ///
    /// Seventy-six commands reaching into every part of the simulation is seventy-six ways
    /// for a rename somewhere else to turn one of them into an exception nobody sees until
    /// they type it. The dispatcher already catches a throw and prints it rather than taking
    /// the game down; this is the same catch, applied to all of them at once, so a broken
    /// command is found in one keystroke rather than in the middle of testing something else.
    ///
    /// Deliberately not a check of what each command ANSWERS - that is not knowable from
    /// here. It is a check that each of them still runs.
    /// </summary>
    private string ConsoleSelfTest()
    {
        EnsureConsoleCommands();
        var broken = new List<string>();
        var ran = 0;
        foreach (var command in _consoleCommands)
        {
            // Not itself, or it recurses; not `clear`, or the report is wiped as it is written;
            // not `touchgrass`, or the test closes the game it is testing.
            if (command.Name is "selftest" or "clear" or "touchgrass") continue;
            try
            {
                command.Run("");
                ran++;
            }
            catch (Exception exception)
            {
                broken.Add($"  {Bb("state.negative")}{command.Name}[/color] threw " +
                           $"{exception.GetType().Name}: {EscapeBb(exception.Message)}");
            }
        }
        return broken.Count == 0
            ? $"{Bb("state.positive")}All {ran} command(s) ran.[/color] " +
              "Whether each said something true is still your problem."
            : $"{ran} ran, {broken.Count} threw:\n" + string.Join("\n", broken);
    }

    private string ConsoleCommandList(string argument)
    {
        EnsureConsoleCommands();
        var filter = argument.Trim();
        var names = _consoleCommands
            .Where(command => filter.Length == 0 ||
                              command.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                              command.Help.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(command => command.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0
            ? $"Nothing matches '{EscapeBb(filter)}'."
            : string.Join(" ", names);
    }

    /// <summary>
    /// What the reader probably meant. A mistyped command is the most common thing anyone
    /// types into a console, and "Unknown command" on its own is the least useful thing to
    /// say back: a name one letter out is right there in the table.
    /// </summary>
    private string ConsoleSuggestion(string typed)
    {
        EnsureConsoleCommands();
        var close = _consoleByName.Keys
            .Select(name => (Name: name, Distance: ConsoleNameDistance(typed, name)))
            .Where(candidate => candidate.Distance <= 2)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Name)
            .Take(4)
            .ToArray();
        return close.Length == 0 ? " Type help." : $" Did you mean {string.Join(", ", close)}?";
    }

    /// <summary>Edit distance, capped: a name more than two typos away is not a suggestion.</summary>
    private static int ConsoleNameDistance(string typed, string name)
    {
        if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 1;
        var previous = new int[name.Length + 1];
        var current = new int[name.Length + 1];
        for (var index = 0; index <= name.Length; index++) previous[index] = index;
        for (var left = 1; left <= typed.Length; left++)
        {
            current[0] = left;
            for (var right = 1; right <= name.Length; right++)
                current[right] = Math.Min(Math.Min(current[right - 1] + 1, previous[right] + 1),
                    previous[right - 1] + (char.ToLowerInvariant(typed[left - 1]) ==
                                           char.ToLowerInvariant(name[right - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[name.Length];
    }

    /// <summary>Every command name that starts with what has been typed, for Tab.</summary>
    private IReadOnlyList<string> ConsoleCompletions(string prefix)
    {
        EnsureConsoleCommands();
        return _consoleCommands
            .Select(command => command.Name)
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    // =====================================================================================
    //  Small shared helpers
    // =====================================================================================

    private static string[] ConsoleWords(string argument) =>
        (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The country a command is about: the one named, else the selected, else yours.</summary>
    private Nation ConsoleNation(string argument)
    {
        if (_campaign is null) return null;
        var tag = (argument ?? "").Trim();
        if (tag.Length == 0) return _selectedNation ?? _campaign.Player;
        return _campaign.NationByTag(tag) ??
               _campaign.Nations.FirstOrDefault(nation =>
                   nation.Name.Contains(tag, StringComparison.OrdinalIgnoreCase));
    }

    private string ConsoleSecede(string argument)
    {
        var state = ConsoleState(argument);
        if (state is null) return "Usage: secede Crimea - or select a region first.";
        var report = SecessionEvents.Force(_campaign, state);
        if (report?.Seceded is null)
            return $"{state.Name} cannot go on its own: a lake, a region with no owner, or its country's last.";
        _campaign.MarkMapChanged();
        Refresh();
        return $"{report.Seceded.Name} ({report.Seceded.Tag}) has left {report.Parent.Name}, " +
               $"{report.Population:N0} people - {report.Outcome}.";
    }

    /// <summary>The region a command is about: the one named, else the selected.</summary>
    private ScenarioState ConsoleState(string argument)
    {
        if (_campaign is null) return null;
        var name = (argument ?? "").Trim();
        if (name.Length == 0) return _selectedState;
        return _campaign.States.FirstOrDefault(state =>
                   string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase)) ??
               _campaign.States.FirstOrDefault(state =>
                   state.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A percentage typed either way: 75 and 0.75 both mean three quarters.</summary>
    private static bool TryShare(string value, out float share)
    {
        share = 0f;
        if (!TryFloat(value, out var number)) return false;
        share = Math.Clamp(number > 1f ? number / 100f : number, 0f, 1f);
        return true;
    }

    // =====================================================================================
    //  The clock
    // =====================================================================================

    private string ConsoleDate()
    {
        if (_campaign is null) return "No campaign.";
        return $"{_campaign.Date.Display(true)} · day {_campaign.ElapsedDays:N0} of the campaign · " +
               $"{_campaign.TickHours} hour(s) a step · {(_campaign.IsPaused ? "paused" : "running")}";
    }

    private string ConsoleSkip(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var text = argument.Trim().ToLowerInvariant();
        if (text.Length == 0) text = "1";
        var unit = text[^1];
        var span = unit switch { 'd' => 1, 'w' => 7, 'm' => 30, 'y' => 365, _ => 0 };
        if (span > 0) text = text[..^1];
        else span = 1;
        if (!int.TryParse(text, out var count) || count <= 0) return "Usage: skip 30 · skip 2w · skip 1y";

        var days = count * span;
        if (days > 3650)
            return $"{days:N0} days is over ten years. The console stops at 3,650 so a slipped " +
                   "key cannot hang the game for a quarter of an hour.";

        var paused = _campaign.IsPaused;
        _campaign.IsPaused = false;
        var from = _campaign.ElapsedDays;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Counted as well as measured: a Tick that stops advancing would otherwise spin here
        // for ever, and a developer console is exactly where that would first happen.
        var steps = 0;
        var ceiling = days * 24 + 64;
        while (_campaign.ElapsedDays - from < days && steps++ < ceiling) _campaign.Tick();
        clock.Stop();
        _campaign.IsPaused = paused;
        Refresh();

        var ran = _campaign.ElapsedDays - from;
        var stalled = ran < days ? " The clock stopped advancing before it got there." : "";
        return $"Ran {ran:N0} day(s) in {clock.ElapsedMilliseconds:N0} ms " +
               $"({(ran == 0 ? 0 : clock.ElapsedMilliseconds / (double)ran):0.0} ms a day). " +
               $"Now {_campaign.Date.Display(true)}.{stalled}";
    }

    private string ConsoleFingerprint() => _campaign is null
        ? "No campaign."
        : $"Fingerprint {CampaignFingerprint.Of(_campaign):X16} at day {_campaign.ElapsedDays:N0} " +
          $"({_campaign.Date}). Two clients that disagree here have diverged.";

    // =====================================================================================
    //  Who you are, and who everybody is
    // =====================================================================================

    private string ConsoleFind(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var text = argument.Trim();
        if (text.Length == 0) return "Usage: find korea";
        var matches = _campaign.Nations
            .Where(nation => nation.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                             nation.Tag.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(nation => nation.Population)
            .Take(20)
            .Select(nation => $"  {nation.Tag}  {EscapeBb(nation.Name)} · {Fmt.People(nation.Population)} · " +
                              $"{nation.Ideology} {nation.Government}")
            .ToArray();
        return matches.Length == 0
            ? $"No country matches '{EscapeBb(text)}'."
            : string.Join("\n", matches);
    }

    private string ConsoleCountry(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        var states = _campaign.StatesOf(nation);
        var divisions = _campaign.Armies.Count(army => army.NationId == nation.Id);
        var men = _campaign.Armies.Where(army => army.NationId == nation.Id).Sum(army => army.Personnel);
        var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id)).ToArray();
        var flag = FlagCatalog.Resolve(_campaign, nation);
        return EscapeBb(
            $"{nation.Name} ({nation.Tag}) · {nation.Continent} · capital {nation.CapitalName}\n" +
            $"  politics   {nation.Ideology} · {nation.Government} · focus {nation.GovernmentFocus} · " +
            $"trade {nation.TradePolicy}\n" +
            $"  standing   stability {nation.Stability:P0} · war support {nation.WarSupport:P0} · " +
            $"notoriety {nation.Notoriety:0} · literacy {nation.Literacy:P0}\n" +
            $"  money      treasury ${nation.Treasury:N1}B · GDP ${nation.Gdp:N1}B · " +
            $"weekly {nation.WeeklyBalance:+0.00;-0.00;0.00}B · tax {nation.TaxRate:P0}\n" +
            $"  people     {Fmt.People(nation.Population)} over {states.Length} region(s) · " +
            $"urban {nation.Urbanisation:P0} · farming {nation.AgricultureShare:P0} · " +
            $"living {nation.StandardOfLiving:P0}\n" +
            $"  army       {divisions:N0} division(s) · {men:N0} men · " +
            $"reserve {nation.ManpowerReserve:N0} · {_campaign.Fleets.Count(fleet => fleet.NationId == nation.Id)} fleet(s)\n" +
            $"  war        {(wars.Length == 0 ? "at peace" : string.Join(", ", wars.Select(war => war.Name)))}\n" +
            $"  flag       {(flag.Length == 0 ? "none - drawn as a plain rectangle" : flag)}");
    }

    // =====================================================================================
    //  Changing a country by hand
    // =====================================================================================

    private string ConsoleWarSupport(string value)
    {
        if (_campaign?.Player is null || !TryShare(value, out var share)) return "Usage: warsupport 80";
        _campaign.Player.WarSupport = share;
        Refresh();
        return $"War support set to {share:P0}.";
    }

    private string ConsoleNotoriety(string value)
    {
        if (_campaign?.Player is null || !TryFloat(value, out var amount)) return "Usage: notoriety 0";
        _campaign.Player.Notoriety = MathF.Max(0f, amount);
        Refresh();
        return $"Notoriety set to {_campaign.Player.Notoriety:0}.";
    }

    private string ConsoleLiteracy(string value)
    {
        if (_campaign?.Player is null || !TryShare(value, out var share)) return "Usage: literacy 90";
        _campaign.Player.Literacy = share;
        foreach (var state in _campaign.StatesOf(_campaign.Player)) state.Literacy = share;
        Refresh();
        return $"Literacy set to {share:P0} across {_campaign.StatesOf(_campaign.Player).Length} region(s).";
    }

    private string ConsolePolitics(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var words = ConsoleWords(argument);
        if (words.Length == 0)
            return $"{_campaign.Player.Tag} is {_campaign.Player.Ideology} · {_campaign.Player.Government}.\n" +
                   "Usage: politics IDEOLOGY [GOVERNMENT] - either may be given alone.";

        // Either order, because nobody remembers which comes first: whichever word names a
        // government is the government, and the other is the ideology.
        var government = "";
        var ideology = "";
        foreach (var word in words)
        {
            if (GovernmentCatalog.Knows(word)) government = word;
            else ideology = word;
        }
        if (!_campaign.ChangePolitics(_campaign.Player.Id, government, ideology, out var refusal))
            return $"Refused: {EscapeBb(refusal)}";
        Refresh();
        return $"{_campaign.Player.Tag} is now {_campaign.Player.Ideology} · {_campaign.Player.Government}. " +
               $"Flag: {FlagCatalog.Resolve(_campaign, _campaign.Player)}";
    }

    private string ConsoleBudget(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var nation = _campaign.Player;
        var words = ConsoleWords(argument);
        if (words.Length == 0)
            return $"tax {nation.TaxRate:P0} · military {nation.MilitarySpending:P0} · " +
                   $"welfare {nation.WelfareSpending:P0} · education {nation.EducationSpending:P0}\n" +
                   $"weekly: tax +{nation.WeeklyTaxIncome:0.00}B, tariffs +{nation.WeeklyTariffIncome:0.00}B, " +
                   $"spending -{nation.WeeklyExpenses:0.00}B, balance {nation.WeeklyBalance:+0.00;-0.00;0.00}B";

        if (words.Length < 2 || !TryShare(words[1], out var rate))
            return "Usage: budget tax 40 · budget military 25 · budget welfare 30 · budget education 20";
        switch (words[0].ToLowerInvariant())
        {
            case "tax": _campaign.SetTaxRate(nation.Id, rate); break;
            case "military": _campaign.SetMilitarySpending(nation.Id, rate); break;
            case "welfare": _campaign.SetWelfareSpending(nation.Id, rate); break;
            case "education": _campaign.SetEducationSpending(nation.Id, rate); break;
            default: return "Lines: tax, military, welfare, education.";
        }
        Refresh();
        return $"{words[0].ToLowerInvariant()} set to {rate:P0}.";
    }

    private string ConsoleGovernmentFocus(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var wanted = argument.Trim();
        if (wanted.Length == 0)
            return $"Focus: {_campaign.Player.GovernmentFocus}. " +
                   $"Choices: {string.Join(", ", Enum.GetNames<GovernmentFocus>())}";
        if (!Enum.TryParse<GovernmentFocus>(wanted, true, out var focus))
            return $"Unknown focus. Choices: {string.Join(", ", Enum.GetNames<GovernmentFocus>())}";
        _campaign.SetGovernmentFocus(_campaign.Player.Id, focus);
        Refresh();
        return $"Cabinet focus set to {focus}.";
    }

    private string ConsoleTradePolicy(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var wanted = argument.Trim();
        if (wanted.Length == 0)
            return $"Trade policy: {_campaign.Player.TradePolicy}. " +
                   $"Choices: {string.Join(", ", Enum.GetNames<TradePolicy>())}";
        if (!Enum.TryParse<TradePolicy>(wanted, true, out var policy))
            return $"Unknown policy. Choices: {string.Join(", ", Enum.GetNames<TradePolicy>())}";
        _campaign.SetTradePolicy(_campaign.Player.Id, policy);
        Refresh();
        return $"Trade policy set to {policy}.";
    }

    private string ConsoleModifier(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var words = ConsoleWords(argument);
        if (words.Length == 0)
            return "Usage: modifier ID [days] · modifier -ID to take one away.\n" +
                   $"Known: {string.Join(", ", ModifierCatalog.All.Take(24).Select(entry => entry.Id))}";

        var id = words[0];
        if (id.StartsWith("-", StringComparison.Ordinal))
        {
            id = id[1..];
            return ModifierSystem.Revoke(_campaign.Player, id)
                ? $"Revoked '{id}'."
                : $"{_campaign.Player.Tag} does not have '{id}'.";
        }
        var days = words.Length > 1 && int.TryParse(words[1], out var parsed) ? parsed : 0;
        if (!ModifierSystem.Grant(_campaign, _campaign.Player, id, days))
            return $"No modifier called '{id}' in common/modifiers.";
        Refresh();
        return $"Granted '{id}'{(days > 0 ? $" for {days} day(s)" : "")}.";
    }

    private string ConsoleModifiers(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        if (nation.Modifiers.Count == 0) return $"Nothing is acting on {nation.Tag}.";
        return string.Join("\n", nation.Modifiers.Select(active =>
        {
            var remaining = active.IsPermanent
                ? "permanent"
                : $"{active.DaysRemaining(_campaign.ElapsedDays):N0} day(s) left";
            return $"  {active.Id} · granted day {active.GrantedDay:N0} · {remaining}";
        }));
    }

    // =====================================================================================
    //  The people
    // =====================================================================================

    private string ConsolePops(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var state = ConsoleState(argument);
        if (state is null) return "Select a region, or name one: pops bavaria";
        var total = Math.Max(1L, state.PopulationGroups.Sum(group => group.Size));

        string Bar(IEnumerable<(string Key, long Size)> rows) => string.Join("\n", rows
            .Where(row => row.Size > 0)
            .Select(row => $"    {row.Key,-22} {row.Size / (double)total,7:P1}  {Fmt.People(row.Size)}"));

        return EscapeBb(
            $"{state.Name} · {Fmt.People(state.Population)} · urban {state.Urbanization:P0} · " +
            $"literacy {state.Literacy:P0} · living {state.StandardOfLiving:P0}\n" +
            "  TRADES\n" + Bar(EconomySystem.Professions(state)
                .Select(row => (DemographyCatalog.ProfessionName(row.Profession), row.Size))) + "\n" +
            "  CLASSES\n" + Bar(EconomySystem.Classes(state).Select(row => (row.Class, row.Size))) + "\n" +
            "  SECTORS\n" + Bar(EconomySystem.Sectors(state).Select(row => (row.Sector, row.Size))) + "\n" +
            "  PEOPLES\n" + Bar(EconomySystem.Cultures(state)
                .Take(6).Select(row => (DemographyCatalog.CultureName(row.Culture), row.Size))));
    }

    // =====================================================================================
    //  The war
    // =====================================================================================

    private string ConsoleWars()
    {
        if (_campaign is null) return "No campaign.";
        var running = _campaign.Wars.Where(war => !war.Ended).ToArray();
        if (running.Length == 0) return "The world is at peace.";
        return string.Join("\n", running.Select(war =>
        {
            var attacker = _campaign.NationById(war.AttackerId);
            var defender = _campaign.NationById(war.DefenderId);
            return EscapeBb($"  {attacker?.Tag} vs {defender?.Tag} · {war.Name}\n" +
                            $"      score {war.WarScore:0.0} (battles {war.BattleScore:0.0}, " +
                            $"occupation {war.OccupationScore:0.0}) · started day {war.StartedDay:N0}\n" +
                            $"      fronts {war.AttackerFront.Count:N0}/{war.DefenderFront.Count:N0} · " +
                            $"losses {war.AttackerLosses:N0}/{war.DefenderLosses:N0}");
        }));
    }

    private string ConsoleDeclare(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var target = ConsoleNation(argument);
        if (target is null) return "Usage: declare RUS";
        if (target.Id == _campaign.Player.Id) return "A country cannot declare war on itself.";
        if (!CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, target.Id, out var reason))
            return $"Refused: {EscapeBb(reason)}";
        var war = CampaignDiplomacy.DeclareWar(_campaign, _campaign.Player.Id, target.Id);
        Refresh();
        return war is null ? "The declaration was refused." : $"War declared: {EscapeBb(war.Name)}.";
    }

    private string ConsoleWhitePeace(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var other = ConsoleNation(argument);
        var wars = _campaign.Wars.Where(war => !war.Ended && war.Involves(_campaign.Player.Id) &&
                                               (other is null || argument.Trim().Length == 0 ||
                                                war.Involves(other.Id))).ToArray();
        if (wars.Length == 0) return "No war to end.";
        foreach (var war in wars)
        {
            war.Ended = true;
            war.EndedDay = _campaign.ElapsedDays;
            // Nobody won: -1, as a war that has not ended says. Zero is Indonesia.
            war.WinnerId = -1;
            war.PeaceSummary = "White peace, from the console.";
        }
        _campaign.MarkStrategicChange();
        Refresh();
        return $"Ended {wars.Length} war(s) where they stood.";
    }

    private string ConsoleWarScore(string value)
    {
        if (_campaign?.Player is null || !TryFloat(value, out var score)) return "Usage: warscore 60";
        var war = _campaign.Wars.FirstOrDefault(candidate =>
            !candidate.Ended && candidate.Involves(_campaign.Player.Id));
        if (war is null) return $"{_campaign.Player.Name} is not at war.";
        // Signed from the player's side, because that is how anybody testing a peace thinks
        // about it: 60 means "I am winning by sixty", whichever side of the war I am on.
        war.WarScore = war.AttackerId == _campaign.Player.Id ? score : -score;
        Refresh();
        return $"{EscapeBb(war.Name)}: war score set to {war.WarScore:0.0}. `peace` opens the conference.";
    }

    private string ConsoleBattles()
    {
        if (_campaign is null) return "No campaign.";
        var fighting = _campaign.Battles.Where(battle => !battle.Ended).ToArray();
        if (fighting.Length == 0) return "Nothing is being fought over anywhere.";
        return string.Join("\n", fighting.Take(30).Select(battle =>
        {
            var attacker = _campaign.NationById(battle.AttackerId);
            var defender = _campaign.NationById(battle.DefenderId);
            return $"  #{battle.Id} province {battle.ProvinceId:N0} · {attacker?.Tag} attacking {defender?.Tag} · " +
                   $"{battle.Attackers.Count}v{battle.Defenders.Count} · started day {battle.StartedDay:N0} · " +
                   $"round {battle.Rounds.Count}";
        }).Append(fighting.Length > 30 ? $"  …and {fighting.Length - 30:N0} more" : ""));
    }

    private string ConsoleOccupy(string argument)
    {
        if (_campaign?.Player is null || _campaign.ProvinceMap is null) return "No campaign.";
        var province = _selectedProvinceId;
        if (argument.Trim().Length > 0 && !int.TryParse(argument.Trim(), out province))
            return "Usage: occupy [PROVINCE_ID]";
        if (province <= 0 || province > _campaign.ProvinceMap.ProvinceCount)
            return "Select a province, or give its number.";
        if (!_campaign.SetProvinceController(province, (ushort)_campaign.Player.MapIndex))
            return $"Province #{province:N0} would not change hands.";
        _campaign.MarkMapChanged();
        Refresh();
        return $"Province #{province:N0} is now held by {_campaign.Player.Tag}.";
    }

    private string ConsoleAnnex(string argument)
    {
        if (_campaign?.Player is null || _campaign.ProvinceMap is null) return "No campaign.";
        var target = ConsoleNation(argument);
        if (target is null || target.Id == _campaign.Player.Id) return "Usage: annex LUX";
        var map = _campaign.ProvinceMap;
        var taken = new List<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == target.MapIndex) taken.Add(province);
        if (taken.Count == 0) return $"{target.Tag} holds no ground.";
        StatePartition.Annex(_campaign, taken, _campaign.Player, asConquest: true);
        _campaign.MarkMapChanged();
        Refresh();
        return $"{_campaign.Player.Tag} annexed {taken.Count:N0} province(s) from {target.Name}.";
    }

    // =====================================================================================
    //  The divisions
    // =====================================================================================

    private string ConsoleOrders()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var chosen = _selectedArmies.Count > 0
            ? _selectedArmies.ToArray()
            : _campaign.Armies.Where(army => army.NationId == _campaign.Player.Id).Take(12).ToArray();
        if (chosen.Length == 0) return "No divisions to report on.";

        return string.Join("\n", chosen.Select(army =>
        {
            var since = army.OrderIssuedDay <= -9000
                ? "never given one"
                : $"{_campaign.ElapsedDays - army.OrderIssuedDay:N0} day(s) ago";
            var progress = army.OrderRouteSteps > 0
                ? $"{army.OrderStepsCompleted}/{army.OrderRouteSteps} steps"
                : "no route";
            var attempt = army.LastMoveResultDay < 0
                ? "never tried to move"
                : $"{army.LastMoveResult} on day {army.LastMoveResultDay:N0}";
            var state = army.InBattle ? "in battle" :
                army.CutOff ? "cut off" :
                army.IsMoving ? "marching" :
                army.Recovering > 0f ? "recovering" : "standing";
            return EscapeBb(
                $"  {army.Name} · {state} · province {army.ProvinceId:N0}\n" +
                $"      order {army.OrderKind} -> province {army.OrderProvinceId:N0}, issued {since}\n" +
                $"      {progress} · queued {army.Route.Count} · planned {army.PlannedRoute.Count} · " +
                $"last attempt: {attempt}\n" +
                $"      org {army.Organisation:P0} · strength {army.Strength:P0} · supply {army.SupplyLevel:P0} · " +
                $"stance {army.Stance}");
        }));
    }

    private string ConsoleSpawn(string argument)
    {
        if (_campaign?.Player is null || _campaign.ProvinceMap is null) return "No campaign.";
        var words = ConsoleWords(argument);
        var count = words.Length > 0 && int.TryParse(words[0], out var parsed) ? parsed : 1;
        if (count is < 1 or > 50) return "Usage: spawn 3 [template] - between 1 and 50.";
        var templateId = words.Length > 1 ? words[1] : "line_infantry";
        var template = UnitTypeCatalog.All.FirstOrDefault(unit =>
            string.Equals(unit.Id, templateId, StringComparison.OrdinalIgnoreCase));
        if (template is null)
            return $"No template '{templateId}'. Known: " +
                   string.Join(", ", UnitTypeCatalog.All.Select(unit => unit.Id));

        var nation = _campaign.Player;
        var province = RecruitmentSystem.DeploymentProvince(_campaign, nation.Id, nation.CapitalStateMapIndex);
        if (province <= 0) return "No controlled province in the capital region to put them in.";
        var position = _campaign.ProvinceMap.CenterOf(province);
        var nextId = _campaign.Armies.Count == 0 ? 1 : _campaign.Armies.Max(army => army.Id) + 1;
        var serial = _campaign.Armies.Count(army => army.NationId == nation.Id) + 1;
        for (var index = 0; index < count; index++)
            _campaign.Armies.Add(new Army
            {
                Id = nextId + index, NationId = nation.Id,
                StateMapIndex = nation.CapitalStateMapIndex, ProvinceId = province,
                Name = $"{serial + index}. {nation.Tag} Division", TemplateId = template.Id,
                Divisions = 1, Personnel = template.RecruitmentPersonnel,
                MaxPersonnel = template.RecruitmentPersonnel,
                Organisation = 1f, Strength = 1f, Equipment = 1f, Experience = 0.10f,
                Cohesion = 1f, SupplyLevel = 1f, OrderKind = ArmyOrder.Manual, PlanActive = false,
                Latitude = position.Latitude, Longitude = position.Longitude
            });
        _campaign.MarkStrategicChange();
        Refresh();
        return $"Raised {count} {template.Id} division(s) at {nation.CapitalName}, province #{province:N0}.";
    }

    private string ConsoleHeal(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var all = argument.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        var chosen = all
            ? _campaign.Armies.Where(army => army.NationId == _campaign.Player.Id).ToArray()
            : _selectedArmies.Where(army => army.NationId == _campaign.Player.Id).ToArray();
        if (chosen.Length == 0) return "Select divisions, or use `heal all`.";
        foreach (var army in chosen)
        {
            army.Personnel = army.MaxPersonnel;
            army.Organisation = 1f;
            army.Strength = 1f;
            army.Equipment = 1f;
            army.Cohesion = 1f;
            army.SupplyLevel = 1f;
            army.Recovering = 0f;
        }
        _campaign.MarkStrategicChange();
        Refresh();
        return $"Filled {chosen.Length:N0} division(s) back up.";
    }

    private string ConsoleKill(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var all = argument.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        var doomed = all
            ? _campaign.Armies.Where(army => army.NationId == _campaign.Player.Id).ToList()
            : _selectedArmies.Where(army => army.NationId == _campaign.Player.Id).ToList();
        if (doomed.Count == 0) return "Select divisions, or use `kill all`.";
        ClearArmySelectionAndClose();
        foreach (var army in doomed) _campaign.Armies.Remove(army);
        _campaign.MarkStrategicChange();
        Refresh();
        return $"Removed {doomed.Count:N0} division(s).";
    }

    private string ConsoleGroups()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var groups = _campaign.ArmyGroups.Where(group => group.NationId == _campaign.Player.Id).ToArray();
        if (groups.Length == 0) return $"{_campaign.Player.Tag} has no army headquarters.";
        return string.Join("\n", groups.Select(group =>
        {
            var under = _campaign.Armies.Count(army => army.ArmyGroupId == group.Id);
            return EscapeBb($"  #{group.Id} {group.Name} · {under:N0} division(s)");
        }));
    }

    private string ConsoleFleets(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        var fleets = _campaign.Fleets.Where(fleet => fleet.NationId == nation.Id).ToArray();
        if (fleets.Length == 0) return $"{nation.Tag} has no fleets.";
        return string.Join("\n", fleets.Take(20).Select(fleet =>
            EscapeBb($"  {fleet.Name} · {fleet.Hulls:N0} hull(s) · " +
                     $"{fleet.Latitude:0.0}, {fleet.Longitude:0.0} · {fleet.Mission}")));
    }

    // =====================================================================================
    //  The map
    // =====================================================================================

    private string ConsoleGoto(string argument)
    {
        if (_campaign is null || _globe is null) return "No campaign.";
        var text = argument.Trim();
        if (text.Length == 0) return "Usage: goto bavaria";

        var state = ConsoleState(text);
        if (state is not null)
        {
            var owner = _campaign.Nations.FirstOrDefault(nation => nation.MapIndex == state.NationMapIndex);
            _selectedState = state;
            if (owner is not null) _selectedNation = owner;
            var seat = _campaign.ProvinceMap is null ? (0f, 0f) : StateCentre(state);
            _globe.FocusOn(Math.Clamp(seat.Item1, -89f, 89f), seat.Item2, 9f);
            Refresh();
            return EscapeBb($"{state.Name} · {owner?.Tag ?? "unowned"} · {Fmt.People(state.Population)}");
        }

        var nation = ConsoleNation(text);
        if (nation is null) return $"Nothing called '{EscapeBb(text)}' on the map.";
        _selectedNation = nation;
        _globe.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude, 8f);
        Refresh();
        return EscapeBb($"{nation.Name} ({nation.Tag}) · capital {nation.CapitalName}");
    }

    /// <summary>Roughly where a region is, by averaging the provinces that make it up.</summary>
    private (float, float) StateCentre(ScenarioState state)
    {
        var map = _campaign.ProvinceMap;
        if (map is null) return (0f, 0f);
        double latitude = 0, longitude = 0;
        var counted = 0;
        for (var province = 1; province <= map.ProvinceCount && counted < 400; province++)
        {
            if (map.ProvinceToState[province] != state.MapIndex) continue;
            var centre = map.CenterOf(province);
            latitude += centre.Latitude;
            longitude += centre.Longitude;
            counted++;
        }
        return counted == 0 ? (0f, 0f) : ((float)(latitude / counted), (float)(longitude / counted));
    }

    private string ConsoleRegion(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var state = ConsoleState(argument);
        if (state is null) return "Select a region, or name one: region bavaria";
        var owner = _campaign.Nations.FirstOrDefault(nation => nation.MapIndex == state.NationMapIndex);
        var built = state.Buildings.Count == 0
            ? "nothing built"
            : string.Join(", ", state.Buildings.Select(entry => $"{entry.Key} ×{entry.Value}"));
        var building = _campaign.Construction.Count(project => project.StateMapIndex == state.MapIndex);
        var pressure = owner is null ? 0f : SecessionCore.Pressure(_campaign, owner, state);
        return EscapeBb(
            $"{state.Name} ({state.Iso}) · map index {state.MapIndex} · {owner?.Name ?? "unowned"}\n" +
            $"  people     {Fmt.People(state.Population)} · urban {state.Urbanization:P0} · " +
            $"literacy {state.Literacy:P0} · living {state.StandardOfLiving:P0}\n" +
            $"  temper     stability {state.Stability:P0} · radicalism {state.Radicalism:P0} · " +
            $"secession pressure {pressure:P0}\n" +
            $"  built      {built}{(building > 0 ? $" · {building} under construction" : "")}\n" +
            $"  groups     {state.PopulationGroups.Count:N0} population group(s) - `pops` breaks them down");
    }

    private string ConsoleBuild(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var state = _selectedState;
        if (state is null || state.NationMapIndex != _campaign.Player.MapIndex)
            return "Select one of your own regions first.";
        var words = ConsoleWords(argument);
        if (words.Length == 0)
            return "Usage: build agriculture [3]. Kinds: " +
                   string.Join(", ", Enum.GetNames<BuildingKind>());
        if (!Enum.TryParse<BuildingKind>(words[0], true, out var kind))
            return $"Unknown building. Kinds: {string.Join(", ", Enum.GetNames<BuildingKind>())}";
        var count = words.Length > 1 && int.TryParse(words[1], out var parsed) ? Math.Clamp(parsed, 1, 12) : 1;

        // Queued through the same command the interface uses, so this tests the real path
        // rather than a private one that might behave differently.
        for (var index = 0; index < count; index++)
            _campaign.Commands.Enqueue(new QueueConstructionCommand
            {
                IssuerId = _campaign.Player.Id, StateMapIndex = state.MapIndex, Building = kind
            });
        return $"Queued {count} × {kind} in {EscapeBb(state.Name)}. It starts on the next step.";
    }

    private string ConsoleBuildings(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var state = ConsoleState(argument);
        if (state is null) return "Select a region, or name one.";
        var projects = _campaign.Construction
            .Where(project => project.StateMapIndex == state.MapIndex)
            .Select(project => $"    building {project.Building} · {project.Progress:P0} · " +
                               $"{project.ProgressDays}/{project.DurationDays} day(s)")
            .ToArray();
        var standing = state.Buildings.Count == 0
            ? "    nothing built"
            : string.Join("\n", state.Buildings.Select(entry => $"    {entry.Key} ×{entry.Value}"));
        return EscapeBb($"{state.Name}\n{standing}" +
                        (projects.Length == 0 ? "" : "\n" + string.Join("\n", projects)));
    }

    // =====================================================================================
    //  The economy and the neighbours
    // =====================================================================================

    private string ConsoleMarket(string argument)
    {
        var nation = ConsoleNation("");
        if (nation is null || _campaign is null) return "No campaign.";
        var filter = argument.Trim();
        var quotes = nation.Market.Values
            .Where(quote => filter.Length == 0 ||
                            quote.Commodity.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(quote => quote.Satisfaction)
            .ToArray();
        if (quotes.Length == 0) return $"{nation.Tag} has no market lines matching '{EscapeBb(filter)}'.";
        return EscapeBb($"{nation.Tag} · market access {nation.MarketAccess:P0} · " +
                        $"shortage index {nation.ShortageIndex:0.00}\n" +
               string.Join("\n", quotes.Take(24).Select(quote =>
                   $"  {quote.Commodity,-20} price {quote.Price,7:0.00} · met {quote.Satisfaction,6:P0} · " +
                   $"make {quote.Production,7:0.0} want {quote.Demand,7:0.0} " +
                   $"in {quote.Imports,6:0.0} out {quote.Exports,6:0.0}" +
                   (quote.IsShortage ? "  SHORT" : ""))));
    }

    private string ConsoleTrade(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        var partners = nation.TradePartners
            .OrderByDescending(entry => entry.Value)
            .Take(12)
            .Select(entry =>
            {
                var other = _campaign.Nations.FirstOrDefault(candidate => candidate.Id == entry.Key);
                var dependence = nation.TradeDependenceOn(entry.Key);
                return $"  {other?.Tag ?? entry.Key.ToString()} · ${entry.Value:N1}B · {dependence:P0} of its trade";
            })
            .ToArray();
        return EscapeBb($"{nation.Tag} · policy {nation.TradePolicy} · exports ${nation.ExportValue:N1}B · " +
                        $"imports ${nation.ImportValue:N1}B · balance {nation.WeeklyTradeBalance:+0.00;-0.00;0.00}B\n" +
                        (partners.Length == 0 ? "  no partners" : string.Join("\n", partners)));
    }

    private string ConsoleRelations(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        var rows = _campaign.Nations
            .Where(other => other.Id != nation.Id)
            .Select(other => (Other: other, Relation: CampaignDiplomacy.Get(_campaign, nation.Id, other.Id)))
            .Where(pair => pair.Relation is not null && MathF.Abs(pair.Relation.Opinion) > 0.5f)
            .OrderByDescending(pair => pair.Relation.Opinion)
            .ToArray();
        if (rows.Length == 0) return $"Nobody has an opinion about {nation.Tag} worth the name.";

        string Row((Nation Other, DiplomaticRelation Relation) pair)
        {
            var marks = new List<string>();
            if (pair.Relation.TradeAgreement) marks.Add("trade");
            if (pair.Relation.NonAggressionPact) marks.Add("pact");
            if (pair.Relation.GuaranteeByA || pair.Relation.GuaranteeByB) marks.Add("guarantee");
            if (pair.Relation.EmbargoByA || pair.Relation.EmbargoByB) marks.Add("embargo");
            if (CampaignDiplomacy.AtWar(_campaign, pair.Other.Id, nation.Id)) marks.Add("AT WAR");
            var reason = pair.Relation.Reasons.Count == 0
                ? ""
                : " · " + string.Join(", ", pair.Relation.Reasons.Take(2).Select(item => item.Id));
            return $"  {pair.Other.Tag} {pair.Relation.Opinion,7:+0.0;-0.0;0.0}" +
                   (marks.Count == 0 ? "" : "  [" + string.Join(" ", marks) + "]") + reason;
        }

        return EscapeBb($"{nation.Tag}: warmest and coldest of {rows.Length:N0} opinion(s)\n" +
                        string.Join("\n", rows.Take(8).Select(Row)) + "\n  …\n" +
                        string.Join("\n", rows.TakeLast(8).Select(Row)));
    }

    private string ConsoleAlliances(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var nation = ConsoleNation(argument);
        if (nation is null) return "No such country.";

        var lines = new List<string>();
        foreach (var ally in AllianceSystem.AlliesOf(_campaign, nation))
        {
            var relation = CampaignDiplomacy.Get(_campaign, nation.Id, ally.Id);
            var since = relation.AllianceSinceDay > -9000
                ? $", since day {relation.AllianceSinceDay:N0}"
                : "";
            lines.Add($"  ally · {ally.Tag} {EscapeBb(ally.Name)} · opinion {relation.Opinion:+0;-0;0}{since}");
        }
        foreach (var other in _campaign.Nations)
        {
            if (other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(_campaign, nation.Id, other.Id);
            if (relation is null) continue;
            if (relation.GuaranteedBy(nation.Id))
                lines.Add($"  guarantees · {other.Tag} {EscapeBb(other.Name)}");
            if (relation.GuaranteedBy(other.Id))
                lines.Add($"  guaranteed by · {other.Tag} {EscapeBb(other.Name)}");
            if (relation.NonAggressionPact)
                lines.Add($"  pact · {other.Tag} {EscapeBb(other.Name)}");
        }

        var offered = AllianceSystem.OfferedTo(_campaign, nation);
        if (offered is not null)
            lines.Insert(0, $"  ON THE TABLE · {offered.Tag} {EscapeBb(offered.Name)} has offered an alliance. " +
                            "`ally " + offered.Tag + "` accepts it.");
        return lines.Count == 0
            ? $"{nation.Tag} has signed nothing with anybody."
            : string.Join("\n", lines);
    }

    private string ConsoleAlly(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var target = ConsoleNation(argument);
        if (target is null) return "Usage: ally FRA";

        // Accepting what is already on the table is the same word as asking, because from
        // the player's side it is the same intention.
        var offered = AllianceSystem.OfferedTo(_campaign, _campaign.Player);
        if (offered is not null && offered.Id == target.Id)
        {
            AllianceSystem.Answer(_campaign, _campaign.Player, true, out var outcome);
            Refresh();
            return EscapeBb(outcome);
        }

        var why = new Breakdown();
        var willingness = AllianceSystem.Willingness(_campaign, target, _campaign.Player, why);
        var signed = AllianceSystem.Propose(_campaign, _campaign.Player, target, out var answer);
        Refresh();
        var parts = new List<string> { EscapeBb(answer) };
        if (!signed && why.HasEntries)
        {
            parts.Add($"  willingness {willingness:0.00} against {_campaign.Defines.AllianceWillingness:0.00} needed:");
            foreach (var entry in why.Entries)
                parts.Add($"    {EscapeBb(entry.Source)} {entry.Value:+0.00;-0.00;0.00}");
        }
        return string.Join("\n", parts);
    }

    private string ConsoleUnally(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var target = ConsoleNation(argument);
        if (target is null) return "Usage: unally FRA";
        if (!AllianceSystem.Break(_campaign, _campaign.Player, target, out var refusal))
            return $"Refused: {EscapeBb(refusal)}";
        Refresh();
        return $"{_campaign.Player.Tag} is no longer allied to {EscapeBb(target.Name)}.";
    }

    private string ConsoleGuarantee(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var words = ConsoleWords(argument);
        if (words.Length == 0) return "Usage: guarantee LUX [off]";
        var target = ConsoleNation(words[0]);
        if (target is null) return $"No country called '{EscapeBb(words[0])}'.";
        var on = words.Length < 2 || !string.Equals(words[1], "off", StringComparison.OrdinalIgnoreCase);
        new GuaranteeCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id, Guarantee = on }
            .Apply(_campaign);
        Refresh();
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, target.Id);
        return relation.GuaranteedBy(_campaign.Player.Id)
            ? $"{_campaign.Player.Tag} guarantees {EscapeBb(target.Name)}."
            : $"{_campaign.Player.Tag} no longer guarantees {EscapeBb(target.Name)}.";
    }

    private string ConsoleSubjects(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var nation = ConsoleNation(argument);
        if (nation is null) return "No such country.";

        var lines = new List<string>();
        var mine = SubjectSystem.SubjectsOf(_campaign, nation);
        foreach (var subject in mine)
        {
            var kind = SubjectCatalog.Of(subject.SubjectType);
            var held = _campaign.ElapsedDays - subject.SubjectSinceDay;
            var grace = kind is not null && held < kind.GraceDays
                ? $", settling for another {kind.GraceDays - held:N0} day(s)"
                : "";
            lines.Add($"  {subject.Tag} {EscapeBb(subject.Name)} · {kind?.Name ?? subject.SubjectType} · " +
                      $"wants out {subject.LibertyDesire:P0} of the way " +
                      $"(pull {SubjectSystem.LibertyDesireOf(_campaign, subject):P0}){grace}");
        }

        var over = SubjectSystem.OverlordOf(_campaign, nation);
        if (over is not null)
            lines.Insert(0, $"  {nation.Tag} itself answers to {over.Tag} as a " +
                            $"{SubjectCatalog.Of(nation.SubjectType)?.Name ?? nation.SubjectType}.");
        if (lines.Count == 0)
            return $"{nation.Tag} holds nobody and answers to nobody. " +
                   $"Kinds available: {string.Join(", ", SubjectCatalog.All.Select(kind => kind.Id))}";
        return string.Join("\n", lines);
    }

    private string ConsoleSubjugate(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var words = ConsoleWords(argument);
        if (words.Length == 0)
            return "Usage: puppet LUX [kind]. Kinds: " +
                   string.Join(", ", SubjectCatalog.All.Select(kind => kind.Id));
        var target = ConsoleNation(words[0]);
        if (target is null) return $"No country called '{EscapeBb(words[0])}'.";
        var kindId = words.Length > 1 ? words[1] : "puppet";
        if (!SubjectSystem.Subjugate(_campaign, _campaign.Player, target, kindId, out var refusal))
            return $"Refused: {EscapeBb(refusal)}";
        Refresh();
        var kind = SubjectCatalog.Of(kindId);
        return $"{target.Name} answers to {_campaign.Player.Tag} as a {kind?.Name ?? kindId}. " +
               $"Tribute {kind?.Tribute:P0} of its taxes; flag {FlagCatalog.Resolve(_campaign, target)}.";
    }

    private string ConsoleRelease(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var target = ConsoleNation(argument);
        if (target is null || !target.IsSubject)
            return "Usage: release LUX - and it has to answer to somebody.";
        var was = _campaign.NationById(target.OverlordId);
        SubjectSystem.Release(_campaign, target);
        Refresh();
        return $"{target.Name} no longer answers to {was?.Tag ?? "anybody"}.";
    }

    private string ConsoleIntegrate(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var target = ConsoleNation(argument);
        if (target is null) return "Usage: integrate LUX";
        if (!SubjectSystem.Annex(_campaign, _campaign.Player, target, out var refusal))
            return $"Refused: {EscapeBb(refusal)}";
        Refresh();
        return $"{target.Name} is part of {_campaign.Player.Name} now.";
    }

    private string ConsoleOpinion(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var words = ConsoleWords(argument);
        if (words.Length < 2 || !TryFloat(words[1], out var opinion)) return "Usage: opinion RUS -100";
        var other = ConsoleNation(words[0]);
        if (other is null) return $"No country called '{EscapeBb(words[0])}'.";
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, other.Id);
        if (relation is null) return "There is no relation between those two to set.";
        relation.Opinion = Math.Clamp(opinion, -200f, 200f);
        Refresh();
        return $"{other.Tag} now thinks {relation.Opinion:+0.0;-0.0;0.0} of {_campaign.Player.Tag}.";
    }

    // =====================================================================================
    //  The content
    // =====================================================================================

    private string ConsoleFlags(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        var candidates = FlagCatalog.Candidates(_campaign, nation);
        var resolved = FlagCatalog.Resolve(_campaign, nation);
        var lines = candidates.Take(24).Select(name =>
        {
            var exists = SpriteCatalog.Has(name);
            var mark = !exists ? " " : name == resolved ? ">" : "·";
            var colour = name == resolved ? Bb("state.positive") : exists ? Bb("text.body") : Bb("text.muted");
            return $"  {mark} {colour}{name}[/color]{(exists ? "" : "   (no such picture)")}";
        });
        return $"{nation.Tag} as {nation.Ideology} · {nation.Government}" +
               (nation.FormedNationId.Length > 0 ? $" · formed {nation.FormedNationId}" : "") + "\n" +
               $"flies {(resolved.Length == 0 ? "nothing - a plain rectangle" : resolved)}, " +
               $"chosen from {candidates.Count} candidate(s):\n" + string.Join("\n", lines);
    }

    private string ConsoleDefines(string argument)
    {
        if (_campaign?.Defines is null) return "No campaign, so no tunables to report.";
        var filter = argument.Trim();
        var rows = typeof(Defines).GetProperties()
            .Where(property => property.CanRead &&
                               (filter.Length == 0 ||
                                property.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Take(60)
            .Select(property =>
            {
                var value = property.GetValue(_campaign.Defines);
                return $"  {property.Name,-34} {value}";
            })
            .ToArray();
        return rows.Length == 0
            ? $"No define matches '{EscapeBb(filter)}'."
            : EscapeBb(string.Join("\n", rows));
    }

    private static string ConsoleSettings(string argument)
    {
        var filter = argument.Trim();
        var rows = SettingsCatalog.All
            .Where(setting => filter.Length == 0 ||
                              setting.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                              setting.Category.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(setting => setting.Category, StringComparer.Ordinal)
            .ThenBy(setting => setting.Order)
            .Take(50)
            .Select(setting => $"  {setting.Category,-12} {setting.Id,-28} {setting.Kind} = " +
                               $"{SettingsCatalog.Chosen(setting.Id)}")
            .ToArray();
        return rows.Length == 0
            ? $"No setting matches '{EscapeBb(filter)}'."
            : EscapeBb(string.Join("\n", rows));
    }

    private string ConsoleDecisions()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var nation = _campaign.Player;
        var rows = DecisionCatalog.All.Select(definition =>
        {
            var blocked = DecisionCatalog.BlockReason(_campaign, nation, definition);
            var cost = DecisionCatalog.Cost(nation, definition);
            return blocked.Length == 0
                ? $"  {Bb("state.positive")}{definition.Id}[/color]  ${cost:0.00}B"
                : $"  {Bb("text.muted")}{definition.Id}[/color]  ${cost:0.00}B  - {EscapeBb(blocked)}";
        }).ToArray();
        return rows.Length == 0 ? "The decision catalogue is empty." : string.Join("\n", rows);
    }

    private string ConsoleDecision(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var id = argument.Trim();
        var found = DecisionCatalog.Find(id);
        if (found is null) return $"No decision called '{EscapeBb(id)}'. Try `decisions`.";
        var definition = found.Value;
        var blocked = DecisionCatalog.BlockReason(_campaign, _campaign.Player, definition);
        if (blocked.Length > 0) return $"Blocked: {EscapeBb(blocked)}";
        _campaign.Commands.Enqueue(new TakeDecisionCommand
        {
            IssuerId = _campaign.Player.Id, DecisionId = definition.Id
        });
        return $"Enacted '{definition.Id}'. It applies on the next step.";
    }

    private static string ConsoleEvents(string argument)
    {
        var filter = argument.Trim();
        var rows = EventCatalog.All
            .Where(definition => filter.Length == 0 ||
                                 definition.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                 definition.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .Select(definition => $"  {definition.Id,-34} {definition.Title}")
            .ToArray();
        return rows.Length == 0
            ? $"No event matches '{EscapeBb(filter)}'."
            : EscapeBb($"{EventCatalog.All.Count} event(s) in the catalogue:\n" + string.Join("\n", rows));
    }

    private string ConsoleFormable()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var nation = _campaign.Player;
        var rows = FormableNations.All.Select(definition =>
        {
            var eligible = FormableNations.Eligible(nation, definition);
            var mark = eligible ? Bb("state.positive") + ">" : Bb("text.muted") + " ";
            return $"  {mark} {definition.Id}[/color] · {EscapeBb(definition.Name)}";
        }).ToArray();
        return rows.Length == 0
            ? "No formable states are declared."
            : $"{nation.Tag} against {rows.Length} formable state(s):\n" + string.Join("\n", rows);
    }

    private string ConsoleSecession(string argument)
    {
        var nation = ConsoleNation(argument);
        if (nation is null || _campaign is null) return "No such country.";
        var rows = _campaign.StatesOf(nation)
            .Select(state => (State: state, Pressure: SecessionCore.Pressure(_campaign, nation, state)))
            .Where(row => row.Pressure > 0.01f)
            .OrderByDescending(row => row.Pressure)
            .Take(20)
            .Select(row => $"  {row.Pressure,6:P0}  {row.State.Name} · " +
                           $"radicalism {row.State.Radicalism:P0} · {Fmt.People(row.State.Population)}")
            .ToArray();
        return rows.Length == 0
            ? $"Nothing in {nation.Tag} is pulling away."
            : EscapeBb($"{nation.Tag}: regions by how hard they are pulling away\n" + string.Join("\n", rows));
    }

    private string ConsoleReload()
    {
        if (_campaign is null) return "No campaign.";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var errors = PresentationReload.Reload(_modLoader.Packages.Select(package => package.PhysicalRoot));
        clock.Stop();
        RepaintPresentation(this);
        RefreshLocalisedText();
        LayoutCampaignShell();
        Refresh();
        return $"Reloaded interface, GUI and localization in {clock.ElapsedMilliseconds:N0} ms. " +
               "Campaign state and package selection unchanged. Standard panels, buttons and captions updated in place.\n" +
               (errors.Count == 0 ? "No loading errors." :
                   EscapeBb($"{errors.Count} loading error(s); valid files applied, invalid values skipped:\n" +
                       string.Join("\n", errors.Take(10))));
    }

    private string ConsoleFeature(string arguments)
    {
        if (_campaign is null) return "No campaign.";
        var words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
            return EscapeBb(string.Join("\n", _campaign.Defines.Features.Keys.Concat(_campaign.FeatureOverrides.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id)
                .Select(id => $"{id}: {(_campaign.FeatureEnabled(id) ? "on" : "off")}")));
        if (words.Length == 1) return EscapeBb($"{words[0]}: {(_campaign.FeatureEnabled(words[0]) ? "on" : "off")}");
        if (words.Length != 2 || (words[1] != "on" && words[1] != "off")) return "Usage: feature aviation on|off";
        if (_networkConnected) return "Mechanic switches must be changed by deterministic scenario scripts in multiplayer.";
        if (!_campaign.SetFeature(words[0], words[1] == "on")) return "Invalid mechanic id.";
        Refresh();
        return EscapeBb($"{words[0]}: {(_campaign.FeatureEnabled(words[0]) ? "on" : "off")}");
    }

    // Bindings belong to the shared UI factories. Do not rebuild the scene or replay
    // initial conditions; keep selections, scroll positions and semantic colour overrides.
    private void RepaintPresentation(Node node)
    {
        if (node.IsQueuedForDeletion()) return;
        if (node is Control control && node.HasMeta("skin_layout"))
            Ui.Place(control, node.GetMeta("skin_layout").AsString());
        if (node is PanelContainer panel && node.HasMeta("skin_panel_surface"))
        {
            var surface = (PanelSurface)node.GetMeta("skin_panel_surface").AsInt32();
            panel.AddThemeStyleboxOverride("panel", node.GetMeta("skin_campaign_surface").AsBool()
                ? CampaignPanelStyle(surface) : PanelStyle(surface));
        }
        if (node is Button button && node.HasMeta("skin_button_primary"))
        {
            var colour = button.GetThemeColor("font_color");
            var custom = node.HasMeta("skin_button_applied") &&
                !colour.IsEqualApprox(node.GetMeta("skin_button_applied").AsColor());
            StyleButton(button, node.GetMeta("skin_button_primary").AsBool());
            if (custom) button.AddThemeColorOverride("font_color", colour);
        }
        if (node is Label label && node.HasMeta("skin_label_colour") &&
            label.GetThemeColor("font_color").IsEqualApprox(node.GetMeta("skin_label_applied").AsColor()))
        {
            var colour = Ui.Colour(node.GetMeta("skin_label_colour").AsString());
            label.AddThemeColorOverride("font_color", colour);
            label.SetMeta("skin_label_applied", colour);
        }
        foreach (var child in node.GetChildren()) RepaintPresentation(child);
    }

    private string ConsoleValidate()
    {
        var packages = _modLoader.Packages.Where(package => package.Errors.Count > 0).ToArray();
        if (packages.Length == 0)
            return $"{_modLoader.Packages.Count} package(s) loaded and none of them complained.";
        return EscapeBb(string.Join("\n", packages.Select(package =>
            $"  {package.Manifest?.Name ?? package.VirtualRoot} · {package.Errors.Count} complaint(s)\n" +
            string.Join("\n", package.Errors.Take(10).Select(error => $"      {error}")))));
    }

    // =====================================================================================
    //  The machine
    // =====================================================================================

    private static string ConsoleFps() =>
        $"{Engine.GetFramesPerSecond()} fps · process {_ProcessTimeMs():0.00} ms · " +
        $"physics {Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0:0.00} ms\n" +
        $"draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):N0} · " +
        $"primitives {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):N0} · " +
        $"video memory {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0:N0} MB\n" +
        $"objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):N0} · " +
        $"nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):N0}";

    private static string ConsoleMemory(string argument)
    {
        var before = GC.GetTotalMemory(false);
        var collected = "";
        if (argument.Trim().Equals("gc", StringComparison.OrdinalIgnoreCase))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            collected = $" · freed {(before - GC.GetTotalMemory(true)) / 1048576.0:N1} MB";
        }
        return $"managed heap {GC.GetTotalMemory(false) / 1048576.0:N1} MB{collected}\n" +
               $"collections: gen0 {GC.CollectionCount(0):N0} · gen1 {GC.CollectionCount(1):N0} · " +
               $"gen2 {GC.CollectionCount(2):N0}\n" +
               $"Godot static memory {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:N1} MB. " +
               "`mem gc` forces a collection.";
    }

    private static string ConsoleLogs() =>
        $"Logging: {(DiagnosticLogs.Enabled ? "active" : "inactive")}\n" +
        $"Folder: {DiagnosticLogs.DirectoryPath}\n" +
        $"Written lines: {DiagnosticLogs.WrittenLines}\n" +
        $"Last failure: {DiagnosticLogs.LastFailure}";

    private string ConsoleSaves()
    {
        var saves = SaveSystem.List(SaveRoot);
        return saves.Count == 0
            ? $"No saves in {SaveRoot}."
            : EscapeBb(string.Join("\n", saves.Take(20).Select(save =>
                $"  {save.Slot,-24} {save.NationTag} · {save.GameDate} · day {save.ElapsedDays:N0} · " +
                $"{save.SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}")));
    }

    private string ConsoleSave(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var slot = argument.Trim();
        if (slot.Length == 0) slot = $"console_{_campaign.Player.Tag}_{_campaign.ElapsedDays}";
        var metadata = SaveSystem.Save(SaveRoot, slot, _campaign, ModFingerprint, slot);
        return $"Saved to '{metadata.Slot}' at {metadata.GameDate}.";
    }

    private string ConsoleLoad(string argument)
    {
        var slot = argument.Trim();
        if (slot.Length == 0) return "Usage: load SLOT - `saves` lists them.";
        if (SaveSystem.List(SaveRoot).All(save =>
                !string.Equals(save.Slot, slot, StringComparison.OrdinalIgnoreCase)))
            return $"No save called '{EscapeBb(slot)}'. `saves` lists them.";
        CloseDeveloperConsole();
        _ = RestoreCampaignFromSlot(slot);
        return "";
    }
}
