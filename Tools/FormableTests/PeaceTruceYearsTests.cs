using System;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The written settlement already keeps five years, or ten when the long truce is on the
/// demand. The sentence only names that length. Reading it does not move land and does not sign.
/// </summary>
static class PeaceTruceYearsTests
{
    const string Five = "This settlement keeps a five-year truce.";
    const string Ten = "This settlement keeps a ten-year truce.";

    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("peace truce: " + note); Console.WriteLine("PASS peace truce: " + note); }

    public static void Run()
    {
        Loc.Language = "english";
        Check(Loc.Get(Five) == Five && Loc.Get(Ten) == Ten,
            "the English sentence names five years or ten");
        Loc.Language = "russian";
        Check(Loc.Get(Five) == "Это соглашение оставляет пятилетнее перемирие." &&
              Loc.Get(Ten) == "Это соглашение оставляет десятилетнее перемирие.",
            "the Russian sentence names five years or ten");
        Loc.Language = "english";

        Check(PeaceConference.TruceLine(null).Length == 0, "a missing demand says nothing");
        Check(PeaceConference.TruceYears(new PeaceDemand()) == 5 && PeaceConference.TruceLine(new PeaceDemand()) == Five,
            "an ordinary demand keeps five years");
        var longDemand = new PeaceDemand { LongTruce = true };
        Check(PeaceConference.TruceYears(longDemand) == 10 && PeaceConference.TruceLine(longDemand) == Ten &&
              365 * PeaceConference.TruceYears(new PeaceDemand()) == 365 * 5 &&
              365 * PeaceConference.TruceYears(longDemand) == 365 * 10,
            "the long truce keeps ten years, the same day counts the signature already uses");

        var board = Map(611094);
        var owners = (ushort[])board.Map.ProvinceToNation.Clone();
        var control = (ushort[])board.Campaign.ProvinceController.Clone();
        var before = CampaignFingerprint.Of(board.Campaign);
        var line = PeaceConference.TruceLine(board.Demand);
        Check(line == Five && PeaceConference.TruceLine(board.Demand) == line,
            "reading the same demand twice returns the same five-year sentence");
        Check(Same(board.Map.ProvinceToNation, owners) && Same(board.Campaign.ProvinceController, control) &&
              !board.War.Ended && board.Campaign.Wars.Count == 1 &&
              CampaignFingerprint.Of(board.Campaign) == before,
            "reading the sentence does not move land, end the war, or change the fingerprint");

        var paperBefore = CampaignFingerprint.Of(board.Campaign);
        var paper = PeaceOffers.Describe(board.Campaign, board.War);
        Check(paper.Contains(Five) && !paper.Contains(Ten) && !paper.Contains("instead of five") &&
              paper.Contains("1 province") && !board.War.Ended &&
              CampaignFingerprint.Of(board.Campaign) == paperBefore,
            "the peace paper names the five-year truce and does not sign, got: " + paper);

        board.Demand.LongTruce = true;
        var longPaper = PeaceOffers.Describe(board.Campaign, board.War);
        Check(longPaper.Contains(Ten) && !longPaper.Contains(Five) && !longPaper.Contains("instead of five") &&
              !board.War.Ended && board.Campaign.Wars.Contains(board.War),
            "a demand that already asks for the long truce names ten years once");
        board.Demand.Reparations = true;
        var withIndemnity = PeaceOffers.Describe(board.Campaign, board.War);
        Check(withIndemnity.Contains("an indemnity") && withIndemnity.Contains(Ten) &&
              !withIndemnity.Contains("instead of five") && !board.War.Ended,
            "the ten-year sentence stays one fact beside the other terms");
        board.Demand.LongTruce = false;
        board.Demand.Reparations = false;

        board.Demand.Provinces.Clear();
        var empty = PeaceOffers.Describe(board.Campaign, board.War);
        Check(empty.Contains(Five) && empty.Contains("pre-war border") && !board.War.Ended,
            "a settlement with no ground still names the five-year truce");

        board.Demand.Provinces.Add(board.Front);
        board.Demand.ReleaseRegions = true;
        var release = PeaceOffers.Describe(board.Campaign, board.War);
        Check(release.Contains(Five) && release.Contains("independent") && !board.War.Ended &&
              Same(board.Map.ProvinceToNation, owners),
            "an independent release still names the truce and does not sign");
        board.Demand.ReleaseRegions = false;

        board.Winner.IsHuman = true;
        var humanOwners = (ushort[])board.Map.ProvinceToNation.Clone();
        var humanPrint = CampaignFingerprint.Of(board.Campaign);
        var humanLine = PeaceConference.TruceLine(board.Demand);
        var humanPaper = PeaceOffers.Describe(board.Campaign, board.War);
        Check(humanLine == Five && humanPaper.Contains(Five) && Same(board.Map.ProvinceToNation, humanOwners) &&
              !board.War.Ended && CampaignFingerprint.Of(board.Campaign) == humanPrint,
            "a human's land stays, and the sentence still does not sign");

        var other = Map(611095);
        var firstPrint = CampaignFingerprint.Of(board.Campaign);
        var secondPrint = CampaignFingerprint.Of(other.Campaign);
        Check(PeaceConference.TruceLine(other.Demand) == Five &&
              PeaceOffers.Describe(other.Campaign, other.War).Contains(Five) &&
              CampaignFingerprint.Of(board.Campaign) == firstPrint &&
              CampaignFingerprint.Of(other.Campaign) == secondPrint,
            "reading the second campaign does not write the first");

        Loc.Language = "russian";
        var russianPrint = CampaignFingerprint.Of(board.Campaign);
        Check(PeaceConference.TruceLine(board.Demand) == "Это соглашение оставляет пятилетнее перемирие." &&
              PeaceOffers.Describe(board.Campaign, board.War).Contains("пятилетнее перемирие") &&
              CampaignFingerprint.Of(board.Campaign) == russianPrint && !board.War.Ended,
            "the Russian paper names the five-year truce and does not sign");
        Loc.Language = "english";
    }

    sealed class Board
    {
        public Campaign Campaign;
        public Nation Winner;
        public ProvinceTopology Map;
        public StrategicWar War;
        public PeaceDemand Demand;
        public int Front;
    }

    static Board Map(int seed)
    {
        const int width = 8, height = 3;
        var campaign = new Campaign(seed);
        var winner = new Nation { Id = 0, MapIndex = 1, Tag = "WIN", Name = "Winner", Population = 100, CapitalStateMapIndex = 1 };
        var loser = new Nation { Id = 1, MapIndex = 2, Tag = "LOS", Name = "Loser", Population = 100, CapitalStateMapIndex = 2 };
        campaign.Nations.Add(winner);
        campaign.Nations.Add(loser);
        var cells = new (int x, int y, int region, int owner)[] { (2, 1, 1, 1), (3, 1, 2, 2) };
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        foreach (var cell in cells)
        {
            states[cell.y * width + cell.x] = (ushort)cell.region;
            owners[cell.y * width + cell.x] = (ushort)cell.owner;
            campaign.States.Add(new ScenarioState
            {
                MapIndex = cell.region, NationMapIndex = cell.owner, Id = "r" + cell.region,
                Name = "Region " + cell.region, Population = 1000
            });
        }
        var map = ProvinceTopology.Build(states, owners, width, height, _ => false);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])map.ProvinceToNation.Clone());
        var front = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToState[province] == 2) { front = province; break; }
        var war = new StrategicWar { Id = 1, AttackerId = winner.Id, DefenderId = loser.Id };
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
        demand.Provinces.Add(front);
        war.PendingOffer = demand;
        war.PendingOfferIsWritten = true;
        war.PendingOfferFromId = winner.Id;
        campaign.Wars.Add(war);
        return new Board
        {
            Campaign = campaign, Winner = winner, Map = map, War = war, Demand = demand, Front = front
        };
    }

    static bool Same(ushort[] left, ushort[] right)
    {
        if (left is null || right is null || left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}
