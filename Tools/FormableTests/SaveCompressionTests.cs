using System;
using System.IO.Compression;
using DealOrForce.Simulation;

static class SaveCompressionTests
{
    public static void Run()
    {
        Check(SaveSystem.StorageCompression(new Defines().SaveCompressionThorough) == CompressionLevel.Fastest &&
              SaveSystem.StorageLevel(new Defines().SaveCompressionThorough) == 1,
            "default manual and automatic saves use Brotli quality 1, not 4 or 11");
        Check(SaveSystem.StorageCodec == SaveContainer.BrotliCodec,
            "the shipped codec is the runtime's own Brotli: no compression library is shipped");
        Check(SaveSystem.StorageCompression(true) == CompressionLevel.SmallestSize,
            "explicit thorough compression still selects the smallest lossless container");
    }
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception(note); Console.WriteLine("PASS save compression: " + note); }
}
