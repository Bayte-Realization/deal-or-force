using System;
using System.Linq;
using DealOrForce.Simulation;

static class OrganizationTests
{
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS organization: " + text); }
    public static void Run()
    {
        var c = new Campaign(82);
        for (var i = 0; i < 4; i++) c.Nations.Add(new Nation { Id = i, MapIndex = 500+i, Tag = "O"+i,
            Name = "Country " + i, Population = 1000000, Treasury = 20, PoliticalPoints = 100, Gdp = 100, IsHuman = true });
        c.ScenarioId = "org_fixture";
        foreach (var n in c.Nations) c.States.Add(new ScenarioState { MapIndex = 3001 + n.Id, Id = "org_" + n.Id, Name = "Region " + n.Id,
            NationMapIndex = n.MapIndex, NationId = n.Tag, Population = n.Population });
        var cells = Enumerable.Range(0, 8192).Select(i => (ushort)(3001 + i % 128 / 32)).ToArray();
        var flags = Enumerable.Range(0, 8192).Select(i => (ushort)(500 + i % 128 / 32)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, flags, 128, 64, _ => false));
        c.ConfigureSinglePlayer(0); foreach (var n in c.Nations) n.IsHuman = true;
        var create = new OrganizationCommand { IssuerId = 0, Action = "create", Name = "A council", CharterId = "development" };
        create.Apply(c); var o = c.Organizations.Single();
        Check(create.Changed && o.Members.SequenceEqual(new[] {0}) && o.Budget == .25f && c.NationById(0).Treasury == 19.75f,
            "founding moves a real deposit into a separate entity");
        create.Apply(c); Check(c.Organizations.Count == 1 && c.NationById(0).Treasury == 19.75f, "duplicate creation cannot charge again");
        var invite = new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "admission", TargetId = 1 };
        invite.Apply(c); Check(o.Motion is not null && !o.Members.Contains(1), "proposing admission does not force membership");
        DomesticCrisesTests.Day(c, o.Motion.EndsDay); InternationalOrganizations.Tick(c);
        Check(o.Invitations.Contains(1) && !o.Members.Contains(1), "assembly approval creates an invitation requiring consent");
        new OrganizationCommand { IssuerId = 1, OrganizationId = o.Id, Action = "join" }.Apply(c);
        Check(o.Members.Contains(1), "invited country makes its own accession decision");
        new OrganizationCommand { IssuerId = 2, OrganizationId = o.Id, Action = "join" }.Apply(c);
        Check(!o.Members.Contains(2), "uninvited outsiders cannot silently join");
        var total = c.Nations.Sum(n => n.Treasury) + o.Budget;
        DomesticCrisesTests.Day(c, 28); InternationalOrganizations.Tick(c);
        Check(Math.Abs(c.Nations.Sum(n => n.Treasury) + o.Budget - total) < .0001f, "weekly dues conserve money");
        var budget = o.Budget; InternationalOrganizations.Tick(c); Check(o.Budget == budget, "repeat review cannot collect dues twice");
        new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "grant", TargetId = 1, Amount = .1f }.Apply(c);
        var motion = o.Motion; Check(motion is not null, "a member can table a real grant");
        new OrganizationCommand { IssuerId = 2, OrganizationId = o.Id, Action = "vote", MotionId = motion.Id, Yes = true }.Apply(c);
        Check(!motion.Votes.ContainsKey(2), "an outsider cannot vote");
        new OrganizationCommand { IssuerId = 1, OrganizationId = o.Id, Action = "vote", MotionId = motion.Id, Yes = false }.Apply(c);
        DomesticCrisesTests.Day(c, motion.EndsDay); InternationalOrganizations.Tick(c);
        Check(o.LastOutcome == "grant_rejected", "one of two is not an assembly majority");
        new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "grant", TargetId = 1, Amount = .1f }.Apply(c);
        motion = o.Motion; new OrganizationCommand { IssuerId = 1, OrganizationId = o.Id, Action = "vote", MotionId = motion.Id, Yes = true }.Apply(c);
        var receiver = c.NationById(1).Treasury; budget = o.Budget;
        var receiverDues = (motion.EndsDay - o.FoundedDay) % 7 == 0 ? InternationalOrganizations.Dues(c, o, c.NationById(1)) : 0;
        DomesticCrisesTests.Day(c, motion.EndsDay); InternationalOrganizations.Tick(c);
        Check(o.LastOutcome == "grant_passed" && Math.Abs(c.NationById(1).Treasury - receiver - .1f + receiverDues) < .0001f && o.Budget <= budget,
            "approved aid is funded, not conjured");
        var snap = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "organizations"), "organizations");
        Check(snap.Success && snap.Campaign.Organizations.Single().Members.Count == 2, "organizations survive host snapshots: " + snap.Warning);
        var defence = InternationalOrganizations.Seed(c, "Defence", "collective_defence", new[] {0,1,2});
        Check(InternationalOrganizations.DefencePartners(c, 0).SequenceEqual(new[]{1,2}) && CampaignDiplomacy.Peek(c,0,1)?.Stance != DiplomaticStance.Allied,
            "common defence is not a web of bilateral alliance records");
        var war = new StrategicWar { Id=1,AttackerId=3,DefenderId=0 }; c.Wars.Add(war);
        var answer = new OrganizationCommand { IssuerId=2,OrganizationId=defence.Id,Action="defend",WarId=war.Id };
        var beforeDefence = CampaignFingerprint.Of(c);
        Check(InternationalOrganizations.Refusal(c,answer)=="" && beforeDefence==CampaignFingerprint.Of(c), "human defence-call preview is read-only");
        answer.Apply(c); Check(war.IsDefending(2) && !war.IsAttacking(2), "a member explicitly joins the existing defensive side, not a second war");
        beforeDefence=CampaignFingerprint.Of(c); answer.Apply(c);
        Check(beforeDefence==CampaignFingerprint.Of(c), "the same collective-defence call cannot be answered twice");
        war.Ended=true;
        var admission = new OrganizationCommand { IssuerId=0,OrganizationId=defence.Id,Action="propose",Kind="admission",TargetId=3 };
        admission.Apply(c); var defenceMotion=defence.Motion;
        new OrganizationCommand { IssuerId=1,OrganizationId=defence.Id,Action="vote",MotionId=defenceMotion.Id,Yes=true }.Apply(c);
        new OrganizationCommand { IssuerId=2,OrganizationId=defence.Id,Action="vote",MotionId=defenceMotion.Id,Yes=false }.Apply(c);
        DomesticCrisesTests.Day(c,defenceMotion.EndsDay); InternationalOrganizations.Tick(c);
        Check(!defence.Invitations.Contains(3) && defence.LastOutcome=="admission_rejected", "one explicit veto defeats an otherwise supported unanimity motion");
        // Leadership & challenger calculation:
        c.NationById(0).Gdp = 500; c.NationById(1).Gdp = 320; c.NationById(2).Gdp = 80;
        Check(InternationalOrganizations.Leader(c, defence)?.Id == 0 && InternationalOrganizations.Challenger(c, defence)?.Id == 1,
            "leading power and challenger are derived from member weight");
        // Doctrine proposal and adoption:
        var docCmd = new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "doctrine", Subject = "common_market" };
        docCmd.Apply(c);
        Check(o.Motion is not null && o.Motion.Kind == "doctrine" && o.Motion.Subject == "common_market",
            "a member can propose a bloc doctrine");
        new OrganizationCommand { IssuerId = 1, OrganizationId = o.Id, Action = "vote", MotionId = o.Motion.Id, Yes = true }.Apply(c);
        var beforeDocHash = CampaignFingerprint.Of(c);
        DomesticCrisesTests.Day(c, o.Motion.EndsDay); InternationalOrganizations.Tick(c);
        Check(o.DoctrineId == "common_market" && o.LastOutcome == "doctrine_passed" && CampaignFingerprint.Of(c) != beforeDocHash,
            "approved bloc doctrine takes effect and moves the campaign fingerprint");
        Check(InternationalOrganizations.Refusal(c, new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "doctrine", Subject = "common_market" }).Length > 0,
            "cannot propose a doctrine already in force");
        // Multilateral sanctions resolution:
        Check(InternationalOrganizations.Refusal(c, new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "sanction", TargetId = 1 }).Length > 0,
            "an organization cannot vote collective sanctions against its own member");
        var sanctionCmd = new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "sanction", TargetId = 3 };
        sanctionCmd.Apply(c);
        new OrganizationCommand { IssuerId = 1, OrganizationId = o.Id, Action = "vote", MotionId = o.Motion.Id, Yes = true }.Apply(c);
        DomesticCrisesTests.Day(c, o.Motion.EndsDay); InternationalOrganizations.Tick(c);
        Check(o.LastOutcome == "sanction_passed" &&
              o.Members.All(m => CampaignDiplomacy.Peek(c, m, 3)?.EmbargoBy(m) == true),
            "passed multilateral sanctions resolution imposes an embargo from every member onto the target");
        var liftCmd = new OrganizationCommand { IssuerId = 0, OrganizationId = o.Id, Action = "propose", Kind = "lift_sanction", TargetId = 3 };
        liftCmd.Apply(c);
        new OrganizationCommand { IssuerId = 1, OrganizationId = o.Id, Action = "vote", MotionId = o.Motion.Id, Yes = true }.Apply(c);
        DomesticCrisesTests.Day(c, o.Motion.EndsDay); InternationalOrganizations.Tick(c);
        Check(o.LastOutcome == "lift_sanction_passed" &&
              o.Members.All(m => CampaignDiplomacy.Peek(c, m, 3)?.EmbargoBy(m) != true),
            "passed lift-sanctions resolution lifts members' embargoes on the target");
        new OrganizationCommand { IssuerId = 1, OrganizationId = defence.Id, Action = "leave" }.Apply(c);
        Check(!InternationalOrganizations.DefencePartners(c,0).Contains(1) && CampaignDiplomacy.Peek(c,0,1).TruceUntil > c.ElapsedDays,
            "leaving actually removes the obligation and creates a cooling-off truce");
        // Collective sanctions: the assembly's embargo is every member's own embargo switch
        // thrown together by a vote - Article 16 as a motion rather than as a script.
        var council = InternationalOrganizations.Seed(c, "Sanctions council", "cooperation", new[] { 0, 1, 2 });
        var againstMember = new OrganizationCommand { IssuerId = 0, OrganizationId = council.Id, Action = "propose", Kind = "sanction", TargetId = 1 };
        Check(InternationalOrganizations.Refusal(c, againstMember).Length > 0, "sanctions cannot name a member of the assembly");
        var noEmbargoLift = new OrganizationCommand { IssuerId = 0, OrganizationId = council.Id, Action = "propose", Kind = "lift_sanction", TargetId = 3 };
        Check(InternationalOrganizations.Refusal(c, noEmbargoLift).Length > 0, "lifting needs an embargo actually in force");
        var sanction = new OrganizationCommand { IssuerId = 0, OrganizationId = council.Id, Action = "propose", Kind = "sanction", TargetId = 3 };
        sanction.Apply(c); var sanctionMotion = council.Motion;
        Check(sanctionMotion is { Kind: "sanction" }, "a member can table a collective-sanctions vote");
        new OrganizationCommand { IssuerId = 1, OrganizationId = council.Id, Action = "vote", MotionId = sanctionMotion.Id, Yes = true }.Apply(c);
        DomesticCrisesTests.Day(c, sanctionMotion.EndsDay); InternationalOrganizations.Tick(c);
        Check(council.LastOutcome == "sanction_passed", "two of three members carry a sanctions resolution");
        Check(new[] { 0, 1, 2 }.All(id => CampaignDiplomacy.Peek(c, id, 3)?.EmbargoBy(id) == true),
            "every member's embargo follows the resolution, including the abstaining one");
        var lift = new OrganizationCommand { IssuerId = 2, OrganizationId = council.Id, Action = "propose", Kind = "lift_sanction", TargetId = 3 };
        lift.Apply(c); var liftMotion = council.Motion;
        new OrganizationCommand { IssuerId = 0, OrganizationId = council.Id, Action = "vote", MotionId = liftMotion.Id, Yes = true }.Apply(c);
        new OrganizationCommand { IssuerId = 1, OrganizationId = council.Id, Action = "vote", MotionId = liftMotion.Id, Yes = true }.Apply(c);
        DomesticCrisesTests.Day(c, liftMotion.EndsDay); InternationalOrganizations.Tick(c);
        Check(council.LastOutcome == "lift_sanction_passed" && new[] { 0, 1, 2 }.All(id => CampaignDiplomacy.Peek(c, id, 3)?.EmbargoBy(id) == false),
            "the same assembly can lift what it imposed");
        c.FeatureOverrides["organizations"] = false;
        var before = CampaignFingerprint.Of(c); InternationalOrganizations.Tick(c);
        new OrganizationCommand { IssuerId = 3, Action = "create", Name = "Disabled", CharterId = "cooperation" }.Apply(c);
        Check(before == CampaignFingerprint.Of(c) && !InternationalOrganizations.DefencePartners(c,0).Any(), "feature switch freezes actions and obligations");
        Check(NetworkCommandCodec.TryEncode(invite, out var kind, out var json) && NetworkCommandCodec.TryDecode(kind, json, out var decoded) && decoded is OrganizationCommand,
            "assembly commands use the real whitelisted network codec");
    }
}
