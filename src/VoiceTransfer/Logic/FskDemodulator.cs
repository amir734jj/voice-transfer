using VoiceTransfer.Data;

namespace VoiceTransfer.Logic;

/// <summary>
/// FSK demodulator using the Goertzel algorithm.
/// The Goertzel algorithm is an efficient single-frequency DFT -- perfect for
/// detecting whether a block of audio contains our mark or space tone.
/// 
/// This is the core DSP that separates our data signal from human voice:
/// - Human voice is broadband (energy spread across many frequencies)
/// - Our FSK signal is narrowband (concentrated at exactly 1800 or 2200 Hz)
/// - The Goertzel filter has a bandwidth of ~SampleRate/N ~ 300 Hz
/// - We compare power at both FSK frequencies and take the stronger one
/// </summary>
public class FskDemodulator(TransmissionProfile profile)
{
    /// <summary>
    /// Compute the Goertzel magnitude^2 at a target frequency for a block of samples.
    /// This is equivalent to computing |DFT[k]|^2 but only at one frequency bin,
    /// making it O(N) instead of O(N log N) for full FFT.
    /// </summary>
    public static double GoertzelPower(float[] samples, int offset, int length, double targetFreq)
    {
        // Map target frequency to the nearest DFT bin
        var k = Math.Round(length * targetFreq / Constants.SampleRate);
        var omega = 2.0 * Math.PI * k / length;
        var coeff = 2.0 * Math.Cos(omega);

        double s1 = 0, s2 = 0;

        for (var i = 0; i < length; i++)
        {
            var s0 = samples[offset + i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        // Power = |X(k)|^2 without the expensive sqrt
        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    /// <summary>
    /// Demodulate one bit from a block of samples.
    /// Returns (bit_value, is_valid_signal).
    /// The bit value is always the best guess (dominant frequency), even when
    /// the decision is ambiguous -- FEC + CRC handle any resulting errors.
    /// The valid flag is false when no signal is detected (below threshold)
    /// or when the decision ratio is too low (ambiguous).
    /// </summary>
    public (bool bit, bool valid) DemodulateBlock(float[] samples, int offset, int length)
    {
        if (offset + length > samples.Length)
        {
            return (false, false);
        }

        var markPower = GoertzelPower(samples, offset, length, profile.FreqMark);
        var spacePower = GoertzelPower(samples, offset, length, profile.FreqSpace);

        var maxPower = Math.Max(markPower, spacePower);
        var minPower = Math.Min(markPower, spacePower);

        // Check if signal is strong enough (distinguishes signal from silence)
        if (maxPower < profile.SignalThreshold)
        {
            return (false, false);
        }

        var bestGuess = markPower > spacePower;

        // Check if decision is clear enough (one frequency dominates)
        if (minPower > 0 && maxPower / minPower < profile.DecisionRatio)
        {
            return (bestGuess, false); // best guess bit, but low confidence
        }

        return (bestGuess, true);
    }

    /// <summary>
    /// Demodulate an entire sample buffer into a raw bit stream.
    /// Processes non-overlapping blocks of SamplesPerBit width.
    /// Returns (bits, validFlags) -- validFlags[i] indicates if bits[i] is trustworthy.
    /// </summary>
    public (bool[] bits, bool[] valid) DemodulateAll(float[] samples, int startOffset = 0)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = (samples.Length - startOffset) / blockSize;

        var bits = new bool[numBlocks];
        var valid = new bool[numBlocks];

        for (var i = 0; i < numBlocks; i++)
        {
            (bits[i], valid[i]) = DemodulateBlock(samples, startOffset + i * blockSize, blockSize);
        }

        return (bits, valid);
    }

    /// <summary>
    /// Find the best bit alignment offset by trying multiple sub-bit offsets
    /// and scoring the quality of the demodulated preamble pattern.
    /// </summary>
    public int FindBestAlignment(float[] samples, int searchStart, int searchLength)
    {
        var blockSize = profile.SamplesPerBit;
        var bestOffset = searchStart;
        double bestScore = -1;

        // Try offsets from 0 to SamplesPerBit-1 within the search region
        var steps = Math.Min(blockSize, 20); // limit search steps for performance
        var stepSize = Math.Max(1, blockSize / steps);

        for (var tryOffset = 0; tryOffset < blockSize; tryOffset += stepSize)
        {
            var offset = searchStart + tryOffset;
            if (offset + searchLength > samples.Length)
            {
                break;
            }

            // Demodulate a stretch of bits and check for alternating pattern
            var numTestBits = Math.Min(32, (searchLength - tryOffset) / blockSize);
            if (numTestBits < 8)
            {
                continue;
            }

            double score = 0;
            bool? prevBit = null;

            for (var b = 0; b < numTestBits; b++)
            {
                var (bit, valid) = DemodulateBlock(samples, offset + b * blockSize, blockSize);
                if (valid)
                {
                    score += 1.0; // reward valid signal
                    if (prevBit.HasValue && bit != prevBit.Value)
                    {
                        score += 2.0; // reward alternation (preamble pattern)
                    }
                }
                prevBit = bit;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        return bestOffset;
    }
}
