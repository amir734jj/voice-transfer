using NAudio.Wave;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Sender mode: reads a file, encodes it as FSK audio, and either
/// plays it through speakers or writes to a WAV file.
/// 
/// In a phone call scenario, the FSK tones play alongside your voice.
/// To the other person (and any eavesdropper) it sounds like background
/// modem noise. But the receiver program can decode it back to data.
/// </summary>
public static class SenderMode
{
    public static void Run(string inputFile, string? outputWav, int deviceIndex, TransmissionProfile profile, string? password = null)
    {
        // 1. Read and validate the input file
        if (!File.Exists(inputFile))
        {
            Log.Error("File not found: {File}", inputFile);
            return;
        }

        var fileData = File.ReadAllBytes(inputFile);
        Log.Information("Read {Bytes} bytes from {File}", fileData.Length, inputFile);

        profile.LogSettings();

        if (fileData.Length > 10_000)
        {
            Log.Warning("Large file ({Bytes} bytes). Transmission will take ~{Seconds}s",
                fileData.Length,
                (int)(fileData.Length * 8 * 1.37 / profile.BaudRate)); // x1.37 for base64 expansion + framing
        }

        // 2. Encode into FSK bit stream
        Log.Information("Encoding: file -> base64 -> {Mode} -> frame -> FSK bits...",
            password != null ? "AES-256-GCM" : "scramble");
        var bits = FrameCodec.Encode(fileData, profile, password);
        Log.Information("Frame: {Bits} bits ({Duration:F1}s at {Baud} baud)",
            bits.Length, (double)bits.Length / profile.BaudRate, profile.BaudRate);

        // 3. Modulate to audio samples
        var modulator = new FskModulator(profile);
        var fskSamples = modulator.ModulateBits(bits);

        // 4. Apply stealth shaping (pink noise + wobble + fade) so it sounds like line static
        var shapedSamples = StealthShaper.Apply(fskSamples, profile);

        // Add comfort noise padding (not silence -- dead silence is suspicious)
        var noiseLevel = profile.Amplitude * 0.25;
        var leadNoise = StealthShaper.GenerateComfortNoise(Constants.LeadingSilence, noiseLevel);
        var trailNoise = StealthShaper.GenerateComfortNoise(Constants.TrailingSilence, noiseLevel);
        var allSamples = FskModulator.Concat(leadNoise, shapedSamples, trailNoise);

        Log.Information("Generated {Samples} audio samples ({Duration:F1}s)",
            allSamples.Length, (double)allSamples.Length / Constants.SampleRate);

        // 4. Output
        if (outputWav != null)
        {
            WriteWav(outputWav, allSamples);
            Log.Information("Written to {File}", outputWav);
        }
        else
        {
            PlayAudio(allSamples, deviceIndex);
        }
    }

    private static void WriteWav(string path, float[] samples)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);
        using var writer = new WaveFileWriter(path, format);

        foreach (var s in samples)
        {
            writer.WriteSample(Math.Clamp(s, -1f, 1f));
        }
    }

    private static void PlayAudio(float[] samples, int deviceIndex)
    {
        Log.Information("Using audio output device index {Index}", deviceIndex);

        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);
        var provider = new BufferedWaveProvider(format)
        {
            BufferLength = samples.Length * 4 + 4096,
            ReadFully = false
        };

        // Write float samples as bytes
        var buffer = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);
        provider.AddSamples(buffer, 0, buffer.Length);

        using var waveOut = new WaveOutEvent
        {
            DeviceNumber = deviceIndex,
            DesiredLatency = 200
        };

        var done = new ManualResetEventSlim(false);
        waveOut.PlaybackStopped += (_, _) => done.Set();

        waveOut.Init(provider);
        waveOut.Play();

        var durationSec = (double)samples.Length / Constants.SampleRate;
        Log.Information("Playing FSK audio ({Duration:F1}s)... Press Ctrl+C to abort.", durationSec);

        // Wait for playback to finish
        done.Wait(TimeSpan.FromSeconds(durationSec + 2));
        Log.Information("Playback complete.");
    }
}
