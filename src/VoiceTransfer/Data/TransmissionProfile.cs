using Serilog;

namespace VoiceTransfer.Data;

/// <summary>
/// Configurable transmission parameters. Both sender and receiver must use
/// identical settings for successful data transfer.
///
/// Presets:
///   slow   -- 150 baud, 256-bit preamble, 5x FEC, 1600/2400 Hz (800 Hz separation). Most resilient to noise.
///   normal -- 300 baud, 128-bit preamble, 3x FEC, 1800/2200 Hz (400 Hz separation). Good balance (default).
///   fast   -- 350 baud, 64-bit preamble,  1x FEC, 1800/2200 Hz (400 Hz separation). Faster but needs cleaner signal.
/// </summary>
public class TransmissionProfile
{
    // --- Configurable fields ---

    /// <summary>
    /// Bits per second. Higher = faster but less noise-resilient.
    /// </summary>
    public int BaudRate { get; init; } = 300;

    /// <summary>
    /// Number of alternating preamble bits for clock recovery. More = better sync.
    /// </summary>
    public int PreambleBits { get; init; } = 128;

    /// <summary>
    /// Signal amplitude [0-1]. Higher = more robust but more audible.
    /// </summary>
    public double Amplitude { get; init; } = 0.12;

    /// <summary>
    /// Minimum Goertzel power to consider a signal present.
    /// </summary>
    public double SignalThreshold { get; init; } = 0.0001;

    /// <summary>
    /// Required ratio of dominant/weaker frequency power for a valid bit decision.
    /// </summary>
    public double DecisionRatio { get; init; } = 1.5;

    /// <summary>
    /// FEC bit repetition factor (1=none, 3=can fix 1 error/group, 5=can fix 2).
    /// </summary>
    public int FecRepeat { get; init; } = 3;

    /// <summary>
    /// FSK mark frequency in Hz (bit=1). Must be within telephone passband and differ from space by >= 200 Hz.
    /// </summary>
    public double FreqMark { get; init; } = 2200.0;

    /// <summary>
    /// FSK space frequency in Hz (bit=0). Must be within telephone passband and differ from mark by >= 200 Hz.
    /// </summary>
    public double FreqSpace { get; init; } = 1800.0;

    // --- Derived (computed from BaudRate) ---

    public int SamplesPerBit => Constants.SampleRate / BaudRate;

    // --- Presets ---

    public static TransmissionProfile Slow => new()
    {
        BaudRate = 150,
        PreambleBits = 256,
        Amplitude = 0.15,
        SignalThreshold = 0.00005,
        DecisionRatio = 1.3,
        FecRepeat = 5,
        FreqMark = 2400.0,
        FreqSpace = 1600.0, // 800 Hz separation for maximum resilience
    };

    public static TransmissionProfile Normal => new()
    {
        BaudRate = 300,
        PreambleBits = 128,
        Amplitude = 0.12,
        SignalThreshold = 0.0001,
        DecisionRatio = 1.5,
        FecRepeat = 3,
        FreqMark = 2200.0,
        FreqSpace = 1800.0, // 400 Hz separation (standard)
    };

    public static TransmissionProfile Fast => new()
    {
        BaudRate = 350,
        PreambleBits = 64,
        Amplitude = 0.12,
        SignalThreshold = 0.0002,
        DecisionRatio = 1.5,
        FecRepeat = 1,
        FreqMark = 2200.0,
        FreqSpace = 1800.0, // 400 Hz separation
    };

    public static TransmissionProfile FromPreset(string name) => name.ToLowerInvariant() switch
    {
        "slow" => Slow,
        "normal" => Normal,
        "fast" => Fast,
        _ => throw new ArgumentException($"Unknown preset '{name}'. Valid: slow, normal, fast.")
    };

    /// <summary>
    /// Build a profile from CLI options: start with a preset, then override individual values.
    /// </summary>
    public static TransmissionProfile FromOptions(string preset, int? baudRate, int? preambleBits,
        double? amplitude, double? signalThreshold, double? decisionRatio, int? fecRepeat,
        double? freqMark = null, double? freqSpace = null)
    {
        var profile = FromPreset(preset);

        // Override individual fields if explicitly supplied
        if (baudRate.HasValue || preambleBits.HasValue || amplitude.HasValue ||
            signalThreshold.HasValue || decisionRatio.HasValue || fecRepeat.HasValue ||
            freqMark.HasValue || freqSpace.HasValue)
        {
            profile = new TransmissionProfile
            {
                BaudRate = baudRate ?? profile.BaudRate,
                PreambleBits = preambleBits ?? profile.PreambleBits,
                Amplitude = amplitude ?? profile.Amplitude,
                SignalThreshold = signalThreshold ?? profile.SignalThreshold,
                DecisionRatio = decisionRatio ?? profile.DecisionRatio,
                FecRepeat = fecRepeat ?? profile.FecRepeat,
                FreqMark = freqMark ?? profile.FreqMark,
                FreqSpace = freqSpace ?? profile.FreqSpace,
            };
        }

        profile.Validate();
        return profile;
    }

    public void Validate()
    {
        if (BaudRate < 50 || BaudRate > 2400)
        {
            throw new ArgumentException($"BaudRate must be 50-2400, got {BaudRate}.");
        }

        // Goertzel frequency resolution = SampleRate / SamplesPerBit
        // Must be narrower than the frequency separation (400 Hz) to distinguish mark from space
        var freqSep = Math.Abs(FreqMark - FreqSpace);
        var binWidth = (double)Constants.SampleRate / SamplesPerBit;
        if (binWidth > freqSep - 50)
        {
            Log.Warning("Baud rate {Baud} gives Goertzel bin width {Bin:F0} Hz, " +
                        "which is close to the {Sep:F0} Hz mark/space separation. Noise resilience will be poor.",
                BaudRate, binWidth, freqSep);
        }

        if (Amplitude <= 0 || Amplitude > 1.0)
        {
            throw new ArgumentException($"Amplitude must be in (0, 1.0], got {Amplitude}.");
        }

        if (PreambleBits < 16)
        {
            throw new ArgumentException($"PreambleBits must be >= 16, got {PreambleBits}.");
        }

        if (SignalThreshold <= 0)
        {
            throw new ArgumentException($"SignalThreshold must be > 0, got {SignalThreshold}.");
        }

        if (DecisionRatio < 1.0)
        {
            throw new ArgumentException($"DecisionRatio must be >= 1.0, got {DecisionRatio}.");
        }

        if (FecRepeat < 1 || FecRepeat > 9 || FecRepeat % 2 == 0)
        {
            throw new ArgumentException($"FecRepeat must be an odd number 1-9, got {FecRepeat}.");
        }

        if (FreqMark < 300 || FreqMark > 3400)
        {
            throw new ArgumentException($"FreqMark must be 300-3400 Hz (telephone passband), got {FreqMark}.");
        }

        if (FreqSpace < 300 || FreqSpace > 3400)
        {
            throw new ArgumentException($"FreqSpace must be 300-3400 Hz (telephone passband), got {FreqSpace}.");
        }

        if (Math.Abs(FreqMark - FreqSpace) < 200)
        {
            throw new ArgumentException($"FreqMark and FreqSpace must differ by >= 200 Hz, got {Math.Abs(FreqMark - FreqSpace):F0} Hz.");
        }
    }

    public void LogSettings()
    {
        Log.Information("Profile: {Baud} baud, preamble={Preamble}, amplitude={Amp}, " +
            "threshold={Thresh}, ratio={Ratio}, FEC={Fec}x, mark={Mark} Hz, space={Space} Hz",
            BaudRate, PreambleBits, Amplitude, SignalThreshold, DecisionRatio, FecRepeat,
            FreqMark, FreqSpace);

        var binWidth = (double)Constants.SampleRate / SamplesPerBit;
        var effectiveBaud = (double)BaudRate / FecRepeat;
        Log.Information("Effective data rate: ~{Rate:F0} data bits/sec (~{Bytes:F1} bytes/sec after base64+framing)",
            effectiveBaud, effectiveBaud / 8 / 1.37);
        var freqSep = Math.Abs(FreqMark - FreqSpace);
        Log.Debug("Goertzel bin width: {Bin:F0} Hz (freq separation: {Sep:F0} Hz)", binWidth, freqSep);
    }
}
