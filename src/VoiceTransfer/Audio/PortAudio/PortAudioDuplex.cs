using System.Runtime.InteropServices;
using PortAudioSharp;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.PortAudio;

internal sealed class PortAudioDuplex : IAudioDuplex
{
    private readonly PortAudioSharp.Stream _stream;
    private readonly ChunkedAudioBuffer _captureBuffer = new();
    private readonly ChunkedAudioBuffer _playBuffer = new();

    public PortAudioDuplex(int inputDeviceIndex, int outputDeviceIndex)
    {
        var inInfo = PortAudioSharp.PortAudio.GetDeviceInfo(inputDeviceIndex);
        var outInfo = PortAudioSharp.PortAudio.GetDeviceInfo(outputDeviceIndex);

        var inParam = new StreamParameters
        {
            device = inputDeviceIndex,
            channelCount = Constants.Channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = inInfo.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        var outParam = new StreamParameters
        {
            device = outputDeviceIndex,
            channelCount = Constants.Channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = outInfo.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        PortAudioSharp.Stream.Callback callback = (IntPtr input, IntPtr output,
            uint frameCount, ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags, IntPtr userData) =>
        {
            var count = (int)frameCount;

            // Capture input
            var inSamples = new float[count];
            Marshal.Copy(input, inSamples, 0, count);
            _captureBuffer.Enqueue(inSamples);

            // Play output from queue
            var outSamples = new float[count];
            _playBuffer.Read(outSamples);
            Marshal.Copy(outSamples, 0, output, count);

            return StreamCallbackResult.Continue;
        };

        _stream = new PortAudioSharp.Stream(
            inParams: inParam, outParams: outParam,
            sampleRate: Constants.SampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff,
            callback: callback,
            userData: IntPtr.Zero);

        _stream.Start();
    }

    public int Read(Span<float> buffer) => _captureBuffer.Read(buffer);

    public void Write(ReadOnlySpan<float> buffer)
    {
        _playBuffer.Enqueue(buffer.ToArray());
    }

    public void Dispose()
    {
        _stream.Stop();
        _stream.Dispose();
    }
}