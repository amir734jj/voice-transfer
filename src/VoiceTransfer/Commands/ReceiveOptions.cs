using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("receive", HelpText = "Listen for FSK audio and decode received file.")]
internal class ReceiveOptions : ProfileOptions
{
    [Option('o', "output", Required = true, HelpText = "Path to write the received file.")]
    public string OutputFile { get; set; } = "";

    [Option('i', "input", HelpText = "Read from WAV file instead of microphone.")]
    public string? InputWav { get; set; }

    [Option('d', "device", Default = -1, HelpText = "Audio input device index (-1 = auto-detect hardware device).")]
    public int DeviceIndex { get; set; }

    [Option('t', "timeout", Default = 60, HelpText = "Recording timeout in seconds (microphone mode).")]
    public int TimeoutSeconds { get; set; }
}