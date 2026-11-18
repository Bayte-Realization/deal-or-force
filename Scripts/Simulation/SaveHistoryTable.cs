using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

/// <summary>
/// The history table as the binary save's second section: plain little-endian numbers written
/// by hand with BinaryWriter.
///
/// The history is the one part of a save that is nothing but numbers - 287,474 rows of a day
/// and twenty-one figures in the owner's 107-year campaign, a quarter of the JSON and a third
/// of the time it took to write. As JSON every figure is printed as shortest-round-trip text
/// and parsed back; here it is its four bytes. The bench of 17 November measured MessagePack
/// for this and found the speed real, but a library was not needed for twenty-two columns,
/// and it came with two costs a save cannot afford: its source generator collects every
/// formatter in an assembly, so a mod assembly could silently change how saves are written,
/// and its dynamic resolvers need Reflection.Emit, which an ahead-of-time compiled build
/// (Android, consoles) does not have. This is a loop over a struct: no dependency, AOT-safe.
///
/// Layout:
///   u16 version (1), u16 column count (22: the day as i32, then the twenty-one figures as
///   f32 in the order NationHistorySample declares them), i32 country count, then per
///   country: i32 key, i32 row count (-1 for a missing list), then the country's rows
///   column by column - every day, then every GDP, then every treasury, and so on.
///
/// Column by column, not row by row, because the file is compressed and a fast compressor
/// finds repeats only nearby: a country's GDP week after week shares its sign and exponent
/// bytes with its neighbours, while a row puts twenty unrelated figures between them.
///
/// Columns are appended, never inserted, removed or retyped, and every column is four bytes.
/// That is the whole compatibility rule: a file with fewer columns than this version knows
/// (an older save) reads the missing ones as zero, and a file with more (a newer save, if a
/// newer version ever lets one through) skips the extra columns. Anything else is a new
/// version number, which this reader refuses rather than misreads.
/// </summary>
internal static class SaveHistoryTable
{
    public const ushort Version = 1;
    /// <summary>The day and the twenty-one figures of <see cref="NationHistorySample"/>.</summary>
    public const int Columns = 22;
    private const int MaxRows = 1 << 20;

    public static void Write(BinaryWriter writer, Dictionary<int, List<NationHistorySample>> table) =>
        Write(writer, table, Columns);

    /// <summary>Write with a given column count. Only tests pass anything but <see cref="Columns"/>:
    /// it is how an older file (fewer columns) or a newer one (more) is made on purpose.</summary>
    internal static void Write(BinaryWriter writer, Dictionary<int, List<NationHistorySample>> table, int columns)
    {
        writer.Write(Version);
        writer.Write((ushort)columns);
        writer.Write(table?.Count ?? 0);
        if (table is null) return;
        // A column is gathered into one array and handed over as its bytes, one call per column:
        // BinaryWriter.Write(float) is a call into the stream per figure, six million of them in
        // the owner's campaign, and that per-figure call was most of the table's 110 ms.
        var column = Array.Empty<int>();
        try
        {
            foreach (var (key, series) in table)
            {
                writer.Write(key);
                if (series is null) { writer.Write(-1); continue; }
                var rows = series.Count;
                writer.Write(rows);
                if (column.Length < rows)
                {
                    if (column.Length > 0) ArrayPool<int>.Shared.Return(column);
                    column = ArrayPool<int>.Shared.Rent(rows);
                }
                var span = column.AsSpan(0, rows);
                for (var c = 0; c < columns; c++)
                {
                    if (c >= Columns) span.Clear();
                    else if (c == 0) for (var r = 0; r < rows; r++) span[r] = series[r].Day;
                    else for (var r = 0; r < rows; r++) span[r] = BitConverter.SingleToInt32Bits(Figure(series[r], c));
                    WriteColumn(writer, span);
                }
            }
        }
        finally
        {
            if (column.Length > 0) ArrayPool<int>.Shared.Return(column);
        }
    }

    public static Dictionary<int, List<NationHistorySample>> Read(BinaryReader reader)
    {
        var version = reader.ReadUInt16();
        if (version != Version)
            throw new InvalidDataException($"The save's history table is version {version}; this game reads version {Version}.");
        int columns = reader.ReadUInt16();
        if (columns < 1) throw new InvalidDataException("The save's history table has no columns.");
        var count = reader.ReadInt32();
        if (count < 0) throw new InvalidDataException("The save's history table is damaged.");
        var table = new Dictionary<int, List<NationHistorySample>>(count);
        // Days and the known figures land in one array, column after column; a column this
        // version does not know is read into the spare row at the end and dropped.
        var cells = Array.Empty<int>();
        try
        {
            for (var n = 0; n < count; n++)
            {
                var key = reader.ReadInt32();
                var rows = reader.ReadInt32();
                // The checksum is judged only after the whole file is read, so a damaged count
                // must not first ask for gigabytes. A million rows is a sample a day for 2,700
                // years; the ledger keeps a few thousand.
                if (rows < -1 || rows > MaxRows) throw new InvalidDataException("The save's history table is damaged.");
                if (rows < 0) { table[key] = null; continue; }
                var needed = checked(rows * (Columns + 1));
                if (cells.Length < needed)
                {
                    if (cells.Length > 0) ArrayPool<int>.Shared.Return(cells);
                    cells = ArrayPool<int>.Shared.Rent(needed);
                }
                Array.Clear(cells, 0, rows * Columns);
                for (var c = 0; c < columns; c++)
                    ReadColumn(reader, cells.AsSpan(Math.Min(c, Columns) * rows, rows));
                var series = new List<NationHistorySample>(rows);
                for (var r = 0; r < rows; r++) series.Add(Build(cells, rows, r));
                table[key] = series;
            }
        }
        finally
        {
            if (cells.Length > 0) ArrayPool<int>.Shared.Return(cells);
        }
        return table;
    }

    /// <summary>A column as little-endian four-byte cells, whatever the machine's order.</summary>
    private static void WriteColumn(BinaryWriter writer, Span<int> cells)
    {
        if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(cells, cells);
        writer.Write(MemoryMarshal.AsBytes(cells));
    }

    private static void ReadColumn(BinaryReader reader, Span<int> cells)
    {
        var bytes = MemoryMarshal.AsBytes(cells);
        while (!bytes.IsEmpty)
        {
            var read = reader.Read(bytes);
            if (read <= 0) throw new EndOfStreamException("The save's history table ends early.");
            bytes = bytes[read..];
        }
        if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(cells, cells);
    }

    /// <summary>Column 1..21 of a row, in the order the record declares its figures.</summary>
    private static float Figure(in NationHistorySample s, int column) => column switch
    {
        1 => s.Gdp, 2 => s.Treasury, 3 => s.PopulationMillions, 4 => s.WeeklyIncome,
        5 => s.WeeklyExpenses, 6 => s.WeeklyBalance, 7 => s.Productivity, 8 => s.StandardOfLiving,
        9 => s.Literacy, 10 => s.Stability, 11 => s.MarketAccess, 12 => s.ShortageIndex,
        13 => s.TradeBalance, 14 => s.MilitaryPower, 15 => s.ManpowerMillions, 16 => s.WarSupport,
        17 => s.FoodPriceIndex, 18 => s.ConsumerPriceIndex, 19 => s.IndustrialPriceIndex,
        20 => s.EnergyPriceIndex, 21 => s.ArmsPriceIndex,
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    /// <summary>Row <paramref name="r"/> of a country's columns: the day, then the figures.</summary>
    private static NationHistorySample Build(int[] cells, int rows, int r)
    {
        float F(int column) => BitConverter.Int32BitsToSingle(cells[column * rows + r]);
        return new(cells[r], F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11),
            F(12), F(13), F(14), F(15), F(16), F(17), F(18), F(19), F(20), F(21));
    }
}
