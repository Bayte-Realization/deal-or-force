using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// How a central bank behaves, as data.
///
/// Every field is a row in `common/inflation` under `central_bank`, so a mod can give the
/// world a hawkish decade, a two-per-cent orthodoxy or no monetary policy at all without
/// touching code.
/// </summary>
public sealed class CentralBankRule
{
    /// <summary>
    /// The key rate a bank holds when it is neither leaning on prices nor propping up
    /// growth - and therefore what the policy line's neutral point MEANS in per cent.
    ///
    /// The rate the player sets has always existed; it was a multiplier between a fifth and
    /// three, which is a number nobody has an intuition about. Four per cent at neutral makes
    /// the same lever read as a rate: a fifth of neutral is 0.8 %, three times it is 12 %,
    /// and both ends are rates a player recognises from the news.
    /// </summary>
    public float NeutralRate = 0.04f;

    /// <summary>What the bank is trying to hold prices at.</summary>
    public float Target = 0.02f;

    /// <summary>
    /// How hard the bank leans on a gap, in points of rate per point of overshoot.
    ///
    /// Above one on purpose. A bank that raises its rate by less than prices rose has
    /// loosened in real terms while appearing to tighten, which is the mistake the whole
    /// literature is about; 1.6 is inside the range real banks are usually fitted at.
    /// </summary>
    public float Response = 1.6f;

    /// <summary>
    /// The share of the distance to its intended rate the bank covers each economic week.
    ///
    /// Banks move in steps and talk about it first. A rate that jumped to wherever the rule
    /// pointed would swing every time a harvest failed, and a player watching the number
    /// would learn nothing from it.
    /// </summary>
    public float Smoothing = 0.25f;

    /// <summary>
    /// How much extra inflation a bank will tolerate while the country is at war before it
    /// starts tightening. Somebody has to fund the war, and a bank that strangles the
    /// economy during one is a bank that gets nationalised.
    /// </summary>
    public float WarTolerance = 0.015f;

    /// <summary>
    /// What a government loses by taking the rate off the bank, per point it holds the rate
    /// BELOW what the bank wanted.
    ///
    /// This is the whole cost of the decision, and it is the real one: cheap money a
    /// government has ordered is money nobody believes will stay cheap, so prices move before
    /// the spending does. Holding the rate ABOVE what the bank wanted costs nothing extra -
    /// a government that out-hawks its own central bank has a credibility problem of a
    /// different kind, and not one this model has an opinion about.
    /// </summary>
    public float Credibility = 0.35f;

    /// <summary>
    /// The most of a year's output a government may have printed for it, per year.
    ///
    /// A ceiling rather than a prohibition. Governments in this much trouble do print, and a
    /// model that refuses to let them cannot show what happens next - which is the only
    /// reason the mechanic is worth having.
    /// </summary>
    public float EmissionCeiling = 0.15f;

    /// <summary>
    /// Points of inflation per unit of annual output printed in a year.
    ///
    /// The direct half: money that was not earned, chasing goods that were not made. Near
    /// one deliberately - printing a tenth of output is about nine points on prices, which
    /// is roughly what it does.
    /// </summary>
    public float EmissionInflation = 0.90f;

    /// <summary>
    /// And the other half, which is larger: what printing does to belief.
    ///
    /// Holding the rate down is a government saying money will be cheap. Printing is a
    /// government saying there is no longer a limit, and that is a far more expensive thing
    /// to say - so this is several times <see cref="Credibility"/> on purpose. It is also
    /// why the two levers do not simply add up to a bigger version of one lever: the rate
    /// buys growth and costs prices; the press buys solvency and costs the currency.
    /// </summary>
    public float EmissionCredibility = 1.60f;

    /// <summary>
    /// How much of the lost belief comes back each economic week once the press stops.
    ///
    /// Not instantly, and that is the point: a country that printed for a year and stopped
    /// does not get its reputation back the following Monday. Twelve per cent a week is
    /// about half a year to be forgiven.
    /// </summary>
    public float EmissionMemoryDecay = 0.12f;
}

/// <summary>
/// The central bank: the key rate, who sets it, and what it is trying to do.
///
/// The rate itself is not new. It has always been the `debt_interest` budget line - a
/// player-set multiplier from a fifth of neutral to three times it, feeding
/// <see cref="InflationCore"/> through the `policy` weight and charging the country interest
/// on its own debt. What it was not, was legible or alive: a player looking for a key rate
/// found "1.0x" in the middle of a tax screen, and the two hundred and sixty countries that
/// are not the player never touched it at all, so the rest of the world had no monetary
/// policy of any kind.
///
/// So three things, none of which duplicate what exists:
///
///   - the multiplier is read and written as a RATE PER ANNUM, against a neutral declared in
///     data. Nothing is stored twice; the budget line remains where the value lives;
///   - a bank that is left alone RUNS ITSELF, by a Taylor rule against its own target, for
///     every country in the world including the player's;
///   - a government can TAKE THE RATE, and pays for it in the one currency a central bank
///     has: if it holds the rate under what the bank wanted, nobody believes the money will
///     stay cheap, and prices move before the spending does.
///
/// That last part is the mechanic. Cheap money is real growth now against real prices later,
/// and it is a decision a player makes with a slider they can see.
/// </summary>
public static class CentralBank
{
    public static CentralBankRule Rule { get; private set; } = new();

    static CentralBank() => ResetCatalog();

    /// <summary>
    /// Read from the same folder the rest of the monetary model is declared in, because it
    /// is the same model: a bank aiming at a target the inflation weights do not share would
    /// be two economies in one country.
    /// </summary>
    public static void ResetCatalog(List<string> errors = null)
    {
        Rule = new CentralBankRule();
        foreach (var document in GameDataLoader.DataDocuments("common", "inflation"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
    }

    /// <summary>Later files win, field by field, so a mod names only what it disagrees with.</summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("central_bank", out var bank) ||
            bank.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in bank.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (entry.Value.ValueKind != JsonValueKind.Number)
            {
                errors?.Add($"common/inflation central_bank: '{entry.Name}' must be a number.");
                continue;
            }
            var value = entry.Value.GetSingle();
            switch (entry.Name.ToLowerInvariant())
            {
                case "neutral_rate": Rule.NeutralRate = value; break;
                case "target": Rule.Target = value; break;
                case "response": Rule.Response = value; break;
                case "smoothing": Rule.Smoothing = value; break;
                case "war_tolerance": Rule.WarTolerance = value; break;
                case "credibility": Rule.Credibility = value; break;
                case "emission_ceiling": Rule.EmissionCeiling = value; break;
                case "emission_inflation": Rule.EmissionInflation = value; break;
                case "emission_credibility": Rule.EmissionCredibility = value; break;
                case "emission_memory_decay": Rule.EmissionMemoryDecay = value; break;
                default:
                    errors?.Add($"common/inflation central_bank: '{entry.Name}' is not a setting this game has.");
                    break;
            }
        }
    }

    /// <summary>The budget line the rate actually lives on. One name, in one place.</summary>
    public static BudgetLine Line => BudgetCore.Find(InflationCore.PolicyLineId);

    /// <summary>
    /// This country's key rate, per annum, as a player reads it: 0.04 is four per cent.
    /// </summary>
    public static float KeyRate(Nation nation)
    {
        var multiplier = InflationCore.PolicyRate(nation, out var neutral);
        if (neutral <= 0.0001f) return Rule.NeutralRate;
        return Rule.NeutralRate * multiplier / neutral;
    }

    /// <summary>The lowest and highest rate the declared line allows, per annum.</summary>
    public static (float Lowest, float Highest) Range()
    {
        var line = Line;
        if (line is null) return (Rule.NeutralRate, Rule.NeutralRate);
        var neutral = line.Neutral <= 0.0001f ? 1f : line.Neutral;
        return (Rule.NeutralRate * line.Minimum / neutral, Rule.NeutralRate * line.Maximum / neutral);
    }

    /// <summary>Set the rate as a rate. Clamped to what the declared line allows.</summary>
    public static void SetKeyRate(Nation nation, float annualRate)
    {
        var line = Line;
        if (line is null || nation is null) return;
        var neutral = line.Neutral <= 0.0001f ? 1f : line.Neutral;
        if (Rule.NeutralRate <= 0.0001f) return;
        BudgetCore.SetRate(nation, line, annualRate / Rule.NeutralRate * neutral);
    }

    /// <summary>
    /// Where the bank would put the rate if nobody stopped it.
    ///
    /// The neutral rate, plus its response to whatever prices are doing against its target -
    /// and in a war it forgives a little of the overshoot, because somebody has to fund the
    /// war and a bank that strangles an economy during one does not stay independent long.
    /// </summary>
    public static float Wanted(Nation nation, bool atWar)
    {
        if (nation is null) return Rule.NeutralRate;
        var tolerated = Rule.Target + (atWar ? MathF.Max(0f, Rule.WarTolerance) : 0f);
        var gap = nation.Inflation - tolerated;
        var (lowest, highest) = Range();
        return M.Clamp(Rule.NeutralRate + gap * Rule.Response, lowest, highest);
    }

    /// <summary>
    /// One economic week of an independent bank.
    ///
    /// Does nothing at all for a country whose government has taken the rate - that is what
    /// taking it means - and nothing for a world whose mod has set the response to zero,
    /// which is how a scenario switches monetary policy off.
    /// </summary>
    public static void Tick(Nation nation, bool atWar)
    {
        if (nation is null || nation.GovernmentSetsKeyRate) return;
        if (Rule.Smoothing <= 0f || Line is null) return;
        var now = KeyRate(nation);
        SetKeyRate(nation, M.Lerp(now, Wanted(nation, atWar), M.Clamp01(Rule.Smoothing)));
    }

    /// <summary>
    /// What a government's cheap money costs in prices, per week, and nothing when the bank
    /// is independent or the government is not holding the rate down.
    ///
    /// See <see cref="CentralBankRule.Credibility"/>: this is the price of the decision, and
    /// the reason taking the rate is a decision rather than a free win.
    /// </summary>
    public static float CredibilityCost(Nation nation, bool atWar)
    {
        if (nation is null || !nation.GovernmentSetsKeyRate || Rule.Credibility <= 0f) return 0f;
        var below = Wanted(nation, atWar) - KeyRate(nation);
        return below <= 0f ? 0f : below * Rule.Credibility;
    }

    // ------------------------------------------------------------------ the printing press

    /// <summary>
    /// The widest a government may open the press, as a share of a year's output.
    /// </summary>
    public static float EmissionRange() => MathF.Max(0f, Rule.EmissionCeiling);

    /// <summary>
    /// Order money printed, as a share of annual output per year. Refused while the bank is
    /// independent, for the same reason the rate is: a government that has not taken the
    /// bank cannot tell it what to do.
    ///
    /// This is the deliberate join between the two levers. Seizing the rate is the cheap,
    /// reversible decision a player makes in year one; the press is the expensive one it
    /// unlocks, and a player who never took the bank never sees it at all.
    /// </summary>
    public static void SetEmission(Nation nation, float shareOfOutput)
    {
        if (nation is null || !nation.GovernmentSetsKeyRate) return;
        nation.MoneyPrinting = M.Clamp(shareOfOutput, 0f, EmissionRange());
    }

    /// <summary>
    /// Print this week's money into the treasury, and remember that it was printed.
    ///
    /// The money is real and arrives immediately, which is the entire appeal: a country one
    /// week from insolvency is solvent again on Monday. What it bought is a number that does
    /// not go away when the press stops - <see cref="Nation.EmissionMemory"/> - and that
    /// number is what prices answer to.
    /// </summary>
    public static void RunThePress(Nation nation, float weeklyOutput)
    {
        if (nation is null) return;

        if (!nation.GovernmentSetsKeyRate) nation.MoneyPrinting = 0f;
        var printing = M.Clamp(nation.MoneyPrinting, 0f, EmissionRange());
        nation.MoneyPrinting = printing;

        if (printing > 0f && weeklyOutput > 0f) nation.Treasury += weeklyOutput * printing;

        // Belief follows the press up quickly and comes back down slowly.
        nation.EmissionMemory = printing > nation.EmissionMemory
            ? printing
            : MathF.Max(printing, nation.EmissionMemory * (1f - M.Clamp01(Rule.EmissionMemoryDecay)));
    }

    /// <summary>
    /// What the press is doing to prices this week: the money itself, plus what printing it
    /// did to anybody's confidence that it will stop.
    /// </summary>
    public static float EmissionCost(Nation nation)
    {
        if (nation is null) return 0f;
        var direct = MathF.Max(0f, nation.MoneyPrinting) * Rule.EmissionInflation;
        var remembered = MathF.Max(0f, nation.EmissionMemory) * Rule.EmissionCredibility;
        return direct + remembered;
    }
}

/// <summary>
/// Order money printed, as a share of annual output per year. Refused while the bank is
/// independent.
/// </summary>
public sealed class SetEmissionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>Share of a year's output, per year. 0.05 is five per cent of GDP a year.</summary>
    public float ShareOfOutput { get; init; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign?.NationById(IssuerId);
        if (nation is null) return;
        CentralBank.SetEmission(nation, ShareOfOutput);
    }
}

/// <summary>
/// Set the key rate, as a rate. Refused while the bank is independent, because a government
/// that has not taken the lever does not have it.
/// </summary>
public sealed class SetKeyRateCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>Per annum. 0.065 is six and a half per cent.</summary>
    public float Rate { get; init; }

    /// <summary>What it now reads, clamped to what the declared line allows.</summary>
    public float Applied { get; private set; }

    public bool Accepted { get; private set; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign?.NationById(IssuerId);
        if (nation is null || !nation.GovernmentSetsKeyRate) return;
        CentralBank.SetKeyRate(nation, Rate);
        Applied = CentralBank.KeyRate(nation);
        Accepted = true;
    }
}

/// <summary>
/// Take the key rate off the central bank, or give it back.
///
/// Giving it back is free and immediate: the bank simply starts moving the rate again from
/// wherever the government left it. Taking it is also free - what costs is what a government
/// then does with it. See <see cref="CentralBankRule.Credibility"/>.
/// </summary>
public sealed class SetCentralBankControlCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>True hands the rate to the government, false gives it back to the bank.</summary>
    public bool GovernmentSets { get; init; }

    public bool Accepted { get; private set; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign?.NationById(IssuerId);
        if (nation is null) return;
        nation.GovernmentSetsKeyRate = GovernmentSets;
        Accepted = true;
    }
}
