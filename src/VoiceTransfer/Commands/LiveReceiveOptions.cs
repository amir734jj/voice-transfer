using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("live-receive", HelpText = "Interactive receiver: listen for FSK audio, pass voice through to speakers, and print decoded text.")]
internal class LiveReceiveOptions : ProfileOptions
{
    [Option('d', "device", Default = 0, HelpText = "Audio input device index (microphone).")]
    public int DeviceIndex { get; set; }

    [Option("output-device", Default = 0, HelpText = "Audio output device index (speakers, for voice passthrough).")]
    public int OutputDeviceIndex { get; set; }
}