using System.Runtime.InteropServices;
using PortAudioSharp;
using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.PortAudio;

internal sealed class PortAudioRecorder : IAudioRecorder
{
    private readonly PortAudioSharp.Stream _stream;
    private readonly ChunkedAudioBuffer _buffer = new();
    private double _sourceSampleRate = Constants.SampleRate;
    private StreamingLinearResampler? _resampler;

    public PortAudioRecorder(int deviceIndex)
    {
        var info = PortAudioSharp.PortAudio.GetDeviceInfo(deviceIndex);
        var param = new StreamParameters
        {
            device = deviceIndex,
            channelCount = Constants.Channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        var openResult = SampleRateFallback.Open(
            Constants.SampleRate,
            info.defaultSampleRate,
            sampleRate => CreateStream(param, sampleRate),
            exception => exception is PortAudioException);
        _stream = openResult.Value;

        if (Math.Abs(openResult.SampleRate - Constants.SampleRate) > 0.5)
        {
            _sourceSampleRate = openResult.SampleRate;
            _resampler = new StreamingLinearResampler(_sourceSampleRate, Constants.SampleRate);
            Log.Warning(
                "Input device does not support {RequestedRate} Hz; using {DeviceRate} Hz and resampling",
                Constants.SampleRate, _sourceSampleRate);
        }

        _stream.Start();

        PortAudioSharp.Stream CreateStream(StreamParameters parameters, double sampleRate) =>
            new(
                inParams: parameters, outParams: null,
                sampleRate: sampleRate,
                framesPerBuffer: 0,
                streamFlags: StreamFlags.ClipOff,
                callback: Callback,
                userData: IntPtr.Zero);

        StreamCallbackResult Callback(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
        {
            var count = (int)frameCount;
            var samples = new float[count];
            Marshal.Copy(input, samples, 0, count);

            if (_resampler != null)
            {
                samples = _resampler.Process(samples);
            }

            _buffer.Enqueue(samples);
            return StreamCallbackResult.Continue;
        }
    }

    public int Read(Span<float> buffer)
    {
        return _buffer.Read(buffer);
    }

    public void Dispose()
    {
        _stream.Stop();
        _stream.Dispose();
    }
}