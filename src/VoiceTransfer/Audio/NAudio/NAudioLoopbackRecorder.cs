using global::NAudio.CoreAudioApi;
using global::NAudio.Wave;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

internal sealed class NAudioLoopbackRecorder : IAudioRecorder
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly ChunkedAudioBuffer _buffer = new();
    private readonly int _sourceRate;
    private readonly int _sourceChannels;

    public NAudioLoopbackRecorder()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Log.Information("WASAPI loopback device: {Name}", device.FriendlyName);

        _capture = new WasapiLoopbackCapture(device);
        _sourceRate = _capture.WaveFormat.SampleRate;
        _sourceChannels = _capture.WaveFormat.Channels;

        Log.Information("WASAPI loopback format: {Rate}Hz, {Ch}ch, {Enc}",
            _sourceRate, _sourceChannels, _capture.WaveFormat.Encoding);

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;

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

            _buffer.Enqueue(floats);
        };
        _capture.StartRecording();
    }

    public int Read(Span<float> buffer) => _buffer.Read(buffer);

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
