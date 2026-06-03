using System.Text;
using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
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
        Log.Information("Type text and press Enter to transmit. Ctrl+C to quit");
        Log.Information("");

        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            EnableOutput = true,
            EnableInput = false
        };

        var outputs = OwnaudioNet.GetOutputDevices();
        if (deviceIndex < outputs.Count)
        {
            config.OutputDeviceId = outputs[deviceIndex].DeviceId;
        }

        OwnaudioNet.Initialize(config);
        OwnaudioNet.Start();

        try
        {
            var mixer = new AudioMixer(OwnaudioNet.Engine!.UnderlyingEngine);
            mixer.Start();

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
                    if (line == null)
                    {
                        break; // EOF / Ctrl+C
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

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

                    // Play this message
                    var source = new SampleSource(all, OwnaudioNet.Engine!.Config);
                    mixer.AddSource(source);
                    source.Play();

                    var duration = (double)all.Length / Constants.SampleRate;
                    Log.Information("Sent {Len} bytes ({Bits} bits, {Dur:F1}s audio)",
                        data.Length, bits.Length, duration);

                    // Wait for playback to finish
                    while (!source.IsEndOfStream)
                        Thread.Sleep(50);

                    mixer.RemoveSource(source);
                    source.Dispose();
                }
            }
            finally
            {
                mixer.Stop();
                mixer.Dispose();
            }
        }
        finally
        {
            OwnaudioNet.Shutdown();
        }
    }
}
