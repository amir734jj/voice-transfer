using Ownaudio.Core;
using OwnaudioNET;
using OwnaudioNET.Mixing;
using OwnaudioNET.Sources;
using VoiceTransfer.Data;

namespace VoiceTransfer.Audio;

public sealed class OwnAudioBackend : IAudioBackend
{
    public IReadOnlyList<string> GetInputDeviceNames() =>
        OwnaudioNet.GetInputDevices().Select(d => d.Name).ToList();

    public IReadOnlyList<string> GetOutputDeviceNames() =>
        OwnaudioNet.GetOutputDevices().Select(d => d.Name).ToList();

    public bool SupportsLoopback => false;

    public IAudioPlayer CreatePlayer(int outputDeviceIndex) =>
        new OwnAudioPlayer(outputDeviceIndex);

    public IAudioRecorder CreateRecorder(int inputDeviceIndex) =>
        new OwnAudioRecorder(inputDeviceIndex);

    public IAudioRecorder? CreateLoopbackRecorder() => null;

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex) =>
        new OwnAudioDuplex(inputDeviceIndex, outputDeviceIndex);
}

internal sealed class OwnAudioPlayer : IAudioPlayer
{
    private readonly AudioMixer _mixer;

    public OwnAudioPlayer(int outputDeviceIndex)
    {
        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            EnableOutput = true,
            EnableInput = false
        };

        var outputs = OwnaudioNet.GetOutputDevices();
        if (outputDeviceIndex < outputs.Count)
            config.OutputDeviceId = outputs[outputDeviceIndex].DeviceId;

        OwnaudioNet.Initialize(config);
        OwnaudioNet.Start();

        _mixer = new AudioMixer(OwnaudioNet.Engine!.UnderlyingEngine);
        _mixer.Start();
    }

    public void Play(float[] samples)
    {
        var source = new SampleSource(samples, OwnaudioNet.Engine!.Config);
        _mixer.AddSource(source);
        source.Play();

        while (!source.IsEndOfStream)
            Thread.Sleep(50);

        _mixer.RemoveSource(source);
        source.Dispose();
    }

    public void Dispose()
    {
        _mixer.Stop();
        _mixer.Dispose();
        OwnaudioNet.Shutdown();
    }
}

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
            config.InputDeviceId = inputs[inputDeviceIndex].DeviceId;

        OwnaudioNet.Initialize(config);
        OwnaudioNet.Start();
    }

    public int Read(Span<float> buffer)
    {
        var raw = OwnaudioNet.Receive(out var sampleCount);
        if (raw == null || sampleCount <= 0)
            return 0;

        var count = Math.Min(sampleCount, buffer.Length);
        raw.AsSpan(0, count).CopyTo(buffer);
        OwnaudioNet.ReturnInputBuffer(raw);
        return count;
    }

    public void Dispose() => OwnaudioNet.Shutdown();
}

internal sealed class OwnAudioDuplex : IAudioDuplex
{
    public OwnAudioDuplex(int inputDeviceIndex, int outputDeviceIndex)
    {
        var config = new AudioConfig
        {
            SampleRate = Constants.SampleRate,
            Channels = Constants.Channels,
            EnableOutput = true,
            EnableInput = true
        };

        var inputs = OwnaudioNet.GetInputDevices();
        if (inputDeviceIndex < inputs.Count)
            config.InputDeviceId = inputs[inputDeviceIndex].DeviceId;

        var outputs = OwnaudioNet.GetOutputDevices();
        if (outputDeviceIndex < outputs.Count)
            config.OutputDeviceId = outputs[outputDeviceIndex].DeviceId;

        OwnaudioNet.Initialize(config);
        OwnaudioNet.Start();
    }

    public int Read(Span<float> buffer)
    {
        var raw = OwnaudioNet.Receive(out var sampleCount);
        if (raw == null || sampleCount <= 0)
            return 0;

        var count = Math.Min(sampleCount, buffer.Length);
        raw.AsSpan(0, count).CopyTo(buffer);
        OwnaudioNet.ReturnInputBuffer(raw);
        return count;
    }

    public void Write(ReadOnlySpan<float> buffer) =>
        OwnaudioNet.Send(buffer);

    public void Dispose() => OwnaudioNet.Shutdown();
}
