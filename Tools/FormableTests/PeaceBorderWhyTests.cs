using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The peace line already decides three facts. The sentence only names the one the border
/// on the table actually shows. Reading it does not move land and does not sign.
/// </summary>
static class PeaceBorderWhyTests
{
    const string Hole = "A pocket shut inside the line is kept with this border.";
    const string Tooth = "A one-province tooth of the occupation was left off this border.";
    const string Kept = "This border keeps the occupation that touches home.";

    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("peace border: " + note); Console.WriteLine("PASS peace border: " + note); }

    public static void Run()
    {
        Loc.Language = "english";
        Check(Loc.Get(Hole) == Hole && Loc.Get(Tooth) == Tooth && Loc.Get(Kept) == Kept,
            "the English sentence names the pocket, the tooth, or the occupation that touches home");
        Loc.Language = "russian";
        Check(Loc.Get(Hole).Contains("Карман") && Loc.Get(Hole).Contains("границей") &&
              Loc.Get(Tooth).Contains("выступ") && Loc.Get(Kept).Contains("оккупацию"),
            "the Russian sentence names the pocket, the tooth, or the occupation");
        Loc.Language = "english";

        var shaped = Shaped();
        var line = PeaceShape.Compact(shaped.Campaign, shaped.Winner, shaped.Standing, shaped.War);
        Check(line.Contains(shaped.Hole) && !line.Contains(shaped.Tooth) && !line.Contains(shaped.Pocket) &&
              line.Contains(shaped.Front),
            "the line still takes the shut pocket, drops the tooth, and leaves the far occupation");
        var owners = (ushort[])shaped.Map.ProvinceToNation.Clone();
        var control = (ushort[])shaped.Campaign.ProvinceController.Clone();
        var before = CampaignFingerprint.Of(shaped.Campaign);
        var why = PeaceShape.BorderWhy(shaped.Campaign, shaped.Winner, line, shaped.War);
        Check(why == Hole, "a border that keeps the shut pocket says so, got: " + why);
        Check(PeaceShape.BorderWhy(shaped.Campaign, shaped.Winner, line, shaped.War) == why,
            "reading the same border twice returns the same sentence");
        Check(Same(shaped.Map.ProvinceToNation, owners) && Same(shaped.Campaign.ProvinceController, control) &&
              !shaped.War.Ended && shaped.Campaign.Wars.Count == 0 &&
              CampaignFingerprint.Of(shaped.Campaign) == before,
            "reading the sentence does not move land, end the war, or change the fingerprint");

        var withoutHole = line.Where(province => province != shaped.Hole).ToList();
        Check(PeaceShape.BorderWhy(shaped.Campaign, shaped.Winner, withoutHole, shaped.War) == Tooth,
            "a border that left the tooth off says so");
        var occupied = shaped.Standing.ToList();
        Check(PeaceShape.BorderWhy(shaped.Campaign, shaped.Winner, occupied, shaped.War).Length == 0,
            "the occupation as it stands, tooth and far pocket included, is not given a reason");
        Check(PeaceShape.BorderWhy(shaped.Campaign, shaped.Winner, Array.Empty<int>(), shaped.War).Length == 0 &&
              PeaceShape.BorderWhy(null, shaped.Winner, line, shaped.War).Length == 0,
            "an empty table and a missing campaign say nothing");

        var toothOnly = ToothOnly();
        var toothLine = PeaceShape.Compact(toothOnly.Campaign, toothOnly.Winner, toothOnly.Standing, toothOnly.War);
        Check(!toothLine.Contains(toothOnly.Tooth) && toothLine.Contains(toothOnly.Front),
            "a one-province tooth is not part of the line");
        Check(PeaceShape.BorderWhy(toothOnly.Campaign, toothOnly.Winner, toothLine, toothOnly.War) == Tooth,
            "the tooth on its own is the sentence");
        var keptTooth = toothLine.Append(toothOnly.Tooth).ToList();
        Check(PeaceShape.BorderWhy(toothOnly.Campaign, toothOnly.Winner, keptTooth, toothOnly.War).Length == 0,
            "a border that still has the tooth does not say it was left off");

        var kept = KeptOnly();
        var keptLine = new[] { kept.Front };
        Check(PeaceShape.BorderWhy(kept.Campaign, kept.Winner, keptLine, kept.War) == Kept,
            "a border that keeps the occupation touching home and leaves the far pocket says so");
        Check(PeaceShape.BorderWhy(kept.Campaign, kept.Winner, new[] { kept.Front, kept.Pocket }, kept.War).Length == 0,
            "keeping the far pocket as well is not that sentence");

        kept.Winner.IsHuman = true;
        var humanOwners = (ushort[])kept.Map.ProvinceToNation.Clone();
        var humanWhy = PeaceShape.BorderWhy(kept.Campaign, kept.Winner, keptLine, kept.War);
        Check(humanWhy == Kept && Same(kept.Map.ProvinceToNation, humanOwners) && !kept.War.Ended,
            "a human's land stays, and the sentence still does not sign");

        var other = KeptOnly();
        var firstPrint = CampaignFingerprint.Of(kept.Campaign);
        var secondPrint = CampaignFingerprint.Of(other.Campaign);
        Check(PeaceShape.BorderWhy(other.Campaign, other.Winner, new[] { other.Front }, other.War) == Kept &&
              CampaignFingerprint.Of(kept.Campaign) == firstPrint &&
              CampaignFingerprint.Of(other.Campaign) == secondPrint,
            "reading the second campaign does not write the first");

        shaped.Campaign.Wars.Add(shaped.War);
        var demand = new PeaceDemand { WarId = shaped.War.Id, ProposerId = shaped.Winner.Id };
        foreach (var province in line) demand.Provinces.Add(province);
        shaped.War.PendingOffer = demand;
        shaped.War.PendingOfferIsWritten = true;
        shaped.War.PendingOfferFromId = shaped.Winner.Id;
        var paperBefore = CampaignFingerprint.Of(shaped.Campaign);
        var paper = PeaceOffers.Describe(shaped.Campaign, shaped.War);
        Check(paper.Contains(Hole) && !shaped.War.Ended && CampaignFingerprint.Of(shaped.Campaign) == paperBefore,
            "the peace paper names the pocket and does not sign");
        demand.ReleaseRegions = true;
        var release = PeaceOffers.Describe(shaped.Campaign, shaped.War);
        Check(!release.Contains(Hole) && release.Contains("independent") && !shaped.War.Ended,
            "an independent release is not described as an annexed pocket");

        var quiet = KeptOnly();
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(quiet.Campaign, null);
        var quietPrint = CampaignFingerprint.Of(quiet.Campaign);
        Check(PeaceShape.BorderWhy(quiet.Campaign, quiet.Winner, new[] { quiet.Front }, quiet.War).Length == 0 &&
              CampaignFingerprint.Of(quiet.Campaign) == quietPrint,
            "without an occupation there is nothing to explain");
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
        public int Hole;
        public int Tooth;
        public int Pocket;
    }

    static Board Shaped()
    {
        var board = Map(611091, new (int x, int y, int region, int owner)[]
        {
            (1, 1, 4, 2), (2, 1, 1, 1), (3, 1, 2, 2), (3, 0, 8, 2), (4, 0, 9, 2), (4, 1, 3, 2), (6, 1, 7, 2)
        });
        board.Tooth = At(board.Map, 4);
        board.Front = At(board.Map, 2);
        board.Hole = At(board.Map, 3);
        board.Pocket = At(board.Map, 7);
        Hold(board, board.Tooth, board.Front, At(board.Map, 8), At(board.Map, 9), board.Pocket);
        return board;
    }

    static Board ToothOnly()
    {
        var board = Map(611092, new (int x, int y, int region, int owner)[]
        {
            (1, 1, 4, 2), (2, 1, 1, 1), (2, 2, 1, 1), (3, 1, 2, 2), (3, 2, 3, 2)
        });
        board.Tooth = At(board.Map, 4);
        board.Front = At(board.Map, 2);
        Hold(board, board.Tooth, board.Front, At(board.Map, 3));
        return board;
    }

    static Board KeptOnly()
    {
        var board = Map(611093, new (int x, int y, int region, int owner)[]
        {
            (2, 1, 1, 1), (3, 1, 2, 2), (6, 1, 7, 2)
        });
        board.Front = At(board.Map, 2);
        board.Pocket = At(board.Map, 7);
        Hold(board, board.Front, board.Pocket);
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
