using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IO;

namespace DealOrForce.Simulation;

/// <summary>
/// The binary save file, framed: "BDF3".
///
/// Until 18 November 2026 a binary save was "BDF2" and one Brotli stream of the whole JSON,
/// history included, at quality 4. On the owner's 107-year campaign that was 3.6 seconds of
/// writing on the worker and 2.8 of reading, two thirds of the write in the compressor, and a
/// file that said nothing about itself: no version, no length, no checksum, so a truncated file
/// was found by the JSON reader somewhere in the middle and a flipped bit in a number was not
/// found at all. The measurement that chose this layout is docs/SAVE_FORMAT_2026_11_18_RU.md.
///
/// The file is a fixed header and then sections, each compressed on its own:
///
///   0   "BDF3"
///   4   u16 header length (bytes from 0; a later version may add fields, a reader skips them)
///   6   u16 container version (this layout: 1)
///   8   u16 payload format version (SaveSystem.FormatVersion, for tools that only read headers)
///   10  u8  section count
///   11  u8  reserved, 0
///   12  32 bytes SHA-256 checksum (see below)
///   44  per section, 20 bytes: u8 kind, u8 codec, u8 level, u8 reserved,
///       u64 stored length (bytes in the file), u64 raw length (bytes after decompression)
///   then the sections, in header order, back to back.
///
/// Sections: 1 is the campaign as JSON with an empty history - the same JSON the text save and
/// every older save carry, read by the same options, so a mod that adds a field still needs no
/// number and no attribute; 2 is the history table, the one block that is all numbers
/// (<see cref="SaveHistoryTable"/>). An unknown kind is skipped, so a later version can add a
/// section that an older reader does not need.
///
/// The checksum is SHA-256 over the header with the checksum field zeroed, followed by the
/// SHA-256 of all section bytes as stored. Hashing the stored bytes is what lets both halves
/// stream: the writer hashes as it writes and patches the header at the end (the target is
/// always a seekable temporary file - see SaveSystem.AtomicWrite), the reader hashes as it reads
/// and refuses the world before handing it over if the sum is wrong. Nothing in either
/// direction holds the whole file, compressed or expanded: the loader streamed on purpose, and
/// a phone does not have a spare few hundred megabytes for a save.
/// </summary>
internal static class SaveContainer
{
    public static ReadOnlySpan<byte> Magic => "BDF3"u8;
    public const ushort ContainerVersion = 1;
    private const int FixedHeaderLength = 44;
    private const int SectionEntryLength = 20;
    private const int ChecksumOffset = 12;

    /// <summary>
    /// What the compressor is handed at a time. The serializer flushes every 64 KB, and Brotli
    /// at its fast qualities compresses whatever one call gives it as a fragment of its own: fed
    /// 64 KB at a time the owner's save came out 34.6 MB instead of 30.4 (measured 18 November).
    /// A megabyte lets the encoder fill its own blocks; it is the only buffer on the way out.
    /// </summary>
    internal const int InputBuffer = 1 << 20;

    public const byte GraphSection = 1;
    public const byte HistorySection = 2;

    /// <summary>Codec ids are part of the file format and are never reused.</summary>
    public const byte StoredCodec = 0;
    public const byte BrotliCodec = 1;
    /// <summary>Reserved for Zstandard. The game does not register it (see the document: the
    /// margin over Brotli quality 1 did not pay for an unsafe-code dependency); the save-format
    /// bench registers it to measure exactly this pipeline with it.</summary>
    public const byte ZstdCodec = 2;

    /// <summary>A compressor a save can be written and read with. The level is recorded in the
    /// header for whoever reads it later; decompression never needs it.</summary>
    internal interface ICodec
    {
        byte Id { get; }
        string Name { get; }
        Stream Compress(Stream target, int level);
        Stream Decompress(Stream source);
    }

    private static readonly Dictionary<byte, ICodec> Codecs = new()
    {
        [StoredCodec] = new StoredCodecImpl(),
        [BrotliCodec] = new BrotliCodecImpl(),
    };

    /// <summary>For tools that measure another codec through the same pipeline. The game
    /// never calls it, so a file written with an unregistered codec is refused by name.</summary>
    internal static void Register(ICodec codec)
    {
        lock (Codecs) Codecs[codec.Id] = codec;
    }

    private static ICodec CodecFor(byte id)
    {
        lock (Codecs)
            return Codecs.TryGetValue(id, out var codec)
                ? codec
                : throw new InvalidDataException($"The save is compressed with codec {id}, which this version does not read.");
    }

    public readonly record struct Section(byte Kind, byte Codec, byte Level, long Offset, long StoredLength, long RawLength);

    public sealed record Header(int Length, ushort Version, ushort FormatVersion, byte[] Checksum, Section[] Sections, byte[] Raw)
    {
        public Section? Find(byte kind)
        {
            foreach (var section in Sections)
                if (section.Kind == kind) return section;
            return null;
        }
    }

    public static bool IsContainer(ReadOnlySpan<byte> start) => start.Length >= 4 && start[..4].SequenceEqual(Magic);

    /// <summary>The header at the stream's position, which is left just after it.</summary>
    public static Header ReadHeader(Stream file)
    {
        var start = file.Position;
        Span<byte> fixedPart = stackalloc byte[FixedHeaderLength];
        file.ReadExactly(fixedPart);
        if (!IsContainer(fixedPart)) throw new InvalidDataException("Not a framed save.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[4..]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[6..]);
        var format = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart[8..]);
        var count = fixedPart[10];
        if (version > ContainerVersion)
            throw new InvalidDataException("This save was written by a newer version of the game.");
        // The length is a u16, so a damaged one costs at most 64 KB to read before the
        // checksum refuses it; only a length too short for its own section table is caught here.
        if (length < FixedHeaderLength + count * SectionEntryLength)
            throw new InvalidDataException("The save's header is damaged.");
        var raw = new byte[length];
        fixedPart.CopyTo(raw);
        file.ReadExactly(raw, FixedHeaderLength, length - FixedHeaderLength);
        var checksum = raw.AsSpan(ChecksumOffset, 32).ToArray();
        var sections = new Section[count];
        var offset = start + length;
        for (var i = 0; i < count; i++)
        {
            var entry = raw.AsSpan(FixedHeaderLength + i * SectionEntryLength, SectionEntryLength);
            var stored = BinaryPrimitives.ReadInt64LittleEndian(entry[4..]);
            var expanded = BinaryPrimitives.ReadInt64LittleEndian(entry[12..]);
            if (stored < 0 || expanded < 0) throw new InvalidDataException("The save's header is damaged.");
            sections[i] = new Section(entry[0], entry[1], entry[2], offset, stored, expanded);
            offset += stored;
        }
        if (offset > file.Length) throw new InvalidDataException("The save is shorter than its header says; it was not written to the end.");
        return new Header(length, version, format, checksum, sections, raw);
    }

    /// <summary>
    /// Write the whole save to a seekable stream at its position: header placeholder, the
    /// graph streamed through the codec, the history table the same way, then the header with
    /// the lengths and the checksum. Returns the file's length and the checksum as hex.
    /// </summary>
    public static (long Bytes, string Checksum) Write(Stream file, Action<Stream> writeGraph,
        Dictionary<int, List<NationHistorySample>> history, byte codecId, int level, ushort formatVersion)
    {
        var codec = CodecFor(codecId);
        var start = file.Position;
        var sectionCount = 2;
        var headerLength = FixedHeaderLength + sectionCount * SectionEntryLength;
        var header = new byte[headerLength];
        file.Write(header);
        using var bodyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var entries = new List<(byte Kind, long Stored, long Raw)>(sectionCount);

        entries.Add(WriteSection(file, bodyHash, codec, level, GraphSection, writeGraph));
        entries.Add(WriteSection(file, bodyHash, codec, level, HistorySection, raw =>
        {
            // BinaryWriter writes each figure as its own four bytes; through a compressor that
            // is a call into it per figure. The buffer makes it one call per 64 KB.
            using var buffered = new BufferedStream(raw, 64 * 1024);
            using var writer = new BinaryWriter(buffered, System.Text.Encoding.UTF8, leaveOpen: true);
            SaveHistoryTable.Write(writer, history);
            writer.Flush();
            buffered.Flush();
        }));

        "BDF3"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), ContainerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), formatVersion);
        header[10] = (byte)sectionCount;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = header.AsSpan(FixedHeaderLength + i * SectionEntryLength, SectionEntryLength);
            entry[0] = entries[i].Kind;
            entry[1] = codec.Id;
            entry[2] = (byte)Math.Clamp(level, 0, 255);
            BinaryPrimitives.WriteInt64LittleEndian(entry[4..], entries[i].Stored);
            BinaryPrimitives.WriteInt64LittleEndian(entry[12..], entries[i].Raw);
        }
        var checksum = Checksum(header, bodyHash.GetHashAndReset());
        checksum.CopyTo(header, ChecksumOffset);
        var end = file.Position;
        file.Position = start;
        file.Write(header);
        file.Position = end;
        return (end - start, Convert.ToHexString(checksum));
    }

    private static (byte Kind, long Stored, long Raw) WriteSection(Stream file, IncrementalHash hash, ICodec codec,
        int level, byte kind, Action<Stream> write)
    {
        var stored = new CountingStream(file, hash, writable: true, limit: long.MaxValue);
        CountingStream raw;
        using (var compressed = codec.Compress(stored, level))
        {
            using var buffered = new BufferedStream(compressed, InputBuffer);
            raw = new CountingStream(buffered, null, writable: true, limit: long.MaxValue);
            write(raw);
            raw.Flush();
        }
        return (kind, stored.Count, raw.Count);
    }

    /// <summary>SHA-256 of the header (checksum field zeroed) followed by the body's own SHA-256.</summary>
    private static byte[] Checksum(byte[] header, byte[] bodyDigest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var copy = ArrayPool<byte>.Shared.Rent(header.Length);
        try
        {
            header.AsSpan().CopyTo(copy);
            copy.AsSpan(ChecksumOffset, 32).Clear();
            hash.AppendData(copy.AsSpan(0, header.Length));
            hash.AppendData(bodyDigest);
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copy);
        }
    }

    /// <summary>
    /// The graph section as JSON, for the readers that only want to look into it (the slot
    /// list reads the embedded metadata at its front, the string-table scan reads the table).
    /// Not checksummed: nothing that reads this way builds a world from it.
    /// </summary>
    public static Stream OpenGraph(Stream file, Header header, bool leaveOpen)
    {
        var graph = header.Find(GraphSection) ?? throw new InvalidDataException("The save has no campaign section.");
        file.Position = graph.Offset;
        var bounded = new CountingStream(file, null, writable: false, limit: graph.StoredLength, ownsInner: !leaveOpen);
        return CodecFor(graph.Codec).Decompress(bounded);
    }

    /// <summary>
    /// Decompresses the graph section into a pooled <see cref="RecyclableMemoryStream"/>
    /// backed by 64 KB blocks, avoiding Large Object Heap allocations during snapshot reads.
    /// </summary>
    internal static RecyclableMemoryStream OpenGraphBuffered(Stream file, Header header, bool leaveOpen)
    {
        var graph = header.Find(GraphSection);
        var hint = graph is { RawLength: > 0 } ? graph.Value.RawLength : 64 * 1024;
        var pooled = RecyclableStreams.Get("SaveContainer.OpenGraphBuffered", hint);
        try
        {
            using var source = OpenGraph(file, header, leaveOpen);
            source.CopyTo(pooled);
            pooled.Position = 0;
            return pooled;
        }
        catch
        {
            pooled.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read the campaign: the graph through <paramref name="readGraph"/>, the history table into
    /// its own dictionary, every stored byte through the checksum. Throws on a checksum or length
    /// that does not match; the caller turns that into a refusal, never into a world.
    /// </summary>
    public static (T Graph, Dictionary<int, List<NationHistorySample>> History) Read<T>(Stream file, Header header,
        Func<Stream, T> readGraph)
    {
        using var bodyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        T graph = default;
        Dictionary<int, List<NationHistorySample>> history = null;
        file.Position = header.Sections.Length > 0 ? header.Sections[0].Offset : header.Length;
        foreach (var section in header.Sections)
        {
            file.Position = section.Offset;
            var stored = new CountingStream(file, bodyHash, writable: false, limit: section.StoredLength);
            if (section.Kind is GraphSection or HistorySection)
            {
                using var expanded = CodecFor(section.Codec).Decompress(stored);
                var raw = new CountingStream(expanded, null, writable: false, limit: long.MaxValue);
                if (section.Kind == GraphSection) graph = readGraph(raw);
                else
                {
                    using var buffered = new BufferedStream(raw, 64 * 1024);
                    using var reader = new BinaryReader(buffered, System.Text.Encoding.UTF8, leaveOpen: true);
                    history = SaveHistoryTable.Read(reader);
                }
                raw.Drain();
                if (raw.Count != section.RawLength)
                    throw new InvalidDataException("A section of the save expands to a different length than its header says.");
            }
            // Whatever the decoder did not need (and every unknown section) still goes through
            // the checksum: the sum is over the file as stored, not over what was understood.
            stored.Drain();
            if (stored.Count != section.StoredLength)
                throw new InvalidDataException("The save is shorter than its header says; it was not written to the end.");
        }
        var expected = Checksum(header.Raw, bodyHash.GetHashAndReset());
        if (!expected.AsSpan().SequenceEqual(header.Checksum))
            throw new InvalidDataException("Save checksum mismatch; the file may be incomplete or corrupted.");
        if (graph is null) throw new InvalidDataException("The save has no campaign section.");
        return (graph, history ?? new Dictionary<int, List<NationHistorySample>>());
    }

    private sealed class StoredCodecImpl : ICodec
    {
        public byte Id => StoredCodec;
        public string Name => "stored";
        public Stream Compress(Stream target, int level) => new CountingStream(target, null, writable: true, limit: long.MaxValue);
        public Stream Decompress(Stream source) => source;
    }

    /// <summary>
    /// Brotli, built into .NET: nothing to ship, no unsafe code of ours, and the same native
    /// library the runtime already carries on every platform, Android included. Level 1 is
    /// CompressionLevel.Fastest (Brotli quality 1); level 11 is SmallestSize, kept for
    /// Defines.SaveCompressionThorough. Any other level is passed as a quality.
    /// </summary>
    private sealed class BrotliCodecImpl : ICodec
    {
        public byte Id => BrotliCodec;
        public string Name => "brotli";

        public Stream Compress(Stream target, int level) => level switch
        {
            1 => new BrotliStream(target, CompressionLevel.Fastest, leaveOpen: true),
            11 => new BrotliStream(target, CompressionLevel.SmallestSize, leaveOpen: true),
            4 => new BrotliStream(target, CompressionLevel.Optimal, leaveOpen: true),
            0 => new BrotliStream(target, CompressionLevel.NoCompression, leaveOpen: true),
            _ => new BrotliEncoderStream(target, level),
        };

        public Stream Decompress(Stream source) => new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
    }

    /// <summary>Brotli at a quality CompressionLevel does not name (2, 3, 5..10), for
    /// measurement and for a mod that wants another trade. BrotliEncoder is in every .NET the
    /// game and its tools target; BrotliCompressionOptions is not (it arrived in .NET 9).</summary>
    private sealed class BrotliEncoderStream : Stream
    {
        private readonly Stream _target;
        private BrotliEncoder _encoder;
        private readonly byte[] _out = new byte[64 * 1024];
        private bool _done;

        public BrotliEncoderStream(Stream target, int quality)
        {
            _target = target;
            _encoder = new BrotliEncoder(Math.Clamp(quality, 0, 11), 22);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (true)
            {
                var status = _encoder.Compress(buffer, _out, out var consumed, out var written, isFinalBlock: false);
                if (written > 0) _target.Write(_out, 0, written);
                buffer = buffer[consumed..];
                if (status == System.Buffers.OperationStatus.InvalidData) throw new InvalidDataException("Brotli refused the data.");
                if (buffer.IsEmpty && status != System.Buffers.OperationStatus.DestinationTooSmall) return;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        protected override void Dispose(bool disposing)
        {
            if (!_done)
            {
                _done = true;
                while (true)
                {
                    var status = _encoder.Compress(ReadOnlySpan<byte>.Empty, _out, out _, out var written, isFinalBlock: true);
                    if (written > 0) _target.Write(_out, 0, written);
                    if (status == System.Buffers.OperationStatus.Done) break;
                }
                _encoder.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// A pass-through that counts bytes, optionally hashes them, and when reading stops at a
    /// limit - so a decoder cannot read past its section into the next one, and a section is
    /// hashed exactly as stored. Disposing it leaves the inner stream open unless told otherwise.
    /// </summary>
    internal sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash;
        private readonly bool _writable;
        private readonly long _limit;
        private readonly bool _ownsInner;

        public CountingStream(Stream inner, IncrementalHash hash, bool writable, long limit, bool ownsInner = false)
        {
            _inner = inner;
            _hash = hash;
            _writable = writable;
            _limit = limit;
            _ownsInner = ownsInner;
        }

        public long Count { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            var left = _limit - Count;
            if (left <= 0) return 0;
            if (buffer.Length > left) buffer = buffer[..(int)left];
            var read = _inner.Read(buffer);
            if (read > 0)
            {
                _hash?.AppendData(buffer[..read]);
                Count += read;
            }
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _hash?.AppendData(buffer);
            Count += buffer.Length;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        /// <summary>Read to the limit (or the end), hashing what is left.</summary>
        public void Drain()
        {
            var scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (Read(scratch, 0, scratch.Length) > 0) { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        public override bool CanRead => !_writable;
        public override bool CanSeek => false;
        public override bool CanWrite => _writable;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { if (_writable) _inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _ownsInner) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
