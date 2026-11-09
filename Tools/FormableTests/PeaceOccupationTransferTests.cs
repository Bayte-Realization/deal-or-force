using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The peace line already keeps occupation that touches home and leaves a far pocket off.
/// The sentence only names that transfer. Reading it does not move land and does not sign.
/// </summary>
static class PeaceOccupationTransferTests
{
    const string Stay = "This occupied ground stays with the holder.";
    const string Pocket = "The pocket beyond the line goes back.";
    const string BorderKept = "This border keeps the occupation that touches home.";

    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("peace transfer: " + note); Console.WriteLine("PASS peace transfer: " + note); }

    public static void Run()
    {
        Loc.Language = "english";
        Check(Loc.Get(Stay) == Stay && Loc.Get(Pocket) == Pocket,
            "the English sentence names the ground that stays or the pocket that goes back");
        Loc.Language = "russian";
        Check(Loc.Get(Stay) == "Эта оккупированная земля остаётся у того, кто её держит." &&
              Loc.Get(Pocket) == "Карман за линией возвращается.",
            "the Russian sentence names the ground that stays or the pocket that goes back");
        Loc.Language = "english";

        var both = Both();
        var line = PeaceShape.Compact(both.Campaign, both.Winner, both.Standing, both.War);
        Check(line.Contains(both.Front) && !line.Contains(both.Pocket),
            "the line still keeps the occupation that touches home and leaves the far pocket");
        var owners = (ushort[])both.Map.ProvinceToNation.Clone();
        var control = (ushort[])both.Campaign.ProvinceController.Clone();
        var before = CampaignFingerprint.Of(both.Campaign);
        var stay = PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Front }, both.War);
        Check(stay == Stay && !stay.Contains(Pocket),
            "a table that keeps the occupation touching home says it stays, got: " + stay);
        Check(PeaceShape.BorderWhy(both.Campaign, both.Winner, new[] { both.Front }, both.War) == BorderKept,
            "the older border sentence is still the one about occupation that touches home");
        Check(PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Front }, both.War) == stay,
            "reading the same table twice returns the same sentence");
        Check(Same(both.Map.ProvinceToNation, owners) && Same(both.Campaign.ProvinceController, control) &&
              !both.War.Ended && both.Campaign.Wars.Count == 0 &&
              CampaignFingerprint.Of(both.Campaign) == before,
            "reading the sentence does not move land, end the war, or change the fingerprint");

        var home = HomeOnly();
        Check(PeaceShape.TransferLine(home.Campaign, home.Winner, new[] { home.Front }, home.War) == Stay,
            "occupation that touches home, with no pocket beyond the line, stays");
        Check(PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Front, both.Pocket }, both.War) == Stay,
            "a table that also keeps the far pocket does not say the pocket goes back");
        Check(PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Pocket }, both.War).Length == 0,
            "keeping only the far pocket is not a transfer the line decided");
        Check(PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Claim }, both.War) == Pocket,
            "a table that keeps neither the home occupation nor the far pocket says the pocket goes back");
        Check(PeaceShape.TransferLine(both.Campaign, both.Winner, Array.Empty<int>(), both.War).Length == 0 &&
              PeaceShape.TransferLine(null, both.Winner, new[] { both.Front }, both.War).Length == 0,
            "an empty table and a missing campaign say nothing");

        var tooth = Tooth();
        var toothLine = PeaceShape.Compact(tooth.Campaign, tooth.Winner, tooth.Standing, tooth.War);
        Check(!toothLine.Contains(tooth.Tooth) && toothLine.Contains(tooth.Front),
            "the line still drops the one-province tooth");
        Check(PeaceShape.TransferLine(tooth.Campaign, tooth.Winner, new[] { tooth.Tooth }, tooth.War).Length == 0,
            "a tooth put back on the table is not occupation the line keeps");

        var quiet = Both();
        foreach (var province in quiet.Standing)
            quiet.Campaign.ProvinceController[province] = (ushort)quiet.Loser.MapIndex;
        Check(PeaceShape.TransferLine(quiet.Campaign, quiet.Winner, new[] { quiet.Front }, quiet.War).Length == 0,
            "without an occupation there is nothing to transfer");

        both.Winner.IsHuman = true;
        var humanOwners = (ushort[])both.Map.ProvinceToNation.Clone();
        var humanLine = PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Front }, both.War);
        Check(humanLine == Stay && Same(both.Map.ProvinceToNation, humanOwners) && !both.War.Ended,
            "a human's land stays, and the sentence still does not sign");

        var other = Both();
        var firstPrint = CampaignFingerprint.Of(both.Campaign);
        var secondPrint = CampaignFingerprint.Of(other.Campaign);
        Check(PeaceShape.TransferLine(other.Campaign, other.Winner, new[] { other.Claim }, other.War) == Pocket &&
              CampaignFingerprint.Of(both.Campaign) == firstPrint &&
              CampaignFingerprint.Of(other.Campaign) == secondPrint,
            "reading the second campaign does not write the first");

        both.Campaign.Wars.Add(both.War);
        var demand = new PeaceDemand { WarId = both.War.Id, ProposerId = both.Winner.Id };
        demand.Provinces.Add(both.Front);
        both.War.PendingOffer = demand;
        both.War.PendingOfferIsWritten = true;
        both.War.PendingOfferFromId = both.Winner.Id;
        var paperBefore = CampaignFingerprint.Of(both.Campaign);
        var paper = PeaceOffers.Describe(both.Campaign, both.War);
        Check(paper.Contains(Stay) && paper.Contains(BorderKept) && !paper.Contains(Pocket) &&
              !both.War.Ended && CampaignFingerprint.Of(both.Campaign) == paperBefore,
            "the peace paper names the stay and does not sign, got: " + paper);

        demand.Provinces.Clear();
        demand.Provinces.Add(both.Claim);
        var pocketPaper = PeaceOffers.Describe(both.Campaign, both.War);
        Check(pocketPaper.Contains(Pocket) && !pocketPaper.Contains(Stay) && !both.War.Ended &&
              Same(both.Map.ProvinceToNation, humanOwners),
            "the peace paper names the pocket when that is the transfer, and does not sign");

        demand.Provinces.Clear();
        demand.Provinces.Add(both.Front);
        demand.ReleaseRegions = true;
        var release = PeaceOffers.Describe(both.Campaign, both.War);
        Check(!release.Contains(Stay) && !release.Contains(Pocket) && release.Contains("independent") &&
              !both.War.Ended,
            "an independent release is not an occupation transfer");
        demand.ReleaseRegions = false;

        Loc.Language = "russian";
        var russianPrint = CampaignFingerprint.Of(both.Campaign);
        Check(PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Front }, both.War) ==
              "Эта оккупированная земля остаётся у того, кто её держит." &&
              PeaceOffers.Describe(both.Campaign, both.War).Contains("остаётся у того, кто её держит") &&
              CampaignFingerprint.Of(both.Campaign) == russianPrint && !both.War.Ended,
            "the Russian paper names the stay and does not sign");
        Loc.Language = "english";

        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(both.Campaign, null);
        var quietPrint = CampaignFingerprint.Of(both.Campaign);
        Check(PeaceShape.TransferLine(both.Campaign, both.Winner, new[] { both.Front }, both.War).Length == 0 &&
              CampaignFingerprint.Of(both.Campaign) == quietPrint,
            "without a controller there is nothing to transfer");
    }

    sealed class Board
    {
        public Campaign Campaign;
        public Nation Winner;
        public Nation Loser;
        public ProvinceTopology Map;
        public StrategicWar War;
        public int[] Standing;
        public int Front;
        public int Pocket;
        public int Claim;
        public int Tooth;
    }

    static Board Both()
    {
        var board = Map(611096, new (int x, int y, int region, int owner)[]
        {
            (0, 0, 3, 2), (2, 1, 1, 1), (3, 1, 2, 2), (6, 1, 7, 2)
        });
        board.Front = At(board.Map, 2);
        board.Claim = At(board.Map, 3);
        board.Pocket = At(board.Map, 7);
        Hold(board, board.Front, board.Pocket);
        return board;
    }

    static Board HomeOnly()
    {
        var board = Map(611097, new (int x, int y, int region, int owner)[]
        {
            (2, 1, 1, 1), (3, 1, 2, 2)
        });
        board.Front = At(board.Map, 2);
        Hold(board, board.Front);
        return board;
    }

    static Board Tooth()
    {
        var board = Map(611098, new (int x, int y, int region, int owner)[]
        {
            (1, 1, 4, 2), (2, 1, 1, 1), (2, 2, 1, 1), (3, 1, 2, 2), (3, 2, 3, 2)
        });
        board.Tooth = At(board.Map, 4);
        board.Front = At(board.Map, 2);
        Hold(board, board.Tooth, board.Front, At(board.Map, 3));
        return board;
    }

    static void Hold(Board board, params int[] provinces)
    {
        foreach (var province in provinces)
            board.Campaign.ProvinceController[province] = (ushort)board.Winner.MapIndex;
        board.Standing = provinces;
    }

    static Board Map(int seed, (int x, int y, int region, int owner)[] cells)
    {
        const int width = 8, height = 3;
        var campaign = new Campaign(seed);
        var winner = new Nation { Id = 0, MapIndex = 1, Tag = "WIN", Name = "Winner", Population = 100, CapitalStateMapIndex = 1 };
        var loser = new Nation { Id = 1, MapIndex = 2, Tag = "LOS", Name = "Loser", Population = 100, CapitalStateMapIndex = 7 };
        campaign.Nations.Add(winner);
        campaign.Nations.Add(loser);
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
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[map.ProvinceCount + 1]);
        return new Board
        {
            Campaign = campaign,
            Winner = winner,
            Loser = loser,
            Map = map,
            War = new StrategicWar { Id = 1, AttackerId = winner.Id, DefenderId = loser.Id }
        };
    }

    static int At(ProvinceTopology map, int region)
    {
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToState[province] == region) return province;
        return 0;
    }

    static bool Same(ushort[] left, ushort[] right)
    {
        if (left is null || right is null || left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
        return true;
    }
}
