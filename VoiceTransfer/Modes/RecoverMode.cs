using Serilog;
using VoiceTransfer.Data;
using VoiceTransfer.Logic;

namespace VoiceTransfer.Modes;

/// <summary>
/// Aggressive recovery mode for severely degraded over-the-air recordings.
/// Tries multiple strategies including echo subtraction, multi-guard sweep,
/// and per-bit gain calibration to recover data from hostile channels.
/// </summary>
public static class RecoverMode
{
    public static void Run(string inputWav, string outputFile, string? password)
    {
        var samples = ReceiverMode.ReadWavPublic(inputWav);
        if (samples.Length == 0)
        {
            return;
        }

        Log.Information("Recovery mode: {Samples} samples ({Duration:F1}s)",
            samples.Length, (double)samples.Length / Constants.SampleRate);

        // Phase 1: Frequency scan — determine what's actually in the recording
        Log.Information("=== Phase 1: Frequency scan ===");
        ScanFrequencies(samples);

        // Phase 2: Mark-only threshold detection
        // When the space tone is destroyed by codec, detect bits using only mark power
        Log.Information("=== Phase 2: Mark-only threshold detection ===");
        var result = TryMarkOnlyThreshold(samples, password);
        if (result != null)
        {
            File.WriteAllBytes(outputFile, result);
            Log.Information("Successfully recovered {Bytes} bytes -> {File}", result.Length, outputFile);
            return;
        }

        // Phase 3: Echo-cancelling demodulation
        Log.Information("=== Phase 3: Echo-cancelling recovery ===");
        result = TryEchoCancelling(samples, password);
        if (result != null)
        {
            File.WriteAllBytes(outputFile, result);
            Log.Information("Successfully recovered {Bytes} bytes -> {File}", result.Length, outputFile);
            return;
        }

        // Phase 4: Multi-preset decode attempts
        Log.Information("=== Phase 4: Multi-preset decode attempts ===");
        result = TryMultiPreset(samples, password);
        if (result != null)
        {
            File.WriteAllBytes(outputFile, result);
            Log.Information("Successfully recovered {Bytes} bytes -> {File}", result.Length, outputFile);
            return;
        }

        // Phase 5: Brute-force baud rate and frequency sweep
        Log.Information("=== Phase 5: Baud rate / frequency sweep ===");
        result = TryBaudFreqSweep(samples, password);
        if (result != null)
        {
            File.WriteAllBytes(outputFile, result);
            Log.Information("Successfully recovered {Bytes} bytes -> {File}", result.Length, outputFile);
            return;
        }

        Log.Error("All recovery strategies exhausted. The recording cannot be decoded.");
        Log.Error("The channel distortion is too severe for the FEC protection used during transmission.");
    }

    /// <summary>
    /// Scan Goertzel power at many frequencies to identify what's in the recording.
    /// </summary>
    private static void ScanFrequencies(float[] samples)
    {
        var midpoint = samples.Length / 2;
        var blockSize = 160; // 300 baud = 160 samples/bit
        var freqs = new double[] { 800, 1000, 1200, 1400, 1500, 1600, 1800, 2000, 2200, 2400, 2500, 2800, 3000, 3500 };

        Log.Information("Goertzel power at midpoint ({Time:F1}s), block={Block}:",
            (double)midpoint / Constants.SampleRate, blockSize);

        foreach (var freq in freqs)
        {
            // Average over 20 consecutive blocks for stability
            double totalPower = 0;
            for (var b = 0; b < 20; b++)
            {
                var offset = midpoint + b * blockSize;
                if (offset + blockSize > samples.Length)
                {
                    break;
                }

                totalPower += FskDemodulator.GoertzelPower(samples, offset, blockSize, freq);
            }
            var avgPower = totalPower / 20;
            var bar = new string('#', (int)Math.Min(50, avgPower * 200));
            Log.Information("  {Freq,5:F0} Hz: {Power:E3} {Bar}", freq, avgPower, bar);
        }

        // Also try with larger block sizes (lower baud rates)
        foreach (var baud in new[] { 100, 150, 200, 300 })
        {
            var bs = Constants.SampleRate / baud;
            var offset = midpoint;
            if (offset + bs > samples.Length)
            {
                continue;
            }

            var mark = FskDemodulator.GoertzelPower(samples, offset, bs, 2200);
            var space = FskDemodulator.GoertzelPower(samples, offset, bs, 1800);
            var ratio = space > 0 ? mark / space : 999;
            Log.Information("  baud={Baud}: mark(2200)={Mark:E3}, space(1800)={Space:E3}, ratio={Ratio:F1}x",
                baud, mark, space, ratio);
        }
    }

    /// <summary>
    /// Mark-only threshold detection: When the space tone is destroyed by the codec,
    /// detect bits by looking only at mark (2200 Hz) power. During true mark bits,
    /// the power is high; during space bits, the mark power drops.
    /// Calibrates the threshold from preamble bits.
    /// </summary>
    private static byte[]? TryMarkOnlyThreshold(float[] samples, string? password)
    {
        var configs = new[] {
            (baud: 300, fec: 3, preamble: 128, mark: 2200.0, space: 1800.0),
            (baud: 150, fec: 5, preamble: 256, mark: 2200.0, space: 1800.0),
        };

        var guards = new[] { 0.0, 0.10, 0.20, 0.30 };

        foreach (var (baud, fec, preamble, freqMark, freqSpace) in configs)
        {
            var profile = TransmissionProfile.FromOptions("normal", baud, preamble,
                null, 0.00005, 1.0, fec, freqMark, freqSpace, 0.0);

            // Try both preprocessed and raw audio
            var rawNorm = new float[samples.Length];
            Array.Copy(samples, rawNorm, samples.Length);
            DspFilters.RemoveDc(rawNorm);
            DspFilters.Normalize(rawNorm);

            var audioVersions = new[] {
                ("preprocessed", DspFilters.Preprocess(samples, profile)),
                ("raw", rawNorm),
            };

            foreach (var (label, audioData) in audioVersions)
            {
            var spb = profile.SamplesPerBit;

            var signalStart = FindSignalStartQuick(audioData, profile);
            if (signalStart < 0) { Log.Information("No signal ({Label})", label); continue; }

            var demod = new FskDemodulator(profile);
            demod.CalibrateFromSignal(audioData, signalStart, audioData.Length);
            var searchLen = Math.Min(preamble * spb * 2, audioData.Length - signalStart);
            var alignedStart = demod.FindBestAlignment(audioData, signalStart, searchLen);

            Log.Information("Mark-only ({Label}): {Baud} baud, signal at {Start}, aligned at {Aligned}",
                label, baud, signalStart, alignedStart);

            foreach (var guard in guards)
            {
                var guardSamples = (int)(spb * guard);
                var analysisLen = spb - 2 * guardSamples;
                if (analysisLen < 16)
                {
                    continue;
                }

                var numBits = (audioData.Length - alignedStart) / spb;
                var markPow = new double[numBits];
                var spacePow = new double[numBits];

                for (var b = 0; b < numBits; b++)
                {
                    var offset = alignedStart + b * spb + guardSamples;
                    if (offset + analysisLen > audioData.Length) { numBits = b; break; }
                    markPow[b] = FskDemodulator.GoertzelPower(audioData, offset, analysisLen, freqMark);
                    spacePow[b] = FskDemodulator.GoertzelPower(audioData, offset, analysisLen, freqSpace);
                }

                // Calibrate from preamble: measure mark power during known-1 and known-0 bits
                // Preamble is alternating 1,0,1,0... starting with 1
                double markDuring1 = 0, markDuring0 = 0;
                double spaceDuring1 = 0, spaceDuring0 = 0;
                var calStart = 20; // skip first 20 bits (alignment settling)
                var calEnd = Math.Min(preamble - 10, numBits);
                var calCount = 0;
                for (var b = calStart; b < calEnd; b++)
                {
                    if (b % 2 == 0) { markDuring1 += markPow[b]; spaceDuring1 += spacePow[b]; }
                    else { markDuring0 += markPow[b]; spaceDuring0 += spacePow[b]; }
                    calCount++;
                }
                if (calCount < 20)
                {
                    continue;
                }

                var half = calCount / 2;
                markDuring1 /= half;
                markDuring0 /= half;
                spaceDuring1 /= half;
                spaceDuring0 /= half;

                Log.Information("  guard={Guard:F2}: markDuring1={M1:F3}, markDuring0={M0:F3}, spaceDuring1={S1:F3}, spaceDuring0={S0:F3}",
                    guard, markDuring1, markDuring0, spaceDuring1, spaceDuring0);

                // Strategy A: Use mark power alone with various thresholds
                if (markDuring1 > markDuring0 * 1.1)  // at least 10% difference
                {
                    // Try thresholds from 0.2 to 0.8 of the range
                    for (var tFrac = 0.1; tFrac <= 0.9; tFrac += 0.05)
                    {
                        var threshold = markDuring0 + (markDuring1 - markDuring0) * tFrac;
                        var bits = new bool[numBits];
                        var softBits = new double[numBits];
                        for (var b = 0; b < numBits; b++)
                        {
                            bits[b] = markPow[b] > threshold;
                            var range = markDuring1 - markDuring0;
                            softBits[b] = range > 0 ? (markPow[b] - threshold) / range * 5.0 : 0;
                        }

                        // Check preamble BER with this threshold
                        var preambleErrors = 0;
                        for (var b = calStart; b < calEnd; b++)
                        {
                            var expected = b % 2 == 0; // 1 for even, 0 for odd
                            if (bits[b] != expected)
                            {
                                preambleErrors++;
                            }
                        }
                        var preamBer = (double)preambleErrors / (calEnd - calStart);

                        if (tFrac < 0.55 && tFrac > 0.45) // log only around midpoint
                        {
                            Log.Information("    threshold={T:F3} (frac={F:F2}): preamble BER = {BER:P1}",
                                threshold, tFrac, preamBer);
                        }

                        if (preamBer > 0.20)
                        {
                            continue; // skip hopeless thresholds
                        }

                        var result = TryDecodeFromBits(bits, softBits, numBits, preamble, fec, password);
                        if (result != null)
                        {
                            Log.Information("Mark-only threshold decoded! guard={Guard:F2}, tFrac={F:F2}, BER={BER:P1}",
                                guard, tFrac, preamBer);
                            return result;
                        }
                    }
                }

                // Strategy B: Use mark-space difference (standard approach but with various gain ratios)
                for (var gainMul = 0.1; gainMul <= 5.0; gainMul += 0.1)
                {
                    var bits = new bool[numBits];
                    var softBits = new double[numBits];
                    for (var b = 0; b < numBits; b++)
                    {
                        var m = markPow[b];
                        var s = spacePow[b] * gainMul;
                        bits[b] = m > s;
                        softBits[b] = (m + s > 0) ? (m - s) / (m + s) * 5.0 : 0;
                    }

                    var result = TryDecodeFromBits(bits, softBits, numBits, preamble, fec, password);
                    if (result != null)
                    {
                        Log.Information("Gain-sweep decoded! guard={Guard:F2}, gain={Gain:F1}",
                            guard, gainMul);
                        return result;
                    }
                }

                // Strategy C: Per-bit adaptive gain using sliding window
                for (var winSize = 10; winSize <= 60; winSize += 10)
                {
                    var bits = new bool[numBits];
                    var softBits = new double[numBits];
                    for (var b = 0; b < numBits; b++)
                    {
                        // Compute local average mark and space over window
                        double localMark = 0, localSpace = 0;
                        var wStart = Math.Max(0, b - winSize);
                        var wEnd = Math.Min(numBits, b + winSize);
                        for (var w = wStart; w < wEnd; w++) { localMark += markPow[w]; localSpace += spacePow[w]; }
                        localMark /= (wEnd - wStart);
                        localSpace /= (wEnd - wStart);

                        // Normalize each power by its local average
                        var normMark = localMark > 0 ? markPow[b] / localMark : 0;
                        var normSpace = localSpace > 0 ? spacePow[b] / localSpace : 0;

                        bits[b] = normMark > normSpace;
                        var sum = normMark + normSpace;
                        softBits[b] = sum > 0 ? (normMark - normSpace) / sum * 5.0 : 0;
                    }

                    var result = TryDecodeFromBits(bits, softBits, numBits, preamble, fec, password);
                    if (result != null)
                    {
                        Log.Information("Adaptive-gain decoded! guard={Guard:F2}, window={Win}",
                            guard, winSize);
                        return result;
                    }
                }
            }
            } // end audioVersions loop

            Log.Information("Mark-only threshold failed for {Baud} baud", baud);
        }

        return null;
    }

    /// <summary>
    /// Try decoding from pre-computed bit arrays at expected positions and via sync byte search.
    /// </summary>
    private static byte[]? TryDecodeFromBits(bool[] bits, double[] softBits, int numBits,
        int preamble, int fec, string? password)
    {
        // Try at expected data position and nearby
        for (var posOff = -10; posOff <= 10; posOff++)
        {
            var dataPos = preamble + 8 + posOff;
            if (dataPos < 0 || dataPos >= numBits)
            {
                continue;
            }

            var result = FrameCodec.DecodeFromPosition(bits, dataPos, fec, password);
            if (result != null)
            {
                return result;
            }

            result = FrameCodec.DecodeFromPositionSoft(softBits, dataPos, fec, password);
            if (result != null)
            {
                return result;
            }
        }

        // Sync byte search
        var hardResult = FrameCodec.Decode(bits, fec, password);
        if (hardResult != null)
        {
            return hardResult;
        }

        var softResult = FrameCodec.DecodeSoft(softBits, fec, password);
        if (softResult != null)
        {
            return softResult;
        }

        return null;
    }

    /// <summary>
    /// Try decoding with multiple presets and guard intervals (skipping full-scan).
    /// </summary>
    private static byte[]? TryMultiPreset(float[] samples, string? password)
    {
        var presets = new[] { "normal", "slow", "fast" };
        var guards = new[] { 0.0, 0.10, 0.15, 0.20, 0.25, 0.30, 0.35, 0.40 };
        var ratios = new[] { 1.0, 1.1, 1.2, 1.5 };

        foreach (var preset in presets)
        {
            foreach (var guard in guards)
            {
                foreach (var ratio in ratios)
                {
                    var profile = TransmissionProfile.FromOptions(preset, null, null,
                        null, null, ratio, null, null, null, guard);

                    var preprocessed = DspFilters.Preprocess(samples, profile);
                    var result = TryDecodeNoFullScan(preprocessed, profile, password);
                    if (result != null)
                    {
                        Log.Information("Decoded with preset={Preset}, guard={Guard:F2}, ratio={Ratio:F1}",
                            preset, guard, ratio);
                        return result;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Echo-cancelling demodulation: subtract estimated echo from previous bit
    /// before making bit decisions. This compensates for room reverb and codec
    /// artifacts that cause frequency power to "leak" into adjacent bit periods.
    /// </summary>
    private static byte[]? TryEchoCancelling(float[] samples, string? password)
    {
        // Try normal preset frequencies (most likely what was used)
        var freqPairs = new[] {
            (mark: 2200.0, space: 1800.0, baud: 300, fec: 3, preamble: 128),
            (mark: 2400.0, space: 1600.0, baud: 150, fec: 5, preamble: 256),
        };

        var guards = new[] { 0.0, 0.15, 0.30 };
        var alphaRange = new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 };

        foreach (var (freqMark, freqSpace, baud, fec, preamble) in freqPairs)
        {
            var profile = TransmissionProfile.FromOptions("normal", baud, preamble,
                null, 0.00005, 1.0, fec, freqMark, freqSpace, 0.0);

            var preprocessed = DspFilters.Preprocess(samples, profile);
            var spb = profile.SamplesPerBit;

            // Find signal start
            var signalStart = FindSignalStartQuick(preprocessed, profile);
            if (signalStart < 0)
            {
                Log.Information("No signal at {Mark}/{Space} Hz, {Baud} baud", freqMark, freqSpace, baud);
                continue;
            }

            Log.Information("Signal found at {Mark}/{Space} Hz, {Baud} baud, sample {Start}",
                freqMark, freqSpace, baud, signalStart);

            // Align using preamble
            var demod = new FskDemodulator(profile);
            demod.CalibrateFromSignal(preprocessed, signalStart, preprocessed.Length);
            var searchLen = Math.Min(preamble * spb * 2, preprocessed.Length - signalStart);
            var alignedStart = demod.FindBestAlignment(preprocessed, signalStart, searchLen);

            foreach (var guard in guards)
            {
                var guardSamples = (int)(spb * guard);
                var analysisLen = spb - 2 * guardSamples;
                if (analysisLen < 16)
                {
                    continue;
                }

                // Compute raw Goertzel powers for all bit positions
                var numBits = (preprocessed.Length - alignedStart) / spb;
                var markPow = new double[numBits];
                var spacePow = new double[numBits];

                for (var b = 0; b < numBits; b++)
                {
                    var offset = alignedStart + b * spb + guardSamples;
                    if (offset + analysisLen > preprocessed.Length) { numBits = b; break; }
                    markPow[b] = FskDemodulator.GoertzelPower(preprocessed, offset, analysisLen, freqMark);
                    spacePow[b] = FskDemodulator.GoertzelPower(preprocessed, offset, analysisLen, freqSpace);
                }

                // Strategy A: Single-tap echo cancellation
                foreach (var alpha in alphaRange)
                {
                    var adjMark = new double[numBits];
                    var adjSpace = new double[numBits];
                    adjMark[0] = markPow[0];
                    adjSpace[0] = spacePow[0];

                    for (var b = 1; b < numBits; b++)
                    {
                        adjMark[b] = Math.Max(0, markPow[b] - alpha * markPow[b - 1]);
                        adjSpace[b] = Math.Max(0, spacePow[b] - alpha * spacePow[b - 1]);
                    }

                    var recovered = TryDecodeFromPowers(adjMark, adjSpace, numBits, preamble, fec, password);
                    if (recovered != null)
                    {
                        Log.Information("Echo-cancel 1-tap decoded: alpha={Alpha:F1}, guard={Guard:F2}",
                            alpha, guard);
                        return recovered;
                    }
                }

                // Strategy B: Two-tap echo cancellation (ISI from b-1 and b-2)
                foreach (var alpha in alphaRange)
                {
                    var beta = alpha * 0.5; // second echo is typically weaker
                    var adjMark = new double[numBits];
                    var adjSpace = new double[numBits];
                    adjMark[0] = markPow[0];
                    adjSpace[0] = spacePow[0];
                    if (numBits > 1) { adjMark[1] = Math.Max(0, markPow[1] - alpha * markPow[0]); adjSpace[1] = Math.Max(0, spacePow[1] - alpha * spacePow[0]); }

                    for (var b = 2; b < numBits; b++)
                    {
                        adjMark[b] = Math.Max(0, markPow[b] - alpha * markPow[b - 1] - beta * markPow[b - 2]);
                        adjSpace[b] = Math.Max(0, spacePow[b] - alpha * spacePow[b - 1] - beta * spacePow[b - 2]);
                    }

                    var recovered = TryDecodeFromPowers(adjMark, adjSpace, numBits, preamble, fec, password);
                    if (recovered != null)
                    {
                        Log.Information("Echo-cancel 2-tap decoded: alpha={Alpha:F1}, beta={Beta:F2}, guard={Guard:F2}",
                            alpha, beta, guard);
                        return recovered;
                    }
                }

                // Strategy C: Iterative decision-feedback equalization (DFE)
                // Use decided bits to estimate and subtract ISI contribution
                for (var dfeAlpha = 0.3; dfeAlpha <= 0.8; dfeAlpha += 0.1)
                {
                    var adjMark = new double[numBits];
                    var adjSpace = new double[numBits];
                    var decided = new bool[numBits];

                    // First pass: raw decisions
                    for (var b = 0; b < numBits; b++)
                        decided[b] = markPow[b] > spacePow[b];

                    // DFE pass: subtract estimated ISI based on previous decision
                    adjMark[0] = markPow[0];
                    adjSpace[0] = spacePow[0];
                    for (var b = 1; b < numBits; b++)
                    {
                        if (decided[b - 1]) // previous bit was mark
                        {
                            adjMark[b] = Math.Max(0, markPow[b] - dfeAlpha * markPow[b - 1]);
                            adjSpace[b] = spacePow[b]; // no space ISI from a mark bit
                        }
                        else // previous bit was space
                        {
                            adjMark[b] = markPow[b]; // no mark ISI from a space bit
                            adjSpace[b] = Math.Max(0, spacePow[b] - dfeAlpha * spacePow[b - 1]);
                        }
                        decided[b] = adjMark[b] > adjSpace[b]; // update decision
                    }

                    var recovered = TryDecodeFromPowers(adjMark, adjSpace, numBits, preamble, fec, password);
                    if (recovered != null)
                    {
                        Log.Information("DFE decoded: alpha={Alpha:F1}, guard={Guard:F2}",
                            dfeAlpha, guard);
                        return recovered;
                    }
                }
            }

            Log.Information("Echo cancellation failed for {Mark}/{Space} Hz, {Baud} baud", freqMark, freqSpace, baud);
        }

        return null;
    }

    /// <summary>
    /// Try decoding from echo-cancelled power arrays with multiple gain calibrations and position offsets.
    /// </summary>
    private static byte[]? TryDecodeFromPowers(double[] adjMark, double[] adjSpace, int numBits,
        int preamble, int fec, string? password)
    {
        // Calibrate gain from preamble (bits 20-100)
        double preamMark = 0, preamSpace = 0;
        var calCount = 0;
        for (var b = 20; b < Math.Min(preamble - 10, numBits); b++)
        {
            preamMark += adjMark[b];
            preamSpace += adjSpace[b];
            calCount++;
        }
        if (calCount < 20 || preamMark < 1e-10 || preamSpace < 1e-10)
        {
            return null;
        }

        preamMark /= calCount;
        preamSpace /= calCount;
        var gainRatio = preamMark / preamSpace;

        var gainTrials = new[] { gainRatio * 0.3, gainRatio * 0.5, gainRatio * 0.7, gainRatio, gainRatio * 1.5, gainRatio * 2.0, gainRatio * 3.0 };

        foreach (var g in gainTrials)
        {
            // Construct soft bits (LLR)
            var softBits = new double[numBits];
            var bits = new bool[numBits];
            for (var b = 0; b < numBits; b++)
            {
                var m = adjMark[b];
                var s = adjSpace[b] * g;
                bits[b] = m > s;
                softBits[b] = (m + s > 0) ? (m - s) / (m + s) * 5.0 : 0;
            }

            // Try decode at expected data position and nearby
            for (var posOff = -10; posOff <= 10; posOff++)
            {
                var dataPos = preamble + 8 + posOff;
                if (dataPos < 0 || dataPos >= numBits)
                {
                    continue;
                }

                var result = FrameCodec.DecodeFromPosition(bits, dataPos, fec, password);
                if (result != null)
                {
                    return result;
                }

                result = FrameCodec.DecodeFromPositionSoft(softBits, dataPos, fec, password);
                if (result != null)
                {
                    return result;
                }
            }

            // Also try finding sync byte in the hard/soft bits
            var hardResult = FrameCodec.Decode(bits, fec, password);
            if (hardResult != null)
            {
                return hardResult;
            }

            var softResult = FrameCodec.DecodeSoft(softBits, fec, password);
            if (softResult != null)
            {
                return softResult;
            }
        }

        return null;
    }

    /// <summary>
    /// Brute-force: try different baud rates and frequencies (in case the recording
    /// used non-default settings we don't know about).
    /// </summary>
    private static byte[]? TryBaudFreqSweep(float[] samples, string? password)
    {
        var bauds = new[] { 100, 150, 200, 250, 300, 350 };
        var freqPairs = new[] {
            (2200.0, 1800.0),
            (2400.0, 1600.0),
            (2500.0, 1500.0),
            (2500.0, 2000.0),
        };
        var fecs = new[] { 1, 3, 5 };

        foreach (var baud in bauds)
        {
            foreach (var (mark, space) in freqPairs)
            {
                foreach (var fec in fecs)
                {
                    var preamble = baud <= 150 ? 256 : 128;
                    var profile = TransmissionProfile.FromOptions("normal", baud, preamble,
                        null, 0.00005, 1.0, fec, mark, space, 0.0);

                    var preprocessed = DspFilters.Preprocess(samples, profile);
                    var result = TryDecodeNoFullScan(preprocessed, profile, password);
                    if (result != null)
                    {
                        Log.Information("Sweep decoded: baud={Baud}, mark={Mark}, space={Space}, fec={Fec}",
                            baud, mark, space, fec);
                        return result;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Try decoding WITHOUT the full-scan fallback (which is too slow for brute-force sweeps).
    /// Only tries: preamble-based alignment + nudges, expected position, brute-force window.
    /// </summary>
    private static byte[]? TryDecodeNoFullScan(float[] samples, TransmissionProfile profile, string? password)
    {
        var demod = new FskDemodulator(profile);
        var signalStart = FindSignalStartQuick(samples, profile);
        if (signalStart < 0)
        {
            return null;
        }

        demod.CalibrateFromSignal(samples, signalStart, samples.Length);

        var searchLen = Math.Min(profile.PreambleBits * profile.SamplesPerBit * 2, samples.Length - signalStart);
        var alignedStart = demod.FindBestAlignment(samples, signalStart, searchLen);

        // Try nudges around aligned start
        for (var nudge = -profile.SamplesPerBit / 2; nudge <= profile.SamplesPerBit / 2; nudge += profile.SamplesPerBit / 8)
        {
            var tryStart = alignedStart + nudge;
            if (tryStart < 0)
            {
                continue;
            }

            var result = TryDecode(samples, tryStart, demod, profile, password);
            if (result != null)
            {
                return result;
            }
        }

        // Try expected data position
        for (var posOff = -30; posOff <= 30; posOff++)
        {
            var expectedDataBit = profile.PreambleBits + 8 + posOff;
            for (var nudge = -profile.SamplesPerBit / 2; nudge <= profile.SamplesPerBit / 2; nudge += profile.SamplesPerBit / 4)
            {
                var sampleOffset = alignedStart + nudge;
                if (sampleOffset < 0)
                {
                    continue;
                }

                var (bits, _) = demod.DemodulateAll(samples, sampleOffset);
                if (expectedDataBit >= bits.Length)
                {
                    continue;
                }

                var result = FrameCodec.DecodeFromPosition(bits, expectedDataBit, profile.FecRepeat, password);
                if (result != null)
                {
                    return result;
                }

                var softBits = demod.DemodulateAllSoft(samples, sampleOffset);
                if (expectedDataBit >= softBits.Length)
                {
                    continue;
                }

                result = FrameCodec.DecodeFromPositionSoft(softBits, expectedDataBit, profile.FecRepeat, password);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static byte[]? TryDecode(float[] samples, int startOffset, FskDemodulator demod, TransmissionProfile profile, string? password)
    {
        var (bits, valid) = demod.DemodulateAll(samples, startOffset);
        if (bits.Length < 16)
        {
            return null;
        }

        var result = FrameCodec.Decode(bits, profile.FecRepeat, password);
        if (result != null)
        {
            return result;
        }

        var softBits = demod.DemodulateAllSoft(samples, startOffset);
        return FrameCodec.DecodeSoft(softBits, profile.FecRepeat, password);
    }

    /// <summary>
    /// Quick signal detection without fancy thresholding.
    /// </summary>
    private static int FindSignalStartQuick(float[] samples, TransmissionProfile profile)
    {
        var blockSize = profile.SamplesPerBit;
        var numBlocks = samples.Length / blockSize;
        if (numBlocks < 8)
        {
            return numBlocks > 0 ? 0 : -1;
        }

        var threshold = profile.SignalThreshold;
        var consecutive = 0;
        for (var i = 0; i < numBlocks; i++)
        {
            var offset = i * blockSize;
            if (offset + blockSize > samples.Length)
            {
                break;
            }

            var mp = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqMark);
            var sp = FskDemodulator.GoertzelPower(samples, offset, blockSize, profile.FreqSpace);
            if (mp + sp >= threshold)
            {
                consecutive++;
                if (consecutive >= 4)
                {
                    return (i - 3) * blockSize;
                }
            }
            else
            {
                consecutive = 0;
            }
        }
        return -1;
    }
}
