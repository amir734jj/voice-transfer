using System.Runtime.InteropServices;
using PortAudioSharp;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.PortAudio;

internal sealed class PortAudioRecorder : IAudioRecorder
{
    private readonly PortAudioSharp.Stream _stream;
    private readonly ChunkedAudioBuffer _buffer = new();

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

        _stream = new PortAudioSharp.Stream(
            inParams: param, outParams: null,
            sampleRate: Constants.SampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff,
            callback: Callback,
            userData: IntPtr.Zero);

        _stream.Start();
        return;

        StreamCallbackResult Callback(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
        {
            var count = (int)frameCount;
            var samples = new float[count];
            Marshal.Copy(input, samples, 0, count);
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