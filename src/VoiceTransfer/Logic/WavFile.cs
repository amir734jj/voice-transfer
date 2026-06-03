namespace VoiceTransfer.Logic;

/// <summary>
/// Minimal WAV file reader/writer for IEEE float and PCM formats.
/// Handles mono/stereo, 16/24/32-bit PCM, and 32-bit float.
/// </summary>
public static class WavFile
{
    public static void Write(string path, float[] samples, int sampleRate, int channels)
    {
        const int bitsPerSample = 32;
        const int bytesPerSample = bitsPerSample / 8;
        var dataSize = samples.Length * bytesPerSample;

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);

        // fmt chunk (IEEE float)
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)3); // IEEE float
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * bytesPerSample);
        bw.Write((short)(channels * bytesPerSample));
        bw.Write((short)bitsPerSample);

        // data chunk
        bw.Write("data"u8);
        bw.Write(dataSize);

        foreach (var s in samples)
            bw.Write(Math.Clamp(s, -1f, 1f));
    }

    public static float[] Read(string path, out int sampleRate, out int channels)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        // RIFF header
        br.ReadBytes(4); // "RIFF"
        br.ReadInt32();   // file size
        br.ReadBytes(4); // "WAVE"

        sampleRate = 0;
        channels = 0;
        short bitsPerSample = 0;
        short audioFormat = 0;

        while (fs.Position < fs.Length)
        {
            var chunkId = new string(br.ReadChars(4));
            var chunkSize = br.ReadInt32();

            switch (chunkId)
            {
                case "fmt ":
                    audioFormat = br.ReadInt16();
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byte rate
                    br.ReadInt16(); // block align
                    bitsPerSample = br.ReadInt16();
                    if (chunkSize > 16)
                        br.ReadBytes(chunkSize - 16);
                    break;

                case "data":
                {
                    float[] samples;

                    if (audioFormat == 3 && bitsPerSample == 32) // IEEE float
                    {
                        samples = new float[chunkSize / 4];
                        for (var i = 0; i < samples.Length; i++)
                            samples[i] = br.ReadSingle();
                    }
                    else if (audioFormat == 1 && bitsPerSample == 16) // PCM 16-bit
                    {
                        samples = new float[chunkSize / 2];
                        for (var i = 0; i < samples.Length; i++)
                            samples[i] = br.ReadInt16() / 32768f;
                    }
                    else if (audioFormat == 1 && bitsPerSample == 24) // PCM 24-bit
                    {
                        samples = new float[chunkSize / 3];
                        for (var i = 0; i < samples.Length; i++)
                        {
                            var b = br.ReadBytes(3);
                            var value = b[0] | (b[1] << 8) | ((sbyte)b[2] << 16);
                            samples[i] = value / 8388608f;
                        }
                    }
                    else
                    {
                        throw new NotSupportedException(
                            $"Unsupported WAV format: audioFormat={audioFormat}, bitsPerSample={bitsPerSample}");
                    }

                    // Convert stereo to mono (average L+R)
                    if (channels == 2)
                    {
                        var mono = new float[samples.Length / 2];
                        for (var i = 0; i < mono.Length; i++)
                            mono[i] = (samples[i * 2] + samples[i * 2 + 1]) * 0.5f;
                        channels = 1;
                        return mono;
                    }

                    return samples;
                }

                default:
                    br.ReadBytes(chunkSize);
                    break;
            }
        }

        return [];
    }
}
