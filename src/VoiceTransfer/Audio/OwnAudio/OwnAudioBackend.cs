using OwnaudioNET;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.OwnAudio;

public sealed class OwnAudioBackend : IAudioBackend
{
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        OwnaudioNet.Initialize(new Ownaudio.Core.AudioConfig());
        var names = OwnaudioNet.GetInputDevices().Select(d => d.Name).ToList();
        OwnaudioNet.Shutdown();
        return names;
    }

    public IReadOnlyList<string> GetOutputDeviceNames()
    {
        OwnaudioNet.Initialize(new Ownaudio.Core.AudioConfig());
        var names = OwnaudioNet.GetOutputDevices().Select(d => d.Name).ToList();
        OwnaudioNet.Shutdown();
        return names;
    }

    public bool SupportsLoopback => false;

    public IAudioPlayer CreatePlayer(int outputDeviceIndex) =>
        new OwnAudioPlayer(outputDeviceIndex);

    public IAudioRecorder CreateRecorder(int inputDeviceIndex) =>
        new OwnAudioRecorder(inputDeviceIndex);

    public IAudioRecorder? CreateLoopbackRecorder() => null;

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex) =>
        new OwnAudioDuplex(inputDeviceIndex, outputDeviceIndex);
}
