using System;
using System.Globalization;
using DealOrForce.Modding;
using DealOrForce.Simulation;

static class ModScalarAccessTests
{
    static void Check(bool pass, string label)
    {
        if (!pass) throw new Exception("Mod scalar access: " + label);
        Console.WriteLine("PASS mod scalar access: " + label);
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(555);
        var n = c.NationByTag("POL");
        var before = CampaignFingerprint.Of(c);
        Check(!ModScalarAccess.SetDefine(c.Defines, "ideology_opinion_weight", double.MaxValue),
            "finite double that overflows float is rejected");
        Check(before == CampaignFingerprint.Of(c), "rejected float overflow leaves the campaign and RNG unchanged");
        Check(!ModScalarAccess.SetNationNumber(c, "POL", "population", 9223372036854775808d),
            "two-to-the-63 population is rejected before the integer cast");
        Check(before == CampaignFingerprint.Of(c), "rejected population overflow leaves the campaign and RNG unchanged");
        Check(ModScalarAccess.GetDefine(c.Defines, "advisor_endpoint", 7) == 7,
            "numeric read of a text define returns its fallback without throwing");
        Check(!ModScalarAccess.SetDefine(c.Defines, "advisor_endpoint", 12),
            "numeric setter does not replace text configuration");
        Check(before == CampaignFingerprint.Of(c), "unsupported scalar types leave the campaign unchanged");

        var values = new[] { double.NaN, double.NegativeInfinity, -double.MaxValue, -1e40,
            -(double)float.MaxValue, -9223372036854775808d, -2147483649d, -65536d, -129d,
            -1.5d, -1d, -.5d, -0d, 0d, double.Epsilon, .5d, 1d, 1.5d, 2.5d, 127d, 128d,
            255d, 256d, 32767d, 32768d, 65535d, 65536d, 2147483647d, 2147483648d,
            4294967295d, 4294967296d, Math.BitDecrement(9223372036854775808d),
            9223372036854775808d, Math.BitIncrement(9223372036854775808d),
            Math.BitDecrement(18446744073709551616d), 18446744073709551616d,
            (double)decimal.MaxValue, (double)float.MaxValue, 1e40, double.MaxValue, double.PositiveInfinity };
        var types = new[] { typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal),
            typeof(string), typeof(char), typeof(DateTime), typeof(object), typeof(DebtCondition), typeof(int?), null };
        var cases = 0;
        foreach (var type in types)
        foreach (var value in values)
        {
            object expected = null;
            var allowed = double.IsFinite(value) && (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal));
            if (allowed)
            {
                try { expected = Convert.ChangeType(value, type, CultureInfo.InvariantCulture); }
                catch (OverflowException) { allowed = false; }
                if (expected is float single && !float.IsFinite(single)) allowed = false;
            }
            var accepted = ModScalarAccess.TryConvertScalar(value, type, out var actual);
            if (accepted != allowed || (accepted ? !Equals(actual, expected) : actual is not null))
                throw new Exception($"Scalar conversion differs: {type}/{value:R}");
            cases++;
        }
        Check(cases == types.Length * values.Length, cases + " scalar boundary/type comparisons match conversion semantics");

        var rejects = new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, -double.MaxValue };
        var fields = new[] { "population", "treasury", "public_debt", "debt_arrears", "payment-arrears", "credit_confidence",
            "gdp", "military_spending", "tax_rate", "stability", "war_support", "manpower" };
        foreach (var field in fields)
        {
            foreach (var value in rejects)
                Check(!ModScalarAccess.SetNationNumber(c, "POL", field, value), field + " rejects non-finite/overflow value " + value);
        }
        Check(before == CampaignFingerprint.Of(c), "all rejected nation writes are atomic and do not consume RNG");
        Check(!ModScalarAccess.SetNationNumber(c, "POL", "population", (double)long.MaxValue),
            "rounded double representation of long.MaxValue is not a valid population");
        var highest = Math.BitDecrement(9223372036854775808d);
        Check(ModScalarAccess.SetNationNumber(c, "POL", "population", highest) && n.Population == checked((long)highest),
            "largest representable double below 2^63 preserves its integer population");
        Check(ModScalarAccess.SetNationNumber(c, "POL", "population", 1234.9) && n.Population == 1234,
            "ordinary population values retain truncation, not round-to-even");
        Check(ModScalarAccess.SetNationNumber(c, "POL", "population", -1e30) && n.Population == 0,
            "negative population retains its zero clamp without integer overflow");
        Check(ModScalarAccess.SetNationNumber(c, "POL", "stability", 2) && n.Stability == 1 &&
              ModScalarAccess.SetNationNumber(c, "POL", "stability", -2) && n.Stability == 0,
            "normal nation ratio setters retain their clamps");
        Check(ModScalarAccess.SetDefine(c.Defines, "order-settle-days", 2.5) && c.Defines.OrderSettleDays == 2 &&
              ModScalarAccess.SetDefine(c.Defines, "ORDER_SETTLE_DAYS", 3.5) && c.Defines.OrderSettleDays == 4,
            "define aliases retain invariant normalization and integer round-to-even");
        Check(ModScalarAccess.SetDefine(c.Defines, "ai_may_attack_player", -1) && c.Defines.AiMayAttackPlayer &&
              ModScalarAccess.SetDefine(c.Defines, "ai_may_attack_player", 0) && !c.Defines.AiMayAttackPlayer,
            "numeric boolean defines retain zero/nonzero semantics");
        Check(ModScalarAccess.GetDefine(c.Defines, "ai_may_attack_player", -1) == 0 &&
              ModScalarAccess.GetDefine(c.Defines, "order_settle_days", -1) == 4,
            "numeric getters return the actual bool and integer values");
        var catalogueStamp = CampaignFingerprint.Of(c);
        var catalogueCases = 0;
        foreach (var property in typeof(Defines).GetProperties())
        {
            if (!property.CanWrite) continue;
            var old = property.GetValue(c.Defines);
            foreach (var value in values)
            {
                var expected = ModScalarAccess.TryConvertScalar(value, property.PropertyType, out var converted);
                var accepted = ModScalarAccess.SetDefine(c.Defines, property.Name, value);
                if (accepted != expected || !Equals(property.GetValue(c.Defines), expected ? converted : old))
                    throw new Exception($"Define setter differs or partially writes: {property.Name}/{value:R}");
                property.SetValue(c.Defines, old);
                catalogueCases++;
            }
        }
        Check(catalogueCases > 0, catalogueCases + " actual define writes match typed values or reject atomically");
        Check(catalogueStamp == CampaignFingerprint.Of(c), "define matrix restores all state and does not consume RNG");
        c.Defines.IdeologyOpinionWeight = float.NaN;
        Check(ModScalarAccess.GetDefine(c.Defines, "ideology_opinion_weight", 19) == 19,
            "already-corrupt numeric define reads return the requested fallback");
        Check(!ModScalarAccess.SetDefine(c.Defines, "max_speed", 4), "computed read-only define is not writable");
        Check(ModScalarAccess.GetDefine(null, "missing", 3) == 3 && !ModScalarAccess.SetDefine(null, "missing", 0) &&
              !ModScalarAccess.SetNationNumber(null, "POL", "treasury", 0), "unbound bridge has safe defaults");
        Check(ModScalarAccess.GetDefine(c.Defines, null, 5) == 5 && !ModScalarAccess.SetDefine(c.Defines, null, 0) &&
              !ModScalarAccess.SetNationNumber(c, "POL", null, 0) && !ModScalarAccess.SetNationNumber(c, "__missing__", "treasury", 0),
            "missing field and nation identifiers are rejected");
    }
}
