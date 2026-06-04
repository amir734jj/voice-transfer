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
        var available = Math.Min(count, samples.Length - _position);
        if (available <= 0)
        {
            return 0;
        }

        Buffer.BlockCopy(samples, _position * sizeof(float), buffer, offset * sizeof(float), available * sizeof(float));
        _position += available;
        return available;
    }
}
