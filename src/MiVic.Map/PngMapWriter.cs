using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MiVic.Map;

/// <summary>
/// A PNG, written by hand: signature, header, one image-data chunk, end.
/// <para>
/// <b>Why this exists at all.</b> The SVG is the format worth having — exact, diffable, and a
/// plain writer — but half the readers of this tool cannot open one. A vision model that reviews
/// pictures reads PNG, JPEG, WebP and GIF, and a diagnostic whose output nobody can look at has
/// failed at the one thing it was built for. So the raster is not a second picture with its own
/// opinion: it is the same scene through a second encoder, and the two are the same numbers.
/// </para>
/// <para>
/// <b>Why not a converter.</b> The repository already manipulates images — <c>tools/crop.ps1</c>
/// loads a screenshot through <c>System.Drawing</c> and saves a PNG — but <c>System.Drawing</c>
/// decodes and re-encodes a bitmap; it cannot rasterise a vector document. Converting would mean
/// an SVG renderer, which is either a native dependency or a rasteriser written from scratch
/// anyway. The encoder is thirty lines: a PNG is a header, rows of bytes with a filter byte in
/// front of each, and a zlib stream, and .NET has had <see cref="ZLibStream"/> since .NET 6.
/// </para>
/// <para>
/// <b>The one caveat, said out loud.</b> The pixels are a pure function of the scene, but the
/// bytes of the compressed image data are the runtime's zlib and not this writer's — so
/// byte-identity is a property of a build, which is what the determinism test asserts, while the
/// SVG is the format whose bytes are this writer's own and therefore the format to diff across
/// machines.
/// </para>
/// </summary>
public static class PngMapWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes a canvas as a PNG and writes it to a file.</summary>
    public static void Write(RasterCanvas canvas, string path)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(full, Encode(canvas));
    }

    /// <summary>Paints a scene and encodes the result, for a caller that wants the bytes.</summary>
    public static byte[] Render(MapScene scene) => Encode(RasterCanvas.Paint(scene));

    /// <summary>The PNG bytes of a canvas: 8-bit RGBA, no interlacing, filter zero on every row.</summary>
    public static byte[] Encode(RasterCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        using var stream = new MemoryStream(canvas.Width * canvas.Height);
        stream.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], canvas.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..8], canvas.Height);
        header[8] = 8;      // Bits per channel.
        header[9] = 6;      // Colour type 6: truecolour with alpha.
        header[10] = 0;     // Compression: deflate, which is the only one PNG has.
        header[11] = 0;     // Filter method 0.
        header[12] = 0;     // Not interlaced.

        Chunk(stream, "IHDR", header);

        // Filter zero on every row. The adaptive filters exist to help a compressor find runs in
        // photographic data; this picture is flat colour blocks and one-pixel marks, where the
        // chooser costs more code than it saves bytes, and a row that is always filtered the same
        // way is a row whose decoder is trivially checkable.
        byte[] raw = new byte[(canvas.Width * 4 + 1) * canvas.Height];
        ReadOnlySpan<byte> pixels = canvas.Pixels;
        int stride = canvas.Width * 4;

        for (int row = 0; row < canvas.Height; row++)
        {
            raw[(row * (stride + 1))] = 0;
            pixels.Slice(row * stride, stride).CopyTo(raw.AsSpan((row * (stride + 1)) + 1, stride));
        }

        Chunk(stream, "IDAT", Deflate(raw));
        Chunk(stream, "IEND", ReadOnlySpan<byte>.Empty);

        return stream.ToArray();
    }

    /// <summary>Zlib, which is what a PNG's image data is: a two-byte header, deflate, an Adler sum.</summary>
    private static byte[] Deflate(byte[] raw)
    {
        using var compressed = new MemoryStream(raw.Length / 2);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return compressed.ToArray();
    }

    /// <summary>Writes a chunk: its length, its type, its data, and the CRC of type and data together.</summary>
    private static void Chunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> tag = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, tag);
        stream.Write(tag);
        stream.Write(data);

        uint crc = Crc(tag, data);
        Span<byte> sum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sum, crc);
        stream.Write(sum);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFF;

        foreach (byte value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFF_FFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
