using System.Diagnostics;
using Ownaudio.Core;
using OwnaudioNET;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.OwnAudio;

internal sealed class OwnAudioPlayer : IAudioPlayer
{
    private const int BufferSize = 512;
    private const int BufferMultiplier = 16;
    private const int MaxAheadSamples = BufferSize * (BufferMultiplier - 2);

    public OwnAudioPlayer(int outputDeviceIndex)
    {
        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            BufferSize = BufferSize,
            EnableOutput = true,
            EnableInput = false
        };

        OwnaudioNet.Initialize(config, bufferMultiplier: BufferMultiplier);
        var outputs = OwnaudioNet.GetOutputDevices();
        if (outputDeviceIndex < outputs.Count)
        {
            OwnaudioNet.Shutdown();
            config.OutputDeviceId = outputs[outputDeviceIndex].DeviceId;
            OwnaudioNet.Initialize(config, bufferMultiplier: BufferMultiplier);
        }

        OwnaudioNet.Start();
    }

    public void Play(float[] samples)
    {
        var offset = 0;
        var sw = Stopwatch.StartNew();
        var samplesPerMs = (double)Constants.SampleRate / 1000.0;

        while (offset < samples.Length)
        {
            var consumed = sw.Elapsed.TotalMilliseconds * samplesPerMs;
            var ahead = offset - consumed;

            if (ahead >= MaxAheadSamples)
            {
                Thread.Sleep(Math.Max(1, (int)((ahead - MaxAheadSamples / 2) / samplesPerMs)));
                continue;
            }

            var count = Math.Min(BufferSize, samples.Length - offset);
            OwnaudioNet.Send(samples.AsSpan(offset, count));
            offset += count;
        }

        // Wait for remaining samples to play out
        var totalMs = samples.Length / samplesPerMs;
        var remainingMs = totalMs - sw.Elapsed.TotalMilliseconds;
        if (remainingMs > 0)
            Thread.Sleep((int)remainingMs + 50);
    }

    public void Dispose()
    {
        OwnaudioNet.Shutdown();
    }
}
