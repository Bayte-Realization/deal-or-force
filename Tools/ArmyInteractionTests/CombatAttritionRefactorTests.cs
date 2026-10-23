using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

// Keep the pre-refactor arithmetic as an independent oracle. Deliberately do not
// share its helpers with production: that would let both implementations drift.
static class CombatAttritionRefactorTests
{
    delegate float DamageCall(Campaign c, IReadOnlyCollection<Army> side, float loss, float scale, out float killed);
    delegate (float Men, float Dead) TotalsCall(Campaign c, IReadOnlyCollection<Army> side, float loss, float scale);
    static readonly DamageCall Damage = typeof(Warfare).GetMethod("ApplyDamage", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<DamageCall>();
    static readonly TotalsCall Totals = typeof(Warfare).GetMethod("CasualtyTotals", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<TotalsCall>();
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL combat attrition: " + label); Console.WriteLine("PASS combat attrition: " + label); }
    static bool Bits(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    public static void Run()
    {
        foreach (var size in new[] { 0, 1, 7, 64 })
        foreach (var rounds in new[] { 1, 8 })
        {
            var c = new Campaign(20261016); var reference = Side(size); var actual = Side(size);
            var random = c.Random.State;
            var equal = true;
            for (var round = 0; round < rounds; round++)
            {
                var loss = round % 2 == 0 ? .03125f : .33f; var scale = .7f + round * .15f;
                var expectedTotals = LegacyTotals(c, reference, loss, scale);
                var actualTotals = Totals(c, actual, loss, scale);
                equal &= Bits(expectedTotals.Men, actualTotals.Men) && Bits(expectedTotals.Dead, actualTotals.Dead);
                var expectedLoss = LegacyDamage(c, reference, loss, scale, out var expectedKilled);
                var actualLoss = Damage(c, actual, loss, scale, out var actualKilled);
                equal &= Bits(expectedLoss, actualLoss) && Bits(expectedKilled, actualKilled);
                equal &= JsonSerializer.Serialize(reference, Json) == JsonSerializer.Serialize(actual, Json);
            }
            Check(equal && random == c.Random.State, $"{size} formations / {rounds} rounds preserve every field, float bit and RNG state");
        }

        var duplicateCampaign = new Campaign(99);
        var duplicateReference = Side(2); var duplicateActual = Side(2);
        duplicateReference.Add(duplicateReference[0]); duplicateActual.Add(duplicateActual[0]);
        var expected = LegacyDamage(duplicateCampaign, duplicateReference, .2f, 1, out var expectedDead);
        var observed = Damage(duplicateCampaign, duplicateActual, .2f, 1, out var observedDead);
        Check(Bits(expected, observed) && Bits(expectedDead, observedDead) &&
            JsonSerializer.Serialize(duplicateReference, Json) == JsonSerializer.Serialize(duplicateActual, Json),
            "duplicate legacy formation references preserve pre-damage exposure semantics");

        var large = Side(32); var small = Side(1);
        Damage(duplicateCampaign, large, .1f, 1, out _);
        var smallReference = Side(1);
        expected = LegacyDamage(duplicateCampaign, smallReference, .1f, 1, out expectedDead);
        observed = Damage(duplicateCampaign, small, .1f, 1, out observedDead);
        Check(Bits(expected, observed) && Bits(expectedDead, observedDead) &&
            JsonSerializer.Serialize(smallReference, Json) == JsonSerializer.Serialize(small, Json),
            "reused exposure buffer cannot leak a preceding side into a smaller one");
        Benchmark();
    }

    static void Benchmark()
    {
        const int iterations = 2000;
        var c = new Campaign(7); var reference = Side(64); var actual = Side(64);
        for (var i = 0; i < 128; i++) { LegacyDamage(c, reference, .01f, 1, out _); Damage(c, actual, .01f, 1, out _); }
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) LegacyDamage(c, reference, .01f, 1, out _);
        var legacyTime = timer.Elapsed.TotalMilliseconds; var legacyBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        for (var i = 0; i < iterations; i++) Damage(c, actual, .01f, 1, out _);
        Console.WriteLine($"ATTRITION_BENCH formations=64 iterations={iterations} legacy_ms={legacyTime:F2} current_ms={timer.Elapsed.TotalMilliseconds:F2} legacy_bytes={legacyBytes} current_bytes={GC.GetAllocatedBytesForCurrentThread() - allocated}");
    }

    static List<Army> Side(int count) => Enumerable.Range(0, count).Select(i => new Army
    {
        Id = i, TemplateId = i % 3 == 0 ? "militia" : "infantry", Divisions = i % 7 == 0 ? .01f : .25f + i % 5,
        Organisation = .05f + i % 8 * .12f, Strength = .1f + i % 9 * .1f,
        SupplyLevel = .2f + i % 5 * .16f, Equipment = .3f + i % 6 * .1f,
        Cohesion = .3f + i % 4 * .16f, Experience = i % 5 * .13f,
        Entrenchment = .6f, Personnel = 15_000, Recovering = i * 3,
        Stance = (ArmyStance)(i % 3), CutOff = i % 4 == 0
    }).ToList();

    static float LegacyDamage(Campaign c, IReadOnlyCollection<Army> side, float organisationLoss, float casualtyScale, out float killed)
    {
        var exposure = side.Select(a => LegacyCasualties(c, a, side, organisationLoss, casualtyScale)).ToList();
        var d = c.Defines; var totalPower = side.Sum(a => M.Max(.01f, a.Power));
        var lost = 0f; killed = 0f; var woundedShare = M.Clamp01(d.WoundedShareOfCasualties); var index = 0;
        foreach (var a in side)
        {
            var template = UnitTypeCatalog.Get(a.TemplateId);
            var share = M.Max(.01f, a.Power) / totalPower * side.Count;
            var loss = organisationLoss * share / M.Max(.2f, template.Organisation);
            var (personnel, casualties) = exposure[index++];
            a.Organisation = M.Clamp01(a.Organisation - loss);
            a.Entrenchment = M.Clamp01(a.Entrenchment - d.EntrenchmentLostPerCombatDay + d.EntrenchmentPerDay * d.EntrenchmentUnderFireShare);
            if (personnel > 1f) a.Strength = M.Clamp01(a.Strength - casualties / personnel);
            a.Personnel = Math.Max(0, a.Personnel - (int)MathF.Round(casualties));
            a.Recovering += casualties * d.WoundedReturningShare;
            a.Experience = M.Clamp01(a.Experience + loss * .004f);
            a.Cohesion = M.Clamp01(a.Cohesion - loss * .08f);
            lost += casualties; killed += casualties * (1f - woundedShare);
        }
        return lost;
    }

    static (float Men, float Dead) LegacyTotals(Campaign c, IReadOnlyCollection<Army> side, float loss, float scale)
    {
        float men = 0, dead = 0;
        foreach (var a in side) { var v = LegacyCasualties(c, a, side, loss, scale); men += v.Personnel; dead += v.Casualties; }
        return (men, dead);
    }

    static (float Personnel, float Casualties) LegacyCasualties(Campaign c, Army a, IReadOnlyCollection<Army> side, float organisationLoss, float casualtyScale)
    {
        var d = c.Defines; var totalPower = side.Sum(other => M.Max(.01f, other.Power));
        var template = UnitTypeCatalog.Get(a.TemplateId);
        var share = M.Max(.01f, a.Power) / totalPower * side.Count;
        var loss = organisationLoss * share / M.Max(.2f, template.Organisation);
        var broken = M.Lerp(d.BrokenCasualtyMultiplier, 1f, M.Clamp01(a.Organisation / M.Max(.05f, d.BattleBreakOrganisation * 4f)));
        var personnel = a.Divisions * MilitaryBootstrap.DivisionSize * a.Strength;
        return (personnel, personnel * loss * d.CasualtiesPerOrganisationPoint * broken * casualtyScale *
            MilitaryDoctrine.CasualtyExposure(a.Stance) * (a.CutOff ? d.EncircledCasualtyMultiplier : 1f));
    }
}
