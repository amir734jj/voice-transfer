namespace VoiceTransfer.Interfaces;

/// <summary>
/// Captures audio samples from an input device (microphone or loopback).
/// Caller polls Read() and accumulates samples.
/// </summary>
public interface IAudioRecorder : IDisposable
{
    int Read(Span<float> buffer);
}