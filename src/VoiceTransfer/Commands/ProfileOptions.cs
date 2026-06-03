using CommandLine;
using VoiceTransfer.Audio;
using VoiceTransfer.Data;

namespace VoiceTransfer.Commands;

/// <summary>
/// Shared transmission profile options. All verbs that send or receive FSK data
/// inherit these so the profile parameters are defined in one place.
/// </summary>
internal abstract class ProfileOptions
{
    [Option('p', "preset", Default = "normal", HelpText = "Speed preset: slow (150 baud), normal (300 baud), fast (350 baud). Sender & receiver must match.")]
    public string Preset { get; set; } = "normal";

    [Option("baud", HelpText = "Override baud rate (bits/sec). Sender & receiver must match.")]
    public int? BaudRate { get; set; }

    [Option("preamble", HelpText = "Override preamble length in bits. Sender & receiver must match.")]
    public int? PreambleBits { get; set; }

    [Option("amplitude", HelpText = "Override signal amplitude (0.0-1.0). Sender & receiver must match.")]
    public double? Amplitude { get; set; }

    [Option("threshold", HelpText = "Override Goertzel signal detection threshold. Sender & receiver must match.")]
    public double? SignalThreshold { get; set; }

    [Option("ratio", HelpText = "Override decision ratio for bit detection. Sender & receiver must match.")]
    public double? DecisionRatio { get; set; }

    [Option("fec", HelpText = "FEC repeat factor (odd: 1,3,5,7,9). Higher = more error correction. Sender & receiver must match.")]
    public int? FecRepeat { get; set; }

    [Option("freq-mark", HelpText = "FSK mark frequency in Hz (bit=1, default 2200). Sender & receiver must match.")]
    public double? FreqMark { get; set; }

    [Option("freq-space", HelpText = "FSK space frequency in Hz (bit=0, default 1800). Sender & receiver must match.")]
    public double? FreqSpace { get; set; }

    [Option("password", HelpText = "Encryption password (AES-256-GCM). Must match on sender & receiver. Omit for no encryption.")]
    public string? Password { get; set; }

    [Option("audio-engine", Default = AudioEngineType.Auto, HelpText = "Audio engine: auto (naudio on Windows, portaudio on Linux/macOS), ownaudio, naudio (Windows only), soundflow, portaudio")]
    public AudioEngineType AudioEngine { get; set; } = AudioEngineType.Auto;

    public TransmissionProfile BuildProfile() =>
        TransmissionProfile.FromOptions(Preset, BaudRate, PreambleBits,
            Amplitude, SignalThreshold, DecisionRatio, FecRepeat,
            FreqMark, FreqSpace);
}