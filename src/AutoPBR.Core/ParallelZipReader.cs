using System.IO.Compression;
using AutoPBR.Core.Models;

namespace AutoPBR.Core;

/// <summary>Extracts a zip file with parallel deflate decompression (one thread per entry up to ZipParallelism).
/// Supports Zip64 for large archives and large entries.</summary>
internal static class ParallelZipReader
{
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const uint CentralFileHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirSignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirSignature = 0x06064b50;
    private const uint Zip64EndOfCentralDirLocatorSignature = 0x07064b50;
    private const ushort CompressionMethodStored = 0;
    private const ushort CompressionMethodDeflate = 8;
    private const ushort Zip64ExtraId = 0x0001;

    private const int MaxEocdSearch = 65536 * 2; // scan last 128KB for EOCD (handles large comments / trailing data)

    public static int ZipParallelism => Math.Max(1, Environment.ProcessorCount - 2);

    public static void ExtractZip(
        string inputZipPath,
        string extracted,
        IProgress<ConversionProgress>? progress,
        ConversionStage stage,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? entriesToExtractOnly = null)
    {
        List<(string fullName, long dataOffset, long compressedSize, long uncompressedSize, bool isStored)> entries;
        using (var fs = new FileStream(inputZipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            entries = ParseZip(fs);

        if (entriesToExtractOnly is { Count: > 0 })
        {
            var set = new HashSet<string>(entriesToExtractOnly, StringComparer.OrdinalIgnoreCase);
            entries = entries.Where(e => set.Contains(e.fullName)).ToList();
        }

        var total = entries.Count;
        progress?.Report(new ConversionProgress(stage, 0, total));

        var degree = Math.Min(ZipParallelism, entries.Count);
        var completed = 0;

        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken }, entry =>
        {
            try { Thread.CurrentThread.Name ??= "AutoPBR.Extract"; } catch (InvalidOperationException) { }
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
                    CopyStream(fs, outFile, compressedSize, cancellationToken);
                }
                else
                {
                    using var deflate = new DeflateStream(fs, CompressionMode.Decompress, leaveOpen: true);
                    CopyStream(deflate, outFile, uncompressedSize, cancellationToken);
                }
            }

            var n = Interlocked.Increment(ref completed);
            progress?.Report(new ConversionProgress(stage, n, total));
        });
    }

    private static void CopyStream(Stream from, Stream to, long count, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[65536];
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var n = from.Read(buffer, 0, toRead);
            if (n <= 0) break;
            to.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static List<(string fullName, long dataOffset, long compressedSize, long uncompressedSize, bool isStored)> ParseZip(Stream fs)
    {
        var len = fs.Length;
        if (len < 22) return [];

        // Find EOCD: scan from end in chunks so we handle large comments or trailing data
        long centralDirOffset;
        long centralDirSize;
        int totalEntries;

        var searchLen = Math.Min(MaxEocdSearch, len);
        var searchStart = len - searchLen;
        fs.Seek(searchStart, SeekOrigin.Begin);
        var buf = new byte[searchLen];
        var n = fs.Read(buf, 0, buf.Length);
        if (n < 22) return [];

        int eocdIndex = -1;
        for (var i = n - 22; i >= 0; i--)
        {
            if (buf[i] == 0x50 && buf[i + 1] == 0x4b && buf[i + 2] == 0x05 && buf[i + 3] == 0x06)
            {
                eocdIndex = i;
                break;
            }
        }
        if (eocdIndex < 0) return [];

        var centralDirSize32 = ReadLe32(buf, eocdIndex + 12);
        var centralDirOffset32 = ReadLe32(buf, eocdIndex + 16);
        var totalEntries16 = ReadLe16(buf, eocdIndex + 8);

        const uint Zip64Marker32 = 0xFFFFFFFF;
        const ushort Zip64Marker16 = 0xFFFF;

        if (centralDirOffset32 == Zip64Marker32 || centralDirSize32 == Zip64Marker32 || totalEntries16 == Zip64Marker16)
        {
            // Zip64: find Zip64 EOCD locator (20 bytes before standard EOCD)
            var eocdFileOffset = searchStart + eocdIndex;
            var locatorOffset = eocdFileOffset - 20;
            if (locatorOffset < 0) return [];

            fs.Seek(locatorOffset, SeekOrigin.Begin);
            var locatorBuf = new byte[20];
            if (ReadFully(fs, locatorBuf, 0, 20) != 20 || ReadLe32(locatorBuf, 0) != Zip64EndOfCentralDirLocatorSignature)
                return [];

            var zip64EocdOffset = ReadLe64(locatorBuf, 8);
            fs.Seek(zip64EocdOffset, SeekOrigin.Begin);

            var zip64EocdBuf = new byte[56];
            if (ReadFully(fs, zip64EocdBuf, 0, 56) != 56 || ReadLe32(zip64EocdBuf, 0) != Zip64EndOfCentralDirSignature)
                return [];

            centralDirSize = ReadLe64(zip64EocdBuf, 40);
            centralDirOffset = ReadLe64(zip64EocdBuf, 48);
            var totalEntries64 = ReadLe64(zip64EocdBuf, 32);
            totalEntries = totalEntries64 > int.MaxValue ? int.MaxValue : (int)totalEntries64;
        }
        else
        {
            centralDirOffset = centralDirOffset32;
            centralDirSize = centralDirSize32;
            totalEntries = totalEntries16;
        }

        var result = new List<(string, long, long, long, bool)>(totalEntries);
        fs.Seek(centralDirOffset, SeekOrigin.Begin);

        for (var i = 0; i < totalEntries; i++)
        {
            var headerBuf = new byte[46];
            if (ReadFully(fs, headerBuf, 0, 46) != 46) break;
            if (ReadLe32(headerBuf, 0) != CentralFileHeaderSignature) break;

            var compression = ReadLe16(headerBuf, 10);
            var compressedSize32 = ReadLe32(headerBuf, 20);
            var uncompressedSize32 = ReadLe32(headerBuf, 24);
            var fileNameLen = ReadLe16(headerBuf, 28);
            var extraLen = ReadLe16(headerBuf, 30);
            var commentLen = ReadLe16(headerBuf, 32);
            var localHeaderOffset32 = ReadLe32(headerBuf, 42);

            var fileNameBytes = new byte[fileNameLen];
            if (ReadFully(fs, fileNameBytes, 0, fileNameLen) != fileNameLen) break;
            var fullName = System.Text.Encoding.UTF8.GetString(fileNameBytes);

            long compressedSize = compressedSize32;
            long uncompressedSize = uncompressedSize32;
            long localHeaderOffset = localHeaderOffset32;

            if (extraLen > 0)
            {
                var extraBuf = new byte[extraLen];
                if (ReadFully(fs, extraBuf, 0, extraLen) != extraLen)
                {
                    fs.Seek(commentLen, SeekOrigin.Current);
                    continue;
                }
                ParseZip64Extra(extraBuf, ref compressedSize, ref uncompressedSize, ref localHeaderOffset,
                    compressedSize32 == Zip64Marker32, uncompressedSize32 == Zip64Marker32, localHeaderOffset32 == Zip64Marker32);
                fs.Seek(commentLen, SeekOrigin.Current);
            }
            else
                fs.Seek(commentLen, SeekOrigin.Current);

            if (string.IsNullOrEmpty(fullName) || fullName.EndsWith('/'))
                continue;

            long dataOffset;
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
            dataOffset = localHeaderOffset + 30 + localNameLen + localExtraLen;
            fs.Seek(pos, SeekOrigin.Begin);

            var isStored = compression == CompressionMethodStored;
            if (compression != CompressionMethodStored && compression != CompressionMethodDeflate)
                continue;
            if (compressedSize < 0 || uncompressedSize < 0)
                continue;
            result.Add((fullName, dataOffset, compressedSize, uncompressedSize, isStored));
        }

        return result;
    }

    private static void ParseZip64Extra(byte[] extra, ref long compressedSize, ref long uncompressedSize, ref long localHeaderOffset,
        bool needCompressed, bool needUncompressed, bool needOffset)
    {
        var pos = 0;
        while (pos + 4 <= extra.Length)
        {
            var id = ReadLe16(extra, pos);
            var size = ReadLe16(extra, pos + 2);
            pos += 4;
            if (pos + size > extra.Length) break;
            if (id == Zip64ExtraId && size >= 8)
            {
                var o = 0;
                if (needUncompressed && o + 8 <= size) { uncompressedSize = ReadLe64(extra, pos + o); o += 8; }
                if (needCompressed && o + 8 <= size) { compressedSize = ReadLe64(extra, pos + o); o += 8; }
                if (needOffset && o + 8 <= size) { localHeaderOffset = ReadLe64(extra, pos + o); o += 8; }
                break;
            }
            pos += size;
        }
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
    private static long ReadLe64(byte[] b, int i) => (long)ReadLe32(b, i) | ((long)ReadLe32(b, i + 4) << 32);
}
