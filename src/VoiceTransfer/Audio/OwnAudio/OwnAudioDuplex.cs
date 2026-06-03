using Ownaudio.Core;
using OwnaudioNET;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.OwnAudio;

internal sealed class OwnAudioDuplex : IAudioDuplex
{
    public OwnAudioDuplex(int inputDeviceIndex, int outputDeviceIndex)
    {
        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            BufferSize = 512,
            EnableOutput = true,
            EnableInput = true
        };

        OwnaudioNet.Initialize(config, bufferMultiplier: 16);
        var inputs = OwnaudioNet.GetInputDevices();
        var outputs = OwnaudioNet.GetOutputDevices();
        var needReinit = false;

        if (inputDeviceIndex < inputs.Count)
        {
            config.InputDeviceId = inputs[inputDeviceIndex].DeviceId;
            needReinit = true;
        }

        if (outputDeviceIndex < outputs.Count)
        {
            config.OutputDeviceId = outputs[outputDeviceIndex].DeviceId;
            needReinit = true;
        }

        if (needReinit)
        {
            OwnaudioNet.Shutdown();
            OwnaudioNet.Initialize(config, bufferMultiplier: 16);
        }

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

    public void Write(ReadOnlySpan<float> buffer) =>
        OwnaudioNet.Send(buffer);

    public void Dispose() => OwnaudioNet.Shutdown();
}
