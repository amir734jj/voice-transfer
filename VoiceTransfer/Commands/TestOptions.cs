using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("test", HelpText = "Test audio devices: list devices, play a test tone through speakers, and record from the microphone.")]
internal class TestOptions : ProfileOptions
{
    [Option('d', "device", Default = -1, HelpText = "Audio input device index (-1 = auto-detect hardware device).")]
    public int DeviceIndex { get; set; }

    [Option("output-device", Default = -1, HelpText = "Audio output device index (-1 = auto-detect hardware device).")]
    public int OutputDeviceIndex { get; set; }

    [Option("duration", Default = 2.0, HelpText = "Duration in seconds for speaker test tone and microphone recording.")]
    public double Duration { get; set; }
}
