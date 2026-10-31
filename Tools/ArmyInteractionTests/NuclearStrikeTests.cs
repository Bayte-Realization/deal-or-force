using System;
using System.Linq;
using DealOrForce.Simulation;

static class NuclearStrikeTests
{
    static int failures;
    static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "nuclear strike: " + label);
        if (!ok) failures++;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);

    /// <summary>The last resort: only at DEFCON 1, only on the holder's own soil, and at its full price.</summary>
    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(947986505);
        c.AttachProvinceTopology(map);
        c.Defines.AiNuclearPosture = false;
        c.Defines.NuclearIncidentChance = 0f;
        c.Defines.NuclearFirstUseNotoriety = 40f;
        c.Wars.Clear(); c.MarkWarsChanged();
        foreach (var r in c.Relations.Values) { r.TensionStage = (int)TensionStage.Calm; r.CrisisOpenedDay = -1; }
        var pk = c.NationByTag("PAK"); var ind = c.NationByTag("IND"); var fr = c.NationByTag("FRA");
        pk.IsHuman = false; ind.IsHuman = false;
        SetDay(c, 300);
        pk.PoliticalPoints = 100f;
        var war = new StrategicWar { Id = 91002, AttackerId = ind.Id, DefenderId = pk.Id, StartedDay = 200 };
        c.Wars.Add(war); c.MarkWarsChanged();

        Check(NuclearPosture.StrikeTarget(c, pk, out _) == 0, "with no enemy on its soil there is nothing to strike");
        // An Indian army standing in a Pakistani province.
        var topology = c.ProvinceMap;
        var ground = Enumerable.Range(1, topology.ProvinceCount).First(p => topology.ProvinceToNation[p] == pk.MapIndex);
        var invader = c.Armies.First(a => a.NationId == ind.Id && a.EmbarkedFleetId < 0 && a.Divisions >= 1f);
        invader.ProvinceId = ground;
        var men = invader.Personnel;
        Check(NuclearPosture.StrikeTarget(c, pk, out _) == ground, "the strike falls where the enemy stands on our ground");
        Check(NuclearPosture.Refusal(c, pk, "strike").Length > 0, "and only forces at DEFCON 1 can be given the order");

        pk.NuclearPosture = 1;
        ind.WarSupport = 0.8f;
        var opinion = CampaignDiplomacy.Get(c, fr.Id, pk.Id).Opinion;
        c.Commands.Enqueue(new NuclearPostureCommand { IssuerId = pk.Id, Action = "strike" });
        c.Commands.ApplyAll(c);
        Check(pk.LastNuclearStrikeDay == 300 && invader.Personnel < men * 0.5f && invader.Organisation == 0f,
            "the struck force loses most of its men and all its order");
        Check(ind.WarSupport < 0.8f - c.Defines.NuclearStrikeWarSupport + 0.01f, "and its country the stomach for the war");
        Check(c.DefconLevel == 1 && c.Chronicle.Entries.Any(e => e.Kind == "nuclear_strike" && e.ActorId == pk.Id),
            "the world goes to DEFCON 1 and hears of it");
        Check(CampaignDiplomacy.Get(c, fr.Id, pk.Id).Opinion < opinion - 1f, "and every government remembers it");
        Check(NuclearPosture.Refusal(c, pk, "strike").Length > 0, "the next order waits a year");
        Check(pk.NuclearFirstUseDay == 300 && pk.Notoriety >= 40f, "the first to use one is the outlaw");

        // The second strike: India's forces were struck, so India goes to launch on warning and may
        // answer on Pakistan's strongest force wherever it stands - not only on Indian soil.
        Check(ind.NuclearPosture == 1 && NuclearPosture.Avenging(c, ind) == pk, "the struck holder goes to DEFCON 1 with the right to answer");
        var answer = NuclearPosture.StrikeTarget(c, ind, out _);
        Check(answer > 0 && c.Armies.Any(a => a.ProvinceId == answer && a.NationId == pk.Id), "the answer falls on the striker's own force");
        ind.PoliticalPoints = 100f;
        var notoriety = ind.Notoriety;
        var opinionOfIndia = CampaignDiplomacy.Get(c, fr.Id, ind.Id).Opinion;
        c.Commands.Enqueue(new NuclearPostureCommand { IssuerId = ind.Id, Action = "strike" });
        c.Commands.ApplyAll(c);
        Check(c.Chronicle.Entries.Any(e => e.Kind == "nuclear_retaliation" && e.ActorId == ind.Id), "and the world hears it answered");
        Check(ind.Notoriety == notoriety && ind.NuclearFirstUseDay < 0, "an answer earns no outlaw's name");
        var condemned = opinionOfIndia - CampaignDiplomacy.Get(c, fr.Id, ind.Id).Opinion;
        Check(condemned > 1f && condemned < 60f, "the world condemns an answer less than a first use");
        Check(NuclearPosture.Avenging(c, pk) == ind && NuclearPosture.Refusal(c, pk, "strike").Length > 0,
            "and the first user, struck back, still waits its year: the exchange ends there");
        SetDay(c, 300 + c.Defines.NuclearRetaliationDays + 1);
        Check(NuclearPosture.Avenging(c, ind) is null && NuclearPosture.Avenging(c, pk) is null, "the right to answer lapses");
        SetDay(c, 300);
        war.Ended = true; c.Wars.Remove(war); c.MarkWarsChanged();
        SetDay(c, 400);
        Check(DefconSystem.EvaluateTarget(c, out var why) <= 2 && why.Length > 0, "and the world stays at the brink for a year");
        SetDay(c, 300 + c.Defines.NuclearShadowDays + 1);
        pk.NuclearPosture = 5; ind.NuclearPosture = 5;
        Check(DefconSystem.EvaluateTarget(c, out _) > 2, "until the shadow passes");
        if (failures > 0) throw new Exception($"{failures} nuclear strike checks failed");
    }
}
