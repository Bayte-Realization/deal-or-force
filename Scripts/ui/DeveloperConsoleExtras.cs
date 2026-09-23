using System;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// More commands: the ones the new mechanics needed, and the ones nobody needed.
///
/// The first half is ordinary. Monetary policy, the length of a step and the cadence of the
/// passes all arrived without a way to look at them from inside a running game, and a
/// mechanic you cannot interrogate is a mechanic you cannot debug.
///
/// The second half is jokes, and they are here on purpose. A console is the first place a
/// curious player goes and the first thing a streamer opens on camera, and a console that
/// answers a silly question with something true about their own country is a better
/// advertisement than any of the serious commands. So none of these print a canned line:
/// every one of them is computed from the world. `blame` names the country that actually
/// dislikes you most. `doomsday` is arithmetic on your real stability trend. `ratio`
/// compares two real war efforts. The joke is the framing; the figures are the game.
///
/// They are translated, unlike the rest of the console. The console is deliberately English
/// because it prints type names and command syntax - but a joke in a language you do not
/// read is not a joke, and these are content rather than instrumentation.
/// </summary>
public partial class DealOrForceGame
{
    // =====================================================================================
    //  The serious half
    // =====================================================================================

    /// <summary>What prices are doing and why, line by line, for any country.</summary>
    private string ConsoleInflation(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var nation = ConsoleNation(argument) ?? _campaign.Player;
        if (nation is null) return "No such country.";

        var atWar = _campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        var drivers = new Breakdown();
        InflationCore.Target(_campaign, nation, nation.Gdp / 52f, atWar, 0f, drivers);

        var lines = drivers.Entries
            .Select(entry => $"  {entry.Source,-38} {entry.Value,8:P2}")
            .ToArray();
        return $"{nation.DisplayName} ({nation.Tag}) inflation {nation.Inflation:P2}, heading for " +
               $"{drivers.Total:P2}.\n" + string.Join("\n", lines) +
               $"\n  {"key rate",-38} {CentralBank.KeyRate(nation),8:P2}" +
               $"\n  {"the bank wants",-38} {CentralBank.Wanted(nation, atWar),8:P2}" +
               $"\n  {(nation.GovernmentSetsKeyRate ? "the government holds the rate" : "the bank is independent")}";
    }

    /// <summary>Read the key rate, set it, or take it off the bank.</summary>
    private string ConsoleKeyRate(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var player = _campaign.Player;
        var text = argument.Trim().ToLowerInvariant();
        var atWar = _campaign.Wars.Any(war => !war.Ended && war.Involves(player.Id));

        if (text is "seize" or "take")
        {
            _campaign.Commands.Enqueue(new SetCentralBankControlCommand
            { IssuerId = player.Id, GovernmentSets = true });
            _campaign.Commands.ApplyAll(_campaign);
            return $"The rate is the government's. The bank wanted {CentralBank.Wanted(player, atWar):P2}.";
        }
        if (text is "release" or "free")
        {
            _campaign.Commands.Enqueue(new SetCentralBankControlCommand
            { IssuerId = player.Id, GovernmentSets = false });
            _campaign.Commands.ApplyAll(_campaign);
            return "The bank has it back.";
        }
        if (text.Length > 0)
        {
            if (!float.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var wanted))
                return "Usage: keyrate 6.5 | keyrate seize | keyrate release";
            if (!player.GovernmentSetsKeyRate) return "The bank sets the rate. `keyrate seize` first.";
            _campaign.Commands.Enqueue(new SetKeyRateCommand { IssuerId = player.Id, Rate = wanted / 100f });
            _campaign.Commands.ApplyAll(_campaign);
        }

        var (lowest, highest) = CentralBank.Range();
        return $"Key rate {CentralBank.KeyRate(player):P2} (neutral {CentralBank.Rule.NeutralRate:P2}, " +
               $"range {lowest:P2}-{highest:P2}). The bank wants {CentralBank.Wanted(player, atWar):P2} " +
               $"against a target of {CentralBank.Rule.Target:P2}. " +
               (player.GovernmentSetsKeyRate
                   ? $"You hold it; the credibility cost is {CentralBank.CredibilityCost(player, atWar):P2} a week."
                   : "The bank holds it.");
    }

    /// <summary>
    /// Read or open the printing press.
    ///
    /// Deliberately refuses until the bank has been taken, and says so: the press is
    /// downstream of the seizure, and a player who has not made the first decision should not
    /// be able to stumble into the second one.
    /// </summary>
    private string ConsoleEmission(string argument)
    {
        if (_campaign?.Player is null) return "No campaign.";
        var player = _campaign.Player;
        var text = argument.Trim().ToLowerInvariant();

        if (text.Length > 0)
        {
            if (!player.GovernmentSetsKeyRate)
                return "The bank is independent and will not print for you. `keyrate seize` first.";
            var wanted = 0f;
            if (text is not ("off" or "stop" or "0") &&
                !float.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out wanted))
                return "Usage: emission 4 | emission off";
            _campaign.Commands.Enqueue(new SetEmissionCommand
            { IssuerId = player.Id, ShareOfOutput = wanted / 100f });
            _campaign.Commands.ApplyAll(_campaign);
        }

        if (!player.GovernmentSetsKeyRate)
            return $"The bank is independent; nothing is being printed. The ceiling, if you took " +
                   $"it, would be {CentralBank.EmissionRange():P0} of output a year.";

        var weekly = player.Gdp / 52f * player.MoneyPrinting;
        return $"Printing {player.MoneyPrinting:P1} of output a year - ${weekly:0.00}B a week into the " +
               $"treasury, ceiling {CentralBank.EmissionRange():P0}. It is adding " +
               $"{CentralBank.EmissionCost(player):P2} to prices, of which " +
               $"{player.EmissionMemory * CentralBank.Rule.EmissionCredibility:P2} is what people " +
               $"remember rather than what you are doing now.";
    }

    /// <summary>
    /// Audit the layout that is actually on screen.
    ///
    /// The question can only be asked after a real layout pass, which is why it is a command
    /// here rather than a check in the bench: the bench has no Godot in it and never will.
    /// </summary>
    private string ConsoleLayout(string argument) => LayoutAudit.Report(this);

    /// <summary>Read or set the length of one simulation step, live.</summary>
    private string ConsoleStep(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var text = argument.Trim();
        if (text.Length > 0)
        {
            if (!int.TryParse(text, out var hours) || hours <= 0) return "Usage: step 1 | step 6 | step 24";
            _campaign.Defines.HoursPerTick = Math.Clamp(hours, 1, 240);
        }
        return $"One step is {_campaign.TickHours} hour(s), which is {24.0 / _campaign.TickHours:0.##} " +
               $"step(s) a campaign day. Everything that accumulates takes {_campaign.TickScale:0.###} of a " +
               "day's worth; everything that happens on a day runs once for each whole day crossed.";
    }

    /// <summary>Advance whole days without starting the clock.</summary>
    private string ConsoleAdvance(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var text = argument.Trim();
        var days = 1;
        if (text.Length > 0 && (!int.TryParse(text, out days) || days <= 0))
            return "Usage: advance 7";
        days = Math.Clamp(days, 1, 3650);

        var paused = _campaign.IsPaused;
        _campaign.IsPaused = false;
        var before = _campaign.ElapsedDays;
        for (var day = 0; day < days; day++) _campaign.TickDay();
        _campaign.IsPaused = paused;
        return $"Advanced {_campaign.ElapsedDays - before:N0} day(s) to {_campaign.Date}. The clock is " +
               (paused ? "still stopped." : "still running.");
    }

    /// <summary>How often each pass ran, and whether that is once a day or once a step.</summary>
    private string ConsoleCadence(string argument)
    {
        var filter = argument.Trim();
        var samples = SimProfiler.All
            .Where(sample => filter.Length == 0 ||
                             sample.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sample => sample.Runs)
            .Take(20)
            .ToArray();
        if (samples.Length == 0) return "Nothing has been measured yet. Let the clock run first.";
        var steps = Math.Max(1, _campaign?.ElapsedHours / Math.Max(1, _campaign.TickHours) ?? 1);
        return $"Runs per pass, against {steps:N0} step(s) of {_campaign?.TickHours ?? 24}h so far:\n" +
               string.Join("\n", samples.Select(sample =>
                   $"  {sample.Name,-18} ×{sample.Runs,-9:N0} {sample.Runs / (double)steps,6:0.##} per step  " +
                   $"{sample.AllocatedBytes / (double)Math.Max(1, sample.Runs) / 1024.0,7:N1} kB/run"));
    }

    /// <summary>The three forms a count takes in the language that is loaded.</summary>
    private string ConsolePlural(string argument)
    {
        var key = argument.Trim();
        if (key.Length == 0) key = "{0:N0} division selected.";
        var rule = Loc.GetOr("language.plural", "one-other");
        return $"'{Loc.Language}' counts by the '{rule}' rule.\n" +
               string.Join("\n", new[] { 1, 2, 5, 11, 21, 22, 101 }.Select(count =>
                   $"  {count,4} -> {Loc.Format(Plural.Key(key, count), count)}"));
    }

    // =====================================================================================
    //  The other half
    // =====================================================================================

    /// <summary>Who dislikes you most, and what they say it is about.</summary>
    private string ConsoleBlame()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var player = _campaign.Player;
        var worst = _campaign.Nations
            .Where(other => other.Id != player.Id)
            .Select(other => (Other: other, Relation: CampaignDiplomacy.Get(_campaign, other.Id, player.Id)))
            .Where(pair => pair.Relation is not null)
            .OrderBy(pair => pair.Relation.Opinion)
            .FirstOrDefault();
        if (worst.Other is null) return Tr("There is nobody else. Congratulations, probably.");

        var opinion = worst.Relation.Opinion;
        var reasons = worst.Relation.Reasons
            .Select(reason => (reason.Id, Weight: OpinionSystem.Weight(_campaign, reason)))
            .OrderBy(pair => pair.Weight)
            .Take(3)
            .Select(pair => Tr(OpinionCatalog.Find(pair.Id)?.Name ?? pair.Id) +
                            $" ({pair.Weight:+0;-0;0})")
            .ToArray();
        return Loc.Format("It was {0}. They think {1:+0;-0;0} of you and they have reasons: {2}",
            worst.Other.DisplayName, opinion,
            reasons.Length == 0 ? Tr("none they will put in writing") : string.Join(", ", reasons));
    }

    /// <summary>How the country is doing, in one sentence, at the register the figures deserve.</summary>
    private string ConsoleVibe()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var player = _campaign.Player;
        var war = _campaign.Wars.Any(candidate => !candidate.Ended && candidate.Involves(player.Id));
        var mood = player.Stability + player.StandardOfLiving - player.Inflation * 2f - (war ? 0.15f : 0f);

        var line = mood switch
        {
            > 1.30f => Tr("Nothing is on fire. Somebody is going to be blamed for this later."),
            > 1.00f => Tr("Fine. Boringly, suspiciously fine."),
            > 0.75f => Tr("Holding. The kind of holding that is one bad harvest from not."),
            > 0.50f => Tr("The wheels are still on, in the sense that they are nearby."),
            > 0.25f => Tr("Everyone is angry and nobody is wrong."),
            _ => Tr("Historians will be kind. Nobody else will.")
        };
        return Loc.Format("{0}: stability {1:P0}, living {2:P0}, prices {3:P1}{4}. {5}",
            player.DisplayName, player.Stability, player.StandardOfLiving, player.Inflation,
            war ? Tr(", and a war on") : "", line);
    }

    /// <summary>At the current rate, when the country stops being one.</summary>
    private string ConsoleDoomsday()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var player = _campaign.Player;
        var samples = _campaign.History.ForNation(player.Id);
        if (samples is null || samples.Count < 8)
            return Tr("Not enough history yet. Come back in a month and be disappointed properly.");

        var first = samples[Math.Max(0, samples.Count - 8)];
        var last = samples[^1];
        var weeks = Math.Max(1, samples.Count > 8 ? 8 : samples.Count - 1);
        var drift = (last.Stability - first.Stability) / weeks;

        if (drift >= -0.0002f)
            return Loc.Format("Stability {0:P0} and holding or rising. No doom available at this time.",
                player.Stability);
        var weeksLeft = player.Stability / -drift;
        return Loc.Format("Stability {0:P0}, falling {1:P2} a week. On that line it reaches nothing in " +
                          "{2:N0} week(s) - about {3:N0} year(s). This is arithmetic, not prophecy.",
            player.Stability, -drift, weeksLeft, weeksLeft / 52f);
    }

    /// <summary>You against whoever you are fighting, figure by figure.</summary>
    private string ConsoleRatio()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var player = _campaign.Player;
        var war = _campaign.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Involves(player.Id));
        if (war is null) return Tr("You are at peace. There is nobody to be better than.");
        var enemy = _campaign.NationById(war.EnemyLeaderOf(player.Id));
        if (enemy is null) return Tr("Your enemy appears to be nobody in particular.");

        var mine = 0;
        string Line(string label, double ours, double theirs, string format)
        {
            var won = ours > theirs;
            if (won) mine++;
            return $"  {Tr(label),-20} {ours.ToString(format, CultureInfo.CurrentCulture),14} " +
                   $"{(won ? ">" : "<")} {theirs.ToString(format, CultureInfo.CurrentCulture),14}";
        }

        var rows = string.Join("\n", new[]
        {
            Line("Output", player.Gdp, enemy.Gdp, "N0"),
            Line("People", player.Population, enemy.Population, "N0"),
            Line("Divisions", _campaign.Armies.Count(a => a.NationId == player.Id),
                _campaign.Armies.Count(a => a.NationId == enemy.Id), "N0"),
            Line("Stability", player.Stability, enemy.Stability, "P0"),
            Line("War support", player.WarSupport, enemy.WarSupport, "P0"),
            Line("Prices", 1 - player.Inflation, 1 - enemy.Inflation, "P0")
        });
        return $"{player.DisplayName} — {enemy.DisplayName}\n{rows}\n" +
               (mine >= 4 ? Tr("Ratio. Comfortably.")
                   : mine >= 3 ? Tr("Honours even, which nobody ever accepts.")
                   : Tr("Ratioed. By a country you chose to fight."));
    }

    /// <summary>Your own divisions that nobody has told to do anything.</summary>
    private string ConsoleIdleArmies()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var idle = _campaign.Armies
            .Where(army => army.NationId == _campaign.Player.Id &&
                           army.Route.Count == 0 && !army.InBattle &&
                           army.OrderKind == ArmyOrder.Manual && !army.PlanActive)
            .ToArray();
        if (idle.Length == 0) return Tr("Everybody is doing something. Enjoy the novelty.");
        return Loc.Format("{0:N0} division(s) are waiting to be told anything at all:\n{1}",
            idle.Length,
            string.Join("\n", idle.Take(12).Select(army =>
                $"  {army.Name} — {Tr(StateNameOf(army.StateMapIndex))}")));
    }

    /// <summary>A coin, flipped out of the campaign's own seed, so it is the same coin twice.</summary>
    private string ConsoleFlip(string argument)
    {
        if (_campaign is null) return "No campaign.";
        var question = argument.Trim();
        var hash = (uint)(_campaign.Seed * 2654435761u + (uint)_campaign.ElapsedHours * 40503u +
                          (uint)question.GetHashCode());
        var heads = (hash >> 7 & 1) == 0;
        return question.Length == 0
            ? (heads ? Tr("Heads.") : Tr("Tails."))
            : question + " — " + (heads ? Tr("yes") : Tr("no")) + " " +
              Tr("(deterministic, like everything else here; ask again on another day)");
    }

    /// <summary>The treasury buys a coffee. It is a rounding error and it is on the books.</summary>
    private string ConsoleCoffee()
    {
        if (_campaign?.Player is null) return "No campaign.";
        _campaign.Player.Treasury -= 1f;
        return Loc.Format("The treasury bought a coffee. Balance {0}. It is on the books, which is " +
                          "more than can be said for most defence procurement.",
            Fmt.Money(_campaign.Player.Treasury));
    }

    /// <summary>The classic. It does exactly what it has always done.</summary>
    private static string ConsoleXyzzy() => Tr("Nothing happens.");

    /// <summary>The other classic.</summary>
    private string ConsoleSudo(string argument) => argument.Trim().Length == 0
        ? Tr("You are the head of state. There is nobody above you to ask.")
        : Loc.Format("You are the head of state. Do it yourself: {0}", argument.Trim());

    /// <summary>What the simulation actually knows about you, which is not very much.</summary>
    private string ConsoleWhoAmI()
    {
        if (_campaign?.Player is null) return "No campaign.";
        var player = _campaign.Player;
        return Loc.Format("{0} ({1}). {2}, {3}. {4:N0} people, {5} in the treasury, {6:N0} division(s). " +
                          "The simulation knows nothing else about you and has no opinion.",
            player.DisplayName, player.Tag,
            Tr(GovernmentCatalog.Of(player.Government).Name),
            Tr(IdeologyCatalog.Of(player.Ideology).Name),
            player.Population, Fmt.Money(player.Treasury),
            _campaign.Armies.Count(army => army.NationId == player.Id));
    }

    /// <summary>How long this has been going on.</summary>
    /// <summary>The verse `touchgrass` answers with. One key, so a translation is one line in a table.</summary>
    private const string TouchGrassVerse =
        "For a hundred years you steered the fate of states,\n" +
        "Carved up the seas and moved the borders round.\n" +
        "Outside, there is grass that no map ever drew.\n" +
        "It is real. Go out and feel it on the ground.";

    /// <summary>Set once the game has been told to close, so asking twice does not start two clocks.</summary>
    private bool _touchingGrass;

    /// <summary>
    /// The one console command that takes its own advice: it says its piece, and eight seconds
    /// later the game closes. Nothing is saved first, and it says so - the joke is on the
    /// session, not on the campaign somebody forgot to save.
    /// </summary>
    private string ConsoleTouchGrass()
    {
        var real = TimeSpan.FromMilliseconds(Time.GetTicksMsec());
        var days = _campaign?.ElapsedDays ?? 0;
        // Days until there are years to count. "0 year(s)" is a true statement and a useless
        // one, and the comparison is the whole of the line.
        var lived = days >= 365
            ? Loc.Format(Simulation.Plural.Key("{0:N0} year of somebody else's", days / 365), days / 365)
            : Loc.Format(Simulation.Plural.Key("{0:N0} day of somebody else's", days), days);
        var tally = Loc.Format("{0:h\\:mm} of your life for {1}. The grass is outside and it is not modelled.",
            real, lived);
        if (!_touchingGrass)
        {
            _touchingGrass = true;
            GetTree().CreateTimer(8.0, true).Timeout += () => GetTree().Quit();
        }
        return Loc.Get(TouchGrassVerse) + "\n\n" + tally + "\n" +
               Loc.Get("The game closes in eight seconds. Nothing has been saved.");
    }

    /// <summary>The region a map index belongs to, by name, or something honest instead.</summary>
    private string StateNameOf(int mapIndex)
    {
        var state = _campaign?.States.FirstOrDefault(candidate => candidate.MapIndex == mapIndex);
        return state?.Name ?? "somewhere";
    }

}
