namespace VoiceTransfer.Interfaces;

/// <summary>
/// Plays float[] samples through an audio output device.
/// Supports multiple sequential Play() calls on the same instance.
/// </summary>
public interface IAudioPlayer : IDisposable
{
    void Play(float[] samples);
}