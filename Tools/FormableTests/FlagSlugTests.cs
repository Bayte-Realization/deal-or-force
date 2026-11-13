using System;
using System.Globalization;
using DealOrForce.Simulation;

internal static class FlagSlugTests
{
    private static string _sink;

    private static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("flag slug: " + note);
        Console.WriteLine("PASS flag slug: " + note);
    }

    // Original spelling rule, intentionally not calling the optimized helper.
    private static string Reference(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        var lastWasBreak = true;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
                lastWasBreak = false;
            }
            else if (!lastWasBreak)
            {
                buffer[length++] = '_';
                lastWasBreak = true;
            }
        }
        while (length > 0 && buffer[length - 1] == '_') length--;
        return new string(buffer[..length]);
    }

    internal static void Run()
    {
        var examples = new string[] { null, "", " ", "___", " -- / ", "at_peace", "at_war",
            "presidential_republic", "one_party_state", "r2", "RUS", "Middle East",
            "_a", "a_", "a__b", "A_B", " a  b ", "a\tb\nc", "\0a\0b\0",
            "Русская Республика", "été", "ÉTÉ", "İSTANBUL", "ΣΟΣ", "中文_１２３", "١٢٣",
            "a\u0301b", "a😀b", "\ud800x\udfff", "K_ſ_ı", new string('x', 1024) };
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "en-US", "ru-RU", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (var value in examples)
                    if (FlagCatalog.Slug(value) != Reference(value))
                        throw new Exception("flag slug spelling changed in " + name);
                Check(true, "existing punctuation, Unicode and casing rules agree in " + name);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }

        var random = new Random(611091);
        for (var sample = 0; sample < 4096; sample++)
        {
            var text = new char[random.Next(0, 128)];
            for (var i = 0; i < text.Length; i++) text[i] = (char)random.Next(0, 65536);
            var value = new string(text);
            if (FlagCatalog.Slug(value) != Reference(value))
                throw new Exception("flag slug changed random UTF-16 input " + sample);
        }
        Check(true, "4096 deterministic mixed UTF-16 inputs preserve the original spelling");

        // Quiet visual polling repeatedly receives these already-normalized identifiers.
        // A byte budget is deterministic; no machine-dependent timing threshold in tests.
        var canonical = new[] { "at_peace", "presidential_republic", "liberalism", "русский_язык", "中文_１２３" };
        for (var i = 0; i < 500; i++) foreach (var value in canonical) _sink = FlagCatalog.Slug(value);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) foreach (var value in canonical) _sink = FlagCatalog.Slug(value);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(bytes == 0, "5000 canonical identifier reads allocate zero bytes (actual " + bytes + ")");
        foreach (var value in canonical)
            Check(ReferenceEquals(value, FlagCatalog.Slug(value)), "canonical input is reused: " + value);
    }
}
