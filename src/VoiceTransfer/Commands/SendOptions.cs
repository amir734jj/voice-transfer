using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("send", HelpText = "Send a file as FSK audio tones (play through speaker or write WAV).")]
internal class SendOptions : ProfileOptions
{
    [Option('f', "file", Required = true, HelpText = "Path to the input file to transmit.")]
    public string InputFile { get; set; } = "";

    [Option('o', "output", HelpText = "Write to WAV file instead of playing through speakers.")]
    public string? OutputWav { get; set; }

    [Option('d', "device", Default = 0, HelpText = "Audio output device index.")]
    public int DeviceIndex { get; set; }
}