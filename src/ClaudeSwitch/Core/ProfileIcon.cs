// A launcher icon per profile in the profile's colour, so "Claude (Work)" and
// "Claude (Personal)" look different in the Start menu, Dock and app menus.
//
// Drawn in code, the same design as the app icon (dark tile, switch track,
// knob), with the knob and a ring in the profile's colour. No image library:
// shapes are signed-distance functions with one pixel of anti-aliasing, and the
// PNG, ICO and ICNS containers are written by hand.

using System.IO.Compression;

namespace ClaudeSwitch.Core;

public static class ProfileIcon
{
    const int Size = 256;

    public static string Folder => Path.Combine(Platform.AppDir, "icons");

    /// <summary>Writes the icon files for a colour and returns the one this OS uses.</summary>
    public static string Write(string slug, string hex)
    {
        byte[] png = Png(Render(Parse(hex)));
        Directory.CreateDirectory(Folder);
        string basePath = Path.Combine(Folder, slug + "-" + hex.TrimStart('#').ToLowerInvariant());
        if (Platform.IsWindows) { File.WriteAllBytes(basePath + ".ico", Ico(png)); return basePath + ".ico"; }
        if (Platform.IsMac) { File.WriteAllBytes(basePath + ".icns", Icns(png)); return basePath + ".icns"; }
        File.WriteAllBytes(basePath + ".png", png);
        return basePath + ".png";
    }

    static (float r, float g, float b) Parse(string hex)
    {
        hex = hex.TrimStart('#');
        int v = Convert.ToInt32(hex, 16);
        return (((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
    }

    // ---- drawing ------------------------------------------------------------------

    static byte[] Render((float r, float g, float b) accent)
    {
        var px = new float[Size * Size * 4];   // premultiplied RGBA, 0..1

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                int i = (y * Size + x) * 4;
                // Tile: rounded square, faint vertical gradient.
                float tile = Cover(RoundBox(fx, fy, 128, 128, 120, 120, 54));
                float shade = 0.16f - 0.05f * (fy / Size);
                Blend(px, i, shade, shade + 0.01f, shade + 0.025f, tile);
                // Ring in the profile colour, just inside the tile edge.
                float ring = Cover(MathF.Abs(RoundBox(fx, fy, 128, 128, 114, 114, 48)) - 4f);
                Blend(px, i, accent.r, accent.g, accent.b, ring * tile);
                // Switch track.
                float track = Cover(RoundBox(fx, fy, 128, 128, 84, 40, 40));
                Blend(px, i, 0.26f, 0.28f, 0.32f, track);
                // Two status dots on the left of the track.
                Blend(px, i, 0.39f, 0.84f, 0.58f, Cover(Circle(fx, fy, 70, 128, 9)));
                Blend(px, i, 0.95f, 0.76f, 0.31f, Cover(Circle(fx, fy, 96, 128, 9)));
                // The knob, in the profile colour, with a soft lighter top.
                float knob = Cover(Circle(fx, fy, 170, 128, 32));
                float lift = 1f + 0.18f * Math.Clamp((128 - fy) / 32f, 0, 1);
                Blend(px, i, Math.Min(1, accent.r * lift), Math.Min(1, accent.g * lift), Math.Min(1, accent.b * lift), knob);
            }

        var rgba = new byte[Size * Size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            float a = px[i + 3];
            rgba[i + 3] = (byte)Math.Round(a * 255);
            for (int c = 0; c < 3; c++) rgba[i + c] = a <= 0 ? (byte)0 : (byte)Math.Round(Math.Clamp(px[i + c] / a, 0, 1) * 255);
        }
        return rgba;
    }

    static float Cover(float distance) => Math.Clamp(0.5f - distance, 0, 1);

    static float Circle(float x, float y, float cx, float cy, float r) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

    static float RoundBox(float x, float y, float cx, float cy, float hw, float hh, float r)
    {
        float qx = MathF.Abs(x - cx) - (hw - r), qy = MathF.Abs(y - cy) - (hh - r);
        float ox = MathF.Max(qx, 0), oy = MathF.Max(qy, 0);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0) - r;
    }

    // "Over" compositing of a solid colour with coverage a onto premultiplied pixels.
    static void Blend(float[] px, int i, float r, float g, float b, float a)
    {
        if (a <= 0) return;
        px[i] = r * a + px[i] * (1 - a);
        px[i + 1] = g * a + px[i + 1] * (1 - a);
        px[i + 2] = b * a + px[i + 2] * (1 - a);
        px[i + 3] = a + px[i + 3] * (1 - a);
    }

    // ---- containers ---------------------------------------------------------------

    public static byte[] Png(byte[] rgba)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < Size; y++)
        {
            raw.WriteByte(0);   // filter: none
            raw.Write(rgba, y * Size * 4, Size * 4);
        }
        byte[] deflated;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(zs);
            deflated = z.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, Size);
        BigEndian(ihdr, 4, Size);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", deflated);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    // One 256x256 PNG entry; Windows Vista+ reads PNG inside ICO.
    public static byte[] Ico(byte[] png)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)1);            // reserved, type icon, one image
        w.Write((byte)0); w.Write((byte)0);                                   // 0 = 256 px
        w.Write((byte)0); w.Write((byte)0);                                   // palette, reserved
        w.Write((short)1); w.Write((short)32);                                // planes, bits
        w.Write(png.Length); w.Write(22);                                     // size, offset
        w.Write(png);
        return ms.ToArray();
    }

    // "ic08" is the 256x256 PNG slot.
    public static byte[] Icns(byte[] png)
    {
        var b = new byte[8 + 8 + png.Length];
        "icns"u8.CopyTo(b);
        BigEndian(b, 4, b.Length);
        "ic08"u8.CopyTo(b.AsSpan(8));
        BigEndian(b, 12, 8 + png.Length);
        png.CopyTo(b, 16);
        return b;
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BigEndian(len, 0, data.Length);
        s.Write(len);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        var c = new byte[4];
        BigEndian(c, 0, (int)crc);
        s.Write(c);
    }

    static void BigEndian(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(uint crc, byte[] data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
