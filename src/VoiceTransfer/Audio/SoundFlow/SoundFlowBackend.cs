using System.Runtime.InteropServices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;
using AudioFormat = SoundFlow.Structs.AudioFormat;

namespace VoiceTransfer.Audio.SoundFlow;

public sealed class SoundFlowBackend : IAudioBackend
{
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        using var engine = new MiniAudioEngine();
        engine.UpdateAudioDevicesInfo();
        return engine.CaptureDevices.Select(d => d.Name).ToList();
    }

    public IReadOnlyList<string> GetOutputDeviceNames()
    {
        using var engine = new MiniAudioEngine();
        engine.UpdateAudioDevicesInfo();
        return engine.PlaybackDevices.Select(d => d.Name).ToList();
    }

    public bool SupportsLoopback => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public IAudioPlayer CreatePlayer(int outputDeviceIndex, bool wasapiOut = false) =>
        new SoundFlowPlayer(outputDeviceIndex);

    public IAudioRecorder CreateRecorder(int inputDeviceIndex) =>
        new SoundFlowRecorder(inputDeviceIndex);

    public IAudioRecorder? CreateLoopbackRecorder()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        return new SoundFlowLoopbackRecorder();
    }

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex) =>
        new SoundFlowDuplex(inputDeviceIndex, outputDeviceIndex);

    internal static AudioFormat MakeFormat() => new()
    {
        SampleRate = Constants.SampleRate,
        Channels = Constants.Channels,
        Format = SampleFormat.F32,
        Layout = ChannelLayout.Mono
    };
}