namespace VoiceTransfer.Data;

/// <summary>
/// Fixed FSK modem parameters that are NOT user-configurable.
/// Configurable parameters live in <see cref="TransmissionProfile"/>.
/// </summary>
public static class Constants
{
    // Audio format (fixed -- must match on both sides by definition)
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    // Frame delimiter (fixed)
    public const byte SyncByte = 0x7E;                          // HDLC-style frame delimiter

    // Scramble key (fixed)
    public const byte ScrambleKey = 0xA7;

    // Silence padding (fixed)
    public const double LeadingSilence = 0.3;
    public const double TrailingSilence = 0.3;
}
