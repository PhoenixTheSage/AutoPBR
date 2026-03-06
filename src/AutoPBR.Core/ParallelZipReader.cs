using System.IO.Compression;
using AutoPBR.Core.Models;

namespace AutoPBR.Core;

/// <summary>Extracts a zip file with parallel deflate decompression (one thread per entry up to ZipParallelism).</summary>
internal static class ParallelZipReader
{
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirSignature = 0x06054b50;
    private const ushort CompressionMethodStored = 0;
    private const ushort CompressionMethodDeflate = 8;

    public static int ZipParallelism => Math.Max(1, Environment.ProcessorCount - 2);

    public static void ExtractZip(
        string inputZipPath,
        string extracted,
        IProgress<ConversionProgress>? progress,
        ConversionStage stage,
        CancellationToken cancellationToken)
    {
        List<(string fullName, long dataOffset, int compressedSize, int uncompressedSize, bool isStored)> entries;
        using (var fs = new FileStream(inputZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            entries = ParseZip(fs);

        var total = entries.Count;
        progress?.Report(new ConversionProgress(stage, 0, total));

        var degree = Math.Min(ZipParallelism, entries.Count);
        var completed = 0;

        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken }, entry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (fullName, dataOffset, compressedSize, uncompressedSize, isStored) = entry;
            var destPath = Path.Combine(extracted, fullName);
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            using (var fs = new FileStream(inputZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var outFile = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Seek(dataOffset, SeekOrigin.Begin);
                if (isStored)
                {
                    CopyStream(fs, outFile, compressedSize);
                }
                else
                {
                    using var deflate = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true);
                    CopyStream(deflate, outFile, uncompressedSize);
                }
            }

            var n = Interlocked.Increment(ref completed);
            progress?.Report(new ConversionProgress(stage, n, total));
        });
    }

    private static void CopyStream(Stream from, Stream to, int count)
    {
        var buffer = new byte[65536];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = Math.Min(buffer.Length, remaining);
            var n = from.Read(buffer, 0, toRead);
            if (n <= 0) break;
            to.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static List<(string fullName, long dataOffset, int compressedSize, int uncompressedSize, bool isStored)> ParseZip(Stream fs)
    {
        var len = fs.Length;
        const int maxComment = 65535;
        var searchStart = Math.Max(0, len - 22 - maxComment);
        fs.Seek(searchStart, SeekOrigin.Begin);
        var buf = new byte[22 + maxComment];
        var n = fs.Read(buf, 0, buf.Length);
        if (n < 22) return [];

        int eocd = -1;
        for (var i = n - 22; i >= 0; i--)
        {
            if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }
        if (eocd < 0) return [];

        var centralDirSize = ReadLe32(buf, eocd + 12);
        var centralDirOffset = ReadLe32(buf, eocd + 16);
        var totalEntries = ReadLe16(buf, eocd + 8);

        var result = new List<(string, long, int, int, bool)>(totalEntries);
        fs.Seek(centralDirOffset, SeekOrigin.Begin);

        for (var i = 0; i < totalEntries; i++)
        {
            var headerBuf = new byte[46];
            if (ReadFully(fs, headerBuf, 0, 46) != 46) break;
            if (ReadLe32(headerBuf, 0) != CentralFileHeaderSignature) break;

            var compression = ReadLe16(headerBuf, 10);
            var compressedSize = (int)ReadLe32(headerBuf, 20);
            var uncompressedSize = (int)ReadLe32(headerBuf, 24);
            var fileNameLen = ReadLe16(headerBuf, 28);
            var extraLen = ReadLe16(headerBuf, 30);
            var commentLen = ReadLe16(headerBuf, 32);
            var localHeaderOffset = ReadLe32(headerBuf, 42);

            var fileNameBytes = new byte[fileNameLen];
            if (ReadFully(fs, fileNameBytes, 0, fileNameLen) != fileNameLen) break;
            var fullName = System.Text.Encoding.UTF8.GetString(fileNameBytes);
            fs.Seek(extraLen + commentLen, SeekOrigin.Current);

            if (string.IsNullOrEmpty(fullName) || fullName.EndsWith('/'))
                continue; // skip directory entries

            var pos = fs.Position;
            fs.Seek(localHeaderOffset, SeekOrigin.Begin);
            var localBuf = new byte[30];
            if (ReadFully(fs, localBuf, 0, 30) != 30 || ReadLe32(localBuf, 0) != LocalFileHeaderSignature)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                continue;
            }
            var localNameLen = ReadLe16(localBuf, 26);
            var localExtraLen = ReadLe16(localBuf, 28);
            var dataOffset = localHeaderOffset + 30 + localNameLen + localExtraLen;
            fs.Seek(pos, SeekOrigin.Begin);

            var isStored = compression == CompressionMethodStored;
            if (compression != CompressionMethodStored && compression != CompressionMethodDeflate)
                continue; // skip unsupported
            result.Add((fullName, dataOffset, compressedSize, uncompressedSize, isStored));
        }

        return result;
    }

    private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = s.Read(buffer, offset + total, count - total);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    private static ushort ReadLe16(byte[] b, int i) => (ushort)(b[i] | (b[i + 1] << 8));
    private static uint ReadLe32(byte[] b, int i) => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
}
