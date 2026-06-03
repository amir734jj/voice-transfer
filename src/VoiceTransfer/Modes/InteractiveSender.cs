using System.Text;
using NAudio.Wave;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Interactive sender: reads lines from the console and transmits each one
/// as a separate FSK frame through the audio output in real-time.
/// Each line is encoded, modulated, stealth-shaped, and played immediately.
/// </summary>
public static class InteractiveSender
{
    public static void Run(int deviceIndex, TransmissionProfile profile, string? password = null)
    {
        profile.LogSettings();

        var effectiveBaud = (double)profile.BaudRate / profile.FecRepeat;
        var bytesPerSec = effectiveBaud / 8 / 1.37;
        Log.Information("Interactive sender ready (~{Rate:F1} bytes/sec effective)", bytesPerSec);
        Log.Information("Type text and press Enter to transmit. Ctrl+C to quit.");
        Log.Information("");

        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);

        using var waveOut = new WaveOutEvent
        {
            DeviceNumber = deviceIndex,
            DesiredLatency = 200
        };

        // Use a BufferedWaveProvider so we can queue audio for each line
        var provider = new BufferedWaveProvider(format)
        {
            BufferLength = Constants.SampleRate * 4 * 120, // 120s buffer
            ReadFully = true, // return silence when empty so WaveOutEvent stays alive
            DiscardOnBufferOverflow = true
        };

        waveOut.Init(provider);
        waveOut.Play();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = false;
            Log.Information("Shutting down sender...");
        };

        try
        {
            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine();
                if (line == null) break; // EOF / Ctrl+C
                if (line.Length == 0) continue;

                var data = Encoding.UTF8.GetBytes(line);

                // Encode -> modulate -> stealth shape
                var bits = FrameCodec.Encode(data, profile, password);
                var modulator = new FskModulator(profile);
                var fskSamples = modulator.ModulateBits(bits);
                var shaped = StealthShaper.Apply(fskSamples, profile);

                // Small comfort noise gap between messages (silence would be conspicuous)
                var noiseLevel = profile.Amplitude * 0.25;
                var gap = StealthShaper.GenerateComfortNoise(0.15, noiseLevel);
                var all = FskModulator.Concat(gap, shaped, gap);

                // Queue for playback
                var buffer = new byte[all.Length * 4];
                Buffer.BlockCopy(all, 0, buffer, 0, buffer.Length);
                provider.AddSamples(buffer, 0, buffer.Length);

                var duration = (double)all.Length / Constants.SampleRate;
                Log.Information("Sent {Len} bytes ({Bits} bits, {Dur:F1}s audio)",
                    data.Length, bits.Length, duration);

                // Wait for this chunk to finish playing before accepting next line
                while (provider.BufferedBytes > 0)
                    Thread.Sleep(50);
            }
        }
        finally
        {
            waveOut.Stop();
        }
    }
}
