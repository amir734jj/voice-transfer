using System.Runtime.InteropServices;

namespace VoiceTransfer.Audio;

public static class AudioBackendFactory
{
    public static IAudioBackend Create(bool loopback = false)
    {
        if (loopback)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException(
                    "Loopback capture requires Windows (WASAPI). On Linux/macOS, use a virtual audio cable instead");

            return new NAudioBackend();
        }

        return new OwnAudioBackend();
    }
}
