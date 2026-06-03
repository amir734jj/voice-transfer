using System.Runtime.InteropServices;
using PortAudioSharp;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.PortAudio;

internal sealed class PortAudioPlayer : IAudioPlayer
{
    private readonly int _deviceIndex;

    public PortAudioPlayer(int deviceIndex)
    {
        _deviceIndex = deviceIndex;
    }

    public void Play(float[] samples)
    {
        var info = PortAudioSharp.PortAudio.GetDeviceInfo(_deviceIndex);
        var param = new StreamParameters
        {
            device = _deviceIndex,
            channelCount = Constants.Channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        var offset = 0;
        var total = samples.Length;
        using var done = new ManualResetEventSlim(false);

        PortAudioSharp.Stream.Callback callback = (IntPtr input, IntPtr output,
            uint frameCount, ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags, IntPtr userData) =>
        {
            var count = (int)frameCount;
            var remaining = total - offset;

            if (remaining <= 0)
            {
                // Write silence and signal completion
                var silence = new byte[count * sizeof(float)];
                Marshal.Copy(silence, 0, output, silence.Length);
                done.Set();
                return StreamCallbackResult.Complete;
            }

            var toCopy = Math.Min(count, remaining);
            Marshal.Copy(samples, offset, output, toCopy);
            offset += toCopy;

            // Zero-fill any remaining frames
            if (toCopy < count)
            {
                var silenceBytes = (count - toCopy) * sizeof(float);
                var silence = new byte[silenceBytes];
                Marshal.Copy(silence, 0, IntPtr.Add(output, toCopy * sizeof(float)), silenceBytes);
            }

            return StreamCallbackResult.Continue;
        };

        using var stream = new PortAudioSharp.Stream(
            inParams: null, outParams: param,
            sampleRate: Constants.SampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff,
            callback: callback,
            userData: IntPtr.Zero);

        stream.Start();
        done.Wait();
        stream.Stop();
    }

    public void Dispose() { }
}