using Serilog;
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
    // Per-frequency gain compensation (calibrated from signal)
    private double _markGain = 1.0;
    private double _spaceGain = 1.0;
    
    // Adaptive calibration: per-block raw power levels and sliding-window gains
    private int _calSignalStart;
    private double[] _blockMarkPower = [];
    private double[] _blockSpacePower = [];
    private double[] _blockMarkGain = [];
    private double[] _blockSpaceGain = [];
    private const int AdaptiveWindowSize = 40; // sliding window for local average (±20 blocks)
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
    /// 
    /// Uses a guard interval: only analyzes the center portion of each bit period
    /// to reduce inter-symbol interference from room reverb/echo. The edges of
    /// each bit period contain echoes from adjacent bits that corrupt Goertzel.
    /// </summary>
    public (bool bit, bool valid) DemodulateBlock(float[] samples, int offset, int length)
    {
        if (offset + length > samples.Length)
        {
            return (false, false);
        }

        // Apply guard interval: analyze only the center portion of the bit period
        var guardSamples = (int)(length * profile.GuardFraction);
        var analysisOffset = offset + guardSamples;
        var analysisLength = length - 2 * guardSamples;
        if (analysisLength < 16) analysisLength = length; // safety: use full period if too short
        if (analysisOffset + analysisLength > samples.Length)
        {
            analysisLength = samples.Length - analysisOffset;
            if (analysisLength < 16) return (false, false);
        }

        var rawMark = GoertzelPower(samples, analysisOffset, analysisLength, profile.FreqMark);
        var rawSpace = GoertzelPower(samples, analysisOffset, analysisLength, profile.FreqSpace);
        
        // Use adaptive per-block gain if available, otherwise fall back to global
        double mg = _markGain, sg = _spaceGain;
        if (_blockMarkGain.Length > 0)
        {
            var blockIdx = (offset - _calSignalStart) / profile.SamplesPerBit;
            if (blockIdx >= 0 && blockIdx < _blockMarkGain.Length)
            {
                mg = _blockMarkGain[blockIdx];
                sg = _blockSpaceGain[blockIdx];
            }
        }

        var markPower = rawMark * mg;
        var spacePower = rawSpace * sg;

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
    /// Calibrate per-frequency gain to compensate for uneven frequency response.
    /// 
    /// Uses a two-pass approach:
    /// 1. Compute raw Goertzel power at mark/space frequencies for every block
    /// 2. For each block, compute a sliding-window average of mark/space power
    ///    and derive a local gain that equalizes the two frequencies.
    /// 
    /// This adaptive approach handles codecs (like WhatsApp/Opus) that change
    /// their frequency response over time. A fixed global gain fails when
    /// the mark/space power ratio varies by 5-20x across the signal.
    /// </summary>
    public void CalibrateFromSignal(float[] samples, int signalStart, int signalEnd)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = (signalEnd - signalStart) / blockSize;
        if (numBlocks < 10)
        {
            return;
        }

        _calSignalStart = signalStart;
        _blockMarkPower = new double[numBlocks];
        _blockSpacePower = new double[numBlocks];

        // Pass 1: compute raw power at each frequency for every block
        // Uses same guard interval as DemodulateBlock for consistency
        double totalMark = 0, totalSpace = 0;
        var count = 0;
        var calGuardSamples = (int)(blockSize * profile.GuardFraction);
        var analysisLen = blockSize - 2 * calGuardSamples;

        for (var i = 0; i < numBlocks; i++)
        {
            var offset = signalStart + i * blockSize + calGuardSamples;
            if (offset + analysisLen > samples.Length) break;

            _blockMarkPower[i] = GoertzelPower(samples, offset, analysisLen, profile.FreqMark);
            _blockSpacePower[i] = GoertzelPower(samples, offset, analysisLen, profile.FreqSpace);

            if (Math.Max(_blockMarkPower[i], _blockSpacePower[i]) > profile.SignalThreshold)
            {
                totalMark += _blockMarkPower[i];
                totalSpace += _blockSpacePower[i];
                count++;
            }
        }

        if (count < 10 || totalMark < 1e-10 || totalSpace < 1e-10)
        {
            return;
        }

        // Global average (used as fallback and for logging)
        var avgMark = totalMark / count;
        var avgSpace = totalSpace / count;

        if (avgMark > avgSpace)
        {
            _spaceGain = avgMark / avgSpace;
            _markGain = 1.0;
        }
        else
        {
            _markGain = avgSpace / avgMark;
            _spaceGain = 1.0;
        }

        // Pass 2: compute per-block adaptive gains using sliding window
        _blockMarkGain = new double[numBlocks];
        _blockSpaceGain = new double[numBlocks];
        var halfWin = AdaptiveWindowSize / 2;

        for (var i = 0; i < numBlocks; i++)
        {
            var winStart = Math.Max(0, i - halfWin);
            var winEnd = Math.Min(numBlocks, i + halfWin);

            double winMark = 0, winSpace = 0;
            var winCount = 0;

            for (var j = winStart; j < winEnd; j++)
            {
                if (Math.Max(_blockMarkPower[j], _blockSpacePower[j]) > profile.SignalThreshold)
                {
                    winMark += _blockMarkPower[j];
                    winSpace += _blockSpacePower[j];
                    winCount++;
                }
            }

            if (winCount < 4 || winMark < 1e-10 || winSpace < 1e-10)
            {
                // Fall back to global calibration
                _blockMarkGain[i] = _markGain;
                _blockSpaceGain[i] = _spaceGain;
            }
            else
            {
                var localAvgMark = winMark / winCount;
                var localAvgSpace = winSpace / winCount;
                if (localAvgMark > localAvgSpace)
                {
                    _blockMarkGain[i] = 1.0;
                    _blockSpaceGain[i] = localAvgMark / localAvgSpace;
                }
                else
                {
                    _blockMarkGain[i] = localAvgSpace / localAvgMark;
                    _blockSpaceGain[i] = 1.0;
                }
            }
        }

        Log.Information("Frequency gain calibration: markGain={MarkGain:F2}, spaceGain={SpaceGain:F2} " +
                  "(avgMark={AvgMark:E2}, avgSpace={AvgSpace:E2}, blocks={Count}), adaptive window={Window}",
            _markGain, _spaceGain, avgMark, avgSpace, count, AdaptiveWindowSize);
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
    /// Demodulate an entire sample buffer into soft-decision values.
    /// Returns a float[] where each value is a log-likelihood ratio (LLR):
    ///   positive = mark (1), negative = space (0), magnitude = confidence.
    /// Used for soft-decision FEC which gives much better error correction
    /// than hard majority voting, especially over-the-air where individual
    /// bit confidence varies widely.
    /// </summary>
    public double[] DemodulateAllSoft(float[] samples, int startOffset = 0)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = (samples.Length - startOffset) / blockSize;
        var softBits = new double[numBlocks];

        for (var i = 0; i < numBlocks; i++)
        {
            softBits[i] = DemodulateBlockSoft(samples, startOffset + i * blockSize, blockSize);
        }

        return softBits;
    }

    /// <summary>
    /// Soft-decision demodulation of one bit period.
    /// Returns log-likelihood ratio: positive = mark (1), negative = space (0).
    /// Magnitude indicates confidence. Zero means no signal.
    /// </summary>
    public double DemodulateBlockSoft(float[] samples, int offset, int length)
    {
        if (offset + length > samples.Length)
        {
            return 0;
        }

        // Apply guard interval
        var guardSamples = (int)(length * profile.GuardFraction);
        var analysisOffset = offset + guardSamples;
        var analysisLength = length - 2 * guardSamples;
        if (analysisLength < 16) analysisLength = length;
        if (analysisOffset + analysisLength > samples.Length)
        {
            analysisLength = samples.Length - analysisOffset;
            if (analysisLength < 16) return 0;
        }

        var rawMark = GoertzelPower(samples, analysisOffset, analysisLength, profile.FreqMark);
        var rawSpace = GoertzelPower(samples, analysisOffset, analysisLength, profile.FreqSpace);

        // Apply adaptive per-block gain
        double mg = _markGain, sg = _spaceGain;
        if (_blockMarkGain.Length > 0)
        {
            var blockIdx = (offset - _calSignalStart) / profile.SamplesPerBit;
            if (blockIdx >= 0 && blockIdx < _blockMarkGain.Length)
            {
                mg = _blockMarkGain[blockIdx];
                sg = _blockSpaceGain[blockIdx];
            }
        }

        var markPower = rawMark * mg;
        var spacePower = rawSpace * sg;

        // Return LLR: log(markPower / spacePower), clamped to avoid extremes
        if (markPower < 1e-15 && spacePower < 1e-15) return 0;
        if (spacePower < 1e-15) return 10.0; // very confident mark
        if (markPower < 1e-15) return -10.0; // very confident space

        var llr = Math.Log(markPower / spacePower);
        return Math.Clamp(llr, -10.0, 10.0);
    }

    /// <summary>
    /// Find the best bit alignment offset by trying multiple sub-bit offsets
    /// and scoring the quality of the demodulated preamble pattern.
    /// Tries every sample offset within one bit period for maximum precision.
    /// </summary>
    public int FindBestAlignment(float[] samples, int searchStart, int searchLength)
    {
        var blockSize = profile.SamplesPerBit;

        // Stage 1: Coarse search — try every bit-period offset over a wide range
        // to handle gaps (comfort noise, recording start delay) before the preamble.
        var maxCoarseSteps = Math.Min(100, searchLength / blockSize);
        var bestCoarseOffset = searchStart;
        double bestCoarseScore = -1;

        for (var step = 0; step < maxCoarseSteps; step++)
        {
            var offset = searchStart + step * blockSize;
            if (offset + blockSize * 6 > samples.Length) break;

            var numTestBits = Math.Min(32, (samples.Length - offset) / blockSize);
            if (numTestBits < 6) continue;

            var score = ScoreAlternation(samples, offset, blockSize, numTestBits);
            if (score > bestCoarseScore)
            {
                bestCoarseScore = score;
                bestCoarseOffset = offset;
            }
        }

        // Stage 2: Fine search — try every sample offset within the winning bit period
        var bestOffset = bestCoarseOffset;
        double bestScore = -1;

        for (var tryOffset = -blockSize; tryOffset < blockSize; tryOffset++)
        {
            var offset = bestCoarseOffset + tryOffset;
            if (offset < 0 || offset + blockSize * 6 > samples.Length) continue;

            var numTestBits = Math.Min(32, (samples.Length - offset) / blockSize);
            if (numTestBits < 6) continue;

            var score = ScoreAlternation(samples, offset, blockSize, numTestBits);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        return bestOffset;
    }

    private double ScoreAlternation(float[] samples, int startOffset, int blockSize, int numBits)
    {
        double score = 0;
        bool? prevBit = null;

        for (var b = 0; b < numBits; b++)
        {
            var (bit, valid) = DemodulateBlock(samples, startOffset + b * blockSize, blockSize);
            if (valid)
            {
                score += 1.0;
                if (prevBit.HasValue && bit != prevBit.Value)
                    score += 2.0;
            }
            else
            {
                if (prevBit.HasValue && bit != prevBit.Value)
                    score += 0.5;
            }
            prevBit = bit;
        }

        return score;
    }
}
