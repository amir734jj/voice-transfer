using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Modes;

/// <summary>
/// Tests audio devices by listing them, playing a test tone through the speaker,
/// and recording from the microphone to verify levels.
/// </summary>
public static class TestMode
{
    public static void Run(IAudioBackend audio, int inputDeviceIndex, int outputDeviceIndex, double durationSeconds)
    {
        ListDevices(audio);
        TestSpeaker(audio, outputDeviceIndex, durationSeconds);
        TestMicrophone(audio, inputDeviceIndex, durationSeconds);

        Log.Information("");
        Log.Information("Audio test complete");
    }

    private static void ListDevices(IAudioBackend audio)
    {
        Log.Information("=== Audio Devices ===");

        var inputs = audio.GetInputDeviceNames();
        Log.Information("Input devices ({Count}):", inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
            Log.Information("  [{Index}] {Name}", i, inputs[i]);

        var outputs = audio.GetOutputDeviceNames();
        Log.Information("Output devices ({Count}):", outputs.Count);
        for (var i = 0; i < outputs.Count; i++)
            Log.Information("  [{Index}] {Name}", i, outputs[i]);

        Log.Information("Loopback supported: {Supported}", audio.SupportsLoopback);
        Log.Information("");
    }

    private static void TestSpeaker(IAudioBackend audio, int deviceIndex, double durationSeconds)
    {
        Log.Information("=== Speaker Test (device {Index}) ===", deviceIndex);
        Log.Information("Playing {Duration:F1}s test tone (440 Hz sine wave)", durationSeconds);

        var sampleCount = (int)(Constants.SampleRate * durationSeconds);
        var samples = new float[sampleCount];

        // Generate a 440 Hz sine wave with fade in/out
        const double freq = 440.0;
        var fadeLength = Math.Min(sampleCount / 4, Constants.SampleRate / 10); // 100ms fade

        for (var i = 0; i < sampleCount; i++)
        {
            var t = (double)i / Constants.SampleRate;
            var sample = (float)(0.3 * Math.Sin(2 * Math.PI * freq * t));

            // Fade in
            if (i < fadeLength)
            {
                sample *= (float)i / fadeLength;
            }
            // Fade out
            else if (i > sampleCount - fadeLength)
            {
                sample *= (float)(sampleCount - i) / fadeLength;
            }

            samples[i] = sample;
        }

        using var player = audio.CreatePlayer(deviceIndex);
        player.Play(samples);

        Log.Information("Speaker test done. Did you hear the tone?");
        Log.Information("");
    }

    private static void TestMicrophone(IAudioBackend audio, int deviceIndex, double durationSeconds)
    {
        Log.Information("=== Microphone Test (device {Index}) ===", deviceIndex);
        Log.Information("Recording {Duration:F1}s from microphone", durationSeconds);

        using var recorder = audio.CreateRecorder(deviceIndex);

        var totalSamples = (int)(Constants.SampleRate * durationSeconds);
        var buffer = new float[4096];
        var samplesRead = 0;
        var peakLevel = 0f;
        var sumSquares = 0.0;

        while (samplesRead < totalSamples)
        {
            var count = recorder.Read(buffer);
            if (count == 0)
            {
                Thread.Sleep(10);
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var abs = Math.Abs(buffer[i]);
                if (abs > peakLevel)
                {
                    peakLevel = abs;
                }

                sumSquares += buffer[i] * buffer[i];
            }

            samplesRead += count;
        }

        var rmsLevel = Math.Sqrt(sumSquares / Math.Max(1, samplesRead));
        var peakDb = peakLevel > 0 ? 20 * Math.Log10(peakLevel) : -100;
        var rmsDb = rmsLevel > 0 ? 20 * Math.Log10(rmsLevel) : -100;

        Log.Information("Recorded {Samples} samples", samplesRead);
        Log.Information("Peak level: {Peak:F4} ({PeakDb:F1} dB)", peakLevel, peakDb);
        Log.Information("RMS level:  {Rms:F4} ({RmsDb:F1} dB)", (float)rmsLevel, rmsDb);

        if (peakLevel < 0.001f)
        {
            Log.Warning("Microphone appears silent - check that the correct device is selected and not muted");
        }
        else if (peakLevel < 0.01f)
        {
            Log.Warning("Microphone level is very low - you may need to increase the input volume");
        }
        else
        {
            Log.Information("Microphone is working");
        }

        Log.Information("");
    }
}
