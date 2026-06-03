using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("test", HelpText = "Test audio devices: list devices, play a test tone through speakers, and record from the microphone.")]
internal class TestOptions : ProfileOptions
{
    [Option('d', "device", Default = 0, HelpText = "Audio device index to test (used for both input and output).")]
    public int DeviceIndex { get; set; }

    [Option("output-device", Default = 0, HelpText = "Audio output device index (if different from input).")]
    public int OutputDeviceIndex { get; set; }

    [Option("duration", Default = 2.0, HelpText = "Duration in seconds for speaker test tone and microphone recording.")]
    public double Duration { get; set; }
}
