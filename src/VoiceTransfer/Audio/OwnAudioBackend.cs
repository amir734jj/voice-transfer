using OwnaudioNET;
using VoiceTransfer.Interfaces;

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