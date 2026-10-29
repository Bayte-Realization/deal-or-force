using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>The situation desk: the same week read from five chairs, built only from what the
/// simulation already knows. Each check states a world and asks what the chair would see.</summary>
static class SituationDeskTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL situation desk: " + label);
        Console.WriteLine("PASS situation desk: " + label);
    }

    static (Campaign Campaign, Nation Nation) World(int seed = 4107)
    {
        var campaign = Campaign.CreateBootstrap(seed);
        var nation = campaign.NationByTag("ROU");
        campaign.ConfigureSinglePlayer(nation.Id);
        return (campaign, nation);
    }

    public static void Run()
    {
        LensesAreSeparate();
        Docking();
        WorldNews();
        Security();
        Economy();
        Society();
        Diplomacy();
        Cabinet();
        Determinism();
        DeadNation();
    }

    static void LensesAreSeparate()
    {
        var (campaign, nation) = World();
        foreach (var lens in Enum.GetValues<DeskLens>())
        {
            var items = SituationDesk.Read(campaign, nation, lens);
            Check(items.All(item => item.Lens == lens),
                lens + " shows only its own lines");
            Check(items.All(item => CampaignUiCatalog.Destinations.Contains(item.Screen)),
                lens + " sends every line to a screen that exists");
            Check(items.All(item => item.Title.Length > 0),
                lens + " gives every line something to read");
        }
        var security = SituationDesk.Read(campaign, nation, DeskLens.Security).Select(i => i.Id).ToArray();
        var economy = SituationDesk.Read(campaign, nation, DeskLens.Economy).Select(i => i.Id).ToArray();
        Check(!security.Intersect(economy).Any(),
            "a soldier and a treasurer are not shown the same list");
    }

    /// <summary>
    /// The desk is a panel a player may put on either side, and a tab that brings it back. All
    /// four rectangles are data in `gui/`, so this is the check that the data exists and that
    /// the two docks are on opposite sides of the screen rather than both on one.
    /// </summary>
    static void Docking()
    {
        InterfaceSkin.ResetCatalog();
        foreach (var key in new[] { "hud.desk_left", "hud.desk_right", "hud.desk_tab_left", "hud.desk_tab_right" })
            Check(InterfaceSkin.HasLayout(key), "the desk has a place on screen: " + key);
        var left = InterfaceSkin.Layout("hud.desk_left");
        var right = InterfaceSkin.Layout("hud.desk_right");
        Check(left.AnchorLeft < 0.5f && right.AnchorLeft > 0.5f,
            "one dock is anchored to the left of the screen and the other to the right");
        Check(left.OffsetRight - left.OffsetLeft > 200f && right.OffsetRight - right.OffsetLeft > 200f,
            "both docks are a panel wide, measured between their own edges");
        Check(left.OffsetLeft >= 82f,
            "the left dock clears the navigation rail instead of covering it");
    }

    static void WorldNews()
    {
        var (campaign, nation) = World(seed: 4113);
        var others = campaign.Nations.Where(other => other.Id != nation.Id && other.Population > 0)
            .Take(2).ToArray();
        var first = others[0];
        var second = others[1];

        // A war between two other countries is the soldier's news, and it names a country.
        campaign.Chronicle.Record(campaign, "war_started", first.Id, second.Id);
        var security = SituationDesk.Read(campaign, nation, DeskLens.Security);
        var world = security.FirstOrDefault(item => item.Id.StartsWith("world_"));
        Check(world.HasNation && (world.NationId == first.Id || world.NationId == second.Id),
            "a war elsewhere points at the country it names, not at a list");
        Check(world.Title.Contains("War", StringComparison.OrdinalIgnoreCase),
            "and it says what happened in words");

        // The same fact is not the treasurer's news.
        Check(!SituationDesk.Read(campaign, nation, DeskLens.Economy).Any(item => item.Id.StartsWith("world_")),
            "a war elsewhere stays on the soldier's desk");

        // A crisis is the diplomat's news.
        campaign.Chronicle.Record(campaign, "crisis_opened", first.Id, second.Id);
        Check(SituationDesk.Read(campaign, nation, DeskLens.Diplomacy).Any(item => item.Id.StartsWith("world_")),
            "a crisis elsewhere is a line for the diplomat");

        // Kinds the desk does not read are not shown at all.
        Check(!SituationDesk.TryRoute("construction_completed", out _, out _),
            "routine news is not routed to any chair");
        Check(SituationDesk.TryRoute("sovereign_default", out var economyLens, out _) &&
              economyLens == DeskLens.Economy,
            "a default is the treasurer's news");

        // Old news falls off the desk after the world window.
        var elapsed = typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours));
        var before = (long)elapsed!.GetValue(campaign)!;
        elapsed.SetValue(campaign, before + (long)(campaign.Defines.DeskWorldDays + 2) * 24);
        var later = SituationDesk.Read(campaign, nation, DeskLens.Security);
        Check(!later.Any(item => item.Id.StartsWith("world_")),
            "news older than the window is no longer on the desk");
        elapsed.SetValue(campaign, before);
    }

    static void Security()
    {
        var (campaign, nation) = World();
        var army = campaign.Armies.First(candidate => candidate.NationId == nation.Id);
        army.CutOff = true;
        var cutOff = SituationDesk.Read(campaign, nation, DeskLens.Security)
            .FirstOrDefault(item => item.Id == "cut_off");
        Check(cutOff.Severity == DeskSeverity.Must, "a cut-off division is a Must, not a note");
        army.CutOff = false;

        var enemy = campaign.Nations.First(other => other.Id != nation.Id && other.Population > 0);
        var war = new StrategicWar { Id = 900, AttackerId = nation.Id, DefenderId = enemy.Id };
        campaign.Wars.Add(war);
        war.WarScore = -20f;
        var warItem = SituationDesk.Read(campaign, nation, DeskLens.Security)
            .FirstOrDefault(item => item.Id == "war_900");
        Check(warItem.Severity == DeskSeverity.Must && warItem.Screen == "Wars",
            "a war being lost is a Must that leads to the war screen");
        Check(warItem.Detail.Contains("war score"),
            "and it carries the number the reader will ask for");
        campaign.Wars.Remove(war);
    }

    static void Economy()
    {
        var (campaign, nation) = World();
        nation.Treasury = -4f;
        var negative = SituationDesk.Read(campaign, nation, DeskLens.Economy)
            .FirstOrDefault(item => item.Id == "treasury_negative");
        Check(negative.Severity == DeskSeverity.Must && negative.Screen == "Economy",
            "an overdrawn treasury is the first line a treasurer reads");
        nation.Treasury = 40f;

        nation.WeeklyBalance = -1.5f;
        Check(SituationDesk.Read(campaign, nation, DeskLens.Economy).Any(item => item.Id == "deficit"),
            "a weekly deficit is on the desk even when the balance is positive");
        nation.WeeklyBalance = 1f;

        nation.Inflation = campaign.Defines.DeskInflationShould + 0.02f;
        Check(SituationDesk.Read(campaign, nation, DeskLens.Economy).Any(item => item.Id == "inflation"),
            "inflation above the threshold is its own line");
        nation.Inflation = 0.01f;

        nation.CreditConfidence = campaign.Defines.DeskCreditShould - 0.05f;
        Check(SituationDesk.Read(campaign, nation, DeskLens.Economy).Any(item => item.Id == "credit"),
            "lenders stepping back is a line, not a tooltip");
        nation.CreditConfidence = 0.75f;
    }

    static void Society()
    {
        var (campaign, nation) = World();
        nation.Stability = campaign.Defines.DeskStabilityMust - 0.02f;
        var must = SituationDesk.Read(campaign, nation, DeskLens.Society)
            .FirstOrDefault(item => item.Id == "stability");
        Check(must.Severity == DeskSeverity.Must, "a government below the floor is a Must");

        nation.Stability = (campaign.Defines.DeskStabilityMust + campaign.Defines.DeskStabilityShould) / 2f;
        var should = SituationDesk.Read(campaign, nation, DeskLens.Society)
            .FirstOrDefault(item => item.Id == "stability");
        Check(should.Severity == DeskSeverity.Should, "and one below the line is a Should, not a Must");

        nation.Stability = 0.8f;
        Check(!SituationDesk.Read(campaign, nation, DeskLens.Society).Any(item => item.Id == "stability"),
            "a settled country is not nagged about stability");

        // Radicalism is read from the regions, not from the nation: the desk averages what the
        // street actually reports.
        foreach (var state in campaign.StatesOf(nation)) state.Radicalism = 0.4f;
        Check(SituationDesk.Read(campaign, nation, DeskLens.Society).Any(item => item.Id == "radicalism"),
            "regional radicalism puts a line on the desk");
        foreach (var state in campaign.StatesOf(nation)) state.Radicalism = 0.02f;
    }

    static void Diplomacy()
    {
        var (campaign, nation) = World();
        var other = campaign.Nations.First(candidate => candidate.Id != nation.Id && candidate.Population > 0);
        var relation = CampaignDiplomacy.Get(campaign, other.Id, nation.Id);
        relation.DiplomaticActBy = other.Id;
        relation.DiplomaticActDay = campaign.ElapsedDays;
        relation.DiplomaticActKind = (int)DiplomaticAct.CulturalCooperation;
        Check(DiplomaticActs.Incoming(campaign, nation.Id) == 1, "fixture: one paper is waiting");
        var papers = SituationDesk.Read(campaign, nation, DeskLens.Diplomacy)
            .FirstOrDefault(item => item.Id == "papers");
        Check(papers.Severity == DeskSeverity.Must && papers.Screen == "Diplomacy",
            "a paper nobody answered is a Must that leads to diplomacy");
        relation.DiplomaticActBy = -1;
        relation.DiplomaticActKind = -1;
    }

    static void Cabinet()
    {
        var (campaign, nation) = World();
        nation.PoliticalPoints = 20f;
        var items = SituationDesk.Read(campaign, nation, DeskLens.Cabinet);
        Check(items.Any(item => item.Id == "political_credit"),
            "credit to spend is worth a line in the cabinet chair");
        Check(items.All(item => item.Lens == DeskLens.Cabinet),
            "and the cabinet chair shows the cabinet, not the front");
    }

    static void Determinism()
    {
        var (campaign, nation) = World();
        var first = SituationDesk.ReadAll(campaign, nation);
        var second = SituationDesk.ReadAll(campaign, nation);
        Check(first.Select(item => item.Id).SequenceEqual(second.Select(item => item.Id)),
            "the same world produces the same desk twice");
        var ordered = true;
        for (var index = 1; index < first.Count; index++)
            if (first[index - 1].Severity < first[index].Severity) ordered = false;
        Check(ordered, "ReadAll puts what cannot wait above what can");
    }

    static void DeadNation()
    {
        var (campaign, _) = World();
        var fallen = new Nation { Id = 9999, Tag = "XXX", Name = "Nowhere", Population = 0 };
        campaign.Nations.Add(fallen);
        Check(SituationDesk.ReadAll(campaign, fallen).Count == 0,
            "a country with no people has nothing on a desk");
    }
}
