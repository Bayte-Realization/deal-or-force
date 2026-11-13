using System;
using DealOrForce.Simulation;

/// <summary>
/// The weekly diplomacy pass remembers a pair by its border, opinion and stance.
/// An unchanged pair is not walked again. A pair that has never been walked is.
/// A second campaign does not read the first campaign's memory.
/// </summary>
static class WeeklyDiplomacyScanTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("weekly diplomacy scan: " + label);
        Console.WriteLine("PASS weekly diplomacy scan: " + label);
    }

    public static void Run()
    {
        var identity = false;
        var first = World(out var left, out var right);
        identity = first.FeatureEnabled("state_identity");
        Check(first.NationById(0) == left && first.NationById(1) == right, "nation id is the list index");
        Check(CampaignDiplomacy.Peek(first, 0, 1) is null, "the pair starts with no relation");

        var walked = AiStatecraft.ReviewWeeklyPair(first, left, right, false, 50f, identity, out var relation);
        Check(walked && AiStatecraft.WeeklyPairWalks(first) == 1 && relation is not null,
            "a pair that has never been walked is walked");
        Check(first.Relations.Count == 1, "the walk opens the relation it is about to write");
        var opinion = relation.Opinion;
        var stance = relation.Stance;
        Check(left.AssessedStrength == 4f && right.AssessedStrength == 4f && right.AssessedDay == 0,
            "the first walk does not refresh a current strength estimate");

        var again = AiStatecraft.ReviewWeeklyPair(first, left, right, false, 50f, identity, out var same);
        Check(!again && AiStatecraft.WeeklyPairWalks(first) == 1 && ReferenceEquals(same, relation),
            "a pair with no change is not walked again");
        Check(first.Relations.Count == 1 && same.Opinion == opinion && same.Stance == stance,
            "the skipped walk does not open a second relation or move the pair");
        Check(right.AssessedStrength == 4f && right.AssessedDay == 0,
            "the skipped walk does not refresh the strength estimate");

        var second = World(out var otherLeft, out var otherRight);
        var copied = CampaignDiplomacy.Get(second, otherLeft.Id, otherRight.Id);
        copied.Opinion = opinion;
        copied.Stance = stance;
        Check(!AiStatecraft.SameWeeklyPair(second, otherLeft.Id, otherRight.Id, false, opinion, stance),
            "two campaigns do not share the scan memory");
        var otherWalked = AiStatecraft.ReviewWeeklyPair(second, otherLeft, otherRight, false, 50f,
            second.FeatureEnabled("state_identity"), out _);
        Check(otherWalked && AiStatecraft.WeeklyPairWalks(second) == 1,
            "the other campaign walks its own pair");
        Check(AiStatecraft.WeeklyPairWalks(first) == 1,
            "the other campaign does not add a walk to the first");
        var firstAgain = AiStatecraft.ReviewWeeklyPair(first, left, right, false, 50f, identity, out _);
        Check(!firstAgain && AiStatecraft.WeeklyPairWalks(first) == 1,
            "the other campaign does not erase the first campaign's memory");

        relation.Opinion = opinion + 4f;
        var opinionMoved = AiStatecraft.ReviewWeeklyPair(first, left, right, false, 50f, identity, out relation);
        Check(opinionMoved && AiStatecraft.WeeklyPairWalks(first) == 2, "a moved opinion is walked");

        relation.Stance = relation.Stance == DiplomaticStance.Rival
            ? DiplomaticStance.Neutral
            : DiplomaticStance.Rival;
        var stanceMoved = AiStatecraft.ReviewWeeklyPair(first, left, right, false, 50f, identity, out _);
        Check(stanceMoved && AiStatecraft.WeeklyPairWalks(first) == 3, "a changed stance is walked");

        var bordered = AiStatecraft.ReviewWeeklyPair(first, left, right, true, 50f, identity, out _);
        Check(bordered && AiStatecraft.WeeklyPairWalks(first) == 4, "a gained border is walked");

        var probe = World(out var asking, out var asked);
        var before = probe.Relations.Count;
        AllianceSystem.Willingness(probe, asked, asking, refreshAssessment: false);
        Check(probe.Relations.Count == before && CampaignDiplomacy.Peek(probe, asked.Id, asking.Id) is null,
            "a willingness probe does not insert a relation");
    }

    static Campaign World(out Nation left, out Nation right)
    {
        var campaign = new Campaign(61112);
        left = campaign.AddNation(new Nation
        {
            Tag = "ASK", Name = "Asker", MapIndex = 11, Population = 8_000_000, Gdp = 400,
            Treasury = 0, PoliticalPoints = 0, Stability = 0.7f, Ideology = "liberalism",
            AssessedDay = 0, AssessedStrength = 4f
        });
        right = campaign.AddNation(new Nation
        {
            Tag = "ANS", Name = "Answer", MapIndex = 12, Population = 2_000_000, Gdp = 40,
            Treasury = 0, PoliticalPoints = 0, Stability = 0.7f, Ideology = "liberalism",
            AssessedDay = 0, AssessedStrength = 4f
        });
        return campaign;
    }
}
