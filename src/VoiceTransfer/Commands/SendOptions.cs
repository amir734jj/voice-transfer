using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("send", HelpText = "Send a file as FSK audio tones (play through speaker or write WAV).")]
internal class SendOptions : ProfileOptions
{
    [Option('f', "file", Required = true, HelpText = "Path to the input file to transmit.")]
    public string InputFile { get; set; } = "";

    [Option('o', "output", HelpText = "Write to WAV file instead of playing through speakers.")]
    public string? OutputWav { get; set; }

    [Option('d', "device", Default = -1, HelpText = "Audio output device index (-1 = auto-detect hardware device).")]
    public int DeviceIndex { get; set; }

    [Option("wasapi-out", Default = false, HelpText = "Use WASAPI output instead of WaveOut (required for loopback capture on same machine).")]
    public bool WasapiOut { get; set; }
}