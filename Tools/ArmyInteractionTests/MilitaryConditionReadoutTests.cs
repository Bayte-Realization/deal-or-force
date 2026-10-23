using System;
using DealOrForce.Simulation;

static class MilitaryConditionReadoutTests
{
    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL military condition readout: " + name);
        Console.WriteLine("PASS: military condition readout " + name);
    }
    static bool Near(float a, float b) => Math.Abs(a - b) < .00001f;

    public static void Run()
    {
        var c = PartialFrontWithdrawalTests.Fixture();
        c.ConfigureSinglePlayer(0);
        c.Defines.BorderVisionDepth = 0;
        c.Defines.ArmyVisionDepth = 0;
        c.Battles.Clear();
        var own = c.Armies[0];
        var enemy = c.Armies[1];
        own.BattleId = enemy.BattleId = -1;
        enemy.Divisions = 1;
        enemy.Organisation = .8f;
        enemy.Equipment = .2f;
        CampaignIntel.Invalidate(c);
        Check(!MilitaryConditionReadout.CanRead(c, 0, enemy), "hidden enemy readiness remains unknown");
        Check(!MilitaryConditionReadout.ForPlayer(c, new[] { enemy }).Known,
            "hidden roster has no fabricated percentages");
        Check(MilitaryConditionReadout.CanRead(c, 0, own), "own readiness is available");

        own.ProvinceId = enemy.ProvinceId;
        CampaignIntel.Invalidate(c);
        var before = CampaignFingerprint.Of(c);
        var random = c.Random.State;
        var observed = MilitaryConditionReadout.ForPlayer(c, new[] { enemy });
        Check(observed.Known && Near(observed.Organisation, .8f) && Near(observed.Equipment, .2f),
            "observed enemy exposes actual organisation and equipment");
        Check(MilitaryConditionReadout.CanRead(c, 1, own), "AI contact rules match player contact rules");
        Check(!CampaignIntel.PlayerCanInspect(c, enemy.NationId) && !ArmyInteraction.CanSelect(c, enemy),
            "observing readiness does not grant foreign dossier or command authority");
        for (var i = 0; i < 10; i++) MilitaryConditionReadout.ForPlayer(c, new[] { enemy });
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == random,
            "repeated display queries preserve fingerprint and random state");

        var second = new Army { Id = 9001, NationId = 1, ProvinceId = enemy.ProvinceId,
            Divisions = 3, Organisation = .2f, Equipment = .6f };
        var hidden = new Army { Id = 9002, NationId = 1, ProvinceId = 4,
            Divisions = 100, Organisation = 1, Equipment = 1 };
        c.Armies.Add(second);
        c.Armies.Add(hidden);
        CampaignIntel.Invalidate(c);
        var mixed = MilitaryConditionReadout.ForPlayer(c, new[] { enemy, second, hidden });
        Check(mixed.Known && mixed.Formations == 2 && Near(mixed.Divisions, 4),
            "unseen reinforcements excluded from displayed counts");
        Check(Near(mixed.Organisation, .35f) && Near(mixed.Equipment, .5f),
            "stack readiness weighted by actual represented divisions");
        hidden.Organisation = hidden.Equipment = 0;
        var changedHidden = MilitaryConditionReadout.ForPlayer(c, new[] { enemy, second, hidden });
        Check(mixed == changedHidden, "hidden damage cannot change visible stack readout");

        enemy.Equipment = .9f;
        var resupplied = MilitaryConditionReadout.ForPlayer(c, new[] { enemy });
        Check(Near(resupplied.Equipment, .9f) && Near(resupplied.Organisation, observed.Organisation),
            "equipment-only change is reflected without changing organisation");
        own.ProvinceId = 2;
        CampaignIntel.Invalidate(c);
        Check(!MilitaryConditionReadout.ForPlayer(c, new[] { enemy }).Known,
            "losing contact removes readiness rather than tracking secretly");

        var battle = new LandBattle { Id = 9003, AttackerId = 0, DefenderId = 1, ProvinceId = 3 };
        battle.Attackers.Add(own.Id);
        battle.Defenders.Add(enemy.Id);
        c.Battles.Add(battle);
        CampaignIntel.Invalidate(c);
        Check(MilitaryConditionReadout.ForPlayer(c, new[] { enemy }).Known,
            "own battle contact reveals opposing readiness");
        battle.Ended = true;
        CampaignIntel.Invalidate(c);
        Check(!MilitaryConditionReadout.ForPlayer(c, new[] { enemy }).Known,
            "ended battle does not reveal recovered troops forever");
        Check(MilitaryConditionReadout.ForPlayer(c, new[] { hidden }, true).Known,
            "explicit fog-off presentation can show remote readiness");
        c.Player.IsHuman = false;
        Check(MilitaryConditionReadout.ForPlayer(c, new[] { hidden }).Known,
            "observer presentation can show world readiness");

        // The province card and the region dossier ask one question: how much fight is left
        // in the foreign formations standing here? Own divisions answer for themselves.
        var province = PartialFrontWithdrawalTests.Fixture();
        province.ConfigureSinglePlayer(0);
        province.Defines.BorderVisionDepth = 0;
        province.Defines.ArmyVisionDepth = 0;
        province.Battles.Clear();
        var mine = province.Armies[0];
        var theirs = province.Armies[1];
        mine.BattleId = theirs.BattleId = -1;
        mine.ProvinceId = theirs.ProvinceId = 6;
        theirs.Divisions = 2;
        theirs.Organisation = .7f;
        theirs.Equipment = .4f;
        CampaignIntel.Invalidate(province);
        var observedProvince = MilitaryConditionReadout.ForPlayerInProvince(province, 6, foreignOnly: true);
        Check(observedProvince.Known && observedProvince.Formations == 1 &&
            Near(observedProvince.Organisation, .7f) && Near(observedProvince.Equipment, .4f),
            "province readout reports the observed foreign formation");
        var wholeProvince = MilitaryConditionReadout.ForPlayerInProvince(province, 6);
        Check(wholeProvince.Formations == 2 && wholeProvince.Divisions > observedProvince.Divisions,
            "province readout can include friendly formations when asked to");
        mine.ProvinceId = 2;
        CampaignIntel.Invalidate(province);
        Check(!MilitaryConditionReadout.ForPlayerInProvince(province, 2, foreignOnly: true).Known,
            "a province holding only friendly formations has no foreign readiness");
        Check(!MilitaryConditionReadout.ForPlayerInProvince(province, 1, foreignOnly: true).Known,
            "a province with no observed formation reports nothing");
        Check(!MilitaryConditionReadout.ForPlayerInProvince(null, 6).Known &&
            !MilitaryConditionReadout.ForPlayerInProvince(province, 0).Known,
            "missing campaign or province reports no readiness");

        var malformed = new Army { NationId = 0, Divisions = 1,
            Organisation = float.NaN, Equipment = float.PositiveInfinity };
        var sanitized = MilitaryConditionReadout.ForPlayer(c, new[] { malformed });
        Check(sanitized.Known && sanitized.Organisation == 0 && sanitized.Equipment == 0,
            "malformed saved conditions display finite bounded values");
        malformed.Divisions = float.NaN;
        Check(!MilitaryConditionReadout.ForPlayer(c, new[] { malformed, null }).Known,
            "malformed or missing formations cannot pollute the mean");
        malformed.Divisions = float.MaxValue;
        malformed.Organisation = .7f;
        malformed.Equipment = .4f;
        var oversized = MilitaryConditionReadout.ForPlayer(c, new[] { malformed, malformed });
        Check(float.IsFinite(oversized.Divisions) && Near(oversized.Organisation, .7f) &&
            Near(oversized.Equipment, .4f), "oversized edited stacks cannot overflow displayed condition");
        Check(!MilitaryConditionReadout.ForPlayer(null, new[] { enemy }).Known &&
            !MilitaryConditionReadout.ForPlayer(c, null).Known &&
            !MilitaryConditionReadout.CanRead(c, -1, enemy), "missing context has no foreign visibility");
    }
}
