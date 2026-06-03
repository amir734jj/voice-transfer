using Ownaudio.Core;
using OwnaudioNET;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.OwnAudio;

internal sealed class OwnAudioRecorder : IAudioRecorder
{
    public OwnAudioRecorder(int inputDeviceIndex)
    {
        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            EnableOutput = false,
            EnableInput = true
        };

        var inputs = OwnaudioNet.GetInputDevices();
        if (inputDeviceIndex < inputs.Count)
        {
            config.InputDeviceId = inputs[inputDeviceIndex].DeviceId;
        }

        OwnaudioNet.Initialize(config);
        OwnaudioNet.Start();
    }

    public int Read(Span<float> buffer)
    {
        var raw = OwnaudioNet.Receive(out var sampleCount);
        if (raw == null || sampleCount <= 0)
        {
            return 0;
        }

        var count = Math.Min(sampleCount, buffer.Length);
        raw.AsSpan(0, count).CopyTo(buffer);
        OwnaudioNet.ReturnInputBuffer(raw);
        return count;
    }

    public void Dispose() => OwnaudioNet.Shutdown();
}
