using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class PerformanceRegressionTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("performance regression: " + label); Console.WriteLine("PASS performance: " + label); }
    public static void Run()
    {
        Scanner();
        var c = Campaign.CreateBootstrap(61019);
        SaveFormats(c);
        AllianceGraph(c);
        ProgrammeOrder(c);
    }
    sealed class Fragments : MemoryStream
    {
        readonly int _chunk;
        public Fragments(byte[] bytes, int chunk) : base(bytes) { _chunk = chunk; }
        public override int Read(byte[] b, int offset, int count) => base.Read(b, offset, Math.Min(count, _chunk));
    }
    static void Scanner()
    {
        var json = "{\"nested\":{\"StringTable\":[\"decoy\"]},\"ignored\":[[1,2],{\"x\":true}],\"Str\\u0069ngTable\":[\"русский\",\"line\\nquote\\\"\",\"é漢字\"]}";
        var expected = new[] { "русский", "line\nquote\"", "é漢字" };
        foreach (var chunk in new[] { 1, 2, 3, 7, 32768 })
        foreach (var bom in new[] { false, true })
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bom) bytes = new byte[] { 239,187,191 }.Concat(bytes).ToArray();
            using var stream = new Fragments(bytes, chunk);
            Check(SaveStringTableScanner.Read(stream).SequenceEqual(expected), "split UTF8, escaped key and late table " + chunk + " BOM=" + bom);
        }
        foreach (var text in new[] { "{}", "{\"StringTable\":null}", "{\"nested\":{\"StringTable\":[\"decoy\"]}}" })
        { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); Check(SaveStringTableScanner.Read(stream).Length == 0, "legacy/no root table"); }
        var longId = new string('x', 70000) + "漢字";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { StringTable = new[] { longId } }))))
            Check(SaveStringTableScanner.Read(stream).Single() == longId, "individual tokens can cross the initial buffer");
        var large = Encoding.UTF8.GetBytes("{\"ignored\":[" + string.Join(",", Enumerable.Repeat("12345", 200000)) + "],\"StringTable\":[\"last\"]}");
        using (var warm = new MemoryStream(large)) SaveStringTableScanner.Read(warm);
        using (var stream = new MemoryStream(large))
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var table = SaveStringTableScanner.Read(stream);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Check(table.Single() == "last" && allocated < 131072, "scanning a 1.2MB unrelated array stays below 128KiB (" + allocated + ")");
        }
        try
        {
            using var stream = new Fragments(Encoding.UTF8.GetBytes("{\"x\":[1,2"), 2);
            SaveStringTableScanner.Read(stream);
            throw new Exception("truncation was accepted");
        }
        catch (JsonException) { Check(true, "truncated JSON is rejected"); }
    }
    static void SaveFormats(Campaign c)
    {
        var state = c.States.First(s => s.NationMapIndex == c.Player.MapIndex);
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)state.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)c.Player.MapIndex, 512).ToArray(), 32, 16, _ => false));
        c.Armies.Clear();
        c.Defines.SaveTextFormat = true;
        var folder = Path.Combine(Path.GetTempPath(), "dof-stream-tests-" + Guid.NewGuid().ToString("N"));
        SaveSystem.Save(folder, "source", c, "tests");
        var plain = File.ReadAllBytes(Path.Combine(folder, "source.bdfsave"));
        var variants = new System.Collections.Generic.Dictionary<string, byte[]> { ["plain"] = plain,
            ["bom"] = new byte[] {239,187,191}.Concat(plain).ToArray() };
        using (var output = new MemoryStream())
        { using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) zip.Write(plain); variants["gzip"] = output.ToArray(); }
        // Read the actual framing used by the save codec rather than inventing a magic.
        using (var output = new MemoryStream())
        { output.Write(Encoding.ASCII.GetBytes("BDF2")); using (var zip = new BrotliStream(output, CompressionLevel.Fastest, true)) zip.Write(plain); variants["brotli"] = output.ToArray(); }
        // A root table may follow every state/population row in an older file.
        var tree = JsonNode.Parse(plain);
        if (tree["StringTable"] is { } table)
        { tree.AsObject().Remove("StringTable"); tree["StringTable"] = table; variants["late-table"] = Encoding.UTF8.GetBytes(tree.ToJsonString()); }
        ulong? expected = null;
        foreach (var pair in variants)
        {
            File.WriteAllBytes(Path.Combine(folder, pair.Key + ".bdfsave"), pair.Value);
            var loaded = SaveSystem.Load(folder, pair.Key, "tests");
            Check(loaded.Success, "actual Load accepts " + pair.Key + ": " + loaded.Warning);
            var hash = CampaignFingerprint.Of(loaded.Campaign);
            expected ??= hash;
            Check(hash == expected, "same restored world through " + pair.Key);
        }
        File.WriteAllBytes(Path.Combine(folder, "broken.bdfsave"), plain.Take(plain.Length / 2).ToArray());
        Check(!SaveSystem.Load(folder, "broken", "tests").Success, "actual Load refuses truncated payload");
        Check(File.ReadAllBytes(Path.Combine(folder, "source.bdfsave")).SequenceEqual(plain), "source save is never rewritten by loading");
        Console.WriteLine("Private stream fixtures: " + folder);
    }
    static void AllianceGraph(Campaign c)
    {
        c.Relations.Clear(); c.Wars.Clear();
        var a = c.Nations[0]; var b = c.Nations[1]; var third = c.Nations[2];
        var pair = CampaignDiplomacy.Get(c,a.Id,b.Id);
        pair.Stance = DiplomaticStance.Allied; pair.Opinion = 0;
        var side = CampaignDiplomacy.Get(c,a.Id,third.Id); side.Stance = DiplomaticStance.Allied;
        var enemy = CampaignDiplomacy.Get(c,b.Id,third.Id); enemy.Stance = DiplomaticStance.Rival;
        var graph = new AllianceReviewGraph(); graph.Rebuild(c);
        void Same(string reason)
        {
            // Manual fixture mutations must invalidate the same derived diplomacy
            // indexes that production war/coalition commands invalidate.
            c.MarkStrategicChange();
            // Newer integration trees additionally index the live-war roster.
            typeof(Campaign).GetMethod("MarkWarsChanged")?.Invoke(c, null);
            Check(BitConverter.SingleToInt32Bits(AllianceMaintenance.Target(c,pair)) ==
                BitConverter.SingleToInt32Bits(AllianceMaintenance.Target(c,pair,null,graph)), reason);
        }
        Same("graph matches full relation scan for conflicting commitments");
        side.Stance = DiplomaticStance.Neutral; graph.Remove(a.Id,third.Id);
        Same("rupture changes later review in the same pass");
        side.Stance = DiplomaticStance.Allied; enemy.Stance = DiplomaticStance.Neutral; graph.Rebuild(c);
        var war = new StrategicWar { AttackerId = b.Id, DefenderId = third.Id };
        c.Wars.Add(war); graph.Rebuild(c); Same("live opposing wars match");
        war.Ended = true; Same("war ending is read live");
        war.Ended = false; war.DefenderId = a.Id; war.AttackerAllies.Add(third.Id);
        Same("coalition membership is read live");
        war.AttackerAllies.Clear(); war.DefenderId = third.Id; war.AttackerAllies.Add(a.Id);
        Same("fighting together matches");
        for (var i=0;i<1000;i++) c.Wars.Add(new StrategicWar { AttackerId=a.Id,DefenderId=b.Id,Ended=true });
        graph.Rebuild(c); Same("historical wars remain irrelevant");
        var before = CampaignFingerprint.Of(c);
        graph.Rebuild(c); Same("rebuilding the same day stays current");
        Check(CampaignFingerprint.Of(c) == before, "scratch construction and target reading alter no campaign data");
        var other = new Campaign(1); graph.Rebuild(other); graph.Rebuild(c); Same("scratch reuse cannot retain another world's alliances");
    }
    static void ProgrammeOrder(Campaign c)
    {
        c.Wars.Clear(); c.Relations.Clear();
        var a = c.Nations[0]; var b = c.Nations[1]; var third = c.Nations[2];
        // Insert larger key first. With only 1.5 in the shared treasury only the
        // lower key may take the next payment, just like the original ordered scan.
        var second = CampaignDiplomacy.Get(c,a.Id,third.Id);
        var first = CampaignDiplomacy.Get(c,a.Id,b.Id);
        foreach(var r in new[] { first, second })
        {
            r.Stance=DiplomaticStance.Allied; r.AllianceProgram="coordination";
            r.AllianceProgramSponsor=a.Id; r.AllianceProgramStarted=0; r.AllianceProgramLastPaid=0;
            r.AllianceProgramDuration=84; r.AllianceProgramWeeklyCost=1; r.AllianceProgramPausedDay=-1;
        }
        a.Treasury=1.5f;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c,7L*24);
        AlliancePrograms.Tick(c);
        Check(AlliancePrograms.Active(first) && !AlliancePrograms.Active(second) && a.Treasury==.5f, "filtering retains deterministic payment order and insufficient-funds outcome");
        foreach(var r in c.Relations.Values) AlliancePrograms.Clear(r);
        for(var i=2;i<140;i++) for(var j=0;j<i;j++) CampaignDiplomacy.Get(c,c.Nations[i].Id,c.Nations[j].Id);
        AlliancePrograms.Tick(c);
        var hash=CampaignFingerprint.Of(c);
        var start=GC.GetAllocatedBytesForCurrentThread();
        AlliancePrograms.Tick(c);
        var allocated=GC.GetAllocatedBytesForCurrentThread()-start;
        Check(allocated<4096 && CampaignFingerprint.Of(c)==hash, "inactive archive is neither sorted nor mutated ("+allocated+" bytes)");
    }
}
