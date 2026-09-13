using global::NAudio.Wave;

namespace VoiceTransfer.Audio.NAudio;

/// <summary>
/// Simple ISampleProvider wrapping a float[] for NAudio playback.
/// </summary>
internal sealed class FloatArrayProvider(float[] samples, WaveFormat format) : ISampleProvider
{
    private int _position;

    public WaveFormat WaveFormat { get; } = format;

    public int Read(float[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    private int Read(Span<float> buffer)
    {
        var available = Math.Min(buffer.Length, samples.Length - _position);
        if (available <= 0)
        {
            return 0;
        }

        samples.AsSpan(_position, available).CopyTo(buffer);
        _position += available;
        return available;
    }
}
