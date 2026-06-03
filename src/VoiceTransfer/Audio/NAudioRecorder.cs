using System.Collections.Concurrent;
using NAudio.Wave;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio;

internal sealed class NAudioRecorder : IAudioRecorder
{
    private readonly WaveInEvent _waveIn;
    private readonly ConcurrentQueue<float[]> _chunks = new();
    private float[] _current = [];
    private int _pos;

    public NAudioRecorder(int deviceIndex)
    {
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceIndex,
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += (_, e) =>
        {
            var floats = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
            _chunks.Enqueue(floats);
        };
        _waveIn.StartRecording();
    }

    public int Read(Span<float> buffer)
    {
        if (_pos >= _current.Length)
        {
            if (!_chunks.TryDequeue(out var next))
            {
                return 0;
            }

            _current = next;
            _pos = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, count).CopyTo(buffer);
        _pos += count;
        return count;
    }

    public void Dispose()
    {
        _waveIn.StopRecording();
        _waveIn.Dispose();
    }
}