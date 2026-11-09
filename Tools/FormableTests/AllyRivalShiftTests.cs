using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// An ally or guarantor becomes a rival only from a fact already on the pair.
/// High opinion, an unbroken promise, and the cooldown each refuse. A human gets a letter.
/// </summary>
static class AllyRivalShiftTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("ally rival: " + note); Console.WriteLine("PASS ally rival: " + note); }

    public static void Run()
    {
        var compiled = new Defines();
        Check(compiled.AllyRivalShift && compiled.AllyRivalShiftCooldownDays == 180 && compiled.AllyRivalShiftPaperDays == 30,
            "the compiled cooldown is 180 days and the letter lasts 30");
        Check(OpinionCatalog.Find("withdrew_guarantee") is { Opinion: < 0 },
            "a withdrawn guarantee is a reason the relation tooltip can name");
        Loc.Language = "english";
        Check(Loc.Get("chronicle.alliance_rival_guarantee") ==
              "{0} now counts {1} a rival because a guarantee was broken." &&
              Loc.Get("paper.ally_rival_opinion").Contains("no land"),
            "the English sentence names the reason and says no land changes hands");
        Loc.Language = "russian";
        Check(Loc.Get("chronicle.alliance_rival_guarantee").Contains("{0}") &&
              Loc.Get("chronicle.alliance_rival_guarantee").Contains("соперником") &&
              Loc.Get("paper.ally_rival_guarantee").Contains("земля не переходит"),
            "the Russian sentence names the rivalry and says land is not taken");
        Loc.Language = "english";

        QuietRefuses();
        OpinionThenCooldown();
        BrokenGuarantee();
        RepairedGuaranteeRefuses();
        FriendWarAndStranger();
        StolenLandDespiteHighOpinion();
        HumanLetter();
        BothHumansObserverWarAndSwitch();
        FingerprintAndSave();
    }

    static void QuietRefuses()
    {
        var campaign = World(1);
        Check(campaign.Defines.AllyRivalShiftCooldownDays == 180 && campaign.Defines.AllyRivalShiftPaperDays == 30 &&
              campaign.Defines.AllianceReviewDays == 7,
            "shipped defines keep the 180-day cooldown, the 30-day letter, and the weekly review");
        var relation = Ally(campaign, 0, 1, 80f);
        var before = CampaignFingerprint.Of(campaign);
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Allied && campaign.Chronicle.Entries.Count == 0 &&
              relation.RivalShiftPaperBy < 0,
            "high opinion and no broken promise do not end the alliance");
        Check(CampaignFingerprint.Of(campaign) == before && AllyRivalShift.Line(campaign, relation).Length == 0,
            "reading a quiet pair does not change the fingerprint");
        Untouched(campaign);
    }

    static void OpinionThenCooldown()
    {
        var campaign = World(2);
        var relation = Ally(campaign, 0, 1, campaign.Defines.AiRivalryThreshold - 1f);
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Rival &&
              campaign.Chronicle.Entries.Any(entry => entry.Kind == "alliance_rival_opinion"),
            "opinion under the existing floor makes two computers rivals");
        Check(AllyRivalShift.ChronicleSentence(campaign.NationById(0), campaign.NationById(1), AllyRivalShift.Opinion)
                .Contains("partnership floor"),
            "the chronicle sentence names the opinion floor");
        relation.Stance = DiplomaticStance.Allied;
        relation.Opinion = campaign.Defines.AiRivalryThreshold - 1f;
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Allied,
            "the same facts do not shift again during the cooldown");
        var step = Math.Max(1, campaign.Defines.AllianceReviewDays);
        var cool = campaign.Defines.AllyRivalShiftCooldownDays;
        var blocked = cool - 1;
        blocked -= blocked % step;
        Day(campaign, blocked);
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Allied, "the last review inside the cooldown still refuses");
        Day(campaign, blocked + step);
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Rival &&
              campaign.Chronicle.Entries.Count(entry => entry.Kind == "alliance_rival_opinion") == 2,
            "after the cooldown the same low opinion can shift again");
        Untouched(campaign);
    }

    static void BrokenGuarantee()
    {
        var campaign = World(3);
        var relation = Ally(campaign, 0, 1, 80f);
        new GuaranteeCommand { IssuerId = 0, TargetId = 1, Guarantee = true }.Apply(campaign);
        new GuaranteeCommand { IssuerId = 0, TargetId = 1, Guarantee = false }.Apply(campaign);
        relation.Opinion = 80f;
        AllyRivalShift.Tick(campaign);
        Check(relation.GuaranteeBrokenBy == 0 && relation.Stance == DiplomaticStance.Rival &&
              !relation.GuaranteeByA && !relation.GuaranteeByB &&
              campaign.Chronicle.Entries.Any(entry => entry.Kind == "alliance_rival_guarantee" && entry.ActorId == 1),
            "a withdrawn guarantee makes allies rivals even while opinion stays high");
        Untouched(campaign);

        var partner = World(4);
        var bond = CampaignDiplomacy.Get(partner, 0, 1);
        bond.Opinion = 70f;
        new GuaranteeCommand { IssuerId = 0, TargetId = 1, Guarantee = true }.Apply(partner);
        new GuaranteeCommand { IssuerId = 0, TargetId = 1, Guarantee = false }.Apply(partner);
        bond.Opinion = 70f;
        AllyRivalShift.Tick(partner);
        Check(bond.Stance == DiplomaticStance.Rival && bond.Stance != DiplomaticStance.Allied,
            "a guarantor who was never allied still becomes a rival when the guarantee is withdrawn");
        Untouched(partner);
    }

    static void RepairedGuaranteeRefuses()
    {
        var campaign = World(5);
        var relation = CampaignDiplomacy.Get(campaign, 0, 1);
        relation.Opinion = 80f;
        new GuaranteeCommand { IssuerId = 0, TargetId = 1, Guarantee = true }.Apply(campaign);
        new GuaranteeCommand { IssuerId = 0, TargetId = 1, Guarantee = false }.Apply(campaign);
        new GuaranteeCommand { IssuerId = 0, TargetId = 1, Guarantee = true }.Apply(campaign);
        relation.Opinion = 80f;
        AllyRivalShift.Tick(campaign);
        Check(relation.GuaranteeBrokenBy < 0 && relation.Stance != DiplomaticStance.Rival && relation.GuaranteeByA,
            "repairing the guarantee before the review refuses the shift");
        Untouched(campaign);
    }

    static void FriendWarAndStranger()
    {
        var campaign = World(6);
        var relation = Ally(campaign, 0, 1, 50f);
        campaign.Wars.Add(new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 2 });
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Allied && campaign.Chronicle.Entries.Count == 0,
            "a war against someone who is not their friend does not break the alliance");
        Ally(campaign, 1, 2, 40f);
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Rival &&
              campaign.Chronicle.Entries.Any(entry => entry.Kind == "alliance_rival_friend" && entry.ActorId == 1 && entry.TargetId == 0),
            "a war against the ally's friend makes the pair rivals");
        Check(CampaignDiplomacy.Peek(campaign, 1, 2).Stance == DiplomaticStance.Allied &&
              campaign.Wars.Count == 1 && !campaign.Wars[0].Ended,
            "the friend's own alliance and the war itself stay");
        Untouched(campaign);

        var quiet = World(7);
        Ally(quiet, 0, 1, 50f);
        Ally(quiet, 1, 2, 40f);
        var before = CampaignFingerprint.Of(quiet);
        AllyRivalShift.Tick(campaign);
        AllyRivalShift.Tick(quiet);
        Check(CampaignDiplomacy.Peek(quiet, 0, 1).Stance == DiplomaticStance.Allied &&
              CampaignFingerprint.Of(quiet) == before,
            "a second campaign does not inherit the first campaign's rivalry");
        Untouched(quiet);
    }

    static void StolenLandDespiteHighOpinion()
    {
        var campaign = World(8);
        var relation = Ally(campaign, 0, 1, 80f);
        OpinionSystem.Remember(campaign, relation, "took_our_land");
        relation.Opinion = 80f;
        Check(relation.Opinion > campaign.Defines.AiRivalryThreshold, "the land fact is being judged while opinion is still high");
        AllyRivalShift.Tick(campaign);
        Check(relation.Stance == DiplomaticStance.Rival &&
              campaign.Chronicle.Entries.Any(entry => entry.Kind == "alliance_rival_land"),
            "held lost land makes allies rivals even though opinion was above the floor");
        Untouched(campaign);
    }

    static void HumanLetter()
    {
        var decline = World(9);
        decline.Nations[1].IsHuman = true;
        var relation = Ally(decline, 0, 1, decline.Defines.AiRivalryThreshold - 5f);
        AllyRivalShift.Tick(decline);
        Check(relation.Stance == DiplomaticStance.Allied && AllyRivalShift.Pending(decline, relation) &&
              relation.RivalShiftPaperBy == 0 && decline.Chronicle.Entries.Count == 0,
            "a human alliance stays until they answer, and the letter names the computer");
        Check(AllyRivalShift.Line(decline, relation).Contains("no land"),
            "the letter is the sentence the player reads");
        var refused = new AnswerAllyRivalCommand { IssuerId = 1, TargetId = 0, Accept = false };
        refused.Apply(decline);
        Check(refused.Result == AllyRivalShift.KeptText && relation.Stance == DiplomaticStance.Allied &&
              !AllyRivalShift.Pending(decline, relation),
            "keeping the partnership does not change stance");
        AllyRivalShift.Tick(decline);
        Check(!AllyRivalShift.Pending(decline, relation) && relation.Stance == DiplomaticStance.Allied,
            "the cooldown stops a new letter on the same day");
        Untouched(decline);

        var expire = World(10);
        expire.Nations[1].IsHuman = true;
        var waiting = Ally(expire, 0, 1, expire.Defines.AiRivalryThreshold - 5f);
        AllyRivalShift.Tick(expire);
        Day(expire, expire.Defines.AllyRivalShiftPaperDays);
        AllyRivalShift.Tick(expire);
        Check(waiting.Stance == DiplomaticStance.Allied && !AllyRivalShift.Pending(expire, waiting) &&
              expire.Chronicle.Entries.Count == 0,
            "a letter that lapses does not end the alliance");
        Untouched(expire);

        var accept = World(11);
        accept.Nations[1].IsHuman = true;
        var paper = Ally(accept, 0, 1, accept.Defines.AiRivalryThreshold - 5f);
        AllyRivalShift.Tick(accept);
        var signed = new AnswerAllyRivalCommand { IssuerId = 1, TargetId = 0, Accept = true };
        signed.Apply(accept);
        Check(signed.Result == AllyRivalShift.EndedText && paper.Stance == DiplomaticStance.Rival &&
              accept.Chronicle.Entries.Any(entry => entry.Kind == "alliance_rival_opinion"),
            "the human click is what ends their alliance and writes the chronicle");
        var stranger = new AnswerAllyRivalCommand { IssuerId = 0, TargetId = 1, Accept = true };
        stranger.Apply(accept);
        Check(stranger.Result == "Only the addressed country may answer.",
            "the computer that sent the letter cannot answer it");
        Untouched(accept);
    }

    static void BothHumansObserverWarAndSwitch()
    {
        var both = World(12);
        both.Nations[0].IsHuman = true;
        both.Nations[1].IsHuman = true;
        var relation = Ally(both, 0, 1, both.Defines.AiRivalryThreshold - 10f);
        AllyRivalShift.Tick(both);
        Check(relation.Stance == DiplomaticStance.Allied && relation.RivalShiftPaperBy < 0,
            "two humans are not shifted and are not sent a letter");
        Untouched(both);

        var observer = World(13);
        observer.Nations[1].IsHuman = true;
        observer.ObserverMode = true;
        var watched = Ally(observer, 0, 1, observer.Defines.AiRivalryThreshold - 10f);
        AllyRivalShift.Tick(observer);
        Check(watched.Stance == DiplomaticStance.Allied && !AllyRivalShift.Pending(observer, watched),
            "observer mode does not flip a human alliance and does not file a letter");
        Untouched(observer);

        var war = World(14);
        var fighting = CampaignDiplomacy.Get(war, 0, 1);
        fighting.Stance = DiplomaticStance.AtWar;
        fighting.Opinion = war.Defines.AiRivalryThreshold - 20f;
        AllyRivalShift.Tick(war);
        Check(fighting.Stance == DiplomaticStance.AtWar && war.Chronicle.Entries.Count == 0,
            "a war already under way is not rewritten into rivalry");
        Untouched(war);

        var off = World(15);
        off.Defines.AllyRivalShift = false;
        var held = Ally(off, 0, 1, off.Defines.AiRivalryThreshold - 20f);
        AllyRivalShift.Tick(off);
        Check(held.Stance == DiplomaticStance.Allied, "the define turns the shift off");
        Untouched(off);
    }

    static void FingerprintAndSave()
    {
        var campaign = World(16);
        var relation = Ally(campaign, 0, 1, 80f);
        var before = CampaignFingerprint.Of(campaign);
        relation.RivalShiftDay = 4;
        relation.RivalShiftReason = AllyRivalShift.Opinion;
        relation.RivalShiftPaperBy = 0;
        relation.RivalShiftPaperDay = 4;
        relation.GuaranteeBrokenBy = 1;
        Check(CampaignFingerprint.Of(campaign) != before, "a letter and a broken guarantee join the fingerprint");
        relation.RivalShiftDay = -9999;
        relation.RivalShiftReason = "";
        relation.RivalShiftPaperBy = -1;
        relation.RivalShiftPaperDay = -9999;
        relation.GuaranteeBrokenBy = -1;
        Check(CampaignFingerprint.Of(campaign) == before, "default rivalry fields leave the fingerprint unchanged");

        relation.RivalShiftDay = 9;
        relation.RivalShiftReason = AllyRivalShift.Friend;
        relation.RivalShiftPaperBy = 1;
        relation.RivalShiftPaperDay = -9999;
        relation.GuaranteeBrokenBy = 0;
        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<DiplomaticRelation>(JsonSerializer.Serialize(relation, options), options);
        Check(restored.RivalShiftDay == 9 && restored.RivalShiftReason == AllyRivalShift.Friend &&
              restored.RivalShiftPaperBy == 1 && restored.RivalShiftPaperDay == -9999 && restored.GuaranteeBrokenBy == 0,
            "the rivalry fields survive the save serializer");
        Untouched(campaign);
    }

    static Campaign World(int seed)
    {
        var campaign = new Campaign(61000 + seed);
        campaign.Nations.Add(new Nation { Id = 0, Tag = "AAA", Name = "Alpha", Population = 1_000_000, MapIndex = 1 });
        campaign.Nations.Add(new Nation { Id = 1, Tag = "BBB", Name = "Beta", Population = 1_000_000, MapIndex = 2 });
        campaign.Nations.Add(new Nation { Id = 2, Tag = "CCC", Name = "Gamma", Population = 1_000_000, MapIndex = 3 });
        campaign.Armies.Add(new Army { Id = 1, NationId = 0, ProvinceId = 4, OrderProvinceId = 4, DirectOrder = true });
        return campaign;
    }

    static DiplomaticRelation Ally(Campaign campaign, int first, int second, float opinion)
    {
        var relation = CampaignDiplomacy.Get(campaign, first, second);
        relation.Stance = DiplomaticStance.Allied;
        relation.Opinion = opinion;
        relation.AllianceSinceDay = 0;
        return relation;
    }

    static void Day(Campaign campaign, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(campaign, (long)day * 24);

    static void Untouched(Campaign campaign)
    {
        var army = campaign.Armies[0];
        Check(army.DirectOrder && army.OrderProvinceId == 4 && army.ProvinceId == 4 && army.NationId == 0,
            "a direct army order is not rewritten");
        Check(campaign.ProvinceController is null && campaign.Nations.All(nation => nation.Population == 1_000_000),
            "no land is taken and nobody's population changes");
    }
}
