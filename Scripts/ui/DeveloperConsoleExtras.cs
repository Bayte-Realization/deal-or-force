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

    /// <summary>Set once the game has been told to close, so asking twice does not start two clocks.</summary>
    private bool _touchingGrass;

    /// <summary>
    /// The one console command that takes its own advice: it says its piece, and eight seconds
    /// later the game closes. Nothing is saved first, and it says so - the joke is on the
    /// session, not on the campaign somebody forgot to save.
    /// </summary>
    private string ConsoleTouchGrass()
    {
        if (!_touchingGrass)
        {
            _touchingGrass = true;
            GetTree().CreateTimer(8.0, true).Timeout += () => GetTree().Quit();
        }
        return Simulation.ConsoleVerse.TouchGrass(Loc.Language) + "\n\n" +
               Loc.Get("The game closes in eight seconds. Nothing has been saved.");
    }


}
