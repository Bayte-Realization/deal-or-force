using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The central bank, on one card.
///
/// The rate has always been in the game and no player could have found it: it was a budget
/// line called "Policy interest rate" sitting between the tax sliders, reading "1.0x". A
/// multiplier of a neutral point nobody was told the value of is not a key rate, it is a
/// number. Here it is a rate, per annum, the way it is written in every newspaper the game
/// is set among - and beside it the two other figures that make a rate mean anything: what
/// prices are doing, and what the bank is aiming at.
///
/// And it is a decision. By default the bank is independent and moves the rate itself, for
/// this country and for the other two hundred and sixty. A government can take the lever,
/// and then the slider lives - along with the bill for using it, which the card states in
/// points of inflation before the player touches anything.
/// </summary>
public partial class DealOrForceGame
{
    private VBoxContainer _centralBankCard;
    private Label _keyRateReading;
    private Label _keyRateDirection;
    private HSlider _keyRateSlider;
    private CheckButton _governmentSetsRate;
    private HSlider _emissionSlider;
    private Label _emissionReading;
    private Label _emissionNote;
    private Label _centralBankFigures;
    private Label _centralBankNote;
    private bool _updatingCentralBank;

    private void BuildCentralBank(VBoxContainer rows)
    {
        StateSection(rows, "CENTRAL BANK",
            "What borrowing costs, who decides it, and what it is being aimed at. The bank holds " +
            "the rate against its own target unless the government takes it away - and a government " +
            "that holds the rate below what the bank wanted pays for the cheap money in prices. " +
            "Everything here is declared in common/inflation and common/budget.");

        _centralBankCard = new VBoxContainer();
        _centralBankCard.AddThemeConstantOverride("separation", 6);
        rows.AddChild(_centralBankCard);

        // ---- the rate itself, at the size a headline figure deserves ----
        var headline = new HBoxContainer();
        headline.AddThemeConstantOverride("separation", 9);
        _centralBankCard.AddChild(headline);
        if (Symbol("inflation", 20, "text.accent") is { } mark) headline.AddChild(mark);
        // Nothing in this row may wrap: the caption takes the width and the two figures
        // beside it would get none, which is how a headline becomes a column of letters.
        var caption = Fixed(Title("Key rate", 13, Ui.Colour("text.caption"), upper: true));
        caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        headline.AddChild(caption);
        _keyRateDirection = Fixed(Title("", 13, Ui.Colour("text.accent_dim")), 96);
        _keyRateDirection.HorizontalAlignment = HorizontalAlignment.Right;
        headline.AddChild(_keyRateDirection);
        _keyRateReading = Fixed(Title("", 24, Ui.Colour("text.bright")), 92);
        _keyRateReading.HorizontalAlignment = HorizontalAlignment.Right;
        headline.AddChild(_keyRateReading);

        // ---- and the lever, which only lives when the government has taken it ----
        var (lowest, highest) = CentralBank.Range();
        _keyRateSlider = new HSlider
        {
            MinValue = lowest * 100d,
            MaxValue = highest * 100d,
            Step = 0.05,
            CustomMinimumSize = new Vector2(210, 26),
            // The same reason as every other slider on this screen: it sits in a scrolling
            // panel, and a wheel that moved it would set monetary policy on the way past.
            Scrollable = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _keyRateSlider.ValueChanged += OnKeyRateChanged;
        _centralBankCard.AddChild(_keyRateSlider);

        // ---- the three figures a rate is only meaningful against ----
        _centralBankFigures = Title("", 12, Ui.Colour("text.body_soft"));
        _centralBankFigures.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _centralBankCard.AddChild(_centralBankFigures);

        // ---- who has it ----
        //
        // Named for what it actually decides, not for one of the two things it unlocks. It
        // used to read "the government sets the rate", and a player looking for a way to take
        // over the central bank did not recognise the sentence as being that way - which is a
        // fair reading, because it names the rate and says nothing about the press that sits
        // right underneath it.
        _governmentSetsRate = new CheckButton { Text = Tr("The government controls the central bank") };
        _governmentSetsRate.TooltipText = Ui.Tip(Tr(
            "Take the central bank off its own board. Both levers on this card come alive: you " +
            "set the key rate yourself, and you can order money printed. Both are also priced - " +
            "money a government has ordered to be cheap is money nobody believes will stay cheap, " +
            "and money it has ordered to be printed is money nobody believes has a limit."));
        _governmentSetsRate.Toggled += OnCentralBankControlToggled;
        _centralBankCard.AddChild(_governmentSetsRate);

        // ---- and what that means, right now, in a sentence ----
        _centralBankNote = Title("", 12, Ui.Colour("text.accent_dim"));
        _centralBankNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _centralBankCard.AddChild(_centralBankNote);

        // ---- the printing press ----
        //
        // Placed under the switch that unlocks it, and dead until that switch is on. The two
        // decisions are a sequence and the card should read like one: take the bank, and a
        // second lever appears that was not there before.
        _centralBankCard.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        // Laid out exactly like the key-rate headline above, and that is not an accident:
        // a clipped label with no minimum width gets none at all when a sibling takes the
        // expand, so the caption has to be the thing that expands. Built the other way, the
        // words "PRINTING MONEY" came out one pixel wide - which is the same layout mistake
        // this card has made three times now.
        var pressRow = new HBoxContainer();
        pressRow.AddThemeConstantOverride("separation", 9);
        var pressCaption = Fixed(Title("Printing money", 13, Ui.Colour("text.caption"), upper: true));
        pressCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        pressRow.AddChild(pressCaption);
        _emissionReading = Fixed(Title("", 20, Ui.Colour("text.bright")), 92);
        _emissionReading.HorizontalAlignment = HorizontalAlignment.Right;
        pressRow.AddChild(_emissionReading);
        _centralBankCard.AddChild(pressRow);

        _emissionSlider = new HSlider
        {
            MinValue = 0d,
            MaxValue = CentralBank.EmissionRange() * 100d,
            Step = 0.5d,
            CustomMinimumSize = new Vector2(0, 22)
        };
        _emissionSlider.TooltipText = Ui.Tip(Tr(
            "Have the bank print money and hand it to the treasury, as a share of a year's output. " +
            "The money is real and arrives at once. What it costs is that nobody believes there is " +
            "a limit any more - and that belief does not come back when the press stops."));
        _emissionSlider.ValueChanged += OnEmissionChanged;
        _centralBankCard.AddChild(_emissionSlider);

        _emissionNote = Title("", 12, Ui.Colour("text.accent_dim"));
        _emissionNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _centralBankCard.AddChild(_emissionNote);
    }

    private void OnEmissionChanged(double percent)
    {
        if (_updatingCentralBank || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetEmissionCommand
        {
            IssuerId = _campaign.Player.Id,
            ShareOfOutput = (float)(percent / 100d)
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshCentralBank(_campaign.Player);
    }

    /// <summary>What the press is doing, priced, in one sentence.</summary>
    private static string EmissionNote(Nation nation)
    {
        if (!nation.GovernmentSetsKeyRate)
            return Tr("The bank is independent. It will not print for a government that does not own it.");
        if (nation.MoneyPrinting <= 0.0001f && nation.EmissionMemory <= 0.0001f)
            return Tr("Nothing is being printed.");

        var weekly = nation.Gdp / 52f * nation.MoneyPrinting;
        var remembered = nation.EmissionMemory * CentralBank.Rule.EmissionCredibility;
        if (nation.MoneyPrinting <= 0.0001f)
            return Loc.Format("The press has stopped. It is still costing {0} on prices, because " +
                              "stopping is not the same as never having started.", Fmt.Percent(remembered));
        return Loc.Format("{0} a week into the treasury, and {1} on prices - {2} of that is what " +
                          "people remember rather than what you are doing now.",
            Fmt.Money(weekly), Fmt.Percent(CentralBank.EmissionCost(nation)), Fmt.Percent(remembered));
    }

    private void OnKeyRateChanged(double percent)
    {
        if (_updatingCentralBank || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetKeyRateCommand
        {
            IssuerId = _campaign.Player.Id,
            Rate = (float)(percent / 100d)
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshCentralBank(_campaign.Player);
    }

    private void OnCentralBankControlToggled(bool governmentSets)
    {
        if (_updatingCentralBank || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetCentralBankControlCommand
        {
            IssuerId = _campaign.Player.Id,
            GovernmentSets = governmentSets
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshCentralBank(_campaign.Player);
    }

    /// <summary>
    /// Put the card where the country actually is.
    ///
    /// Set without firing the signals, for the reason every other control on this screen is:
    /// a slider that reported its own value back would queue an order every refresh, which
    /// at speed five is several a second.
    /// </summary>
    private void RefreshCentralBank(Nation nation)
    {
        if (_centralBankCard is null || nation is null) return;
        _updatingCentralBank = true;
        try
        {
            var atWar = _campaign is not null &&
                        _campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
            var rate = CentralBank.KeyRate(nation);
            var wanted = CentralBank.Wanted(nation, atWar);
            var neutral = CentralBank.Rule.NeutralRate;
            var target = CentralBank.Rule.Target;

            var (lowest, highest) = CentralBank.Range();
            _keyRateSlider.MinValue = lowest * 100d;
            _keyRateSlider.MaxValue = highest * 100d;
            _keyRateSlider.Value = rate * 100d;
            _keyRateSlider.Editable = nation.GovernmentSetsKeyRate;
            _keyRateSlider.Modulate = new Color(1, 1, 1, nation.GovernmentSetsKeyRate ? 1f : 0.45f);

            // A decimal, unlike everywhere else: a key rate moves in quarter points and the
            // whole-number format the rest of the interface uses would show four moves of the
            // bank as the same figure four times.
            _keyRateReading.Text = rate.ToString("P1", System.Globalization.CultureInfo.CurrentCulture);
            // Above neutral is money being made dear; below it, cheap. Coloured the way the
            // rest of the interface colours a figure that is working against you.
            _keyRateReading.AddThemeColorOverride("font_color", Ui.Colour(
                rate > neutral + 0.0005f ? "state.warning"
                : rate < neutral - 0.0005f ? "state.info"
                : "text.bright"));
            _keyRateDirection.Text = rate > neutral + 0.0005f ? Tr("tightening")
                : rate < neutral - 0.0005f ? Tr("loosening")
                : Tr("neutral");

            _governmentSetsRate.Text = Tr(nation.GovernmentSetsKeyRate
                ? "The government controls the central bank"
                : "The central bank is independent");
            _governmentSetsRate.ButtonPressed = nation.GovernmentSetsKeyRate;

            _centralBankFigures.Text = Loc.Format(
                "Inflation {0} · the bank's target {1} · neutral {2}",
                Fmt.Percent(nation.Inflation), Fmt.Percent(target), Fmt.Percent(neutral));

            _centralBankNote.Text = CentralBankNote(nation, rate, wanted, atWar);
            _centralBankNote.AddThemeColorOverride("font_color", Ui.Colour(
                nation.GovernmentSetsKeyRate && wanted > rate + 0.0005f
                    ? "state.warning"
                    : "text.accent_dim"));

            _emissionSlider.MaxValue = CentralBank.EmissionRange() * 100d;
            _emissionSlider.Value = nation.MoneyPrinting * 100d;
            _emissionSlider.Editable = nation.GovernmentSetsKeyRate;
            _emissionSlider.Modulate = new Color(1, 1, 1, nation.GovernmentSetsKeyRate ? 1f : 0.35f);
            _emissionReading.Text = nation.MoneyPrinting.ToString("P1",
                System.Globalization.CultureInfo.CurrentCulture);
            _emissionReading.AddThemeColorOverride("font_color", Ui.Colour(
                nation.MoneyPrinting > 0.0001f ? "state.warning" : "text.bright"));
            _emissionNote.Text = EmissionNote(nation);
            _emissionNote.AddThemeColorOverride("font_color", Ui.Colour(
                nation.EmissionMemory > 0.0001f ? "state.warning" : "text.accent_dim"));
        }
        finally { _updatingCentralBank = false; }
    }

    /// <summary>
    /// One sentence saying what the arrangement currently means, priced.
    ///
    /// The point of the card: a player should be able to read what their own decision costs
    /// before they make it, in the unit the decision is denominated in - points of inflation.
    /// </summary>
    private static string CentralBankNote(Nation nation, float rate, float wanted, bool atWar)
    {
        if (!nation.GovernmentSetsKeyRate)
        {
            if (wanted > rate + 0.0015f) return Tr("The bank is raising the rate.");
            if (wanted < rate - 0.0015f) return Tr("The bank is cutting the rate.");
            return atWar
                ? Tr("The bank is holding, and forgiving some of the war's prices while it does.")
                : Tr("The bank is holding. Prices are where it wants them.");
        }

        var cost = CentralBank.CredibilityCost(nation, atWar);
        if (cost <= 0.0001f)
            return Loc.Format("You hold the rate. The bank wanted {0}, and you are not under it, " +
                              "so nothing is being paid for the privilege.", Fmt.Percent(wanted));
        return Loc.Format("You hold {0} where the bank wanted {1}. Cheap money nobody believes in " +
                          "adds {2} to inflation every week it lasts.",
            Fmt.Percent(rate), Fmt.Percent(wanted), Fmt.Percent(cost));
    }
}
