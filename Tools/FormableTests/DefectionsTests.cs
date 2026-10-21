using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class DefectionsTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("defections: " + label);
        Console.WriteLine("PASS defections: " + label);
    }

    public static void Run()
    {
        var campaign = new Campaign(20261020) { ScenarioId = "defections_fixture" };
        var parent = new Nation
        {
            Id = 0, MapIndex = 1, Tag = "PAR", Name = "Parent", Population = 1_000_000,
            Gdp = 100, Stability = .7f, WarSupport = .2f, CapitalStateMapIndex = 1
        };
        var rebel = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "REB", Name = "Rising", Population = 200_000,
            Gdp = 20, Stability = .8f, WarSupport = .9f, CapitalStateMapIndex = 2,
            SecededFromId = parent.Id
        };
        campaign.Nations.AddRange(new[] { parent, rebel });
        const int width = 8, height = 3;
        var states = new ushort[width * height];
        var owners = new ushort[states.Length];
        var cell = height * width / 2 + 4;
        states[cell] = 1; owners[cell] = (ushort)parent.MapIndex;
        campaign.States.Add(new ScenarioState
        {
            MapIndex = 1, Id = "region_1", Name = "Region 1",
            NationMapIndex = parent.MapIndex, Population = 1_000_000
        });
        var topology = ProvinceTopology.Build(states, owners, width, height, _ => false);
        campaign.AttachProvinceTopology(topology);
        var province = Enumerable.Range(1, topology.ProvinceCount)
            .First(p => topology.ProvinceToState[p] == 1);

        var opening = new Army
        {
            Id = 1, NationId = parent.Id, ProvinceId = province, Divisions = 1f,
            Strength = .5f, Cohesion = .2f, SupplyLevel = .2f, Personnel = 8_000,
            MaxPersonnel = 15_000, TemplateId = "line_infantry"
        };
        campaign.Armies.Add(opening);
        campaign.Defines.SecessionDefectionShare = 1f;
        var moved = Reintegration.Defect(campaign, parent, rebel, new[] { province });
        Check(moved == 1 && opening.NationId == rebel.Id,
            "the opening garrison changes sides when a secession is founded");
        Check(opening.AssignedWarId == -1 && opening.OrderKind == ArmyOrder.Manual,
            "a defecting formation is detached from its old staff and orders");
        opening.Cohesion = .9f;

        var wartime = new Army
        {
            Id = 2, NationId = parent.Id, ProvinceId = province, Divisions = 1f,
            Strength = .5f, Cohesion = .1f, SupplyLevel = .1f, Personnel = 7_000,
            MaxPersonnel = 15_000, TemplateId = "line_infantry"
        };
        var steady = new Army
        {
            Id = 3, NationId = parent.Id, ProvinceId = province, Divisions = 1f,
            Strength = .95f, Cohesion = .9f, SupplyLevel = .9f, Personnel = 14_000,
            MaxPersonnel = 15_000, TemplateId = "line_infantry"
        };
        campaign.Armies.AddRange(new[] { wartime, steady });
        var war = new StrategicWar
        {
            Id = 1, AttackerId = parent.Id, DefenderId = rebel.Id, CivilConflict = true,
            StartedDay = 0, WarScore = 0
        };
        campaign.Wars.Add(war);
        campaign.ProvinceController[province] = (ushort)rebel.MapIndex;
        campaign.Defines.WartimeDefectionChanceCivil = 1f;
        campaign.Defines.WartimeDefectionChanceInterstate = 0f;
        var defected = Defections.Tick(campaign, 30);
        Check(defected == 1 && wartime.NationId == rebel.Id,
            "a broken formation in a civil war can cross to the rising");
        Check(steady.NationId == parent.Id,
            "a formation that still has cohesion and supply stays loyal");
        Check(wartime.Strength == .5f && wartime.Personnel == 7_000,
            "defection transfers the formation instead of cloning or destroying it");

        campaign.Defines.WartimeDefectionChanceCivil = 0f;
        var none = Defections.Tick(campaign, 60);
        Check(none == 0, "setting the chance to zero disables wartime defection");
    }
}
