namespace VoiceTransfer.Interfaces;

/// <summary>
/// Full-duplex audio: captures input and plays output simultaneously.
/// Used for voice passthrough (mic -> speakers) while decoding FSK.
/// </summary>
public interface IAudioDuplex : IDisposable
{
    int Read(Span<float> buffer);
    void Write(ReadOnlySpan<float> buffer);
}