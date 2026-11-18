using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// Developer console commands for campaign clock, player tag/observer state, country stats,
/// population, economy, buildings, technology, and central banking.
/// </summary>
internal static class ConsoleWorldCommands
{
    public static void Register(ConsoleContext ctx, RegisterConsoleCommand add)
    {
        // TIME
        add("pause", "TIME", "pause", "Stop and start the clock.", _ => Pause(ctx));
        add("speed", "TIME", "speed 1-5", "How fast the clock runs while it is running.", a => Speed(ctx, a));
        add("date", "TIME", "date", "The date, the hour, and how long the campaign has run.", _ => Date(ctx));
        add("skip", "TIME", "skip 30 · skip 2w · skip 1y",
            "Run days immediately, without watching them. The fastest way to find out what a " +
            "change does over a year. Days, weeks, months or years; ten years is the ceiling.",
            a => Skip(ctx, a), "run", "advance");
        add("fingerprint", "TIME", "fingerprint",
            "One number for the whole world state. Two machines that disagree about a " +
            "campaign disagree about this first; it is what desync is diagnosed with.",
            _ => Fingerprint(ctx), "hash", "sync");
        add("step", "TIME", "step [HOURS]",
            "How much campaign time one simulation step covers, and set it live. One hour is " +
            "the shipped pace; the answer does not change with it, only the grain.",
            a => Step(ctx, a), "tick");
        add("advance", "TIME", "advance [DAYS]",
            "Advance whole campaign days without starting the clock, whatever the step length is.",
            a => Advance(ctx, a), "day", "days");

        // PLAY
        add("tag", "PLAY", "tag DEU", "Become another country.", a => Tag(ctx, a), "play");
        add("observe", "PLAY", "observe", "Hand every country to the AI and watch.", _ => Observe(ctx));
        add("find", "PLAY", "find korea", "Countries whose name or tag matches, with their tags.",
            a => Find(ctx, a), "search");

        // COUNTRY
        add("country", "COUNTRY", "country [DEU]",
            "Everything a country is, on one screen: politics, money, war, people, army.",
            a => Country(ctx, a), "nation", "dossier");
        add("money", "COUNTRY", "money 500", "Add to the treasury, in billions. Negative takes it away.",
            a => Money(ctx, a), "cash", "treasury");
        add("manpower", "COUNTRY", "manpower 100000", "Add to the manpower reserve.", a => Manpower(ctx, a));
        add("stability", "COUNTRY", "stability 75", "Set stability, as a percentage.", a => Stability(ctx, a));
        add("warsupport", "COUNTRY", "warsupport 80", "Set war support, as a percentage.", a => WarSupport(ctx, a));
        add("notoriety", "COUNTRY", "notoriety 0", "Set how much the world holds against you.",
            a => Notoriety(ctx, a));
        add("literacy", "COUNTRY", "literacy 90", "Set literacy, as a percentage.", a => Literacy(ctx, a));
        add("politics", "COUNTRY", "politics communism one_party_state",
            "Change ideology and form of government at once. Either may be given alone. " +
            "This is the quick way to see a country's other flags.",
            a => Politics(ctx, a), "regime");
        add("budget", "COUNTRY", "budget [tax|military|welfare|education] [0-100]",
            "The four rates, or set one of them.", a => Budget(ctx, a), "spending");
        add("govfocus", "COUNTRY", "govfocus nationalsecurity",
            "What the cabinet is working on.", a => GovernmentFocusCmd(ctx, a), "cabinet");
        add("tradepolicy", "COUNTRY", "tradepolicy balancedtrade",
            "How open the country is to trade.", a => TradePolicyCmd(ctx, a));
        add("modifier", "COUNTRY", "modifier ID [days] · modifier -ID",
            "Grant a modifier from common/modifiers, or take one away with a leading minus.",
            a => Modifier(ctx, a));
        add("modifiers", "COUNTRY", "modifiers [DEU]",
            "What is currently acting on a country, and when each of it lapses.",
            a => Modifiers(ctx, a));

        // PEOPLE
        add("pops", "PEOPLE", "pops [region name]",
            "A region's people three ways: by trade, by class, and by sector. The selected " +
            "region unless one is named.", a => Pops(ctx, a), "population");
        add("migration", "PEOPLE", "migration [DEU]",
            "Brain drain, talent inflow and regional migration balance for a country.",
            a => Migration(ctx, a), "braindrain");

        // ECONOMY & MONEY
        add("economy", "ECONOMY", "economy",
            "Whether the emergent economy still reproduces the authored figures.", _ => Economy(ctx), "gdp");
        add("market", "ECONOMY", "market [oil]",
            "What a country makes, wants, buys and sells, and what it is short of.",
            a => Market(ctx, a), "goods", "prices");
        add("trade", "ECONOMY", "trade [DEU]",
            "Who a country trades with, how much, and how badly it would miss them.",
            a => Trade(ctx, a));
        add("build", "ECONOMY", "build agriculture [3]",
            "Queue building work in the selected region, without waiting for the money.",
            a => Build(ctx, a), "construct");
        add("instabuild", "ECONOMY", "instabuild [on|off] [TAG]",
            "Finish construction at once: whatever a country builds - through the ordinary build menu - " +
            "is standing the next day, free. No argument toggles it for your country.",
            a => Instabuild(ctx, a), "instantbuild");
        add("research", "ECONOMY", "research [ID|all] [TAG]",
            "Learn a technology at once, or every one. No argument lists what is still unknown.",
            a => Research(ctx, a), "tech");
        add("buildings", "ECONOMY", "buildings [region]",
            "What stands in a region, and what is being built there.",
            a => Buildings(ctx, a));
        add("inflation", "MONEY", "inflation [country]",
            "What prices are doing and why, line by line, with the key rate beside them.",
            a => Inflation(ctx, a), "prices");
        add("keyrate", "MONEY", "keyrate [PERCENT|seize|release]",
            "Read the central bank's key rate, take it off the bank, give it back, or set it " +
            "yourself once you have taken it - and see what the cheap money is costing.",
            a => KeyRate(ctx, a), "rate", "cb");
        add("emission", "MONEY", "emission [PERCENT|off]",
            "Have money printed, as a share of a year's output. Needs the bank taken first. " +
            "The money is real and arrives at once; what it costs is written in the prices.",
            a => Emission(ctx, a), "print", "printer");
    }

    public static string Tag(ConsoleContext ctx, string tag)
    {
        var nation = ctx.Campaign?.NationByTag(tag);
        if (nation is null) return "Usage: tag KOR — unknown country tag.";
        ctx.SetObserverMode(false);
        ctx.Campaign.ConfigureSinglePlayer(nation.Id);
        ctx.ClearArmySelectionAndClose();
        ctx.SetSelectedNation(nation);
        ctx.Globe?.FocusOn(nation.CapitalLatitude, nation.CapitalLongitude, 8f);
        ctx.Refresh();
        return $"Player country changed to {nation.Name} ({nation.Tag}).";
    }

    public static string Advance(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var text = (argument ?? "").Trim();
        var days = 1;
        if (text.Length > 0 && (!int.TryParse(text, out days) || days <= 0))
            return "Usage: advance 7";
        days = Math.Clamp(days, 1, 3650);

        var paused = campaign.IsPaused;
        campaign.IsPaused = false;
        var before = campaign.ElapsedDays;
        ctx.SetSimulating(true);
        try { for (var day = 0; day < days; day++) campaign.TickDay(); }
        finally { ctx.SetSimulating(false); }
        campaign.IsPaused = paused;
        return $"Advanced {campaign.ElapsedDays - before:N0} day(s) to {campaign.Date}. The clock is " +
               (paused ? "still stopped." : "still running.");
    }

    private static string Pause(ConsoleContext ctx)
    {
        ctx.TogglePause();
        return ctx.Campaign.IsPaused ? "Game paused." : "Game resumed.";
    }

    private static string Speed(ConsoleContext ctx, string value)
    {
        if (!int.TryParse(value, out var speed) || speed is < 1 or > 5) return "Usage: speed 1-5";
        ctx.SetSpeed(speed);
        return $"Game speed set to {speed}.";
    }

    private static string Date(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        return $"{campaign.Date.Display(true)} · day {campaign.ElapsedDays:N0} of the campaign · " +
               $"{campaign.TickHours} hour(s) a step · {(campaign.IsPaused ? "paused" : "running")}";
    }

    private static string Skip(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
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

        var paused = campaign.IsPaused;
        campaign.IsPaused = false;
        var from = campaign.ElapsedDays;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var steps = 0;
        var ceiling = days * 24 + 64;
        ctx.SetSimulating(true);
        try { while (campaign.ElapsedDays - from < days && steps++ < ceiling) campaign.Tick(); }
        finally { ctx.SetSimulating(false); }
        clock.Stop();
        campaign.IsPaused = paused;
        ctx.Refresh();

        var ran = campaign.ElapsedDays - from;
        var stalled = ran < days ? " The clock stopped advancing before it got there." : "";
        return $"Ran {ran:N0} day(s) in {clock.ElapsedMilliseconds:N0} ms " +
               $"({(ran == 0 ? 0 : clock.ElapsedMilliseconds / (double)ran):0.0} ms a day). " +
               $"Now {campaign.Date.Display(true)}.{stalled}";
    }

    private static string Fingerprint(ConsoleContext ctx) => ctx.Campaign is null
        ? "No campaign."
        : $"Fingerprint {CampaignFingerprint.Of(ctx.Campaign):X16} at day {ctx.Campaign.ElapsedDays:N0} " +
          $"({ctx.Campaign.Date}). Two clients that disagree here have diverged.";

    private static string Step(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var text = argument.Trim();
        if (text.Length > 0)
        {
            if (!int.TryParse(text, out var hours) || hours <= 0) return "Usage: step 1 | step 6 | step 24";
            campaign.Defines.HoursPerTick = Math.Clamp(hours, 1, 240);
        }
        return $"One step is {campaign.TickHours} hour(s), which is {24.0 / campaign.TickHours:0.##} " +
               $"step(s) a campaign day. Everything that accumulates takes {campaign.TickScale:0.###} of a " +
               "day's worth; everything that happens on a day runs once for each whole day crossed.";
    }

    private static string Observe(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        ctx.SetObserverMode(true);
        foreach (var nation in campaign.Nations) nation.IsHuman = false;
        campaign.HumanSeats.Clear();
        ctx.ClearArmySelectionAndClose();
        FrontPlanner.AssignAi(campaign);
        ctx.SetObserverIdentityBadge(ctx.Tr("Observer mode"));
        return "Observer mode enabled. Country AI controls every nation; use tag TAG to return.";
    }

    private static string Find(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var text = argument.Trim();
        if (text.Length == 0) return "Usage: find korea";
        var matches = campaign.Nations
            .Where(nation => nation.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                             nation.Tag.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(nation => nation.Population)
            .Take(20)
            .Select(nation => $"  {nation.Tag}  {ConsoleContext.EscapeBb(nation.Name)} · {Fmt.People(nation.Population)} · " +
                              $"{nation.Ideology} {nation.Government}")
            .ToArray();
        return matches.Length == 0
            ? $"No country matches '{ConsoleContext.EscapeBb(text)}'."
            : string.Join("\n", matches);
    }

    private static string Country(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var states = campaign.StatesOf(nation);
        var divisions = campaign.Armies.Count(army => army.NationId == nation.Id);
        var men = campaign.Armies.Where(army => army.NationId == nation.Id).Sum(army => army.Personnel);
        var wars = campaign.Wars.Where(war => !war.Ended && war.Involves(nation.Id)).ToArray();
        var flag = FlagCatalog.Resolve(campaign, nation);
        return ConsoleContext.EscapeBb(
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
            $"reserve {nation.ManpowerReserve:N0} · {campaign.Fleets.Count(fleet => fleet.NationId == nation.Id)} fleet(s)\n" +
            $"  war        {(wars.Length == 0 ? "at peace" : string.Join(", ", wars.Select(war => war.Name)))}\n" +
            $"  flag       {(flag.Length == 0 ? "none - drawn as a plain rectangle" : flag)}");
    }

    private static string Money(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || !ConsoleContext.TryFloat(value, out var amount)) return "Usage: money 100";
        campaign.Player.Treasury += amount;
        ctx.Refresh();
        return $"Treasury changed by ${amount:0.##}B.";
    }

    private static string Manpower(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || !ConsoleContext.TryFloat(value, out var amount)) return "Usage: manpower 100000";
        campaign.Player.ManpowerReserve = MathF.Max(0, campaign.Player.ManpowerReserve + amount);
        ctx.Refresh();
        return $"Manpower reserve changed by {amount:N0}.";
    }

    private static string Stability(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || !ConsoleContext.TryFloat(value, out var amount)) return "Usage: stability 75";
        campaign.Player.Stability = Math.Clamp(amount > 1f ? amount / 100f : amount, 0f, 1f);
        ctx.Refresh();
        return $"Stability set to {campaign.Player.Stability:P0}.";
    }

    private static string WarSupport(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || !ConsoleContext.TryShare(value, out var share)) return "Usage: warsupport 80";
        campaign.Player.WarSupport = share;
        ctx.Refresh();
        return $"War support set to {share:P0}.";
    }

    private static string Notoriety(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || !ConsoleContext.TryFloat(value, out var amount)) return "Usage: notoriety 0";
        campaign.Player.Notoriety = MathF.Max(0f, amount);
        ctx.Refresh();
        return $"Notoriety set to {campaign.Player.Notoriety:0}.";
    }

    private static string Literacy(ConsoleContext ctx, string value)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null || !ConsoleContext.TryShare(value, out var share)) return "Usage: literacy 90";
        campaign.Player.Literacy = share;
        foreach (var state in campaign.StatesOf(campaign.Player)) state.Literacy = share;
        ctx.Refresh();
        return $"Literacy set to {share:P0} across {campaign.StatesOf(campaign.Player).Length} region(s).";
    }

    private static string Politics(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        if (words.Length == 0)
            return $"{campaign.Player.Tag} is {campaign.Player.Ideology} · {campaign.Player.Government}.\n" +
                   "Usage: politics IDEOLOGY [GOVERNMENT] - either may be given alone.";

        var government = "";
        var ideology = "";
        foreach (var word in words)
        {
            if (GovernmentCatalog.Knows(word)) government = word;
            else ideology = word;
        }
        if (!campaign.ChangePolitics(campaign.Player.Id, government, ideology, out var refusal))
            return $"Refused: {ConsoleContext.EscapeBb(refusal)}";
        ctx.Refresh();
        return $"{campaign.Player.Tag} is now {campaign.Player.Ideology} · {campaign.Player.Government}. " +
               $"Flag: {FlagCatalog.Resolve(campaign, campaign.Player)}";
    }

    private static string Budget(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var nation = campaign.Player;
        var words = ConsoleContext.Words(argument);
        if (words.Length == 0)
            return $"tax {nation.TaxRate:P0} · military {nation.MilitarySpending:P0} · " +
                   $"welfare {nation.WelfareSpending:P0} · education {nation.EducationSpending:P0}\n" +
                   $"weekly: tax +{nation.WeeklyTaxIncome:0.00}B, tariffs +{nation.WeeklyTariffIncome:0.00}B, " +
                   $"spending -{nation.WeeklyExpenses:0.00}B, balance {nation.WeeklyBalance:+0.00;-0.00;0.00}B";

        if (words.Length < 2 || !ConsoleContext.TryShare(words[1], out var rate))
            return "Usage: budget tax 40 · budget military 25 · budget welfare 30 · budget education 20";
        switch (words[0].ToLowerInvariant())
        {
            case "tax": campaign.SetTaxRate(nation.Id, rate); break;
            case "military": campaign.SetMilitarySpending(nation.Id, rate); break;
            case "welfare": campaign.SetWelfareSpending(nation.Id, rate); break;
            case "education": campaign.SetEducationSpending(nation.Id, rate); break;
            default: return "Lines: tax, military, welfare, education.";
        }
        ctx.Refresh();
        return $"{words[0].ToLowerInvariant()} set to {rate:P0}.";
    }

    private static string GovernmentFocusCmd(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var wanted = argument.Trim();
        if (wanted.Length == 0)
            return $"Focus: {campaign.Player.GovernmentFocus}. " +
                   $"Choices: {string.Join(", ", Enum.GetNames<GovernmentFocus>())}";
        if (!Enum.TryParse<GovernmentFocus>(wanted, true, out var focus))
            return $"Unknown focus. Choices: {string.Join(", ", Enum.GetNames<GovernmentFocus>())}";
        campaign.SetGovernmentFocus(campaign.Player.Id, focus);
        ctx.Refresh();
        return $"Cabinet focus set to {focus}.";
    }

    private static string TradePolicyCmd(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var wanted = argument.Trim();
        if (wanted.Length == 0)
            return $"Trade policy: {campaign.Player.TradePolicy}. " +
                   $"Choices: {string.Join(", ", Enum.GetNames<TradePolicy>())}";
        if (!Enum.TryParse<TradePolicy>(wanted, true, out var policy))
            return $"Unknown policy. Choices: {string.Join(", ", Enum.GetNames<TradePolicy>())}";
        campaign.SetTradePolicy(campaign.Player.Id, policy);
        ctx.Refresh();
        return $"Trade policy set to {policy}.";
    }

    private static string Modifier(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        if (words.Length == 0)
            return "Usage: modifier ID [days] · modifier -ID to take one away.\n" +
                   $"Known: {string.Join(", ", ModifierCatalog.All.Take(24).Select(entry => entry.Id))}";

        var id = words[0];
        if (id.StartsWith("-", StringComparison.Ordinal))
        {
            id = id[1..];
            return ModifierSystem.Revoke(campaign.Player, id)
                ? $"Revoked '{id}'."
                : $"{campaign.Player.Tag} does not have '{id}'.";
        }
        var days = words.Length > 1 && int.TryParse(words[1], out var parsed) ? parsed : 0;
        if (!ModifierSystem.Grant(campaign, campaign.Player, id, days))
            return $"No modifier called '{id}' in common/modifiers.";
        ctx.Refresh();
        return $"Granted '{id}'{(days > 0 ? $" for {days} day(s)" : "")}.";
    }

    private static string Modifiers(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        if (nation.Modifiers.Count == 0) return $"Nothing is acting on {nation.Tag}.";
        return string.Join("\n", nation.Modifiers.Select(active =>
        {
            var remaining = active.IsPermanent
                ? "permanent"
                : $"{active.DaysRemaining(campaign.ElapsedDays):N0} day(s) left";
            return $"  {active.Id} · granted day {active.GrantedDay:N0} · {remaining}";
        }));
    }

    private static string Pops(ConsoleContext ctx, string argument)
    {
        if (ctx.Campaign is null) return "No campaign.";
        var state = ctx.ResolveState(argument);
        if (state is null) return "Select a region, or name one: pops bavaria";
        var total = Math.Max(1L, state.PopulationGroups.Sum(group => group.Size));

        string Bar(IEnumerable<(string Key, long Size)> rows) => string.Join("\n", rows
            .Where(row => row.Size > 0)
            .Select(row => $"    {row.Key,-22} {row.Size / (double)total,7:P1}  {Fmt.People(row.Size)}"));

        return ConsoleContext.EscapeBb(
            $"{state.Name} · {Fmt.People(state.Population)} · urban {state.Urbanization:P0} · " +
            $"literacy {state.Literacy:P0} · living {state.StandardOfLiving:P0}\n" +
            "  TRADES\n" + Bar(EconomySystem.Professions(state)
                .Select(row => (DemographyCatalog.ProfessionName(row.Profession), row.Size))) + "\n" +
            "  CLASSES\n" + Bar(EconomySystem.Classes(state).Select(row => (row.Class, row.Size))) + "\n" +
            "  SECTORS\n" + Bar(EconomySystem.Sectors(state).Select(row => (row.Sector, row.Size))) + "\n" +
            "  PEOPLES\n" + Bar(EconomySystem.Cultures(state)
                .Take(6).Select(row => (DemographyCatalog.CultureName(row.Culture), row.Size))));
    }

    private static string Migration(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var dest = campaign.NationById(nation.BrainDrainDestinationId);
        var src = campaign.NationById(nation.BrainGainSourceId);
        var states = campaign.StatesOf(nation)
            .OrderByDescending(s => Math.Abs(s.ReviewNetMigration))
            .ThenByDescending(s => s.BrainDrainPressure)
            .Take(6)
            .Select(s => $"    {s.Name,-24} net {Fmt.PeopleDelta(s.ReviewNetMigration),10} · pressure {Fmt.Percent(s.BrainDrainPressure)} · literacy {Fmt.Percent(s.Literacy)}");
        return ConsoleContext.EscapeBb(
            $"{nation.Name} ({nation.Tag}) · weekly {Fmt.PeopleDelta(nation.WeeklyNetMigration)} · cumulative {Fmt.PeopleDelta(nation.CumulativeNetMigration)}\n" +
            $"  brain flow   {nation.BrainDrainRate:+0.0%;-0.0%;0.0%} · " +
            (dest is not null ? $"primary outflow to {dest.Name} ({dest.Tag})"
                : src is not null ? $"primary inflow from {src.Name} ({src.Tag})"
                : "no active cross-border stream") + "\n" +
            "  REGIONS\n" + string.Join("\n", states));
    }

    private static string Economy(ConsoleContext ctx)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var nation = ctx.GetSelectedNation() ?? campaign.Player;
        var lines = new System.Text.StringBuilder(EconomySystem.LastCalibrationReport);
        if (nation is not null)
        {
            var emergent = nation.GrossOutput * 52f * nation.OutputScale;
            lines.Append($"\n{nation.Tag} now: GDP {nation.Gdp:N1}B, emergent target {emergent:N1}B, " +
                         $"staffed {nation.EmployedShare:P0}, productivity {nation.Productivity:0.###}");
        }
        return ConsoleContext.EscapeBb(lines.ToString());
    }

    private static string Market(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation("");
        if (nation is null || ctx.Campaign is null) return "No campaign.";
        var filter = argument.Trim();
        var quotes = nation.Market.Values
            .Where(quote => filter.Length == 0 ||
                            quote.Commodity.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(quote => quote.Satisfaction)
            .ToArray();
        if (quotes.Length == 0) return $"{nation.Tag} has no market lines matching '{ConsoleContext.EscapeBb(filter)}'.";
        return ConsoleContext.EscapeBb($"{nation.Tag} · market access {nation.MarketAccess:P0} · " +
                        $"shortage index {nation.ShortageIndex:0.00}\n" +
               string.Join("\n", quotes.Take(24).Select(quote =>
                   $"  {quote.Commodity,-20} price {quote.Price,7:0.00} · met {quote.Satisfaction,6:P0} · " +
                   $"make {quote.Production,7:0.0} want {quote.Demand,7:0.0} " +
                   $"in {quote.Imports,6:0.0} out {quote.Exports,6:0.0}" +
                   (quote.IsShortage ? "  SHORT" : ""))));
    }

    private static string Trade(ConsoleContext ctx, string argument)
    {
        var nation = ctx.ResolveNation(argument);
        var campaign = ctx.Campaign;
        if (nation is null || campaign is null) return "No such country.";
        var partners = nation.TradePartners
            .OrderByDescending(entry => entry.Value)
            .Take(12)
            .Select(entry =>
            {
                var other = campaign.Nations.FirstOrDefault(candidate => candidate.Id == entry.Key);
                var dependence = nation.TradeDependenceOn(entry.Key);
                return $"  {other?.Tag ?? entry.Key.ToString()} · ${entry.Value:N1}B · {dependence:P0} of its trade";
            })
            .ToArray();
        return ConsoleContext.EscapeBb($"{nation.Tag} · policy {nation.TradePolicy} · exports ${nation.ExportValue:N1}B · " +
                        $"imports ${nation.ImportValue:N1}B · balance {nation.WeeklyTradeBalance:+0.00;-0.00;0.00}B\n" +
                        (partners.Length == 0 ? "  no partners" : string.Join("\n", partners)));
    }

    private static string Build(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var state = ctx.GetSelectedState();
        if (state is null || state.NationMapIndex != campaign.Player.MapIndex)
            return "Select one of your own regions first.";
        var words = ConsoleContext.Words(argument);
        if (words.Length == 0)
            return "Usage: build agriculture [3]. Kinds: " +
                   string.Join(", ", Enum.GetNames<BuildingKind>());
        if (!Enum.TryParse<BuildingKind>(words[0], true, out var kind))
            return $"Unknown building. Kinds: {string.Join(", ", Enum.GetNames<BuildingKind>())}";
        var count = words.Length > 1 && int.TryParse(words[1], out var parsed) ? Math.Clamp(parsed, 1, 12) : 1;

        for (var index = 0; index < count; index++)
            campaign.Commands.Enqueue(new QueueConstructionCommand
            {
                IssuerId = campaign.Player.Id,
                StateMapIndex = state.MapIndex,
                Building = kind
            });
        return $"Queued {count} × {kind} in {ConsoleContext.EscapeBb(state.Name)}. It starts on the next step.";
    }

    private static string Instabuild(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        var nation = campaign.Player;
        bool? wanted = null;
        foreach (var word in words)
        {
            if (word.Equals("on", StringComparison.OrdinalIgnoreCase) || word == "1") wanted = true;
            else if (word.Equals("off", StringComparison.OrdinalIgnoreCase) || word == "0") wanted = false;
            else if (ctx.ResolveNation(word) is { } named) nation = named;
            else return $"No country called {ConsoleContext.EscapeBb(word)}.";
        }
        nation.InstantConstruction = wanted ?? !nation.InstantConstruction;
        campaign.MarkStrategicChange();
        var underway = campaign.Construction.Count(project => project.NationId == nation.Id);
        return nation.InstantConstruction
            ? $"Instant construction ON for {ConsoleContext.EscapeBb(nation.Name)}: {underway} project(s) under way finish on the next day, and so will anything queued from the build menu."
            : $"Instant construction OFF for {ConsoleContext.EscapeBb(nation.Name)}.";
    }

    private static string Research(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var words = ConsoleContext.Words(argument);
        var nation = words.Length > 1 ? ctx.ResolveNation(words[1]) : campaign.Player;
        if (nation is null) return "No such country.";
        var unknown = TechnologyCatalog.All.Where(t => !nation.Technologies.Contains(t.Id)).ToList();
        if (words.Length == 0)
            return unknown.Count == 0 ? "Everything is known."
                : $"{unknown.Count} unknown: " + string.Join(", ", unknown.Take(40).Select(t => t.Id)) + (unknown.Count > 40 ? " ..." : "");
        var learned = words[0].Equals("all", StringComparison.OrdinalIgnoreCase)
            ? unknown
            : unknown.Where(t => t.Id.Equals(words[0], StringComparison.OrdinalIgnoreCase)).ToList();
        if (learned.Count == 0) return $"No unknown technology called {ConsoleContext.EscapeBb(words[0])}. 'research' lists them.";
        foreach (var technology in learned)
        {
            nation.Technologies.Add(technology.Id);
            nation.ResearchProgress.Remove(technology.Id);
            if (nation.CurrentResearch == technology.Id) nation.CurrentResearch = "";
        }
        campaign.MarkStrategicChange();
        return $"{ConsoleContext.EscapeBb(nation.Name)} learned {learned.Count} technolog{(learned.Count == 1 ? "y" : "ies")}.";
    }

    private static string Buildings(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var state = ctx.ResolveState(argument);
        if (state is null) return "Select a region, or name one.";
        var projects = campaign.Construction
            .Where(project => project.StateMapIndex == state.MapIndex)
            .Select(project => $"    building {project.Building} · {project.Progress:P0} · " +
                               $"{project.ProgressDays:0.#}/{project.DurationDays} day(s)")
            .ToArray();
        var standing = state.Buildings.Count == 0
            ? "    nothing built"
            : string.Join("\n", state.Buildings.Select(entry => $"    {entry.Key} ×{entry.Value}"));
        return ConsoleContext.EscapeBb($"{state.Name}\n{standing}" +
                        (projects.Length == 0 ? "" : "\n" + string.Join("\n", projects)));
    }

    private static string Inflation(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign is null) return "No campaign.";
        var nation = ctx.ResolveNation(argument) ?? campaign.Player;
        if (nation is null) return "No such country.";

        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        var drivers = new Breakdown();
        InflationCore.Target(campaign, nation, nation.Gdp / 52f, atWar, 0f, drivers);

        var lines = drivers.Entries
            .Select(entry => $"  {entry.Source,-38} {entry.Value,8:P2}")
            .ToArray();
        return $"{nation.DisplayName} ({nation.Tag}) inflation {nation.Inflation:P2}, heading for " +
               $"{drivers.Total:P2}.\n" + string.Join("\n", lines) +
               $"\n  {"key rate",-38} {CentralBank.KeyRate(nation),8:P2}" +
               $"\n  {"the bank wants",-38} {CentralBank.Wanted(nation, atWar),8:P2}" +
               $"\n  {(nation.GovernmentSetsKeyRate ? "the government holds the rate" : "the bank is independent")}";
    }

    private static string KeyRate(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var player = campaign.Player;
        var text = argument.Trim().ToLowerInvariant();
        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(player.Id));

        if (text is "seize" or "take")
        {
            campaign.Commands.Enqueue(new SetCentralBankControlCommand
            { IssuerId = player.Id, GovernmentSets = true });
            campaign.Commands.ApplyAll(campaign);
            return $"The rate is the government's. The bank wanted {CentralBank.Wanted(player, atWar):P2}.";
        }
        if (text is "release" or "free")
        {
            campaign.Commands.Enqueue(new SetCentralBankControlCommand
            { IssuerId = player.Id, GovernmentSets = false });
            campaign.Commands.ApplyAll(campaign);
            return "The bank has it back.";
        }
        if (text.Length > 0)
        {
            if (!float.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var wanted))
                return "Usage: keyrate 6.5 | keyrate seize | keyrate release";
            if (!player.GovernmentSetsKeyRate) return "The bank sets the rate. `keyrate seize` first.";
            campaign.Commands.Enqueue(new SetKeyRateCommand { IssuerId = player.Id, Rate = wanted / 100f });
            campaign.Commands.ApplyAll(campaign);
        }

        var (lowest, highest) = CentralBank.Range();
        return $"Key rate {CentralBank.KeyRate(player):P2} (neutral {CentralBank.Rule.NeutralRate:P2}, " +
               $"range {lowest:P2}-{highest:P2}). The bank wants {CentralBank.Wanted(player, atWar):P2} " +
               $"against a target of {CentralBank.Rule.Target:P2}. " +
               (player.GovernmentSetsKeyRate
                   ? $"You hold it; the credibility cost is {CentralBank.CredibilityCost(player, atWar):P2} a week."
                   : "The bank holds it.");
    }

    private static string Emission(ConsoleContext ctx, string argument)
    {
        var campaign = ctx.Campaign;
        if (campaign?.Player is null) return "No campaign.";
        var player = campaign.Player;
        var text = argument.Trim().ToLowerInvariant();

        if (text.Length > 0)
        {
            if (!player.GovernmentSetsKeyRate)
                return "The bank is independent and will not print for you. `keyrate seize` first.";
            var wanted = 0f;
            if (text is not ("off" or "stop" or "0") &&
                !float.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out wanted))
                return "Usage: emission 4 | emission off";
            campaign.Commands.Enqueue(new SetEmissionCommand
            { IssuerId = player.Id, ShareOfOutput = wanted / 100f });
            campaign.Commands.ApplyAll(campaign);
        }

        if (!player.GovernmentSetsKeyRate)
            return $"The bank is independent; nothing is being printed. The ceiling, if you took " +
                   $"it, would be {CentralBank.EmissionRange():P0} of output a year.";

        var weekly = player.Gdp / 52f * player.MoneyPrinting;
        return $"Printing {player.MoneyPrinting:P1} of output a year - ${weekly:0.00}B a week into the " +
               $"treasury, ceiling {CentralBank.EmissionRange():P0}. It is adding " +
               $"{CentralBank.EmissionCost(player):P2} to prices, of which " +
               $"{player.EmissionMemory * CentralBank.Rule.EmissionCredibility:P2} is what people " +
               "remember rather than what you are doing now.";
    }
}
