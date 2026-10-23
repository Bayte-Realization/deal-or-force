using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class DynamicAllianceTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS alliances: " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(501);
        var a = c.NationByTag("GBR"); var b = c.NationByTag("CAN");
        var r = CampaignDiplomacy.Get(c, a.Id, b.Id);
        r.Stance = DiplomaticStance.Allied; r.Opinion = 90;
        r.AllianceSinceDay = 0; r.AllianceAssessmentDay = 0;
        void Day(int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)day * 24);
        Day(7);
        AllianceMaintenance.Tick(c);
        Check(r.Stance == DiplomaticStance.Allied && r.AllianceCohesion > 0.75f, "healthy alliances retain trust");
        var fingerprint = CampaignFingerprint.Of(c);
        r.AllianceCohesion -= 0.1f;
        Check(fingerprint != CampaignFingerprint.Of(c), "trust participates in multiplayer fingerprint");
        r.Opinion = -100; r.EmbargoByA = true; r.AllianceCohesion = 0.1f;
        for (int day = 14; day <= 112; day += 7) { Day(day); AllianceMaintenance.Tick(c); }
        Check(r.Stance == DiplomaticStance.Neutral, "sustained conflict ends an AI alliance after grace period");
        Check(!AllianceSystem.CanPropose(c, a, b, out _), "rupture has a reconciliation cooldown");
        Check(c.Chronicle.Entries.Count > 0, "strain and rupture create visible news");
        r.Stance = DiplomaticStance.Allied; r.AllianceCohesion = 0.2f;
        Check(!AllianceMaintenance.Reaffirm(c, a, b), "sanctioning an ally cannot be covered with a declaration");
        r.EmbargoByA = false; r.Opinion = 80; r.AllianceStrainedDays = 7;
        var cash = a.Treasury;
        Check(AllianceMaintenance.Reaffirm(c, a, b) && r.AllianceCohesion == .2f && a.Treasury < cash,
            "funded promise costs treasury immediately but grants no instant trust");
        Check(r.AllianceStrainedDays == 7, "signing never erases the strain clock");
        Check(!AllianceMaintenance.Reaffirm(c, a, b), "reaffirmation cannot be spammed");
        Day(119); AlliancePrograms.Tick(c);
        Check(r.AllianceCohesion > .2f && r.AllianceProgramPaidWeeks == 2, "weekly fulfilled commitments gradually earn trust");
        var hash = CampaignFingerprint.Of(c); r.AllianceProgramWeeklyCost *= 2;
        Check(hash != CampaignFingerprint.Of(c), "promised payments participate in peer fingerprint");
        r.AllianceProgramWeeklyCost /= 2;
        var restored = JsonSerializer.Deserialize<DiplomaticRelation>(JsonSerializer.Serialize(r,
            new JsonSerializerOptions { IncludeFields = true }), new JsonSerializerOptions { IncludeFields = true });
        Check(restored.AllianceCohesion == r.AllianceCohesion && restored.AllianceReaffirmedDay == r.AllianceReaffirmedDay &&
              restored.AllianceProgram == r.AllianceProgram && restored.AllianceProgramWeeklyCost == r.AllianceProgramWeeklyCost,
            "new alliance state survives JSON save serialization");
        c.Defines.DynamicAlliances = false; var trust = r.AllianceCohesion;
        AllianceMaintenance.Tick(c);
        Day(212); AllianceMaintenance.Tick(c);
        Check(r.AllianceCohesion == trust, "define cleanly disables automatic maintenance");
        var pausedCash = a.Treasury;
        c.Defines.DynamicAlliances = true; Day(312); AlliancePrograms.Tick(c);
        Check(a.Treasury == pausedCash, "resuming does not bill disabled weeks");
        Check(!AlliancePrograms.Cancel(c, b, a), "a partner cannot cancel somebody else's contribution");
        a.Treasury = 0; Day(319); AlliancePrograms.Tick(c);
        Check(!AlliancePrograms.Active(r) && r.AllianceCohesion < trust + .03f,
            "missed payment ends the promise with a bounded trust penalty");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "alliance_program_lapsed"), "a lapsed programme is visible news");

        // The diplomacy screen opens on somebody else. It used to open on the player's own
        // country, where every panel is a comparison with itself and reads as a screen of zeroes.
        var player = c.NationByTag("FRA") ?? c.Nations.First(n => n.Population > 0);
        var partner = CampaignDiplomacy.DefaultPartner(c, player);
        Check(partner is not null && partner.Id != player.Id,
            "diplomacy opens on somebody else, never on the player's own country");
        Check(CampaignDiplomacy.DefaultPartner(c, player).Id == partner.Id,
            "the opening choice is the same every time it is asked");
        Check(CampaignDiplomacy.DefaultPartner(c, null) is null && CampaignDiplomacy.DefaultPartner(null, player) is null,
            "asking about no player answers nothing rather than guessing");
        var neighbours = c.Nations.Where(n => n.Id != player.Id && n.Population > 0 && n.Movement is null &&
            CampaignDiplomacy.SharesLandBorder(c, player.MapIndex, n.MapIndex)).ToList();
        if (neighbours.Count > 0)
        {
            Check(neighbours.Any(n => n.Id == partner.Id),
                "the opening choice is one of the player's own neighbours");
            var worstOpinion = neighbours.Min(n => CampaignDiplomacy.Peek(c, player.Id, n.Id)?.Opinion ?? 0f);
            Check((CampaignDiplomacy.Peek(c, player.Id, partner.Id)?.Opinion ?? 0f) <= worstOpinion + 0.0001f,
                "it opens on the neighbour the player is on worst terms with");
        }
    }
}
