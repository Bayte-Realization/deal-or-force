using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DealOrForce.Simulation;
using K4os.Compression.LZ4;
using MessagePack;
using MessagePack.Resolvers;
using MemoryPack;
using ZstdSharp;
using CampaignSave = DealOrForce.Simulation.SaveSystem.CampaignSave;

namespace DealOrForce.Tools;

internal sealed record Candidate(string Name, string Note, Action<CampaignSave, PooledBuffer> Encode, Func<byte[], CampaignSave> Decode);

/// <summary>
/// The formats under test. Each one is the bytes that would be the save file: header, body
/// and compression, nothing left for later.
/// </summary>
internal static class Candidates
{
    public static Candidate[] All => System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(Base, StreamCandidates.All));

    private static readonly Candidate[] Base =
    {
        new("json-text", "today's text save (Defines.SaveTextFormat), no compression",
            (p, o) => Formats.WriteJson(p, o, text: true), b => Formats.ReadJsonStream(new MemoryStream(b))),
        new("json", "today's compact JSON, no compression",
            (p, o) => Formats.WriteJson(p, o, text: false), b => Formats.ReadJsonStream(new MemoryStream(b))),
        new("json+brotli", "today's binary save: BDF2 + Brotli CompressionLevel.Optimal (the default)",
            Formats.WriteJsonBrotli, Formats.ReadJsonBrotli),
        new("json+gzip", "today's network snapshot: gzip Fastest",
            Formats.WriteJsonGzip, Formats.ReadJsonGzip),
        new("json+lz4", "compact JSON + K4os LZ4 L00_FAST, one block",
            (p, o) => Formats.WriteJsonThen(p, o, Formats.Lz4(LZ4Level.L00_FAST)), b => Formats.ReadJsonSpan(Formats.UnLz4(b))),
        new("json+lz4hc", "compact JSON + K4os LZ4 L09_HC, one block",
            (p, o) => Formats.WriteJsonThen(p, o, Formats.Lz4(LZ4Level.L09_HC)), b => Formats.ReadJsonSpan(Formats.UnLz4(b))),
        new("json+zstd1", "compact JSON + ZstdSharp level 1",
            (p, o) => Formats.WriteJsonThen(p, o, Formats.Zstd(1)), b => Formats.ReadJsonSpan(Formats.UnZstd(b))),
        new("json+zstd3", "compact JSON + ZstdSharp level 3 (zstd default)",
            (p, o) => Formats.WriteJsonThen(p, o, Formats.Zstd(3)), b => Formats.ReadJsonSpan(Formats.UnZstd(b))),
        new("json+zstd3mt", "compact JSON + ZstdSharp level 3, 4 worker threads",
            (p, o) => Formats.WriteJsonThen(p, o, Formats.Zstd(3, workers: 4)), b => Formats.ReadJsonSpan(Formats.UnZstd(b))),
        new("mpack", "MessagePack v3 contractless (member names as keys), no compression",
            (p, o) => Formats.WriteMsgPack(p, o, Formats.MsgPackPlain), b => Formats.ReadMsgPack(b, Formats.MsgPackPlain)),
        new("mpack+lz4", "MessagePack v3 contractless + its own Lz4BlockArray",
            (p, o) => Formats.WriteMsgPack(p, o, Formats.MsgPackLz4), b => Formats.ReadMsgPack(b, Formats.MsgPackLz4)),
        new("mpack+zstd3", "MessagePack v3 contractless + ZstdSharp level 3",
            (p, o) => Formats.WriteMsgPackThen(p, o, Formats.Zstd(3)), b => Formats.ReadMsgPack(Formats.UnZstd(b), Formats.MsgPackPlain)),
        new("mpack+brotli", "MessagePack v3 contractless + Brotli Optimal",
            (p, o) => Formats.WriteMsgPackThen(p, o, Formats.Brotli), b => Formats.ReadMsgPack(Formats.UnBrotli(b), Formats.MsgPackPlain)),
        new("mpack-hk", "MessagePack v3 contractless, history rows as integer-keyed arrays (one formatter)",
            (p, o) => Formats.WriteMsgPack(p, o, Formats.MsgPackHistoryKeyed), b => Formats.ReadMsgPack(b, Formats.MsgPackHistoryKeyed)),
        new("mpack-hk+lz4", "as mpack-hk + MessagePack's own Lz4BlockArray",
            (p, o) => Formats.WriteMsgPack(p, o, Formats.MsgPackHistoryKeyedLz4), b => Formats.ReadMsgPack(b, Formats.MsgPackHistoryKeyedLz4)),
        new("mpack-hk+zstd3", "as mpack-hk + ZstdSharp level 3",
            (p, o) => Formats.WriteMsgPackThen(p, o, Formats.Zstd(3), Formats.MsgPackHistoryKeyed),
            b => Formats.ReadMsgPack(Formats.UnZstd(b), Formats.MsgPackHistoryKeyed)),
        new("json-hk+zstd1", "hybrid: compact JSON without history + history as integer-keyed MessagePack, one ZstdSharp level-1 frame",
            (p, o) => Formats.WriteHybrid(p, o, Formats.Zstd(1)), Formats.ReadHybridZstd),
        new("memorypack", "MemoryPack, as-is (no [MemoryPackable] on game types)",
            (p, o) => MemoryPackSerializer.Serialize(o, p), b => MemoryPackSerializer.Deserialize<CampaignSave>(b)),
    };
}

/// <summary>A growable buffer from the shared pool, as the game's own PooledByteWriter: a
/// save of hundreds of megabytes is not allocated afresh on every autosave.</summary>
public sealed class PooledBuffer : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    public int Length { get; private set; }
    public PooledBuffer(int capacity) => _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, Length);
    public void Reset() => Length = 0;
    public void Advance(int count) => Length += count;
    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(Length); }
    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(Length); }
    /// <summary>Already-written bytes, for back-patching a length header.</summary>
    public Span<byte> GetSpanAt(int offset) => _buffer.AsSpan(offset, Length - offset);
    public void Write(ReadOnlySpan<byte> data) { data.CopyTo(GetSpan(data.Length)); Length += data.Length; }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_buffer.Length - Length >= sizeHint) return;
        var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, Math.Max((long)_buffer.Length * 2, (long)Length + sizeHint)));
        _buffer.AsSpan(0, Length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    public void Dispose()
    {
        if (_buffer is null) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
    }

    /// <summary>A Stream view, for the compressors that only speak Stream.</summary>
    public Stream AsStream() => new Sink(this);

    private sealed class Sink : Stream
    {
        private readonly PooledBuffer _owner;
        public Sink(PooledBuffer owner) => _owner = owner;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _owner.Length;
        public override long Position { get => _owner.Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _owner.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => _owner.Write(buffer);
    }
}

internal static class Formats
{
    // The game's own options, taken from SaveSystem rather than re-declared, so the baseline
    // is today's format by construction and not by careful copying.
    public static readonly JsonSerializerOptions Compact = Options("JsonOptions");
    public static readonly JsonSerializerOptions Text = Options("TextOptions");

    private static JsonSerializerOptions Options(string field) =>
        (JsonSerializerOptions)typeof(SaveSystem).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    // Contractless: no attribute on any game type. Standard resolvers first, so primitives,
    // strings, collections and enums use the built-in formatters; everything else is a map
    // keyed by member name, which is the closest MessagePack gets to what JSON does today.
    // The AllowPrivate variant is required, not chosen: the payload root (SaveSystem.CampaignSave)
    // is an internal nested class, and the plain contractless resolver refuses any non-public
    // type ("Building dynamic formatter only allows public type"). AllowPrivate also writes
    // non-public members, so it carries more than JSON does - recorded in the document.
    public static readonly MessagePackSerializerOptions MsgPackPlain =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolverAllowPrivate.Instance);
    public static readonly MessagePackSerializerOptions MsgPackLz4 = MsgPackPlain.WithCompression(MessagePackCompression.Lz4BlockArray);

    // The same contractless resolver with one hand-written formatter in front of it: the
    // history row as [Key(0)]..[Key(21)] would write it. Shows what annotating the single
    // largest block buys before anyone annotates the other 73 types.
    //
    // The formatter is deliberately PRIVATE. MessagePack v3's source generator collects every
    // internal-or-public IMessagePackFormatter<T> in the assembly into the generated resolver
    // that StandardResolver consults first - so an internal formatter here silently changed
    // the plain "mpack" candidate too (found on 17 November: the contractless history came out
    // byte-for-byte the size of the keyed one). Private keeps the two candidates apart.
    public static readonly MessagePackSerializerOptions MsgPackHistoryKeyed = MessagePackSerializerOptions.Standard.WithResolver(
        CompositeResolver.Create(
            new MessagePack.Formatters.IMessagePackFormatter[] { new KeyedSampleFormatter() },
            new IFormatterResolver[] { ContractlessStandardResolverAllowPrivate.Instance }));
    public static readonly MessagePackSerializerOptions MsgPackHistoryKeyedLz4 = MsgPackHistoryKeyed.WithCompression(MessagePackCompression.Lz4BlockArray);

    private sealed class KeyedSampleFormatter : MessagePack.Formatters.IMessagePackFormatter<NationHistorySample>
    {
        public void Serialize(ref MessagePackWriter w, NationHistorySample v, MessagePackSerializerOptions options)
        {
            w.WriteArrayHeader(22);
            w.Write(v.Day); w.Write(v.Gdp); w.Write(v.Treasury); w.Write(v.PopulationMillions);
            w.Write(v.WeeklyIncome); w.Write(v.WeeklyExpenses); w.Write(v.WeeklyBalance); w.Write(v.Productivity);
            w.Write(v.StandardOfLiving); w.Write(v.Literacy); w.Write(v.Stability); w.Write(v.MarketAccess);
            w.Write(v.ShortageIndex); w.Write(v.TradeBalance); w.Write(v.MilitaryPower); w.Write(v.ManpowerMillions);
            w.Write(v.WarSupport); w.Write(v.FoodPriceIndex); w.Write(v.ConsumerPriceIndex); w.Write(v.IndustrialPriceIndex);
            w.Write(v.EnergyPriceIndex); w.Write(v.ArmsPriceIndex);
        }

        public NationHistorySample Deserialize(ref MessagePackReader r, MessagePackSerializerOptions options)
        {
            // A later version that appended a column still reads here: extra entries are
            // skipped, and a shorter (older) row leaves the missing columns at zero - which is
            // the version tolerance integer keys buy.
            var count = r.ReadArrayHeader();
            Span<float> f = stackalloc float[21];
            var day = count > 0 ? r.ReadInt32() : 0;
            for (var i = 1; i < count; i++)
                if (i <= 21) f[i - 1] = r.ReadSingle(); else r.Skip();
            return new NationHistorySample(day, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10],
                f[11], f[12], f[13], f[14], f[15], f[16], f[17], f[18], f[19], f[20]);
        }
    }

    // ---- JSON, the way SaveSystem.PreparedSave.Commit writes it ----

    public static void WriteJson(CampaignSave payload, PooledBuffer output, bool text)
    {
        var interned = SaveStringTable.InternIds;
        SaveStringTable.Use(payload.StringTable);
        if (text) SaveStringTable.InternIds = false;
        try
        {
            if (!text)
            {
                using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, Encoder = Compact.Encoder, MaxDepth = Compact.MaxDepth });
                JsonSerializer.Serialize(writer, payload, Compact);
                return;
            }
            // A text save as Commit writes it: the payload indented with an empty history,
            // then the history spliced in as compact JSON. Writing the history indented
            // instead made this candidate 264 MB against the game's 211 MB - a format the
            // game does not write.
            var history = payload.History;
            payload.History = new Dictionary<int, List<NationHistorySample>>();
            using var body = new PooledBuffer(64 << 20);
            try
            {
                using (var writer = new Utf8JsonWriter(body, new JsonWriterOptions { Indented = true, Encoder = Compact.Encoder, MaxDepth = Compact.MaxDepth }))
                    JsonSerializer.Serialize(writer, payload, Text);
            }
            finally { payload.History = history; }
            var span = body.WrittenSpan;
            var crlf = span.EndsWith("\"History\": {}\r\n}"u8);
            if (!crlf && !span.EndsWith("\"History\": {}\n}"u8)) throw new InvalidOperationException("text save does not end with History");
            output.Write(span[..^(crlf ? 5 : 4)]);
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = Compact.Encoder, MaxDepth = Compact.MaxDepth }))
                JsonSerializer.Serialize(writer, history, Compact);
            output.Write(crlf ? "\r\n}"u8 : "\n}"u8);
        }
        finally { SaveStringTable.InternIds = interned; }
    }

    // Reading is what Load does: the string table is scanned from the front of the stream,
    // then the whole payload is read from a second pass over the same bytes.
    public static CampaignSave ReadJsonStream(Stream source)
    {
        SaveStringTable.Use(SaveStringTableScanner.Read(source));
        source.Position = 0;
        return JsonSerializer.Deserialize<CampaignSave>(source, Compact);
    }

    public static CampaignSave ReadJsonSpan(ArraySegment<byte> json)
    {
        try
        {
            using (var scan = new MemoryStream(json.Array!, json.Offset, json.Count, writable: false))
                SaveStringTable.Use(SaveStringTableScanner.Read(scan));
            return JsonSerializer.Deserialize<CampaignSave>(json.AsSpan(), Compact);
        }
        finally { ArrayPool<byte>.Shared.Return(json.Array!); }
    }

    // Quality 4 (Optimal) written out, not SaveSystem.StorageCompression: that policy moved to
    // quality 1 on 18 November, and "today's binary save" must keep meaning the BDF2 file that
    // builds before it wrote. The first run of the second round read the policy and measured
    // quality 1 twice under two names; found by the two files being the same size to the byte.
    public static void WriteJsonBrotli(CampaignSave payload, PooledBuffer output)
    {
        using var json = new PooledBuffer(64 << 20);
        WriteJson(payload, json, text: false);
        output.Write("BDF2"u8);
        using var brotli = new BrotliStream(output.AsStream(), CompressionLevel.Optimal, leaveOpen: true);
        brotli.Write(json.WrittenSpan);
    }

    public static CampaignSave ReadJsonBrotli(byte[] file)
    {
        var scan = new BrotliStream(new MemoryStream(file, 4, file.Length - 4), CompressionMode.Decompress);
        SaveStringTable.Use(SaveStringTableScanner.Read(scan));
        scan.Dispose();
        using var body = new BrotliStream(new MemoryStream(file, 4, file.Length - 4), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CampaignSave>(body, Compact);
    }

    public static void WriteJsonGzip(CampaignSave payload, PooledBuffer output)
    {
        using var json = new PooledBuffer(64 << 20);
        WriteJson(payload, json, text: false);
        using var gzip = new GZipStream(output.AsStream(), CompressionLevel.Fastest, leaveOpen: true);
        gzip.Write(json.WrittenSpan);
    }

    public static CampaignSave ReadJsonGzip(byte[] file)
    {
        var scan = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        SaveStringTable.Use(SaveStringTableScanner.Read(scan));
        scan.Dispose();
        using var body = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CampaignSave>(body, Compact);
    }

    // ---- hybrid: the object graph stays JSON, the numeric history table goes binary ----
    //
    // Body layout before compression: 8-byte length of the JSON part, the JSON (History
    // left as {}), then the history as MsgPackHistoryKeyed. Measured because the history
    // bench shows that block is where a binary format pays (11x on write) while the rest of
    // the graph gains little - so the question is what the two together cost.

    public static void WriteHybrid(CampaignSave payload, PooledBuffer output, Action<ReadOnlySpan<byte>, PooledBuffer> compress)
    {
        using var body = new PooledBuffer(64 << 20);
        body.GetSpan(8);
        body.Advance(8);
        var history = payload.History;
        payload.History = new Dictionary<int, List<NationHistorySample>>();
        try { WriteJson(payload, body, text: false); }
        finally { payload.History = history; }
        BinaryPrimitives.WriteInt64LittleEndian(body.GetSpanAt(0), body.Length - 8);
        MessagePackSerializer.Serialize(body, history, MsgPackHistoryKeyed);
        compress(body.WrittenSpan, output);
    }

    public static CampaignSave ReadHybridZstd(byte[] file)
    {
        var all = UnZstd(file);
        try
        {
            var span = all.AsSpan();
            var jsonLength = checked((int)BinaryPrimitives.ReadInt64LittleEndian(span));
            using (var scan = new MemoryStream(all.Array!, all.Offset + 8, jsonLength, writable: false))
                SaveStringTable.Use(SaveStringTableScanner.Read(scan));
            var payload = JsonSerializer.Deserialize<CampaignSave>(span.Slice(8, jsonLength), Compact);
            payload.History = MessagePackSerializer.Deserialize<Dictionary<int, List<NationHistorySample>>>(
                all.AsMemory(8 + jsonLength), MsgPackHistoryKeyed);
            return payload;
        }
        finally { ArrayPool<byte>.Shared.Return(all.Array!); }
    }

    public static void WriteJsonThen(CampaignSave payload, PooledBuffer output, Action<ReadOnlySpan<byte>, PooledBuffer> compress)
    {
        using var json = new PooledBuffer(64 << 20);
        WriteJson(payload, json, text: false);
        compress(json.WrittenSpan, output);
    }

    // ---- MessagePack ----

    public static void WriteMsgPack(CampaignSave payload, PooledBuffer output, MessagePackSerializerOptions options) =>
        MessagePackSerializer.Serialize(output, payload, options);

    public static CampaignSave ReadMsgPack(byte[] file, MessagePackSerializerOptions options) =>
        MessagePackSerializer.Deserialize<CampaignSave>(file, options);

    public static CampaignSave ReadMsgPack(ArraySegment<byte> body, MessagePackSerializerOptions options)
    {
        try { return MessagePackSerializer.Deserialize<CampaignSave>(body.AsMemory(), options); }
        finally { ArrayPool<byte>.Shared.Return(body.Array!); }
    }

    public static void WriteMsgPackThen(CampaignSave payload, PooledBuffer output, Action<ReadOnlySpan<byte>, PooledBuffer> compress,
        MessagePackSerializerOptions options = null)
    {
        using var body = new PooledBuffer(64 << 20);
        MessagePackSerializer.Serialize(body, payload, options ?? MsgPackPlain);
        compress(body.WrittenSpan, output);
    }

    // ---- compressors over a whole buffer. Header: 8-byte original length, then the body. ----

    public static Action<ReadOnlySpan<byte>, PooledBuffer> Lz4(LZ4Level level) => (source, output) =>
    {
        var header = output.GetSpan(8);
        BinaryPrimitives.WriteInt64LittleEndian(header, source.Length);
        output.Advance(8);
        var target = output.GetSpan(LZ4Codec.MaximumOutputSize(source.Length));
        var written = LZ4Codec.Encode(source, target, level);
        if (written < 0) throw new InvalidOperationException("LZ4 encode failed");
        output.Advance(written);
    };

    public static ArraySegment<byte> UnLz4(byte[] file)
    {
        var length = (int)BinaryPrimitives.ReadInt64LittleEndian(file);
        var target = ArrayPool<byte>.Shared.Rent(length);
        var read = LZ4Codec.Decode(file.AsSpan(8), target.AsSpan(0, length));
        if (read != length) throw new InvalidDataException("LZ4 decode size mismatch");
        return new ArraySegment<byte>(target, 0, length);
    }

    public static Action<ReadOnlySpan<byte>, PooledBuffer> Zstd(int level, int workers = 0) => (source, output) =>
    {
        using var compressor = new Compressor(level);
        if (workers > 0) compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, workers);
        var target = output.GetSpan(Compressor.GetCompressBound(source.Length));
        var written = compressor.Wrap(source, target);
        output.Advance(written);
    };

    public static ArraySegment<byte> UnZstd(byte[] file)
    {
        var length = checked((int)Decompressor.GetDecompressedSize(file));
        var target = ArrayPool<byte>.Shared.Rent(length);
        using var decompressor = new Decompressor();
        var read = decompressor.Unwrap(file, target.AsSpan(0, length));
        if (read != length) throw new InvalidDataException("zstd decode size mismatch");
        return new ArraySegment<byte>(target, 0, length);
    }

    public static void Brotli(ReadOnlySpan<byte> source, PooledBuffer output)
    {
        using var brotli = new BrotliStream(output.AsStream(), CompressionLevel.Optimal, leaveOpen: true);
        brotli.Write(source);
    }

    public static ArraySegment<byte> UnBrotli(byte[] file)
    {
        using var body = new BrotliStream(new MemoryStream(file), CompressionMode.Decompress);
        var buffer = new PooledBuffer(file.Length * 4);
        body.CopyTo(buffer.AsStream());
        // Hand the pooled array over to the reader, which returns it to the pool.
        var array = (byte[])typeof(PooledBuffer).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(buffer)!;
        return new ArraySegment<byte>(array, 0, buffer.Length);
    }

    // ---- the round-trip judge ----

    /// <summary>The payload as the game's compact JSON, hashed. Two payloads with the same
    /// digest carry the same values in every member JSON writes - which is the definition of
    /// "the save is the same" this game already lives by.</summary>
    public static byte[] JsonDigest(CampaignSave payload)
    {
        using var buffer = new PooledBuffer(64 << 20);
        WriteJson(payload, buffer, text: false);
        return SHA256.HashData(buffer.WrittenSpan);
    }

    /// <summary>When the digests differ, say where: compare the top-level members one by
    /// one, then the first differing byte of the first member that differs.</summary>
    public static void ExplainDifference(CampaignSave a, CampaignSave b)
    {
        SaveStringTable.Use(a.StringTable);
        var shown = 0;
        foreach (var field in typeof(CampaignSave).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            byte[] left, right;
            try
            {
                left = JsonSerializer.SerializeToUtf8Bytes(field.GetValue(a), field.FieldType, Compact);
                right = JsonSerializer.SerializeToUtf8Bytes(field.GetValue(b), field.FieldType, Compact);
            }
            catch (Exception error) { Console.WriteLine($"DIFF {field.Name}: cannot compare ({error.Message})"); continue; }
            if (left.AsSpan().SequenceEqual(right)) continue;
            var at = left.AsSpan().CommonPrefixLength(right);
            string Around(byte[] s) => System.Text.Encoding.UTF8.GetString(s, Math.Max(0, at - 120), Math.Min(s.Length - Math.Max(0, at - 120), 240));
            Console.WriteLine($"DIFF {field.Name}: {left.Length} vs {right.Length} bytes, first difference at {at}");
            if (shown++ < 6)
            {
                Console.WriteLine("  original: " + Around(left));
                Console.WriteLine("  decoded : " + Around(right));
            }
        }
    }
}
