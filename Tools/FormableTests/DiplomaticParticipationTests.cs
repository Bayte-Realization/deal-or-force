using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class DiplomaticParticipationTests
{
    static void Check(bool condition, string caption)
    { if (!condition) throw new Exception("FAIL diplomatic participation: " + caption); Console.WriteLine("PASS diplomatic participation: " + caption); }

    static Campaign World()
    {
        var c = new Campaign(250082) { ScenarioId = "participation_fixture" };
        c.Defines.AiMayFoundOrganizations = false;
        c.Defines.AiGreatPowers = 1;
        for (var i = 0; i < 4; i++)
        {
            var n = c.AddNation(new Nation { Tag = "DP" + i, Name = "Country " + i, MapIndex = 500 + i,
                Population = 1_000_000, Gdp = i == 2 ? 5000 : 100, Treasury = 20, PoliticalPoints = 100,
                Stability = .8f, Region = "participation", IsHuman = true });
            c.States.Add(new ScenarioState { Id = "dp_" + i, MapIndex = 3001 + i, NationId = n.Tag,
                NationMapIndex = n.MapIndex, Population = n.Population });
        }
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Range(0, 16).Select(i => (ushort)(3001 + i / 4)).ToArray(),
            Enumerable.Range(0, 16).Select(i => (ushort)(500 + i / 4)).ToArray(), 16, 1, _ => false));
        c.ConfigureSinglePlayer(2);
        foreach (var n in c.Nations) n.IsHuman = true;
        for (var a = 0; a < 4; a++) for (var b = a + 1; b < 4; b++) CampaignDiplomacy.Get(c, a, b).Opinion = 60;
        return c;
    }
    static void Day(Campaign c, int day) => DomesticCrisesTests.Day(c, day);
    static OrganizationCommand Action(int issuer, InternationalOrganization o, string action) =>
        new() { IssuerId = issuer, OrganizationId = o.Id, Action = action };
    static InternationalOrganization Council(Campaign c, string charter = "cooperation") =>
        InternationalOrganizations.Seed(c, "Participation council", charter, new[] { 0, 1 });
    static void Apply(Campaign c, ICampaignCommand command)
    { c.Commands.Enqueue(command); c.Commands.ApplyAll(c); }

    public static void Run()
    {
        FoundingParity(); ApplicationFlow(); RefusalsAndWithdrawal(); CrisisCommands();
    }
    static void FoundingParity()
    {
        foreach (var human in new[] { true, false })
        {
            var c = World(); var founder = c.Nations[0]; founder.IsHuman = human;
            var before = CampaignFingerprint.Of(c); var random = c.Random.State;
            var command = new OrganizationCommand { IssuerId = 0, Action = "create", Name = "Equal rules", CharterId = "cooperation" };
            Check(InternationalOrganizations.Refusal(c, command).Length == 0 && CampaignFingerprint.Of(c) == before && c.Random.State == random,
                "founding preview changes no state or randomness: human=" + human);
            Apply(c, command);
            Check(command.Changed && founder.Treasury == 19.75f && founder.PoliticalPoints == 70,
                "founding charges the same deposit and 30 political points: human=" + human);
            Apply(c, command);
            Check(!command.Changed && founder.PoliticalPoints == 70 && founder.Treasury == 19.75f && c.Organizations.Count == 1,
                "duplicate founding never charges again: human=" + human);
        }
        var low = World(); low.Nations[0].PoliticalPoints = 29;
        var denied = new OrganizationCommand { IssuerId = 0, Action = "create", Name = "Too early", CharterId = "cooperation" };
        var fp = CampaignFingerprint.Of(low); Apply(low, denied);
        Check(!denied.Changed && CampaignFingerprint.Of(low) == fp, "insufficient political capital is refused atomically");
        var ai = World(); ai.Nations[0].IsHuman = false;
        var wrapper = new FoundCooperationForumCommand { IssuerId = 0, TargetId = 1, CharterId = "collective_defence", OrganizationName = "Consent first" };
        Apply(ai, wrapper);
        Check(wrapper.Changed && ai.Nations[0].PoliticalPoints == 70, "AI wrapper pays the common founding charge exactly once");
        Check(ai.Organizations.Single().Motion is { Kind: "admission", TargetId: 1 } &&
            !ai.Organizations.Single().Members.Contains(1) && CampaignDiplomacy.Peek(ai, 0, 1).Stance != DiplomaticStance.Allied,
            "founding a defensive bloc does not force membership or a bilateral alliance");
        low.Defines.OrganicOrganizationReachEnabled = false; Apply(low, denied);
        Check(denied.Changed && low.Nations[0].PoliticalPoints == 29, "legacy reach setting has the same zero political fee for both callers");
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -1f })
        {
            var bad = World(); bad.Defines.AiOrganizationFoundingPoliticalCost = invalid;
            var before = CampaignFingerprint.Of(bad);
            var order = new OrganizationCommand { IssuerId = 0, Action = "create", Name = "Invalid price", CharterId = "cooperation" };
            Apply(bad, order);
            Check(!order.Changed && CampaignFingerprint.Of(bad) == before, "invalid common founding fee is refused without changes: " + invalid);
        }
    }
    static void ApplicationFlow()
    {
        foreach (var human in new[] { true, false })
        {
            var c = World(); var o = Council(c); c.Nations[2].IsHuman = human;
            var request = Action(2, o, "request_membership");
            var fp = CampaignFingerprint.Of(c); var random = c.Random.State;
            Check(InternationalOrganizations.Refusal(c, request).Length == 0 && CampaignFingerprint.Of(c) == fp && c.Random.State == random,
                "application preview is read-only: human=" + human);
            Apply(c, request); var motion = o.Motion;
            Check(request.Changed && motion is { Kind: "admission", ProposerId: 2, TargetId: 2 } && motion.Votes.Count == 0,
                "outsider starts the same admission vote without voting for itself: human=" + human);
            Check(!o.Members.Contains(2) && !o.Invitations.Contains(2) && c.IsPaused,
                "request starts immediately while paused but grants no membership or invitation");
            var vote = new OrganizationCommand { IssuerId = 2, OrganizationId = o.Id, Action = "vote", MotionId = motion.Id, Yes = true };
            Apply(c, vote); Check(!vote.Changed && motion.Votes.Count == 0, "applicant cannot vote in the assembly");
            Check(NetworkCommandCodec.TryEncode(request, out var type, out var json) && NetworkCommandCodec.TryDecode(type, json, out var decoded) &&
                decoded is OrganizationCommand { Action: "request_membership", IssuerId: 2 }, "membership application round-trips through the command codec");
            var copy = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "participation"), "participation");
            Check(copy.Success && copy.Campaign.Organizations.Single().Motion is { ProposerId: 2, TargetId: 2 } &&
                copy.Campaign.Nations[2].DecisionAvailableDay.GetValueOrDefault("organization_application/" + o.Id) > c.ElapsedDays,
                "application and anti-spam cooldown survive a real snapshot");
            foreach (var member in o.Members) Apply(c, new OrganizationCommand { IssuerId = member, OrganizationId = o.Id, Action = "vote", MotionId = motion.Id, Yes = true });
            Day(c, motion.EndsDay); InternationalOrganizations.Tick(c);
            Check(o.Invitations.Contains(2) && !o.Members.Contains(2), "successful assembly vote gives an invitation, not forced accession");
            if (human) Apply(c, Action(2, o, "join"));
            else { Day(c, c.ElapsedDays + 1); InternationalOrganizations.Tick(c); }
            Check(o.Members.Contains(2) && !o.Invitations.Contains(2), "human accepts explicitly; AI uses its existing consent assessment");
        }
        var rejected = World(); var council = Council(rejected, "collective_defence");
        Apply(rejected, Action(2, council, "request_membership")); var pending = council.Motion;
        Apply(rejected, new OrganizationCommand { IssuerId = 0, OrganizationId = council.Id, Action = "vote", MotionId = pending.Id, Yes = true });
        Day(rejected, pending.EndsDay); InternationalOrganizations.Tick(rejected);
        Check(council.LastOutcome == "admission_rejected" && !council.Invitations.Contains(2), "defensive charter still requires every member's approval");
        foreach (var opinion in new[] { 60f, -60f })
        {
            var ai = World(); var assembly = Council(ai);
            foreach (var id in assembly.Members)
            { ai.Nations[id].IsHuman = false; CampaignDiplomacy.Get(ai, id, 2).Opinion = opinion; }
            Apply(ai, Action(2, assembly, "request_membership"));
            var vote = assembly.Motion; Day(ai, vote.EndsDay); InternationalOrganizations.Tick(ai);
            Check(assembly.Invitations.Contains(2) == (opinion > 0) && !assembly.Members.Contains(2),
                "AI assembly assesses the player's application through existing voting rules: opinion=" + opinion);
        }
    }
    static void RefusalsAndWithdrawal()
    {
        var c = World(); var o = Council(c);
        Apply(c, Action(2, o, "request_membership"));
        var busy = CampaignFingerprint.Of(c); Apply(c, Action(3, o, "request_membership"));
        Check(CampaignFingerprint.Of(c) == busy && o.Motion.TargetId == 2, "another application cannot overwrite an active assembly vote");
        var fp = CampaignFingerprint.Of(c); Apply(c, Action(3, o, "withdraw_application"));
        Check(CampaignFingerprint.Of(c) == fp, "another country cannot withdraw the applicant's vote");
        Apply(c, Action(2, o, "withdraw_application"));
        Check(o.Motion is null && !o.Members.Contains(2), "withdrawal removes only the pending application");
        fp = CampaignFingerprint.Of(c); Apply(c, Action(2, o, "request_membership"));
        Check(o.Motion is null && CampaignFingerprint.Of(c) == fp, "withdrawing cannot reset the retry cooldown");
        Day(c, c.Nations[2].DecisionAvailableDay["organization_application/" + o.Id]);
        Apply(c, Action(2, o, "request_membership")); Check(o.Motion is not null, "a fresh application is possible after the cooldown");
        var wartime = World(); var wo = Council(wartime);
        wartime.Wars.Add(new StrategicWar { Id = 9, AttackerId = 2, DefenderId = 0 });
        fp = CampaignFingerprint.Of(wartime); Apply(wartime, Action(2, wo, "request_membership"));
        Check(CampaignFingerprint.Of(wartime) == fp && wo.Motion is null, "war with a member blocks an application without side effects");
        var capped = World(); var co = Council(capped); capped.Defines.OrganizationMembershipLimit = 1;
        InternationalOrganizations.Seed(capped, "Other council", "cooperation", new[] { 2, 3 });
        Apply(capped, Action(2, co, "request_membership")); Check(co.Motion is null, "membership limit also applies to applicants");
        c.FeatureOverrides["organizations"] = false; fp = CampaignFingerprint.Of(c);
        Apply(c, Action(2, o, "withdraw_application")); Check(CampaignFingerprint.Of(c) == fp, "disabled organizations freeze application actions too");
    }
    static Campaign Crisis()
    {
        var c = World(); var relation = CampaignDiplomacy.Get(c, 0, 1);
        relation.Tension = c.Defines.TensionCrisisAt + 15;
        relation.TensionStage = (int)TensionStage.Crisis;
        return c;
    }
    static void CrisisCommands()
    {
        var c = Crisis(); var treasury = c.Nations[2].Treasury;
        var fp = CampaignFingerprint.Of(c); var random = c.Random.State;
        Check(Escalation.MediationBlock(c, c.Nations[2], c.Nations[0], c.Nations[1]).Length == 0 &&
            CampaignFingerprint.Of(c) == fp && c.Random.State == random, "crisis action preview is read-only");
        Apply(c, new MediateCrisisCommand { IssuerId = 3, MediatorId = 2, TargetAId = 0, TargetBId = 1 });
        Check(CampaignFingerprint.Of(c) == fp, "issuer cannot spend another Great Power's treasury through a forged mediator ID");
        var mediation = new MediateCrisisCommand { IssuerId = 2, MediatorId = 2, TargetAId = 0, TargetBId = 1 };
        Apply(c, mediation);
        Check(mediation.Mediated && c.IsPaused && c.ElapsedDays == 0 && c.Nations[2].Treasury == treasury - c.Defines.CrisisMediationCost,
            "player mediation pays its cost immediately without advancing or unpausing");
        Check(CampaignDiplomacy.Peek(c, 0, 1).MediatorId == 2 && c.Wars.Count == 0, "mediation records the real actor without starting a war");
        treasury = c.Nations[2].Treasury; Apply(c, mediation);
        Check(!mediation.Mediated && c.Nations[2].Treasury == treasury, "duplicate mediation cannot charge a second fee");
        var direct = Crisis(); Escalation.StartMediation(direct, direct.Nations[2], direct.Nations[0], direct.Nations[1]);
        Check(direct.Nations[2].Treasury == c.Nations[2].Treasury && CampaignDiplomacy.Peek(direct, 0, 1).Tension == CampaignDiplomacy.Peek(c, 0, 1).Tension,
            "AI core and player command have identical mediation costs and effects");
        var interventionWorld = Crisis(); fp = CampaignFingerprint.Of(interventionWorld);
        Apply(interventionWorld, new InterveneCrisisCommand { IssuerId = 3, PowerId = 2, ProtegeId = 0, AggressorId = 1 });
        Check(CampaignFingerprint.Of(interventionWorld) == fp, "issuer cannot forge another country's intervention");
        var intervene = new InterveneCrisisCommand { IssuerId = 2, PowerId = 2, ProtegeId = 0, AggressorId = 1 };
        Apply(interventionWorld, intervene);
        Check(intervene.Intervened && CampaignDiplomacy.Peek(interventionWorld, 0, 1).InterveningPowerId == 2 &&
            CampaignDiplomacy.Peek(interventionWorld, 0, 1).InterventionTargetId == 1 && interventionWorld.Wars.Count == 0,
            "player intervention backs the selected side but declares no war");
        Check(NetworkCommandCodec.TryEncode(intervene, out var type, out var json) && NetworkCommandCodec.TryDecode(type, json, out var restored) &&
            restored is InterveneCrisisCommand { IssuerId: 2, PowerId: 2, ProtegeId: 0, AggressorId: 1 }, "intervention actor and chosen sides survive command serialization");
        var small = Crisis(); var denied = new MediateCrisisCommand { IssuerId = 3, MediatorId = 3, TargetAId = 0, TargetBId = 1 };
        Apply(small, denied); Check(!denied.Mediated, "a non-Great Power cannot bypass the same role restriction used by AI");
    }
}
