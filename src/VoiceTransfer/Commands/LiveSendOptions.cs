using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("live-send", HelpText = "Interactive sender: type text lines, each transmitted as FSK audio in real-time.")]
internal class LiveSendOptions : ProfileOptions
{
    [Option('d', "device", Default = -1, HelpText = "Audio output device index (-1 = auto-detect hardware device).")]
    public int DeviceIndex { get; set; }
}