using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Where the treasuries went (15 November 2026).
///
/// The construction rework put the building sites on their own ledger line and found them 2-6 %
/// of a large state's payouts. SimBench --construction, a year from January 2024 on seeds 1, 7 and
/// 42, summed the rest against budget income: the upkeep of a raised alert took 22-38 % of the
/// world's income (the United States 32-56 %, China 41-71 %), diplomacy 30 % (the median country
/// 85 %, the United States 2.6 %), covert work 7 % (the United States 15.5 %). Pinned here:
///
///   1. a diplomatic price is the listed one times the country's income against a reference, for
///      acts, subsidies, gestures, missions, talks and mediation alike, and the price charged is
///      the price shown;
///   2. a computer-run secret service spends a share of income: a purchase is earned back before
///      the next, a proxy war's weekly payment comes off the top, the player is never capped;
///   3. each country pays for its own alert, as a surcharge on its own army's pay - a holder for
///      the posture it ordered, any other country only while it is itself at war;
///   4. the ledger can say which line of code a payment left by (a bench switch, off in the game);
///   5. the defines file carries the numbers.
/// </summary>
static class MoneyDrainsTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("money drains: " + label); Console.WriteLine("PASS money drains: " + label); }

    static bool Near(float a, float b, float tolerance = 1e-5f) => MathF.Abs(a - b) <= tolerance * MathF.Max(1f, MathF.Abs(b));

    public static void Run()
    {
        DiplomacyPricesFollowIncome();
        TheChargedPriceIsTheShownPrice();
        TheComputerServiceSpendsAShareOfIncome();
        ProxyWarsComeOffTheTop();
        EachCountryPaysForItsOwnAlert();
        TheWeeklyPassChargesTheAlertOnTheLedger();
        TheLedgerNamesTheLineOfCode();
        TheDefinesFileCarriesTheRebalance();
    }

    /// <summary>Last week's budget as the ledger closed it: one revenue line, and the army's pay.</summary>
    static void Books(Nation n, float income, float armedForces = 0f)
    {
        var lines = BudgetCore.All;
        n.LedgerBudgetLastWeek = new float[lines.Count];
        var revenue = Enumerable.Range(0, lines.Count).First(i => lines[i].Side == BudgetSide.Revenue);
        n.LedgerBudgetLastWeek[revenue] = income;
        var army = Enumerable.Range(0, lines.Count).First(i => lines[i].Store == BudgetStore.MilitarySpending);
        n.LedgerBudgetLastWeek[army] = -armedForces;
    }

    static (Campaign C, Nation Rich, Nation Poor) Pair(int seed)
    {
        var c = new Campaign(seed);
        c.Defines.DiplomacyReferenceIncome = 20f;
        c.Defines.DiplomacyPriceMinimumScale = 0.0005f;
        c.Defines.DiplomacyPriceMaximumScale = 2f;
        var rich = c.AddNation(new Nation { Id = 1, MapIndex = 1, Tag = "RCH", Name = "Rich", Gdp = 20000, Population = 300_000_000, Treasury = 500f });
        var poor = c.AddNation(new Nation { Id = 2, MapIndex = 2, Tag = "POR", Name = "Poor", Gdp = 15, Population = 3_000_000, Treasury = 0.05f });
        Books(rich, 20f);
        Books(poor, 0.5f);
        return (c, rich, poor);
    }

    static void DiplomacyPricesFollowIncome()
    {
        var (c, rich, poor) = Pair(4101);
        var listed = c.Defines.DiplomaticActCost;
        Check(Near(DiplomaticActs.Cost(c, rich, DiplomaticAct.CulturalCooperation), listed),
            "a country at the reference income pays the listed price of an act");
        Check(Near(DiplomaticActs.Cost(c, poor, DiplomaticAct.CulturalCooperation), listed * 0.5f / 20f),
            "a country with a fortieth of the income pays a fortieth of the price");
        Check(Near(DiplomaticActs.Cost(c, poor, DiplomaticAct.Subsidy), c.Defines.DiplomaticSubsidy * 0.025f),
            "a subsidy is measured by the giver's income");
        Check(Near(DiplomaticGestures.Cash(c, poor, "state_visit"), c.Defines.StateVisitCash * 0.025f) &&
              Near(DiplomaticGestures.Cash(c, poor, "show_of_force"), c.Defines.ShowOfForceCash * 0.025f) &&
              Near(DiplomaticGestures.Cash(c, poor, "joint_exercises"), c.Defines.ExercisesCash * 0.025f) &&
              DiplomaticGestures.Cash(c, poor, "expel_diplomats") == 0f,
            "a gesture's cash follows the same scale; a gesture without a price stays free");
        Check(Near(Escalation.TalksPrice(c, poor), c.Defines.TalksCost * 0.025f) &&
              Near(Escalation.MediationPrice(c, rich), c.Defines.CrisisMediationCost),
            "talks and mediation follow the same scale");
        Check(DiplomaticActs.Refusal(c, poor, rich, DiplomaticAct.CulturalCooperation).Length == 0,
            $"a poor treasury ({poor.Treasury}) that could never pay the listed {listed} can pay its own price");

        Books(rich, 1000f);
        Check(Near(DiplomacyPrice.Scale(c, rich), 2f), "no country pays more than the largest multiple");
        poor.LedgerBudgetLastWeek = new float[BudgetCore.All.Count];
        Check(Near(DiplomacyPrice.Scale(c, poor), 0.0005f), "a country with no income yet pays the smallest multiple, not nothing");
        c.Defines.DiplomacyReferenceIncome = 0f;
        Check(DiplomacyPrice.Scale(c, poor) == 1f && DiplomaticActs.Cost(c, poor, DiplomaticAct.CulturalCooperation) == listed,
            "a reference of nought restores the listed price for everybody");
    }

    static void TheChargedPriceIsTheShownPrice()
    {
        var (c, rich, poor) = Pair(4102);
        var relation = CampaignDiplomacy.Get(c, poor.Id, rich.Id);
        relation.Opinion = 80f;
        relation.Stance = DiplomaticStance.Friendly;
        var before = poor.Treasury;
        Check(DiplomaticActs.TryAi(c, poor, rich), "the computer signs an act its own price allows");
        var paid = before - poor.Treasury;
        var largest = MathF.Max(c.Defines.DiplomaticSubsidy, c.Defines.DiplomaticActCost * 2.5f) * DiplomacyPrice.Scale(c, poor);
        Check(paid > 0f && paid <= largest + 1e-6f,
            $"what it paid ({paid:0.#####}) is a scaled price, at most {largest:0.#####}, not the listed {c.Defines.DiplomaticActCost}");
        var booked = -poor.LedgerWeek[MoneyLedger.FlowCount + (int)MoneyFlow.Diplomacy];
        Check(Near(booked, paid), "the payment is written on the diplomacy line of the ledger");

        // A mission fixes its weekly price when it opens, scaled to the issuer then.
        var (m, big, small) = Pair(4103);
        small.Treasury = 10f;
        small.PoliticalPoints = 100f;
        var opened = DiplomaticOutreach.Refusal(m, small.Id, big.Id, true).Length == 0 && DiplomaticOutreach.Set(m, small.Id, big.Id, true);
        var mission = opened ? DiplomaticOutreach.Of(CampaignDiplomacy.Peek(m, small.Id, big.Id), small.Id) : null;
        Check(mission is not null && Near(mission.WeeklyCash, m.Defines.DiplomaticMissionWeeklyCash * 0.025f) &&
              Near(10f - small.Treasury, m.Defines.DiplomaticMissionStartCash * 0.025f),
            "a mission's start and weekly cash are the issuer's own price");
    }

    static (Campaign C, Nation Ai, Nation Player) Service(int seed)
    {
        var c = new Campaign(seed);
        c.Defines.CovertAiIncomeShare = 0.05f;
        c.Defines.CovertAiLongestPause = 365;
        var ai = c.AddNation(new Nation { Id = 1, MapIndex = 1, Tag = "SVC", Name = "Service", Gdp = 1000, Population = 50_000_000, Treasury = 100f });
        var player = c.AddNation(new Nation { Id = 2, MapIndex = 2, Tag = "PLR", Name = "Player", Gdp = 1000, Population = 50_000_000, Treasury = 100f });
        Books(ai, 10f);
        Books(player, 10f);
        c.ConfigureSinglePlayer(player.Id);
        return (c, ai, player);
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);

    static void TheComputerServiceSpendsAShareOfIncome()
    {
        var (c, ai, player) = Service(4104);
        Check(Near(CovertBudget.WeeklyAllowance(c, ai), 0.5f), "the allowance is the share of the week's budget income");
        Check(CovertBudget.AiMaySpend(c, ai), "a service with nothing spent may buy");
        CovertBudget.AiSpent(c, ai, 2f);
        // 2 at 0.5 a week: four weeks, 28 days, before the next purchase.
        Check(ai.DecisionAvailableDay[CovertBudget.PauseKey] == 28, "a purchase is earned back from the allowance: 2 at 0.5 a week is 28 days");
        SetDay(c, 27);
        Check(!CovertBudget.AiMaySpend(c, ai), "the service waits while it is being earned back");
        SetDay(c, 28);
        Check(CovertBudget.AiMaySpend(c, ai), "and may buy again when it has been");
        CovertBudget.AiSpent(c, ai, 1000f);
        Check(ai.DecisionAvailableDay[CovertBudget.PauseKey] == 28 + 365, "no purchase holds the service back longer than the longest pause");

        CovertBudget.AiSpent(c, player, 50f);
        Check(!player.DecisionAvailableDay.ContainsKey(CovertBudget.PauseKey) && CovertBudget.AiMaySpend(c, player),
            "the player's service is never capped");
        c.Defines.CovertAiIncomeShare = 0f;
        Check(CovertBudget.AiMaySpend(c, ai) && float.IsPositiveInfinity(CovertBudget.Free(c, ai)),
            "a share of nought is no cap, whatever pause was set before");
    }

    static void ProxyWarsComeOffTheTop()
    {
        var (c, ai, _) = Service(4105);
        var weekly = ProxyWars.WeeklyCost(c, ai, 1);
        Check(weekly > 0f && weekly < 0.5f, $"one level of backing costs less than the allowance ({weekly:0.###} of 0.5)");
        c.Sponsorships.Add(new Sponsorship { Id = 1, SponsorId = ai.Id, ClientId = 2, Level = 1 });
        Check(Near(CovertBudget.Free(c, ai), 0.5f - weekly), "a proxy war's weekly payment comes off the top");
        Check(CovertBudget.AiMayCommit(c, ai, 0.5f - weekly) && !CovertBudget.AiMayCommit(c, ai, 0.5f - weekly + 0.01f),
            "a new backing is taken on only within what is left");
        c.Defines.CovertAiIncomeShare = weekly / 10f * 0.5f;
        Check(!CovertBudget.AiMaySpend(c, ai), "a service whose proxy wars already take the whole allowance buys nothing more");
    }

    static void EachCountryPaysForItsOwnAlert()
    {
        var c = Campaign.CreateBootstrap(947986506);
        c.Defines.AiNuclearPosture = false;
        c.Wars.Clear();
        c.MarkWarsChanged();
        var us = c.NationByTag("USA");
        var other = c.Nations.First(n => n.Population > 0 && n.Movement is null && Deterrence.TierOf(c, n) == 0);
        Check(Deterrence.TierOf(c, us) >= 1, "the United States holds the weapon");
        Books(us, 20f, armedForces: 10f);
        Books(other, 2f, armedForces: 1f);
        c.DefconLevel = 2;
        us.NuclearPosture = 5;
        Check(DefconSystem.UpkeepMultiplier(c, us) == 1f && DefconSystem.WeeklyUpkeep(c, us) == 0f,
            "a world at DEFCON 2 bills nothing to a holder that kept its own forces at routine readiness");
        us.NuclearPosture = 3;
        var surcharge = c.Defines.DefconUpkeepMultiplier3 - 1f;
        Check(Near(DefconSystem.WeeklyUpkeep(c, us), surcharge * 10f),
            $"a holder at its own DEFCON 3 pays the surcharge on its army's pay ({surcharge:P0} of 10)");
        Check(DefconSystem.WeeklyUpkeep(c, us) < 0.1f * 20f,
            "and it is a part of its income, not three quarters of it as a share of the whole economy was");
        Check(DefconSystem.UpkeepMultiplier(c, other) == 1f && DefconSystem.WeeklyUpkeep(c, other) == 0f,
            "a country without the weapon at peace pays nothing for the world's alert");
        c.Wars.Add(new StrategicWar { Id = 99001, AttackerId = other.Id, DefenderId = us.Id, StartedDay = 0 });
        c.MarkWarsChanged();
        var spill = (c.Defines.DefconUpkeepMultiplier2 - 1f) * 0.4f;
        Check(Near(DefconSystem.WeeklyUpkeep(c, other), spill * 1f),
            "a country without the weapon pays a part of the world's alert while it is itself at war");
        Books(other, 2f, armedForces: 0f);
        Check(DefconSystem.WeeklyUpkeep(c, other) == 0f, "a country with no army pays nothing to keep it on alert");
    }

    static void TheWeeklyPassChargesTheAlertOnTheLedger()
    {
        var c = Campaign.CreateBootstrap(947986507);
        c.Defines.AiNuclearPosture = false;
        c.Wars.Clear();
        c.MarkWarsChanged();
        var us = c.NationByTag("USA");
        Books(us, 20f, armedForces: 10f);
        us.NuclearPosture = 2;
        us.Treasury = 500f;
        us.LedgerWeek = null;
        var expected = DefconSystem.WeeklyUpkeep(c, us);
        SetDay(c, 6);
        TreasuryDynamics.Tick(c);
        var booked = -(us.LedgerWeek?[MoneyLedger.FlowCount + (int)MoneyFlow.AlertUpkeep] ?? 0f);
        Check(expected > 0f && Near(booked, expected),
            $"the weekly treasury pass charges the alert's own price ({booked:0.###} of {expected:0.###}) on its ledger line");
    }

    static void TheLedgerNamesTheLineOfCode()
    {
        var n = new Nation { Id = 7, Tag = "LDG", Name = "Ledger" };
        Check(MoneyLedger.Sources is null, "the trace is off in the game");
        MoneyLedger.Sources = new();
        try
        {
            MoneyLedger.Book(n, MoneyFlow.Covert, -1.5f);
            var key = MoneyLedger.Sources.Keys.Single();
            Check(key.Nation == 7 && key.Flow == MoneyFlow.Covert && key.Site.StartsWith("MoneyDrainsTests.cs:", StringComparison.Ordinal) &&
                  MoneyLedger.Sources[key] == -1.5,
                "a bench run learns the file and line a payment left by: " + key.Site);
        }
        finally { MoneyLedger.Sources = null; }
    }

    static void TheDefinesFileCarriesTheRebalance()
    {
        var path = Path.Combine(GameDataLoader.DataFolder(), "common", "defines", "106_money_drains.json");
        using var defines = JsonDocument.Parse(File.ReadAllText(path), GameDataLoader.ForgivingJson);
        foreach (var key in new[] { "DiplomacyReferenceIncome", "DiplomacyPriceMinimumScale", "DiplomacyPriceMaximumScale",
                     "CovertAiIncomeShare", "CovertAiLongestPause", "AlertUpkeepArmyShare" })
            Check(defines.RootElement.TryGetProperty(key, out _), "the defines file carries " + key);
        var shipped = new Campaign(4106).Defines;
        Check(shipped.DiplomacyReferenceIncome > 0f && shipped.CovertAiIncomeShare is > 0f and <= 0.2f && shipped.AlertUpkeepArmyShare > 0f,
            $"the shipped game scales diplomacy ({shipped.DiplomacyReferenceIncome}), caps the computer's service ({shipped.CovertAiIncomeShare}) " +
            $"and bills alerts on the army ({shipped.AlertUpkeepArmyShare})");
    }
}
