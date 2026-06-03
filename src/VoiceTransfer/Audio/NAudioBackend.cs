using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceTransfer.Data;

namespace VoiceTransfer.Audio;

public sealed class NAudioBackend : IAudioBackend
{
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => d.FriendlyName)
            .ToList();
    }

    public IReadOnlyList<string> GetOutputDeviceNames()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => d.FriendlyName)
            .ToList();
    }

    public bool SupportsLoopback => true;

    public IAudioPlayer CreatePlayer(int outputDeviceIndex) =>
        new NAudioPlayer(outputDeviceIndex);

    public IAudioRecorder CreateRecorder(int inputDeviceIndex) =>
        new NAudioRecorder(inputDeviceIndex);

    public IAudioRecorder CreateLoopbackRecorder() =>
        new NAudioLoopbackRecorder();

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex) =>
        new NAudioDuplex(inputDeviceIndex, outputDeviceIndex);
}

internal sealed class NAudioPlayer : IAudioPlayer
{
    private readonly int _deviceIndex;

    public NAudioPlayer(int deviceIndex) => _deviceIndex = deviceIndex;

    public void Play(float[] samples)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);
        var provider = new FloatArrayProvider(samples, format);

        using var waveOut = new WaveOutEvent { DeviceNumber = _deviceIndex };
        using var done = new ManualResetEventSlim(false);
        waveOut.PlaybackStopped += (_, _) => done.Set();
        waveOut.Init(provider);
        waveOut.Play();
        done.Wait();
    }

    public void Dispose() { }
}

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
                return 0;
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
            if (e.BytesRecorded == 0) return;

            var floats = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);

            if (_sourceChannels > 1)
                floats = DownmixToMono(floats, _sourceChannels);

            if (_sourceRate != Constants.SampleRate)
                floats = Resample(floats, _sourceRate, Constants.SampleRate);

            _chunks.Enqueue(floats);
        };
        _capture.StartRecording();
    }

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

internal sealed class NAudioDuplex : IAudioDuplex
{
    private readonly WaveInEvent _waveIn;
    private readonly WaveOutEvent _waveOut;
    private readonly BufferedWaveProvider _playBuffer;
    private readonly ConcurrentQueue<float[]> _captureChunks = new();
    private float[] _current = [];
    private int _pos;

    public NAudioDuplex(int inputDeviceIndex, int outputDeviceIndex)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Constants.SampleRate, Constants.Channels);

        _waveIn = new WaveInEvent
        {
            DeviceNumber = inputDeviceIndex,
            WaveFormat = format,
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += (_, e) =>
        {
            var floats = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
            _captureChunks.Enqueue(floats);
        };

        _playBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
        _waveOut = new WaveOutEvent { DeviceNumber = outputDeviceIndex };
        _waveOut.Init(_playBuffer);

        _waveIn.StartRecording();
        _waveOut.Play();
    }

    public int Read(Span<float> buffer)
    {
        if (_pos >= _current.Length)
        {
            if (!_captureChunks.TryDequeue(out var next))
                return 0;
            _current = next;
            _pos = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, count).CopyTo(buffer);
        _pos += count;
        return count;
    }

    public void Write(ReadOnlySpan<float> buffer)
    {
        var bytes = new byte[buffer.Length * 4];
        for (var i = 0; i < buffer.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), buffer[i]);
        _playBuffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveOut.Stop();
        _waveOut.Dispose();
    }
}

/// <summary>
/// Simple ISampleProvider wrapping a float[] for NAudio playback.
/// </summary>
internal sealed class FloatArrayProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public FloatArrayProvider(float[] samples, WaveFormat format)
    {
        _samples = samples;
        WaveFormat = format;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var available = Math.Min(count, _samples.Length - _position);
        if (available <= 0) return 0;
        Array.Copy(_samples, _position, buffer, offset, available);
        _position += available;
        return available;
    }
}
