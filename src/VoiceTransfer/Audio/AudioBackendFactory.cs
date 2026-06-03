using System.Runtime.InteropServices;
using Serilog;
using VoiceTransfer.Audio.NAudio;
using VoiceTransfer.Audio.OwnAudio;
using VoiceTransfer.Audio.SoundFlow;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio;

public enum AudioEngineType
{
    Auto,
    OwnAudio,
    NAudio,
    SoundFlow
}

public static class AudioBackendFactory
{
    public static IAudioBackend Create(AudioEngineType engine = AudioEngineType.Auto, bool loopback = false)
    {
        var backend = engine switch
        {
            AudioEngineType.NAudio => RequireWindows(new NAudioBackend()),
            AudioEngineType.OwnAudio => new OwnAudioBackend(),
            AudioEngineType.SoundFlow => new SoundFlowBackend(),
            AudioEngineType.Auto => CreateAuto(loopback),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown audio engine")
        };

        var resolved = backend.GetType().Name.Replace("Backend", "");
        Log.Information("Audio engine: {Engine}{Auto}", resolved, engine == AudioEngineType.Auto ? " (auto)" : "");
        return backend;
    }

    private static IAudioBackend CreateAuto(bool loopback)
    {
        if (loopback)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Log.Error("Loopback capture requires Windows (WASAPI). On Linux/macOS, use a virtual audio cable instead");
                Environment.Exit(1);
            }

            return new NAudioBackend();
        }

        return new OwnAudioBackend();
    }

    private static IAudioBackend RequireWindows(IAudioBackend backend)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("NAudio backend requires Windows");
        }

        return backend;
    }
}
