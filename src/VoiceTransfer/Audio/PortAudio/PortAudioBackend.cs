using System.Runtime.InteropServices;
using PortAudioSharp;
using VoiceTransfer.Data;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.PortAudio;

public sealed class PortAudioBackend : IAudioBackend
{
    private static bool _initialized;
    private static readonly object InitLock = new();

    internal static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (!_initialized)
            {
                PortAudioSharp.PortAudio.Initialize();
                _initialized = true;
            }
        }
    }

    public IReadOnlyList<string> GetInputDeviceNames()
    {
        EnsureInitialized();
        var names = new List<string>();
        for (var i = 0; i < PortAudioSharp.PortAudio.DeviceCount; i++)
        {
            var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0)
            {
                names.Add(info.name);
            }
        }
        return names;
    }

    public IReadOnlyList<string> GetOutputDeviceNames()
    {
        EnsureInitialized();
        var names = new List<string>();
        for (var i = 0; i < PortAudioSharp.PortAudio.DeviceCount; i++)
        {
            var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels > 0)
            {
                names.Add(info.name);
            }
        }
        return names;
    }

    public bool SupportsLoopback => false;

    public IAudioPlayer CreatePlayer(int outputDeviceIndex)
    {
        EnsureInitialized();
        var deviceIndex = ResolveOutputDevice(outputDeviceIndex);
        return new PortAudioPlayer(deviceIndex);
    }

    public IAudioRecorder CreateRecorder(int inputDeviceIndex)
    {
        EnsureInitialized();
        var deviceIndex = ResolveInputDevice(inputDeviceIndex);
        return new PortAudioRecorder(deviceIndex);
    }

    public IAudioRecorder? CreateLoopbackRecorder() => null;

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex)
    {
        EnsureInitialized();
        return new PortAudioDuplex(ResolveInputDevice(inputDeviceIndex), ResolveOutputDevice(outputDeviceIndex));
    }

    private static int ResolveOutputDevice(int index)
    {
        var outputIndex = 0;
        for (var i = 0; i < PortAudioSharp.PortAudio.DeviceCount; i++)
        {
            var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
            if (info.maxOutputChannels > 0)
            {
                if (outputIndex == index)
                {
                    return i;
                }

                outputIndex++;
            }
        }
        return PortAudioSharp.PortAudio.DefaultOutputDevice;
    }

    private static int ResolveInputDevice(int index)
    {
        var inputIndex = 0;
        for (var i = 0; i < PortAudioSharp.PortAudio.DeviceCount; i++)
        {
            var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
            if (info.maxInputChannels > 0)
            {
                if (inputIndex == index)
                {
                    return i;
                }

                inputIndex++;
            }
        }
        return PortAudioSharp.PortAudio.DefaultInputDevice;
    }
}

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

        PortAudioSharp.Stream.Callback callback = (IntPtr input, IntPtr output,
            uint frameCount, ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags, IntPtr userData) =>
        {
            var count = (int)frameCount;
            var samples = new float[count];
            Marshal.Copy(input, samples, 0, count);
            _buffer.Enqueue(samples);
            return StreamCallbackResult.Continue;
        };

        _stream = new PortAudioSharp.Stream(
            inParams: param, outParams: null,
            sampleRate: Constants.SampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff,
            callback: callback,
            userData: IntPtr.Zero);

        _stream.Start();
    }

    public int Read(Span<float> buffer) => _buffer.Read(buffer);

    public void Dispose()
    {
        _stream.Stop();
        _stream.Dispose();
    }
}

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
