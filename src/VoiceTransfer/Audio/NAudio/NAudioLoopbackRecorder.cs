using System.Collections.Concurrent;
using global::NAudio.Wave;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

internal sealed class NAudioLoopbackRecorder : IAudioRecorder
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly ConcurrentQueue<float[]> _chunks = new();
    private float[] _current = [];
    private int _pos;
    private readonly int _sourceRate;
    private readonly int _sourceChannels;

    public NAudioLoopbackRecorder()
    {
        _capture = new WasapiLoopbackCapture();
        _sourceRate = _capture.WaveFormat.SampleRate;
        _sourceChannels = _capture.WaveFormat.Channels;

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0)
            {
                return;
            }

            var floats = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);

            if (_sourceChannels > 1)
            {
                floats = DownmixToMono(floats, _sourceChannels);
            }

            if (_sourceRate != Constants.SampleRate)
            {
                floats = Resample(floats, _sourceRate, Constants.SampleRate);
            }

            _chunks.Enqueue(floats);
        };
        _capture.StartRecording();
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
        _capture.StopRecording();
        _capture.Dispose();
    }

    private static float[] DownmixToMono(float[] samples, int channels)
    {
        var mono = new float[samples.Length / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0f;
            for (var ch = 0; ch < channels; ch++)
                sum += samples[i * channels + ch];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        var ratio = (double)fromRate / toRate;
        var outputLen = (int)(input.Length / ratio);
        var output = new float[outputLen];
        for (var i = 0; i < outputLen; i++)
        {
            var srcPos = i * ratio;
            var idx = (int)srcPos;
            var frac = (float)(srcPos - idx);
            output[i] = idx + 1 < input.Length
                ? input[idx] * (1 - frac) + input[idx + 1] * frac
                : input[idx];
        }
        return output;
    }
}
