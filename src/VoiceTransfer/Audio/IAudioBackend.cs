namespace VoiceTransfer.Audio;

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

/// <summary>
/// Plays float[] samples through an audio output device.
/// Supports multiple sequential Play() calls on the same instance.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    void Play(float[] samples);
}

/// <summary>
/// Captures audio samples from an input device (microphone or loopback).
/// Caller polls Read() and accumulates samples.
/// </summary>
public interface IAudioRecorder : IDisposable
{
    int Read(Span<float> buffer);
}

/// <summary>
/// Full-duplex audio: captures input and plays output simultaneously.
/// Used for voice passthrough (mic -> speakers) while decoding FSK.
/// </summary>
public interface IAudioDuplex : IDisposable
{
    int Read(Span<float> buffer);
    void Write(ReadOnlySpan<float> buffer);
}
