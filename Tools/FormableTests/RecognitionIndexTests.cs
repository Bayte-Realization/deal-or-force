using System;
using DealOrForce.Simulation;

/// <summary>
/// Recognition does not ask every country for the same id twice. A renamed region is
/// still found. A second campaign does not read the first campaign's index.
/// </summary>
static class RecognitionIndexTests
{
    static int _passed;

    static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("recognition index: " + note);
        _passed++;
        Console.WriteLine("PASS recognition index: " + note);
    }

    public static void Run()
    {
        var (first, movement) = World();
        var (second, otherMovement) = World();
        Check(first.NationById(0) == first.Nations[0] && first.NationById(2) == movement &&
              second.NationById(2) == otherMovement,
            "nation id is the list index");

        CampaignDiplomacy.Get(first, 2, 3).Opinion = 40f;
        CampaignDiplomacy.Get(first, 2, 1).Opinion = 25f;
        CampaignDiplomacy.Get(first, 2, 4).Opinion = 90f;
        var fingerprint = CampaignFingerprint.Of(first);
        var walked = MovementAi.CountryWalks;
        var (asked, warmest) = MovementAi.WarmestRecogniser(first, movement, movement.Movement);
        var brute = Brute(first, movement, movement.Movement);
        Check(asked == first.Nations[3] && warmest == 40f && asked == brute,
            "the warmest landed country is chosen");
        Check(MovementAi.LastNationVisits < first.Nations.Count && MovementAi.CountryWalks == walked,
            "the lookup does not walk every country");
        var again = MovementAi.WarmestRecogniser(first, movement, movement.Movement);
        Check(again.Asked == asked && MovementAi.CountryWalks == walked &&
              MovementAi.LastNationVisits < first.Nations.Count,
            "the same id does not walk every country");
        Check(CampaignFingerprint.Of(first) == fingerprint,
            "reading the index does not change the fingerprint");

        CampaignDiplomacy.Get(first, 2, 3).Opinion = 10f;
        var shifted = MovementAi.WarmestRecogniser(first, movement, movement.Movement);
        Check(shifted.Asked == first.Nations[1] && shifted.Warmest == 25f &&
              MovementAi.CountryWalks == walked,
            "a changed opinion is read without walking every country");

        var ties = World().Item1;
        var tieMovement = ties.Nations[2];
        CampaignDiplomacy.Get(ties, 2, 3).Opinion = 40f;
        CampaignDiplomacy.Get(ties, 2, 1).Opinion = 40f;
        Check(MovementAi.WarmestRecogniser(ties, tieMovement, tieMovement.Movement).Asked == ties.Nations[1] &&
              Brute(ties, tieMovement, tieMovement.Movement) == ties.Nations[1],
            "an equal opinion keeps the earlier country");

        var cold = World().Item1;
        var coldMovement = cold.Nations[2];
        CampaignDiplomacy.Get(cold, 2, 0).Opinion = -30f;
        CampaignDiplomacy.Get(cold, 2, 1).Opinion = -5f;
        CampaignDiplomacy.Get(cold, 2, 3).Opinion = -10f;
        Check(MovementAi.WarmestRecogniser(cold, coldMovement, coldMovement.Movement).Asked == cold.Nations[1] &&
              Brute(cold, coldMovement, coldMovement.Movement) == cold.Nations[1],
            "when every opinion is negative the least cold country is chosen");

        movement.Movement.RecognisedBy.Add(3);
        Check(MovementAi.WarmestRecogniser(first, movement, movement.Movement).Asked == first.Nations[1],
            "a country that already recognises it is not asked");

        CampaignDiplomacy.Get(second, 2, 0).Opinion = 70f;
        var secondAsked = MovementAi.WarmestRecogniser(second, otherMovement, otherMovement.Movement).Asked;
        Check(secondAsked == second.Nations[0] && !MovementAi.SharesOpinionIndex(first, second),
            "two campaigns do not share the recognition index");
        CampaignDiplomacy.Get(first, 2, 1).Opinion = 5f;
        Check(MovementAi.WarmestRecogniser(second, otherMovement, otherMovement.Movement).Asked == second.Nations[0],
            "the other campaign keeps its own answer");

        var home = Movements.StateById(first, "home");
        Check(home == first.States[0], "the region id resolves");
        first.States[0].Id = "renamed";
        Movements.NoteRegionRenamed(first);
        Check(Movements.StateById(first, "renamed") == first.States[0],
            "a rename still resolves");
        Check(Movements.StateById(second, "home") == second.States[0] &&
              Movements.StateById(second, "renamed") == null,
            "a second campaign does not use the renamed id");

        var exileA = first.Nations[2];
        exileA.Movement.Exile = true;
        exileA.Movement.Landless = true;
        exileA.Population = 0;
        var exileB = second.Nations[2];
        exileB.Movement.Exile = true;
        exileB.Movement.Landless = true;
        exileB.Population = 0;
        Restoration.BestRegion(first, exileA);
        Restoration.BestRegion(second, exileB);
        Check(!Restoration.SharesOrderIndex(first, second),
            "two campaigns do not share the region index");

        Console.WriteLine($"PASS recognition index: {_passed} checks");
    }

    static Nation Brute(Campaign campaign, Nation nation, MovementState state)
    {
        Nation asked = null;
        var warmest = float.MinValue;
        var nations = campaign.Nations;
        for (var i = 0; i < nations.Count; i++)
        {
            var other = nations[i];
            if (other.Movement is not null || other.Id == nation.Id || state.RecognisedBy.Contains(other.Id)) continue;
            var opinion = CampaignDiplomacy.Peek(campaign, other.Id, nation.Id)?.Opinion ?? 0f;
            if (opinion <= warmest) continue;
            if (!campaign.HasOwnedLand(other)) continue;
            warmest = opinion;
            asked = other;
        }
        return asked;
    }

    static (Campaign Campaign, Nation Movement) World()
    {
        var campaign = new Campaign(12014);
        campaign.AddNation(new Nation { Tag = "AAA", Name = "Early", MapIndex = 1, Population = 100 });
        campaign.AddNation(new Nation { Tag = "BBB", Name = "Mid", MapIndex = 2, Population = 100 });
        campaign.AddNation(new Nation
        {
            Tag = "MOV", Name = "Movement", MapIndex = 3, Population = 100,
            Movement = new MovementState { Id = "recog", Dissolved = false }
        });
        campaign.AddNation(new Nation { Tag = "CCC", Name = "Late", MapIndex = 4, Population = 100 });
        campaign.AddNation(new Nation { Tag = "DRY", Name = "Landless", MapIndex = 5, Population = 0 });
        campaign.States.Add(new ScenarioState { Id = "home", Name = "Home", MapIndex = 100, NationMapIndex = 1, Population = 50 });
        return (campaign, campaign.Nations[2]);
    }
}
