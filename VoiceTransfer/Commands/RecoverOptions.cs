using CommandLine;

namespace VoiceTransfer.Commands;

[Verb("recover", HelpText = "Aggressive recovery: try every parameter combination to decode a damaged recording.")]
internal class RecoverOptions
{
    [Option('i', "input", Required = true, HelpText = "Audio file to attempt recovery on.")]
    public string InputWav { get; set; } = "";

    [Option('o', "output", Required = true, HelpText = "Path to write recovered file.")]
    public string OutputFile { get; set; } = "";

    [Option("password", HelpText = "Encryption password if the original was encrypted.")]
    public string? Password { get; set; }
}
