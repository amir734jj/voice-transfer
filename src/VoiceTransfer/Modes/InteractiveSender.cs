using System.Text;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Interactive sender: reads lines from the console and transmits each one
/// as a separate FSK frame through the audio output in real-time.
/// Each line is encoded, modulated, stealth-shaped, and played immediately.
/// </summary>
public static class InteractiveSender
{
    public static void Run(IAudioBackend audio, int deviceIndex, TransmissionProfile profile, string? password = null, bool wasapiOut = false)
    {
        profile.LogSettings();

        var effectiveBaud = (double)profile.BaudRate / profile.FecRepeat;
        var bytesPerSec = effectiveBaud / 8 / 1.37;
        Log.Information("Interactive sender ready (~{Rate:F1} bytes/sec effective)", bytesPerSec);
        Log.Information("Type text and press Enter to transmit. Up/Down for history. Ctrl+C to quit");
        Log.Information("");

        using var player = audio.CreatePlayer(deviceIndex, wasapiOut);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = false;
            Log.Information("Shutting down sender...");
        };

        ReadLine.HistoryEnabled = true;

        while (true)
        {
            var line = ReadLine.Read("> ");
            if (line == null)
            {
                break; // EOF / Ctrl+C
            }

            if (line.Length == 0)
            {
                continue;
            }

            ReadLine.AddHistory(line);

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

            var duration = (double)all.Length / Constants.SampleRate;
            Log.Information("Sent {Len} bytes ({Bits} bits, {Dur:F1}s audio)",
                data.Length, bits.Length, duration);

            player.Play(all);
        }
    }
}
