using System.Collections.Concurrent;

namespace VoiceTransfer.Audio;

/// <summary>
/// Thread-safe chunked audio buffer. Producers enqueue float[] chunks;
/// consumers read sequentially via <see cref="Read"/>.
/// Shared by all recorder and duplex implementations to avoid duplication.
/// </summary>
internal sealed class ChunkedAudioBuffer
{
    private readonly ConcurrentQueue<float[]> _chunks = new();
    private float[] _current = [];
    private int _pos;

    public void Enqueue(float[] samples) => _chunks.Enqueue(samples);

    public int Read(Span<float> buffer)
    {
        if (_pos >= _current.Length)
        {
            if (!_chunks.TryDequeue(out var next))
                return 0;
            _current = next;
            _pos = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, count).CopyTo(buffer);
        _pos += count;
        return count;
    }
}
