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