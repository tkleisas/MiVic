namespace MiVic.Audio;

/// <summary>
/// Writes 16-bit PCM WAV files.
/// <para>
/// A hand-written header is all that stands between the generator and a file any
/// player can open, which matters because the only way to judge game music is to
/// hear it. <c>--render-audio</c> uses this to export one file per faction.
/// </para>
/// </summary>
public static class WavWriter
{
    /// <summary>Serialises mono 16-bit PCM as a WAV byte array.</summary>
    public static byte[] ToBytes(short[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);

        int dataBytes = samples.Length * sizeof(short);
        byte[] file = new byte[44 + dataBytes];

        WriteAscii(file, 0, "RIFF");
        WriteInt32(file, 4, 36 + dataBytes);
        WriteAscii(file, 8, "WAVE");

        WriteAscii(file, 12, "fmt ");
        WriteInt32(file, 16, 16);            // PCM chunk size
        WriteInt16(file, 20, 1);             // format: PCM
        WriteInt16(file, 22, 1);             // channels: mono
        WriteInt32(file, 24, sampleRate);
        WriteInt32(file, 28, sampleRate * sizeof(short)); // byte rate
        WriteInt16(file, 32, sizeof(short)); // block align
        WriteInt16(file, 34, 16);            // bits per sample

        WriteAscii(file, 36, "data");
        WriteInt32(file, 40, dataBytes);

        Buffer.BlockCopy(samples, 0, file, 44, dataBytes);
        return file;
    }

    /// <summary>Writes mono 16-bit PCM to a file.</summary>
    public static void Write(string path, short[] samples, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllBytes(path, ToBytes(samples, sampleRate));
    }

    /// <summary>Reads a mono 16-bit PCM WAV back, for verification.</summary>
    public static short[] Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            return ReadCore(stream);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("WAV file is truncated.", exception);
        }
    }

    private static short[] ReadCore(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();

        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int channels = 1;
        int bitsPerSample = 16;

        // Walk the chunks rather than assuming the canonical 44-byte layout.
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(reader.ReadChars(4));
            int size = reader.ReadInt32();

            if (id == "fmt ")
            {
                reader.ReadInt16();                  // format
                channels = reader.ReadInt16();
                reader.ReadInt32();                  // sample rate
                reader.ReadInt32();                  // byte rate
                reader.ReadInt16();                  // block align
                bitsPerSample = reader.ReadInt16();
                stream.Position += size - 16;
            }
            else if (id == "data")
            {
                byte[] data = reader.ReadBytes(size);
                short[] samples = new short[data.Length / sizeof(short)];
                Buffer.BlockCopy(data, 0, samples, 0, samples.Length * sizeof(short));

                if (bitsPerSample != 16)
                {
                    throw new InvalidDataException($"Only 16-bit audio is supported, got {bitsPerSample}.");
                }

                if (channels != 1)
                {
                    throw new InvalidDataException($"Only mono audio is supported, got {channels} channels.");
                }

                return samples;
            }
            else
            {
                stream.Position += size + (size % 2);
            }
        }

        throw new InvalidDataException("No data chunk found.");
    }

    /// <summary>Reads a mono 16-bit PCM WAV from a file.</summary>
    public static short[] Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    private static void WriteAscii(byte[] target, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            target[offset + i] = (byte)text[i];
        }
    }

    private static void WriteInt32(byte[] target, int offset, int value)
    {
        target[offset + 0] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] target, int offset, short value)
    {
        target[offset + 0] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
    }
}
