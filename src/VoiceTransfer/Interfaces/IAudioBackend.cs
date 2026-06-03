namespace VoiceTransfer.Interfaces;

/// <summary>
/// Abstraction over platform-specific audio I/O.
/// Implementations: OwnAudioBackend (cross-platform), NAudioBackend (Windows, with loopback).
/// </summary>
public interface IAudioBackend
{
    IReadOnlyList<string> GetInputDeviceNames();
    IReadOnlyList<string> GetOutputDeviceNames();
    bool SupportsLoopback { get; }

    IAudioPlayer CreatePlayer(int outputDeviceIndex);
    IAudioRecorder CreateRecorder(int inputDeviceIndex);
    IAudioRecorder? CreateLoopbackRecorder();
    IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex);
}