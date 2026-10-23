using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A covert operation that moves the civic road rather than the economic anger.
///
/// The point of the check is that the two purchases stay different. Stirring unrest raises one
/// region's radicalism (the unrest map); funding a separatist cause moves that region's opinion
/// away from its centre and lowers its trust in that centre's government and laws
/// (PoliticalCohorts). A player who pays for one must not receive the other.
///
/// Reading the price, the refusal or the odds must not touch the world: these are quotes, and a
/// quote that changes the thing it quotes is a bug a player finds before the developers do.
/// </summary>
public static class CovertPoliticalTests
{
    private const int Owner = 0, Target = 1, HotRegion = 2002;

    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL covert political: " + text);
        Console.WriteLine("PASS covert political: " + text);
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < .00001f;

    public static Campaign Fixture(int seed = 982)
    {
        var c = new Campaign(seed) { ScenarioId = "covert_separatism" };
        c.Defines.HoursPerTick = 24;
        // No government nobody plays may add operations of its own here: the fixture is about
        // one order and its consequence.
        c.Defines.CovertAiReviewDays = 100000;
        for (var id = 0; id < 2; id++)
            c.Nations.Add(new Nation
            {
                Id = id, Tag = "CP" + id, Name = "Political country " + id, MapIndex = 100 + id,
                Population = 1000000, Gdp = 200, Treasury = 100, MilitarySpending = .05f, StockpilesSeeded = true
            });
        c.States.Add(new ScenarioState
        {
            Id = "cp_hot", Name = "Hot region", MapIndex = HotRegion, NationMapIndex = 101, NationId = "CP1",
            Population = 100000, PoliticalDivergence = .30f, GovernmentApproval = .40f, LawApproval = .45f, Radicalism = .10f
        });
        c.NationById(Owner).IntelNetworks[Target] = .6f;
        return c;
    }

    /// <summary>
    /// Run the operation until it works, and report the deltas on the region. The roll is a hash
    /// of the campaign seed and the day, and the day cannot be moved without running the whole
    /// simulation, so the seed is what varies here: this measures what success does, not whether
    /// any particular world must see it on its first day.
    /// </summary>
    private static (bool Hit, float Divergence, float Government, float Law, float Radicalism, float Pressure)
        Attempt(string kind)
    {
        for (var seed = 982; seed < 1382; seed++)
        {
            var c = Fixture(seed);
            var region = c.States.Find(s => s.MapIndex == HotRegion);
            var owner = c.NationById(Owner);
            var divergence = region.PoliticalDivergence;
            var government = region.GovernmentApproval;
            var law = region.LawApproval;
            var radicalism = region.Radicalism;
            var pressure = region.SecessionPressure;
            owner.CovertOperations.Add(new CovertOperation
            {
                Kind = kind, TargetId = Target, RegionMapIndex = HotRegion,
                StartedDay = 0, DueDay = 0, Spent = 1f
            });
            CovertOperations.Tick(c);
            var hit = kind == "unrest" ? region.Radicalism > radicalism : region.PoliticalDivergence > divergence;
            if (hit)
                return (true, region.PoliticalDivergence - divergence, region.GovernmentApproval - government,
                    region.LawApproval - law, region.Radicalism - radicalism, region.SecessionPressure - pressure);
        }
        return (false, 0f, 0f, 0f, 0f, 0f);
    }

    public static void Run()
    {
        var kind = CovertOperations.Find("separatism");
        Check(kind is not null && kind.Effect == "separatism", "the shipped data defines a separatist operation");
        Check(kind is not null && kind.Regional && kind.Days > 0 && kind.MinimumStrength > 0f && kind.MinimumStrength < 1f,
            "it is aimed at one region and priced in days and network strength");

        // What success does, and what it must not do.
        var hit = Attempt("separatism");
        Check(hit.Hit, "a funded separatist cause moves the region away from its centre");
        Check(Near(hit.Divergence, .06f + .08f * .6f), "the drift is the definition's power scaled by the network's strength");
        Check(Near(hit.Government, -.05f) && Near(hit.Law, -.03f),
            "the region trusts that centre's government and its laws less, by the defined amounts");
        Check(Near(hit.Radicalism, 0f), "it does not make the region angrier: radicalism belongs to stirring unrest");
        Check(Near(hit.Pressure, 0f), "it does not write the secession pressure that the federal break reads");

        var unrest = Attempt("unrest");
        Check(unrest.Hit && unrest.Radicalism > 0f && Near(unrest.Divergence, 0f) && Near(unrest.Government, 0f),
            "stirring unrest still raises radicalism and leaves the civic numbers alone");

        // Refusals come from the same definition the execution reads.
        var unread = Fixture();
        unread.States.Find(s => s.MapIndex == HotRegion).PoliticalDivergence = -1f;
        Check(CovertOperations.Refusal(unread, unread.NationById(Owner), unread.NationById(Target), kind,
                unread.States.Find(s => s.MapIndex == HotRegion)).Length > 0,
            "a region whose politics have never been read is refused, not filled with an invented opinion");

        var noCohorts = Fixture();
        noCohorts.SetFeature("political_cohorts", false);
        Check(CovertOperations.Refusal(noCohorts, noCohorts.NationById(Owner), noCohorts.NationById(Target), kind,
                noCohorts.States.Find(s => s.MapIndex == HotRegion)).Length > 0,
            "without political cohorts there is no civic road to pay for");
        var treasuryBefore = noCohorts.NationById(Owner).Treasury;
        new CovertOperationCommand { IssuerId = Owner, TargetId = Target, OperationId = "separatism", RegionMapIndex = HotRegion }
            .Apply(noCohorts);
        Check(Near(noCohorts.NationById(Owner).Treasury, treasuryBefore) &&
            noCohorts.NationById(Owner).CovertOperations.Count == 0,
            "a refused order costs nothing and starts nothing");

        // The order itself: the price quoted is the price paid, and the region is named.
        var order = Fixture();
        var owner = order.NationById(Owner);
        var price = CovertOperations.Cost(order, owner, kind);
        var treasury = owner.Treasury;
        var command = new CovertOperationCommand
        {
            IssuerId = Owner, TargetId = Target, OperationId = "separatism", RegionMapIndex = HotRegion
        };
        command.Apply(order);
        Check(command.Refusal.Length == 0 && Near(owner.Treasury, treasury - price),
            "ordering it spends exactly the price it quoted");
        var running = owner.CovertOperations.SingleOrDefault();
        Check(running is not null && running.Kind == "separatism" && running.RegionMapIndex == HotRegion &&
            running.DueDay == order.ElapsedDays + kind.Days,
            "the order names the region and the day it comes due");

        // Reading changes nothing.
        var quote = Fixture();
        var fingerprint = CampaignFingerprint.Of(quote);
        CovertOperations.Cost(quote, quote.NationById(Owner), kind);
        CovertOperations.SuccessChance(quote, quote.NationById(Owner), quote.NationById(Target));
        CovertOperations.ExposureChance(quote, quote.NationById(Target), kind, true);
        CovertOperations.Refusal(quote, quote.NationById(Owner), quote.NationById(Target), kind,
            quote.States.Find(s => s.MapIndex == HotRegion));
        Check(CampaignFingerprint.Of(quote) == fingerprint, "quoting price, odds and refusal changes nothing");

        var seen = Fixture();
        var before = CampaignFingerprint.Of(seen);
        seen.States.Find(s => s.MapIndex == HotRegion).PoliticalDivergence = .31f;
        Check(CampaignFingerprint.Of(seen) != before,
            "the region's opinion is authoritative state, so a peer can see the drift");

    }
}
