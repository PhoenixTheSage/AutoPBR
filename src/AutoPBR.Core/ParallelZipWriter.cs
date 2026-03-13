using System.IO.Compression;
using System.IO.Hashing;
using AutoPBR.Core.Models;

namespace AutoPBR.Core;

/// <summary>Writes a zip file with parallel deflate compression (one thread per file up to ZipParallelism).</summary>
internal static class ParallelZipWriter
{
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirSignature = 0x06054b50;
    private const ushort CompressionMethodDeflate = 8;
    private const ushort VersionNeeded = 20;

    public static int ZipParallelism => Math.Max(1, Environment.ProcessorCount - 2);

    public static void WriteZip(
        string outputPath,
        IReadOnlyList<string> files,
        string basePath,
        IProgress<ConversionProgress>? progress,
        ConversionStage stage,
        CancellationToken cancellationToken)
    {
        var total = files.Count;
        progress?.Report(new ConversionProgress(stage, 0, total));

        var degree = Math.Min(ZipParallelism, files.Count);
        var entries = new (string relativePath, uint crc32, int uncompressedSize, byte[] compressed)[files.Count];
        var completed = 0;

        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken }, i =>
        {
            try { Thread.CurrentThread.Name ??= "AutoPBR.Pack"; } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = files[i];
            var relativePath = Path.GetRelativePath(basePath, fullPath).Replace('\\', '/');
            var data = File.ReadAllBytes(fullPath);
            var crc32 = BitConverter.ToUInt32(Crc32.Hash(data), 0);
            var compressed = Compress(data);
            entries[i] = (relativePath, crc32, data.Length, compressed);
            var n = Interlocked.Increment(ref completed);
            progress?.Report(new ConversionProgress(stage, n, total));
        });

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var centralDirEntries = new List<(string name, uint crc32, int compressedSize, int uncompressedSize, long localHeaderOffset)>();

        foreach (var (relativePath, crc32, uncompressedSize, compressed) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localHeaderOffset = fs.Position;
            WriteLocalFileHeader(fs, relativePath, crc32, compressed.Length, uncompressedSize);
            fs.Write(compressed, 0, compressed.Length);
            centralDirEntries.Add((relativePath, crc32, compressed.Length, uncompressedSize, localHeaderOffset));
        }

        var centralDirOffset = fs.Position;
        foreach (var (name, crc32, compressedSize, uncompressedSize, localHeaderOffset) in centralDirEntries)
            WriteCentralFileHeader(fs, name, crc32, compressedSize, uncompressedSize, localHeaderOffset);
        var centralDirSize = (int)(fs.Position - centralDirOffset);
        WriteEndOfCentralDirectory(fs, centralDirEntries.Count, centralDirSize, centralDirOffset);
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static void WriteLocalFileHeader(Stream s, string fileName, uint crc32, int compressedSize, int uncompressedSize)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
        var (time, date) = GetDosDateTime(DateTimeOffset.Now);
        s.Write(BitConverter.GetBytes(LocalFileHeaderSignature), 0, 4);
        WriteLe(s, VersionNeeded);
        WriteLe(s, 0); // flags
        WriteLe(s, CompressionMethodDeflate);
        WriteLe(s, time);
        WriteLe(s, date);
        WriteLe(s, crc32);
        WriteLe(s, (uint)compressedSize);
        WriteLe(s, (uint)uncompressedSize);
        WriteLe(s, (ushort)nameBytes.Length);
        WriteLe(s, 0); // extra field length
        s.Write(nameBytes, 0, nameBytes.Length);
    }

    private static void WriteCentralFileHeader(Stream s, string fileName, uint crc32, int compressedSize, int uncompressedSize, long localHeaderOffset)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
        var (time, date) = GetDosDateTime(DateTimeOffset.Now);
        s.Write(BitConverter.GetBytes(CentralFileHeaderSignature), 0, 4);
        WriteLe(s, 0); // version made by
        WriteLe(s, VersionNeeded);
        WriteLe(s, 0); // flags
        WriteLe(s, CompressionMethodDeflate);
        WriteLe(s, time);
        WriteLe(s, date);
        WriteLe(s, crc32);
        WriteLe(s, (uint)compressedSize);
        WriteLe(s, (uint)uncompressedSize);
        WriteLe(s, (ushort)nameBytes.Length);
        WriteLe(s, 0); // extra field length
        WriteLe(s, 0); // comment length
        WriteLe(s, 0); // disk number
        WriteLe(s, 0); // internal attrs
        WriteLe(s, 0); // external attrs
        WriteLe(s, (uint)localHeaderOffset);
        s.Write(nameBytes, 0, nameBytes.Length);
    }

    private static void WriteEndOfCentralDirectory(Stream s, int entryCount, int centralDirSize, long centralDirOffset)
    {
        s.Write(BitConverter.GetBytes(EndOfCentralDirSignature), 0, 4);
        WriteLe(s, 0); // disk number
        WriteLe(s, 0); // disk with central dir
        WriteLe(s, (ushort)entryCount);
        WriteLe(s, (ushort)entryCount);
        WriteLe(s, (uint)centralDirSize);
        WriteLe(s, (uint)centralDirOffset);
        WriteLe(s, 0); // comment length
    }

    private static (ushort time, ushort date) GetDosDateTime(DateTimeOffset dt)
    {
        var time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
        var date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
        return (time, date);
    }

    private static void WriteLe(Stream s, ushort value)
    {
        var b = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        s.Write(b, 0, 2);
    }

    private static void WriteLe(Stream s, uint value)
    {
        var b = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        s.Write(b, 0, 4);
    }
}
