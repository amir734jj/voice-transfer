using System.Runtime.InteropServices;
using VoiceTransfer.Audio.NAudio;
using VoiceTransfer.Audio.OwnAudio;
using VoiceTransfer.Audio.SoundFlow;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio;

public static class AudioBackendFactory
{
    public static IAudioBackend Create(string engine = "auto", bool loopback = false)
    {
        return engine.ToLowerInvariant() switch
        {
            "naudio" => RequireWindows(new NAudioBackend()),
            "ownaudio" => new OwnAudioBackend(),
            "soundflow" => new SoundFlowBackend(),
            "auto" => CreateAuto(loopback),
            _ => throw new ArgumentException($"Unknown audio engine '{engine}'. Valid: auto, ownaudio, naudio, soundflow")
        };
    }

    private static IAudioBackend CreateAuto(bool loopback)
    {
        if (loopback)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException(
                    "Loopback capture requires Windows (WASAPI). On Linux/macOS, use a virtual audio cable instead");
            }

            return new NAudioBackend();
        }

        return new OwnAudioBackend();
    }

    private static IAudioBackend RequireWindows(IAudioBackend backend)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("NAudio backend requires Windows");
        return backend;
    }
}
