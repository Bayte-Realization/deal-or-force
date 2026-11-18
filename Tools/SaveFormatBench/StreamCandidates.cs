using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using DealOrForce.Simulation;
using ZstdSharp;
using CampaignSave = DealOrForce.Simulation.SaveSystem.CampaignSave;

namespace DealOrForce.Tools;

/// <summary>
/// The second round (18 November 2026), after an independent review of the first: the
/// baselines that need no new library, measured the same way as the library.
///
/// - Brotli at quality 1 ("Fastest", in .NET itself) against zstd level 1, because the first
///   round compared zstd-1 with Brotli at quality 4 and so measured the level, not the codec.
/// - Each codec read two ways: streamed (as the game reads, nothing whole in memory) and in
///   one piece (the whole compressed file and the whole expanded JSON in arrays), because the
///   first round streamed Brotli and decoded zstd in one piece - again two things at once.
/// - The game's own new pipeline (SaveSystem.WriteBinary / ReadBinary, the framed "BDF3"
///   file) with each codec plugged in, so the shipped code is what gets measured.
///
/// Peak memory is in the result line for every one of them (Program.Measure).
/// </summary>
internal static class StreamCandidates
{
    static StreamCandidates()
    {
        SaveContainer.Register(new ZstdCodec());
    }

    public static readonly Candidate[] All =
    {
        new("bdf2-brotli1", "today's BDF2 pipeline unchanged except CompressionLevel.Fastest (Brotli quality 1)",
            (p, o) => WriteBdf2(p, o, CompressionLevel.Fastest), Formats.ReadJsonBrotli),
        new("json+brotli1-stream", "compact JSON streamed into Brotli q1; read streamed (two passes, as Load)",
            (p, o) => WriteStreamed(p, o, s => new BrotliStream(s, CompressionLevel.Fastest, true)),
            b => ReadStreamed(b, s => new BrotliStream(s, CompressionMode.Decompress, true))),
        new("json+brotli1-onepiece", "same file; read in one piece: BrotliDecoder.TryDecompress into one array, then the JSON from the span",
            (p, o) => WriteStreamed(p, o, s => new BrotliStream(s, CompressionLevel.Fastest, true)), ReadBrotliOnePiece),
        new("json+zstd1-stream", "compact JSON streamed into ZstdSharp level 1; read streamed (two passes, as Load)",
            (p, o) => WriteStreamed(p, o, s => new CompressionStream(s, 1, leaveOpen: true)),
            b => ReadStreamed(b, s => new DecompressionStream(s, leaveOpen: true))),
        new("json+zstd1-onepiece", "same file; read in one piece: Decompressor.Unwrap into one array, then the JSON from the span",
            (p, o) => WriteStreamed(p, o, s => new CompressionStream(s, 1, leaveOpen: true)), ReadZstdOnePiece),
        new("json+gzip1-stream", "compact JSON streamed into gzip Fastest (zlib-ng in .NET 9+); read streamed",
            (p, o) => WriteStreamed(p, o, s => new GZipStream(s, CompressionLevel.Fastest, true)),
            b => ReadStreamed(b, s => new GZipStream(s, CompressionMode.Decompress, true))),
        Framed("bdf3-stored", SaveContainer.StoredCodec, 0),
        Framed("bdf3-brotli1", SaveContainer.BrotliCodec, 1),
        Framed("bdf3-brotli2", SaveContainer.BrotliCodec, 2),
        Framed("bdf3-brotli3", SaveContainer.BrotliCodec, 3),
        Framed("bdf3-brotli4", SaveContainer.BrotliCodec, 4),
        Framed("bdf3-zstd1", SaveContainer.ZstdCodec, 1),
        Framed("bdf3-zstd3", SaveContainer.ZstdCodec, 3),
    };

    private static Candidate Framed(string name, byte codec, int level) =>
        new(name, $"the game's framed save (SaveSystem.WriteBinary/ReadBinary): codec {codec} level {level}, streamed both ways",
            (p, o) =>
            {
                using var sink = new SeekableSink(o);
                SaveSystem.WriteBinary(sink, p, null, codec, level);
            },
            b => SaveSystem.ReadBinary(new MemoryStream(b, writable: false)));

    // ---- the plain streamed file: 8-byte expanded length, then one compressed stream ----

    private static void WriteBdf2(CampaignSave payload, PooledBuffer output, CompressionLevel level)
    {
        using var json = new PooledBuffer(64 << 20);
        Formats.WriteJson(payload, json, text: false);
        output.Write("BDF2"u8);
        using var brotli = new BrotliStream(output.AsStream(), level, leaveOpen: true);
        brotli.Write(json.WrittenSpan);
    }

    private static void WriteStreamed(CampaignSave payload, PooledBuffer output, Func<Stream, Stream> compressor)
    {
        using var sink = new SeekableSink(output);
        sink.Write(stackalloc byte[8]);
        SaveStringTable.Use(payload.StringTable);
        var counter = new SaveContainer.CountingStream(Stream.Null, null, writable: true, limit: long.MaxValue);
        using (var compressed = compressor(sink))
        {
            // The same 1 MB of input buffering the game puts in front of its compressor
            // (SaveContainer.InputBuffer); without it Brotli at quality 1 sees the serializer's
            // 64 KB flushes as separate fragments and the file is 14 % larger.
            using var buffered = new BufferedStream(compressed, SaveContainer.InputBuffer);
            var tee = new Tee(buffered, counter);
            JsonSerializer.Serialize(tee, payload, Formats.Compact);
            tee.Flush();
        }
        BinaryPrimitives.WriteInt64LittleEndian(output.GetSpanAt(0), counter.Count);
    }

    private static CampaignSave ReadStreamed(byte[] file, Func<Stream, Stream> decompressor)
    {
        using (var scan = decompressor(new MemoryStream(file, 8, file.Length - 8, writable: false)))
            SaveStringTable.Use(SaveStringTableScanner.Read(scan));
        using var body = decompressor(new MemoryStream(file, 8, file.Length - 8, writable: false));
        return JsonSerializer.Deserialize<CampaignSave>(body, Formats.Compact);
    }

    private static CampaignSave ReadBrotliOnePiece(byte[] file)
    {
        var length = checked((int)BinaryPrimitives.ReadInt64LittleEndian(file));
        var json = ArrayPool<byte>.Shared.Rent(length);
        if (!BrotliDecoder.TryDecompress(file.AsSpan(8), json.AsSpan(0, length), out var written) || written != length)
            throw new InvalidDataException("Brotli one-piece decode failed");
        return Formats.ReadJsonSpan(new ArraySegment<byte>(json, 0, length));
    }

    private static CampaignSave ReadZstdOnePiece(byte[] file)
    {
        var length = checked((int)BinaryPrimitives.ReadInt64LittleEndian(file));
        var json = ArrayPool<byte>.Shared.Rent(length);
        using (var decompressor = new Decompressor())
            if (decompressor.Unwrap(file.AsSpan(8), json.AsSpan(0, length)) != length)
                throw new InvalidDataException("zstd one-piece decode failed");
        return Formats.ReadJsonSpan(new ArraySegment<byte>(json, 0, length));
    }

    /// <summary>Zstandard through ZstdSharp's own streams, for the framed pipeline. Bench only:
    /// the game does not register it.</summary>
    private sealed class ZstdCodec : SaveContainer.ICodec
    {
        public byte Id => SaveContainer.ZstdCodec;
        public string Name => "zstd";
        public Stream Compress(Stream target, int level) => new CompressionStream(target, level, leaveOpen: true);
        public Stream Decompress(Stream source) => new DecompressionStream(source, leaveOpen: true);
    }

    /// <summary>Writes to two streams: the compressor and a counter of expanded bytes.</summary>
    private sealed class Tee : Stream
    {
        private readonly Stream _a, _b;
        public Tee(Stream a, Stream b) { _a = a; _b = b; }
        public override void Write(ReadOnlySpan<byte> buffer) { _a.Write(buffer); _b.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() { _a.Flush(); _b.Flush(); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A seekable view of the output buffer, because the framed writer patches its
    /// header at the end exactly as it does on the game's temporary file.</summary>
    private sealed class SeekableSink : Stream
    {
        private readonly PooledBuffer _owner;
        private long _position;
        public SeekableSink(PooledBuffer owner) { _owner = owner; _position = owner.Length; }
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _owner.Length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _owner.Length + offset
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_position < _owner.Length)
            {
                var overwrite = (int)Math.Min(buffer.Length, _owner.Length - _position);
                buffer[..overwrite].CopyTo(_owner.GetSpanAt((int)_position));
                _position += overwrite;
                buffer = buffer[overwrite..];
            }
            if (buffer.IsEmpty) return;
            _owner.Write(buffer);
            _position = _owner.Length;
        }
    }
}
