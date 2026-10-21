using System;
using System.Linq;
using DealOrForce.Simulation;

static class WarAimResolveTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("war aim resolve: " + note); Console.WriteLine("PASS war aim resolve: " + note); }

    public static void Run()
    {
        var c = new Campaign(20261018) { ScenarioId = "war_aim_resolve" };
        var a = new Nation { Id = 0, MapIndex = 501, Tag = "PRK", Stability = .8f, WarSupport = .9f, Population = 3000000 };
        var b = new Nation { Id = 1, MapIndex = 502, Tag = "KOR", Population = 3000000 };
        c.Nations.AddRange(new[] {a,b});
        const int width = 12, height = 3;
        var cells = new ushort[width * height]; var owners = new ushort[cells.Length];
        for (var i = 0; i < 4; i++)
        {
            var region = new ScenarioState { MapIndex = 3001 + i, Id = "aim_" + i,
                NationMapIndex = i == 0 ? 501 : 502, Population = 1000000 };
            region.PopulationGroups.Add(new PopulationGroup { Culture = "korean", Size = region.Population });
            c.States.Add(region);
        }
        for (var x = 1; x <= 4; x++)
        { cells[width + x] = (ushort)(3000 + x); owners[width + x] = (ushort)(x == 1 ? 501 : 502); }
        var map = ProvinceTopology.Build(cells, owners, width, height, _ => false);
        c.AttachProvinceTopology(map);
        int P(int region) => Enumerable.Range(1,map.ProvinceCount).First(p => map.ProvinceToState[p] == region);
        var post = P(3002); var capital = P(3003);
        c.SetProvinceController(post,501); c.ProvinceOccupation[post] = 1;
        var war = new StrategicWar { Id = 11, AttackerId = 0, DefenderId = 1,
            WarScore = 60, ClaimedStateMapIndex = 3003, Justification = "irredentism", StartedDay = 0 };
        war.AttackerFront.Add(post); c.Wars.Add(war);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 200L*24);
        var unit = new Army { Id = 1, NationId = 0, AssignedWarId = war.Id, ProvinceId = post,
            OrderKind = ArmyOrder.AdvanceFront, Strength = 1, Organisation = 1, SupplyLevel = 1,
            Divisions = 4, Planning = 1, PlanActive = true };
        c.Armies.Add(unit);
        var draft = new PeaceDemand { WarId = war.Id, ProposerId = a.Id }; draft.Provinces.Add(post);
        var before = CampaignFingerprint.Of(c);
        Check(AiStatecraft.ShouldPressWarAim(c,a,war,draft),
            "a healthy winning army beside the unfinished capital does not cash out for an incidental border region");
        Check(CampaignFingerprint.Of(c) == before, "reading pursuit intent changes no campaign state");
        var refusal = PeaceNegotiation.Acceptance(c,war,b.Id,PeaceTerms.WhitePeace);
        c.Defines.AiPursueUnfinishedWarAims = false;
        Check(!AiStatecraft.ShouldPressWarAim(c,a,war,draft) &&
            PeaceNegotiation.Acceptance(c,war,b.Id,PeaceTerms.WhitePeace) > refusal,
            "the same intent affects compromise acceptance and is moddable");
        c.Defines.AiPursueUnfinishedWarAims = true;
        draft.Provinces.Add(capital);
        Check(!AiStatecraft.ShouldPressWarAim(c,a,war,draft), "a draft delivering the nearby objective may be signed");
        draft.Provinces.Remove(capital); draft.Subjugate = "protectorate";
        Check(!AiStatecraft.ShouldPressWarAim(c,a,war,draft), "an intact dependent settlement is a real alternative to annexation");
        draft.Subjugate = ""; unit.SupplyLevel = .1f;
        Check(!AiStatecraft.ShouldPressWarAim(c,a,war,draft), "an unsupplied army is not an achievable victory");
        unit.SupplyLevel = 1; a.Stability = .01f;
        Check(!AiStatecraft.ShouldPressWarAim(c,a,war,draft), "domestic collapse can justify a partial peace");
        a.Stability = .8f; war.Stalled = true;
        Check(!AiStatecraft.ShouldPressWarAim(c,a,war,draft), "an established stalemate is not a promise of imminent victory");
        war.Stalled = false;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)(c.Defines.AiWearyDays+1)*24);
        Check(!AiStatecraft.ShouldPressWarAim(c,a,war,draft), "war weariness prevents an endless pursuit veto");
        Check(PeaceNegotiation.Acceptance(c,war,b.Id,PeaceTerms.Capitulate) == 100,
            "unconditional surrender is never blocked by territorial resolve");
        war.ClaimedStateMapIndex = 3002;
        Check(AiStatecraft.StrategicWarAim(c,a,war,c.States.Single(r=>r.MapIndex==3003)) &&
            PeaceShape.AimOf(c,war,a) == PeaceShape.Aim.People,
            "irredentist wars use their stated demographic aim rather than a random treaty personality");
        war.Justification = "";
        Check(AiStatecraft.StrategicWarAim(c,a,war,c.States.Single(r=>r.MapIndex==3003)),
            "an authored national consolidation interest survives an unrelated coalition justification");
        b.Tag = "TST";
        Check(!AiStatecraft.StrategicWarAim(c,a,war,c.States.Single(r=>r.MapIndex==3003)),
            "shared culture alone does not invent total-conquest aims for unrelated wars");
    }
}

