using System;
using System.IO.Compression;
using DealOrForce.Simulation;

static class SaveCompressionTests
{
    public static void Run()
    {
        Check(SaveSystem.StorageCompression(new Defines().SaveCompressionThorough) == CompressionLevel.Optimal,
            "default manual and automatic saves do not secretly request Brotli quality 11");
        Check(SaveSystem.StorageCompression(true) == CompressionLevel.SmallestSize,
            "explicit thorough compression still selects the smallest lossless container");
    }
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception(note); Console.WriteLine("PASS save compression: " + note); }
}
