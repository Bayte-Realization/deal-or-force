using System;
using DealOrForce.Simulation;

/// <summary>
/// A player's report: "Russia's GDP keeps falling, year after year". Measured with
/// `SimBench --gdp-watch`, Russia's own decline is its wars, its overstretch and its stability;
/// what it found beside it was a whole class of peaceful, stable countries - Switzerland,
/// Czechia, Singapore, Hong Kong, Austria - losing a third to a half of their output in ten
/// years while their computer kept building. Switzerland went from 11 buildings to 29 for the
/// same 5.3 million workers and its GDP from 703 to 341.
///
/// The cause: a country's plant is multiplied by the share of its jobs it can staff
/// (EmployedShare = workers / jobs) AND by the diminishing return on capital
/// (MarketSystem.CapitalReturns), and the second measured the unstaffed plant as well. Past full
/// employment a building nobody can work already adds nothing - the workers are only spread
/// thinner - and the capital penalty then took a further share of everything: output ∝
/// workers × (jobs/workers)^(share-1), falling with every building. The diminishing return is
/// now measured on the plant the country can staff; with workers to spare it is unchanged.
/// </summary>
static class CapitalReturnsTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("capital returns: " + label); Console.WriteLine("PASS capital returns: " + label); }

    /// <summary>What a plant of <paramref name="jobs"/> rated jobs makes for <paramref name="workers"/>, at unit output per job.</summary>
    static float Output(Campaign c, Nation n, float jobs, float workers) =>
        jobs * MarketSystem.StaffingShare(jobs, workers) * MarketSystem.CapitalReturns(c, n, jobs, workers);

    public static void Run()
    {
        var c = new Campaign(20261114) { ScenarioId = "capital_returns_fixture" };
        // A Swiss start: 0.75 jobs for every worker, so the diminishing return begins at
        // 0.75 x 1.3 = 0.975 jobs per worker, just short of full employment.
        var n = new Nation { Tag = "CHE", CapitalIntensityReference = 0.75f };
        const float Workers = 5.3f;
        Check(c.Defines.CapitalDeepeningHeadroom > 0f && c.Defines.CapitalDeepeningShare < 1f,
            "the diminishing return on capital is switched on in the shipped defines");

        // With workers to spare, past the headroom, more plant still makes more - but less than
        // in proportion: the reason the return exists (a rich country compounding) still holds.
        // (A start of half a job per worker, so the headroom ends at 0.65 and there is room past it.)
        var roomy = new Nation { Tag = "USA", CapitalIntensityReference = 0.5f };
        var slackLow = Output(c, roomy, 4.0f, 6.0f);
        var slackHigh = Output(c, roomy, 5.5f, 6.0f);
        Check(MarketSystem.CapitalReturns(c, roomy, 5.5f, 6.0f) < 1f && slackHigh > slackLow && slackHigh / slackLow < 5.5f / 4.0f,
            $"with workers to spare a building past the headroom adds less than the one before it ({slackLow:0.000} -> {slackHigh:0.000})");

        // Short of workers, a building nobody can staff adds nothing - and never takes output away.
        var previous = Output(c, n, Workers, Workers);
        var previousJobs = Workers;
        foreach (var jobs in new[] { 6f, 7.5f, 10f, 13f, 20f })
        {
            var output = Output(c, n, jobs, Workers);
            Check(output >= previous - 1e-4f,
                $"{jobs:0.0} jobs for {Workers} million workers make no less than {previousJobs:0.0} did ({output:0.000} >= {previous:0.000})");
            previous = output;
            previousJobs = jobs;
        }
        Check(Output(c, n, 13f, Workers) >= Output(c, n, Workers, Workers) * 0.98f,
            $"Switzerland's 29 buildings make what its 11 did, not two thirds of it ({Output(c, n, 13f, Workers):0.000} vs {Output(c, n, Workers, Workers):0.000})");

        // A country that began over-built (Russia: 1.15 jobs a worker) never pays the capital
        // return for plant it cannot staff.
        var russia = new Nation { Tag = "RUS", CapitalIntensityReference = 1.153f };
        Check(MarketSystem.CapitalReturns(c, russia, 126f, 83.6f) == 1f,
            "a labour-bound country's unstaffed plant costs it nothing beyond the workers it lacks");
    }
}
