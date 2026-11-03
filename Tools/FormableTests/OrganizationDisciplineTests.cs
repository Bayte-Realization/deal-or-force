using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Suspension, expulsion, protest exits, rival blocs and dissolution (OrganizationDiscipline,
/// OrganizationDynamics): one member judged by the rest, with real consequences.
/// </summary>
static class OrganizationDisciplineTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("organization discipline: " + note); Console.WriteLine("PASS organization discipline: " + note); }

    /// <summary>A small world of <paramref name="count"/> countries, one region each, all governed by the computer.</summary>
    static Campaign World(int count)
    {
        var c = new Campaign(91) { ScenarioId = "org_discipline_fixture" };
        for (var i = 0; i < count; i++)
            c.Nations.Add(new Nation { Id = i, MapIndex = 700 + i, Tag = "OD" + i, Name = "Member " + i, Population = 1000000,
                Treasury = 50, PoliticalPoints = 100, Gdp = 100, Stability = 0.6f, Government = "parliamentary_republic" });
        foreach (var n in c.Nations)
            c.States.Add(new ScenarioState { MapIndex = 4001 + n.Id, Id = "od_" + n.Id, Name = "Region " + n.Id,
                NationMapIndex = n.MapIndex, NationId = n.Tag, Population = n.Population });
        var width = 32 * count; var height = 32;
        var cells = new ushort[width * height]; var owners = new ushort[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            cells[y * width + x] = (ushort)(4001 + x / 32);
            owners[y * width + x] = (ushort)(700 + x / 32);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, width, height, _ => false));
        c.ConfigureSinglePlayer(0);
        foreach (var n in c.Nations) n.IsHuman = false;
        c.Defines.OrganizationSuspendMotionChance = 1f;
        c.Defines.OrganizationSeatMotionChance = 1f;
        c.Defines.OrganizationPreemptiveExitChance = 0f;
        c.Defines.OrganizationProtestChance = 1f;
        return c;
    }

    public static void Run()
    {
        Expulsion();
        CoupSplit();
        Readmission();
        Dissolution();
    }

    /// <summary>The Council of Europe and Russia: suspended for the attack, then voted out, and it costs.</summary>
    static void Expulsion()
    {
        var c = World(4);
        var pact = InternationalOrganizations.Seed(c, "Fixture pact", "collective_defence", new[] { 0, 1, 2, 3 });
        Check(pact is not null && InternationalOrganizations.DefencePartners(c, 3).Contains(1), "the fixture pact binds all four");
        var printBefore = CampaignFingerprint.Of(c);
        c.Wars.Add(new StrategicWar { Id = 1, AttackerId = 3, DefenderId = 1 });
        OrganizationDiscipline.Week(c, pact);
        Check(!pact.Members.Contains(3) && pact.Suspended.ContainsKey(3) && pact.SuspendedFor[3] == "aggression",
            "a member that attacks a fellow member is suspended the same week");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "organization_suspended_aggression" && e.ActorId == 3 && e.TargetId == 1),
            "the suspension is news, naming aggressor and victim");
        Check(!InternationalOrganizations.DefencePartners(c, 3).Any() && !InternationalOrganizations.DefencePartners(c, 0).Contains(3),
            "a suspended seat carries no common defence either way");
        Check(CampaignFingerprint.Of(c) != printBefore, "a suspension is authoritative state the fingerprint sees");
        var snap = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "discipline"), "discipline");
        Check(snap.Success && snap.Campaign.Organizations.Single().Suspended.ContainsKey(3) &&
              snap.Campaign.Organizations.Single().SuspendedFor[3] == "aggression", "a suspended seat survives a snapshot: " + snap.Warning);
        Check(InternationalOrganizations.Refusal(c, new OrganizationCommand { IssuerId = 3, OrganizationId = pact.Id, Action = "request_membership" }).Length > 0,
            "a suspended country cannot apply its way back in");

        pact.Suspended[3] = c.ElapsedDays - c.Defines.OrganizationExpelAfterDays - 1;
        var aggressor = c.NationById(3);
        var stability = aggressor.Stability;
        OrganizationDiscipline.Week(c, pact);
        Check(pact.Motion is { Kind: "expel", TargetId: 3 }, "a suspension whose cause stands goes to an expulsion vote");
        var motion = pact.Motion;
        DomesticCrisesTests.Day(c, motion.EndsDay);
        InternationalOrganizations.Tick(c);
        Check(pact.LastOutcome == "expel_passed" && !pact.Members.Contains(3) && !pact.Suspended.ContainsKey(3),
            $"the rest vote it out unanimously, the accused without a vote ({pact.LastYes} of {pact.LastVoters})");
        Check(pact.LastVoters == 3, "the accused is not counted among the voters");
        Check(aggressor.Stability < stability, "an expulsion costs the expelled government at home");
        Check(OpinionSystem.Has(CampaignDiplomacy.Peek(c, 3, 0), "voted_to_expel_us") &&
              OpinionSystem.Has(CampaignDiplomacy.Peek(c, 3, 0), "expelled_from_our_organization"),
            "the expelled remembers who voted it out, and they remember expelling it");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "organization_expelled_vote" && e.ActorId == 3), "the expulsion is news");
        Check(NewsFeed.Importance("organization_expelled_vote") >= 3 && Loc.Get("chronicle.organization_expelled_vote") != "chronicle.organization_expelled_vote",
            "the expulsion carries importance and a sentence");

        // Walking out before the vote: Russia left the Council of Europe the day before.
        var c2 = World(4);
        var council = InternationalOrganizations.Seed(c2, "Fixture council", "cooperation", new[] { 0, 1, 2, 3 });
        c2.Wars.Add(new StrategicWar { Id = 1, AttackerId = 3, DefenderId = 1 });
        OrganizationDiscipline.Week(c2, council);
        council.Suspended[3] = c2.ElapsedDays - c2.Defines.OrganizationExpelAfterDays - 1;
        c2.Defines.OrganizationPreemptiveExitChance = 1f;
        c2.NationById(3).Stability = 0.1f;
        council.Cohesion = 0.05f;
        council.UnpaidWeeks[3] = 30;
        var estranged = OrganizationDynamics.Estranged(c2, council, c2.NationById(3));
        OrganizationDiscipline.Week(c2, council);
        Check(!estranged || (!council.Suspended.ContainsKey(3) && council.Motion is null &&
              c2.Chronicle.Entries.Any(e => e.Kind == "organization_withdrew" && e.ActorId == 3)),
            $"an estranged member put to an expulsion vote walks out before it (estranged {estranged})");
    }

    /// <summary>ECOWAS and the Sahel juntas: a coup suspended, a fellow junta walks out in protest, and the two found a rival bloc.</summary>
    static void CoupSplit()
    {
        var c = World(5);
        var bloc = InternationalOrganizations.Seed(c, "Fixture community", "cooperation", new[] { 0, 1, 2, 3, 4 });
        var junta = c.NationById(4); var friend = c.NationById(3);
        junta.Government = "military_junta"; junta.LastMilitaryCoupDay = c.ElapsedDays;
        friend.Government = "military_junta";
        bloc.Cohesion = 0.1f; bloc.UnpaidWeeks[3] = 30;
        Check(OrganizationDiscipline.Cause(c, bloc, junta) == "coup" && OrganizationDiscipline.Cause(c, bloc, friend) == "",
            "a fresh coup in an assembly of elected governments is a breach; an old junta is not a new one");
        Check(OrganizationDynamics.Belonging(c, bloc, friend) < c.Defines.OrganizationDepartureBelonging + 0.15f,
            "the friend is already half out (fixture precondition)");
        OrganizationDiscipline.Week(c, bloc);
        Check(bloc.Motion is { Kind: "suspend", TargetId: 4 }, "the coup is put to a suspension vote by a member that would vote for it");
        DomesticCrisesTests.Day(c, bloc.Motion.EndsDay);
        InternationalOrganizations.Tick(c);
        Check(c.Chronicle.Entries.Any(e => e.Kind == "organization_suspended_coup" && e.ActorId == 4),
            $"the majority suspends the junta ({bloc.LastYes} of {bloc.LastVoters})");
        Check(!bloc.Members.Contains(3) && c.Chronicle.Entries.Any(e => e.Kind == "organization_protest_exit" && e.ActorId == 3 && e.TargetId == 4),
            "a fellow junta walks out in protest, and the news names whom for");
        var rival = InternationalOrganizations.Active(c).FirstOrDefault(o => o.Id != bloc.Id);
        Check(rival is not null && rival.Members.Contains(3) && rival.Members.Contains(4) && rival.CharterId == "cooperation",
            "the suspended and the protester found a rival bloc of the same kind");
        Check(!bloc.Suspended.ContainsKey(4) && bloc.Members.SequenceEqual(new[] { 0, 1, 2 }),
            "the split leaves the old organisation with the three that stayed");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "organization_rival_bloc" && e.ActorId == 4 && e.TargetId == 3),
            "the rival bloc is news");
    }

    /// <summary>Gabon and the African Union, 2025: the junta gone, the seat given back.</summary>
    static void Readmission()
    {
        var c = World(4);
        var council = InternationalOrganizations.Seed(c, "Fixture union", "cooperation", new[] { 0, 1, 2, 3 });
        var junta = c.NationById(3);
        junta.Government = "military_junta"; junta.LastMilitaryCoupDay = c.ElapsedDays;
        OrganizationDiscipline.Suspend(c, council, junta, "coup");
        Check(council.Suspended.ContainsKey(3) && OrganizationDiscipline.StandingCause(c, council, junta) == "coup",
            "a junta's suspension stands while the junta does");
        DomesticCrisesTests.Day(c, 500);
        Check(OrganizationDiscipline.StandingCause(c, council, junta) == "coup", "and outlasts the memory of the coup itself");
        junta.Government = "presidential_republic";
        OrganizationDiscipline.Week(c, council);
        Check(council.Motion is { Kind: "readmit", TargetId: 3 }, "with the cause gone and a year served, the seat is put to a vote");
        DomesticCrisesTests.Day(c, council.Motion.EndsDay);
        InternationalOrganizations.Tick(c);
        Check(council.Members.Contains(3) && !council.Suspended.ContainsKey(3) &&
              c.Chronicle.Entries.Any(e => e.Kind == "organization_readmitted" && e.ActorId == 3),
            "the assembly gives the seat back and the world hears of it");
    }

    /// <summary>A council nobody believes in is wound up; an old institution withers instead of folding.</summary>
    static void Dissolution()
    {
        var c = World(3);
        DomesticCrisesTests.Day(c, 1000);
        var forum = InternationalOrganizations.Seed(c, "Fixture forum", "cooperation", new[] { 0, 1, 2 });
        forum.Budget = 3f;
        c.Defines.OrganizationDissolveCohesion = 0.99f;
        c.Defines.OrganizationDissolveChance = 1f;
        Check(OrganizationDynamics.Durability(c, forum) == 1f, "a young council folds as readily as ever");
        var treasuries = c.Nations.Sum(n => n.Treasury);
        // Its weekday: each organisation is reviewed on its own day of the week.
        var day = c.ElapsedDays; while ((day + forum.Id * 3) % 7 != 0) day++;
        DomesticCrisesTests.Day(c, day);
        forum.Cohesion = 0.35f;
        OrganizationDynamics.Tick(c);
        Check(forum.Dissolved && c.Chronicle.Entries.Any(e => e.Kind == "organization_dissolved"),
            "a council below the line is wound up");
        Check(Math.Abs(c.Nations.Sum(n => n.Treasury) - treasuries - 3f) < 0.001f, "its common fund goes back to the members");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "organization_rift") || forum.Cohesion >= c.Defines.OrganizationRiftCohesion,
            "falling through the rift line on the way down is news");
        var old = InternationalOrganizations.Seed(c, "Fixture institution", "cooperation", new[] { 0, 1, 2 });
        old.FoundedDay = 0;
        Check(OrganizationDynamics.Durability(c, old) < 0.2f, "an institution the world began with withers before it folds");
    }
}
