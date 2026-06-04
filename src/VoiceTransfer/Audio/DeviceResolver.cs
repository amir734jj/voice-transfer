using Serilog;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio;

/// <summary>
/// Resolves device indices, auto-detecting hardware devices when index is -1.
/// </summary>
public static class DeviceResolver
{
    private static readonly string[] VirtualDevicePrefixes =
        ["Voicemeeter", "CABLE", "VB-Audio", "Virtual"];

    /// <summary>
    /// Resolves an input device index. When -1, picks the first non-virtual device.
    /// </summary>
    public static int ResolveInput(IAudioBackend audio, int requestedIndex)
    {
        if (requestedIndex >= 0)
            return requestedIndex;

        var names = audio.GetInputDeviceNames();
        for (var i = 0; i < names.Count; i++)
        {
            if (!IsVirtualDevice(names[i]))
            {
                Log.Information("Auto-detected input device [{Index}] {Name}", i, names[i]);
                return i;
            }
        }

        // No hardware device found — fall back to 0
        if (names.Count > 0)
            Log.Warning("No hardware input device found, using device [0] {Name}", names[0]);
        return 0;
    }

    /// <summary>
    /// Resolves an output device index. When -1, picks the first non-virtual device.
    /// </summary>
    public static int ResolveOutput(IAudioBackend audio, int requestedIndex)
    {
        if (requestedIndex >= 0)
            return requestedIndex;

        var names = audio.GetOutputDeviceNames();
        for (var i = 0; i < names.Count; i++)
        {
            if (!IsVirtualDevice(names[i]))
            {
                Log.Information("Auto-detected output device [{Index}] {Name}", i, names[i]);
                return i;
            }
        }

        if (names.Count > 0)
            Log.Warning("No hardware output device found, using device [0] {Name}", names[0]);
        return 0;
    }

    private static bool IsVirtualDevice(string name) =>
        VirtualDevicePrefixes.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
}
