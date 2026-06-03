using Ownaudio.Core;
using OwnaudioNET;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.OwnAudio;

internal sealed class OwnAudioPlayer : IAudioPlayer
{
    private readonly int _chunkSize;
    private readonly int _chunkSleepMs;

    public OwnAudioPlayer(int outputDeviceIndex)
    {
        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            EnableOutput = true,
            EnableInput = false
        };

        OwnaudioNet.Initialize(config);
        var outputs = OwnaudioNet.GetOutputDevices();
        if (outputDeviceIndex < outputs.Count)
        {
            OwnaudioNet.Shutdown();
            config.OutputDeviceId = outputs[outputDeviceIndex].DeviceId;
            OwnaudioNet.Initialize(config);
        }

        OwnaudioNet.Start();

        _chunkSize = config.BufferSize > 0 ? config.BufferSize : 512;
        _chunkSleepMs = Math.Max(1, (int)((double)_chunkSize / Constants.SampleRate * 1000 * 0.8));
    }

    public void Play(float[] samples)
    {
        // Send directly to the audio engine, bypassing the mixer layer.
        // Pace sends to match the hardware consumption rate.
        var offset = 0;
        while (offset < samples.Length)
        {
            var count = Math.Min(_chunkSize, samples.Length - offset);
            OwnaudioNet.Send(samples.AsSpan(offset, count));
            offset += count;
            Thread.Sleep(_chunkSleepMs);
        }

        // Wait for the ring buffer to drain
        var tailMs = (int)((double)_chunkSize * 3 / Constants.SampleRate * 1000);
        Thread.Sleep(tailMs);
    }

    public void Dispose()
    {
        OwnaudioNet.Shutdown();
    }
}
