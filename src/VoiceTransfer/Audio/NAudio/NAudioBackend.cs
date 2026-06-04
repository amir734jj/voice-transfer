using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceTransfer.Interfaces;

namespace VoiceTransfer.Audio.NAudio;

public sealed class NAudioBackend : IAudioBackend
{
    public IReadOnlyList<string> GetInputDeviceNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => d.FriendlyName)
                .ToList();
        }
        catch (NotSupportedException)
        {
            // COM disabled (trimmed build) -- fall back to WaveIn API
            var names = new List<string>();
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                names.Add(WaveInEvent.GetCapabilities(i).ProductName);
            return names;
        }
    }

    public IReadOnlyList<string> GetOutputDeviceNames()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => d.FriendlyName)
                .ToList();
        }
        catch (NotSupportedException)
        {
            // COM disabled (trimmed build) -- WaveOutEvent doesn't expose device enumeration,
            // so use P/Invoke to waveOutGetNumDevs/waveOutGetDevCaps
            var count = waveOutGetNumDevs();
            var names = new List<string>(count);
            for (uint i = 0; i < count; i++)
            {
                var caps = new WaveOutCapsW();
                if (waveOutGetDevCapsW(i, ref caps, WaveOutCapsW.Size) == 0)
                {
                    names.Add(caps.szPname);
                }
                else
                {
                    names.Add($"Output Device {i}");
                }
            }
            return names;
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int waveOutGetDevCapsW(uint deviceId, ref WaveOutCapsW caps, int size);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct WaveOutCapsW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
        public static readonly int Size = System.Runtime.InteropServices.Marshal.SizeOf<WaveOutCapsW>();
    }

    public bool SupportsLoopback => true;

    public IAudioPlayer CreatePlayer(int outputDeviceIndex) =>
        new NAudioPlayer(outputDeviceIndex);

    public IAudioRecorder CreateRecorder(int inputDeviceIndex) =>
        new NAudioRecorder(inputDeviceIndex);

    public IAudioRecorder CreateLoopbackRecorder() =>
        new NAudioLoopbackRecorder();

    public IAudioDuplex CreateDuplex(int inputDeviceIndex, int outputDeviceIndex) =>
        new NAudioDuplex(inputDeviceIndex, outputDeviceIndex);
}
