using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("live-send", HelpText = "Interactive sender: type text lines, each transmitted as FSK audio in real-time.")]
internal class LiveSendOptions : ProfileOptions
{
    [Option('d', "device", Default = 0, HelpText = "Audio output device index.")]
    public int DeviceIndex { get; set; }
}