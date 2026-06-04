using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("live-receive", HelpText = "Interactive receiver: listen for FSK audio and print decoded text.")]
internal class LiveReceiveOptions : ProfileOptions
{
    [Option('d', "device", Default = -1, HelpText = "Audio input device index (-1 = auto-detect hardware device).")]
    public int DeviceIndex { get; set; }

    [Option("output-device", Default = -1, HelpText = "Audio output device index (-1 = auto-detect hardware device).")]
    public int OutputDeviceIndex { get; set; }

    [Option("loopback", Default = false, HelpText = "Use WASAPI loopback capture to capture system audio output directly (Windows only, for same-machine testing).")]
    public bool Loopback { get; set; }

    [Option("passthrough", Default = false, HelpText = "Enable voice passthrough: play captured mic audio through speakers (use headphones to avoid feedback).")]
    public bool Passthrough { get; set; }
}