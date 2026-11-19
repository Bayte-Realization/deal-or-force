using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// The diet of a campaign of centuries (LongCampaign): the long dead are retired to a short
/// record and come back as the same country; nothing walks a retired country; the record
/// survives a save, and a save from before any of this loads and goes on the same diet; a
/// ledger thinned keeps its ends; and the sweep is the same sweep wherever it is run. And the
/// table of who lives where (PopulationFolding): slivers are folded into their nearest kin with
/// nobody lost, the same way on every machine, and never the people somebody there is for.
/// </summary>
static class LongCampaignTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("long campaign: " + label);
        Console.WriteLine("PASS long campaign: " + label);
    }

    const int Today = 12 * 364;

    public static void Run()
    {
        Check(!LongCampaign.UnknownDroppedNames().Any(),
            "every member a retired country lets go of is a member of a country: " +
            string.Join(", ", LongCampaign.UnknownDroppedNames()));
        EmptyRelations();
        LedgerThinning();
        RetireAndRestore();
        NotWhoIsLooking();
        Organisations();
        ReturnsAndLivesOn();
        SaveRoundTrip();
        NotWalked();
        Deterministic();
        FingerprintSeesTheClock();
        Folding();
        FoldingKeepsTheLargestHome();
        FoldingInTheCycle();
    }

    // ---- a relation that says nothing -------------------------------------------------------

    static void EmptyRelations()
    {
        Check(LongCampaign.CarriesNothing(new DiplomaticRelation { A = 3, B = 9, Dormant = true }),
            "a relation as Get makes it carries nothing, whoever its two sides are and asleep or not");
        Check(LongCampaign.CarriesNothing(new DiplomaticRelation { A = 3, B = 9, Opinion = 0.006f }),
            "an opinion too small to show is nothing");
        Check(!LongCampaign.CarriesNothing(new DiplomaticRelation { A = 3, B = 9, Opinion = -5.5f }),
            "an opinion is something");
        var remembered = new DiplomaticRelation { A = 3, B = 9 };
        remembered.Reasons.Add(new OpinionReason());
        Check(!LongCampaign.CarriesNothing(remembered), "a reason for an opinion is something");

        // Every saved member, one at a time: a relation that differs from a new one in that
        // member alone is not empty. Walked from the class, so a member added to a relation in
        // any file is covered the day it is added.
        var missed = new List<string>();
        var covered = 0;
        foreach (var field in typeof(DiplomaticRelation).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.Name is "A" or "B" or "Dormant" or "Opinion") continue;
            if (field.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null) continue;
            var relation = new DiplomaticRelation { A = 3, B = 9 };
            var was = field.GetValue(relation);
            object changed = was switch
            {
                int number => number + 7,
                float number => number + 0.5f,
                bool flag => !flag,
                string => "x",
                Enum value => Enum.GetValues(field.FieldType).Cast<object>().First(other => !other.Equals(value)),
                null when !field.FieldType.IsAbstract && field.FieldType.GetConstructor(Type.EmptyTypes) is not null =>
                    Activator.CreateInstance(field.FieldType),
                _ => null
            };
            if (changed is null) continue; // a collection: empty is its only empty state, checked above
            field.SetValue(relation, changed);
            covered++;
            if (LongCampaign.CarriesNothing(relation)) missed.Add(field.Name);
        }
        Check(missed.Count == 0 && covered > 40,
            $"each of a relation's {covered} saved members makes it something: " + string.Join(", ", missed));
    }

    // ---- the ledger ---------------------------------------------------------------------------

    static NationHistorySample Sample(int day) =>
        new(day, day, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    static void LedgerThinning()
    {
        // Sixty years of one point a week.
        var weekly = new List<NationHistorySample>();
        var today = 60 * 364;
        for (var day = 0; day <= today; day += 7) weekly.Add(Sample(day));
        var all = weekly.Count;
        CampaignHistory.Thin(weekly, today);
        Check(weekly[^1].Day == today, "a thinned ledger still ends today");
        Check(weekly[0].Day >= today - CampaignHistory.KeptDays && weekly[0].Day < today - CampaignHistory.KeptDays + 91,
            "and still begins with the first point of its oldest quarter");
        Check(weekly.Count < all / 5 && weekly.Count <= CampaignHistory.MaximumSamplesPerNation,
            $"sixty years of weeks ({all}) are {weekly.Count} points");
        Check(weekly.Zip(weekly.Skip(1)).All(pair => pair.First.Day < pair.Second.Day), "in order, none twice");
        var once = weekly.Select(s => s.Day).ToArray();
        CampaignHistory.Thin(weekly, today);
        Check(once.SequenceEqual(weekly.Select(s => s.Day)), "thinned again the same day it is the same ledger");

        // A country that stopped being sampled long ago: its books keep the row they closed on.
        var closed = new List<NationHistorySample>();
        for (var day = 0; day <= 5 * 364; day += 7) closed.Add(Sample(day));
        CampaignHistory.Thin(closed, 80 * 364);
        Check(closed.Count == 1 && closed[0].Day == 5 * 364,
            "a ledger wholly older than the charts reach keeps its last row: the day the books closed");

        // A landless country's: no weekly stretch at all.
        var coarse = new List<NationHistorySample>();
        for (var day = 0; day <= today; day += 7) coarse.Add(Sample(day));
        CampaignHistory.Thin(coarse, today - 200, CampaignHistory.Bands.Shipped.Coarse());
        var lately = coarse.Where(s => s.Day > today - 5 * 364).ToList();
        Check(lately.Count > 60 && lately.Zip(lately.Skip(1)).All(pair => pair.Second.Day - pair.First.Day == 28 || pair.Second.Day == today),
            "no stretch of a landless country's ledger is weekly, this year's included");
        Check(coarse[^1].Day == today && coarse.Count < weekly.Count,
            $"a landless country's ledger is coarser ({coarse.Count} points against {weekly.Count}) and ends on the same day");

        // The rule is the campaign's own (95_long_campaign.json). As shipped it is the rule above.
        var shipped = new List<NationHistorySample>();
        for (var day = 0; day <= today; day += 7) shipped.Add(Sample(day));
        CampaignHistory.Thin(shipped, today, CampaignHistory.Bands.Of(new Defines()));
        Check(shipped.Select(s => s.Day).SequenceEqual(weekly.Select(s => s.Day)) &&
              CampaignHistory.Bands.Of(null) == CampaignHistory.Bands.Shipped,
            "the ledger's rule read from the defines is, as shipped, the rule it always was");

        // One year of weeks, four-weekly to ten, a point a year to forty.
        var yearly = new List<NationHistorySample>();
        for (var day = 0; day <= today; day += 7) yearly.Add(Sample(day));
        var lean = CampaignHistory.Bands.Of(new Defines { HistoryWeeklyDays = 364, HistoryOldSampleDays = 364 });
        CampaignHistory.Thin(yearly, today, lean);
        var old = yearly.Where(s => today - s.Day > CampaignHistory.MonthlyDays).Select(s => s.Day).ToArray();
        Check(yearly[^1].Day == today && yearly[0].Day >= today - CampaignHistory.KeptDays &&
              yearly[0].Day < today - CampaignHistory.KeptDays + 364 && yearly.Count < weekly.Count * 2 / 3 &&
              old.Length is >= 29 and <= 31 && old.Zip(old.Skip(1)).All(pair => pair.Second - pair.First == 364),
            $"with a year of weeks and a point a year beyond ten it is {yearly.Count} points, {old.Length} of them a year apart, and ends today");
        var leanOnce = yearly.Select(s => s.Day).ToArray();
        CampaignHistory.Thin(yearly, today, lean);
        Check(leanOnce.SequenceEqual(yearly.Select(s => s.Day)), "and thinned again it is the same ledger");
        var odd = CampaignHistory.Bands.Of(new Defines { HistoryWeeklyDays = -5, HistoryMonthlyDays = -9, HistoryKeptDays = 100, HistoryOldSampleDays = 3 });
        Check(odd.Weekly == 0 && odd.Monthly == 0 && odd.Kept == 100 && odd.OldSpan == 28,
            "a rule written backwards is put in order rather than obeyed");

        // The books of a country that fell in the second week of a four-week span: once those
        // weeks are years old the chart still ends on the day they closed, not three weeks before.
        var fell = new List<NationHistorySample>();
        var closedOn = 28 * 40 + 7;
        for (var day = 0; day <= closedOn; day += 7) fell.Add(Sample(day));
        CampaignHistory.Thin(fell, closedOn + 5 * 364);
        Check(fell[^1].Day == closedOn && fell[^2].Day == closedOn - 7 && fell[0].Day == 0,
            "a ledger that closed mid-span keeps its last point when the span is thinned");
        var fellOnce = fell.Select(s => s.Day).ToArray();
        CampaignHistory.Thin(fell, closedOn + 5 * 364);
        Check(fellOnce.SequenceEqual(fell.Select(s => s.Day)), "and is the same ledger thinned twice");
        CampaignHistory.Thin(fell, closedOn + 25 * 364);
        Check(fell[^1].Day == closedOn && fell.Zip(fell.Skip(1)).All(pair => pair.First.Day < pair.Second.Day),
            "twenty years on, a point a quarter, it still ends there");
    }

    // ---- a world with some dead in it -------------------------------------------------------

    sealed class World
    {
        public Campaign C;
        public Nation Living, LongDead, LatelyDead, Entangled;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);

    /// <summary>
    /// The opening world on a strip of map twelve countries share, twelve years in, with three
    /// countries that have lost everything: one eight years ago, one last year, and one eight
    /// years ago that still has a paper open.
    /// </summary>
    static World Build()
    {
        var c = Campaign.CreateBootstrap(61116);
        var landed = c.Nations.Where(n => c.States.Any(s => s.NationMapIndex == n.MapIndex)).Take(12).ToList();
        if (!landed.Contains(c.Player)) landed[0] = c.Player;
        const int width = 96, height = 8;
        var regions = new ushort[width * height];
        var owners = new ushort[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var owner = landed[x * landed.Count / width];
            regions[y * width + x] = (ushort)c.States.First(s => s.NationMapIndex == owner.MapIndex).MapIndex;
            owners[y * width + x] = (ushort)owner.MapIndex;
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, width, height, _ => false));
        c.Armies.Clear();
        c.Fleets.Clear();
        SetDay(c, Today);

        var world = new World { C = c, Living = landed.First(n => !ReferenceEquals(n, c.Player)) };
        var spare = c.Nations.Where(n => !landed.Contains(n) && n.Movement is null && !n.IsSubject).Take(3).ToList();
        world.LongDead = spare[0];
        world.LatelyDead = spare[1];
        world.Entangled = spare[2];
        var ledgers = new Dictionary<int, List<NationHistorySample>>();
        Kill(world, world.LongDead, Today - 8 * 364, ledgers);
        Kill(world, world.LatelyDead, Today - 364, ledgers);
        Kill(world, world.Entangled, Today - 8 * 364, ledgers);
        c.History.Restore(ledgers);
        // The weekly sweep has been by, as it has in any world this old: what the dead had with
        // the opening world is asleep.
        RelationDormancy.Sweep(c);
        // Somebody is still arming the third one's cause, on paper: it is not done with.
        c.Sponsorships.Add(new Sponsorship { Id = 1, SponsorId = world.Living.Id, ClientId = world.Entangled.Id });
        return world;
    }

    /// <summary>A country with no ground and no people, and everything a dead country is left holding.</summary>
    static void Kill(World world, Nation dead, int day, Dictionary<int, List<NationHistorySample>> ledgers)
    {
        var c = world.C;
        // Its regions are somebody else's now, as they are when a country has lost its last ground.
        foreach (var region in c.States)
            if (region.NationMapIndex == dead.MapIndex)
            {
                region.NationMapIndex = world.Living.MapIndex;
                region.NationId = world.Living.Tag;
            }
        c.InvalidateStateIndex();
        dead.Population = 0;
        dead.Treasury = 3.25f;
        dead.PublicDebt = 11.5f;
        c.Fallen.Add(dead.Id);
        c.Homelands[dead.Id] = new List<int> { 1 };
        if (!dead.Technologies.Contains("test_knowledge")) dead.Technologies.Add("test_knowledge");
        dead.Market[Commodity.Food] = new MarketQuote();
        dead.TradePartners[world.Living.Id] = 1f;
        dead.ResearchProgress["test_knowledge_2"] = 40f;
        dead.DecisionAvailableDay["something"] = 100;
        dead.LedgerWeek = new float[8];
        dead.LedgerOpen = true;
        dead.TacticalDoctrine.Track("land").Branch = "test";

        // A relation with a living country that remembers a war, asleep as the weekly sweep leaves it.
        var relation = CampaignDiplomacy.Get(c, dead.Id, world.Living.Id);
        relation.Opinion = -40f;
        relation.TruceUntil = day + 400;
        var key = ((long)relation.A << 32) | (uint)relation.B;
        c.Relations.Remove(key);
        relation.Dormant = true;
        c.DormantRelations[key] = relation;

        world.Living.PublicImages[SocialMemory.NationKey(dead)] = new PublicImage { Esteem = -0.7f, Status = -1 };
        var ledger = new List<NationHistorySample>();
        for (var at = day - 70; at <= day; at += 7) ledger.Add(Sample(at));
        ledgers[dead.Id] = ledger;

        c.Fleets.Add(new Fleet { Id = 9000 + dead.Id, NationId = dead.Id, Name = "Nobody's fleet", Hulls = 4 });
        c.ArmyGroups.Add(new ArmyGroup { Id = 9000 + dead.Id, NationId = dead.Id, Name = "Nobody's army group" });
        c.Recruitment.Add(new RecruitmentOrder { Id = 9000 + dead.Id, NationId = dead.Id });
        c.Construction.Add(new ConstructionProject { NationId = dead.Id });
    }

    static void RetireAndRestore()
    {
        var world = Build();
        var c = world.C;
        var dead = world.LongDead;
        var (id, tag, name, colour, mapIndex) = (dead.Id, dead.Tag, dead.Name, dead.Color, dead.MapIndex);
        var people = c.Nations.Sum(n => n.Population);
        var regionPeople = c.States.Sum(s => (long)s.Population);
        var money = c.Nations.Sum(n => (double)n.Treasury);
        var debt = c.Nations.Sum(n => (double)n.PublicDebt);
        var nations = c.Nations.Count;
        Check(c.ActiveNations.Count == nations && c.RetiredNations == 0, "before the sweep every country is walked");

        var report = LongCampaign.Run(c);
        Check(report.Retired == 1 && dead.RetiredDay == Today,
            $"a country gone eight years with nothing alive about it is retired, and only it ({report})");
        Check(world.LatelyDead.RetiredDay < 0 && world.LatelyDead.GoneSinceDay == Today - 364,
            "one gone a year is not: its clock reads the day its ledger closed");
        Check(world.Entangled.RetiredDay < 0 && report.Busy == 1,
            "one that something still points at - an open sponsorship - is left for a later sweep");

        Check(c.Nations.Count == nations && ReferenceEquals(c.Nations[id], dead) && ReferenceEquals(c.NationById(id), dead) &&
              ReferenceEquals(c.NationByTag(tag), dead),
            "the retired country keeps its row: the same object under the same id and tag");
        Check(dead.Id == id && dead.Tag == tag && dead.Name == name && dead.Color == colour && dead.MapIndex == mapIndex &&
              dead.Technologies.Contains("test_knowledge") && c.Homelands.ContainsKey(id) && c.Fallen.Contains(id),
            "and who it was: names, colour, place on the map, what it knew, its homeland, its fall");
        Check(dead.Market.Count == 0 && dead.TradePartners.Count == 0 && dead.ResearchProgress.Count == 0 &&
              dead.DecisionAvailableDay.Count == 0 && dead.LedgerWeek is null && !dead.LedgerOpen &&
              dead.TacticalDoctrine.Tracks.Count == 0 && dead.PublicImages.Count == 0,
            "what it was doing is gone: market, partners, research in hand, clocks, ledger, doctrine shelves");
        Check(CampaignDiplomacy.Peek(c, id, world.Living.Id) is null &&
              CampaignDiplomacy.Peek(c, world.LatelyDead.Id, world.Living.Id) is not null,
            "its relations go with it; the lately dead keep theirs");
        Check(!world.Living.PublicImages.ContainsKey(SocialMemory.NationKey(dead)) &&
              world.Living.PublicImages.ContainsKey(SocialMemory.NationKey(world.LatelyDead)),
            "so does what the living thought of it");
        Check(c.History.ForNation(id).Count == 0 && c.History.ForNation(world.LatelyDead.Id).Count > 0,
            "and its ledger");
        Check(!c.Fleets.Any(f => f.NationId == id) && !c.ArmyGroups.Any(g => g.NationId == id) &&
              !c.Recruitment.Any(o => o.NationId == id) && !c.Construction.Any(p => p.NationId == id) &&
              c.Fleets.Count(f => f.NationId == world.LatelyDead.Id) == 1 && report.HullsStruck == 4,
            "its estate is struck: the fleet nobody mans, the empty army group, the orders nobody will finish");
        Check(c.Nations.Sum(n => n.Population) == people && c.States.Sum(s => (long)s.Population) == regionPeople &&
              c.Nations.Sum(n => (double)n.Treasury) == money && c.Nations.Sum(n => (double)n.PublicDebt) == debt &&
              dead.Treasury == 3.25f && dead.PublicDebt == 11.5f,
            "nobody is lost and no money moves: the record keeps its treasury and its debt as they stood");
        Check(c.ActiveNations.Count == nations - 1 && !c.ActiveNations.Contains(dead) && c.RetiredNations == 1,
            "a pass that walks the active list does not meet it");

        var second = LongCampaign.Run(c);
        Check(second.Retired == 0 && second.RelationsDropped == 0, "a second sweep the same day has nothing left to do");

        // Back, the way a release brings a vanished country back: its fall undone, people again.
        var walk = c.ActiveNations;
        c.Fallen.Remove(id);
        dead.Population = 250_000;
        Check(c.ActiveNations.Contains(dead) && dead.RetiredDay < 0 && dead.GoneSinceDay < 0 && c.RetiredNations == 0,
            "the day its fall is undone it is walked again, without the reviver having to say so");
        Check(!walk.Contains(dead) && walk.Count == nations - 1,
            "a pass that was already walking goes on over the list it began");
        Check(ReferenceEquals(c.NationById(id), dead) && dead.Tag == tag && dead.Name == name && dead.Color == colour &&
              dead.Technologies.Contains("test_knowledge"),
            "and it is the same country: same id, tag, name, colour, knowledge");
        var again = CampaignDiplomacy.Get(c, id, world.Living.Id);
        Check(again is not null && again.Opinion == 0f && again.TruceUntil == 0 && !again.Dormant,
            "it meets the world anew: a new relation, not the old grudge");

        // And the other way back: a rising under the old tag.
        var other = Build();
        LongCampaign.Run(other.C);
        var risen = other.LongDead;
        Check(risen.RetiredDay >= 0, "the same country retired in a second world");
        risen.Movement = new MovementState { Id = "test_rising" };
        other.C.MovementRevision++;
        Check(other.C.ActiveNations.Contains(risen) && risen.RetiredDay < 0,
            "a movement raised under a retired country's tag wakes it too");

        // A fall undone on the very day another country falls: the count of the fallen is what it
        // was, so nothing in the list's own bookkeeping has moved. The daily look at the records
        // themselves finds it.
        var masked = Build();
        LongCampaign.Run(masked.C);
        _ = masked.C.ActiveNations;
        masked.C.Fallen.Remove(masked.LongDead.Id);
        masked.C.Fallen.Add(masked.Living.Id);
        masked.C.WakeTheReturned();
        Check(masked.LongDead.RetiredDay < 0 && masked.C.ActiveNations.Contains(masked.LongDead) && masked.C.RetiredNations == 0,
            "a fall undone while another country falls is found within the day");

        // Off is off.
        var untouched = Build();
        untouched.C.Defines.LongCampaignEnabled = false;
        SetDay(untouched.C, 28 * 200);
        var print = CampaignFingerprint.Of(untouched.C);
        LongCampaign.Sweep(untouched.C);
        Check(untouched.C.RetiredNations == 0 && CampaignFingerprint.Of(untouched.C) == print &&
              untouched.LongDead.GoneSinceDay < 0,
            "with the define off the sweep does nothing at all");
        var never = Build();
        never.C.Defines.RetireDeadAfterDays = 0;
        LongCampaign.Run(never.C);
        Check(never.C.RetiredNations == 0 && never.LongDead.Market.Count > 0,
            "with the retirement age at zero nobody is retired");
    }

    // ---- the same on every machine, whoever is looking ---------------------------------------

    static (int Retired, int Gone)[] Clocks(Campaign c) => c.Nations.Select(n => (n.RetiredDay, n.GoneSinceDay)).ToArray();

    /// <summary>
    /// Who a machine calls its own (IsPlayer, Campaign.Player) differs from machine to machine in
    /// a network game, and the observer screen points Player at whatever was clicked. Retirement
    /// reads only the seats, which every machine holds alike.
    /// </summary>
    static void NotWhoIsLooking()
    {
        var host = Build();
        LongCampaign.Run(host.C);
        var expected = Clocks(host.C);
        Check(host.LongDead.RetiredDay == Today, "on the host the long dead country is retired");

        // A guest whose own country it was: SetPlayer with the seats already taken moves IsPlayer
        // and Player and no seat (NetworkMultiplayerUI does this on every machine for its own).
        var guest = Build();
        if (guest.C.HumanSeats.Count == 0) guest.C.AddHumanSeat(guest.C.Player.Id);
        var seats = guest.C.HumanSeats.ToList();
        guest.C.SetPlayer(guest.LongDead.Id);
        Check(guest.LongDead.IsPlayer && ReferenceEquals(guest.C.Player, guest.LongDead) && !guest.LongDead.IsHuman &&
              guest.C.HumanSeats.SequenceEqual(seats),
            "a guest's machine calls the dead country its own, and nobody sits in it");
        LongCampaign.Run(guest.C);
        Check(Clocks(guest.C).SequenceEqual(expected),
            "and retires it on the same day as the host, with every other country's clock as on the host");

        var observer = Build();
        observer.C.ViewAs(observer.LongDead.Id);
        LongCampaign.Run(observer.C);
        Check(ReferenceEquals(observer.C.Player, observer.LongDead) && Clocks(observer.C).SequenceEqual(expected),
            "an observer looking at the dead country on sweep day does not keep it out of the archive");

        var seated = Build();
        seated.C.AddHumanSeat(seated.LongDead.Id);
        LongCampaign.Run(seated.C);
        Check(seated.LongDead.IsHuman && seated.LongDead.RetiredDay < 0,
            "a country somebody sits in is never retired - the seat is what every machine agrees on");
    }

    // ---- organisations ----------------------------------------------------------------------------

    /// <summary>
    /// A retired member's seat goes the way any member's does: with its suspension, its crisis,
    /// its votes and the vote about it; an organisation left with nobody alive is wound up and
    /// its fund goes back through the members' treasuries; and who stands by whom is asked again.
    /// </summary>
    static void Organisations()
    {
        var world = Build();
        var c = world.C;
        var dead = world.LongDead;
        var living = world.Living;
        var other = c.Nations.First(n => n != living && n != dead && c.HasOwnedLand(n) && n.Population > 0);
        var defence = InternationalOrganizations.Charters.First(ch => ch.Defence);
        var plain = InternationalOrganizations.Charters.FirstOrDefault(ch => !ch.Defence) ?? defence;

        var pact = InternationalOrganizations.Seed(c, "Test pact of three", defence.Id, new[] { living.Id, dead.Id, other.Id });
        var club = InternationalOrganizations.Seed(c, "Test club of the dead", plain.Id, new[] { living.Id, dead.Id });
        var council = InternationalOrganizations.Seed(c, "Test council", plain.Id, new[] { living.Id, other.Id });
        Check(pact is not null && club is not null && council is not null, "three test organisations are founded");
        pact.Cohesion = 0.9f;
        pact.CrisisMemberId = dead.Id;
        pact.CrisisSinceDay = Today - 30;
        pact.Motion = new OrganizationMotion { Id = 1, ProposerId = living.Id, TargetId = dead.Id, Kind = "grant", Amount = 1f, EndsDay = Today + 30,
            Votes = new() { [living.Id] = true, [dead.Id] = true } };
        // The living member left long ago: only the dead one is still on the roll, with the fund.
        club.Members.Remove(living.Id);
        club.Budget = 50f;
        club.Motion = new OrganizationMotion { Id = 1, ProposerId = dead.Id, TargetId = other.Id, Kind = "admit", EndsDay = Today + 30,
            Votes = new() { [dead.Id] = true } };
        council.Suspended[dead.Id] = Today - 400;
        council.SuspendedFor[dead.Id] = "coup";
        dead.OrganizationBelongingAtStart[pact.Id] = 0.5f;
        dead.OrganizationConcessions[pact.Id] = Today - 900;
        InternationalOrganizations.InvalidateDefencePartners();
        Check(InternationalOrganizations.DefencePartners(c, living.Id).Contains(dead.Id),
            "before the sweep the living member counts the dead one as a defence partner");
        double Money() => c.Nations.Sum(n => (double)n.Treasury) + c.Organizations.Sum(o => (double)o.Budget);
        var money = Money();

        var report = LongCampaign.Run(c);
        Check(report.Retired == 1 && dead.RetiredDay == Today, "the dead member is retired");
        Check(club.Dissolved && club.Budget == 0f && club.Motion is null && club.Members.Count == 0 && club.Invitations.Count == 0,
            "an organisation with nobody alive left is wound up: no open motion to pass on nobody's votes, no fund for a newcomer");
        Check(dead.Treasury == 3.25f + 50f && Math.Abs(Money() - money) < 1e-6,
            $"its fund goes into the member's treasury, and no money appears or vanishes anywhere ({Money() - money:0.###E+0} apart)");
        Check(!pact.Dissolved && pact.Members.SequenceEqual(new[] { living.Id, other.Id }.OrderBy(id => id)) &&
              pact.CrisisMemberId == -1 && pact.CrisisSinceDay == -1 && pact.Motion is null,
            "an organisation with the living in it keeps going without the seat, the crisis or a vote about the retired country");
        Check(!council.Suspended.ContainsKey(dead.Id) && !council.SuspendedFor.ContainsKey(dead.Id) && !council.Dissolved,
            "a suspension of a retired country goes with it");
        Check(!InternationalOrganizations.DefencePartners(c, living.Id).Contains(dead.Id) &&
              InternationalOrganizations.DefencePartners(c, living.Id).Contains(other.Id),
            "and the living no longer count it a defence partner: who stands by whom is asked again");
        Check(dead.OrganizationBelongingAtStart.Count == 0 && dead.OrganizationConcessions.Count == 0 && dead.LedgerWeek is null,
            "the record forgets its seats and its books stay closed after the fund came through them");
    }

    // ---- back, and on with life ---------------------------------------------------------------

    /// <summary>
    /// A retired country given ground again - the ground itself, through the same annexation a
    /// release uses - is a country the next day and stays one: it has people, a market, a ledger
    /// and relations again within the month, and the world runs on without a fault.
    /// </summary>
    static void ReturnsAndLivesOn()
    {
        var world = Build();
        var c = world.C;
        c.Sponsorships.Clear();
        c.IsPaused = false;
        LongCampaign.Run(c);
        var back = world.LongDead;
        Check(back.RetiredDay >= 0 && back.Market.Count == 0, "a retired country before its return: a record, no market");
        var (id, tag, name) = (back.Id, back.Tag, back.Name);

        // Half of a living neighbour's provinces, handed over the way a release hands them over.
        var map = c.ProvinceMap;
        var provinces = new List<int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == world.Living.MapIndex) provinces.Add(province);
        provinces = provinces.Take(Math.Max(1, provinces.Count / 2)).ToList();
        c.Fallen.Remove(id);
        StatePartition.Annex(c, provinces, back, asConquest: false);
        Check(c.HasOwnedLand(back) && !c.IsRetired(back) && c.ActiveNations.Contains(back) && back.RetiredDay < 0,
            "given ground, it is a country again before the next pass asks");
        // The ledger screen reads a country's quotes by index; a woken record had none until its
        // first weekly balance. By the end of the day it has the quotes a new campaign starts with.
        Check(back.Market.Count == 0 && c.WokenAwaitingQuotes.Contains(id), "woken, it waits for a market until the day is done");
        LongCampaign.GiveQuotes(c);
        Check(back.Market.Count == CommodityCatalog.All.Count() && c.WokenAwaitingQuotes.Count == 0 &&
              back.Market.All(pair => pair.Value.Commodity == pair.Key && pair.Value.Price == CommodityCatalog.Get(pair.Key).BasePrice),
            $"and at the end of the day it has every good's quote at its base price ({back.Market.Count} goods)");

        for (var day = 0; day < 35; day++) c.TickDay();
        Check(c.SimulationFault.Length == 0, "the world runs five weeks with the returned country in it: " + c.SimulationFault);
        Check(ReferenceEquals(c.NationById(id), back) && back.Tag == tag && back.Name == name &&
              back.Technologies.Contains("test_knowledge") && back.RetiredDay < 0 && back.GoneSinceDay < 0,
            "it is the same country - id, tag, name, what it knew - and has not been retired again");
        Check(back.Population > 0 && back.Market.Count > 0 && c.History.ForNation(id).Count > 0,
            $"with people ({back.Population:N0}), a market ({back.Market.Count} goods) and a ledger ({c.History.ForNation(id).Count} weeks) of its own again");
        Check(c.Relations.Values.Any(r => r.A == id || r.B == id),
            "and the world has relations with it again");
    }

    // ---- saves ----------------------------------------------------------------------------------

    static int Occurrences(string text, string what)
    {
        var count = 0;
        for (var at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>
    /// A text save laid out the way the writer before the diet laid it out: the same document,
    /// every object indented member by member (relations included - they now take one line), and
    /// each relation with its mediator, intervening power and that power's target written as -1
    /// (they are now left out while they say "nobody"). Token by token, so nothing else changes.
    /// </summary>
    static string OldLayout(string text)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text),
            new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            var relations = -1;
            var next = false;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        var name = reader.GetString();
                        next = reader.CurrentDepth == 1 && name == "Relations";
                        writer.WritePropertyName(name);
                        break;
                    case JsonTokenType.StartObject:
                        if (next) { relations = reader.CurrentDepth; next = false; }
                        writer.WriteStartObject();
                        break;
                    case JsonTokenType.EndObject:
                        if (relations >= 0 && reader.CurrentDepth == relations + 1)
                        {
                            writer.WriteNumber("MediatorId", -1);
                            writer.WriteNumber("InterveningPowerId", -1);
                            writer.WriteNumber("InterventionTargetId", -1);
                        }
                        else if (reader.CurrentDepth == relations) relations = -1;
                        writer.WriteEndObject();
                        break;
                    case JsonTokenType.StartArray: next = false; writer.WriteStartArray(); break;
                    case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                    case JsonTokenType.String: next = false; writer.WriteStringValue(reader.GetString()); break;
                    case JsonTokenType.Number:
                        next = false;
                        // Shortest round trip either way: a float written as "0.12345679" is read
                        // and written back as the same digits.
                        if (reader.TryGetInt64(out var whole)) writer.WriteNumberValue(whole);
                        else if (reader.TryGetUInt64(out var large)) writer.WriteNumberValue(large);
                        else writer.WriteNumberValue(reader.GetDouble());
                        break;
                    case JsonTokenType.True: next = false; writer.WriteBooleanValue(true); break;
                    case JsonTokenType.False: next = false; writer.WriteBooleanValue(false); break;
                    case JsonTokenType.Null: next = false; writer.WriteNullValue(); break;
                }
            }
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    static void SaveRoundTrip()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dof-long-campaign-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A save from before the archive: nobody retired, no field of it in the file.
            var old = Build();
            old.C.Defines.SaveTextFormat = true;
            SaveSystem.Save(folder, "old", old.C, "tests");
            var oldText = File.ReadAllText(Path.Combine(folder, "old.bdfsave"));
            Check(!oldText.Contains("\"RetiredDay\"") && !oldText.Contains("\"GoneSinceDay\""),
                "a world nobody has been retired in is written as it always was: no archive in the file");
            Check(!oldText.Contains("\"MediatorId\"") && oldText.Contains("\"TruceUntil\""),
                "and a relation no longer writes \"nobody\" for the three countries it does not have");
            var loadedOld = SaveSystem.Load(folder, "old", "tests");
            Check(loadedOld.Success && CampaignFingerprint.Of(loadedOld.Campaign) == CampaignFingerprint.Of(old.C),
                "it loads as the world that was saved: " + loadedOld.Warning);
            var mediated = loadedOld.Campaign.DormantRelations.Values.First();
            Check(mediated.MediatorId == -1 && mediated.InterveningPowerId == -1 && mediated.InterventionTargetId == -1,
                "with nobody mediating and nobody intervening, as a new relation has it");

            // The file the build before the diet wrote. The one above is the new writer's, so
            // reading it back proves only that the new writer agrees with itself; this is that
            // file laid out the old way - every relation spelling out its three "nobody"s and
            // spread over lines like any other object - which is what a player's save looks like.
            var ancientText = OldLayout(oldText);
            foreach (var file in Directory.GetFiles(folder, "old.*"))
                File.Copy(file, Path.Combine(folder, "ancient" + Path.GetExtension(file)), overwrite: true);
            File.WriteAllText(Path.Combine(folder, "ancient.bdfsave"), ancientText);
            var relationCount = old.C.Relations.Count + old.C.DormantRelations.Count;
            Check(Occurrences(ancientText, "\"InterventionTargetId\": -1") == relationCount && ancientText.Contains("\"TruceUntil\": ") &&
                  !oldText.Contains("\"TruceUntil\": "),
                $"the old layout: each of {relationCount:N0} relations writes its \"nobody\"s and takes a line per member");
            var ancient = SaveSystem.Load(folder, "ancient", "tests");
            Check(ancient.Success && CampaignFingerprint.Of(ancient.Campaign) == CampaignFingerprint.Of(old.C) &&
                  ancient.Campaign.DormantRelations.Keys.SequenceEqual(loadedOld.Campaign.DormantRelations.Keys) &&
                  ancient.Campaign.Relations.Keys.SequenceEqual(loadedOld.Campaign.Relations.Keys) &&
                  ancient.Campaign.Nations.All(n => n.RetiredDay < 0 && n.GoneSinceDay < 0),
                "a save in the old layout loads as the same world, its relations in the same tables in the same order, nobody retired: " + ancient.Warning);
            Check(ancient.Campaign.DormantRelations.Values.All(r => r.MediatorId == -1 && r.InterveningPowerId == -1 && r.InterventionTargetId == -1),
                "its spelled-out \"nobody\"s read as the -1 they always were");
            ancient.Campaign.AttachProvinceTopology(old.C.ProvinceMap);
            var ancientSweep = LongCampaign.Run(ancient.Campaign);
            Check(ancientSweep.Retired == 1 && ancient.Campaign.NationById(old.LongDead.Id).RetiredDay == Today,
                "and it goes on the diet at its first sweep like any other");

            loadedOld.Campaign.AttachProvinceTopology(old.C.ProvinceMap);
            var later = LongCampaign.Run(loadedOld.Campaign);
            Check(later.Retired == 1 && loadedOld.Campaign.NationById(old.LongDead.Id).RetiredDay == Today,
                "and goes on the diet at its first sweep: the same country retired, by the day its ledger closed");

            // A save with the archive in it, both formats.
            var world = Build();
            var c = world.C;
            LongCampaign.Run(c);
            var print = CampaignFingerprint.Of(c);
            foreach (var text in new[] { false, true })
            {
                c.Defines.SaveTextFormat = text;
                var slot = text ? "text" : "packed";
                SaveSystem.Save(folder, slot, c, "tests");
                var back = SaveSystem.Load(folder, slot, "tests");
                Check(back.Success, $"a save with a retired country in it loads ({slot}): " + back.Warning);
                var loaded = back.Campaign;
                loaded.AttachProvinceTopology(c.ProvinceMap);
                var record = loaded.NationById(world.LongDead.Id);
                Check(CampaignFingerprint.Of(loaded) == print, $"as the same world ({slot})");
                Check(record.RetiredDay == Today && record.Tag == world.LongDead.Tag && record.Name == world.LongDead.Name &&
                      record.Market.Count == 0 && record.Technologies.Contains("test_knowledge") &&
                      loaded.RetiredNations == 1 && loaded.ActiveNations.Count == c.ActiveNations.Count,
                    $"with the record as it was left, still out of the walk ({slot})");
                Check(loaded.DormantRelations.Keys.SequenceEqual(c.DormantRelations.Keys) &&
                      loaded.Relations.Keys.SequenceEqual(c.Relations.Keys),
                    $"and both relation tables in the order the swept world holds them ({slot})");
                Check(loaded.NationById(world.LatelyDead.Id).GoneSinceDay == Today - 364,
                    $"and the clock of the not yet retired ({slot})");
                Check(LongCampaign.Run(loaded).Retired == 0, $"a sweep of the loaded world finds nothing new ({slot})");
            }
            var before = new FileInfo(Path.Combine(folder, "old.bdfsave")).Length;
            var after = new FileInfo(Path.Combine(folder, "text.bdfsave")).Length;
            Check(after < before, $"the file with the archive is the smaller one ({after:N0} bytes against {before:N0})");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    // ---- nothing walks the retired -----------------------------------------------------------

    /// <summary>Every plain value and every collection's size on a country, by member name.</summary>
    static Dictionary<string, object> Picture(Nation nation)
    {
        var picture = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in typeof(Nation).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = field.GetValue(nation);
            picture[field.Name] = value switch
            {
                null => "null",
                string or Enum => value,
                ICollection collection => "count " + collection.Count,
                _ when field.FieldType.IsPrimitive => value,
                TacticalDoctrineMemory doctrine => "shelves " + doctrine.Tracks.Count + " known " + doctrine.Known.Count,
                PublicImage image => "image " + image.Esteem + " " + image.LastReviewDay,
                _ => "object"
            };
        }
        return picture;
    }

    static void NotWalked()
    {
        var world = Build();
        var c = world.C;
        // The entangled one too, so that two records sit through the weeks.
        c.Sponsorships.Clear();
        c.IsPaused = false;
        // A week with them dead but not yet retired: whatever a pass writes to any country the
        // first time it meets it (its opening government, for the naming) is written by now.
        c.Defines.LongCampaignEnabled = false;
        for (var day = 0; day < 7; day++) c.TickDay();
        c.Defines.LongCampaignEnabled = true;
        var report = LongCampaign.Run(c);
        Check(report.Retired == 2, $"two countries retired before the weeks begin ({report})");
        var first = Picture(world.LongDead);
        var second = Picture(world.Entangled);
        var relations = c.Relations.Count + c.DormantRelations.Count;

        var retiredOn = c.ElapsedDays;
        // Five weeks: every weekly pass five times over, the four-weekly sweep, a month's end.
        if (Environment.GetEnvironmentVariable("DOF_LC_TRACE") == "1")
        {
            // DOF_LC_TRACE=1: which pass wrote - one stage at a time, naming the passes that ran in a
            // stage that changed the record. How the passes this test now holds were found.
            for (var step = 0; step < 400000 && c.ElapsedDays < Today + 42; step++)
            {
                var was = Picture(world.LongDead);
                var totals = SimProfiler.All.ToDictionary(sample => sample.Name, sample => sample.Runs);
                c.StepFor(0);
                var now = Picture(world.LongDead);
                var changed = was.Keys.Where(key => !Equals(was[key], now[key])).ToList();
                if (changed.Count == 0) continue;
                Console.WriteLine($"TRACE hour {c.ElapsedHours} ({c.ElapsedDays % 7}): {string.Join(", ", changed)} <- " +
                    string.Join(", ", SimProfiler.All.Where(sample => sample.Runs != totals.GetValueOrDefault(sample.Name)).Select(sample => sample.Name.Trim())));
            }
        }
        else for (var day = 0; day < 35; day++) c.TickDay();
        Check(c.ElapsedDays == Today + 42 && c.SimulationFault.Length == 0,
            "the world runs five weeks with retired countries in it: " + c.SimulationFault);

        foreach (var (nation, was) in new[] { (world.LongDead, first), (world.Entangled, second) })
        {
            var now = Picture(nation);
            var changed = was.Keys.Where(key => !Equals(was[key], now[key])).Select(key => $"{key}: {was[key]} -> {now[key]}").ToList();
            Check(changed.Count == 0,
                $"no pass wrote to the retired {nation.Tag} in five weeks: " + string.Join("; ", changed));
            Check(c.History.ForNation(nation.Id).Count == 0, $"no ledger was started for {nation.Tag}");
            Check(!c.Relations.Values.Concat(c.DormantRelations.Values).Any(r => r.A == nation.Id || r.B == nation.Id),
                $"nothing opened a relation with {nation.Tag}");
            Check(!c.Nations.Any(n => n.PublicImages.ContainsKey(SocialMemory.NationKey(nation))),
                $"nobody formed a view of {nation.Tag}");
            Check(nation.RetiredDay == retiredOn && !c.ActiveNations.Contains(nation), $"{nation.Tag} is still retired");
        }
        Check(c.Fleets.All(f => c.NationById(f.NationId).RetiredDay < 0) &&
              c.ArmyGroups.All(g => c.NationById(g.NationId).RetiredDay < 0),
            "and nothing was raised in their name");
    }

    // ---- the same sweep everywhere -----------------------------------------------------------

    static void Deterministic()
    {
        var left = Build();
        var right = Build();
        var a = LongCampaign.Run(left.C);
        var b = LongCampaign.Run(right.C);
        Check(a.ToString() == b.ToString() && CampaignFingerprint.Of(left.C) == CampaignFingerprint.Of(right.C) &&
              left.C.DormantRelations.Keys.SequenceEqual(right.C.DormantRelations.Keys),
            "two copies of one world are swept to the same world, table for table");

        // The table is left as a load lays it out: the next row put to sleep lands in the same
        // place on a host that swept and on a guest that loaded the host's save.
        var folder = Path.Combine(Path.GetTempPath(), "dof-long-campaign-" + Guid.NewGuid().ToString("N"));
        try
        {
            SaveSystem.Save(folder, "host", left.C, "tests");
            var guest = SaveSystem.Load(folder, "host", "tests").Campaign;
            guest.AttachProvinceTopology(left.C.ProvinceMap);
            foreach (var c in new[] { left.C, guest })
            {
                var relation = new DiplomaticRelation { A = left.Living.Id, B = left.Entangled.Id, Dormant = true, Opinion = 3f };
                c.DormantRelations[((long)Math.Min(relation.A, relation.B) << 32) | (uint)Math.Max(relation.A, relation.B)] = relation;
            }
            Check(left.C.DormantRelations.Keys.SequenceEqual(guest.DormantRelations.Keys),
                "a relation put to sleep after the sweep sits in the same place on the host and on a guest that loaded its save");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        // The landless pair that says nothing, and the one that says something.
        var world = Build();
        var c2 = world.C;
        c2.Sponsorships.Clear();
        var movements = c2.Nations.Where(n => !c2.HasOwnedLand(n) && n.RetiredDay < 0 &&
            n != world.LongDead && n != world.LatelyDead && n != world.Entangled).Take(3).ToList();
        void Sleep(Nation one, Nation two, float opinion)
        {
            var relation = new DiplomaticRelation { A = Math.Min(one.Id, two.Id), B = Math.Max(one.Id, two.Id), Dormant = true, Opinion = opinion };
            c2.DormantRelations[((long)relation.A << 32) | (uint)relation.B] = relation;
        }
        Sleep(movements[0], movements[1], 0.004f);
        Sleep(movements[0], movements[2], 12f);
        Sleep(movements[1], world.Living, 0.004f);
        var pruned = LongCampaign.Run(c2);
        Check(pruned.EmptyRelationsDropped == 1 &&
              CampaignDiplomacy.Peek(c2, movements[0].Id, movements[1].Id) is null &&
              CampaignDiplomacy.Peek(c2, movements[0].Id, movements[2].Id) is not null &&
              CampaignDiplomacy.Peek(c2, movements[1].Id, world.Living.Id) is not null,
            "a sleeping relation between two landless sides that says nothing is dropped; one with an opinion, or with a landed side, stays");

        // A war long over lets go of its province record and nothing else.
        var old = new StrategicWar { Id = 501, AttackerId = world.Living.Id, DefenderId = c2.Player.Id, Ended = true, EndedDay = Today - 800, Name = "Old war", AttackerLosses = 5 };
        old.OriginalController[3] = 7;
        var recent = new StrategicWar { Id = 502, AttackerId = world.Living.Id, DefenderId = c2.Player.Id, Ended = true, EndedDay = Today - 30 };
        recent.OriginalController[3] = 7;
        c2.Wars.Add(old); c2.Wars.Add(recent);
        var trimmed = LongCampaign.Run(c2);
        Check(trimmed.WarsTrimmed == 1 && old.OriginalController.Count == 0 && recent.OriginalController.Count == 1 &&
              old.Name == "Old war" && old.AttackerLosses == 5 && old.EndedDay == Today - 800,
            "a war two years over lets go of who held each province and keeps its ledger line; one a month over keeps both");
    }

    // ---- what lockstep compares ---------------------------------------------------------------

    static void Resleep(Campaign c, bool reversed)
    {
        var rows = c.DormantRelations.ToList();
        if (reversed) rows.Reverse();
        c.DormantRelations.Clear();
        foreach (var row in rows) c.DormantRelations.Add(row.Key, row.Value);
    }

    /// <summary>
    /// Two machines that disagree about when a dead country went, or about the order of the
    /// sleeping table the weekly wake walks, disagree about the world - the first only five years
    /// later, when one of them retires it. The fingerprint has to see both now.
    /// </summary>
    static void FingerprintSeesTheClock()
    {
        var a = Build();
        var b = Build();
        LongCampaign.Run(a.C);
        LongCampaign.Run(b.C);
        Check(CampaignFingerprint.Of(a.C) == CampaignFingerprint.Of(b.C), "two copies of one swept world have one fingerprint");
        b.LatelyDead.GoneSinceDay -= 7;
        Check(CampaignFingerprint.Of(a.C) != CampaignFingerprint.Of(b.C),
            "a week's difference in when a not yet retired country went is a different world");
        Check(CampaignFingerprint.Of(a.C, longCampaignClock: false) == CampaignFingerprint.Of(b.C, longCampaignClock: false),
            "and the bench's number without that clock leaves only it out");
        b.LatelyDead.GoneSinceDay += 7;
        Resleep(b.C, reversed: true);
        Check(CampaignFingerprint.Of(a.C) != CampaignFingerprint.Of(b.C),
            "once anybody is retired, the same sleeping relations in another order are a different world");
        Resleep(b.C, reversed: true);
        Check(CampaignFingerprint.Of(a.C) == CampaignFingerprint.Of(b.C), "and put back in order, the same one again");

        var young = Build();
        var youngTwin = Build();
        Resleep(youngTwin.C, reversed: true);
        Check(CampaignFingerprint.Of(young.C) == CampaignFingerprint.Of(youngTwin.C) &&
              CampaignFingerprint.Of(young.C) == CampaignFingerprint.Of(young.C, longCampaignClock: false),
            "a world nobody has been retired in, and nobody's clock started, keeps the fingerprint it always had");
    }

    // ---- slivers folded -----------------------------------------------------------------------

    static PopulationGroup Group(string people, string faith, string trade, SocialClass rank, Sector sector, long size, float employment = 0.9f) =>
        new()
        {
            Culture = people, Religion = faith, Profession = trade, Class = rank, Sector = sector, Size = size,
            Employment = employment, Wealth = 0.5f, StandardOfLiving = 0.5f, Radicalism = 0.1f
        };

    static long Of(ScenarioState region, string people) =>
        region.PopulationGroups.Where(g => string.Equals(g.Culture, people, StringComparison.OrdinalIgnoreCase)).Sum(g => g.Size);

    static long OfFaith(ScenarioState region, string faith) =>
        region.PopulationGroups.Where(g => string.Equals(g.Religion, faith, StringComparison.OrdinalIgnoreCase)).Sum(g => g.Size);

    static long Sum(ScenarioState region) => region.PopulationGroups.Sum(g => g.Size);

    static long WorldOf(Campaign c, string people) => c.States.Sum(s => Of(s, people));

    static PopulationGroup Trade(ScenarioState region, string people, string trade) =>
        region.PopulationGroups.FirstOrDefault(g => g.Culture == people && g.Profession == trade);

    sealed class Slivers
    {
        public Campaign C;
        public ScenarioState Home, Colony, Far;
        public Nation Owner, Movement, HomelandOf;
        /// <summary>The owner's own people; a living movement's people; the people of the country Home is the homeland of.</summary>
        public string Own, MovementPeople, HomelandPeople;
    }

    const string Faith = "test_faith_a", OtherFaith = "test_faith_b", ThirdFaith = "test_faith_c";
    const string Big = "test_big_people", Other = "test_other_people", Tiny = "test_tiny_people", Declared = "test_declared_people";

    /// <summary>
    /// The world of Build with three regions written by hand. Home, the owner's own country:
    /// nine and a half million of its own people in real trades, and beside them one sliver of
    /// every kind there is - a trade's, a faith's, a people's with kin of its faith and one
    /// without, and five that something protects. Colony, also the owner's: five million of
    /// another people, where the owner's own people are the sliver, and two of the protected
    /// peoples stand unprotected. Far, somebody else's: where the big peoples really live.
    /// </summary>
    static Slivers BuildSlivers()
    {
        var world = Build();
        var c = world.C;
        // Folding ships switched off (it changes the world and waits for the owner's word);
        // these worlds are the ones that test it, so they switch it on themselves.
        c.Defines.FoldSliverGroups = true;
        var owner = world.Living;
        var own = DemographyCatalog.PrimaryCulture(owner.Tag);
        long World(string people) => WorldOf(c, people);

        // A living movement with a people of its own, and a country with a people of its own:
        // neither the owner's, each two million strong somewhere, so that nothing but the rule
        // under test stands between its sliver and the fold.
        var movement = c.Nations.First(n => n.Movement is { Dissolved: false } && Movements.DefinitionOf(n) is { } d &&
            d.Peoples.Count > 0 && !string.Equals(d.Peoples[0], own, StringComparison.OrdinalIgnoreCase) &&
            !DemographyCatalog.IsPrimary(owner.Tag, d.Peoples[0]) && World(d.Peoples[0]) >= 2_000_000);
        var movementPeople = Movements.DefinitionOf(movement).Peoples[0];
        var homelandOf = c.Nations.First(n => n != owner && n.Movement is null && DemographyCatalog.PrimarySet(n.Tag) is not null &&
            DemographyCatalog.PrimaryCulture(n.Tag) is var people &&
            !string.Equals(people, own, StringComparison.OrdinalIgnoreCase) && !DemographyCatalog.IsPrimary(owner.Tag, people) &&
            !string.Equals(people, movementPeople, StringComparison.OrdinalIgnoreCase) &&
            !Movements.DefinitionOf(movement).HasPeople(people) && World(people) >= 2_000_000);
        var homelandPeople = DemographyCatalog.PrimaryCulture(homelandOf.Tag);

        // Three regions no movement keeps its headquarters in, none of them the largest home of
        // the two real peoples; the first two given to the owner, the third left to somebody
        // who is for neither of them.
        bool Plain(ScenarioState s) => !s.Unclaimed && s.Population > 0 &&
            !c.Nations.Any(n => n.Movement is not null && n.Movement.HeadquartersStateMapIndex == s.MapIndex) &&
            Of(s, movementPeople) == 0 && Of(s, homelandPeople) == 0 && Of(s, own) == 0;
        var plain = c.States.Where(Plain).Take(3).ToList();
        var (home, colony, far) = (plain[0], plain[1], plain[2]);
        var stranger = c.Nations.First(n => n != owner && n != homelandOf && c.HasOwnedLand(n) && n.Movement is null &&
            !DemographyCatalog.IsPrimary(n.Tag, movementPeople) && !DemographyCatalog.IsPrimary(n.Tag, homelandPeople));
        foreach (var (region, to) in new[] { (home, owner), (colony, owner), (far, stranger) })
        {
            region.NationMapIndex = to.MapIndex;
            region.NationId = to.Tag;
        }
        c.InvalidateStateIndex();
        // Nobody stands in the two regions but the one movement, in Home; nobody's homeland but Home.
        foreach (var nation in c.Nations)
        {
            if (nation.Movement is null) continue;
            foreach (var region in new[] { home, colony, far })
            {
                nation.Movement.Support.Remove(region.MapIndex);
                nation.Movement.Infiltrated.Remove(region.MapIndex);
            }
        }
        movement.Movement.Support[home.MapIndex] = 0.3f;
        c.Homelands.Clear();
        c.Homelands[homelandOf.Id] = new List<int> { home.MapIndex };
        owner.DeclaredCulture = Declared;

        void Write(ScenarioState region, List<PopulationGroup> groups)
        {
            region.PopulationGroups.Clear();
            region.PopulationGroups.AddRange(groups);
            region.Population = (int)groups.Sum(g => g.Size);
        }

        var homeGroups = new List<PopulationGroup>
        {
            Group(own, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 4_000_000),
            Group(own, Faith, "workers", SocialClass.Lower, Sector.Industry, 3_000_000),
            Group(own, Faith, "clerks", SocialClass.Middle, Sector.Services, 2_000_000),
            // A trade's slivers: to the same class in the same sector, or failing that the same class.
            Group(own, Faith, "engineers", SocialClass.Middle, Sector.Industry, 900, employment: 0.5f),
            Group(own, Faith, "smiths", SocialClass.Lower, Sector.Industry, 800),
            // A faith's sliver of the owner's own people.
            Group(own, OtherFaith, "priests", SocialClass.Middle, Sector.Services, 90),
            // A people's sliver with kin of its faith here, in two trades; and one with none.
            Group(Big, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 50),
            Group(Big, Faith, "clerks", SocialClass.Middle, Sector.Services, 30),
            Group(Other, ThirdFaith, "herders", SocialClass.Lower, Sector.Agriculture, 70),
            // And the protected: too few in the world; the people the owner declares itself of;
            // a movement's people where it stands; a people in its country's homeland.
            Group(Tiny, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 60),
            Group(Declared, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 85),
            Group(movementPeople, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 95),
            Group(homelandPeople, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 75),
        };
        // Enough other trades that the region is one folding looks at (FoldMinRegionGroups).
        for (var trade = 0; trade < 22; trade++)
            homeGroups.Add(Group(own, Faith, "owners_" + trade, SocialClass.Upper, Sector.Ownership, 20_000));
        Write(home, homeGroups);

        var colonyGroups = new List<PopulationGroup> { Group(Big, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 200_000) };
        for (var trade = 0; trade < 24; trade++)
            colonyGroups.Add(Group(Big, Faith, "trade_" + trade, SocialClass.Lower, Sector.Industry, 200_000));
        // The owner's own people as a sliver of its own colony: they stay.
        colonyGroups.Add(Group(own, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 65));
        // The movement's people where it does not stand, and the other country's outside its homeland: they go.
        colonyGroups.Add(Group(movementPeople, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 95));
        colonyGroups.Add(Group(homelandPeople, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 75));
        Write(colony, colonyGroups);

        // Where the big peoples' largest groups are: no sliver of theirs is the last of them.
        Write(far, new List<PopulationGroup>
        {
            Group(Big, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 1_000_000),
            Group(Other, ThirdFaith, "herders", SocialClass.Lower, Sector.Agriculture, 1_000_000),
            Group(Declared, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 1_000_000),
        });

        return new Slivers
        {
            C = c, Home = home, Colony = colony, Far = far, Owner = owner, Movement = movement, HomelandOf = homelandOf,
            Own = own, MovementPeople = movementPeople, HomelandPeople = homelandPeople
        };
    }

    static void Folding()
    {
        var w = BuildSlivers();
        var c = w.C;
        var d = c.Defines;
        Check(new Defines().FoldSliverGroups && PopulationFolding.Enabled(Build().C),
            "folding ships switched on: professions and micro-slivers fold into larger groups");
        Check(PopulationFolding.Enabled(c) && d.FoldTradeSliverPeople == 2500 && d.FoldPeopleSliverPeople == 100,
            "switched on, its thresholds as shipped are a trade's sliver 2,500 people, a people's 100");

        var people = c.States.Sum(Sum);
        var (homeSum, colonySum, farSum) = (Sum(w.Home), Sum(w.Colony), Sum(w.Far));
        var (homePopulation, colonyPopulation) = (w.Home.Population, w.Colony.Population);
        var worldBig = WorldOf(c, Big); var worldOther = WorldOf(c, Other);
        var worldMovement = WorldOf(c, w.MovementPeople); var worldHomeland = WorldOf(c, w.HomelandPeople);
        var (farGroups, homeGroups, colonyGroups) = (w.Far.PopulationGroups.Count, w.Home.PopulationGroups.Count, w.Colony.PopulationGroups.Count);
        // Every other region as it stood, to see that only slivers moved there too.
        var elsewhere = c.States.Where(s => s != w.Home && s != w.Colony && s != w.Far).ToDictionary(s => s, Sum);

        var gone = PopulationFolding.FoldWorld(c);

        // ---- people are conserved exactly
        Check(c.States.Sum(Sum) == people, $"folding conserves the people of the world to the person ({people:N0})");
        Check(Sum(w.Home) == homeSum && Sum(w.Colony) == colonySum && Sum(w.Far) == farSum && elsewhere.All(pair => Sum(pair.Key) == pair.Value),
            "and of every region: nobody crosses a border by being folded");
        Check(w.Home.Population == homePopulation && w.Colony.Population == colonyPopulation,
            "a region's own count is not touched");
        Check(c.States.All(s => s.PopulationGroups.All(g => g.Size > 0)), "no emptied group is left in any list");
        Check(gone == c.FoldedGroups && gone >= 8, $"the groups that went are counted ({gone:N0})");

        // ---- a trade's sliver: same people, same faith
        Check(Trade(w.Home, w.Own, "engineers") is null && Trade(w.Home, w.Own, "smiths") is null,
            "a trade's slivers are gone from the list");
        Check(Trade(w.Home, w.Own, "workers").Size == 3_000_800,
            "a trade's sliver joins its own class in its own sector: 800 smiths with the workers, not with the more numerous farmers");
        var clerks = Trade(w.Home, w.Own, "clerks");
        Check(clerks.Size == 2_000_000 + 900 + 90,
            "failing that its own class: 900 engineers with the clerks - and so does the faith's sliver, 90 priests");
        var expected = (2_000_000 * 0.9 + 900 * 0.5 + 90 * 0.9) / 2_000_990.0;
        Check(Math.Abs(clerks.Employment - expected) < 1e-6 && clerks.Employment < 0.9f,
            $"what the merged carried goes in weighted by people (employment {clerks.Employment:0.000000}, expected {expected:0.000000})");

        // ---- a faith's sliver keeps its people
        Check(OfFaith(w.Home, OtherFaith) == 0 && Of(w.Home, w.Own) == 9_441_700 + 90 + 80 + 70,
            "a faith's sliver joins its own people's largest faith; nobody of the owner's people is lost");

        // ---- a people's sliver
        Check(Of(w.Home, Big) == 0 && Of(w.Home, Other) == 0,
            "a people's sliver is folded - with kin of its faith here, and without");
        Check(Trade(w.Home, w.Own, "farmers").Size == 4_000_000 + 80 + 70,
            "into the largest people of its faith, by trade where the trade exists (both of its trades had become one sliver first)");
        Check(WorldOf(c, Other) == worldOther - 70 && WorldOf(c, Big) == worldBig - 80 + 95 + 75,
            "what a people loses in the world is exactly its folded slivers - and what it gains, the slivers folded into it where it is the many");

        // ---- the protected
        Check(Of(w.Home, Tiny) == 60, "a people with too few in the whole world is never folded into another");
        Check(Of(w.Home, Declared) == 85, "nor the people the region's owner declares itself of");
        Check(Of(w.Home, w.MovementPeople) == 95 && Of(w.Colony, w.MovementPeople) == 0,
            "nor a living movement's people where it stands - in the region where it does not, they go");
        Check(Of(w.Home, w.HomelandPeople) == 75 && Of(w.Colony, w.HomelandPeople) == 0,
            "nor a country's people in its homeland - outside it, they go");
        Check(Of(w.Colony, w.Own) == 65, "nor the owner's own people, however few of them live in its region");
        Check(WorldOf(c, w.MovementPeople) <= worldMovement - 95 && WorldOf(c, w.HomelandPeople) <= worldHomeland - 75,
            "and what they lost in the world is at least those slivers: a protected people gains nobody by being protected");
        Check(w.Far.PopulationGroups.Count == farGroups, "a region with a handful of groups is left alone");
        Check(c.FoldedTradePeople >= 900 + 800 + 30 && c.FoldedFaithPeople >= 90 && c.FoldedCulturePeople >= 80 + 70 + 95 + 75,
            $"the gauge says who is counted differently (trade {c.FoldedTradePeople:N0}, faith {c.FoldedFaithPeople:N0}, people {c.FoldedCulturePeople:N0})");
        // No people has left the world: each still lives where its largest group lived.
        var before = BuildSlivers();
        var names = before.C.States.SelectMany(s => s.PopulationGroups).Select(g => g.Culture).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var after = new HashSet<string>(c.States.SelectMany(s => s.PopulationGroups).Select(g => g.Culture), StringComparer.OrdinalIgnoreCase);
        Check(names.Count > 100 && names.All(after.Contains), $"every one of the world's {names.Count} peoples is still in it");

        // ---- deterministic
        var twin = BuildSlivers();
        Check(CampaignFingerprint.Of(twin.C) == CampaignFingerprint.Of(before.C), "two copies of the test world are one world");
        PopulationFolding.FoldWorld(twin.C);
        Check(CampaignFingerprint.Of(twin.C) == CampaignFingerprint.Of(c) && twin.C.FoldedGroups == c.FoldedGroups,
            "two copies of one world fold to the same world");
        // Region by region in the opposite order: a region is judged against the world as the
        // week found it, so the order the regions are worked in - which on a player's machine
        // is the thread pool's - changes nothing.
        var shield = PopulationFolding.Prepare(before.C);
        for (var index = before.C.States.Count - 1; index >= 0; index--)
        {
            var region = before.C.States[index];
            if (region is null || region.Unclaimed || region.Population <= 0) continue;
            if (PopulationFolding.FoldRegion(before.C, region, shield)) region.PopulationGroups.RemoveAll(g => g.Size <= 0);
        }
        Check(CampaignFingerprint.Of(before.C) == CampaignFingerprint.Of(c),
            "folded in the opposite order of regions it is the same world");

        // ---- the switches
        foreach (var (label, off) in new (string, Action<Defines>)[]
                 {
                     ("its own switch off", defines => defines.FoldSliverGroups = false),
                     ("the whole diet off", defines => defines.LongCampaignEnabled = false),
                     ("both thresholds at nobody", defines => { defines.FoldTradeSliverPeople = 0; defines.FoldPeopleSliverPeople = 0; }),
                 })
        {
            var untouched = BuildSlivers();
            var print = CampaignFingerprint.Of(untouched.C);
            off(untouched.C.Defines);
            Check(!PopulationFolding.Enabled(untouched.C) && PopulationFolding.FoldWorld(untouched.C) == 0 &&
                  CampaignFingerprint.Of(untouched.C) == print && untouched.Home.PopulationGroups.Count == homeGroups &&
                  untouched.Colony.PopulationGroups.Count == colonyGroups,
                $"with {label} nothing is folded and the world is the world it was");
        }
        var tradesOnly = BuildSlivers();
        tradesOnly.C.Defines.FoldPeopleSliverPeople = 0;
        PopulationFolding.FoldWorld(tradesOnly.C);
        Check(tradesOnly.C.FoldedCulturePeople == 0 && tradesOnly.C.FoldedFaithPeople == 0 && tradesOnly.C.FoldedTradePeople > 0 &&
              Of(tradesOnly.Home, Big) == 80 && OfFaith(tradesOnly.Home, OtherFaith) == 90,
            "with the people's threshold at nobody only trades are folded: nobody changes people or faith");

        // ---- a people's slivers a region's one week in the year
        var yearly = BuildSlivers();
        var turns = new List<int>();
        var otherTurns = new List<int>();
        for (var week = 0; week < 104; week++)
        {
            SetDay(yearly.C, Today + week * 7);
            if (PopulationFolding.PeoplesTurn(yearly.C, yearly.Home)) turns.Add(week);
            if (PopulationFolding.PeoplesTurn(yearly.C, yearly.Colony)) otherTurns.Add(week);
        }
        SetDay(yearly.C, Today);
        Check(turns.Count == 2 && turns[1] - turns[0] == 52 && otherTurns.Count == 2 &&
              (turns[0] - otherTurns[0] - (yearly.Colony.MapIndex - yearly.Home.MapIndex)) % 52 == 0,
            "as shipped a region's slivers of a faith and of a people are looked at one week in fifty-two, each region on its own week");
        PopulationFolding.FoldRegion(yearly.C, yearly.Home, PopulationFolding.Prepare(yearly.C), peoplesToo: false);
        yearly.Home.PopulationGroups.RemoveAll(g => g.Size <= 0);
        Check(Trade(yearly.Home, yearly.Own, "engineers") is null && Of(yearly.Home, Big) == 80 && Of(yearly.Home, Other) == 70 &&
              OfFaith(yearly.Home, OtherFaith) == 90 && yearly.C.FoldedCulturePeople == 0 && yearly.C.FoldedFaithPeople == 0,
            "in any other week only its trades are folded: the newcomers of a people are left to become a community");
        yearly.C.Defines.FoldPeopleEveryWeeks = 1;
        Check(Enumerable.Range(0, 8).All(week => { SetDay(yearly.C, Today + week * 7); return PopulationFolding.PeoplesTurn(yearly.C, yearly.Home); }),
            "and with the define at one week, every week is its week");
    }

    /// <summary>
    /// A people scattered so thin that every one of its groups is a sliver still keeps the region
    /// where its largest group lives - that is what stops a people leaving the world by being
    /// folded everywhere at once. The shipped thresholds hide it (a people of 250,000 has at least
    /// 118 in its largest region, above the 100 a sliver may hold), so this world raises them.
    /// </summary>
    static void FoldingKeepsTheLargestHome()
    {
        const string Scattered = "test_scattered_people";
        var w = BuildSlivers();
        var c = w.C;
        c.Defines.FoldPeopleSliverPeople = 500;
        c.Defines.FoldKeepCulturePeople = 500;
        c.Defines.FoldPeopleSliverOfPeople = 1f;
        w.Home.PopulationGroups.Add(Group(Scattered, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 400));
        w.Colony.PopulationGroups.Add(Group(Scattered, Faith, "farmers", SocialClass.Lower, Sector.Agriculture, 300));
        w.Home.Population += 400;
        w.Colony.Population += 300;
        var homeIndex = c.States.IndexOf(w.Home);
        Check(homeIndex != w.Home.MapIndex, $"the largest home's place in the list ({homeIndex}) is not its map index ({w.Home.MapIndex})");
        PopulationFolding.FoldWorld(c);
        Check(Of(w.Home, Scattered) == 400 && Of(w.Colony, Scattered) == 0,
            $"a people of 700, every group of it a sliver, is folded where it is fewest and kept in its largest home " +
            $"(home {Of(w.Home, Scattered)}, elsewhere {Of(w.Colony, Scattered)})");
    }

    /// <summary>
    /// The weekly cycle itself: with folding on the world runs and the table shrinks; with it
    /// off, by either of its switches, the weeks are the same weeks to the bit.
    /// </summary>
    static void FoldingInTheCycle()
    {
        (Campaign C, long Groups) Weeks(Action<Defines> set)
        {
            var world = Build();
            var c = world.C;
            c.Sponsorships.Clear();
            set(c.Defines);
            c.IsPaused = false;
            for (var day = 0; day < 21; day++) c.TickDay();
            Check(c.SimulationFault.Length == 0, "the world runs three weeks: " + c.SimulationFault);
            return (c, c.States.Sum(s => (long)s.PopulationGroups.Count));
        }
        var off = Weeks(defines => defines.FoldSliverGroups = false);
        var nobody = Weeks(defines => { defines.FoldTradeSliverPeople = 0; defines.FoldPeopleSliverPeople = 0; });
        var on = Weeks(defines => defines.FoldSliverGroups = true);
        var shipped = Weeks(_ => { });
        Check(shipped.C.FoldedGroups == on.C.FoldedGroups && CampaignFingerprint.Of(shipped.C) == CampaignFingerprint.Of(on.C),
            "as shipped slivers fold by default: three weeks are the same three weeks as with the switch on");
        Check(off.C.FoldedGroups == 0 && nobody.C.FoldedGroups == 0 && CampaignFingerprint.Of(off.C) == CampaignFingerprint.Of(nobody.C),
            "switched off, or with thresholds that catch nobody, three weeks of the world are the same three weeks");
        Check(on.C.FoldedGroups > 0 && on.Groups < off.Groups,
            $"switched on, the weekly cycle folds: {on.Groups:N0} groups against {off.Groups:N0}, {on.C.FoldedGroups:N0} folded");
        Check(on.C.States.All(s => s.PopulationGroups.All(g => g.Size > 0)), "and leaves no emptied group behind");
        var share = on.C.FoldedCulturePeople / (double)Math.Max(1, on.C.States.Sum(s => (long)s.Population));
        Check(share < 0.0005, $"those counted under another people in three weeks are a sliver of the world themselves ({share:P4})");
    }
}
