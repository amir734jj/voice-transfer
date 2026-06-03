using NAudio.CoreAudioApi;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

public sealed class NAudioBackend : IAudioBackend
{
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => d.FriendlyName)
            .ToList();
    }

    public IReadOnlyList<string> GetOutputDeviceNames()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => d.FriendlyName)
            .ToList();
    }

    public bool SupportsLoopback => true;

    public IAudioPlayer CreatePlayer(int outputDeviceIndex) =>
        new NAudioPlayer(outputDeviceIndex);

    public IAudioRecorder CreateRecorder(int inputDeviceIndex) =>
        new NAudioRecorder(inputDeviceIndex);

    public IAudioRecorder CreateLoopbackRecorder() =>
        new NAudioLoopbackRecorder();

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex) =>
        new NAudioDuplex(inputDeviceIndex, outputDeviceIndex);
}
