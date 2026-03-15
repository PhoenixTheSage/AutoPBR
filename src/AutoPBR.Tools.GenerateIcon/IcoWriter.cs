// Writes ICO file format: header, directory, and DIB image data for each resolution.

namespace AutoPBR.Tools.GenerateIcon;

/// <summary>
/// Writes a multi-resolution ICO file from pre-built DIB buffers.
/// </summary>
internal static class IcoWriter
{
    public static void Write(string path, int[] sizes, List<byte[]> dibs)
    {
        int count = sizes.Length;
        int dirSize = 16 * count;
        int headerSize = 6;
        int dataStart = headerSize + dirSize;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write((short)0);     // reserved
        bw.Write((short)1);     // type (1 = ICO)
        bw.Write((short)count);

        int offset = dataStart;
        for (int i = 0; i < count; i++)
        {
            int w = sizes[i];
            int h = sizes[i];
            if (w == 256) w = 0;
            if (h == 256) h = 0;
            int size = dibs[i].Length;

            bw.Write((byte)w);
            bw.Write((byte)h);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(size);
            bw.Write(offset);

            offset += size;
        }

        foreach (var dib in dibs)
            bw.Write(dib);
    }
}
