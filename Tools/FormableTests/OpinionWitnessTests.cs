using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// What bystanders make of other countries' quarrels, and the ways back from a bad relationship
/// (OpinionWitnesses, DiplomaticGestures "apology" and "humanitarian_aid", DiplomaticOutreach).
///
/// Written for a player's complaint: two dishonest acts as China, and Germany, Brazil and Australia
/// were at minus a hundred over things that had nothing to do with them. The world here is five
/// countries on one strip of map - an actor, its victim next door, the victim's ally next door to
/// that, the victim's rival and a country with no ties at all, both on the far side of the globe -
/// with two filler countries keeping the far ones from sharing a border with anybody who matters.
/// </summary>
static class OpinionWitnessTests
{
    static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("opinion witnesses: " + text);
        Console.WriteLine("PASS opinion witnesses: " + text);
    }

    sealed record World(Campaign C, Nation Actor, Nation Victim, Nation Ally, Nation Rival, Nation Far);

    static World Build()
    {
        var c = new Campaign(20261101);
        Nation Add(string tag, int map, float lat, float lon) => c.AddNation(new Nation
        {
            Tag = tag, Name = tag, MapIndex = map, Population = 10_000_000, Gdp = 200, Treasury = 50,
            PoliticalPoints = 100, Ideology = "liberalism", OutputScale = 1, Stability = 0.7f,
            CapitalLatitude = lat, CapitalLongitude = lon
        });
        var far = Add("FAR", 501, -40f, -70f);
        var filler1 = Add("FLA", 502, -35f, -65f);
        var rival = Add("RIV", 503, -30f, -60f);
        var filler2 = Add("FLB", 504, 0f, 0f);
        var ally = Add("ALY", 505, 30f, 106f);
        var victim = Add("VIC", 506, 30f, 103f);
        var actor = Add("ACT", 507, 30f, 100f);
        var filler3 = Add("FLC", 508, 0f, 30f);
        // The strip wraps round like the world map does, so the last country borders the first:
        // a filler at each end keeps the far ones from touching the actor.
        var order = new[] { far, filler1, rival, filler2, ally, victim, actor, filler3 };
        var states = new ushort[order.Length];
        var control = new ushort[order.Length];
        for (var index = 0; index < order.Length; index++)
        {
            var nation = order[index];
            states[index] = (ushort)(1001 + index);
            control[index] = (ushort)nation.MapIndex;
            c.States.Add(new ScenarioState
            {
                Id = "witness_" + nation.Tag, MapIndex = 1001 + index, NationId = nation.Tag,
                NationMapIndex = nation.MapIndex, Population = nation.Population
            });
        }
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(states, control, order.Length, 1, _ => false));
        c.SetPlayer(far.Id);
        SovereignFinance.NormalizeAccounts(c);
        EconomySystem.Explain(c);
        var bond = CampaignDiplomacy.Get(c, ally.Id, victim.Id);
        bond.Stance = DiplomaticStance.Allied;
        var feud = CampaignDiplomacy.Get(c, rival.Id, victim.Id);
        feud.Stance = DiplomaticStance.Rival;
        return new World(c, actor, victim, ally, rival, far);
    }

    static float Held(Campaign c, Nation one, Nation other, string id)
    {
        var relation = CampaignDiplomacy.Peek(c, one.Id, other.Id);
        var reason = relation?.Reasons.FirstOrDefault(r => r.Id == id);
        return reason is null ? 0f : OpinionSystem.Weight(c, reason);
    }

    public static void Run()
    {
        Witnesses();
        Notoriety();
        Ceiling();
        Embargo();
        Apology();
        Aid();
        Mission();
    }

    static void Witnesses()
    {
        var w = Build();
        var c = w.C;
        Check(OpinionWitnesses.FearReach(c, w.Far, w.Actor) <= c.Defines.FearOfStrengthFarShare + 0.01f && OpinionWitnesses.FearReach(c, w.Victim, w.Actor) >= 0.99f,
            "fear of a stronger country is felt in full next door and only at the far share across the world");

        // The actor attacks a fellow member of an organisation all five sit in.
        foreach (var observer in new[] { w.Far, w.Rival, w.Ally, w.Victim })
            OpinionWitnesses.Witness(c, observer, w.Actor, w.Victim, "attacked_a_fellow_member", tie: c.Defines.WitnessOrganizationTie);

        var far = Held(c, w.Far, w.Actor, "attacked_a_fellow_member");
        Check(far > -(c.Defines.WitnessBaseConcern + 0.15f) * 30f, $"(a) a remote country with no ties reacts only at about the base concern ({far:0.0} of -30)");
        var rivalAnger = Held(c, w.Rival, w.Actor, "attacked_a_fellow_member");
        var rivalApproval = Held(c, w.Rival, w.Actor, "acted_against_our_rival");
        Check(rivalAnger == 0f && rivalApproval > 0f,
            $"(b) the victim's rival does not punish the actor and quietly approves ({rivalApproval:+0.0})");
        var victim = Held(c, w.Victim, w.Actor, "attacked_a_fellow_member");
        var ally = Held(c, w.Ally, w.Actor, "attacked_a_fellow_member");
        Check(victim <= -29.9f, $"(c) the victim remembers it in full ({victim:0.0})");
        Check(ally <= -25f, $"(c) the victim's ally next door remembers it nearly in full ({ally:0.0})");

        // Without the organisation, a far country with nothing at stake does not react at all,
        // and no relation is created to say so.
        var w2 = Build();
        var before = w2.C.Relations.Count;
        var concern = OpinionWitnesses.Witness(w2.C, w2.Far, w2.Actor, w2.Victim, "troops_on_friends_land");
        Check(concern < w2.C.Defines.WitnessThreshold && CampaignDiplomacy.Peek(w2.C, w2.Far.Id, w2.Actor.Id) is null &&
              w2.C.Relations.Count == before, $"an act that does not concern a country leaves no trace with it ({concern:0.00})");
    }

    static void Notoriety()
    {
        var w = Build();
        var c = w.C;
        w.Actor.Notoriety = 60f;
        CampaignDiplomacy.Get(c, w.Far.Id, w.Actor.Id);
        CampaignDiplomacy.Get(c, w.Victim.Id, w.Actor.Id);
        DomesticCrisesTests.Day(c, 7);
        Statecraft.Tick(c);
        var far = Held(c, w.Far, w.Actor, "notoriety");
        var near = Held(c, w.Victim, w.Actor, "notoriety");
        Check(far > -6f && near < far - 5f,
            $"a land-grabber's bad name is minded next door and barely on the far side of the world ({near:0.0} vs {far:0.0})");
    }

    static void Ceiling()
    {
        var w = Build();
        var c = w.C;
        var relation = CampaignDiplomacy.Get(c, w.Far.Id, w.Actor.Id);
        foreach (var id in new[] { "nuclear_test", "nuclear_threats", "seized_power_by_coup", "troops_on_friends_land", "attacked_a_fellow_member" })
            OpinionSystem.Remember(c, relation, id);
        var raw = relation.Reasons.Sum(r => OpinionSystem.Weight(c, r));

        Check(raw < -60f && relation.Opinion >= c.Defines.ThirdPartyOpinionFloor - 1f,
            $"everything held as a bystander adds up to no more than the ceiling ({raw:0} remembered, opinion {relation.Opinion:0})");
        Check(relation.OpinionDrivers.Entries.Any(e => e.Source.Contains("quarrels")), "the cap is a line of its own on the opinion panel");

        var direct = CampaignDiplomacy.Get(c, w.Victim.Id, w.Actor.Id);
        OpinionSystem.Remember(c, direct, "declared_war_on_us");
        OpinionSystem.Remember(c, direct, "fought_a_war");
        Check(direct.Opinion <= -70f, $"(c) a direct victim's grievances are not capped ({direct.Opinion:0})");
    }

    static void Embargo()
    {
        var w = Build();
        var c = w.C;
        new SetEmbargoCommand { IssuerId = w.Actor.Id, TargetId = w.Victim.Id, Embargo = true }.Apply(c);
        new SetEmbargoCommand { IssuerId = w.Victim.Id, TargetId = w.Actor.Id, Embargo = true }.Apply(c);
        var relation = CampaignDiplomacy.Peek(c, w.Actor.Id, w.Victim.Id);
        new SetEmbargoCommand { IssuerId = w.Actor.Id, TargetId = w.Victim.Id, Embargo = false }.Apply(c);
        Check(OpinionSystem.Has(relation, "embargoed_us"), "one side lifting its embargo leaves the other side's grievance in place");
        new SetEmbargoCommand { IssuerId = w.Victim.Id, TargetId = w.Actor.Id, Embargo = false }.Apply(c);
        Check(!OpinionSystem.Has(relation, "embargoed_us"), "the grievance goes when nobody is refusing any more");
    }

    static void Apology()
    {
        var w = Build();
        var c = w.C;
        var relation = CampaignDiplomacy.Get(c, w.Actor.Id, w.Victim.Id);
        Check(DiplomaticGestures.Refusal(c, w.Actor.Id, w.Victim.Id, "apology").Length > 0, "there is nothing to apologise for in a clean relationship");
        OpinionSystem.Remember(c, relation, "denounced");
        OpinionSystem.Remember(c, relation, "covert_operation_against_us");
        OpinionSystem.Remember(c, relation, "nuclear_test");
        DomesticCrisesTests.Day(c, 60);
        OpinionSystem.Settle(c, relation);
        var before = relation.Opinion;
        var cash = w.Victim.Treasury;
        Check(DiplomaticGestures.Refusal(c, w.Actor.Id, w.Victim.Id, "apology").Length == 0, "an apology needs no warm opinion to be made");
        Check(DiplomaticGestures.Perform(c, w.Actor.Id, w.Victim.Id, "apology"), "(d) the apology is made");
        Check(relation.Opinion >= before + 25f && w.Victim.Treasury > cash,
            $"(d) an apology with compensation mends at once ({before:0} -> {relation.Opinion:0}) and pays the injured side");
        Check(DiplomaticGestures.Refusal(c, w.Actor.Id, w.Victim.Id, "apology").Length > 0, "and cannot be repeated at once");
    }

    static void Aid()
    {
        var w = Build();
        var c = w.C;
        Check(DiplomaticGestures.Refusal(c, w.Actor.Id, w.Far.Id, "humanitarian_aid").Length > 0,
            "humanitarian aid is not for a stable country as rich as we are");
        w.Far.Stability = 0.2f;
        var relation = CampaignDiplomacy.Get(c, w.Actor.Id, w.Far.Id);
        OpinionSystem.Remember(c, relation, "nuclear_test");
        OpinionSystem.Settle(c, relation);
        var before = relation.Opinion;
        Check(DiplomaticGestures.Perform(c, w.Actor.Id, w.Far.Id, "humanitarian_aid") && relation.Opinion >= before + 12f,
            $"(d) help sent to a country in need is remembered whatever it thought of us ({before:0} -> {relation.Opinion:0})");
    }

    static void Mission()
    {
        var w = Build();
        var c = w.C;
        var relation = CampaignDiplomacy.Get(c, w.Actor.Id, w.Far.Id);
        foreach (var id in new[] { "nuclear_test", "seized_power_by_coup", "attacked_a_fellow_member" })
            OpinionSystem.Remember(c, relation, id);
        OpinionSystem.Settle(c, relation);
        var start = relation.Opinion;
        var start3rd = relation.Reasons.Where(r => OpinionCatalog.Find(r.Id) is { ThirdParty: true }).Sum(r => OpinionSystem.Weight(c, r));
        // Their embargo no longer closes the road back.
        if (relation.A == w.Far.Id) relation.EmbargoByA = true; else relation.EmbargoByB = true;
        var command = new ImproveRelationsCommand { IssuerId = w.Actor.Id, TargetId = w.Far.Id };
        command.Apply(c);
        Check(command.Changed, "a goodwill mission can be sent to a country that embargoes us");
        if (relation.A == w.Far.Id) relation.EmbargoByA = false; else relation.EmbargoByB = false;
        var middle = 0f;
        for (var day = 1; day <= 175; day++)
        {
            DomesticCrisesTests.Day(c, day);
            OpinionSystem.Tick(c);
            if (day == 84) middle = relation.Opinion;
        }
        var end3rd = relation.Reasons.Where(r => OpinionCatalog.Find(r.Id) is { ThirdParty: true }).Sum(r => OpinionSystem.Weight(c, r));
        Check(middle > start + 8f && relation.Opinion > middle,
            $"(d) a sustained mission raises opinion over time ({start:0} -> {middle:0} -> {relation.Opinion:0})");
        Check(end3rd > start3rd * 0.25f,
            $"(d) and wears down what is held over other countries' quarrels ({start3rd:0} -> {end3rd:0})");
    }
}
