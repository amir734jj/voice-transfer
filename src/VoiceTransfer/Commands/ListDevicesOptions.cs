using CommandLine;
using VoiceTransfer.Audio;

namespace VoiceTransfer.Commands;

[Verb("list-devices", HelpText = "List all available audio input and output devices.")]
internal class ListDevicesOptions
{
    [Option("audio-engine", Default = AudioEngineType.Auto, HelpText = "Audio engine: auto (naudio on Windows, portaudio on Linux/macOS), ownaudio, naudio (Windows only), soundflow, portaudio")]
    public AudioEngineType AudioEngine { get; set; }
}
