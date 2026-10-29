using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>The two battle orders a division can be given besides its general posture:
/// hold at any price, and attack now rather than when ready. They are postures like the other
/// three - the same arithmetic reads them - and they are the two that cost the most blood.</summary>
static class BattleOrderTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL battle orders: " + label);
        Console.WriteLine("PASS battle orders: " + label);
    }

    public static void Run()
    {
        Multipliers();
        CommandAndSave();
        Vocabulary();
    }

    static void Multipliers()
    {
        // A last stand defends better than anything else, and pays for it in blood.
        Check(MilitaryDoctrine.Defense(ArmyStance.LastStand) > MilitaryDoctrine.Defense(ArmyStance.Cautious),
            "a last stand defends better than a cautious posture");
        Check(MilitaryDoctrine.Entrenchment(ArmyStance.LastStand) > MilitaryDoctrine.Entrenchment(ArmyStance.Cautious),
            "and digs in faster than a cautious posture");
        Check(MilitaryDoctrine.CasualtyExposure(ArmyStance.LastStand) >
              MilitaryDoctrine.CasualtyExposure(ArmyStance.Aggressive),
            "and is the most expensive posture to hold");
        Check(MilitaryDoctrine.Recovery(ArmyStance.LastStand) < MilitaryDoctrine.Recovery(ArmyStance.Balanced),
            "and recovers more slowly, because it is not relieved");
        Check(MilitaryDoctrine.Attack(ArmyStance.LastStand) < MilitaryDoctrine.Attack(ArmyStance.Balanced),
            "a formation holding on is not also attacking well");

        // Attack now is the heaviest attack in he book, and the weakest defence.
        Check(MilitaryDoctrine.Attack(ArmyStance.ForceAttack) > MilitaryDoctrine.Attack(ArmyStance.Aggressive),
            "an ordered attack hits harder than an aggressive posture");
        Check(MilitaryDoctrine.Planning(ArmyStance.ForceAttack) > MilitaryDoctrine.Planning(ArmyStance.Aggressive),
            "and it is the fastest way to spend a plan");
        Check(MilitaryDoctrine.Defense(ArmyStance.ForceAttack) < MilitaryDoctrine.Defense(ArmyStance.Aggressive),
            "at the worst defence of any posture");
        Check(MilitaryDoctrine.CasualtyExposure(ArmyStance.ForceAttack) >
              MilitaryDoctrine.CasualtyExposure(ArmyStance.Balanced),
            "and heavier casualties than standing still");

        // The old three are untouched: a saved game's cautious division is still cautious.
        Check(MathF.Abs(MilitaryDoctrine.Attack(ArmyStance.Cautious) - 0.84f) < 0.0001f &&
              MathF.Abs(MilitaryDoctrine.Attack(ArmyStance.Balanced) - 1f) < 0.0001f &&
              MathF.Abs(MilitaryDoctrine.Attack(ArmyStance.Aggressive) - 1.16f) < 0.0001f,
            "the three original postures keep their numbers");
    }

    static void CommandAndSave()
    {
        var campaign = Campaign.CreateBootstrap(1711);
        var nation = campaign.NationByTag("ROU");
        var army = campaign.Armies.First(candidate => candidate.NationId == nation.Id);
        campaign.ConfigureSinglePlayer(nation.Id);
        var command = new SetArmyStanceCommand { IssuerId = nation.Id, ArmyId = army.Id, Stance = ArmyStance.LastStand };
        campaign.Commands.Enqueue(command);
        campaign.Commands.ApplyAll(campaign);
        Check(army.Stance == ArmyStance.LastStand, "the order reaches the formation");

        // Somebody else's division is not ours to order about.
        var foreign = campaign.Armies.First(candidate => candidate.NationId != nation.Id);
        new SetArmyStanceCommand { IssuerId = nation.Id, ArmyId = foreign.Id, Stance = ArmyStance.ForceAttack }
            .Apply(campaign);
        Check(foreign.Stance != ArmyStance.ForceAttack, "another country's division keeps its posture");

        var options = new JsonSerializerOptions { IncludeFields = true };
        var saved = JsonSerializer.Serialize(army, options);
        var loaded = JsonSerializer.Deserialize<Army>(saved, options);
        Check(loaded is not null && loaded.Stance == ArmyStance.LastStand,
            "a battle order survives a save roundtrip");

        // The order is posture state, so the network fingerprint sees it.
        var print = CampaignFingerprint.Of(campaign);
        army.Stance = ArmyStance.Balanced;
        Check(CampaignFingerprint.Of(campaign) != print, "the fingerprint covers the posture");
    }

    static void Vocabulary()
    {
        var language = Loc.Language;
        try
        {
            Loc.Language = "english";
            Check(ArmyVocabulary.StanceName(ArmyStance.LastStand) == "Last stand" &&
                  ArmyVocabulary.StanceName(ArmyStance.ForceAttack) == "Force attack",
                "both orders have names in English");
            Loc.Language = "russian";
            Check(ArmyVocabulary.StanceName(ArmyStance.LastStand) == "Держаться до конца" &&
                  ArmyVocabulary.StanceName(ArmyStance.ForceAttack) == "Атака немедленно",
                "and in Russian");
        }
        finally { Loc.Language = language; }
    }
}
